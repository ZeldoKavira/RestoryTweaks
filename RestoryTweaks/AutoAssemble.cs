using System;
using System.Collections;
using System.Collections.Generic;
using BepInEx.Configuration;
using Restory.Data.Elements;
using Restory.Data.Elements.Condition;
using Restory.Gameplay.Devices;
using Restory.Gameplay.Disassemble.StateMachine;
using Restory.Gameplay.Elements;
using Restory.Gameplay.Workplace;
using UnityEngine;

namespace RestoryTweaks
{
    // Put the device back together once every part is finished.
    //
    // The game models the hard parts already. Device.SortedSockets is the install order and
    // ElementSocket.IsAvailable refuses a socket whose covered sockets aren't filled yet, so
    // re-checking availability each pass respects assembly order without working it out here.
    // AttachElement does the rest: removes the part from the bench, reparents it, attaches it to
    // the device and notifies linked sockets.
    //
    // Parts are gathered from ANYWHERE loose, not just the work surface. Screws go into a
    // SmallElementBin when removed, which simply parents them to itself - they never appear in
    // WorkSurface.PlacedElements, which is why assembly used to stall at NotScrewed with every
    // screw sitting in the bin.
    public static class AutoAssembleConfig
    {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> RequireAllReady;
        internal static ConfigEntry<float> PartDelayMs;
        internal static ConfigEntry<float> ScrewDelayMs;
        internal static ConfigEntry<KeyboardShortcut> Key;
        internal static ConfigEntry<KeyboardShortcut> ToggleKey;
        internal static ConfigEntry<KeyboardShortcut> ForceRepairKey;

        public static void Init(ConfigFile cfg)
        {
            Enabled = cfg.Bind("AutoAssemble", "Enabled", true,
                "Reassemble the device automatically once every part is identified, cleaned and " +
                "undamaged. Screws are fitted too.");
            RequireAllReady = cfg.Bind("AutoAssemble", "RequireEveryPartReady", true,
                "Wait until EVERY loose part is ready before assembling anything. Turn off to fit " +
                "whatever is ready as it goes.");
            PartDelayMs = cfg.Bind("AutoAssemble", "DelayBetweenPartsMs", 750f,
                new ConfigDescription("Pause between fitting normal parts.",
                    new AcceptableValueRange<float>(0f, 5000f)));
            ScrewDelayMs = cfg.Bind("AutoAssemble", "DelayBetweenScrewsMs", 200f,
                new ConfigDescription("Pause between driving screws (small parts).",
                    new AcceptableValueRange<float>(0f, 5000f)));
            Key = cfg.Bind("AutoAssemble", "AssembleNowKey", new KeyboardShortcut(KeyCode.F6),
                "Assemble right now, without waiting for every part to be ready.");
            ForceRepairKey = cfg.Bind("AutoAssemble", "ForceRepairKey",
                new KeyboardShortcut(KeyCode.F8, KeyCode.LeftControl),
                "RESCUE ONLY. Fills every remaining socket on the device at the bench - recreating " +
                "parts that no longer exist if it has to - and sets everything to perfect. Meant " +
                "for a device that can't be finished any other way. Deliberately needs Ctrl held, " +
                "so it can't be hit by accident.");
            ToggleKey = cfg.Bind("AutoAssemble", "ToggleKey", new KeyboardShortcut(KeyCode.F7),
                "Turn automatic assembly on or off without restarting, and stop a run already " +
                "under way. The new setting is saved, so it survives a restart. The assemble-now " +
                "key still works while it's off.");
        }

        internal static bool On => Enabled != null && Enabled.Value;
    }

    internal static class AutoAssemble
    {
        private static DeviceService _devices;
        private static WorkSurface _surface;

        private static DeviceService Devices
        {
            get
            {
                if (_devices == null) _devices = UnityEngine.Object.FindObjectOfType<DeviceService>();
                return _devices;
            }
        }

        private static WorkSurface Surface
        {
            get
            {
                if (_surface == null) _surface = UnityEngine.Object.FindObjectOfType<WorkSurface>();
                return _surface;
            }
        }

        public static Device PlacedDevice()
        {
            try
            {
                var container = Devices != null ? Devices.PlacedDeviceContainer : null;
                return container != null ? container.Device : null;
            }
            catch { return null; }
        }

