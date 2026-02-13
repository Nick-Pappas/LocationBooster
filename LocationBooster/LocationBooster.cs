#nullable disable
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;
using static ZoneSystem;

namespace LocationBudgetBooster
{
    /// <summary>
    /// Multiplies Valheim's location placement budgets to improve success rates in custom terrain.
    /// Targets the two-phase placement system: Zone finding (outer loop) and pinpoint placement (inner loop).
    /// </summary>
    [BepInPlugin("nickpappas.locationbooster", "Location Budget Booster", "0.3.1")]
    public class LocationBooster : BaseUnityPlugin
    {
        public static ConfigEntry<int> OuterMultiplier;
        public static ConfigEntry<int> InnerMultiplier;
        public static ConfigEntry<bool> LogSuccesses;
        public static ConfigEntry<bool> WriteToFile;
        public static ConfigEntry<int> ProgressInterval;
        public static ConfigEntry<bool> DiagnosticMode;  // Extra verbose logging for debugging

        // TEMPORARY: Hardcoded Hildir_cave distance filter for testing
        public static ConfigEntry<float> HildirCaveMinDistance;
        public static ConfigEntry<float> HildirCaveMaxDistance;
        public static ConfigEntry<bool> EnableDistanceFilter;
        public static ConfigEntry<float> WorldRadius;

        // Track current location being placed for GetRandomZone filter
        private static ZoneLocation _currentLocationForFilter = null;
        //log the ranges to fing debug this thing
        private static HashSet<string> _loggedRanges = new HashSet<string>();

        // Recursion guard for GetRandomZonePrefix
        private static bool _insideGetRandomZone = false;

        public static ManualLogSource Log;
        private static Harmony _harmony;
        private static StreamWriter _logWriter;

        // Reflection Caching: Fields are captured per state machine type (e.g., d__46 vs d__48)
        // because different coroutine implementations may have different field layouts
        public static Dictionary<Type, FieldInfo> LocationFields = new Dictionary<Type, FieldInfo>();
        public static Dictionary<Type, FieldInfo> LimitFields = new Dictionary<Type, FieldInfo>();      // The budget limit (<attempts>)
        public static Dictionary<Type, FieldInfo> CounterFields = new Dictionary<Type, FieldInfo>();    // The loop counter (<i>)
        public static Dictionary<Type, FieldInfo> PlacedFields = new Dictionary<Type, FieldInfo>();     // Current placement count
        public static Dictionary<string, FieldInfo> ErrorFields = new Dictionary<string, FieldInfo>();  // Error counters (errorBiome, etc.)

        // Shadow Counters: Track error counts as long to prevent int32 overflow
        // Key: instance GetHashCode(), Value: dictionary of field name -> count
        private static Dictionary<int, Dictionary<string, long>> ShadowCounters = new Dictionary<int, Dictionary<string, long>>();

        private static HashSet<string> _patchedTypes = new HashSet<string>();
        private static HashSet<string> _transpiledMethods = new HashSet<string>();

