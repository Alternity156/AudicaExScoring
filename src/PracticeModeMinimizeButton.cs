using System;
using System.IO;
using System.IO.Compression;
using Harmony;
using MelonLoader;
using TMPro;
using UnhollowerBaseLib;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.TextCore;

namespace ExScoringMod
{
    public partial class ExScoring : MelonMod
    {
        // ── Sprite atlas (collapse/expand chevrons) ──────────────────────
        private static bool practiceIconsInitialized = false;
        private static TMP_SpriteAsset practiceIconsSpriteAsset;

        private static readonly string[] practiceIconNames = new string[]
        {
            "collapse",
            "expand"
        };

        // Zlib-compressed raw RGBA pixel data (64x64, bottom-up), base64 encoded
        private static readonly string[] practiceIconBase64 = new string[]
        {
            // collapse (chevron pointing down)
            "eNrtmMERwzAMw7z/0u4/j17rSDIpAQuYMJlrmrUAAAAAoIL9YLL7pDvYPzDZHf++dzDZfx+A/1z3Tncw2X8HgP9cd+c7mOy/E+j6rt/N/99Ou23gxKWL/2" +
            "mXXTbwxsHd/22H7huIyO7qH9Wd6wYiM7v5R3fmtoGMrC7+WV25bCAzo7p/dkfqG6jIpupf1Y3qBiozqflXd6K2gRtZVPxvdaGygZsZbvvf7sDl/I77c3v+" +
            "InNM//3B3/P9C/+6M9X+f1Weqfb9ofJMte9vCwAAAAC+8gGLa8vd",

            // expand (chevron pointing up)
            "eNrtmUEOhDAMA/P/T5f7SiC2TYLteF6QyYhSQYQxxhhjjHli/TBpnnUDinvlPOsFCO4Vs6yXoLhnz7P+wP6z/b86g1HOABR/pd0jNWDw9/Ondf6i3UG6W6" +
            "C9f7t7oLl3NkFs39kF1b2jDXL7jj7o7pWNGNpXdmJxr2jF1L6iF5t7ZjPG9pndWN0z2jG3z+jH7n7SUKH9SUcV952WSu13eqq57/wzUPPP3kEQMt0/awdB" +
            "zHT/0x2EANP9d3cQQkz3V73n2l/zm679v9lBDGCy+90OwhhjjDHGtHABXe6r/Q==",
        };

        private static readonly int practiceIconSize = 64;
        private static readonly int practiceAtlasColumns = 2;

        private static byte[] DecompressZlibPracticeIcons(byte[] compressed)
        {
            // Skip zlib header (2 bytes) to get raw deflate stream
            using (var input = new MemoryStream(compressed, 2, compressed.Length - 2))
            using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                deflate.CopyTo(output);
                return output.ToArray();
            }
        }