        // Finished = identified and in perfect condition, rather than dirty, burnt or broken.
        //
        // Screws are the exception, and getting this wrong is what stalled assembly: they're never
        // inspected and never cleaned, so demanding IsInspected + Perfect rejected every one. They
        // then sat in the bin as "spare parts" while the sockets they block stayed unavailable -
        // which is exactly what "0 sockets with no ready part, 13 blocked" was describing.
        public static bool IsReady(ElementBase element)
        {
            try
            {
                var data = element != null && element.ConditionHandler != null
                    ? element.ConditionHandler.ElementData : null;
                if (data == null) return false;

                if (data.Info != null && data.Info.Category == ElementCategory.Small)
                    return !(data.Condition is DamagedElementCondition)
                        && !(data.Condition is BurntElementCondition);

                return data.IsInspected && data.Condition is PerfectElementCondition;
            }
            catch { return false; }
        }

        // Parts belonging to THIS device that aren't currently installed - bench, bin, floor.
        //
        // Restricted to parts the device actually has a socket for. A scene-wide sweep also picks
        // up elements from storage and elsewhere, and since assembly waits for everything to be
        // ready, one unrelated dirty part would silently stop it starting at all.
        public static List<ElementBase> LooseParts(Device device)
        {
            var loose = new List<ElementBase>();
            try
            {
                var installed = new HashSet<int>();
                var wanted = new HashSet<int>();

                foreach (var socket in device.ElementSockets)
                {
                    if (socket == null) continue;
                    if (socket.NestedElement != null) installed.Add(socket.NestedElement.GetInstanceID());
                    if (socket.CompatibleElementInfo != null) wanted.Add(socket.CompatibleElementInfo.GetInstanceID());
                }

                foreach (var el in UnityEngine.Object.FindObjectsOfType<ElementBase>())
                {
                    if (el == null || el.Info == null) continue;
                    if (installed.Contains(el.GetInstanceID())) continue;
                    if (!wanted.Contains(el.Info.GetInstanceID())) continue;   // not this device's
                    loose.Add(el);
                }
            }
            catch (Exception e) { Plugin.Log.LogError($"[AutoAssemble] scan failed: {e.Message}"); }
            return loose;
        }

        // The first part of this device that isn't finished yet, looking at parts still installed as
        // well as loose ones.
        //
        // Checking only the loose parts was wrong: a part sitting in its socket is unexamined until
        // you pull it out and inspect it, so assembly would start as soon as the handful you'd
        // already taken out were done - screwing the case shut over everything you hadn't looked at.
        // The game treats installed parts as inspectable state too; its own "Inspect Placed Device"
        // cheat walks Device.ElementSockets and sets IsInspected on each socket's NestedElement.
        public static ElementBase FirstUnreadyPart(Device device, List<ElementBase> loose, out bool installed)
        {
            installed = false;

            if (loose != null)
                foreach (var el in loose)
                    if (!IsReady(el)) return el;

            try
            {
                foreach (var socket in device.ElementSockets)
                {
                    if (socket == null) continue;
                    var el = socket.NestedElement;
                    if (el != null && !IsReady(el)) { installed = true; return el; }
                }
            }
            catch (Exception e) { Plugin.Log.LogError($"[AutoAssemble] readiness scan failed: {e.Message}"); }

            return null;
        }

        public static string Describe(ElementBase el)
        {
            try { return el != null && el.Info != null ? el.Info.name : "a part"; }
            catch { return "a part"; }
        }

        // Drive the screw in the same way the player does.
        //
        // Clicking a screw's phantom calls ElementProjection.Activate(), and the socket's handler
        // then does the real work: it attaches its remembered lastNestedElement AND enters
        // InstallingDisassembleState, which runs the screwdriver interaction. Attaching the element
        // ourselves skips that second half, which is why screws never counted - so this triggers
        // the projection instead and lets the game run its own sequence.
        //
        // The projection is parented to the socket and only exists while the device is activated,
        // which is why assembly waits for that.
        private static System.Reflection.MethodInfo _completeInteraction;
        private static System.Reflection.MethodInfo _hideProjection;

