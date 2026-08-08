using System;
using System.Reflection;
using Restory.Gameplay.Devices;
using Restory.Gameplay.Disassemble.StateMachine;
using Restory.Gameplay.Elements;
using Restory.Gameplay.Equipment.Ultrasonic;

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
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[AutoOpenCleaner] ultrasonic: {e.Message}");
                return false;
            }
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
