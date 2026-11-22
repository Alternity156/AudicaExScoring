using System;
using System.Collections.Generic;
using Harmony;
using MelonLoader;
using UnityEngine;

namespace ExScoringMod
{
    /// <summary>
    /// Song-list warning icons for unconventional charts, shown at the right edge of each song row:
    ///
    ///  • Mine icon — white if the chart has any mines (Dodge), red if at least one mine comes close
    ///    enough to a still, centred head to hit or nearly hit the player (see MineCanHit).
    ///  • 360 icon — shown if any non-melee target goes past the default grid to the left or right
    ///    (or over the top and behind). Tinted green → orange → red by how far it goes: green just
    ///    past the grid edge, red directly behind the player.
    ///
    /// Everything is computed from chart data only (pitch + gridOffset + zOffset), using the native
    /// dome placement (claude/Spectator-Window-Target-Findings.md), so it works in song select.
    /// Results are cached per song + difficulty. DifficultyCalculator feeds the cache while it
    /// pre-caches ratings at load (reusing the cues it already parsed); anything missing (community
    /// maps, songs added later) is computed on first display.
    /// </summary>
    internal static class MapFlags
    {
        // ── Tuning: icon placement on the song row (SongSelectItem local space; tune in UnityExplorer
        //    under <row>/MapFlagIcons) ──
        private static readonly Vector3 IconsLocalPosition = new Vector3(1.6f, 0f, -0.06f);
        private const float IconSize = 0.32f;      // edge length of each square icon's mesh (before IconScale)
        // Each icon keeps its own fixed slot, side by side, whether or not the other one shows.
        private static readonly Vector3 MineIconLocalPosition = new Vector3(9f, 0f, 0f);
        private static readonly Vector3 OffGridIconLocalPosition = new Vector3(11f, 0f, 0f);
        private static readonly Vector3 IconScale = new Vector3(6f, 6f, 6f);

        // ── Tuning: colours ──
        private static readonly Color MineColor = Color.white;
        private static readonly Color MineHitColor = new Color(0.92f, 0.18f, 0.18f);
        private static readonly Color OffGridGreen = new Color(0.24f, 0.82f, 0.35f);
        private static readonly Color OffGridOrange = new Color(1f, 0.59f, 0.12f);
        private static readonly Color OffGridRed = new Color(0.92f, 0.18f, 0.18f);

        // ── Dome geometry (native TargetSpawner placement, relative to the 'spawners' root at floor level) ──
        private static readonly Vector3 DomeOrigin = new Vector3(0f, -1.5f, 6f);
        private const float DomeRadius = 15f;
        private static readonly Vector3 HeadPosition = new Vector3(0f, 1.75f, 0f); // fixed reference height, same for every player
        private const float GridMinCol = -0.5f, GridMaxCol = 11.5f;
        private const float GridMinRow = -0.5f, GridMaxRow = 6.5f;
        private const float OffGridToleranceDeg = 0.5f; // ignore float noise right at the grid edge
        private const float RadToDeg = (float)(180.0 / Math.PI); // Mathf.Rad2Deg is a const, stripped from the unhollowed Mathf

        // ── Mines: melee lanes (pitch 98-101) in MeleeHeightAdjust space (claude/Mine-Hit-Findings.md) ──
        // The game (Target.Update) hits the player when, at the mine's tick, the head (camera) is within
        // 0.45 m of the mine's holder. The icon goes red when a holder comes within that distance plus a
        // generous margin of a still, centred head, so mines that only miss by a little are flagged too.
        private const float MineLaneX = 0.35f;
        private const float MineLowY = 0.3712f, MineHighY = 0.8712f;
        private const float MineLaneZ = 0.75f;
        private const float MineLeftYaw = 141.38f, MineRightYaw = 218.62f;
        private const float MeleeSpawnerScale = 0.5f;
        private static readonly Vector3 HeadInMeleeSpace = new Vector3(0f, 0.9069f, -0.0361f); // measured, player standing centred
        private const float MineHitRadius = 0.45f;   // game's own check (distance² > 0.2025 = dodged)
        private const float MineDangerMargin = 0.45f; // extra distance still flagged; 0.9 m total catches mines at the default high lane spots (0.86 m), which players do have to avoid
        private const float MineDangerRadius = MineHitRadius + MineDangerMargin;

        public struct Result
        {
            public bool hasMines;
            public bool minesCanHit;
            public bool offGrid;
            public float maxYawDeg;   // furthest off-grid target's horizontal angle from straight ahead, 0-180
            public float severity;    // 0 = just past the grid edge, 1 = directly behind
        }

        private static readonly Dictionary<string, Result> cache = new Dictionary<string, Result>();

        private static string Key(string id, KataConfig.Difficulty difficulty) => id + "|" + difficulty;

        /// <summary>Called by DifficultyCalculator with cues it already parsed. No-op if already cached.</summary>
        public static void Store(string songID, KataConfig.Difficulty difficulty, SongCues.Cue[] cues)
        {
            if (songID == null) return;
            string key = Key(songID, difficulty);
            if (cache.ContainsKey(key)) return;
            try { cache[key] = Analyze(cues); }
            catch (Exception e) { DebugLog.Log($"[MapFlags] Analyze failed for {songID} {difficulty}: {e.Message}"); }
        }

        /// <summary>Flags for a song (mapPath null) or a community map (always its single Expert chart).</summary>
        public static Result Get(string songID, KataConfig.Difficulty difficulty, string mapPath)
        {
            if (mapPath != null) difficulty = KataConfig.Difficulty.Expert;
            string key = Key(mapPath ?? songID, difficulty);
            if (cache.TryGetValue(key, out Result cached)) return cached;

            Result result = new Result();
            try
            {
                var songData = SongList.I != null ? SongList.I.GetSong(songID) : null;
                if (songData != null)
                {
                    SongCues.Cue[] cues = null;
                    if (mapPath != null)
                    {
                        if (CommunityMaps.I == null) return result; // would load the parent's chart — don't cache
                        CommunityMapManager.WithMapPath(mapPath, () => { cues = SongCues.GetCues(songData, KataConfig.Difficulty.Expert); });
                    }
                    else
                    {
                        cues = SongCues.GetCues(songData, difficulty);
                    }
                    result = Analyze(cues);
                }
            }
            catch (Exception e)
            {
                DebugLog.Log($"[MapFlags] Get failed for {mapPath ?? songID} {difficulty}: {e.Message}");
            }

            cache[key] = result;
            return result;
        }

