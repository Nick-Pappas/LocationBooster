using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using static ZoneSystem;

namespace LocationBudgetBooster
{
    [BepInPlugin("com.nickpappas.locationbooster", "Location Budget Booster", "0.2.3")]
    public class LocationBooster : BaseUnityPlugin
    {
        public static ConfigEntry<int> OuterMultiplier;
        public static ConfigEntry<int> InnerMultiplier;
        public static ManualLogSource Log;
        private static Harmony _harmony;

        // Reflection Caching
        public static Dictionary<Type, FieldInfo> LocationFields = new Dictionary<Type, FieldInfo>();
        public static Dictionary<Type, FieldInfo> LimitFields = new Dictionary<Type, FieldInfo>(); // Was AttemptsFields
        public static Dictionary<Type, FieldInfo> CounterFields = new Dictionary<Type, FieldInfo>(); // New: The actual 'i' loop counter
        public static Dictionary<Type, FieldInfo> PlacedFields = new Dictionary<Type, FieldInfo>();
        public static Dictionary<string, FieldInfo> ErrorFields = new Dictionary<string, FieldInfo>();

        private static HashSet<string> _patchedTypes = new HashSet<string>();

        void Awake()
        {
            Log = Logger;

            OuterMultiplier = Config.Bind("General", "OuterLoopMultiplier", 5, "Multiplies the budget for finding candidate Zones.");
            InnerMultiplier = Config.Bind("General", "InnerLoopMultiplier", 5, "Multiplies placement attempts inside a valid Zone.");

            Log.LogInfo($"[Booster] Initializing. Multipliers: {OuterMultiplier.Value}x (Zone Search), {InnerMultiplier.Value}x (Placement).");

            _harmony = new Harmony("com.nick.locationbooster");

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
                        Log.LogInfo($"[Booster] Patching target: {type.Name}");
                        _harmony.Patch(method, transpiler: new HarmonyMethod(typeof(LocationBooster), nameof(BoosterTranspiler)));
                        _patchedTypes.Add(type.FullName);
                    }
                }
            }
        }

        private static bool ScanForTarget(MethodInfo method)
        {
            try
            {
                var instructions = PatchProcessor.GetOriginalInstructions(method);
                return instructions.Any(x => x.opcode == OpCodes.Ldc_I4 && (x.operand is int v && (v == 100000 || v == 200000)));
            }
            catch { return false; }
        }

        public static long GetVal(object instance, string fieldName)
        {
            if (ErrorFields.TryGetValue(fieldName, out var field))
            {
                try { return Convert.ToInt64(field.GetValue(instance)); } catch { }
            }
            return 0;
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

        // Shared Logic for Success and Failure
        public static void LogDiagnostics(object instance, bool isSuccess)
        {
            var loc = GetLocation(instance);
            string locationName = loc?.m_prefabName ?? "Unknown";
            if (locationName == "Unknown") return;

            var type = instance.GetType();

            // Get Budget Data
            long limit = 0;
            if (LimitFields.TryGetValue(type, out var limitField)) limit = Convert.ToInt64(limitField.GetValue(instance));

            long actualIterations = 0;
            if (CounterFields.TryGetValue(type, out var countField)) actualIterations = Convert.ToInt64(countField.GetValue(instance));

            // Get Placement Progress
            int placedSoFar = 0;
            if (PlacedFields.TryGetValue(type, out var placedField)) placedSoFar = (int)placedField.GetValue(instance);

            // For Success: Only log if we finished the whole group
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

            Log.Log(level, $"[Booster] {status}: {locationName} (Placed {placedSoFar}/{loc.m_quantity}). Cost: {actualIterations:N0}/{limit:N0} ({costPct:F2}%)");

            // Diagnostic Dump
            long errBiomeArea = GetVal(instance, "errorBiomeArea");
            long errZoneOccupied = GetVal(instance, "errorLocationInZone");
            long phase1Failures = errBiomeArea + errZoneOccupied;

            long successfulZoneFinds = actualIterations - phase1Failures; // Approximation: Iterations that passed Phase 1

            long errBiome = GetVal(instance, "errorBiome");
            long errAlt = GetVal(instance, "errorAlt");
            long errDist = GetVal(instance, "errorCenterDistance");
            long errTerr = GetVal(instance, "errorTerrainDelta");
            long errSim = GetVal(instance, "errorSimilar");
            long errVeg = GetVal(instance, "errorVegetation");
            long errFor = GetVal(instance, "errorForest");
            long errNotSim = GetVal(instance, "errorNotSimilar");

            long totalPhase2Failures = errBiome + errAlt + errDist + errTerr + errSim + errVeg + errFor + errNotSim;

            Log.Log(level, $"   Phase 1 (Zone Find): Found {successfulZoneFinds:N0} valid grids out of {actualIterations:N0} checks. Things failed because:");
            if (errBiomeArea > 0) Log.Log(level, $"      - Grid checked for a biome was not inside the world's disk: {errBiomeArea:N0}");
            if (errZoneOccupied > 0) Log.Log(level, $"      - Grid already had a location in it:   {errZoneOccupied:N0}");

            Log.Log(level, $"   Phase 2 (Placement): Rejected {totalPhase2Failures:N0} spots inside those grids for the following reasons:");

            void PrintErr(string name, long count)
            {
                if (count > 0) Log.Log(level, $"      - {name,-35}: {count,6:N0}");
            }

            PrintErr("Point was in wrong Biome", errBiome);
            PrintErr("Point altitude was not right (either too high or too low)", errAlt);
            PrintErr("Point was outside the Min/Max dist", errDist);
            PrintErr("Terrain too steep/uneven", errTerr);
            PrintErr("Too close to a similar location", errSim);
            PrintErr("Too far from required neighbor", errNotSim);
            PrintErr("Vegetation blocked placement", errVeg);
            PrintErr("Forest density was either too high or too low for what was needed", errFor);
        }

        public static void ReportSuccess(object instance) => LogDiagnostics(instance, true);
        public static void ReportFailure(object instance) => LogDiagnostics(instance, false);

        public static IEnumerable<CodeInstruction> BoosterTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var codes = instructions.ToList();
            int outerMult = OuterMultiplier.Value;
            int innerMult = InnerMultiplier.Value;
            string lastLogString = "";
            Type currentType = original.DeclaringType;

            Log.LogInfo($"--- Transpiling {codes.Count} instructions for {currentType.Name} ---");

            for (int i = 0; i < codes.Count; i++)
            {
                var opcode = codes[i].opcode;
                var operand = codes[i].operand;

                // 1. Capture Location Field
                if (opcode == OpCodes.Ldfld && operand is FieldInfo fi && fi.FieldType == typeof(ZoneLocation))
                {
                    if (!LocationFields.ContainsKey(currentType)) LocationFields[currentType] = fi;
                }

                // 2. Capture Placed Field
                if (opcode == OpCodes.Call && operand is MethodInfo mi && mi.Name == "CountNrOfLocation")
                {
                    if (i + 1 < codes.Count && codes[i + 1].opcode == OpCodes.Stfld)
                    {
                        var pField = codes[i + 1].operand as FieldInfo;
                        if (!PlacedFields.ContainsKey(currentType)) PlacedFields[currentType] = pField;
                    }
                }

                // 3. Capture Loop Counter 'i'
                // In d__48, 'i' is initialized to 0: ldc.i4.0 -> stfld <i>
                // We look for stfld <i> followed closely by a jump or loop start logic.
                // However, a more robust way: It's the field used in the main loop condition: ldarg.0 -> ldfld <i> -> ldarg.0 -> ldfld <attempts> -> blt/bge
                // Let's hook the Comparison logic to find it.

                // 4. Outer Loop Boost & Field Capture
                if (opcode == OpCodes.Ldc_I4 && operand is int val && (val == 100000 || val == 200000))
                {
                    Log.LogInfo($"   [Outer] Boosting {val} at IL_{i:D4}");
                    yield return codes[i];
                    yield return new CodeInstruction(OpCodes.Ldc_I4, outerMult);
                    yield return new CodeInstruction(OpCodes.Mul);

                    // Capture Limit field
                    if (val == 200000 && i + 1 < codes.Count && codes[i + 1].opcode == OpCodes.Stfld)
                    {
                        var limitField = codes[i + 1].operand as FieldInfo;
                        LimitFields[currentType] = limitField;
                    }
                    continue;
                }

                // 5. Capture Loop Counter (Post-Hoc Analysis of Instruction Stream)
                // In d__48 IL_0718: ldarg.0, ldfld <i>, ldarg.0, ldfld <attempts>
                // We know <attempts> is LimitFields[currentType].
                if (LimitFields.ContainsKey(currentType) && opcode == OpCodes.Ldfld && operand == LimitFields[currentType])
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
                            Log.LogInfo($"   [Reflect] Captured Loop Counter: {counterField.Name}");
                        }
                    }
                }

                // 6. Inner Loop Boost
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
                        Log.LogInfo($"   [Inner] Boosting loop count 20 at IL_{i:D4}");
                        yield return new CodeInstruction(OpCodes.Ldc_I4, 20 * innerMult);
                        continue;
                    }
                }

                // 7. Hook Success
                if (opcode == OpCodes.Call && operand is MethodInfo miReg && miReg.Name == "RegisterLocation")
                {
                    yield return codes[i];
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(LocationBooster), nameof(ReportSuccess)));
                    continue;
                }

                // 8. Hook Failure
                if (opcode == OpCodes.Ldstr && operand is string str)
                {
                    lastLogString = str.Trim();
                    if (str.Contains("Failed to place all"))
                    {
                        Log.LogInfo($"   [Hook] Injecting ReportFailure at IL_{i:D4}");
                        yield return new CodeInstruction(OpCodes.Ldarg_0);
                        yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(LocationBooster), nameof(ReportFailure)));
                    }
                }

                // 9. Map Error Fields
                if (opcode == OpCodes.Ldflda && !string.IsNullOrEmpty(lastLogString) && lastLogString.StartsWith("error"))
                {
                    var field = operand as FieldInfo;
                    if (field != null && !ErrorFields.ContainsKey(lastLogString)) ErrorFields[lastLogString] = field;
                    lastLogString = "";
                }

                yield return codes[i];
            }
        }
    }
}