        void Awake()
        {
            Log = Logger;

            OuterMultiplier = Config.Bind("General", "OuterLoopMultiplier", 50, "Multiplies the budget for finding candidate Zones.");
            InnerMultiplier = Config.Bind("General", "InnerLoopMultiplier", 20, "Multiplies placement attempts inside a valid Zone.");
            LogSuccesses = Config.Bind("Logging", "LogSuccess", true, "Log successful placements. Disable to reduce log spam.");
            WriteToFile = Config.Bind("Logging", "WriteToFile", true, "Write diagnostics to a separate log file in BepInEx/LogOutput.log");
            ProgressInterval = Config.Bind("Logging", "ProgressInterval", 100000, "Log progress every N outer loop iterations. Set to 0 to disable.");
            DiagnosticMode = Config.Bind("Logging", "DiagnosticMode", false, "Enable extra verbose transpiler logging for debugging. Very spammy.");

            // TEMPORARY: Testing distance filter with Hildir_cave
            EnableDistanceFilter = Config.Bind("DistanceFilter", "EnableDistanceFilter", false, "Enable distance-based grid filtering (EXPERIMENTAL)");
            HildirCaveMinDistance = Config.Bind("DistanceFilter", "HildirCaveMinDistance", 0.1f, "Min distance fraction for Hildir_cave (0.1 = 10% of world radius)");
            HildirCaveMaxDistance = Config.Bind("DistanceFilter", "HildirCaveMaxDistance", 0.8f, "Max distance fraction for Hildir_cave (0.8 = 80% of world radius)");
            WorldRadius = Config.Bind("DistanceFilter", "WorldRadius", 10000f, "World radius in meters. Vanilla=10000, Better Continents users should set to their configured world size.");

            WriteLog($"[Booster {Info.Metadata.Version}] Initializing. Multipliers: {OuterMultiplier.Value}x, {InnerMultiplier.Value}x.");

            if (WriteToFile.Value)
            {
                try
                {
                    string logPath = Path.Combine(Paths.BepInExRootPath, "LocationBooster.log");
                    _logWriter = new StreamWriter(logPath, false) { AutoFlush = true };
                    _logWriter.WriteLine($"=== Location Budget Booster v{Info.Metadata.Version} ===");
                    _logWriter.WriteLine($"Started: {DateTime.Now}");
                    _logWriter.WriteLine($"Multipliers: {OuterMultiplier.Value}x outer, {InnerMultiplier.Value}x inner");
                    _logWriter.WriteLine("");
                    WriteLog($"[Booster] Writing diagnostics to: {logPath}");
                }
                catch (Exception ex)
                {
                    Log.LogError($"[Booster] Failed to create log file: {ex.Message}");
                }
            }

            _harmony = new Harmony("nickpappas.locationbooster");

            // Patch GetRandomZone for distance filtering
            if (EnableDistanceFilter.Value)
            {
                var getRandomZoneMethod = AccessTools.Method(typeof(ZoneSystem), nameof(ZoneSystem.GetRandomZone));
                if (getRandomZoneMethod != null)
                {
                    _harmony.Patch(getRandomZoneMethod,
                        prefix: new HarmonyMethod(typeof(LocationBooster), nameof(GetRandomZonePrefix)));
                    WriteLog("[Booster] Patched GetRandomZone for distance filtering");
                }
            }

            var zoneSystemType = typeof(ZoneSystem);
            var nestedTypes = zoneSystemType.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);

            foreach (var type in nestedTypes)
            {
                if (type.Name.Contains("GenerateLocationsTimeSliced"))
                {
                    if (_patchedTypes.Contains(type.FullName)) continue;

                    var method = AccessTools.Method(type, "MoveNext");
                    if (method != null && ScanForTarget(method))
                    {
                        WriteLog($"[Booster] Patching target: {type.Name}");
                        _harmony.Patch(method, transpiler: new HarmonyMethod(typeof(LocationBooster), nameof(BoosterTranspiler)));
                        _patchedTypes.Add(type.FullName);
                    }
                }
            }

            WriteLog($"[Booster] Patching complete. Successfully patched {_patchedTypes.Count} state machine(s).");
        }

        void OnDestroy()
        {
            _logWriter?.Close();
        }

        /// <summary>
        /// Pre-scan to verify target method contains the budget constants we need to patch.
        /// Prevents blind patching attempts that would fail.
        /// </summary>
        private static bool ScanForTarget(MethodInfo method)
        {
            try
            {
                var instructions = PatchProcessor.GetOriginalInstructions(method);
                return instructions.Any(x => x.opcode == OpCodes.Ldc_I4 && (x.operand is int v && (v == 100000 || v == 200000)));
            }
            catch (Exception ex)
            {
                Log.LogWarning($"Pre-scan failed for {method.Name}: {ex.Message}");
                return false;
            }
        }

        public static long GetVal(object instance, string fieldName)
        {
            int instanceHash = instance.GetHashCode();

            // Read from shadow counters (accurate long values)
            if (ShadowCounters.TryGetValue(instanceHash, out var counters))
            {
                if (counters.TryGetValue(fieldName, out var count))
                    return count;
            }

            // Fallback: read from game field (may overflow)
            if (ErrorFields.TryGetValue(fieldName, out var field))
            {
                try { return Convert.ToInt64(field.GetValue(instance)); } catch { }
            }
            return 0;
        }