        public static void ClearCache() => cache.Clear();

        // ── Analysis ─────────────────────────────────────────────────────────

        public static Result Analyze(SongCues.Cue[] cues)
        {
            Result r = new Result();
            if (cues == null) return r;

            float edgeAtCentre = EdgeYaw(3f);

            for (int i = 0; i < cues.Length; i++)
            {
                SongCues.Cue cue = cues[i];
                if (cue == null) continue;

                if (cue.behavior == Target.TargetBehavior.Dodge)
                {
                    r.hasMines = true;
                    if (!r.minesCanHit && MineCanHit(cue)) r.minesCanHit = true;
                    continue;
                }

                if (cue.behavior == Target.TargetBehavior.Melee) continue;
                if (cue.pitch >= 98 && cue.pitch <= 101) continue; // melee lanes, whatever the behavior

                float col = cue.pitch % 12 + cue.gridOffset.x;
                float row = cue.pitch / 12 + cue.gridOffset.y;
                float yaw = Mathf.Abs(TargetYaw(col, row, cue.zOffset));

                // Compare against the grid edge at this target's height (the edge isn't a constant
                // angle because the dome isn't centred on the head). Purely physical, so a target
                // whose column wraps all the way around back into the grid's range doesn't count.
                float edge = EdgeYaw(Mathf.Clamp(row, GridMinRow, GridMaxRow));
                if (yaw <= edge + OffGridToleranceDeg) continue;

                r.offGrid = true;
                if (yaw > r.maxYawDeg) r.maxYawDeg = yaw;
            }

            if (r.offGrid)
                r.severity = Mathf.Clamp01((r.maxYawDeg - edgeAtCentre) / (180f - edgeAtCentre));

            return r;
        }

        /// <summary>Signed horizontal angle (degrees, -180..180, + = right) of a dome target seen from the head.
        /// Targets that go over the top and down behind come out near ±180, i.e. directly behind.</summary>
        private static float TargetYaw(float col, float row, float zOffset)
        {
            Quaternion rot = Quaternion.Euler(-16.5f + 11f * row, 86.5f + 17f * col, 0f);
            Vector3 pos = DomeOrigin + rot * Vector3.back * (DomeRadius * (1f + zOffset));
            Vector3 d = pos - HeadPosition;
            return Mathf.Atan2(d.x, d.z) * RadToDeg;
        }

        /// <summary>Horizontal angle of the default grid's left/right edge (symmetric) at a given row.</summary>
        private static float EdgeYaw(float row) => Mathf.Abs(TargetYaw(GridMaxCol, row, 0f));

        private static bool MineCanHit(SongCues.Cue cue)
        {
            float laneX, laneY, yaw;
            switch (cue.pitch)
            {
                case 98: laneX = -MineLaneX; laneY = MineLowY; yaw = MineLeftYaw; break;
                case 99: laneX = MineLaneX; laneY = MineLowY; yaw = MineRightYaw; break;
                case 100: laneX = -MineLaneX; laneY = MineHighY; yaw = MineLeftYaw; break;
                case 101: laneX = MineLaneX; laneY = MineHighY; yaw = MineRightYaw; break;
                default: return false; // a mine outside the melee lanes — placement unknown
            }

            // Holder local position inside its spawner = (2·gx, 2·gy, zOffset); the spawner is scaled 0.5
            // and yawed, so in MeleeHeightAdjust space the holder sits at lane + yaw × (gx, gy, zOffset/2).
            // Mirror mode puts the mine at the mirror image of this spot, which is the same distance from a
            // centred head, so it needs no separate case.
            Vector3 local = new Vector3(2f * cue.gridOffset.x, 2f * cue.gridOffset.y, cue.zOffset) * MeleeSpawnerScale;
            Vector3 holder = new Vector3(laneX, laneY, MineLaneZ) + Quaternion.Euler(0f, yaw, 0f) * local;

            return (holder - HeadInMeleeSpace).sqrMagnitude <= MineDangerRadius * MineDangerRadius;
        }

        public static Color OffGridColor(float severity)
        {
            return severity < 0.5f
                ? Color.Lerp(OffGridGreen, OffGridOrange, severity * 2f)
                : Color.Lerp(OffGridOrange, OffGridRed, (severity - 0.5f) * 2f);
        }

        // ── Song row icons ───────────────────────────────────────────────────

        private const string ContainerName = "MapFlagIcons";
        private static Material mineMaterial, offGridMaterial;
        private static bool iconLoadFailed;

        /// <summary>Shows/updates/hides the icons on one song row for the current difficulty.</summary>
        public static void ApplyToRow(SongSelectItem item)
        {
            if (item == null) return;

            Transform existing = item.transform.Find(ContainerName);
            SongList.SongData songData = item.mSongData;
            if (songData == null)
            {
                if (existing != null) existing.gameObject.SetActive(false);
                return;
            }

            string mapPath = VirtualSongList.MapPathForItem(item);
            Result r = Get(songData.songID, KataConfig.I.GetDifficulty(), mapPath);

            if (!r.hasMines && !r.offGrid)
            {
                if (existing != null) existing.gameObject.SetActive(false);
                return;
            }

            if (!EnsureMaterials()) return;

            GameObject container = existing != null ? existing.gameObject : CreateContainer(item.transform);
            container.SetActive(true);

            Transform mine = container.transform.Find("Mine");
            Transform offGrid = container.transform.Find("OffGrid");

            mine.gameObject.SetActive(r.hasMines);
            if (r.hasMines)
            {
                mine.localPosition = MineIconLocalPosition;
                mine.localScale = IconScale;
                SetQuadColor(mine, r.minesCanHit ? MineHitColor : MineColor);
            }

            offGrid.gameObject.SetActive(r.offGrid);
            if (r.offGrid)
            {
                offGrid.localPosition = OffGridIconLocalPosition;
                offGrid.localScale = IconScale;
                SetQuadColor(offGrid, OffGridColor(r.severity));
            }
        }

