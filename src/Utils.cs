using MelonLoader;
using System.Collections.Generic;
using UnhollowerBaseLib;
using UnityEngine;
using static SongCues;

namespace ExScoringMod
{
    public partial class ExScoring : MelonMod
    {
        public static string spriteDebug =
                            "<sprite=\"EmojiOne\" index=0> 0: Smiling face with smiling eyes\n" +
                            "<sprite=\"EmojiOne\" index=1> 1: 1f60b\n" +
                            "<sprite=\"EmojiOne\" index=2> 2: 1f60d\n" +
                            "<sprite=\"EmojiOne\" index=3> 3: 1f60e\n" +
                            "<sprite=\"EmojiOne\" index=4> 4: Grinning face\n" +
                            "<sprite=\"EmojiOne\" index=5> 5: 1f601\n" +
                            "<sprite=\"EmojiOne\" index=6> 6: Face with tears of joy\n" +
                            "<sprite=\"EmojiOne\" index=7> 7: 1f603\n" +
                            "<sprite=\"EmojiOne\" index=8> 8: 1f604\n" +
                            "<sprite=\"EmojiOne\" index=9> 9: 1f605\n" +
                            "<sprite=\"EmojiOne\" index=10> 10: 1f606\n" +
                            "<sprite=\"EmojiOne\" index=11> 11: 1f609\n" +
                            "<sprite=\"EmojiOne\" index=12> 12: 1f618\n" +
                            "<sprite=\"EmojiOne\" index=13> 13: 1f923\n" +
                            "<sprite=\"EmojiOne\" index=14> 14: 263a\n" +
                            "<sprite=\"EmojiOne\" index=15> 15: 2639\n" +
                            "<sprite=\"icons\" index=0> 0: icons_0\n" +
                            "<sprite=\"icons\" index=1> 1: icons_1";

        public static string targetIconDebug =
                            "<sprite=\"TargetIcons\" name=\"standard\"> standard\n" +
                            "<sprite=\"TargetIcons\" name=\"horizontal\"> horizontal\n" +
                            "<sprite=\"TargetIcons\" name=\"chain\"> chain\n" +
                            "<sprite=\"TargetIcons\" name=\"chainstart\"> chainstart\n" +
                            "<sprite=\"TargetIcons\" name=\"hold\"> hold\n" +
                            "<sprite=\"TargetIcons\" name=\"melee\"> melee\n" +
                            "<sprite=\"TargetIcons\" name=\"mine\"> mine";

        public static void ResetExScore()
        {
            difficultyUISetup = false;
            songListUISetup = false;
            launchPanelUISetup = false;
            favoriteButtonSetup = false;
            GameplayStatsUpdateDisplayPatch._hasRun = false;
            processedCuesIndexes.Clear();
            exCues.Clear();
            exScore = 0;
            judgementScore = 0;
            currentMaxPossibleExScore = 0;
            currentMaxPossibleJudgementScore = 0;
            DestroyIntensityGraph();
            DestroyIntensityGraph();
            DestroyHeatmap();
        }

        public static int GetPercentFromRaw(float rawScore)
        {
            return (int)(rawScore * 100);
        }

        public static float GetTimingMsFromCue(Cue cue)
        {
            float startTick;
            float endTick;
            float tickSpan;

            if (cue.tick < cue.successTick)
            {
                startTick = cue.tick;
                endTick = cue.successTick;
                tickSpan = AudioDriver.TickSpanToMs(selectedSongData, startTick, endTick);
            }
            else if (cue.tick > cue.successTick)
            {
                startTick = cue.successTick;
                endTick = cue.tick;
                tickSpan = -AudioDriver.TickSpanToMs(selectedSongData, startTick, endTick);
            }
            else
            {
                tickSpan = 0;
            }

            return tickSpan;
        }

        public static List<Cue> GetChainFromLastNode(Cue lastNode)
        {
            List<Cue> chain = new List<Cue>();
            Cue current = lastNode;

            while (current != null)
            {
                chain.Add(current);
                current = current.chainPrevious;
            }

            chain.Reverse();
            return chain;
        }

        public static TargetHitPos GetTargetHitPos(Target target, Vector3 intersectionPoint)
        {
            Vector3 targetPos = target.GetContactPosition();
            Vector3 localPoint = intersectionPoint - targetPos;

            return new TargetHitPos
            {
                x = Vector3.Dot(localPoint, target.transform.right),
                y = Vector3.Dot(localPoint, target.transform.up)
            };
        }

        public static float GetChainAverageFromLastNodeCue(SongCues.Cue cue)
        {
            List<Cue> fullChain = GetChainFromLastNode(cue);

            float total = 0;

            foreach (Cue chainCue in fullChain)
            {
                if (cue.behavior == Target.TargetBehavior.Chain)
                {
                    total += chainCue.aim;
                }
            }

            float chainAverage = total / fullChain.Count;

            return chainAverage;
        }