        /// <summary>
        /// Increment shadow counter to track error counts beyond int32 limits.
        /// Called by injected IL after every game error counter increment.
        /// </summary>
        public static void IncrementShadow(object instance, string fieldName)
        {
            int instanceHash = instance.GetHashCode();

            if (!ShadowCounters.ContainsKey(instanceHash))
                ShadowCounters[instanceHash] = new Dictionary<string, long>();

            if (!ShadowCounters[instanceHash].ContainsKey(fieldName))
                ShadowCounters[instanceHash][fieldName] = 0;

            ShadowCounters[instanceHash][fieldName]++;
        }

        public static ZoneLocation GetLocation(object instance)
        {
            var type = instance.GetType();
            if (LocationFields.TryGetValue(type, out var field))
            {
                return field.GetValue(instance) as ZoneLocation;
            }
            return null;
        }

        private static void WriteLog(string message, LogLevel level = LogLevel.Info)
        {
            Log.Log(level, message);
            if (WriteToFile.Value && _logWriter != null)
            {
                _logWriter.WriteLine($"[{level}] {message}");
            }
        }

        // Heartbeat tracking: last reported progress per instance
        private static Dictionary<int, long> _lastReported = new Dictionary<int, long>();

        // World generation baseline timestamp
        private static bool _baselineLogged = false;

        private static int _filterCallCount = 0;
        private static string _lastFilteredLocation = "";

        /// <summary>
        /// Prefix patch for GetRandomZone - retries until we get a zone in the valid distance ring
        /// </summary>
        public static bool GetRandomZonePrefix(ref Vector2i __result, float range)
        {
            // TEMPORARY DIAGNOSTIC
            if (_currentLocationForFilter != null)
            {
                string name = _currentLocationForFilter.m_prefabName;
                // Log once per location type to avoid spam, or checking specific ones
                if (name == "StoneTowerRuins04" || name == "Dragonqueen" || name == "Hildir_cave")
                {
                    // Use a static set to log only the first call for each prefab
                    if (!_loggedRanges.Contains(name))
                    {
                        _loggedRanges.Add(name);
                        WriteLog($"[RANGE CHECK] {name} is calling GetRandomZone with range: {range:F0}m");
                    }
                }
            }
            // Recursion guard - prevent infinite loop
            if (_insideGetRandomZone) return true;

            if (!EnableDistanceFilter.Value || _currentLocationForFilter == null) return true;
            if (_currentLocationForFilter.m_prefabName != "Hildir_cave") return true;

            float minDist = _currentLocationForFilter.m_minDistance + 64f;
            float maxDist = _currentLocationForFilter.m_maxDistance - 64f;

            // DIAGNOSTIC: Log once per location change
            if (DiagnosticMode.Value && _lastFilteredLocation != _currentLocationForFilter.m_prefabName)
            {
                _lastFilteredLocation = _currentLocationForFilter.m_prefabName;
                WriteLog($"[DistFilter] Starting filter for {_currentLocationForFilter.m_prefabName}");
                WriteLog($"[DistFilter] Zone filter range: {minDist:F0}m - {maxDist:F0}m (Live: {_currentLocationForFilter.m_minDistance:F0}+64 to {_currentLocationForFilter.m_maxDistance:F0}-64)");
            }

            int attempts = 0;
            int maxAttempts = 1000;

            _insideGetRandomZone = true;
            try
            {
                while (attempts++ < maxAttempts)
                {
                    Vector2i zone = ZoneSystem.GetRandomZone(range);
                    Vector3 gridPos = ZoneSystem.GetZonePos(zone);
                    float distFromOrigin = gridPos.magnitude;

                    if (distFromOrigin >= minDist && distFromOrigin <= maxDist)
                    {
                        __result = zone;
                        if (DiagnosticMode.Value && _filterCallCount++ < 10)
                            WriteLog($"[DistFilter] Accepted zone {zone} at distance {distFromOrigin:F0}m (attempt {attempts})");
                        return false; // Skip original, use our result
                    }
                }

                // Failed to find valid zone after 1000 attempts, log and use last attempt
                WriteLog($"[DistFilter] WARNING: Failed to find valid zone after {maxAttempts} attempts");
                return true;
            }
            finally
            {
                _insideGetRandomZone = false;
            }
        }

