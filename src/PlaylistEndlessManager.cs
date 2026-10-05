using System;
using System.Collections;
using System.Collections.Generic;
using MelonLoader;
using UnityEngine;

namespace ExScoringMod
{
    public static class PlaylistEndlessManager
    {
        private static List<SongList.SongData> queue = new List<SongList.SongData>();
        private static int index = 0;

        /// <summary>Feed the marathon the exact songs to play (in order). Set before starting.</summary>
        public static void SetQueue(List<SongList.SongData> songs)
        {
            queue = songs ?? new List<SongList.SongData>();
        }

        private static float volumeFadeTime = 3f;
        private static float originalVolume = PlayerPreferences.I.MusicLevel.mVal;
        private static bool fadeInProgress = false;
        private static bool previousNoFail = false;
        private static bool pendingReset = false;
        public static bool EndlessActive => PlaylistManager.state == PlaylistManager.PlaylistState.Endless;

        public static void StartEndlessSession()
        {
            MelonCoroutines.Start(IStartEndlessSession());
        }

        private static IEnumerator IStartEndlessSession()
        {
            MenuState.I.GoToSongPage();
            ResetIndex();

            if (queue == null || queue.Count == 0)
            {
                MelonLogger.Log("[Marathon] No songs in queue.");
                PlaylistManager.state = PlaylistManager.PlaylistState.None;
                yield break;
            }

            // Give the song page a moment to come up before launching the first song.
            float waited = 0f;
            while (GameObject.FindObjectOfType<SongSelect>() == null && waited < 3f)
            {
                waited += 0.2f;
                yield return new WaitForSecondsRealtime(0.2f);
            }

            previousNoFail = PlayerPreferences.I.NoFail.mVal;
            PlayerPreferences.I.NoFail.mVal = PlaylistConfig.NoFail;
            pendingReset = true;
            if (PlaylistConfig.Shuffle) queue.Shuffle();

            SetNextSong();
            MelonCoroutines.Start(ILaunch());
            yield return null;
        }

        public static void NextSong()
        {
            MelonCoroutines.Start(INextSong());
        }

        private static IEnumerator INextSong()
        {
            while (fadeInProgress)
            {
                yield return new WaitForSecondsRealtime(.2f);
            }
            yield return new WaitForSecondsRealtime(2f);
            float previousSongHealth = ScoreKeeper.I.GetHealth();
            AudioDriver.I.Pause();
            SetNextSong();
            InGameUI.I.Restart();

            // Load AuthorableModifiers' modifiers for the next song. No-op (Busy stays false)
            // when AuthorableModifiers isn't installed; the bridge never waits forever.
            AuthorableBridge.BeginLoadForCurrentSong();
            while (AuthorableBridge.Busy)
            {
                yield return new WaitForSecondsRealtime(.2f);
            }
            yield return new WaitForSeconds(2f);
            UpdateVolume(originalVolume);
            if (!PlaylistConfig.ResetHealth) ScoreKeeper.I.mHealth = previousSongHealth;
        }

        public static void FadeOut()
        {
            MelonCoroutines.Start(DoFadeOut());
        }

        private static IEnumerator DoFadeOut()
        {
            originalVolume = PlayerPreferences.I.MusicLevel.mVal;
            float currentVol = originalVolume;
            float time = 0f;
            fadeInProgress = true;
            while (currentVol > -5f && time < 1f)
            {
                time = Mathf.MoveTowards(time, 1, Time.unscaledDeltaTime / volumeFadeTime);
                currentVol = Mathf.Lerp(originalVolume, -5f, time);
                UpdateVolume(currentVol);
                yield return null;
            }
            fadeInProgress = false;
            UpdateVolume(-5f);
            yield return null;
        }

        private static void UpdateVolume(float amount)
        {
            PlayerPreferences.I.MusicLevel.mVal = amount;
            PlayerPreferences.I.UpdateAudioLevels();
        }

        private static IEnumerator ILaunch()
        {
            // Tells AuthorableModifiers to keep the arena between songs. No-op without it.
            AuthorableBridge.SetEndless(true);
            MenuState.I.GoToLaunchPage();
            LaunchPanel launchPanel = null;
            while (launchPanel is null)
            {
                launchPanel = GameObject.FindObjectOfType<LaunchPanel>();
                yield return new WaitForSecondsRealtime(.5f);
            }
            // The LaunchPanel.Play prefix hands this to AuthorableBridge, which loads the
            // modifiers for the song in SongDataHolder and waits for the arena before the real
            // Play runs. Without AuthorableModifiers it launches immediately.
            launchPanel.Play();
            yield return null;
        }

        public static void ResetIndex()
        {
            index = 0;
            if (pendingReset)
            {
                pendingReset = false;
                Reset();
            }
        }

        private static void Reset()
        {
            PlayerPreferences.I.NoFail.mVal = previousNoFail;
        }

        private static void SetNextSong()
        {
            SongDataHolder.I.songData = queue[index];
            index++;
            if (index == queue.Count)
            {
                PlaylistManager.state = PlaylistManager.PlaylistState.None;
            }
        }

        private static void Shuffle<T>(this List<T> list)
        {
            System.Random random = new System.Random();
            int n = list.Count;
            while (n > 1)
            {
                n--;
                int k = random.Next(n + 1);
                T value = list[k];
                list[k] = list[n];
                list[n] = value;
            }
        }
    }
}