using System;
using Restory.Data.Elements;
using Restory.Data.Localization;
using Restory.Gameplay.UserInterface;
using UnityEngine;

namespace RestoryTweaks
{
    // On-screen messages, using the game's own warning banner.
    //
    // GUI_GameWarningDialogue is a self-dismissing toast: Show() sets the text and plays a tween,
    // and GameWarningService deactivates the object again when the tween finishes. So this only has
    // to do what the service does - switch it on and call Show - and the cleanup takes care of
    // itself. Reusing it also means our messages look like the game's rather than a second style.
    internal static class Toast
    {
        private static GUI_GameWarningDialogue _dialogue;

        private static GUI_GameWarningDialogue Dialogue
        {
            get
            {
                if (_dialogue != null) return _dialogue;

                // The banner sits inactive between uses, so an ordinary FindObjectOfType never sees
                // it. FindObjectsOfTypeAll does, but also returns prefab assets - hence the scene
                // check to be sure we light up the one that's actually on screen.
                foreach (var candidate in Resources.FindObjectsOfTypeAll<GUI_GameWarningDialogue>())
                {
                    if (candidate == null || !candidate.gameObject.scene.IsValid()) continue;
                    _dialogue = candidate;
                    break;
                }
                return _dialogue;
            }
        }

        private static LocalizationSystem _localization;

        // The readable name of a part, for messages that are shown rather than logged. An
        // ElementInfo's asset name is fine in a log but reads as debris on screen
        // ("PSP_1000_Screw - ElementInfo"), and the game already has the translated name.
        public static string NameOf(ElementInfo element)
        {
            try
            {
                if (element == null) return "part";

                if (_localization == null)
                    _localization = UnityEngine.Object.FindObjectOfType<LocalizationSystem>();

                if (_localization != null && !string.IsNullOrEmpty(element.NameLocalizationKey))
                {
                    string translated;
                    if (_localization.TryGetTranslation(element.NameLocalizationKey, out translated)
                        && !string.IsNullOrEmpty(translated))
                        return translated;
                }

                return element.name;
            }
            catch { return "part"; }
        }

        public static void Show(string message)
        {
            try
            {
                var dialogue = Dialogue;
                if (dialogue == null) return;      // no banner in this scene; the log still has it

                dialogue.gameObject.SetActive(true);
                dialogue.Show(message);
            }
            catch (Exception e) { Plugin.Log.LogError($"[Toast] {e.Message}"); }
        }
    }
}
