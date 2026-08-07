using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace RestoryTweaks
{
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "net.zeldo.restorytweaks";
        public const string Name = "Restory Tweaks";
        public const string Version = "0.1.0";

        internal static ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;

            EnableConsole();

            OrderPartConfig.Init(Config);
            DeliveryToPartsBoxConfig.Init(Config);
            AutoAssembleConfig.Init(Config);

            ApplyPatches();

            // Driver object for the per-frame watchers.
            var go = new GameObject("RestoryTweaks");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<DeliveryWatcher>();
            go.AddComponent<AutoAssembleWatcher>();

            Log.LogInfo($"{Name} v{Version} loaded.");
        }

        // BepInEx ships with its console off, so there's no window to see the log in. Turn it on in
        // BepInEx.cfg. This only takes effect on the NEXT launch - the console is created during
        // preloading, long before any plugin runs.
        private void EnableConsole()
        {
            try
            {
                string cfgPath = System.IO.Path.Combine(Paths.ConfigPath, "BepInEx.cfg");
                if (!System.IO.File.Exists(cfgPath)) return;

                var lines = System.IO.File.ReadAllLines(cfgPath);
                bool inConsole = false, changed = false;

                for (int i = 0; i < lines.Length; i++)
                {
                    string t = lines[i].Trim();
                    if (t.StartsWith("[")) inConsole = t.Equals("[Logging.Console]", StringComparison.OrdinalIgnoreCase);
                    if (!inConsole) continue;

                    if (t.StartsWith("Enabled", StringComparison.OrdinalIgnoreCase)
                        && t.EndsWith("false", StringComparison.OrdinalIgnoreCase))
                    {
                        lines[i] = "Enabled = true";
                        changed = true;
                    }
                }

                if (!changed) return;
                System.IO.File.WriteAllLines(cfgPath, lines);
                Log.LogInfo("Enabled the BepInEx console - it'll appear from the next launch.");
            }
            catch (Exception e) { Log.LogWarning($"Couldn't enable the console: {e.Message}"); }
        }

        // Patch each class on its own rather than with PatchAll: PatchAll is all-or-nothing, so a
        // single bad target (an overloaded method needing an explicit signature, say) throws and
        // takes every other feature down with it, with no clue beyond a missing log line.
        private void ApplyPatches()
        {
            var harmony = new Harmony(Guid);
            int ok = 0, failed = 0;

            foreach (var type in System.Reflection.Assembly.GetExecutingAssembly().GetTypes())
            {
                if (type.GetCustomAttributes(typeof(HarmonyPatch), true).Length == 0) continue;
                try { harmony.CreateClassProcessor(type).Patch(); ok++; }
                catch (Exception e) { failed++; Log.LogError($"Patch failed: {type.Name} - {e.Message}"); }
            }

            if (failed > 0) Log.LogWarning($"{ok} patch class(es) applied, {failed} failed.");
            else Log.LogInfo($"{ok} patch class(es) applied.");

            // Not attribute-driven: the target is found reflectively (see the class for why).
            InteractorHook.Apply(harmony);
        }
    }
}
