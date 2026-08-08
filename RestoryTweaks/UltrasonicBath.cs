using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using HarmonyLib;
using Restory.Data.Elements.Condition;
using Restory.Gameplay.Devices;
using Restory.Gameplay.Disassemble.StateMachine;
using Restory.Gameplay.Elements;
using Restory.Gameplay.Equipment;
using Restory.Gameplay.Equipment.Ultrasonic;
using Restory.Gameplay.Equipment.Ultrasonic.States;

namespace RestoryTweaks
{
    // Dropping a part straight into the ultrasonic bath on pickup.
    //
    // Both halves of the game's hover-then-release flow are needed, in order. TryFitElementToSonicBath
    // is what targets the bath's element fitter at this part - and TryInsertElement refuses outright
    // ("Current targetElement is not equal to inserted element") unless that has happened. It also
    // pulls the drawer open for us, so there's no need to drive that separately.
    internal static class UltrasonicBath
    {
        private static UltrasonicService _service;
        private static FieldInfo _bathField;
        private static FieldInfo _canInsertField;
        private static FieldInfo _deviceServiceField;
        private static FieldInfo _stateMachineField;

        private static UltrasonicService Service
        {
            get
            {
                if (_service == null) _service = UnityEngine.Object.FindObjectOfType<UltrasonicService>();
                return _service;
            }
        }

        private static SonicBath Bath(UltrasonicService service)
        {
            // The service's own reference, so we can't end up inspecting a different bath than the
            // one it would insert into.
            if (_bathField == null)
                _bathField = typeof(UltrasonicService).GetField("sonicBath",
                    BindingFlags.Instance | BindingFlags.NonPublic);

            return _bathField != null ? _bathField.GetValue(service) as SonicBath : null;
        }

        public static bool TryInsert(DraggingDisassembleState drag, ElementBase element)
        {
            try
            {
                if (!AutoOpenCleanerConfig.PreferUltrasonicBath.Value) return false;

                var service = Service;
                if (service == null) return false;

                var bath = Bath(service);
                if (bath == null || bath.ActiveTool == null) return false;   // no bath owned

                // The service worked out whether this part can go in when the drag started - bath
                // present and not full, not running, not a quest item, not broken. Reusing that
                // answer avoids re-deriving the rules, and avoids the warning popups that
                // TryInsertElementToSonicBath fires on refusal: here a refusal isn't the player
                // doing something wrong, it just means fall back to the brush window.
                if (!CanInsert(service)) return false;

                // Any point works: TryFitElement clamps it into the basket's placement area, so the
                // basket's own position lands the part in the middle of it.
                var fitter = bath.ElementFitter;
                if (fitter == null) return false;

                if (!service.TryFitElementToSonicBath(element, fitter.transform.position)) return false;

                if (!service.TryInsertElementToSonicBath(element))
                {
                    // Fitting already rescaled and moved the part; undo that so it isn't left
                    // shrunken and floating in the basket when we fall back to the brush.
                    fitter.ResetElement();
                    return false;
                }

                LeaveDragState(drag);
                MaybeStart(service, bath);
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[AutoOpenCleaner] ultrasonic: {e.Message}");
                return false;
            }
        }

        // Run a cycle once there's nothing more worth waiting for: the basket is full, or nothing
        // left on this device would go in the bath anyway.
        private static void MaybeStart(UltrasonicService service, SonicBath bath)
        {
            try
            {
                if (!AutoOpenCleanerConfig.AutoStartUltrasonic.Value) return;

                string why;
                if (bath.IsFull) why = "the basket is full";
                else if (!MorePartsForTheBath(service, bath)) why = "nothing else needs cleaning";
                else return;

                if (!Start(service, bath)) return;
                Plugin.Log.LogInfo($"[AutoOpenCleaner] Started the ultrasonic bath - {why}.");
                Toast.Show("Ultrasonic bath started");
            }
            catch (Exception e) { Plugin.Log.LogError($"[AutoOpenCleaner] auto-start: {e.Message}"); }
        }