        private static GameObject CreateContainer(Transform parent)
        {
            GameObject container = new GameObject(ContainerName);
            container.transform.SetParent(parent, false);
            container.layer = parent.gameObject.layer;
            container.transform.localPosition = IconsLocalPosition;
            container.transform.localRotation = Quaternion.identity;
            container.transform.localScale = Vector3.one;

            CreateQuad(container.transform, "Mine", mineMaterial);
            CreateQuad(container.transform, "OffGrid", offGridMaterial);
            return container;
        }

        private static void CreateQuad(Transform parent, string name, Material material)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.layer = parent.gameObject.layer;

            float h = IconSize * 0.5f;
            Mesh mesh = new Mesh();
            mesh.vertices = new Vector3[] { new Vector3(-h, -h, 0f), new Vector3(-h, h, 0f), new Vector3(h, h, 0f), new Vector3(h, -h, 0f) };
            mesh.uv = new Vector2[] { new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f) };
            mesh.colors = new Color[] { Color.white, Color.white, Color.white, Color.white };
            mesh.triangles = new int[] { 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateBounds();

            go.AddComponent<MeshFilter>().mesh = mesh;
            MeshRenderer renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material; // shared; the tint lives in the mesh's vertex colours
        }

        private static void SetQuadColor(Transform quad, Color color)
        {
            MeshFilter filter = quad.GetComponent<MeshFilter>();
            if (filter == null || filter.mesh == null) return;
            filter.mesh.colors = new Color[] { color, color, color, color };
        }

        private static bool EnsureMaterials()
        {
            if (mineMaterial != null && offGridMaterial != null) return true;
            if (iconLoadFailed) return false;

            mineMaterial = CreateIconMaterial("MapFlagMine", MineIconPng);
            offGridMaterial = CreateIconMaterial("MapFlagOffGrid", OffGridIconPng);
            if (mineMaterial == null || offGridMaterial == null) { iconLoadFailed = true; return false; }
            return true;
        }

