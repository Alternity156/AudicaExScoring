using System;
using MelonLoader;
using TMPro;
using UnityEngine;
using UnityEngine.Events;

namespace ExScoringMod
{
    public partial class ExScoring : MelonMod
    {
        private static GameObject favoriteButton;
        private static GameObject favoriteIndicator;
        private static bool favoriteButtonSetup = false;

        /// <summary>
        /// Creates the favorite toggle button on the launch panel, below the practice mode button.
        /// Uses the same SelectedIndicator system as the difficulty buttons.
        /// </summary>
        public static void SetupFavoriteButton()
        {
            if (favoriteButtonSetup) return;
            favoriteButtonSetup = true;

            // Clone the practice toggle to create the favorite button
            GameObject practiceToggle = GameObject.Find("menu/ShellPage_Launch/page/ShellPanel_Center/NoFailPracticeToggle/PracticeToggle");
            if (practiceToggle == null) return;

            Transform parent = practiceToggle.transform.parent;

            favoriteButton = GameObject.Instantiate(practiceToggle, parent);
            favoriteButton.name = "FavoriteToggle";

            // Position below the practice button
            favoriteButton.transform.localPosition = new Vector3(5f, 15.5f, 0f);
            favoriteButton.transform.localScale = new Vector3(0.75f, 0.75f, 0.75f);

            // Remove the localizer so we can set custom text
            Localizer localizer = favoriteButton.GetComponentInChildren<Localizer>();
            if (localizer != null) GameObject.Destroy(localizer);

            // Set the label
            TextMeshPro label = favoriteButton.GetComponentInChildren<TextMeshPro>();
            if (label != null)
                label.text = "Favorite";

            // Set up the gun button behavior
            GunButton gunButton = favoriteButton.GetComponentInChildren<GunButton>();
            if (gunButton != null)
            {
                gunButton.destroyOnShot = false;
                gunButton.disableOnShot = false;
                gunButton.doMeshExplosion = false;
                gunButton.doParticles = false;
                gunButton.onHitEvent = new UnityEvent();
                gunButton.onHitEvent.AddListener(new Action(() => { OnFavoriteButtonShot(); }));
            }

            // Create the selected indicator (same approach as difficulty buttons)
            GameObject indicatorSource = GameObject.Find("menu/ShellPage_Launch/page/ShellPanel_Center/play/SelectedIndicator");
            if (indicatorSource != null)
            {
                // Remove any existing indicator from the clone
                Transform existing = favoriteButton.transform.Find("SelectedIndicator");
                if (existing != null) GameObject.Destroy(existing.gameObject);

                favoriteIndicator = GameObject.Instantiate(indicatorSource, favoriteButton.transform);
                favoriteIndicator.name = "SelectedIndicator";
                favoriteIndicator.transform.localPosition = new Vector3(0f, 0f, -0.005f);
                favoriteIndicator.transform.localRotation = Quaternion.identity;
                favoriteIndicator.transform.localScale = new Vector3(0.675f, 0.75f, 1f);

                MeshRenderer renderer = favoriteIndicator.GetComponent<MeshRenderer>();
                if (renderer != null)
                    renderer.material.color = new Color(1f, 1f, 1f, 1f);

                favoriteIndicator.SetActive(false);
            }
        }

        private static void OnFavoriteButtonShot()
        {
            if (selectedSong == null) return;

            FilterPanel.AddFavorite(selectedSong);
            UpdateFavoriteIndicator();
        }

        /// <summary>
        /// Updates the favorite indicator to reflect whether the currently selected song is favorited.
        /// Call this whenever the selected song changes.
        /// </summary>
        public static void UpdateFavoriteIndicator()
        {
            if (favoriteIndicator == null) return;

            bool isFav = selectedSong != null && FilterPanel.IsFavorite(selectedSong);
            favoriteIndicator.SetActive(isFav);
        }
    }
}