        /// <summary>
        /// Progress heartbeat logger. Called after every counter increment, but only logs every N iterations.
        /// Shows intermediate error statistics to help diagnose slow placements.
        /// </summary>
        public static void LogProgress(object instance)
        {
            int progressInterval = ProgressInterval.Value;
            if (progressInterval <= 0) return;

            var loc = GetLocation(instance);
            var type = instance.GetType();

            if (!CounterFields.TryGetValue(type, out var field))
            {
                if (DiagnosticMode.Value) WriteLog($"[DIAG] LogProgress: CounterFields not found for {type.Name}");
                return;
            }
            if (!LimitFields.TryGetValue(type, out var limitField))
            {
                if (DiagnosticMode.Value) WriteLog($"[DIAG] LogProgress: LimitFields not found for {type.Name}");
                return;
            }
            if (!PlacedFields.TryGetValue(type, out var placedField))
            {
                if (DiagnosticMode.Value) WriteLog($"[DIAG] LogProgress: PlacedFields not found for {type.Name}");
                return;
            }

            long current = Convert.ToInt64(field.GetValue(instance));
            long limit = Convert.ToInt64(limitField.GetValue(instance));
            int placed = Convert.ToInt32(placedField.GetValue(instance));

            // Only report at interval boundaries, not before first interval
            if (current < progressInterval) return;
            if (current % progressInterval != 0) return;

            if (DiagnosticMode.Value)
                WriteLog($"[DIAG] LogProgress called: field={field.Name}, current={current}, limit={limit}, placed={placed}");

            double pct = (double)current / limit * 100.0;
            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            WriteLog($"[Progress] {loc?.m_prefabName ?? "Unknown"}: {current:N0}/{limit:N0} ({pct:F1}%) - Placed: {placed}/{loc?.m_quantity ?? 0} (Dist: {loc?.m_minDistance:F0}-{loc?.m_maxDistance:F0}) [{timestamp}]");

            // Show intermediate error statistics so user can diagnose issues
            long errBiome = GetVal(instance, "errorBiome");
            long errAlt = GetVal(instance, "errorAlt");
            long errTerr = GetVal(instance, "errorTerrainDelta");
            long errBiomeArea = GetVal(instance, "errorBiomeArea");
            long errDist = GetVal(instance, "errorCenterDistance");

            WriteLog($"   Top failures so far:");
            if (errBiome > 0) WriteLog($"      - Wrong biome          : {errBiome,12:N0}");
            if (errAlt > 0) WriteLog($"      - Wrong altitude       : {errAlt,12:N0}");
            if (errTerr > 0) WriteLog($"      - Terrain too steep    : {errTerr,12:N0}");
            if (errBiomeArea > 0) WriteLog($"      - Grid outside world   : {errBiomeArea,12:N0}");
            if (errDist > 0) WriteLog($"      - Outside min/max dist : {errDist,12:N0}");
        }

