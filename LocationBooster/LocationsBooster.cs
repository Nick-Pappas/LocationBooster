#nullable disable
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System.Reflection;
using UnityEngine;

namespace LocationBudgetBooster
{
    public enum BoosterMode
    {
        Vanilla,    // Standard game behavior (Off)
        Filter,     // Generate standard square, but reject if outside distance ring (Sieve)
        Force,      // Generate directly inside distance ring (Donut Math)
        Survey      // My best idea for BC
    }


    [BepInPlugin("nickpappas.locationbooster", "Location Budget Booster", "0.3.2")]
    public class LocationBooster : BaseUnityPlugin
    {
        public static ConfigEntry<int> OuterMultiplier;
        public static ConfigEntry<int> InnerMultiplier;
        public static ConfigEntry<bool> LogSuccesses;
        public static ConfigEntry<bool> WriteToFile;
        public static ConfigEntry<int> ProgressInterval;
        public static ConfigEntry<bool> DiagnosticMode;
        public static ConfigEntry<int> InnerProgressInterval;
        public static ConfigEntry<int> SurveyVisitLimit;

        public static ConfigEntry<BoosterMode> Mode;
        public static ConfigEntry<string> FilterTarget;
        public static ConfigEntry<float> HildirCaveMinDistance;
        public static ConfigEntry<float> HildirCaveMaxDistance;
        public static ConfigEntry<float> WorldRadius;

        public static ManualLogSource Log;
        private static Harmony _harmony;

        void Awake()
        {
            Log = Logger;

            OuterMultiplier = Config.Bind("General", "OuterLoopMultiplier", 3, "Multiplies the budget for finding candidate Zones.");
            InnerMultiplier = Config.Bind("General", "InnerLoopMultiplier", 5, "Multiplies placement attempts inside a valid Zone.");
            Mode = Config.Bind("Strategy", "BoosterMode", BoosterMode.Vanilla, "Vanilla: Default behavior. Filter: Rejects zones outside range (fast). Force: Generates zones inside range (math).");
            FilterTarget = Config.Bind("Strategy", "TargetLocation", "Hildir_cave", "The prefab name to apply Filter/Force modes to. Leave empty to apply to nothing.");
            HildirCaveMinDistance = Config.Bind("DistanceFilter", "HildirCaveMinDistance", 0.1f, "Min distance fraction for Hildir_cave.");
            HildirCaveMaxDistance = Config.Bind("DistanceFilter", "HildirCaveMaxDistance", 0.8f, "Max distance fraction for Hildir_cave.");
            WorldRadius = Config.Bind("DistanceFilter", "WorldRadius", 10000f, "World radius in meters.");
            LogSuccesses = Config.Bind("Logging", "LogSuccess", true, "Log successful placements.");
            WriteToFile = Config.Bind("Logging", "WriteToFile", true, "Write diagnostics to a log file.");
            ProgressInterval = Config.Bind("Logging", "ProgressInterval", 100000, "Log progress every N outer loop iterations.");
            DiagnosticMode = Config.Bind("Logging", "DiagnosticMode", false, "Enable verbose debugging.");
            InnerProgressInterval = Config.Bind("Logging", "InnerProgressInterval", 200000, "Log inner progress every N iterations. Set to 0 to disable.");
            SurveyVisitLimit = Config.Bind("Strategy", "SurveyVisitLimit", 8, "How many times Survey mode revisits a zone before giving up on it.");

            BoosterDiagnostics.Initialize(Info.Metadata.Version.ToString());

            _harmony = new Harmony("nickpappas.locationbooster");

            // 1. Patch GetRandomZone
            var getRandomZoneMethod = AccessTools.Method(typeof(ZoneSystem), nameof(ZoneSystem.GetRandomZone));
            if (getRandomZoneMethod != null)
            {
                _harmony.Patch(getRandomZoneMethod,
                    prefix: new HarmonyMethod(typeof(BoosterPatches), nameof(BoosterPatches.GetRandomZonePrefix)));
            }

            // 2. Patch Breakdown Diagnostics
            _harmony.Patch(
                AccessTools.Method(typeof(WorldGenerator), nameof(WorldGenerator.GetBiome), new[] { typeof(Vector3) }),
                postfix: new HarmonyMethod(typeof(BoosterDiagnostics), nameof(BoosterDiagnostics.CaptureWrongBiome))
            );
            _harmony.Patch(
                AccessTools.Method(typeof(WorldGenerator), nameof(WorldGenerator.GetBiomeArea)),
                postfix: new HarmonyMethod(typeof(BoosterDiagnostics), nameof(BoosterDiagnostics.CaptureWrongBiomeArea))
            );

            // 3. Patch the Coroutines (The core logic)
            var zoneSystemType = typeof(ZoneSystem);
            var nestedTypes = zoneSystemType.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);

            foreach (var type in nestedTypes)
            {
                if (type.Name.Contains("GenerateLocationsTimeSliced"))
                {
                    if (BoosterPatches.PatchedTypes.Contains(type.FullName)) continue;

                    var method = AccessTools.Method(type, "MoveNext");

                    if (method != null)
                    {
                        bool hasInnerLoopLogic = BoosterPatches.ScanForInnerLoop(method);
                        bool hasOuterLoopLogic = BoosterPatches.ScanForOuterLoop(method);

                        if (hasOuterLoopLogic)
                        {
                            // Outer Loop (Coordinator): Injects logic to RESET the flag when a new location is picked.
                            _harmony.Patch(method, transpiler: new HarmonyMethod(typeof(BoosterPatches), nameof(BoosterPatches.OuterLoopTranspiler)));
                            BoosterDiagnostics.WriteLog($"[Booster] Patched Outer Loop (Reset Logic): {type.Name}");
                            BoosterPatches.PatchedTypes.Add(type.FullName);
                        }
                        else if (hasInnerLoopLogic)
                        {
                            // Inner Loop (Worker): Injects Kill Switch, Multipliers, and Logging.
                            _harmony.Patch(method, prefix: new HarmonyMethod(typeof(BoosterPatches), nameof(BoosterPatches.InnerLoopPrefix)));
                            _harmony.Patch(method, transpiler: new HarmonyMethod(typeof(BoosterPatches), nameof(BoosterPatches.InnerLoopTranspiler)));
                            BoosterDiagnostics.WriteLog($"[Booster] Patched Inner Loop (Multipliers + Kill Switch): {type.Name}");
                            BoosterPatches.PatchedTypes.Add(type.FullName);
                        }
                    }
                }
            }

            string target = string.IsNullOrWhiteSpace(FilterTarget.Value) ? "all locations" : FilterTarget.Value;
            BoosterDiagnostics.WriteLog($"[Booster] Initialized. Mode: {Mode.Value} (applies to: {target})");
        }

        void OnDestroy()
        {
            BoosterDiagnostics.Dispose();
        }
    }
}