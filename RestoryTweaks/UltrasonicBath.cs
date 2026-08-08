using System;
using System.Reflection;
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
}
