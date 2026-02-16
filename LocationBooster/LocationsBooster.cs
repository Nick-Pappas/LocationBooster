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

        // NEW: Enum based configuration
        public static ConfigEntry<BoosterMode> Mode;

        // TEMPORARY: Target filter
        public static ConfigEntry<string> FilterTarget; // e.g., "Hildir_cave"
        public static ConfigEntry<float> HildirCaveMinDistance;
        public static ConfigEntry<float> HildirCaveMaxDistance;
        public static ConfigEntry<float> WorldRadius;

        public static ManualLogSource Log;
        private static Harmony _harmony;

        void Awake()
        {
            Log = Logger;

            // General Settings
            OuterMultiplier = Config.Bind("General", "OuterLoopMultiplier", 50, "Multiplies the budget for finding candidate Zones.");
            InnerMultiplier = Config.Bind("General", "InnerLoopMultiplier", 20, "Multiplies placement attempts inside a valid Zone.");

            // Mode Settings
            Mode = Config.Bind("Strategy", "BoosterMode", BoosterMode.Vanilla, "Vanilla: Default behavior. Filter: Rejects zones outside range (fast). Force: Generates zones inside range (math).");
            FilterTarget = Config.Bind("Strategy", "TargetLocation", "Hildir_cave", "The prefab name to apply Filter/Force modes to. Leave empty to apply to nothing.");

            // Distance Settings
            HildirCaveMinDistance = Config.Bind("DistanceFilter", "HildirCaveMinDistance", 0.1f, "Min distance fraction for Hildir_cave.");
            HildirCaveMaxDistance = Config.Bind("DistanceFilter", "HildirCaveMaxDistance", 0.8f, "Max distance fraction for Hildir_cave.");
            WorldRadius = Config.Bind("DistanceFilter", "WorldRadius", 10000f, "World radius in meters.");

            // Logging
            LogSuccesses = Config.Bind("Logging", "LogSuccess", true, "Log successful placements.");
            WriteToFile = Config.Bind("Logging", "WriteToFile", true, "Write diagnostics to a log file.");
            ProgressInterval = Config.Bind("Logging", "ProgressInterval", 100000, "Log progress every N outer loop iterations.");
            DiagnosticMode = Config.Bind("Logging", "DiagnosticMode", false, "Enable verbose debugging.");

            BoosterDiagnostics.Initialize(Info.Metadata.Version.ToString());

            _harmony = new Harmony("nickpappas.locationbooster");

            // Patch GetRandomZone
            var getRandomZoneMethod = AccessTools.Method(typeof(ZoneSystem), nameof(ZoneSystem.GetRandomZone));
            if (getRandomZoneMethod != null)
            {
                _harmony.Patch(getRandomZoneMethod,
                    prefix: new HarmonyMethod(typeof(BoosterPatches), nameof(BoosterPatches.GetRandomZonePrefix)));
                BoosterDiagnostics.WriteLog("[Booster] Patched GetRandomZone.");
            }

            // Patch breakdown capture methods
            _harmony.Patch(
                AccessTools.Method(typeof(WorldGenerator), nameof(WorldGenerator.GetBiome), new[] { typeof(Vector3) }),
                postfix: new HarmonyMethod(typeof(BoosterDiagnostics), nameof(BoosterDiagnostics.CaptureWrongBiome))
            );
            _harmony.Patch(
                AccessTools.Method(typeof(WorldGenerator), nameof(WorldGenerator.GetBiomeArea)),
                postfix: new HarmonyMethod(typeof(BoosterDiagnostics), nameof(BoosterDiagnostics.CaptureWrongBiomeArea))
            );

            // Transpiler Patches
            var zoneSystemType = typeof(ZoneSystem);
            var nestedTypes = zoneSystemType.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);

            foreach (var type in nestedTypes)
            {
                if (type.Name.Contains("GenerateLocationsTimeSliced"))
                {
                    if (BoosterPatches.PatchedTypes.Contains(type.FullName)) continue;

                    var method = AccessTools.Method(type, "MoveNext");
                    if (method != null && BoosterPatches.ScanForTarget(method))
                    {
                        _harmony.Patch(method, transpiler: new HarmonyMethod(typeof(BoosterPatches), nameof(BoosterPatches.BoosterTranspiler)));
                        BoosterPatches.PatchedTypes.Add(type.FullName);
                    }
                }
            }

            BoosterDiagnostics.WriteLog($"[Booster] Mode: {Mode.Value}. Target: {FilterTarget.Value}.");
        }

        void OnDestroy()
        {
            BoosterDiagnostics.Dispose();
        }
    }
}