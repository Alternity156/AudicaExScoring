using MelonLoader;
using TMPro;
using UnityEngine;

namespace ExScoringMod
{
    public partial class ExScoring : MelonMod
    {
        public static GameObject CreateTextObject(string name, string text, Transform parent, int layer, TMP_Text original, RectTransform originalRt, Vector3 localPosition, Vector3 localScale, TextAlignmentOptions options = TextAlignmentOptions.Left)
        {
            GameObject obj = new GameObject(name);
            obj.transform.SetParent(parent, false);
            obj.layer = layer;

            TextMeshPro tmp = obj.AddComponent<TextMeshPro>();
            tmp.text = text;
            tmp.fontSize = original.fontSize;
            tmp.richText = true;
            tmp.font = original.font;
            tmp.fontStyle = original.fontStyle;
            tmp.color = original.color;
            tmp.alignment = options;

            RectTransform rt = obj.GetComponent<RectTransform>();
            rt.sizeDelta = originalRt.sizeDelta;
            rt.anchoredPosition = originalRt.anchoredPosition;
            rt.localScale = localScale;
            rt.localPosition = localPosition;

            return obj;
        }

        public static GameObject CreateTextObject(string name, string text, Transform parent, int layer, TextMeshPro original, RectTransform originalRt, Vector3 localPosition, Vector3 localScale, TextAlignmentOptions options = TextAlignmentOptions.Left)
        {
            GameObject obj = GameObject.Instantiate(original.gameObject);
            obj.name = name;
            obj.transform.SetParent(parent, false);
            obj.layer = layer;

            TextMeshPro tmp = obj.GetComponent<TextMeshPro>();
            tmp.text = text;
            tmp.richText = true;
            tmp.alignment = options;

            RectTransform rt = obj.GetComponent<RectTransform>();
            rt.sizeDelta = originalRt.sizeDelta;
            rt.anchoredPosition = originalRt.anchoredPosition;
            rt.localScale = localScale;
            rt.localPosition = localPosition;

            return obj;
        }
    }
}
