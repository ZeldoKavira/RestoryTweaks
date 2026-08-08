using System;
using System.Collections.Generic;
using System.Reflection;
using Restory.Data.Elements;
using Restory.Data.Elements.Condition;
using Restory.Gameplay.Devices;
using Restory.Gameplay.Elements;
using Restory.Gameplay.Soldering;
using Restory.UI.Presenters.Notepad;
using UnityEngine;

namespace RestoryTweaks
{
    // Last resort: declare the device on the bench finished, whatever state it's in.
    //
    // This exists because a device can end up genuinely unfinishable - a screw socket left with no
    // memory of its screw, a part destroyed - at which point normal play has no way back and a
    // story device would be lost. It is deliberately blunt, and deliberately manual.
    //
    // It follows the game's own "Repair Placed Device" cheat where it can: same per-element repair,
    // same ForceCheckQuality afterwards. That cheat only fixes the CONDITION of parts already
    // installed, though, so this fills the empty sockets first - which is the half that matters
    // when the complaint is missing screws.
    internal static class ForceRepair
    {
        private static ElementService _elements;
        private static DefaultElementConditions _conditions;
        private static MethodInfo _completeInteraction;

        private static ElementService Elements
        {
            get
            {
                if (_elements == null) _elements = UnityEngine.Object.FindObjectOfType<ElementService>();
                return _elements;
            }
        }

        // A ScriptableObject asset rather than a scene component, so it's reached through the loaded
        // assets rather than by searching the scene. Everything that needs the standard conditions
        // is handed this same asset, so there's only ever one.
        private static DefaultElementConditions Conditions
        {
            get
            {
                if (_conditions != null) return _conditions;
                try
                {
                    var all = Resources.FindObjectsOfTypeAll<DefaultElementConditions>();
                    if (all != null && all.Length > 0) _conditions = all[0];
                }
                catch (Exception e) { Plugin.Log.LogError($"[ForceRepair] conditions: {e.Message}"); }
                return _conditions;
            }
        }

        public static void Run()
        {
            try
            {
                var device = AutoAssemble.PlacedDevice();
                if (device == null)
                {
                    Report("Force repair: no device on the bench.");
                    return;
                }
                if (!AutoAssemble.StillOnRepairPad(device))
                {
                    Report("Force repair: open the device at the repair pad first.");
                    return;
                }

                int filled = 0, made = 0;
                FillEverySocket(device, ref filled, ref made);
                int repaired = RepairInstalled(device);

                var status = device.CheckAssembleStatus();
                RefreshQuality();

                string summary = $"Force repair: {filled} socket(s) filled"
                                 + (made > 0 ? $" ({made} part(s) recreated)" : "")
                                 + $", {repaired} part(s) set to perfect. Device is now: {status}.";
                Plugin.Log.LogInfo($"[ForceRepair] {summary}");
                Toast.Show(status == Device.AssembleStatus.Assembled
                    ? "Device forced to repaired"
                    : $"Force repair incomplete: {status}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[ForceRepair] failed: {e}");
                Toast.Show("Force repair failed - see the log");
            }
        }

        // Fill every socket, ignoring whether parts are identified, clean or intact - the point is
        // to finish a device that can't be finished any other way.
        private static void FillEverySocket(Device device, ref int filled, ref int made)
        {
            var loose = AutoAssemble.LooseParts(device);

            bool progress = true;
            int guard = 0;

            // Each fill can unblock sockets that weren't available before, so sweep repeatedly. The
            // guard is a backstop against a socket that reports available but never accepts.
            while (progress && guard++ < 200)
            {
                progress = false;

                foreach (var socket in AutoAssemble.AllSockets(device))
                {
                    if (socket == null || socket.NestedElement != null) continue;
                    if (!socket.IsAvailable) continue;

                    if (AutoAssemble.IsSmall(socket))
                    {
                        if (AutoAssemble.TryDriveScrew(socket, loose, out _)) { filled++; progress = true; break; }

                        // No screw of this type left anywhere: make one.
                        var screw = Create(socket);
                        if (screw == null) continue;
                        if (DriveCreatedScrew(socket, screw)) { filled++; made++; progress = true; break; }
                        continue;
                    }

                    var part = TakeAnything(loose, socket);
                    bool created = false;
                    if (part == null) { part = Create(socket); created = part != null; }
                    if (part == null) continue;

                    try
                    {
                        socket.AttachElement(part);
                        if (socket.NestedElement == null) continue;
                        loose.Remove(part);
                        filled++;
                        if (created) made++;
                        progress = true;
                        break;
                    }
                    catch (Exception e) { Plugin.Log.LogError($"[ForceRepair] attach failed: {e.Message}"); }
                }
            }
        }