        public static void InitializePracticeIcons()
        {
            if (practiceIconsInitialized) return;

            try
            {
                int iconCount = practiceIconNames.Length;
                int atlasRows = (iconCount + practiceAtlasColumns - 1) / practiceAtlasColumns;
                int atlasWidth = practiceAtlasColumns * practiceIconSize;
                int atlasHeight = atlasRows * practiceIconSize;

                Texture2D atlas = new Texture2D(atlasWidth, atlasHeight, TextureFormat.RGBA32, false);
                atlas.name = "PracticeIconsAtlas";
                atlas.hideFlags = HideFlags.DontUnloadUnusedAsset;

                Il2CppStructArray<Color> clearPixels = new Il2CppStructArray<Color>(atlasWidth * atlasHeight);
                Color transparent = new Color(0, 0, 0, 0);
                for (int i = 0; i < clearPixels.Length; i++)
                    clearPixels[i] = transparent;
                atlas.SetPixels(clearPixels);

                for (int i = 0; i < iconCount; i++)
                {
                    byte[] compressed = Convert.FromBase64String(practiceIconBase64[i]);
                    byte[] rawRGBA = DecompressZlibPracticeIcons(compressed);

                    int col = i % practiceAtlasColumns;
                    int row = atlasRows - 1 - (i / practiceAtlasColumns);

                    Il2CppStructArray<Color> iconPixels = new Il2CppStructArray<Color>(practiceIconSize * practiceIconSize);
                    for (int j = 0; j < practiceIconSize * practiceIconSize; j++)
                    {
                        int idx = j * 4;
                        iconPixels[j] = new Color(
                            rawRGBA[idx] / 255f,
                            rawRGBA[idx + 1] / 255f,
                            rawRGBA[idx + 2] / 255f,
                            rawRGBA[idx + 3] / 255f
                        );
                    }

                    atlas.SetPixels(col * practiceIconSize, row * practiceIconSize, practiceIconSize, practiceIconSize, iconPixels);
                }

                atlas.Apply();

                Material mat = new Material(Shader.Find("Sprites/Default"));
                mat.name = "PracticeIconsMaterial";
                mat.hideFlags = HideFlags.DontUnloadUnusedAsset;
                mat.mainTexture = atlas;

                practiceIconsSpriteAsset = ScriptableObject.CreateInstance<TMP_SpriteAsset>();
                practiceIconsSpriteAsset.name = "PracticeIcons";
                practiceIconsSpriteAsset.hideFlags = HideFlags.DontUnloadUnusedAsset;
                practiceIconsSpriteAsset.spriteSheet = atlas;
                practiceIconsSpriteAsset.material = mat;
                practiceIconsSpriteAsset.hashCode = TMP_TextUtilities.GetSimpleHashCode("PracticeIcons");
                practiceIconsSpriteAsset.materialHashCode = TMP_TextUtilities.GetSimpleHashCode("PracticeIconsMaterial");

                var spriteInfoList = new Il2CppSystem.Collections.Generic.List<TMP_Sprite>();

                for (int i = 0; i < iconCount; i++)
                {
                    int col = i % practiceAtlasColumns;
                    int row = atlasRows - 1 - (i / practiceAtlasColumns);

                    int x = col * practiceIconSize;
                    int y = row * practiceIconSize;

                    TMP_Sprite sprite = new TMP_Sprite();
                    sprite.name = practiceIconNames[i];
                    sprite.id = i;
                    sprite.x = x;
                    sprite.y = y;
                    sprite.width = practiceIconSize;
                    sprite.height = practiceIconSize;
                    sprite.xOffset = 0;
                    sprite.yOffset = practiceIconSize * 0.9f;
                    sprite.xAdvance = practiceIconSize;
                    sprite.scale = 1f;
                    sprite.hashCode = TMP_TextUtilities.GetSimpleHashCode(practiceIconNames[i]);
                    sprite.unicode = 0xFE10 + i; // separate private-use range from TargetIcons' 0xFE00+

                    spriteInfoList.Add(sprite);
                }

                practiceIconsSpriteAsset.spriteInfoList = spriteInfoList;
                practiceIconsSpriteAsset.UpdateLookupTables();

                MaterialReferenceManager.AddSpriteAsset(practiceIconsSpriteAsset.hashCode, practiceIconsSpriteAsset);

                var defaultAsset = TMP_Settings.instance.m_defaultSpriteAsset;
                if (defaultAsset != null)
                {
                    if (defaultAsset.fallbackSpriteAssets == null)
                    {
                        defaultAsset.fallbackSpriteAssets = new Il2CppSystem.Collections.Generic.List<TMP_SpriteAsset>();
                    }

                    bool alreadyRegistered = false;
                    for (int i = 0; i < defaultAsset.fallbackSpriteAssets.Count; i++)
                    {
                        if (defaultAsset.fallbackSpriteAssets[i].name == "PracticeIcons")
                        {
                            alreadyRegistered = true;
                            break;
                        }
                    }

                    if (!alreadyRegistered)
                    {
                        defaultAsset.fallbackSpriteAssets.Add(practiceIconsSpriteAsset);
                        MelonLogger.Log("PracticeIcons sprite asset registered as fallback");
                    }
                }

                practiceIconsInitialized = true;
                MelonLogger.Log($"PracticeIcons initialized: {iconCount} icons in {atlasWidth}x{atlasHeight} atlas");
            }
            catch (Exception ex)
            {
                MelonLogger.Log($"Failed to initialize PracticeIcons: {ex.Message}");
                MelonLogger.Log(ex.StackTrace);
            }
        }

