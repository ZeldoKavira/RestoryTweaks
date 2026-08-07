using System;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using Restory.Gameplay.Disassemble.StateMachine;
using Restory.Gameplay.Elements;
using Restory.Gameplay.Equipment;

namespace RestoryTweaks
{
    public static class AutoOpenCleanerConfig
    {
        public static ConfigEntry<bool> Enabled;
        public static ConfigEntry<bool> OnlyForDeviceParts;

        public static void Init(ConfigFile cfg)
        {
            Enabled = cfg.Bind("AutoOpenCleaner", "Enabled", true,
                "Picking up a part that needs cleaning or soldering opens the cleaning window "
                + "straight away, instead of you having to drag it onto the cleaner.");

            // Off by default: a part bought dirty from the shop needs cleaning before it can go in
            // just as much as one that came out of the device, so restricting by origin would only
            // surprise you. The option is here for anyone who wants the narrower behaviour.
            OnlyForDeviceParts = cfg.Bind("AutoOpenCleaner", "OnlyForDeviceParts", false,
                "Only do this for parts belonging to the device on the bench, leaving parts picked "
                + "up from storage alone.");
        }
    }

    // Picking a part up opens the cleaner for it, when it needs cleaning or soldering.
    //
    // Rather than reproduce the drop sequence, this triggers the game's own TrySendElementToCleaner
    // - the method that runs when you release a part over the cleaner. It sets up the panel, drops
    // the drag and transitions the state machine in the right order, and any of that we got subtly
    // wrong would show up as a half-entered cleaning mode.
    //
    // The one input it needs is isOverCleaner, which is normally set by the hover raycast in
    // OnUpdate; since we fire before the player has moved the mouse anywhere, we set it ourselves.
    [HarmonyPatch(typeof(DraggingDisassembleState), "Enter", new Type[] { typeof(ElementBase) })]
    public static class Patch_OpenCleanerOnPickup
    {
        private static FieldInfo _isOverCleaner;
        private static FieldInfo _selectedElement;
        private static FieldInfo _elementCleaner;
        private static MethodInfo _trySend;
        private static bool _broken;

        private static bool Resolve()
        {
            if (_broken) return false;
            if (_trySend != null) return true;

            const BindingFlags Priv = BindingFlags.Instance | BindingFlags.NonPublic;
            var t = typeof(DraggingDisassembleState);

            _isOverCleaner = t.GetField("isOverCleaner", Priv);
            _selectedElement = t.GetField("selectedElement", Priv);
            _elementCleaner = t.GetField("elementCleaner", Priv);
            _trySend = t.GetMethod("TrySendElementToCleaner", Priv);

            if (_isOverCleaner == null || _selectedElement == null || _elementCleaner == null || _trySend == null)
            {
                _broken = true;
                Plugin.Log.LogWarning("[AutoOpenCleaner] The game's drag-to-cleaner path has changed; "
                                      + "leaving pickup alone.");
                return false;
            }
            return true;
        }

        private static void Postfix(DraggingDisassembleState __instance, ElementBase selectedElement)
        {
            try
            {
                if (!AutoOpenCleanerConfig.Enabled.Value) return;
                if (!Resolve()) return;

                // Enter() has several early exits that never start a drag - no device on the bench,
                // the button already released. In those the state has moved on and its
                // selectedElement was never assigned, so anything we did here would act on nothing.
                var dragging = _selectedElement.GetValue(__instance) as ElementBase;
                if (dragging == null || !ReferenceEquals(dragging, selectedElement)) return;

                var cleaner = _elementCleaner.GetValue(__instance) as ElementCleaner;
                if (cleaner == null) return;

                // Non-null exactly when the part needs cleaning OR soldering - the game computes it
                // at the top of Enter, checking both, so one test covers both cases.
                var work = cleaner.DraggingElementInitialCleaningData;
                if (work == null) return;

                if (AutoOpenCleanerConfig.OnlyForDeviceParts.Value && !BelongsToPlacedDevice(selectedElement))
                    return;

                _isOverCleaner.SetValue(__instance, true);

                // Safe to transition from inside Enter: the state machine assigns ActiveState before
                // calling Enter, so the nested change exits this state cleanly rather than being
                // overwritten once we return.
                bool sent = _trySend.Invoke(__instance, null) is bool b && b;

                if (!sent)
                {
                    _isOverCleaner.SetValue(__instance, false);
                    return;
                }

                Plugin.Log.LogInfo($"[AutoOpenCleaner] Opened the cleaner for {Name(selectedElement)}.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[AutoOpenCleaner] {e.Message}");
            }
        }

        // The part came out of the device currently on the bench, rather than out of storage. This
        // is the same comparison the game uses to decide whether to show its "drag me to the
        // cleaner" indicator.
        private static bool BelongsToPlacedDevice(ElementBase element)
        {
            try
            {
                var device = AutoAssemble.PlacedDevice();
                if (device == null || element.Info == null) return false;
                return device.Info == element.Info.SourceDevice as Restory.Data.Devices.DeviceInfo;
            }
            catch { return false; }
        }

        private static string Name(ElementBase element)
        {
            try { return element != null && element.Info != null ? element.Info.name : "a part"; }
            catch { return "a part"; }
        }
    }
}
