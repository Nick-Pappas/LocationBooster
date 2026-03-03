#nullable disable
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;
using static ZoneSystem;

namespace LocationBudgetBooster
{
    public static class BoosterPatches
    {
        public static HashSet<string> PatchedTypes = new HashSet<string>();
        private static HashSet<string> _transpiledMethods = new HashSet<string>();
        private static bool _insideGetRandomZone = false;
        private static string _lastLoggedLocation = "";

        public static bool ScanForInnerLoop(MethodInfo method)
        {
            try { return PatchProcessor.GetOriginalInstructions(method).Any(x => x.opcode == OpCodes.Call && x.operand is MethodInfo mi && mi.Name == "GetRandomPointInZone"); }
            catch { return false; }
        }

        public static bool ScanForOuterLoop(MethodInfo method)
        {
            try { return PatchProcessor.GetOriginalInstructions(method).Any(x => x.opcode == OpCodes.Stfld && x.operand is FieldInfo fi && fi.Name == "m_estimatedGenerateLocationsCompletionTime"); }
            catch { return false; }
        }

        public static void ResetAndPrepareForNewLocation()
        {
            BoosterSurvey.SurveyExhausted = false;
            BoosterDiagnostics.ResetInnerLoopCounter();
        }

        public static bool InnerLoopPrefix(object __instance, ref bool __result)
        {
            if (BoosterSurvey.SurveyExhausted)
            {
                if (BoosterReflection.IterationsPkgField != null)
                {
                    try
                    {
                        var pkg = BoosterReflection.IterationsPkgField.GetValue(__instance) as ZPackage;
                        if (pkg != null)
                        {
                            pkg.Clear();
                            pkg.Write(0);
                            pkg.SetPos(0);
                        }
                    }
                    catch { }
                }

                BoosterDiagnostics.ReportFailure(__instance);

                __result = false;
                return false;
            }
            return true;
        }

        public static bool GetRandomZonePrefix(ref Vector2i __result, float range)
        {
            
            BoosterDiagnostics.FilterTotalCalls++;
            if (_insideGetRandomZone) return true;

            var mode = LocationBooster.Mode.Value;
            var currentLoc = BoosterReflection.CurrentLocationForFilter;
        
            if (currentLoc == null) return true;
            if (currentLoc.m_centerFirst) return true; // vanilla handles this correctly I guess. Just bail for this one so that we do not insert it at ANY meadows on the map...
            // Log START for all modes
            if (currentLoc.m_prefabName != _lastLoggedLocation)
            {
                if (LocationBooster.LogSuccesses.Value || LocationBooster.DiagnosticMode.Value)
                {
                    BoosterDiagnostics.LogLocationStart(currentLoc, mode);
                }
                _lastLoggedLocation = currentLoc.m_prefabName;
            }

            // Early return for Vanilla
            if (mode == BoosterMode.Vanilla) return true;

            string target = LocationBooster.FilterTarget.Value;
            bool isGlobalMode = string.IsNullOrWhiteSpace(target);
            if (!isGlobalMode && currentLoc.m_prefabName != target) return true;

            try
            {
                _insideGetRandomZone = true;
                float min = currentLoc.m_minDistance;
                float max = currentLoc.m_maxDistance > 0.1f ? currentLoc.m_maxDistance : LocationBooster.WorldRadius.Value;

                if (mode == BoosterMode.Force) { __result = BoosterForce.GenerateDonut(min, max); BoosterDiagnostics.FilterAcceptedZones++; return false; }
                if (mode == BoosterMode.Filter) { __result = BoosterFilter.GenerateSieve(min, max, LocationBooster.WorldRadius.Value); BoosterDiagnostics.FilterAcceptedZones++; return false; }

                if (mode == BoosterMode.Survey)
                {
                    if (BoosterSurvey.GetZone(currentLoc, out Vector2i surveyResult))
                    {
                        __result = surveyResult; BoosterDiagnostics.FilterAcceptedZones++; return false;
                    }
                    else
                    {
                        __result = BoosterReflection.CachedOccupiedZone ?? Vector2i.zero;
                        return false;
                    }
                }
                if (mode == BoosterMode.SurveyPlus)
                {
                    if (BoosterSurveyPlus.GetZone(currentLoc, out Vector2i surveyResult))
                    {
                        __result = surveyResult;
                        BoosterDiagnostics.FilterAcceptedZones++;
                        return false;
                    }
                    else
                    {
                        // Exhausted: Return the last known occupied zone (standard fail behavior)
                        // or (0,0) to let the inner loop fail naturally.
                        __result = BoosterReflection.CachedOccupiedZone ?? Vector2i.zero;
                        return false;
                    }
                }
                return true;
            }
            finally { _insideGetRandomZone = false; }
        }

