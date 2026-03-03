#nullable disable
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System.Linq;
using System.Reflection;
using UnityEngine;
using System.IO;

namespace LocationBudgetBooster
{
    public enum BoosterMode
    {
        Vanilla,    // Off
        Filter,     // Sieve (Reject outside ring)
        Force,      // Donut (Math inside ring)
        //Survey,     // Legacy 1-point (Fastest)
        SurveyPlus  // High-Res Configurable set to 1x1 and heigh off to be exactly as legacy survey
    }

    [BepInPlugin("nickpappas.locationbooster", "Location Budget Booster", "0.3.5")]
    public class LocationBooster : BaseUnityPlugin
    {
        // 1. General
        public static ConfigEntry<BoosterMode> Mode;
        public static ConfigEntry<float> WorldRadius;

        // 2. Survey Strategy
        public static ConfigEntry<int> SurveyScanResolution;
        public static ConfigEntry<int> SurveyVisitLimit;
        public static ConfigEntry<bool> EnableAltitudeMapping;

        // 3. Relaxation (Smart Recovery)
        public static ConfigEntry<int> MaxRelaxationAttempts;
        public static ConfigEntry<float> RelaxationMagnitude;

        // 4. Performance Tuning
        public static ConfigEntry<int> OuterMultiplier;
        public static ConfigEntry<int> InnerMultiplier;

        // 5. Logging & Diagnostics
        public static ConfigEntry<bool> LogSuccesses;
        public static ConfigEntry<bool> WriteToFile;
        public static ConfigEntry<bool> DiagnosticMode;
        public static ConfigEntry<int> ProgressInterval;
        public static ConfigEntry<int> InnerProgressInterval;

        public static ManualLogSource Log;
        private static Harmony _harmony;
        private static FileSystemWatcher _configWatcher;

        void Awake()
        {
            Log = Logger;

            // --- SECTION 1: GENERAL ---
            Mode = Config.Bind("1 - General", "BoosterMode", BoosterMode.SurveyPlus,
                "Vanilla: Disabled.\nFilter: Rejects zones outside Min/Max distance.\nForce: Mathematically picks zones inside Min/Max distance.\nSurvey: Scans center of zone only (Fastest).\nSurveyPlus: Scans multiple points per zone based on Resolution.");

            WorldRadius = Config.Bind("1 - General", "WorldRadius", 10000f,
                "The max radius of the world in meters. Used for distance calculations.");

            // --- SECTION 2: SURVEY STRATEGY ---
            SurveyScanResolution = Config.Bind("2 - Survey Strategy", "ScanResolution", 3,
                "Grid width for SurveyPlus. 1=1x1 (Center), 3=3x3 (9 points), 5=5x5 (25 points). Odd numbers recommended. Higher values find more valid zones but take longer to start.");

            SurveyVisitLimit = Config.Bind("2 - Survey Strategy", "VisitLimit", 8,
                "How many times the generator can revisit a single candidate zone before forcing it to pick a different one. Prevents clustering.");

            EnableAltitudeMapping = Config.Bind("2 - Survey Strategy", "EnableAltitudeMapping", true,
                "If True, samples terrain height during the survey (~10s overhead). Allows 'Smart Relaxation' to instantly fix altitude failures. If False, relaxation is iterative (slower generation, faster startup).");

            // --- SECTION 3: RELAXATION ---
            MaxRelaxationAttempts = Config.Bind("3 - Relaxation", "MaxAttempts", 4,
                "How many times to relax requirements if a critical location fails to place. Set to 0 to disable relaxation.");

            RelaxationMagnitude = Config.Bind("3 - Relaxation", "RelaxationMagnitude", 0.05f,
                "Percentage to relax constraints per attempt (0.05 = 5%). E.g., a 200m altitude requirement becomes 190m.");

            // --- SECTION 4: PERFORMANCE ---
            OuterMultiplier = Config.Bind("4 - Performance", "OuterLoopMultiplier", 3,
                "Multiplies Valheim's budget for finding candidate Zones. Higher = higher chance to find a valid biome.");

            InnerMultiplier = Config.Bind("4 - Performance", "InnerLoopMultiplier", 5,
                "Multiplies placement attempts inside a valid Zone. Higher = higher chance to fit the location in rough terrain.");

            // --- SECTION 5: LOGGING ---
            LogSuccesses = Config.Bind("5 - Logging", "LogSuccess", true, "Log every successful placement to the console/file.");
            WriteToFile = Config.Bind("5 - Logging", "WriteToFile", true, "Write detailed diagnostics to BepInEx/LocationBooster.log.");
            DiagnosticMode = Config.Bind("5 - Logging", "DiagnosticMode", false, "Enable verbose debugging (spammy).");
            ProgressInterval = Config.Bind("5 - Logging", "ProgressInterval", 100000, "Log progress every N outer loop iterations.");
            InnerProgressInterval = Config.Bind("5 - Logging", "InnerProgressInterval", 0, "Log inner placement loop progress every N iterations. 0 to disable.");

            // Cleanup legacy config entries automatically
            // We remove 'TargetLocation' and 'DistanceFilter' section entirely
            var configKeys = Config.Keys.ToList();
            foreach (var key in configKeys)
            {
                if (key.Section == "DistanceFilter" || (key.Section == "Strategy" && key.Key == "TargetLocation"))
                    Config.Remove(key);
            }

            SetupConfigWatcher();
            BoosterDiagnostics.Initialize(Info.Metadata.Version.ToString());

            _harmony = new Harmony("nickpappas.locationbooster");

            // Patch Methods
            var getRandomZoneMethod = AccessTools.Method(typeof(ZoneSystem), nameof(ZoneSystem.GetRandomZone));
            if (getRandomZoneMethod != null)
                _harmony.Patch(getRandomZoneMethod, prefix: new HarmonyMethod(typeof(BoosterPatches), nameof(BoosterPatches.GetRandomZonePrefix)));

            _harmony.Patch(AccessTools.Method(typeof(WorldGenerator), nameof(WorldGenerator.GetBiome), new[] { typeof(Vector3) }),
                postfix: new HarmonyMethod(typeof(BoosterDiagnostics), nameof(BoosterDiagnostics.CaptureWrongBiome)));

            _harmony.Patch(AccessTools.Method(typeof(WorldGenerator), nameof(WorldGenerator.GetBiomeArea)),
                postfix: new HarmonyMethod(typeof(BoosterDiagnostics), nameof(BoosterDiagnostics.CaptureWrongBiomeArea)));

            PatchCoroutines();

            BoosterDiagnostics.WriteLog($"[Booster] Initialized. Mode: {Mode.Value}");
        }