        // Like TakeMatching, but without the readiness test: a dirty or damaged part is fine here
        // because RepairInstalled sets everything to perfect immediately afterwards.
        private static ElementBase TakeAnything(List<ElementBase> loose, ElementSocket socket)
        {
            var wanted = socket.CompatibleElementInfo;
            if (wanted == null) return null;

            for (int i = 0; i < loose.Count; i++)
            {
                var el = loose[i];
                if (el == null) { loose.RemoveAt(i--); continue; }
                if (!ReferenceEquals(el.Info, wanted)) continue;
                loose.RemoveAt(i);
                return el;
            }
            return null;
        }

        // Build a replacement for a part that no longer exists anywhere.
        private static ElementBase Create(ElementSocket socket)
        {
            try
            {
                var info = socket.CompatibleElementInfo;
                var service = Elements;
                var conditions = Conditions;
                if (info == null || service == null || conditions == null) return null;

                var element = service.CreateElement(new ElementData
                {
                    Info = info,
                    Condition = conditions.PerfectElementCondition,
                    IsInspected = true
                });

                if (element != null)
                    Plugin.Log.LogInfo($"[ForceRepair] Recreated a missing {info.name}.");
                return element;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[ForceRepair] could not recreate a part: {e.Message}");
                return null;
            }
        }

        // Same two steps as the normal screw path: attach, then complete the interaction, because
        // ThreadedElement.AttachToDevice leaves Progress at 1 and a screw at Progress 1 blocks
        // every socket it covers.
        private static bool DriveCreatedScrew(ElementSocket socket, ElementBase screw)
        {
            try
            {
                socket.AttachElement(screw);
                if (socket.NestedElement == null) return false;

                if (_completeInteraction == null)
                    _completeInteraction = typeof(ElementBase).GetMethod("CompleteInteraction",
                        BindingFlags.Instance | BindingFlags.NonPublic);

                if (_completeInteraction != null) _completeInteraction.Invoke(screw, null);
                return true;
            }
            catch (Exception e) { Plugin.Log.LogError($"[ForceRepair] screw: {e.Message}"); return false; }
        }

        // The game's own RepairElement, applied to everything installed: clear the dirt texture,
        // drop any scorching, set the condition to perfect. Also marks parts inspected, so the
        // notepad doesn't still show them as unknown.
        private static int RepairInstalled(Device device)
        {
            var conditions = Conditions;
            if (conditions == null)
            {
                Plugin.Log.LogWarning("[ForceRepair] Couldn't find the default conditions; parts were "
                                      + "fitted but not set to perfect.");
                return 0;
            }

            int repaired = 0;
            foreach (var socket in device.ElementSockets)
            {
                var element = socket != null ? socket.NestedElement : null;
                if (element == null || element.ConditionHandler == null) continue;

                try
                {
                    var data = element.ConditionHandler.ElementData;
                    if (data != null)
                    {
                        data.IsInspected = true;

                        if (data.Condition is DirtyElementCondition
                            && element.ConditionHandler.TextureMaskHolder != null)
                            element.ConditionHandler.TextureMaskHolder.ClearWorkTexture();

                        if (data.AdditionalProperty is ScorchedCircuitProperty)
                            data.AdditionalProperty = null;

                        if (data.Condition is PerfectElementCondition) continue;
                    }

                    element.ConditionHandler.UpdateCondition(conditions.PerfectElementCondition);
                    repaired++;
                }
                catch (Exception e) { Plugin.Log.LogError($"[ForceRepair] repairing a part: {e.Message}"); }
            }
            return repaired;
        }

        // Make the game re-evaluate the device, and refresh the notepad if it's open, so the result
        // shows up rather than waiting for the next thing to poke it.
        private static void RefreshQuality()
        {
            try
            {
                var devices = UnityEngine.Object.FindObjectOfType<DeviceService>();
                var container = devices != null ? devices.PlacedDeviceContainer : null;
                if (container != null) container.ForceCheckQuality();

                var notepad = UnityEngine.Object.FindObjectOfType<GUI_NotepadWindow>();
                if (notepad != null && notepad.IsVisible) notepad.UpdateInfoFromCurrentDevice();
            }
            catch (Exception e) { Plugin.Log.LogError($"[ForceRepair] refresh: {e.Message}"); }
        }

        private static void Report(string message)
        {
            Plugin.Log.LogInfo($"[ForceRepair] {message}");
            Toast.Show(message);
        }
    }
}