        public static IEnumerable<CodeInstruction> OuterLoopTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = instructions.ToList();
            for (int i = 0; i < codes.Count; i++)
            {
                yield return codes[i];
                if (codes[i].opcode == OpCodes.Stfld && codes[i].operand is FieldInfo fi && fi.FieldType == typeof(ZoneLocation))
                {
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(BoosterPatches), nameof(BoosterPatches.ResetAndPrepareForNewLocation)));
                }
            }
        }
        public static void OuterLoopPrefix()
        {
            if (ZoneSystem.instance != null)
            {
                BoosterGlobalProgress.StartGeneration(ZoneSystem.instance);
            }
        }

        public static void OuterLoopPostfix(ref bool __result)
        {
            // The coroutine's MoveNext returns false when the iteration is entirely finished
            if (!__result)
            {
                BoosterGlobalProgress.EndGeneration();
            }
        }
        public static IEnumerable<CodeInstruction> InnerLoopTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var codes = instructions.ToList();
            Type currentType = original.DeclaringType;
            string methodKey = $"{currentType.Name}::{original.Name}";

            string lastLogString = "";
            FieldInfo limitFieldFound = null;

            // --- PASS 1: Pre-scan ---
            for (int i = 0; i < codes.Count; i++)
            {
                var instruction = codes[i];

                if (instruction.opcode == OpCodes.Ldfld && instruction.operand is FieldInfo fiPkg && fiPkg.FieldType == typeof(ZPackage)) BoosterReflection.IterationsPkgField = fiPkg;
                if (instruction.opcode == OpCodes.Ldfld && instruction.operand is FieldInfo fi)
                {
                    if (fi.FieldType == typeof(ZoneLocation)) BoosterReflection.LocationFields[currentType] = fi;
                    if (fi.FieldType == typeof(Vector2i) && fi.Name.Contains("zoneID")) BoosterReflection.ZoneIDFields[currentType] = fi;
                }
                if (instruction.opcode == OpCodes.Ldc_I4 && instruction.operand is int val && (val == 100000 || val == 200000))
                {
                    for (int k = 1; k <= 5 && i + k < codes.Count; k++)
                    {
                        if (codes[i + k].opcode == OpCodes.Stfld) { limitFieldFound = codes[i + k].operand as FieldInfo; BoosterReflection.LimitFields[currentType] = limitFieldFound; break; }
                    }
                }
                if (instruction.opcode == OpCodes.Ldfld && limitFieldFound != null && (instruction.operand as FieldInfo) == limitFieldFound && i >= 2 && codes[i - 2].opcode == OpCodes.Ldfld)
                {
                    BoosterReflection.CounterFields[currentType] = codes[i - 2].operand as FieldInfo;
                }
                if (instruction.opcode == OpCodes.Call && instruction.operand is MethodInfo mi && mi.Name == "CountNrOfLocation" && i + 1 < codes.Count && codes[i + 1].opcode == OpCodes.Stfld)
                {
                    BoosterReflection.PlacedFields[currentType] = codes[i + 1].operand as FieldInfo;
                }
                if (instruction.opcode == OpCodes.Ldc_I4_S && Convert.ToInt32(instruction.operand) == 20)
                {
                    if (i > 0 && codes[i - 1].opcode == OpCodes.Ldfld) BoosterReflection.InnerCounterFields[currentType] = codes[i - 1].operand as FieldInfo;
                }
                if (instruction.opcode == OpCodes.Ldstr && instruction.operand is string s && s.StartsWith("error")) lastLogString = s.Trim();
                if (instruction.opcode == OpCodes.Ldflda && !string.IsNullOrEmpty(lastLogString) && lastLogString.StartsWith("error"))
                {
                    if (instruction.operand is FieldInfo f) BoosterReflection.ErrorFields[lastLogString] = f;
                    lastLogString = "";
                }
            }

            // --- PASS 2: Modify ---
            int outerMult = LocationBooster.OuterMultiplier.Value;
            int innerMult = LocationBooster.InnerMultiplier.Value;
            int getRandomZoneIndex = codes.FindIndex(c => c.opcode == OpCodes.Call && c.operand is MethodInfo mi && mi.Name == "GetRandomZone");

            for (int i = 0; i < codes.Count; i++)
            {
                var instruction = codes[i];
                var opcode = instruction.opcode;
                var operand = instruction.operand;

                // 1. REPLACEMENTS
                if (opcode == OpCodes.Ldc_I4 && operand is int val && (val == 100000 || val == 200000))
                {
                    yield return instruction;
                    yield return new CodeInstruction(OpCodes.Ldc_I4, outerMult);
                    yield return new CodeInstruction(OpCodes.Mul);
                    continue;
                }
                if (opcode == OpCodes.Ldc_I4_S && Convert.ToInt32(operand) == 20)
                {
                    if (i > 0 && codes[i - 1].opcode == OpCodes.Ldfld)
                    {
                        yield return new CodeInstruction(OpCodes.Ldc_I4, 20 * innerMult);
                        continue;
                    }
                }

                // 2. INJECTIONS
                if (i == getRandomZoneIndex)
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Ldfld, BoosterReflection.LocationFields[currentType]);
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(BoosterReflection), nameof(BoosterReflection.SetCurrentLocation)));
                }

                if (opcode == OpCodes.Ldc_R8 && operand is double dval && dval == 30.0)
                {
                    for (int j = i + 1; j < i + 10 && j < codes.Count; j++)
                    {
                        if ((codes[j].opcode == OpCodes.Stloc || codes[j].opcode == OpCodes.Stloc_S) && codes[j].operand is LocalBuilder lb && lb.LocalType == typeof(float))
                        {
                            yield return instruction;
                            int k = i + 1;
                            while (k <= j) { yield return codes[k]; k++; }

                            yield return new CodeInstruction(OpCodes.Ldloc_S, lb);
                            yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(BoosterDiagnostics), nameof(BoosterDiagnostics.TrackGlobalAltitude)));

                            i = j;
                            goto next_instr;
                        }
                    }
                }

                yield return instruction;

                // Progress Log
                if (opcode == OpCodes.Stfld && BoosterReflection.CounterFields.TryGetValue(currentType, out var cf) && (operand as FieldInfo) == cf)
                {
                    if (LocationBooster.ProgressInterval.Value > 0)
                    {
                        yield return new CodeInstruction(OpCodes.Ldarg_0);
                        yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(BoosterDiagnostics), nameof(BoosterDiagnostics.LogProgress)));
                    }
                }

                // Heartbeat Log
                if (opcode == OpCodes.Call && operand is MethodInfo miRP && miRP.Name == "GetRandomPointInZone")
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(BoosterDiagnostics), nameof(BoosterDiagnostics.LogInnerLoopProgress)));
                }

                if (opcode == OpCodes.Call && operand is MethodInfo miReg && miReg.Name == "RegisterLocation")
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(BoosterDiagnostics), nameof(BoosterDiagnostics.ReportSuccess)));
                }

                if (opcode == OpCodes.Ldstr && operand is string str && str.Contains("Failed to place all"))
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(BoosterDiagnostics), nameof(BoosterDiagnostics.ReportFailure)));
                }

                if (opcode == OpCodes.Ldfld && operand is FieldInfo errField)
                {
                    if (i + 3 < codes.Count && codes[i + 1].opcode == OpCodes.Ldc_I4_1 && codes[i + 2].opcode == OpCodes.Add)
                    {
                        if (errField.Name.Contains("errorAlt"))
                        {
                            LocalBuilder altLocal = null, pointLocal = null;
                            for (int x = 1; x <= 40 && i - x >= 0; x++)
                            {
                                if (codes[i - x].operand is LocalBuilder lb)
                                {
                                    if (lb.LocalType == typeof(float) && altLocal == null) altLocal = lb;
                                    if (lb.LocalType == typeof(Vector3) && pointLocal == null) pointLocal = lb;
                                    if (altLocal != null && pointLocal != null) break;
                                }
                            }

                            if (altLocal != null && pointLocal != null && BoosterReflection.LocationFields.ContainsKey(currentType))
                            {
                                yield return new CodeInstruction(OpCodes.Ldarg_0);
                                yield return new CodeInstruction(OpCodes.Ldloc_S, altLocal);
                                yield return new CodeInstruction(OpCodes.Ldarg_0);
                                yield return new CodeInstruction(OpCodes.Ldfld, BoosterReflection.LocationFields[currentType]);
                                yield return new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(ZoneLocation), "m_minAltitude"));
                                yield return new CodeInstruction(OpCodes.Ldarg_0);
                                yield return new CodeInstruction(OpCodes.Ldfld, BoosterReflection.LocationFields[currentType]);
                                yield return new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(ZoneLocation), "m_maxAltitude"));
                                yield return new CodeInstruction(OpCodes.Ldloc_S, pointLocal);
                                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(BoosterDiagnostics), nameof(BoosterDiagnostics.TrackAltitudeFailure)));
                            }
                        }

                        if (errField.Name.Contains("errorCenterDistance"))
                        {
                            LocalBuilder distLocal = null;
                            for (int x = 1; x <= 30 && i - x >= 0; x++)
                            {
                                if (codes[i - x].opcode == OpCodes.Stloc_S && codes[i - x].operand is LocalBuilder lb && lb.LocalType == typeof(float))
                                {
                                    if (i - x - 1 >= 0 && codes[i - x - 1].opcode == OpCodes.Call && codes[i - x - 1].operand is MethodInfo miMag && miMag.Name == "get_magnitude")
                                    {
                                        distLocal = lb;
                                        break;
                                    }
                                }
                            }
                            if (distLocal != null && BoosterReflection.LocationFields.ContainsKey(currentType))
                            {
                                yield return new CodeInstruction(OpCodes.Ldarg_0);
                                yield return new CodeInstruction(OpCodes.Ldloc_S, distLocal);
                                yield return new CodeInstruction(OpCodes.Ldarg_0);
                                yield return new CodeInstruction(OpCodes.Ldfld, BoosterReflection.LocationFields[currentType]);
                                yield return new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(ZoneLocation), "m_minDistance"));
                                yield return new CodeInstruction(OpCodes.Ldarg_0);
                                yield return new CodeInstruction(OpCodes.Ldfld, BoosterReflection.LocationFields[currentType]);
                                yield return new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(ZoneLocation), "m_maxDistance"));
                                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(BoosterDiagnostics), nameof(BoosterDiagnostics.TrackDistanceFailure)));
                            }
                        }
                    }
                }
            next_instr:;
            }
        }
    }
}