        private static Material CreateIconMaterial(string name, string base64Png)
        {
            try
            {
                Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!ImageConversion.LoadImage(tex, Convert.FromBase64String(base64Png)))
                {
                    MelonLogger.LogError($"[MapFlags] Couldn't decode icon {name}.");
                    return null;
                }
                tex.name = name;
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.filterMode = FilterMode.Bilinear;
                tex.hideFlags = HideFlags.DontUnloadUnusedAsset;

                Material mat = new Material(Shader.Find("Sprites/Default"));
                mat.name = name;
                mat.mainTexture = tex;
                mat.renderQueue = 3100; // same as the row grade stars
                mat.hideFlags = HideFlags.DontUnloadUnusedAsset;
                return mat;
            }
            catch (Exception e)
            {
                MelonLogger.LogError($"[MapFlags] Icon {name} failed: {e.Message}");
                return null;
            }
        }

        // ── Hook ─────────────────────────────────────────────────────────────

        /// <summary>
        /// UpdateHighScoreInfo runs whenever a row is (re)bound with Init, and on every visible row when
        /// the difficulty changes (RefreshAllVisibleSongRowScores) — exactly when the icons need updating.
        /// Postfix runs even when ExScoring's own prefix skips the native method.
        /// </summary>
        [HarmonyPatch(typeof(SongSelectItem), "UpdateHighScoreInfo")]
        public static class SongSelectItemUpdateHighScoreInfoMapFlagsPatch
        {
            public static void Postfix(SongSelectItem __instance)
            {
                try { ApplyToRow(__instance); }
                catch (Exception e) { DebugLog.Log("[MapFlags] ApplyToRow failed: " + e.Message); }
            }
        }

        // ── Icon images (128x128 PNG, white on transparent; tinted per row) ──

        private static readonly string MineIconPng =
            "iVBORw0KGgoAAAANSUhEUgAAAIAAAACACAYAAADDPmHLAAARpElEQVR42u2de/RcVXXHP/fOBMyL/JKYgOHdBpQQYhGyqkA1UXmo+K5ApUWxYK22LGu1L0Vbsqioi7owC4vLpUZEdFkpIpRaFgUMIF" +
            "qlhIISMBYhPEICxBBC+OU3M7t/nH3629zceZ97Zya/c9a6687Mb3537j17n33247v3TkSEOKbuSOMURAaIo/+RRAaY2kMiA0zdlZ8AM0ZVEqRRDPc8KrryzwbeNKrzuacwQFWJkZTIbALMAj4HzIsS" +
            "YLCrfgz4iBKlUtK8NYCPK/H3jjrA4JSvBHgS+FPgY0BdJUKRor8OHAX8hd7DosgAg3+GXwCfBV4N1EqQBJfoyk+AhZEBBr8NbNTzvwCH6SpNC1r97wZWArv08/mjag7uSVbAJj0vBP4V2CewYpYqge" +
            "cAn8konV4JbEQGGJxZ5iXAOLAU+KYSJA10L4le71PAAfq6YiRAUrIlMvQMUNZkeLH7mJ6nqQ5wKvB5FdmVAPNUB5YBf5azvYwB06MO8MLVcoASJy2JAZ4wz1RRJvgw8EF9XQ0gzS5RBrNeQICZujVE" +
            "BjB75TI1y7yoTApmgC3AjhyFbTVwUh9M4K9zJrCiiUSZWYDOMbIM4Pfd/wDeB1ysk1b0lrMNeCqz/fjj28BLezAP/b4+lqP42b9X9DuRATJ75qdwHrovGe9ZWpCu8RzOIZTnsZsLfE+19W7uITWK3/" +
            "5N/tdLoBfHLWBy1JUo1wDrgffr69kZ7Tm0xbEpxx73+sDLgG8ZqVDR588eFd0q9tZ7XQZ8qIVfoZFhgCgBjFgc19UPLmJ2M3BQAe7aNGMJSE6waEJ1gdX6+3UlXvaoK8OM63Wyil+z8eJRjaJRsBS4" +
            "HPg7FcPHAD8E3g6s09+vBVj9VSXexhbfm6bf+SCwXAmc5GwljwG/BB7FRftWdCi1FjA4T6gMIwOIXv9pYI1aBOPAISoJTgdu6IMJvIOnDjyvnz3dZqV6C2V5l8/RiaRcOADpnRgFeyhdwQ29yS8CO4" +
            "G9lNhjwL8B7zGaedKFWZYYcb2fXucaYFUHIeHEbAHNjpoe9Q7uKzFbQBmh6Ioe/vnn9EPHMhggBX4NXGUmy4vUNcDfmolOOuR4AX4H+DJwj17nLcpYSReT2Oyo6tENQecY5ilCya1kmPdQ4AvAWeaZ" +
            "epDTIkUfFRFJROQYEanrISLSEJGavr7UfD/N/H+i1/Dv3ygiN4nIhEyOmh4NKX809NgqIh8QkenmOdI+5y4VkWrms1eLyOUi8ryI3C8iM3SOkl5+owwGsES9wRDMT54n5NUiMtMwDRnCHysiN2cmf2" +
            "JARG811ovI6ea+qz0QJ808+0wR+SMRWWt+Z6eIHNZk0QwdA/iHOUVvvp5DSBGRO0RkkX53mp73FZE15jv1Aa72dpKgZt7fIiLLW0g2mkg6yywHi8j5IrLBXHeXns81DMawM4B9wHU5k2WZYIOILNX/" +
            "OUNEHs6I+mEfdpsbF5GPN5Fodl6yRHyliHxFRLZlnn1cX383BPFFhKTE3EBv7p0NfLVJUMUHbDYBd6i/gADRvEGMulFcfwKcqwqrn4esGfci4K3An+DQRnZOUqMMPgYcbeIefYFQymQAr53PAO6jeb" +
            "jY+tsbHVgHwz488z6DA65eaWIM6Dz8IfBeDVh530MWzOKvc4oG2ir9+gDKhoR5+3wHcJmx5Zs5a+oB0TyD9rbWceHib+KiigDHqpv8f4BPK/HrxiSu5BD/YiV+NQTxy5YAlrgLgAdGNYYeACH1c2BJ" +
            "hsBpkwXpt8q7gFeaGIaMIijUi7XNwDdCuDIZLfSyF+1H6vuacZmnLcAuz6vDZ1do9HE6wJXwRX2wyihn1/bABKlZwdU20s+v/r8C7g2173fDAEXswd4NfB9wSwtdgD28LkPSgRVR1ZjJ6pD7fjfRwI" +
            "a54dSIMOkzmFEDzgBOKAglNOrDbpXnFLlI0jaiaikujt8w0TFvulV7AHt6qNhSDeTMmkJKYDdbpDd/368+kbQoBqi2UVgWAVfjABL/CfyXmi3bMjfUiYTwRJ6Fy9yZFQizv6cNL/ovw4W4Q4BmejID" +
            "PdetBG400mKTmiS3AT8C7ga25vxvliH8g1wJ/MGIevfKEv3rceip8ZAmXy9+AE+0VwDfxyFjs+MJZYgfKVPczSQyBwPHmgDegcMFROI3F//PqW60rgitvxdHkGeCI4HrcJCucQOeyO7fTyoT3Abcrq" +
            "83qy6xnknwZFT88iXAOHCizt1QMIBlgkOAf8dBrGumNEvDKIdZwv5GdYe5OCyeRKWv7Rbwv8ARxlEkg2YAmyK1H3AtzpedJ8pbMUQkfmdKYAWXhHpp0VKg21iAv5kxtQ5WdLCfW7Mmiv3O9AABtgOH" +
            "43IeS/cDtOPO3wBvVJ3AJ120AzRG4ndXh2AOk4WvkmGRAFkTsYpD5J4ZNftCMp6fARarYl2IMyjtU1mp48AMlxpFMZYfDysFfr9Iq6lfPEBibnYV8Ik9CMgxDBZBgvPCLjGrX4YpOVQM0ud84C+Naz" +
            "dKgjDgmcOA1xZVbSUNGLyoAv8E/HGIpMU4XiAFzioqaBYaEuZdvm8HrsAhXUcd1DkMMLJNahJuD72w0oKg0Dfh4EvR9OtfGfTOtxV90iwxvpikKAbwCuHrcM6ielz9wUzC17fYBixxbXJrNaOT7Rau" +
            "rxaE/T8hKoLB9bTjDYawmuN2bzffe+NqGPwWDl/4FJBUCwIz/F6M+AVHEx+BC6htyfnOXuoz2FeJfKAeB+FAPfuqNfEcruTNOr9YqwXc6FwmM1yi+A+3rc5Qa+AhXG2ARUrkRcBLdMsda3Gda3Ho4v" +
            "VFWQE+UHQ8sLbE5g1x7G461gxu8xe4Gk3XZOhUWI2gw42bOI5iAKOSkbCJkcCiW8IuXMrZRbiYQmq2aYpgAH8zi6MCWOh2UGmThArwA131d+Wt+iL8AIkROQsjnSgbQOJBtxtxBbPeoMSvtku/q/Zp" +
            "nthMXp+3dmRUAEvdDnxq3WpcQG6LWdi1kHUCrRepZiprgqvBexwud31JNAFLg41VgFtxldZubyfue4kFZFe5HUcAr8GVXz0+iv7S4wNbgH8A/tlIgq5zCLIMkOTU48PUxF8OnIxzSx6dUUgaJfQGiC" +
            "5hN7dfAz4JPJLBZASPBh6Kc+uehGvHdlCO5plEAEhpDDABnGZs+r7TxiwDTFMxvgwXeXoN8HJcSNePCQNMiCu9fLG/Hef12xmqS5lVAo8C3omL5C1mshcemXg/OWI/bwuJo5gxG1dnKSkSEJLiUrgW" +
            "AQfr8dt6PlS5cH7U9EuXADtx0bxNoYAhlgGy7sRWYy6u0NNBeizWGztUX8+LWUAUkTL2kErqZ0N5W6s5AM88H3NivlPHpYNvxVX6yo7PAR8toYnzVLQAHlU9IFiOQLXND0obeJFljgrOG7gx0quwsT" +
            "m0l7XaJxw877N7oyu4MAlwT+i5TQu4yV+pspLGiCChI60/H+Y6gR7D/rhuAzEvgKC4wAlcsY2hLhSZqh5w+6i2Ux/i5JCHgQdDp4qnBYmqH0Y9ICgDgAN5jIe2rNKCbvaOmBgSPEdwCc6/MhESa5kW" +
            "5LDYoPtVErGBwRhgpUrWw0P6WIpqHw8uNzBiA8M2416KA4CsJFBBjrTAfevBqAcQumFkHRex/QG9Nd0snAF8IejFuFKnEvWA4PPbwEVl1wB/b7bYdNDp4d4/vUDF1Esz1cbjoBBA6NdxRaV39VJSLh" +
            "QD+JjANFxd4RMybVBiZJBCG1LdjCu/v7lblFAauAvGFUr8XaYNygOR+BTdim+lSt2l3SqHaUDl5Au4ilbP41KTGji48rG4OLZEz2ChTHC4moknGyZIimaAafpjfw38uRL/RWoBnIjLS9uOKx4VYwPF" +
            "t6abh2sxc06mOWUhOoDnvLNUEfFc9z1c98vNpph0HRcfOC42iaCMuEECXIgr20crAEmvDOCJfyKuXOxe+v4TTDZGrJiaQYJDGP80Akc7bhdDzrlbC+EKlQbjzSyEXhjAX+jlqn3OxWEA3oerC5DmRA" +
            "KrhkFWxbKyQVLDacIkthnlNKXJabjGHrtZCN0ygBcl++MCPgcC38X1xH2yhQmSZBopvyJuBbloqgldVAtwXVVn6XlmH4kke+GAJO8E7s/qYt0wgBfl01WULwE+jKs500lSomeelwE/0+tEJ9EL7fn3" +
            "AJebeZltmGAeDoo/H+cOXoCD7vvP5+p3Z+tRydEPTsOV+f9/Cd0pA1hU8I9VApymUqCbpETPJGcA3wrhy94DxoSK6tXAeUaj70U5m67MMjvDMPvgcjxmqGXmC01JJwxgif99/ZF34XoG9JKb5v/nIj" +
            "Ufp7I+4J/9VlzupV1MSZs9vp1uQChXsN+/P612/id7yUNvwgSXKNdPxe6hnvi34Zpv7AjQHyibz5FlmN3S/DupD1DBlRwZx/WuTwPdqI9xXw281ZguU4n4j+MqqmwtsjtovxJgrt7slsANjHxpk6OA" +
            "O6dQTyFv/WwE3kJJ/QH7SQzZmkGlhPZcfWSQEzBAsf8OU89nYM/ejRUgBQAblunqT9mz0UNicHy36MrfPgyMn3aZ9RN6Ui4wzJB0mILGiGb2VFXpfb1J8By41AvdMKIbV/JxKgqbwcYaGbdyOsIevp" +
            "04b+k3MmY1w1KKfBCJjqtabC0+lLnNEH9UO5Il6uy5IYOQYioygBf3J+EaIWVNP9t/aC0u4LQSuM8AHEaJERLTafXzwwiIKXsL8Az3Y1zJORsQsq8vBv7GeBmn46KNF+BcnJhuJOmQlG9pZ+0I8Luq" +
            "9A6N1VMmA3jv37uA72QI7s2jJ4EPAFdlClD4ydof+BAOBTt/gPUJLbytkgFitLL9b8RhKKYcAyRGE16HiwhKRjKs1VX+q5yASJLRml8CvE2Z4cicwslp4G5lkgFrVDJ+krkdKKueCd4EXB+ixl+YJx" +
            "Mp46jo+Vxxo6aHH58x36m2uE5ivuev+wYR+aqIPCK7j7q0Ho2co27ubyJzn35sF5HrRORsEVkgIh81f6s1+a2aXn+dPmNa0ty3PMqQAH4lTsd1rzjQABWeUPPo6i5LniY5dvQcVRhXAK/CQaRnBMpJ" +
            "qOPg7T/BRe5uxOXr23EqDhs5r0WE00uBc4CvDMNWUAYDeFH3MeCzJv59k07Eg33EwJNmnTCU0d6NCztLkzDqjpxrPoWrwbtZGfa/cX12HsiI7NQwoU+JOwL4tno485jAbyGP4gA1Owbu7CpYxKR6zB" +
            "eRTUYc/qMR5ZVAv+W3Byte9xGRZ4y4tyL6ZhFZKCL76XmBnqe32cqaie+q+c2rMmJfcraI8wM/f09H0T/gJ+UCfehHRORUQ7Ci9kHPDImI3JqZ+Ak9X9iGcT2xK/o+6ULXQURWNdFFvJ6xTUQWFTwP" +
            "bY+0YJu/BhyA6yx+g+7N1xnR2CjYNhe1LsgpgnmnyWfM1j1sqGiv6blTxI31TZwPnKliPjXbh7+vfXDgmoHmTRapA3hTaY2aSueFKnHeZczhZFw+vbXVJ3QP3lAgEMM/53LgSlzKfM0ky/gI4dGqay" +
            "TDCgjpp4nkITrR1/fb2KCPe5ivhB4zCtsGva+JglPWPBPsiwsEnWiSZbzz6lpceHggFkFacMDnYSV+OoDkUO8QeorJ9mle8txtii1JwQCQipq7p+CQv5VMR9A3q+k6kDyJtKQq140BB7uyesDPSgSg" +
            "2PS489TvUcvoBRcNKkqYlljnbpA1dtdmIHB3lhyTb5jfv0z1ksfUGTaOCxKdPghg7CAAIYPovD0G/BKXSfMsLpf+8QFJJ68XHIxLjnmVvv+1OpDGy3QOpVMg5y7FJbH8VD+7X4mfDEg6eb3gIRwm4u" +
            "vKFIt1eyhVCkwFGLZ/xluUIe7KmKkMCBqe4hJt3otr6y64fr9jZULg0ilUa/9WXfV3MlzFHCq4Liun4gJmF7YAyUYG6EMBuwd4Wp0uwwLKtHDx61UZPA44xkiJyACB3MLPqtPl4SEsZe89hPeqhbBf" +
            "WbTZ062ArDVwDA5g+twQ6yuNgpJxpjQDjGJ7mGgGFji5o5BMEs3AAi2COGJ9njgiA8TB/wHRfH8PQcRa1QAAAABJRU5ErkJggg==";

        private static readonly string OffGridIconPng =
            "iVBORw0KGgoAAAANSUhEUgAAAIAAAACACAYAAADDPmHLAAATXklEQVR42u2da7RdVXXHf3vvmwQCMSG8RF4ihEBhIA0oiIAFgWJLEBQpD9FSrEDQ0tZH+6ll1L47OoYdHUBEgmBrQOgAX6hBrEJ4DA" +
            "2xFkUIRDCVlBChJEBo4N5zZj/MuZqZxdrnnnPv2edx715jnHHvPfec/Vjzv+aaj/+cOxMR6jF9R15PQQ2AetQAqEcNgHrUAKhHDYB61ACox3QaI1P0vjL3wv0EkOj3LPpe/DlxrxoAAyro3AmrWZHA" +
            "wnmyCBRDDYxsiCKBsbAbLSZ/NrAb8EZgd+BN9nMuMB/Y2T4zB3gJeAV4GfgfYBPwK+C/geeADcDzwJYW11XY781hA8WgAyC3l5jA4zEHeAtwiHu9GdgH2BXYsQvX8L8GiqeBXwA/s9fjwJPAi4nvFA" +
            "aMpr1qAExAzY4lwLAQOBJ4m70WAHu2OJ44AUjiXIzzvzz6XDyeBdYAPwZ+CKwGnkiAdSTanmoAJIROYuIWAMcD7zKBL3Tq1g+/HWQJoXWqlsuO4Q3H1HWMGQB+CNwDPGAAibUDgwKGfgGgTOhzgWOB" +
            "U4GTgcOAmSXC9pa+X+lZGyu30+G3oPi80gIUo8BPgO8DdwEPRltG38HQawAEwXihzzdhnwm82wy2lMDzhLDzFrGM18yIe8FU9Ubby58HNtve3jAhzHYG4jzbVvYEdjFjckbJOZrRtfjrSwFiPfBd4C" +
            "sGihciMEivbYZeACCsSI/y2Sbsc4HTgD0Sk9rOhGIW/FOmatcAj9rf6w0AL03i2ueY97A3cABwqG1DBwP7mzdBm4CNwfossAK4Ffie3UfZfA0lAFKr/WjgfOBsm1CibSBz1rM4a9qr4rXAw7bPrrK/" +
            "f9nmtWRtqvt2VuI+BoZFwNvNOH1LJOSwdWTOm5FI/WOAvR242QzJnmmFKgBQREKdD5wFfBg4MVrp4iasmZiYMDkPmOpcZat8tIXrRRcDNVkiqljmks4wzXAscBJwXARyPyf+nrMINPcAN9k28ULVQO" +
            "gmAIpIbR0KXAx8ENgrspTzaKX7iOSrwENmNN1lLtbWEmH3MyKXR8ZgDIodgbeaQXu6ab+Z0Tx4zdCM5mE9sBxY5jyJ8PnGIAEgFvwJwBJT87MSyE/d7KvAvYb6u0ytlwVWBjXSlkUCTbm0p9q8HA/s" +
            "0GJReE24FbgDuBq4v9tAmAwAYsEvBj5uN+lvrGih4h8Avgx8MxJ6Fqk9GdI8hbeD/D0caPP1AdsqyraIRrRQVgD/DNzZLSBMBACx4M8CPmGoJrJ6g+C90H8B/Jupt/9ICH3gw6eTDGvHYDgKOM/AsH" +
            "8EBj+HPraxEvhH4KstDO42Ixwi7b5yESnc32eKyP2ybTREZMx+b4rIqPtfU0TuEpHzRGRnd4xMREbs2EyjV273nbn3dhaR80XkOzZfYYy6v8dsnsNYKSJnuGMUnc5lOx/KIsH/hoisiATfSIBARGST" +
            "iFwrIkdFxyymodDbXVjYfC0Vkc1uLr3wYyB8W0ROiOY36wYA/IUdKiK3tCn4p0XkMyKyX+JGs1roLRean583i8hf2Hy2A4SbTU4p+XUEgNyt0Hki8jcissWp87HE7yIia0XkD0Rkl3q1d1UrzBeRK0" +
            "XkiQgIzcTvW0Tkr0RkbkKWbQHAn/g8E6o/aUrwj4vIx6L9faRe7V2zFbyd8PFxgBDGEyJy7njaIKWGEJH9ReT2FoaIV/V/LCI71YKvfHuIgfBJEVlfsji9AX6b24qzVgAIaLtIRDYm9hi/52+2vWm3" +
            "WvB9BcLupu5fHMcu2yAiF6S24xAHCNmnXwMecX5oKvv2ReAvjfgQ2C6NqcqaHeAgU+FYUwuBPwMuKMlOFu5zjzt5vy6XPpII3oTQ60PAKZbUecKFZ8dq4fd8iMslFJYruNBS66sTWUcpSbT9vwYIEa" +
            "YdLDp3sPtSbsI/xgFDpmi0bpijjJlb7astEeUzro9YQuo1T3HzfPrCWDKfd0mJkOlaiLJta+EP5vD8iQOBgxxNLfy8zpJuXiNstwWEOPK/opSpEff+HOD3o/h0PQbPLmgAlwI7OXmOoPUOy1PE25i9" +
            "UqBUpdsSCYkPGRAaXSZc1qN7wp9r/Iuw3wdh34JyIYvYXitbzUsjHlsDJWu+zwGlHoMzgjw+gPIrG85AbACfK6l/eB0AwopfDdwXpRgFuCwiLdRjMEaQ2+XRexlKOH3Yu37jaYDw3jVO1QfVcSzwjl" +
            "oLDNzqF5SJtSjBv7imlbbPS9CUAV8H1iVIGpfXfv9AjiURrb4Afo6yrUrJInlJkKFAeepfiAJDgnLa9qk9goHx/xvmoi92sguLdVnK9WvHCAwH+IIBwVO9d0KjgXWHkcHp8HIxykL2rt/LKL2cVjZb" +
            "KwAUwH8BX3MqJHz+EjRqWLuE/Xf9ZhsAiAiit6M9DoqJAIASIyKc4ADgt2tjcCCMv/cC+7oFGt6/drJNosLqvg+tyInpx1eMp17qQdXh32D8SRTlexD4QTuU8bxNlC1NvPcutB5OalugL6u/ifZMeG" +
            "cUDMps9bcll7yNAEOG8vg3OOEHdXNZotNWPXo3LosKTwq0lc0d7dYJ5G3knQu0qcG/JAgGv4OWT9fGYO+NvzcC5yTi/jehDa2KduI1eQd7zfVoLtm7hPNQIgK1MdjzuP+HgDewjRVUoHWEyzqxzdoF" +
            "QI5Sib4dcQVA08QjtRbo6eqfCXwkcv0ytGbwqfFcv4m0is0ilzCLeISn1sZgT12/09Fq40bUeeTqThdh3kG2KUObNPw0UeS4pCzdWI9KXL8rEpnAH6Ml9nRSJJp3iL4xlDIWI/I0tEljs9YClbt+h6" +
            "NNteIg3NIWTO6uACCgajlKMfIuYbwn1aO68dGIil+grW1v7XT1dyqscLLn0KYOsUv4QZSSNFYbg5UZf/PRJlux6/cltJ/QSKfb8ERXq1c34eL2RClJtUtYnet3Htq30FO+Rt223Kz6gRHB4HgYpRrF" +
            "lLHLu93EqB7badpLE4b53Wjz6rwXAIgpY7ExuAilJtVZwu67ficBR7hUfdYO5asKAATkfROlHMWUsSW1zOgF5SsHHkMbR2UT1br5BOvSCpRqtCxBGVuMNjuqKWN0jfJ1EPBbCcrX9WYDFBONweSTDE" +
            "jchFKPfBXRjsDv1S5hVylfl6A9Fz3lazNaxTUpTsZkAFCglKPbE1VEv4tSler8wORdvzlo4iemfN2GVnEV/QAAEWUsqKZwgfuhVKXaGJy88fc+tCprQpSvKgEQLugHKAUpdgmX1JSxSbt+WUm1z73A" +
            "j7rhcuddAtC1iaDFO1HKUrPWAhNe/e9g+74MRPOd0+cnhwZE3oF2tw77UQq99eh8XJ6o9lnH9lT9vgIg7PFbgBsT7WXej1KXapewc9dvH7QKK3b9bkAbeRTdSL/nXcxR34BSkgrXKfwNkQVbj/Zl8m" +
            "G2b/QQyvVu7KZt1S0AFOhDFO9MVBF9BE0X1y5h+67fDub7x5Svr6LVWsUgAcCPqxNVRAtQClPtErZv/J2BVl/FlK9rur2I8i43KLgXpSbVVUSTr/aJDe1V6AM2sm5mW/MKjJdUFdHJKJWppoyNT/k6" +
            "Eq26kqjaZ2kV85d3OXAByhb6VUQZG0GpTNR2AO1U+4TFFOyrDWh1Ft3mWuRd7l5ZoE/nXJ6gjF2AUppqY7Dc+NsdrbYiMvS+iFZnjXSbeZ1X0MIUtCnhaNROdleU0lRTxsopXxei1Vae8vUamvatxI" +
            "bKKzBicpSidLdDdljxl0Zkxnps05Qzom0yzNu30N7M+TAAIFVF5NXZESi1qXYJX28on4I+bDN+Qtg1VdpOeUWuTIY+AHJNgjJ2RS3z5LZ5ReKxez8F/r3K3ox5RTcU9q7PR+8J8B60oXGdH9im1g9B" +
            "q6viuP917uGbMiwASDWe9iXls9hWRTTdvQFfYT3DVfu0bPA8DAAoazxd1I2ntxN+E62murCTBs/DAAASjad9FVFoPD2dXcIg2HPRqqq2GzwPCwBaNZ4GJTtM58bTYX4u7bTB8zBpgLyFS3gMSnmajp" +
            "Qx3+D5qG5X+wwSAAKiv0Z542nqap/tqn3GbfA8TADwzyK6oW483VaD5+sZp8HzsBmBdePp9ho8F8BLaOKnZ9yJXgGgAH6JUpqmc+PpzAH/4gTlq60Gz8MGAH/zZY2nz5gm+YF2Gjxn/VBHvXB5QClN" +
            "qcbT06WKqIzyBdsaPGe9zJbmfaA8tWo8PZVdwnCvbwOOi+4162a1z6ACIKC6VeNppkHmzwfAAgjW00GD52EFwHRuPB22vL3QaqmY8nUjHTR4HlYA+Li2bzwdKGNTufF0mOeL2NbgOZtog+dhBkBQeb" +
            "7x9Ji7jo+yLS3KFIv7z4qaaQYQfANt8JxPBwDQ4llETZQSdYrLiU+FMTJOg+dQ7ZP1UzX1o/GBbzydooxNFZcwfraPRA2e76ma9DGIGiA0nr7O2QYjU7DxtG/w/G6nEUhwJZhOAGjVeHoGSpGaSvkB" +
            "T4f3DZ6/3M/V388JDpPwPEp9il3CC+ms8XTm9tXCVpl/FR3ea9biWP542SQbPG+qotpnWIzAMD7XQePplGAyR6UO7WnGolfD7cU7o3zEmc74mmmAm+sEUnYsfzxx1+jBkbnrPh+tihor2f76autkIj" +
            "IItOjvmPXvU6M/QsOmeUSaSI2ZaBft3SzYsoe9drP4wt4m3J+j9LTVaHZyk31/PpqUOgYNSy+wwMwG+7kJbZO/0VT3Bns9746RurfM7uOI6N6+hXb+zKc7AEZsNZyNpkIbznDKTRj3us/vgPYgXIg+" +
            "q+gQtMZgbzSKOKfkPE8Cf4emo58d55p2R7N1nzYglI0xA8BG8+PXAo/a60ngGbQs/rvufsLPM4GvD0KZXL8BkDnB/gxlyTSdjfAV4LPAiaYNDjcAFOO4XQ0Xb/9zE74kahH8e5lz08J7fwpcZUIT97" +
            "nx9v/NwCMGpgXuuMH1W+QM32Y/bYBB2AJmoBSoy9CM2FgbQaBGQnhZ9Ei7rSjl+hvRU868evZ/S/R307VrudVA6gEk7qdE9kDWouf/MuCPUPZP7C7KdDECM3fTr6J1AnskjL4glNjwKkoscnHfOceE" +
            "PyOiYYsz7prR8SXiKsywY5zjPhsDr8wobUT7e1jxl9g2cb2LDTT6RYjphwbw+97hFiE71yzlbj1Z40+AvzcBjkbn3QklY55k9gNmHH7P9uUt0TWGY3zatpJGlwX1kHlCX0LJs3mkUSp2yEV6+Srs59" +
            "4islREtsq2MSaTG+H7q0QkE5ER++nPe7aIrGlxjDX2Gf8df6xVXbrWph2j6d57REQucHOV90ImvRR+uKHzROQZd+Oj0URMFgCL7Twj0XmvjD47aj/972FcGX03HGtxlwDgRyM63u0islevQNDrlX9V" +
            "BYIPkygislZEZiZW/ulOcGPjgCj8//SEJphp5/DnrAIIa0XksF6AIO/hnv8Jc8mC8TXSxRRoMLZWsP0TzptoHv6ziX48rXr1YN+Z5Y4deh6sqCiCl7vHwB1owaI3Vf1M5rxHVKiFwF87AyqvKJ6wqq" +
            "T1ysIOjLfCXfMpCet8VcW9DYLRua+LX2QMeXHoRRaurepmwjHXOfcxvHfSBKxqcY9qw7l2/hxZD0gk8dNChjYOcFDFbk0QxpZElG+/CQors+/GNfpbegCAcOzZaO3k0AeC1lU8YeImrMw+mIxt4cfs" +
            "HjRuCMd+1XIKQwuApst9NyNVWsWE7RtF6bAkjUzwmGsTx9u3BwAIPIg70axlPkxdwlIs4IeBz7hcuFQEgKMT793lQradzEtodRcL++iKATBmhuBzwCerJov2sjr4KuCf7OYyR5Do5n2cZgZUw6Ve70" +
            "Pr7vI2zxlo6g/ad3OXYRyxc1TVZjckwjagSainqm6jk/eI/hWE8YdoaZRvfNzowmry/fZOjnznJvAxNDs4Mg4IggC22nfibOHJFRBWm07lj6CNIY9HC0WrLxPvcS4gRLUOFpFbokjY6CSjayGKtrIk" +
            "FHyWiGxxsfjR6BWiklvss6lQ8Mou5wL8/a4TkSWJKOaUyQWQuLHjRORmEXklmpzRRLKkExAssePPiM65SERWtPj+CvuM/044xpJJCr9REop+TEQ+JSLzXcg575U8+kUIySMv4WC0OPT9wFtJkz/aYe" +
            "KEAM5rtlevdJE1n+I9Gu3QdZDzFFaiqVkfDQzfPcEMwpltVvFI9IpJIptM1S83S39rIlXOdGEE+ZLoHKV+vQclS/w6mr9P2RQSMYF8983cJvm9KKfQA6cV88Z7C+E8J6JcwnmJvT8WNFHZlx9PA/eb" +
            "wO+O/Pu+MYL6DQCiSYsNtH0NEOExtIegPLtWBpV/TM0rwKfYvilFHjGISFDLcY9v+QcL/vgi1nwcTfCaBb8eNmPuAeA/gZcT4J/WnMCyFZiVeAfz0UzZocBhaMLmAPTppLu2SPasBv4WrUh+eZxrmA" +
            "38JkoIffs4n30ZZQavQ5nAj6Jk0MfsvbGSbGNfhT7IACjbIrJx3MV5aDHJHihFfE9XF7CLxdNnAT8Bvm97/XqUvRu+vz9wrG09R9reH+oCNru6gI1oJ69n2FYn8CrlmcVsENi/wwqAlIaI9+lOjKYd" +
            "3eNYXzFVjYFjZ7dtvDhBkmuZXTC4EzpkAGiVOcta7MvNCQqkbK8vo4QP3fg/TMtJCsejWPsAAAAASUVORK5CYII=";
    }
}