        private static string Colorize(string text, Color color)
        {
            string hex = ColorUtility.ToHtmlStringRGB(color);
            return $"<color=#{hex}>{text}</color>";
        }

        public static string GetTempoString(Il2CppReferenceArray<SongList.SongData.TempoChange> tempos)
        {
            float min = float.MaxValue;
            float max = float.MinValue;

            for (int i = 0; i < tempos.Count; i++)
            {
                float bpm = tempos[i].tempo;
                if (bpm < min) min = bpm;
                if (bpm > max) max = bpm;
            }

            int minBpm = Mathf.RoundToInt(min);
            int maxBpm = Mathf.RoundToInt(max);

            if (minBpm == maxBpm)
                return $"{minBpm}";

            return $"{minBpm} - {maxBpm}";
        }

        public static void FixMappers()
        {
            for (int i = 0; i < SongList.I.songs.Count; i++)
            {
                switch (SongList.I.songs[i].songID)
                {
                    case "destiny":
                    case "adrenaline":
                    case "collider":
                    case "golddust":
                    case "hr8938cephei":
                    case "ifeellove":
                    case "iwantu":
                    case "lazerface":
                    case "popstars":
                    case "perfectexceeder":
                    case "predator":
                    case "resistance":
                    case "smoke":
                    case "splinter":
                    case "synthesized":
                    case "thespace":
                    case "titanium_cazzette":
                    case "reedsofmitatrush":
                    case "destiny_full":
                    case "popstars_full":
                        SongList.I.songs[i].author = "HMXJeff";
                        break;
                    case "addictedtoamemory":
                    case "breakforme":
                    case "channel42":
                    case "everyday":
                    case "gametime":
                    case "highwaytooblivion_short":
                    case "overtime":
                    case "tothestars":
                    case "addictedtoamemory_full":
                    case "highwaytooblivion_full":
                    case "avalanche":
                    case "badguy":
                    case "believer":
                    case "betternow":
                    case "cantfeelmyface":
                    case "centuries":
                    case "countingstars":
                    case "dontletmedown":
                    case "exitwounds":
                    case "gdfr":
                    case "girlsbedancing":
                    case "intoyou":
                    case "juice":
                    case "longrun":
                    case "methanebreather":
                    case "moveslikejagger":
                    case "newrules":
                    case "sorryforpartyrocking":
                    case "starships":
                    case "stook":
                    case "thegreatest":
                    case "themiddle":
                    case "themotherweshare":
                    case "urprey":
                    case "weallbecome":
                    case "youngblood":
                    case "allstars":
                    case "howweknow":
                    case "preexistingcondition":
                        SongList.I.songs[i].author = "HMXRick";
                        break;
                    case "boomboom":
                    case "raiseyourweapon_noisia":
                    case "timeforcrime":
                        SongList.I.songs[i].author = "HMXJeff & HMXRick";
                        break;
                    case "eyeforaneye":
                    case "goatpolyphia":
                    case "illmerica":
                    case "funkycomputer":
                        SongList.I.songs[i].author = "Simon";
                        break;
                    case "loyal":
                        SongList.I.songs[i].author = "Simon & HMXRick";
                        break;
                    case "highhopes":
                    case "goodbyedearsorrows_ab42b2e6b0934471474875729b4f9934":
                    case "shatterme_30eb4181110577459bc89b8650d3386a":
                        SongList.I.songs[i].author = "aggrogahu";
                        break;
                    case "bigppwoo_0966cf748cb5f637e3b0f00feeda9d9a":
                        SongList.I.songs[i].author = "Sleepyhead";
                        break;
                    case "children-of-a-miracle_bc34b4da4eea98a2a2e7c28d378738e6":
                    case "get-jinxed_bd9d8a475804d6e2086fc3d2090ea9fb":
                    case "no-worries_b48e9121c4f412e2da920212ff375a45":
                        SongList.I.songs[i].author = "Fredrix";
                        break;
                    case "LegendsNeverDie_96f3da6e3455fc7b74535f4ad3171955":
                        SongList.I.songs[i].author = "CircuitLord";
                        break;
                    case "Camellia_The_King_of_Lions_f39276e8867fbfd9c0d9c1e99dc03052":
                        SongList.I.songs[i].author = "CriminalCannoli";
                        break;
                    case "weaponizedcelldwellershark_a0508d763c057c198291149233c4d150":
                        SongList.I.songs[i].author = "whattheshark";
                        break;
                    case "ainideaikoiwatsudzukuoctober_d674ca136c43e57ef82a92cb8f80da87":
                    case "sadmachine_a49b6978f4ab867057f8bb22bcf53580":
                        SongList.I.songs[i].author = "october";
                        break;
                    case "deviltrigger_728ff099c5d7d1f2ad0f724fb53b9b43":
                    case "onceagain_ProtoPip_0e8bb6d431dd2fdabb62a4d988c263eb":
                        SongList.I.songs[i].author = "ProtoPip";
                        break;
                }
            }
        }
    }
}