        private static string GetPracticeIconTag(string iconName)
        {
            return $"<sprite=\"PracticeIcons\" name=\"{iconName}\" tint=1>";
        }

        // ── Minimize button ───────────────────────────────────────────────
        private static GameObject practiceModePanel;
        private static GameObject practiceMinimizeButtonObj;
        private static TextMeshPro practiceMinimizeButtonText;

        private static Vector3 practiceMinimizeLocalPosition = new Vector3(0.75f, 6.335f, 1.16f);
        private static Quaternion practiceMinimizeLocalRotation = Quaternion.Euler(45f, 0f, 0f);
        private static Vector3 practiceMinimizeLocalScale = new Vector3(0.1f, 0.1f, 0.1f);

        [HarmonyPatch(typeof(PracticeMode), "Awake")]
        public static class PracticeModeAwakePatch
        {
            public static void Postfix(PracticeMode __instance)
            {
                if (!Config.PracticeModeMinimizeButtonEnabled)
                {
                    return;
                }

                practiceModePanel = __instance.gameObject.transform.GetChild(0).gameObject;
                CreatePracticeModeMinimizeButton(__instance.gameObject);
            }
        }

        private static void CreatePracticeModeMinimizeButton(GameObject practiceModeObj)
        {
            // Avoid creating a duplicate if one is already parented here
            Transform existing = practiceModeObj.transform.Find("PracticeModeMinimizeButton");
            if (existing != null)
            {
                practiceMinimizeButtonObj = existing.gameObject;
                practiceMinimizeButtonText = practiceMinimizeButtonObj.GetComponentInChildren<TextMeshPro>();
                return;
            }

            InitializePracticeIcons();

            string name = "InGameUI/ShellPage_PracticeModeOver/page/ShellPanel_Center/exit";
            GameObject refButton = GameObject.Find(name);
            if (refButton == null)
            {
                MelonLogger.Log("PracticeModeMinimizeButton: could not find reference button, skipping");
                return;
            }

            GameObject button = GameObject.Instantiate(refButton);
            button.name = "PracticeModeMinimizeButton";
            button.transform.SetParent(practiceModeObj.transform, false);
            button.transform.localPosition = practiceMinimizeLocalPosition;
            button.transform.localRotation = practiceMinimizeLocalRotation;
            button.transform.localScale = practiceMinimizeLocalScale;

            Localizer localizer = button.GetComponentInChildren<Localizer>();
            if (localizer != null) GameObject.Destroy(localizer);

            TextMeshPro label = button.GetComponentInChildren<TextMeshPro>();
            if (label != null) label.text = GetPracticeIconTag("collapse");

            GunButton gunButton = button.GetComponentInChildren<GunButton>();
            if (gunButton != null)
            {
                gunButton.destroyOnShot = false;
                gunButton.disableOnShot = false;
                gunButton.doMeshExplosion = false;
                gunButton.doParticles = false;
                gunButton.onHitEvent = new UnityEvent();
                gunButton.onHitEvent.AddListener(new Action(() => { OnPracticeModeMinimizeButtonShot(); }));
            }

            practiceMinimizeButtonObj = button;
            practiceMinimizeButtonText = label;
        }

        private static void OnPracticeModeMinimizeButtonShot()
        {
            if (practiceModePanel == null) return;

            bool minimizing = practiceModePanel.activeSelf;
            practiceModePanel.SetActive(!minimizing);

            if (practiceMinimizeButtonText != null)
                practiceMinimizeButtonText.text = GetPracticeIconTag(minimizing ? "expand" : "collapse");
        }
    }
}