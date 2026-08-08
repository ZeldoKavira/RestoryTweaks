using System;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using Restory.Data.Equipment;
using Restory.Gameplay.Disassemble.StateMachine;
using Restory.Gameplay.Elements;
using Restory.Gameplay.Equipment;
using Restory.Gameplay.Soldering;

namespace RestoryTweaks
{
    public static class AutoOpenCleanerConfig
    {
        public static ConfigEntry<bool> Enabled;
        public static ConfigEntry<bool> OnlyForDeviceParts;
        public static ConfigEntry<bool> SelectTool;
        public static ConfigEntry<bool> PreferUltrasonicBath;
        public static ConfigEntry<bool> AutoStartUltrasonic;
        public static ConfigEntry<bool> AutoEmptyUltrasonic;

        public static void Init(ConfigFile cfg)
        {
            AutoEmptyUltrasonic = cfg.Bind("AutoOpenCleaner", "AutoEmptyUltrasonic", true,
                "When a cycle finishes, take the clean parts out of the bath and lay them back on "
                + "the bench, instead of fishing them out one at a time.");

            AutoStartUltrasonic = cfg.Bind("AutoOpenCleaner", "AutoStartUltrasonic", true,
                "Run the bath as soon as there's no reason to keep loading it - the basket is full, "
                + "or nothing left on the device would go in it. Parts needing solder don't count, "
                + "since they never go in the bath.");

            PreferUltrasonicBath = cfg.Bind("AutoOpenCleaner", "PreferUltrasonicBath", true,
                "If you own an ultrasonic bath, drop parts that only need cleaning straight into "
                + "it instead of opening the brush window. Parts that need soldering still go to "
                + "the brush window, since the bath can't resolder anything.");

            SelectTool = cfg.Bind("AutoOpenCleaner", "SelectTool", true,
                "Equip the tool the part actually needs: a brush while there's dirt or soot to "
                + "clear, the soldering iron once it's clean enough to resolder. Applies whenever "
                + "the cleaning window opens, however you got there.");

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

                // The bath removes dirt and soot but cannot resolder, so anything with solder points
                // to redo still needs the brush window and its iron. SolderPointsCount is only
                // non-zero when the game's own IsElementNeedsSoldering said so.
                if (work.SolderPointsCount == 0
                    && UltrasonicBath.TryInsert(__instance, selectedElement))
                {
                    Plugin.Log.LogInfo($"[AutoOpenCleaner] Put {Name(selectedElement)} in the ultrasonic bath.");
                    return;
                }

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

    // Picking the tool the part needs.
    //
    // The choice isn't ours to make: SolderingService.Init already applies the game's rule - any
    // sooty solder point puts it in cleaning mode, and only a fully cleaned element goes straight
    // to soldering mode. So we read InSolderingMode rather than re-deriving "is it dirty", and the
    // brush-before-iron ordering falls out for free.
    internal static class CleaningTools
    {
        private static CleaningToolSelectionService _service;
        private static FieldInfo _availableToolsField;
        private static bool _warnedNoIron;

        private static CleaningToolSelectionService Service
        {
            get
            {
                if (_service == null) _service = UnityEngine.Object.FindObjectOfType<CleaningToolSelectionService>();
                return _service;
            }
        }

        private static AvailableToolsTrackingService Tracking(CleaningToolSelectionService service)
        {
            // Read the service's own reference rather than searching the scene, so we can't end up
            // consulting a different tracker than the one it validates selections against.
            if (_availableToolsField == null)
                _availableToolsField = typeof(CleaningToolSelectionService)
                    .GetField("availableTools", BindingFlags.Instance | BindingFlags.NonPublic);

            var tracking = _availableToolsField != null
                ? _availableToolsField.GetValue(service) as AvailableToolsTrackingService
                : null;

            return tracking != null ? tracking : UnityEngine.Object.FindObjectOfType<AvailableToolsTrackingService>();
        }

        public static void SelectFor(bool soldering)
        {
            try
            {
                if (!AutoOpenCleanerConfig.SelectTool.Value) return;
                var service = Service;
                if (service == null) return;

                if (soldering) SelectSoldering(service);
                else SelectBrush(service);
            }
            catch (Exception e) { Plugin.Log.LogError($"[AutoOpenCleaner] tool switch: {e.Message}"); }
        }

        private static void SelectBrush(CleaningToolSelectionService service)
        {
            // Any brush will do the job, so leave a deliberate choice of a better one alone.
            if (service.CurrentlySelectedTool is CleaningToolInfo) return;

            if (!service.TryToSelectDefaultTool())
                Plugin.Log.LogInfo("[AutoOpenCleaner] No cleaning tool available to select.");
        }

        private static void SelectSoldering(CleaningToolSelectionService service)
        {
            if (service.CurrentlySelectedTool is SolderingToolInfo) return;

            var tracking = Tracking(service);
            if (tracking != null)
                foreach (var tool in tracking.AvailableTools)
                    if (tool is SolderingToolInfo iron && service.TryToSelectTool(iron))
                    {
                        _warnedNoIron = false;
                        return;
                    }

            // Not an error: you can reach a scorched board before owning an iron. Say it once
            // rather than every time the panel opens.
            if (_warnedNoIron) return;
            _warnedNoIron = true;
            Plugin.Log.LogInfo("[AutoOpenCleaner] This part needs soldering, but no soldering iron is available.");
        }
    }

    // Opening the cleaner: equip whatever the part needs first.
    [HarmonyPatch(typeof(CleaningDisassembleState), "Enter", new Type[] { typeof(ElementBase) })]
    public static class Patch_SelectToolOnCleanerOpen
    {
        private static FieldInfo _solderingService;

        // Runs after Enter, which matters twice over: InitSoldering has decided the mode by then,
        // and the state has already subscribed to OnToolSwitched - so our switch reaches
        // ElementCleaner.SetCleaningTool through the game's own handler.
        private static void Postfix(CleaningDisassembleState __instance)
        {
            try
            {
                if (_solderingService == null)
                {
                    _solderingService = typeof(CleaningDisassembleState)
                        .GetField("solderingService", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (_solderingService == null)
                    {
                        Plugin.Log.LogWarning("[AutoOpenCleaner] Couldn't read the soldering state; "
                                              + "leaving tool selection alone.");
                        return;
                    }
                }

                var soldering = _solderingService.GetValue(__instance) as SolderingService;
                CleaningTools.SelectFor(soldering != null && soldering.IsActive && soldering.InSolderingMode);
            }
            catch (Exception e) { Plugin.Log.LogError($"[AutoOpenCleaner] {e.Message}"); }
        }
    }

    // Brushing the soot off flips the session into soldering mode partway through, so swap to the
    // iron at that moment too - otherwise the tool would only ever be right at the start.
    [HarmonyPatch(typeof(SolderingService), "SwitchFromCleaningToSolderingMode")]
    public static class Patch_SwapToIronWhenSootCleared
    {
        private static void Postfix(SolderingService __instance)
        {
            // The method bails out early when it's already soldering or has lost its target; only
            // act on a switch that really happened.
            try
            {
                if (__instance == null || !__instance.InSolderingMode) return;
                CleaningTools.SelectFor(soldering: true);
            }
            catch (Exception e) { Plugin.Log.LogError($"[AutoOpenCleaner] {e.Message}"); }
        }
    }
}
