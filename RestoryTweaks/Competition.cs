using System;
using Restory.Gameplay.Devices;
using Restory.Gameplay.Disassemble;
using Restory.Gameplay.Disassemble.StateMachine;

namespace RestoryTweaks
{
    // Finishing a competition that the mod assembled.
    //
    // The game ends a competition from the drop handler: CompleteDrag attaches the part you were
    // holding, asks the device whether it's whole, and only then enters EndCompetitionState.
    // Assembly here attaches parts to sockets directly and never goes through that handler, so a
    // run could finish with the device fully together and the competition still counting up.
    internal static class Competition
    {
        private static DisassembleGameMode _mode;
        private static DisassembleStateMachine _states;

        private static DisassembleGameMode Mode
        {
            get
            {
                if (_mode == null) _mode = UnityEngine.Object.FindObjectOfType<DisassembleGameMode>();
                return _mode;
            }
        }

        private static DisassembleStateMachine States
        {
            get
            {
                if (_states == null) _states = UnityEngine.Object.FindObjectOfType<DisassembleStateMachine>();
                return _states;
            }
        }

        // Is a competition running right now?
        public static bool InProgress
        {
            get
            {
                try
                {
                    var mode = Mode;
                    return mode != null && mode.IsInCompetition;
                }
                catch { return false; }
            }
        }

        public static void NotifyAssembled(Device device)
        {
            try
            {
                var mode = Mode;
                if (mode == null || !mode.IsInCompetition) return;

                // The same question the drop handler asks: every socket filled, and nothing still
                // mid-install. A screw left part-driven counts as not finished.
                if (device == null || !device.CheckIntegrityAndIsInstalling()) return;

                var states = States;
                if (states == null || states.ActiveState is EndCompetitionState) return;

                Plugin.Log.LogInfo("[AutoAssemble] Device is whole - ending the competition.");
                states.Enter<EndCompetitionState>();
            }
            catch (Exception e) { Plugin.Log.LogError($"[AutoAssemble] competition: {e.Message}"); }
        }
    }
}
