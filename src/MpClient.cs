using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace ExScoringMod
{
    /// <summary>
    /// The online-play socket (ApiContract.md Section 19.1): transport and connection lifecycle
    /// only. What the messages mean lives in MpSession, which is the only caller of this class.
    ///
    /// Threading: the socket runs on background tasks (Il2Cpp UnityWebRequest has no WebSocket
    /// support, so this uses ClientWebSocket on MelonLoader's managed runtime, confirmed working
    /// by MpConnectionTest on 2026-10-20). Background code only touches managed objects and the
    /// concurrent queues below. Everything else, including every call into MpSession, happens on
    /// Unity's main thread inside Tick(), so the game side never needs locks.
    ///
    /// Each socket is wrapped in a Connection with its own id. Events from a socket that is no
    /// longer the current one (closed by the player, replaced by a reconnect) are dropped in Tick.
    /// </summary>
    internal static class MpClient
    {
        private const string WsUrl = "wss://audicaexscoringweb-production.up.railway.app/api/mp/ws";

        private const int ConnectTimeoutMs = 15000;
        private const long HandshakeTimeoutMs = 10000;
        private const int CloseTimeoutMs = 2000;
        private const int MaxInboundMessageBytes = 1024 * 1024;

        // Unexpected drops are retried with these delays (the last one repeats) until the outage
        // has lasted ReconnectGiveUpMs. The server holds the seat for 30 s after a drop (19.6), so
        // the first few attempts are inside the grace and get the seat back with the resumeToken.
        private static readonly int[] ReconnectBackoffMs = { 1000, 2000, 4000, 8000, 15000 };
        private const long ReconnectGiveUpMs = 120000;

        private sealed class Connection
        {
            public int Id;
            public ClientWebSocket Socket;
            public readonly ConcurrentQueue<string> Outgoing = new ConcurrentQueue<string>();
            public readonly SemaphoreSlim OutgoingSignal = new SemaphoreSlim(0);
            public readonly CancellationTokenSource Cancel = new CancellationTokenSource();
            public volatile bool Open;
        }

        private enum InboundKind { Opened, Message, Closed }

        private struct Inbound
        {
            public int ConnectionId;
            public InboundKind Kind;
            public string Text;
            public int? CloseCode;
            public string CloseDescription;
            public Exception Error;
        }

        private static readonly ConcurrentQueue<Inbound> inbound = new ConcurrentQueue<Inbound>();

        private static Connection current;
        private static int nextConnectionId = 0;

        // True once a welcome has arrived since the last Open(). Only then is a drop retried: a
        // first connect that fails is reported, not retried in a loop.
        private static bool everWelcomed;
        private static long outageStartedMs;
        private static int reconnectAttempt;
        private static long nextReconnectAtMs;
        private static long handshakeDeadlineMs;

        public static MpConnectionState State { get; private set; } = MpConnectionState.Disconnected;

        public static long NowMs()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        /// <summary>Opens a new connection. Main thread. Does nothing unless fully disconnected.</summary>
        public static void Open()
        {
            if (State != MpConnectionState.Disconnected) return;

            everWelcomed = false;
            outageStartedMs = 0;
            reconnectAttempt = 0;
            BeginConnect();
        }

        /// <summary>
        /// Closes the connection on purpose (the player, the toggle, or quitting). Main thread.
        /// No reconnect follows. The socket's own Closed event is dropped, since it's no longer current.
        /// </summary>
        public static void Close(string reason)
        {
            Connection conn = current;
            current = null;
            State = MpConnectionState.Disconnected;

            if (conn != null)
            {
                Log($"Closing connection #{conn.Id} ({reason})");
                Task.Run(() => CloseQuietly(conn, reason));
            }
        }

        /// <summary>
        /// Queues one JSON text message on the current socket. Returns false when there is no open
        /// socket. Main thread (but safe from anywhere).
        /// </summary>
        public static bool Send(string json)
        {
            Connection conn = current;
            if (conn == null || !conn.Open) return false;

            conn.Outgoing.Enqueue(json);
            conn.OutgoingSignal.Release();
            return true;
        }

        /// <summary>Called by MpSession once a welcome arrives: the connection is fully up.</summary>
        public static void MarkWelcomed()
        {
            State = MpConnectionState.Connected;
            everWelcomed = true;
            outageStartedMs = 0;
            reconnectAttempt = 0;
        }

        /// <summary>
        /// Best-effort close for OnApplicationQuit, waiting briefly so the server sees a clean close
        /// instead of a drop (which would hold the seat for 30 s).
        /// </summary>
        public static void Shutdown()
        {
            Connection conn = current;
            current = null;
            State = MpConnectionState.Disconnected;
            if (conn == null) return;

            try
            {
                CloseQuietly(conn, "game closing").Wait(500);
            }
            catch
            {
                // Quitting anyway.
            }
        }

        /// <summary>Main thread, once per frame (via MpSession.Tick).</summary>
        public static void Tick()
        {
            while (inbound.TryDequeue(out Inbound item))
            {
                if (current == null || item.ConnectionId != current.Id) continue;

                switch (item.Kind)
                {
                    case InboundKind.Opened:
                        State = MpConnectionState.Handshaking;
                        handshakeDeadlineMs = NowMs() + HandshakeTimeoutMs;
                        Log($"Connection #{item.ConnectionId} open, sending hello");
                        MpSession.HandleSocketOpened();
                        break;

                    case InboundKind.Message:
                        HandleMessageText(item.Text);
                        break;

                    case InboundKind.Closed:
                        HandleClosed(item);
                        break;
                }

                // A handler may have closed the connection (e.g. the player disconnected from an
                // event callback). Whatever is left in the queue belongs to that old socket.
                if (current == null) break;
            }

            long now = NowMs();

            if (State == MpConnectionState.Handshaking && now > handshakeDeadlineMs)
            {
                Log($"No welcome within {HandshakeTimeoutMs} ms, dropping the socket");
                handshakeDeadlineMs = long.MaxValue;
                AbortCurrent();
            }

            if (State == MpConnectionState.Reconnecting && now >= nextReconnectAtMs)
            {
                Log($"Reconnect attempt {reconnectAttempt}");
                BeginConnect();
            }
        }

        // ── Main-thread internals ──

        private static void BeginConnect()
        {
            string apiKey = Config.ApiKey;
            Connection conn = new Connection { Id = ++nextConnectionId };
            current = conn;
            State = MpConnectionState.Connecting;

            Log($"Connecting #{conn.Id} to {WsUrl} as {(string.IsNullOrEmpty(apiKey) ? "guest (no API key)" : "registered (API key set)")}");
            Task.Run(() => RunConnection(conn, apiKey));
        }

        private static void HandleMessageText(string text)
        {
            JObject message;
            try
            {
                message = JObject.Parse(text);
            }
            catch (Exception ex)
            {
                Log($"Unparsable message ({ex.Message}): {Preview(text)}");
                return;
            }

            try
            {
                MpSession.HandleSocketMessage(message);
            }
            catch (Exception ex)
            {
                Log($"Error handling \"{message.Value<string>("type")}\": {ex}");
            }
        }

        private static void HandleClosed(Inbound item)
        {
            current = null;

            int? code = item.CloseCode;
            string reason;
            bool retry = everWelcomed;
            bool forgetSeat = false;

            switch (code)
            {
                case 4000:
                    reason = "The server doesn't support this version of the mod.";
                    retry = false;
                    forgetSeat = true;
                    break;
                case 4002:
                    reason = "Online play was opened somewhere else with the same account.";
                    retry = false;
                    forgetSeat = true;
                    break;
                case 4009:
                    reason = "Too many guest connections from this network.";
                    retry = false;
                    forgetSeat = true;
                    break;
                case 4010:
                    reason = "The server restarted and every lobby was closed.";
                    forgetSeat = true;
                    break;
                case 4008:
                    reason = "The connection timed out.";
                    break;
                case 1009:
                    reason = "The server refused a message that was too large.";
                    break;
                default:
                    if (code.HasValue)
                    {
                        reason = $"The connection closed ({code}{(string.IsNullOrEmpty(item.CloseDescription) ? "" : " " + item.CloseDescription)}).";
                    }
                    else
                    {
                        reason = "Couldn't reach the online play server.";
                    }
                    break;
            }

            string errorText = item.Error != null ? $" | {item.Error.GetType().Name}: {item.Error.Message}" : "";
            Log($"Connection #{item.ConnectionId} closed: code={(code.HasValue ? code.Value.ToString() : "none")} " +
                $"desc=\"{item.CloseDescription}\"{errorText}");
            if (item.Error != null && !(item.Error is OperationCanceledException))
            {
                Log($"Full exception: {item.Error}");
            }

            long now = NowMs();
            if (retry)
            {
                if (outageStartedMs == 0) outageStartedMs = now;
                if (now - outageStartedMs > ReconnectGiveUpMs)
                {
                    retry = false;
                    reason += " Gave up reconnecting.";
                }
            }

            if (retry)
            {
                int delay = ReconnectBackoffMs[Math.Min(reconnectAttempt, ReconnectBackoffMs.Length - 1)];
                reconnectAttempt++;
                nextReconnectAtMs = now + delay;
                State = MpConnectionState.Reconnecting;
                Log($"Reconnecting in {delay} ms (attempt {reconnectAttempt}, outage {now - outageStartedMs} ms so far)");
            }
            else
            {
                State = MpConnectionState.Disconnected;
            }

            MpSession.HandleSocketClosed(new MpDisconnectInfo
            {
                closeCode = code,
                reason = reason,
                willReconnect = retry
            }, forgetSeat);
        }

        private static void AbortCurrent()
        {
            Connection conn = current;
            if (conn == null) return;

            try
            {
                conn.Socket?.Abort();
            }
            catch (Exception ex)
            {
                Log($"Abort failed: {ex.Message}");
            }
            try
            {
                conn.Cancel.Cancel();
            }
            catch
            {
                // Already disposed.
            }
        }

        // ── Background tasks ──

        private static async Task RunConnection(Connection conn, string apiKey)
        {
            int? closeCode = null;
            string closeDescription = null;
            Exception error = null;
            ClientWebSocket socket = null;

            try
            {
                try
                {
                    ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                }
                catch
                {
                    // Already available (the connection test showed Tls12 on by default).
                }

                socket = new ClientWebSocket();
                conn.Socket = socket;
                if (!string.IsNullOrEmpty(apiKey))
                {
                    socket.Options.SetRequestHeader("Authorization", "ApiKey " + apiKey);
                }

                using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(conn.Cancel.Token))
                {
                    timeout.CancelAfter(ConnectTimeoutMs);
                    await socket.ConnectAsync(new Uri(WsUrl), timeout.Token);
                }

                conn.Open = true;
                inbound.Enqueue(new Inbound { ConnectionId = conn.Id, Kind = InboundKind.Opened });

                // Runs until the connection is cancelled; nothing awaits it.
                _ = SendLoop(conn);
                await ReceiveLoop(conn);

                if (socket.CloseStatus.HasValue)
                {
                    closeCode = (int)socket.CloseStatus.Value;
                    closeDescription = socket.CloseStatusDescription;
                }

                // The server started the close: finish the handshake politely.
                if (socket.State == WebSocketState.CloseReceived)
                {
                    using (CancellationTokenSource timeout = new CancellationTokenSource(CloseTimeoutMs))
                    {
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", timeout.Token);
                    }
                }
            }
            catch (Exception ex)
            {
                error = ex;
                if (socket != null && socket.CloseStatus.HasValue)
                {
                    closeCode = (int)socket.CloseStatus.Value;
                    closeDescription = socket.CloseStatusDescription;
                }
            }
            finally
            {
                conn.Open = false;
                try { conn.Cancel.Cancel(); } catch { }

                inbound.Enqueue(new Inbound
                {
                    ConnectionId = conn.Id,
                    Kind = InboundKind.Closed,
                    CloseCode = closeCode,
                    CloseDescription = closeDescription,
                    Error = error
                });

                try { socket?.Dispose(); } catch { }
            }
        }

        private static async Task ReceiveLoop(Connection conn)
        {
            byte[] buffer = new byte[16 * 1024];
            using (MemoryStream message = new MemoryStream())
            {
                while (true)
                {
                    WebSocketReceiveResult result = await conn.Socket.ReceiveAsync(new ArraySegment<byte>(buffer), conn.Cancel.Token);
                    if (result.MessageType == WebSocketMessageType.Close) return;

                    message.Write(buffer, 0, result.Count);
                    if (message.Length > MaxInboundMessageBytes)
                    {
                        throw new InvalidDataException($"Server message larger than {MaxInboundMessageBytes} bytes");
                    }

                    if (!result.EndOfMessage) continue;

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        inbound.Enqueue(new Inbound
                        {
                            ConnectionId = conn.Id,
                            Kind = InboundKind.Message,
                            Text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length)
                        });
                    }
                    message.SetLength(0);
                }
            }
        }

        /// <summary>
        /// The only code that calls SendAsync, since a ClientWebSocket allows one send in flight
        /// at a time. A send failure aborts the socket, which ends the receive loop and reports
        /// the drop through the normal Closed path.
        /// </summary>
        private static async Task SendLoop(Connection conn)
        {
            try
            {
                while (!conn.Cancel.IsCancellationRequested)
                {
                    await conn.OutgoingSignal.WaitAsync(conn.Cancel.Token);

                    while (conn.Outgoing.TryDequeue(out string json))
                    {
                        byte[] bytes = Encoding.UTF8.GetBytes(json);
                        await conn.Socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, conn.Cancel.Token);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Connection ending.
            }
            catch (Exception ex)
            {
                Log($"Send failed on #{conn.Id}: {ex.GetType().Name}: {ex.Message}");
                try { conn.Socket.Abort(); } catch { }
                try { conn.Cancel.Cancel(); } catch { }
            }
        }

        private static async Task CloseQuietly(Connection conn, string reason)
        {
            try
            {
                if (conn.Socket != null && conn.Socket.State == WebSocketState.Open)
                {
                    using (CancellationTokenSource timeout = new CancellationTokenSource(CloseTimeoutMs))
                    {
                        await conn.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, reason ?? "", timeout.Token);
                    }
                }
            }
            catch
            {
                // Best effort; the abort below covers it.
            }
            finally
            {
                try { conn.Cancel.Cancel(); } catch { }
            }
        }

        private static string Preview(string text)
        {
            if (text == null) return "";
            return text.Length > 300 ? text.Substring(0, 300) + "…" : text;
        }

        private static void Log(string message)
        {
            DebugLog.Log("[Mp] " + message);
        }
    }
}