        /// <summary>
        /// Unified diagnostic logger for both success and failure cases.
        /// Extracts placement stats, budget usage, and error breakdowns.
        /// </summary>
        public static void LogDiagnostics(object instance, bool isSuccess)
        {
            // Log baseline timestamp on first location
            if (!_baselineLogged)
            {
                _baselineLogged = true;
                WriteLog($"[Booster] World generation started [{DateTime.Now:HH:mm:ss}]");
            }

            // Apply config filter for success logging
            if (isSuccess && !LogSuccesses.Value) return;

            var loc = GetLocation(instance);
            string locationName = loc?.m_prefabName ?? "Unknown";
            if (locationName == "Unknown") return;

            var type = instance.GetType();

            // Get Budget Data
            long limit = 0;
            if (LimitFields.TryGetValue(type, out var limitField))
                limit = Convert.ToInt64(limitField.GetValue(instance));

            long actualIterations = 0;
            if (CounterFields.TryGetValue(type, out var countField))
                actualIterations = Convert.ToInt64(countField.GetValue(instance));

            // Get Placement Progress
            int placedSoFar = 0;
            if (PlacedFields.TryGetValue(type, out var placedField))
                placedSoFar = (int)placedField.GetValue(instance);

            // For Success: Only log when we've finished the entire quantity for this location type
            if (isSuccess)
            {
                // Note: On success hook, placedSoFar hasn't incremented yet, so +1 matches quantity
                if (placedSoFar + 1 < loc.m_quantity) return;
                placedSoFar++; // Adjust for display
            }

            // Calculations
            double costPct = limit > 0 ? (double)actualIterations * 100.0 / limit : 0;
            string status = isSuccess ? "COMPLETE" : "FAILURE";
            LogLevel level = isSuccess ? LogLevel.Info : LogLevel.Warning;
            string timestamp = DateTime.Now.ToString("HH:mm:ss");

            WriteLog($"[Booster] {status}: {locationName} (Placed {placedSoFar}/{loc.m_quantity}). Cost: {actualIterations:N0}/{limit:N0} ({costPct:F2}%) (Dist: {loc?.m_minDistance:F0}-{loc?.m_maxDistance:F0}) [{timestamp}]", level);

            // Phase 1 Diagnostic Dump (Zone Finding)
            long errBiomeArea = GetVal(instance, "errorBiomeArea");
            long errZoneOccupied = GetVal(instance, "errorLocationInZone");
            long phase1Failures = errBiomeArea + errZoneOccupied;

            // Approximation: Iterations that passed Phase 1 zone checks
            // Not perfectly accurate if loop exits early, but close enough for diagnostics
            long successfulZoneFinds = actualIterations - phase1Failures;

            // Phase 2 Diagnostic Dump (Pinpoint Placement)
            long errBiome = GetVal(instance, "errorBiome");
            long errAlt = GetVal(instance, "errorAlt");
            long errDist = GetVal(instance, "errorCenterDistance");
            long errTerr = GetVal(instance, "errorTerrainDelta");
            long errSim = GetVal(instance, "errorSimilar");
            long errVeg = GetVal(instance, "errorVegetation");
            long errFor = GetVal(instance, "errorForest");
            long errNotSim = GetVal(instance, "errorNotSimilar");

            long totalPhase2Failures = errBiome + errAlt + errDist + errTerr + errSim + errVeg + errFor + errNotSim;

            WriteLog($"   Phase 1 (Zone Find): Found {successfulZoneFinds:N0} valid grids out of {actualIterations:N0} checks. Things failed because:", level);
            if (errBiomeArea > 0) WriteLog($"      - Grid checked for a biome was not inside the world's disk: {errBiomeArea:N0}", level);
            if (errZoneOccupied > 0) WriteLog($"      - Grid already had a location in it:   {errZoneOccupied:N0}", level);

            WriteLog($"   Phase 2 (Placement): Rejected {totalPhase2Failures:N0} spots inside those grids for the following reasons:", level);

            void PrintErr(string name, long count)
            {
                if (count > 0) WriteLog($"      - {name,-70}: {count,10:N0}", level);
            }

            PrintErr("Point was in wrong Biome", errBiome);
            PrintErr("Point altitude was not right (either too high or too low)", errAlt);
            PrintErr("Point was outside the Min/Max dist", errDist);
            PrintErr("Terrain too steep/uneven", errTerr);
            PrintErr("Too close to a similar location", errSim);
            PrintErr("Too far from required neighbor", errNotSim);
            PrintErr("Vegetation blocked placement", errVeg);
            PrintErr("Forest density was either too high or too low for what was needed", errFor);

            // Add separator for human readability
            WriteLog("────────────────────────────────────────────────────────", level);

            // Clear shadow counters after logging to prevent bleed to next location
            int instanceHash = instance.GetHashCode();
            ShadowCounters.Remove(instanceHash);
        }

        public static void ReportSuccess(object instance) => LogDiagnostics(instance, true);
        public static void ReportFailure(object instance) => LogDiagnostics(instance, false);

        /* BACKUP HEARTBEAT: Prefix Patch Method
         * If IL injection fails, uncomment this and patch it manually
         * 
        private static int _callCounter = 0;
        private static string _lastLocation = "";

        [HarmonyPrefix]
        [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.IsZoneGenerated))] // Or whatever inner method fires frequently
        static void HeartbeatPrefix(ZoneSystem __instance)
        {
            // Track progress by counting method calls
            // This is less precise than IL injection but guaranteed to work
            if (++_callCounter % 10000 == 0)
            {
                WriteLog($"[Heartbeat] Processing... {_callCounter:N0} checks completed");
            }
        }
        */