        // Exactly what IdleUltrasonicState does when you press the button, in the same order: shut
        // the cover on the contents, start the countdown, and only then switch state - entering the
        // launched state without a running timer is an error case the game logs and backs out of.
        //
        // TurnOn first, so the switch and lamp match. The button's own tween completing afterwards
        // is harmless: the launched state only reacts to a click that turns the button OFF.
        private static bool Start(UltrasonicService service, SonicBath bath)
        {
            var button = bath.ToggleButton;
            var timer = bath.Timer;
            var cover = bath.Cover;
            if (button == null || timer == null || cover == null) return false;

            if (timer.IsCountdown) return false;          // already running

            bool wasOn = button.IsOn;
            if (!wasOn) button.TurnOn();

            if (cover.IsOpen)
            {
                bath.FreezeInsertedElements();
                cover.Close();
            }

            if (!timer.TryStartCountdown(bath.CleaningDuration))
            {
                // Don't leave the switch showing "on" over a bath that isn't running.
                if (!wasOn) button.TurnOff();
                return false;
            }

            var states = StateMachine(service);
            if (states != null) states.EnterLaunchedState();
            return true;
        }

        // Anything left on the device that would end up in this bath?
        //
        // Parts still bolted in count: you haven't taken them out yet, and running a cycle now
        // would mean waiting through a second one for them. Parts that need soldering don't count -
        // those go to the brush window, so waiting for them would mean never starting.
        private static bool MorePartsForTheBath(UltrasonicService service, SonicBath bath)
        {
            var device = AutoAssemble.PlacedDevice();
            if (device == null) return false;

            var cleaner = Cleaner(service);

            foreach (var element in AutoAssemble.EveryPart(device))
            {
                if (element == null || Holds(element)) continue;

                var data = element.ConditionHandler != null ? element.ConditionHandler.ElementData : null;
                if (data == null || !(data.Condition is DirtyElementCondition)) continue;

                if (cleaner != null && cleaner.IsElementNeedsSoldering(element, out _, out _)) continue;

                return true;
            }
            return false;
        }

        // Take everything out when the cycle finishes and put it back on the bench.
        //
        // Retrieval is the game's own TryRetrieveElementFromSonicBath, which restores the part's
        // original scale, updates the occupancy indicator and registers it on the work surface. What
        // it doesn't do is decide where the part goes - normally you're dragging it, so your cursor
        // answers that. Here nothing is, so each part is run through the same placement finder the
        // game uses when dropping items out of storage, or it would be left hovering in the basket.
        public static void EmptyAfterCleaning()
        {
            try
            {
                if (!AutoOpenCleanerConfig.AutoEmptyUltrasonic.Value) return;

                var service = Service;
                if (service == null) return;

                var bath = Bath(service);
                if (bath == null || bath.InsertedElements == null) return;

                // Snapshot first: retrieving mutates the collection being iterated.
                var contents = new List<ElementBase>();
                foreach (var pair in bath.InsertedElements) contents.Add(pair.Key);
                if (contents.Count == 0) return;

                bath.TryPull();          // open the drawer, as you would before reaching in

                var placement = Placement();
                int taken = 0;

                foreach (var element in contents)
                {
                    if (element == null) continue;
                    if (!service.TryRetrieveElementFromSonicBath(element)) continue;
                    PlaceOnBench(placement, element);
                    taken++;
                }

                if (taken == 0) return;

                Plugin.Log.LogInfo($"[AutoOpenCleaner] Took {taken} clean part(s) out of the ultrasonic bath.");
                Toast.Show(taken == 1 ? "1 part out of the bath" : $"{taken} parts out of the bath");
            }
            catch (Exception e) { Plugin.Log.LogError($"[AutoOpenCleaner] emptying the bath: {e.Message}"); }
        }

        private static void PlaceOnBench(ElementPlacementController placement, ElementBase element)
        {
            try
            {
                // Frozen and rescaled while it soaked; put it back to behaving like a loose part.
                if (element.BehaviorSwitcher != null) element.BehaviorSwitcher.SwitchToPlacedBehavior();

                if (placement == null) return;   // already on the surface, just not repositioned

                placement.SetTargetElement(element);
                if (placement.TryFindAvailablePlacementPosition(Quaternion.identity))
                    placement.SetPlacementPosition();
                placement.Clear();
            }
            catch (Exception e) { Plugin.Log.LogError($"[AutoOpenCleaner] placing a part: {e.Message}"); }
        }

        private static FieldInfo _placementField;