        // The socket's own projection teardown: unsubscribes and returns it to the pool.
        private static void HideProjection(ElementSocket socket)
        {
            try
            {
                if (_hideProjection == null)
                    _hideProjection = typeof(ElementSocket).GetMethod("HideSmallElementProjection",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (_hideProjection != null) _hideProjection.Invoke(socket, null);
            }
            catch (Exception e) { Plugin.Log.LogError($"[AutoAssemble] hiding projection failed: {e.Message}"); }
        }

        // Drive a screw home.
        //
        // Screws are ThreadedElements, and their AttachToDevice deliberately does NOT run the snap
        // that normal parts use - it just seats the transform, fires OnInstalled and sets
        // IsInstalling = true, leaving Progress at 1. Only CompleteInteraction finishes the job:
        //
        //     if (IsInstalling) { Progress = 0f; shouldBeInstalled = false; }
        //
        // That matters because ElementSocket.IsAvailable requires the sockets a socket covers to
        // hold a part with Progress == 0. A screw left at Progress 1 therefore blocks everything it
        // covers, which is why assembly deadlocked with every remaining socket "blocked" - the
        // attach was only ever half the operation.
        public static bool TryDriveScrew(ElementSocket socket, List<ElementBase> loose, out string why)
        {
            why = null;
            try
            {
                if (!IsSmall(socket)) { why = "not a small socket"; return false; }
                if (socket.NestedElement != null) { why = "already filled"; return false; }

                var screw = socket.LastNestedElement;

                // A socket can forget which screw was its own. Leaving the bench runs
                // Device.ThrowLooseElements, which cancels and detaches any screw that wasn't
                // fully driven - so a run interrupted partway leaves sockets available but with
                // lastNestedElement empty. The screws themselves are still on the bench, and
                // screws of a type are interchangeable, so match one by type rather than giving up.
                // Without this the device can never be finished again, by the mod or by hand.
                if (screw == null) screw = PeekMatching(loose, socket);

                if (screw == null) { why = "no screw of this type left on the bench"; return false; }

                // Destroy the phantom FIRST, exactly as ResolveProjectionActivated does. Skipping
                // this leaves the projection sitting over a screw that's already in, so seated
                // screws keep glowing as though they still need driving.
                HideProjection(socket);

                socket.AttachElement(screw);
                if (socket.NestedElement == null) { why = "attach didn't take"; return false; }

                // Finish the screwing. Protected, so reached by reflection - the alternative is
                // entering InstallingDisassembleState, which then waits on player input.
                if (_completeInteraction == null)
                {
                    _completeInteraction = typeof(ElementBase).GetMethod("CompleteInteraction",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    if (_completeInteraction == null) { why = "CompleteInteraction not found"; return false; }
                }

                _completeInteraction.Invoke(screw, null);   // virtual: runs ThreadedElement's override

                if (screw.Progress > 0f)
                    why = $"screw seated but progress is {screw.Progress:0.00}";

                // Claim it either way. A remembered screw can also be sitting in the loose list, and
                // leaving it there let a later socket take the same element back out again.
                if (loose != null) loose.Remove(screw);

                return true;
            }
            catch (Exception e) { why = e.GetType().Name + ": " + e.Message; return false; }
        }

        // Assembly only makes sense while the device is opened up: that's when sockets are
        // activated and screw projections exist at all.
        public static bool DeviceIsOpen(Device device)
        {
            try { return device != null && device.IsActivated; }
            catch { return false; }
        }

        private static DisassembleStateMachine _states;

        private static DisassembleStateMachine States
        {
            get
            {
                if (_states == null) _states = UnityEngine.Object.FindObjectOfType<DisassembleStateMachine>();
                return _states;
            }
        }

        // Still at the bench, with this device open in front of you.
        //
        // Checked between every step, because assembly is paced over seconds and walking away
        // partway through shouldn't leave it quietly screwing the case shut behind you.
        //
        // The state machine is what notices first: the camera merely starting to swing away from
        // the disassemble view enters DisabledDisassembleState immediately, whereas Device
        // .IsActivated stays true until the exit animation finishes - easily long enough to fit
        // several more parts after you've left.
        public static bool StillOnRepairPad(Device device)
        {
            try
            {
                if (!DeviceIsOpen(device)) return false;

                // A different job on the bench is not this job.
                if (!ReferenceEquals(PlacedDevice(), device)) return false;

                var states = States;
                if (states == null) return true;   // can't tell; don't interrupt work that's fine

                return !(states.ActiveState is DisabledDisassembleState);
            }
            catch { return false; }
        }

        // Every socket, in install order.
        //
        // Device.SortedSockets CANNOT be used on its own: InitSortedSockets deliberately excludes
        // every Small socket, so iterating it means never seeing a single screw. That's why screws
        // were never fitted and never even appeared in diagnostics - the loop never visited them.
        // Sorted order is still used for the draggable parts; the screws are appended.
        public static List<ElementSocket> AllSockets(Device device)
        {
            var list = new List<ElementSocket>();
            try
            {
                foreach (var sk in device.SortedSockets)
                    if (sk != null) list.Add(sk);

                foreach (var sk in device.ElementSockets)
                    if (sk != null && !list.Contains(sk)) list.Add(sk);   // the Small ones
            }
            catch (Exception e) { Plugin.Log.LogError($"[AutoAssemble] socket list failed: {e.Message}"); }
            return list;
        }

        // Batteries and the like sit in a SerialElementSocket: only that one socket is ever
        // installable, and its SubordinateElementSockets report IsAvailable => false permanently.
        // Fitting a second one works by the game SHIFTING the current occupant down into a
        // subordinate slot when you start dragging - the "batteries slide over to make room"
        // behaviour. Nothing drags here, so the shift has to be performed explicitly.
        private static System.Reflection.FieldInfo _subordinates;
        private static System.Reflection.MethodInfo _passToSocket;

        public static bool TryFreeSerialSocket(ElementSocket socket)
        {
            try
            {
                if (!(socket is SerialElementSocket serial)) return false;
                if (serial.NestedElement == null) return true;          // already free

                if (_subordinates == null)
                    _subordinates = typeof(SerialElementSocket).GetField("subordinateSockets",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (_passToSocket == null)
                    _passToSocket = typeof(SerialElementSocket).GetMethod("TryPassElementToSocket",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (_subordinates == null || _passToSocket == null) return false;

                var subs = _subordinates.GetValue(serial) as System.Collections.IList;
                if (subs == null) return false;

                // Last to first, exactly as ResolveElementStartDrag does.
                for (int i = subs.Count - 1; i >= 0; i--)
                {
                    var target = subs[i] as ElementSocket;
                    if (target == null) continue;
                    bool moved = (bool)_passToSocket.Invoke(serial, new object[] { target });
                    if (moved) return serial.NestedElement == null;
                }
            }
            catch (Exception e) { Plugin.Log.LogError($"[AutoAssemble] serial shift failed: {e.Message}"); }
            return false;
        }

        public static bool IsSmall(ElementSocket socket)
        {
            try { return socket.CompatibleElementInfo != null && socket.CompatibleElementInfo.Category == ElementCategory.Small; }
            catch { return false; }
        }

        // Everything loose is finished, and there's something to do.
        public static bool ReadyToAssemble(out Device device, out List<ElementBase> loose)
        {
            device = PlacedDevice();
            loose = null;
            if (device == null) return false;

            // Only while the device is opened up for work - otherwise it would rebuild itself
            // behind your back, and the screw projections don't even exist yet.
            if (!DeviceIsOpen(device)) return false;

            if (device.CheckAssembleStatus() == Device.AssembleStatus.Assembled) return false;

            loose = LooseParts(device);

            bool inDevice;
            if (AutoAssembleConfig.RequireAllReady.Value
                && FirstUnreadyPart(device, loose, out inDevice) != null) return false;

            if (loose.Count > 0) return true;

            // Nothing loose doesn't mean nothing to do: screws live on their sockets rather than
            // lying about, so a device needing only screws has no loose parts at all.
            foreach (var socket in device.ElementSockets)
                if (socket != null && socket.NestedElement == null
                    && IsSmall(socket) && socket.LastNestedElement != null)
                    return true;

            return false;
        }

        // Peek, don't take. Using TakeMatching as a probe removed the part from the list even when
        // nothing was fitted, which quietly lost candidates mid-sweep.
        public static bool HasMatching(List<ElementBase> loose, ElementSocket socket)
        {
            var wanted = socket.CompatibleElementInfo;
            if (wanted == null) return false;

            foreach (var el in loose)
                if (el != null && ReferenceEquals(el.Info, wanted) && IsReady(el)) return true;
            return false;
        }

        // The element this socket would take, left in place. Used where the caller only removes it
        // from the list once the attach has actually succeeded.
        public static ElementBase PeekMatching(List<ElementBase> loose, ElementSocket socket)
        {
            var wanted = socket.CompatibleElementInfo;
            if (wanted == null || loose == null) return null;

            foreach (var el in loose)
                if (el != null && ReferenceEquals(el.Info, wanted) && IsReady(el)) return el;
            return null;
        }

        public static ElementBase TakeMatching(List<ElementBase> loose, ElementSocket socket)
        {
            var wanted = socket.CompatibleElementInfo;
            if (wanted == null) return null;

            for (int i = 0; i < loose.Count; i++)
            {
                var el = loose[i];
                if (el == null) { loose.RemoveAt(i--); continue; }
                if (!ReferenceEquals(el.Info, wanted)) continue;

                // Never fit an unfinished part, even on the manual trigger - putting a dirty or
                // broken part back into the device is never the intent.
                if (!IsReady(el)) continue;

                loose.RemoveAt(i);
                return el;
            }
            return null;
        }
    }

    public class AutoAssembleWatcher : MonoBehaviour
    {
        private float _nextCheck;
        private bool _running;
        private bool _cancel;
        private int _lastStuckAt;
        private bool _saidStuck;

        // Cheap description of the situation, so a fruitless attempt isn't repeated until something
        // actually changes.
        //
        // This has to cover the parts available as well as the device itself. Taking a missing part
        // out of the parts box changes nothing about the sockets, yet it's exactly the event that
        // should un-stick a pass that stalled for want of that part - and hashing sockets alone
        // meant the retry never happened. Readiness is folded in too, so identifying or cleaning a
        // part that's already on the bench counts as a change.
        private static int Fingerprint(Device device, List<ElementBase> loose)
        {
            int n = 17;
            try
            {
                foreach (var socket in device.ElementSockets)
                    n = n * 31 + (socket != null && socket.NestedElement != null ? 1 : 0);

                if (loose != null)
                {
                    // Summed rather than sequenced: the scan order of loose parts isn't stable, and
                    // a reordering isn't a change worth retrying for.
                    int parts = 0;
                    foreach (var el in loose)
                    {
                        if (el == null) continue;
                        parts += el.GetInstanceID() * (AutoAssemble.IsReady(el) ? 2 : 1);
                    }
                    n = n * 31 + parts;
                }
            }
            catch { }
            return n;
        }

        // Flip automatic assembly, and abandon anything in progress.
        //
        // Written straight back to the config entry rather than held in a separate runtime flag, so
        // there's one answer to "is it on" and the choice survives a restart - BepInEx saves the
        // file when the value is set.
        private void Toggle()
        {
            bool on = !AutoAssembleConfig.Enabled.Value;
            AutoAssembleConfig.Enabled.Value = on;

            _cancel = !on;                 // stop the current run; turning it back on doesn't
            _lastStuckAt = 0;              // a fresh decision deserves a fresh attempt
            _saidStuck = false;
            _lastIdleReason = 0;

            string message = on ? "Auto-assemble ON" : "Auto-assemble OFF";
            Plugin.Log.LogInfo($"[AutoAssemble] {message}.");
            Toast.Show(message);
        }

        private void Update()
        {
            try
            {
                // Ahead of the _running guard on purpose: the point of the toggle is to be able to
                // stop a run that's already under way and put a device back the way you want it.
                if (AutoAssembleConfig.ToggleKey.Value.IsDown()) Toggle();

                // Also ahead of the running guard: the device it rescues may be one a run is
                // currently stuck on.
                if (AutoAssembleConfig.ForceRepairKey.Value.IsDown())
                {
                    _cancel = true;             // don't have both writing to the same sockets
                    ForceRepair.Run();
                    _lastStuckAt = 0; _saidStuck = false; _lastIdleReason = 0;
                    return;
                }

                if (_running) return;

                if (AutoAssembleConfig.Key.Value.IsDown())
                {
                    _lastStuckAt = 0; _saidStuck = false;      // manual trigger always retries
                    StartCoroutine(Run(force: true));
                    return;
                }

                if (!AutoAssembleConfig.On || Time.unscaledTime < _nextCheck) return;
                _nextCheck = Time.unscaledTime + 1f;      // a device doesn't finish mid-frame

                if (!AutoAssemble.ReadyToAssemble(out var dev, out var available))
                {
                    ExplainIdle();
                    return;
                }

                // If a previous pass on this exact situation achieved nothing, don't keep retrying
                // it - wait until the device or the available parts change.
                int fingerprint = Fingerprint(dev, available);
                if (fingerprint == _lastStuckAt)
                {
                    // Don't retry a state already failed on - but say so ONCE, because otherwise
                    // this is indistinguishable from the mod doing nothing at all.
                    if (!_saidStuck)
                    {
                        _saidStuck = true;
                        Plugin.Log.LogInfo("[AutoAssemble] Nothing more it can fit; waiting for the " +
                                           "device to change (F6 forces a retry).");
                    }
                    return;
                }
                _lastStuckAt = 0;
                _saidStuck = false;

                StartCoroutine(Run(force: false));
            }
            catch (Exception e) { Plugin.Log.LogError($"[AutoAssemble] {e.Message}"); }
        }

        // Fitted one at a time with a pause between, so it reads as the device being rebuilt
        // rather than snapping together in a single frame.
        // Full picture of why assembly stopped: every empty socket, and for the blocked ones, what
        // is holding them. Availability depends on the sockets a socket COVERS being filled with a
        // part whose Progress is 0, so both facts are printed - a part sitting at Progress > 0
        // would block everything it should have unblocked.
        private void DumpSockets(Device device)
        {
            try
            {
                Plugin.Log.LogInfo("[AutoAssemble] --- socket dump ---");

                // Screws first and never truncated: they're the ones everything else waits on, and
                // capping the list is how the last dump managed to show none of them at all.
                var ordered = new List<ElementSocket>();
                var all = AutoAssemble.AllSockets(device);
                foreach (var sk in all)
                    if (sk.NestedElement == null && AutoAssemble.IsSmall(sk)) ordered.Add(sk);
                foreach (var sk in all)
                    if (sk.NestedElement == null && !AutoAssemble.IsSmall(sk)) ordered.Add(sk);

                int shown = 0;
                foreach (var socket in ordered)
                {
                    // Show every screw; limit only the bulk of ordinary parts.
                    if (!AutoAssemble.IsSmall(socket) && shown++ >= 8) { Plugin.Log.LogInfo("[AutoAssemble] ...more parts omitted."); break; }

                    string name = socket.CompatibleElementInfo != null ? socket.CompatibleElementInfo.name : "?";
                    string kind = AutoAssemble.IsSmall(socket) ? "SCREW" : "part";
                    string last = socket.LastNestedElement != null ? "remembered" : "none";

                    var holding = new List<string>();
                    foreach (var blocked in socket.BlockedSockets)
                    {
                        if (blocked == null) { holding.Add("(lost)"); continue; }
                        var nested = blocked.NestedElement;
                        if (nested == null) holding.Add("empty:" + (blocked.CompatibleElementInfo != null ? blocked.CompatibleElementInfo.name : "?"));
                        else if (nested.Progress > 0f) holding.Add($"progress={nested.Progress:0.00}:{nested.name}");
                    }

                    Plugin.Log.LogInfo($"[AutoAssemble] {kind} '{name}' available={socket.IsAvailable} " +
                                       $"lastNested={last} blocks={socket.BlockedSockets.Count} " +
                                       $"blockers={socket.Blockers.Count}" +
                                       (holding.Count > 0 ? " waiting on -> " + string.Join(", ", holding.ToArray()) : ""));
                }
            }
            catch (Exception e) { Plugin.Log.LogError($"[AutoAssemble] dump failed: {e.Message}"); }
        }

        // Explain what's still missing, rather than leaving a half-assembled device unexplained.
        private void ReportUnfilled(Device device, List<ElementBase> loose)
        {
            try
            {
                int blocked = 0, noPart = 0;
                var examples = new List<string>();

                foreach (var socket in AutoAssemble.AllSockets(device))
                {
                    if (socket == null || socket.NestedElement != null) continue;

                    string name = socket.CompatibleElementInfo != null ? socket.CompatibleElementInfo.name : "?";
                    if (!socket.IsAvailable) { blocked++; continue; }

                    noPart++;
                    if (examples.Count < 4)
                        examples.Add($"{name}{(AutoAssemble.IsSmall(socket) ? " (screw)" : "")}");
                }

                if (noPart == 0 && blocked == 0) return;
                Plugin.Log.LogInfo($"[AutoAssemble] Still empty: {noPart} socket(s) with no ready part" +
                                   (examples.Count > 0 ? " (" + string.Join(", ", examples.ToArray()) + ")" : "") +
                                   $", {blocked} blocked by other sockets. {loose.Count} spare part(s) left over.");
            }
            catch { }
        }

        // Say why assembly isn't starting, once per changed situation - "nothing happened" is the
        // hardest thing to debug from the outside.
        private int _lastIdleReason;

        private void ExplainIdle()
        {
            try
            {
                var device = AutoAssemble.PlacedDevice();
                string reason;
                int key;

                if (device == null) { reason = "no device on the bench"; key = 1; }
                else if (!AutoAssemble.DeviceIsOpen(device)) { reason = "device isn't opened up for work"; key = 2; }
                else if (device.CheckAssembleStatus() == Device.AssembleStatus.Assembled) { reason = "already assembled"; key = 3; }
                else
                {
                    var loose = AutoAssemble.LooseParts(device);

                    // Count both loose and still-installed parts, so "waiting" names something you
                    // can actually go and deal with.
                    int notReady = 0, notReadyInDevice = 0;
                    foreach (var el in loose)
                        if (!AutoAssemble.IsReady(el)) notReady++;
                    foreach (var socket in device.ElementSockets)
                    {
                        if (socket == null || socket.NestedElement == null) continue;
                        if (!AutoAssemble.IsReady(socket.NestedElement)) { notReady++; notReadyInDevice++; }
                    }

                    if (notReady > 0)
                    {
                        bool inDevice;
                        var first = AutoAssemble.FirstUnreadyPart(device, loose, out inDevice);
                        reason = $"{notReady} part(s) not ready yet ({notReadyInDevice} still in the device)" +
                                 (first != null ? $", e.g. {AutoAssemble.Describe(first)}" : "");
                        key = 4000 + notReady * 10 + (notReadyInDevice > 0 ? 1 : 0);
                    }
                    else { reason = $"nothing to fit ({loose.Count} loose part(s))"; key = 5000 + loose.Count; }
                }

                if (key == _lastIdleReason) return;
                _lastIdleReason = key;
                Plugin.Log.LogInfo($"[AutoAssemble] Waiting: {reason}.");
            }
            catch { }
        }

        private IEnumerator Run(bool force)
        {
            _running = true;
            _cancel = false;       // a cancel only applies to the run it was asked for
            yield return RunInner(force);
            _running = false;      // reached even when RunInner stops early
        }

        private IEnumerator RunInner(bool force)
        {

            var device = AutoAssemble.PlacedDevice();
            if (device == null) { Plugin.Log.LogInfo("[AutoAssemble] No device on the bench."); yield break; }

            // Also guards the F6 path, which doesn't come through ReadyToAssemble. Off the pad the
            // screw projections don't exist, so there'd be nothing to work with anyway.
            if (!AutoAssemble.StillOnRepairPad(device))
            {
                Plugin.Log.LogInfo("[AutoAssemble] Not at the repair pad; not starting.");
                yield break;
            }

            var loose = AutoAssemble.LooseParts(device);
            if (!force && AutoAssembleConfig.RequireAllReady.Value)
            {
                bool inDevice;
                var unready = AutoAssemble.FirstUnreadyPart(device, loose, out inDevice);
                if (unready != null)
                {
                    Plugin.Log.LogInfo($"[AutoAssemble] {AutoAssemble.Describe(unready)} isn't ready " +
                                       $"({(inDevice ? "still installed" : "loose")}); not starting.");
                    yield break;
                }
            }

            int fitted = 0, screws = 0;
            string screwProblem = null;
            bool progress = true;
            string stopped = null;

            // Repeat passes: fitting a part unblocks sockets that weren't available before.
            while (progress && stopped == null)
            {
                progress = false;

                foreach (var socket in AutoAssemble.AllSockets(device))
                {
                    if (socket == null) continue;

                    // A full serial socket isn't finished - it can shift its occupant aside and
                    // take another, which is how a row of batteries gets filled.
                    if (socket.NestedElement != null)
                    {
                        if (!(socket is SerialElementSocket)) continue;
                        if (!AutoAssemble.HasMatching(loose, socket)) continue;      // nothing left to add
                        if (!AutoAssemble.TryFreeSerialSocket(socket)) continue;     // no free slot to shift into

                        // Freeing the socket IS progress. Restart the sweep and let the normal path
                        // fit the next one, rather than trying to do both in one iteration.
                        progress = true;
                        break;
                    }

                    if (!socket.IsAvailable) continue;

                    bool small = AutoAssemble.IsSmall(socket);

                    if (small)
                    {
                        if (!AutoAssemble.TryDriveScrew(socket, loose, out string why))
                        {
                            // Report the first refusal only; otherwise every screw logs every pass.
                            if (screwProblem == null && why != null) screwProblem = why;
                            continue;
                        }
                        fitted++; screws++; progress = true;
                    }
                    else
                    {
                        var part = AutoAssemble.TakeMatching(loose, socket);
                        if (part == null) continue;
                        try
                        {
                            socket.AttachElement(part);
                            fitted++; progress = true;
                        }
                        catch (Exception e) { Plugin.Log.LogError($"[AutoAssemble] fitting failed: {e.Message}"); continue; }
                    }

                    float ms = small ? AutoAssembleConfig.ScrewDelayMs.Value : AutoAssembleConfig.PartDelayMs.Value;
                    if (ms > 0f) yield return new WaitForSeconds(ms / 1000f);

                    // Re-check after every wait, not just at the start: this is the window in which
                    // you can leave or switch it off, and stopping here means what's already fitted
                    // stays fitted rather than being unwound.
                    if (_cancel) stopped = "you switched auto-assemble off";
                    else if (!AutoAssemble.StillOnRepairPad(device)) stopped = "you left the repair pad";

                    // The socket list is being walked while the device changes underneath it, so
                    // restart the sweep rather than continuing over stale availability.
                    break;
                }
            }

            if (stopped != null)
            {
                // Deliberately no stuck fingerprint: nothing failed here, it was interrupted. Come
                // back to the bench, or switch it on again, and it picks up where it left off.
                Plugin.Log.LogInfo($"[AutoAssemble] Stopped - {stopped}. "
                                   + $"Fitted {fitted} part(s) ({screws} screw(s)) first.");
                yield break;
            }

            var status = device.CheckAssembleStatus();
            if (fitted > 0)
                Plugin.Log.LogInfo($"[AutoAssemble] Fitted {fitted} part(s) ({screws} screw(s)). " +
                                   $"Device is now: {status}.");

            if (screws == 0 && screwProblem != null)
                Plugin.Log.LogInfo($"[AutoAssemble] No screws driven: {screwProblem}.");

            // Report whenever the device ISN'T finished - including when nothing at all could be
            // fitted, which is precisely the case that needs explaining. The stuck-state
            // fingerprint below keeps this to once per situation rather than once a second.
            if (status != Device.AssembleStatus.Assembled)
            {
                if (fitted == 0)
                    Plugin.Log.LogInfo("[AutoAssemble] Ran but couldn't fit anything.");
                ReportUnfilled(device, loose);
                DumpSockets(device);
            }

            // Rescan rather than reusing the local list: fitting consumes entries from it, so it no
            // longer describes what's lying on the bench by the time we get here.
            if (status != Device.AssembleStatus.Assembled)
                _lastStuckAt = Fingerprint(device, AutoAssemble.LooseParts(device));
        }
    }
}