        /// <summary>
        /// IL Transpiler that:
        /// 1. Multiplies the 100k/200k outer loop budgets
        /// 2. Multiplies the 20-attempt inner loop budget
        /// 3. Captures reflection metadata for runtime diagnostics
        /// 4. Injects success/failure hooks
        /// 5. Injects progress heartbeat logging
        /// </summary>
        public static IEnumerable<CodeInstruction> BoosterTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase original, ILGenerator generator)
        {
            var codes = instructions.ToList();
            int outerMult = OuterMultiplier.Value;
            int innerMult = InnerMultiplier.Value;
            string lastLogString = "";
            Type currentType = original.DeclaringType;

            string methodKey = $"{currentType.Name}::{original.Name}";
            bool verbose = !_transpiledMethods.Contains(methodKey);

            if (verbose)
            {
                WriteLog($"--- Transpiling {codes.Count} instructions for {currentType.Name} ---");
                _transpiledMethods.Add(methodKey);
            }

            // PRE-PASS: Find GetRandomZone call to inject location context
            int getRandomZoneIndex = -1;
            if (EnableDistanceFilter.Value)
            {
                for (int i = 0; i < codes.Count; i++)
                {
                    if (codes[i].opcode == OpCodes.Call && codes[i].operand is MethodInfo mi && mi.Name == "GetRandomZone")
                    {
                        getRandomZoneIndex = i;
                        if (verbose) WriteLog($"   [DistFilter] Found GetRandomZone at IL_{i:D4}");
                        break;
                    }
                }
            }

            FieldInfo zoneIDField = null;

            for (int i = 0; i < codes.Count; i++)
            {
                var opcode = codes[i].opcode;
                var operand = codes[i].operand;

                // 1. Capture Location Field
                // The ZoneLocation instance contains all configuration for this location type
                if (opcode == OpCodes.Ldfld && operand is FieldInfo fi && fi.FieldType == typeof(ZoneLocation))
                {
                    if (!LocationFields.ContainsKey(currentType)) LocationFields[currentType] = fi;
                }

                // 2. Capture Placed Field
                // This field tracks how many instances of this location have been successfully placed so far
                if (opcode == OpCodes.Call && operand is MethodInfo mi && mi.Name == "CountNrOfLocation")
                {
                    if (i + 1 < codes.Count && codes[i + 1].opcode == OpCodes.Stfld)
                    {
                        var pField = codes[i + 1].operand as FieldInfo;
                        if (!PlacedFields.ContainsKey(currentType))
                        {
                            PlacedFields[currentType] = pField;
                            if (verbose) WriteLog($"   [Reflect] Captured Placed Count: {pField.Name}");
                        }
                    }
                }

                // 3. Outer Loop Boost & Budget Field Capture
                // Multiply the 100k/200k budget constants and capture the field they're stored in
                if (opcode == OpCodes.Ldc_I4 && operand is int val && (val == 100000 || val == 200000))
                {
                    if (verbose) WriteLog($"   [Outer] Boosting {val} at IL_{i:D4}");
                    yield return codes[i];
                    yield return new CodeInstruction(OpCodes.Ldc_I4, outerMult);
                    yield return new CodeInstruction(OpCodes.Mul);

                    // Capture the <attempts> field (works for both 100k and 200k assignments)
                    if (i + 1 < codes.Count && codes[i + 1].opcode == OpCodes.Stfld)
                    {
                        var limitField = codes[i + 1].operand as FieldInfo;
                        if (!LimitFields.ContainsKey(currentType)) // Only capture once per type
                        {
                            LimitFields[currentType] = limitField;
                            if (verbose) WriteLog($"   [Reflect] Captured Budget Limit: {limitField.Name}");
                        }
                    }
                    continue;
                }

                // 4. Capture Loop Counter (Post-Hoc Pattern Analysis)
                // FRAGILE: Assumes IL pattern: ldarg.0, ldfld <i>, ldarg.0, ldfld <attempts>, bge/blt
                // The loop counter 'i' appears two instructions before the budget limit check.
                // If the compiler reorders IL, this capture will fail silently.
                if (LimitFields.ContainsKey(currentType) && opcode == OpCodes.Ldfld && operand is FieldInfo fi2 && fi2 == LimitFields[currentType])
                {
                    // This instruction loads <attempts>. 
                    // The previous instruction loaded 'this' (ldarg.0).
                    // The one BEFORE that loaded <i> (ldfld).
                    // So at i-2 is ldfld <i>
                    if (i >= 2 && codes[i - 2].opcode == OpCodes.Ldfld)
                    {
                        var counterField = codes[i - 2].operand as FieldInfo;
                        if (!CounterFields.ContainsKey(currentType))
                        {
                            CounterFields[currentType] = counterField;
                            if (verbose) WriteLog($"   [Reflect] Captured Loop Counter: {counterField.Name}");
                        }
                    }
                }

                // 5. Distance Filter - Inject location context capture before GetRandomZone
                if (EnableDistanceFilter.Value && getRandomZoneIndex >= 0 && i == getRandomZoneIndex)
                {
                    // Inject: LocationBooster._currentLocationForFilter = this.location
                    // Pattern: Before GetRandomZone call, we need to capture the location
                    // The location field should be loaded somewhere before this call
                    if (LocationFields.ContainsKey(currentType))
                    {
                        yield return new CodeInstruction(OpCodes.Ldarg_0);
                        yield return new CodeInstruction(OpCodes.Ldfld, LocationFields[currentType]);
                        yield return new CodeInstruction(OpCodes.Stsfld, AccessTools.Field(typeof(LocationBooster), nameof(_currentLocationForFilter)));
                        if (verbose) WriteLog($"   [DistFilter] Injected location context capture at IL_{i:D4} (field={LocationFields[currentType].Name})");
                    }
                    else
                    {
                        if (verbose) WriteLog($"   [DistFilter] WARNING: Cannot inject at IL_{i:D4} - LocationFields not captured yet!");
                    }
                }

                // 6. Progress Heartbeat Injection
                // Inject progress logging after loop counter store (no branching, method handles modulo check)
                if (ProgressInterval.Value > 0 && CounterFields.ContainsKey(currentType) &&
                    opcode == OpCodes.Stfld && operand is FieldInfo fi3 && fi3 == CounterFields[currentType])
                {
                    if (DiagnosticMode.Value || verbose)
                        WriteLog($"   [Heartbeat] Matched stfld {fi3.Name} at IL_{i:D4}, injecting LogProgress");

                    yield return codes[i]; // Store the counter first

                    // Call progress logger unconditionally (it handles the modulo check internally)
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(LocationBooster), nameof(LogProgress)));

                    if (verbose) WriteLog($"   [Heartbeat] Injected progress logging at IL_{i:D4}");
                    continue;
                }

                // DIAGNOSTIC: Log ALL stfld instructions we encounter if diagnostic mode enabled
                if (DiagnosticMode.Value && opcode == OpCodes.Stfld && operand is FieldInfo diagField)
                {
                    if (diagField.Name.Contains("<i>") || diagField.Name.Contains("attempt") || diagField.Name.Contains("counter"))
                        WriteLog($"   [DIAG] Found stfld {diagField.Name} at IL_{i:D4}");
                }

                // 7. Inner Loop Boost
                // Multiply the 20-attempt inner loop budget
                // Pattern: Ldfld <j> → Ldc_I4_S 20 → Blt/Bge (loop condition)
                if (opcode == OpCodes.Ldc_I4_S && Convert.ToInt32(operand) == 20)
                {
                    bool prevIsLoad = i > 0 && codes[i - 1].opcode == OpCodes.Ldfld;
                    bool nextIsBranch = i + 1 < codes.Count && (
                        codes[i + 1].opcode == OpCodes.Blt || codes[i + 1].opcode == OpCodes.Blt_S ||
                        codes[i + 1].opcode == OpCodes.Ble || codes[i + 1].opcode == OpCodes.Ble_S ||
                        codes[i + 1].opcode == OpCodes.Bge || codes[i + 1].opcode == OpCodes.Bge_S
                    );

                    if (prevIsLoad && nextIsBranch)
                    {
                        if (verbose) WriteLog($"   [Inner] Boosting loop count 20 at IL_{i:D4}");
                        yield return new CodeInstruction(OpCodes.Ldc_I4, 20 * innerMult);
                        continue;
                    }
                }

                // 8. Hook Success
                // Inject our diagnostic logger after every successful RegisterLocation call
                if (opcode == OpCodes.Call && operand is MethodInfo miReg && miReg.Name == "RegisterLocation")
                {
                    yield return codes[i];
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(LocationBooster), nameof(ReportSuccess)));
                    continue;
                }

                // 9. Hook Failure
                // Inject our diagnostic logger when "Failed to place all" is logged
                if (opcode == OpCodes.Ldstr && operand is string str)
                {
                    lastLogString = str.Trim();
                    if (str.Contains("Failed to place all"))
                    {
                        if (verbose) WriteLog($"   [Hook] Injecting ReportFailure at IL_{i:D4}");
                        yield return new CodeInstruction(OpCodes.Ldarg_0);
                        yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(LocationBooster), nameof(ReportFailure)));
                    }
                }

                // 10. Map Error Fields & Inject Shadow Counter Tracking
                // Correlate error counter fields with their log strings (e.g., "errorBiome" → field reference)
                // FRAGILE: Assumes compiler emits Ldstr immediately before Ldflda for error logging.
                if (opcode == OpCodes.Ldflda && !string.IsNullOrEmpty(lastLogString) && lastLogString.StartsWith("error"))
                {
                    var field = operand as FieldInfo;
                    if (field != null && !ErrorFields.ContainsKey(lastLogString))
                    {
                        ErrorFields[lastLogString] = field;
                        if (verbose) WriteLog($"   [Reflect] Mapped error field '{lastLogString}' → {field.Name}");
                    }
                    lastLogString = "";
                }

                // 11. Inject Shadow Counter Increment After Error Field Stores
                // Pattern: errorField++ becomes: ldfld errorX, ldc.i4.1, add, stfld errorX
                // We inject after the stfld to track the increment in our long counter
                // Match by field name pattern since mapping may not be complete yet
                if (opcode == OpCodes.Stfld && operand is FieldInfo stField)
                {
                    // Check if this is an error field by name pattern
                    if (stField.Name.StartsWith("<error") && stField.Name.Contains(">"))
                    {
                        // Only inject if this is an increment (preceded by 'add'), not initialization
                        bool isIncrement = i > 0 && codes[i - 1].opcode == OpCodes.Add;

                        if (isIncrement)
                        {
                            // Extract the error field name (e.g., "<errorBiome>5__6" → "errorBiome")
                            string fieldName = stField.Name;
                            int start = fieldName.IndexOf('<') + 1;
                            int end = fieldName.IndexOf('>');
                            if (start > 0 && end > start)
                            {
                                string errorName = fieldName.Substring(start, end - start);

                                // Store the incremented value first
                                yield return codes[i];

                                // Inject shadow counter increment
                                yield return new CodeInstruction(OpCodes.Ldarg_0);  // Load 'this'
                                yield return new CodeInstruction(OpCodes.Ldstr, errorName);  // Load field name
                                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(LocationBooster), nameof(IncrementShadow)));

                                if (verbose) WriteLog($"   [Shadow] Injected overflow tracking for '{errorName}' at IL_{i:D4}");
                                continue;
                            }
                        }
                    }
                }

                yield return codes[i];
            }

            // Post-transpile validation: Verify we captured critical fields
            if (!LimitFields.ContainsKey(currentType))
                Log.LogWarning($"Failed to capture budget limit field for {currentType.Name}. Diagnostics will be inaccurate.");

            if (!CounterFields.ContainsKey(currentType))
                Log.LogWarning($"Failed to capture loop counter for {currentType.Name}. Diagnostics will be inaccurate.");

            if (!LocationFields.ContainsKey(currentType))
                Log.LogWarning($"Failed to capture location field for {currentType.Name}. Diagnostics will be unavailable.");

            if (verbose) WriteLog($"   [Summary] Captured {ErrorFields.Count} error field(s) for {currentType.Name}");
        }
    }
}