        // The placement controller isn't a component, so it can't be searched for. The dragging
        // state holds the same instance, and the state machine hands out its states publicly.
        private static ElementPlacementController Placement()
        {
            try
            {
                var states = UnityEngine.Object.FindObjectOfType<DisassembleStateMachine>();
                if (states == null) return null;

                var drag = states.GetState<DraggingDisassembleState>();
                if (drag == null) return null;

                if (_placementField == null)
                    _placementField = typeof(DraggingDisassembleState).GetField("elementPlacementController",
                        BindingFlags.Instance | BindingFlags.NonPublic);

                return _placementField != null
                    ? _placementField.GetValue(drag) as ElementPlacementController : null;
            }
            catch { return null; }
        }

        // Is this part currently sitting in the bath?
        //
        // Assembly has to leave those alone. A part in the basket is still an ordinary element in
        // the scene, so it looks loose and fittable - but attaching it would pull it out behind the
        // bath's back, leaving the occupancy indicator wrong and the part still shrunk to the scale
        // the fitter gave it.
        public static bool Holds(ElementBase element)
        {
            try
            {
                if (element == null) return false;

                var service = Service;
                if (service == null) return false;

                var bath = Bath(service);
                var inserted = bath != null ? bath.InsertedElements : null;
                if (inserted == null) return false;

                foreach (var pair in inserted)
                    if (ReferenceEquals(pair.Key, element)) return true;

                return false;
            }
            catch { return false; }
        }

        private static FieldInfo _stateMachineOfService;
        private static FieldInfo _cleanerField;

        private static UltrasonicStateMachine StateMachine(UltrasonicService service)
        {
            if (_stateMachineOfService == null)
                _stateMachineOfService = typeof(UltrasonicService).GetField("ultrasonicStateMachine",
                    BindingFlags.Instance | BindingFlags.NonPublic);

            return _stateMachineOfService != null
                ? _stateMachineOfService.GetValue(service) as UltrasonicStateMachine : null;
        }

        private static ElementCleaner Cleaner(UltrasonicService service)
        {
            if (_cleanerField == null)
                _cleanerField = typeof(UltrasonicService).GetField("elementCleaner",
                    BindingFlags.Instance | BindingFlags.NonPublic);

            return _cleanerField != null ? _cleanerField.GetValue(service) as ElementCleaner : null;
        }

        private static bool CanInsert(UltrasonicService service)
        {
            if (_canInsertField == null)
                _canInsertField = typeof(UltrasonicService).GetField(
                    "isDraggingElementCanBeInsertedToSonicBath",
                    BindingFlags.Instance | BindingFlags.NonPublic);

            if (_canInsertField == null) return false;
            return _canInsertField.GetValue(service) is bool b && b;
        }

        // What the game does after a part goes in on release: the drag is over, so move to the
        // state that follows it. Leaving the machine in the dragging state would keep a part in
        // hand that is no longer on the bench.
        private static void LeaveDragState(DraggingDisassembleState drag)
        {
            const BindingFlags Priv = BindingFlags.Instance | BindingFlags.NonPublic;

            if (_deviceServiceField == null)
                _deviceServiceField = typeof(DraggingDisassembleState).GetField("deviceService", Priv);
            if (_stateMachineField == null)
                _stateMachineField = typeof(DraggingDisassembleState).GetField("stateMachine", Priv);

            var states = _stateMachineField != null
                ? _stateMachineField.GetValue(drag) as DisassembleStateMachine : null;
            if (states == null) return;

            var devices = _deviceServiceField != null
                ? _deviceServiceField.GetValue(drag) as DeviceService : null;

            // Taking the last part out empties the device, and the game tears the container down
            // rather than leaving an empty shell on the bench.
            if (devices != null && devices.IsPlacedDeviceCompletelyDisassembled())
            {
                devices.DestroyDeviceContainer();
                states.Enter<EmptyDisassembleState>();
                return;
            }

            states.Enter<DetectionDisassembleState>();
        }
    }

    // The end of a cycle. Patched here rather than on a timer: this is the game's own "the
    // countdown finished" handler, and it has already made the contents clean and returned the
    // machine to idle by the time the postfix runs, so the parts coming out really are done.
    [HarmonyPatch(typeof(LaunchedUltrasonicState), "ResolveTimerCountdownComplete")]
    public static class Patch_EmptyBathWhenDone
    {
        private static void Postfix()
        {
            UltrasonicBath.EmptyAfterCleaning();
        }
    }
}
