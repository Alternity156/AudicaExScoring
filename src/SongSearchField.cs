using System;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnhollowerRuntimeLib;   // Il2CppType
using UnhollowerBaseLib;      // TryCast

namespace ExScoringMod
{
    internal static class SongSearchField
    {
        private static GameObject field;

        // Tunable placement — adjust like the other *UISetup constants.
        private static Vector3 fieldLocalPos = new Vector3(-0.5f, 11.5f, 0f);
        private static Vector3 fieldLocalScale = new Vector3(0.9f, 0.9f, 0.9f);

        public static void CreateField()
        {
            if (field != null) return; // Unity-null: recreates after a scene change

            OptionsMenu menu = SongSearchScreen.primaryMenu;
            if (menu == null)
            {
                foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<OptionsMenu>()))
                { menu = o.TryCast<OptionsMenu>(); if (menu != null) break; }
            }
            if (menu == null || menu.textEntryButtonPrefab == null) return;

            var parent = GameObject.Find("menu/ShellPage_Song/page/ShellPanel_Center");
            if (parent == null) return;

            field = GameObject.Instantiate(menu.textEntryButtonPrefab.gameObject, parent.transform);
            field.name = "ExScoring_SearchField";

            // Strip the OptionsMenuButton logic (it expects AddButton-time init) and the Localizer
            var omb = field.GetComponent<OptionsMenuButton>();
            if (omb != null) GameObject.Destroy(omb);
            var loc = field.GetComponentInChildren<Localizer>();
            if (loc != null) GameObject.Destroy(loc);

            // Hide the checkbox + hover-help bits so it reads as a plain entry field
            HideChild(field.transform, "checkmark");
            HideChild(field.transform, "emptycheckbox");
            HideChild(field.transform, "OptionsHoverHelp");

            // Drive the visible text through the label TMP
            SongSearch.liveText = field.GetComponentInChildren<TextMeshPro>();

            // Shooting the field opens the search keyboard
            var gb = field.GetComponentInChildren<GunButton>();
            if (gb != null)
            {
                gb.destroyOnShot = false;
                gb.disableOnShot = false;
                gb.doMeshExplosion = false;
                gb.doParticles = false;
                gb.onHitEvent = new UnityEvent();
                gb.onHitEvent.AddListener(new Action(() => { SearchKeyboard.Show(); }));
            }

            field.transform.localPosition = fieldLocalPos;
            field.transform.localRotation = Quaternion.identity;
            field.transform.localScale = fieldLocalScale;
            field.SetActive(true);

            SongSearch.UpdateLiveText(); // show the placeholder
        }

        private static void HideChild(Transform root, string name)
        {
            for (int i = 0; i < root.childCount; i++)
            {
                if (root.GetChild(i).name == name)
                { root.GetChild(i).gameObject.SetActive(false); return; }
            }
        }
    }
}