        void PatchCoroutines()
        {
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
                        bool hasInner = BoosterPatches.ScanForInnerLoop(method);
                        bool hasOuter = BoosterPatches.ScanForOuterLoop(method);

                        if (hasOuter)
                        {
                            _harmony.Patch(method,
                                prefix: new HarmonyMethod(typeof(BoosterPatches), nameof(BoosterPatches.OuterLoopPrefix)),
                                postfix: new HarmonyMethod(typeof(BoosterPatches), nameof(BoosterPatches.OuterLoopPostfix)),
                                transpiler: new HarmonyMethod(typeof(BoosterPatches), nameof(BoosterPatches.OuterLoopTranspiler)));
                            BoosterPatches.PatchedTypes.Add(type.FullName);
                        }
                        else if (hasInner)
                        {
                            _harmony.Patch(method,
                                prefix: new HarmonyMethod(typeof(BoosterPatches), nameof(BoosterPatches.InnerLoopPrefix)),
                                transpiler: new HarmonyMethod(typeof(BoosterPatches), nameof(BoosterPatches.InnerLoopTranspiler)));
                            BoosterPatches.PatchedTypes.Add(type.FullName);
                        }
                    }
                }
            }
        }

        private void SetupConfigWatcher()
        {
            _configWatcher = new FileSystemWatcher(Paths.ConfigPath, Path.GetFileName(Config.ConfigFilePath))
            {
                NotifyFilter = NotifyFilters.LastWrite,
                EnableRaisingEvents = true,
            };
            _configWatcher.Changed += OnConfigChanged;
        }

        private void OnConfigChanged(object sender, FileSystemEventArgs e)
        {
            if (e.FullPath != Config.ConfigFilePath) return;
            Logger.LogInfo("Configuration file has been modified. Reloading settings.");
            Config.Reload();
        }


        void OnDestroy()
        {
            _configWatcher?.Dispose();
            BoosterDiagnostics.Dispose();
        }
    }
}