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

        public static bool ScanForTarget(MethodInfo method)
        {
            try
            {
                var instructions = PatchProcessor.GetOriginalInstructions(method);
                return instructions.Any(x => x.opcode == OpCodes.Ldc_I4 && (x.operand is int v && (v == 100000 || v == 200000)));
            }
            catch { return false; }
        }

        public static bool GetRandomZonePrefix(ref Vector2i __result, float range)
        {
            BoosterDiagnostics.FilterTotalCalls++;

            // Recursion guard
            if (_insideGetRandomZone) return true;

            // 1. Check Mode
            var mode = LocationBooster.Mode.Value;
            if (mode == BoosterMode.Vanilla) return true;

            // 2. Check Target
            var currentLoc = BoosterReflection.CurrentLocationForFilter;
            string target = LocationBooster.FilterTarget.Value;

            if (currentLoc == null || string.IsNullOrEmpty(target)) return true;
            if (currentLoc.m_prefabName != target) return true;

            // 3. Execute Strategy
            try
            {
                _insideGetRandomZone = true;

                float min = currentLoc.m_minDistance;
                float max = currentLoc.m_maxDistance;
                // Fallback to max range if location doesn't specify
                if (max <= 0.1f) max = LocationBooster.WorldRadius.Value;

                if (mode == BoosterMode.Force)
                {
                    __result = BoosterForce.GenerateDonut(min, max);
                    BoosterDiagnostics.FilterAcceptedZones++;
                }
                else if (mode == BoosterMode.Filter)
                {
                    __result = BoosterFilter.GenerateSieve(min, max, LocationBooster.WorldRadius.Value);
                    BoosterDiagnostics.FilterAcceptedZones++;
                }

                return false; // Skip vanilla
            }
            finally
            {
                _insideGetRandomZone = false;
            }
        }

        public static IEnumerable<CodeInstruction> BoosterTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase original, ILGenerator generator)
        {
            var codes = instructions.ToList();
            int outerMult = LocationBooster.OuterMultiplier.Value;
            int innerMult = LocationBooster.InnerMultiplier.Value;
            string lastLogString = "";
            Type currentType = original.DeclaringType;
            string methodKey = $"{currentType.Name}::{original.Name}";
            bool verbose = !_transpiledMethods.Contains(methodKey);

            if (verbose)
            {
                BoosterDiagnostics.WriteLog($"--- Transpiling {codes.Count} instructions for {currentType.Name} ---");
                _transpiledMethods.Add(methodKey);
            }

            // Unconditional search for GetRandomZone to inject context capture
            int getRandomZoneIndex = -1;
            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode == OpCodes.Call && codes[i].operand is MethodInfo mi && mi.Name == "GetRandomZone")
                {
                    getRandomZoneIndex = i;
                    if (verbose) BoosterDiagnostics.WriteLog($"   [DistFilter] Found GetRandomZone at IL_{i:D4}");
                    break;
                }
            }

            FieldInfo maxRangeField = null;

            for (int i = 0; i < codes.Count; i++)
            {
                var opcode = codes[i].opcode;
                var operand = codes[i].operand;

                // 1. Capture Location Field
                if (opcode == OpCodes.Ldfld && operand is FieldInfo fi && fi.FieldType == typeof(ZoneLocation))
                {
                    if (!BoosterReflection.LocationFields.ContainsKey(currentType)) BoosterReflection.LocationFields[currentType] = fi;
                }

                // 1a. Capture ZoneID Field
                if (opcode == OpCodes.Ldfld && operand is FieldInfo fiZone && fiZone.FieldType == typeof(Vector2i) && fiZone.Name.Contains("zoneID"))
                {
                    if (!BoosterReflection.ZoneIDFields.ContainsKey(currentType))
                    {
                        BoosterReflection.ZoneIDFields[currentType] = fiZone;
                        if (verbose) BoosterDiagnostics.WriteLog($"   [Reflect] Captured ZoneID: {fiZone.Name}");
                    }
                }

                // 2. Capture Placed Field
                if (opcode == OpCodes.Call && operand is MethodInfo mi && mi.Name == "CountNrOfLocation")
                {
                    if (i + 1 < codes.Count && codes[i + 1].opcode == OpCodes.Stfld)
                    {
                        var pField = codes[i + 1].operand as FieldInfo;
                        if (!BoosterReflection.PlacedFields.ContainsKey(currentType))
                        {
                            BoosterReflection.PlacedFields[currentType] = pField;
                            if (verbose) BoosterDiagnostics.WriteLog($"   [Reflect] Captured Placed Count: {pField.Name}");
                        }
                    }
                }

                // 3. Outer Loop Boost & Budget Field Capture
                if (opcode == OpCodes.Ldc_I4 && operand is int val && (val == 100000 || val == 200000))
                {
                    if (verbose) BoosterDiagnostics.WriteLog($"   [Outer] Boosting {val} at IL_{i:D4}");
                    yield return codes[i];
                    yield return new CodeInstruction(OpCodes.Ldc_I4, outerMult);
                    yield return new CodeInstruction(OpCodes.Mul);

                    if (i + 1 < codes.Count && codes[i + 1].opcode == OpCodes.Stfld)
                    {
                        var limitField = codes[i + 1].operand as FieldInfo;
                        if (!BoosterReflection.LimitFields.ContainsKey(currentType))
                        {
                            BoosterReflection.LimitFields[currentType] = limitField;
                            if (verbose) BoosterDiagnostics.WriteLog($"   [Reflect] Captured Budget Limit: {limitField.Name}");
                        }
                    }
                    continue;
                }

                // 3a. Capture maxRange field
                if (opcode == OpCodes.Ldc_R4 && operand is float fval && fval == 10000f)
                {
                    if (i + 1 < codes.Count && codes[i + 1].opcode == OpCodes.Stfld && codes[i + 1].operand is FieldInfo maxRangeFi)
                    {
                        maxRangeField = maxRangeFi;
                        if (verbose) BoosterDiagnostics.WriteLog($"   [MaxRange] Found maxRange field at IL_{i:D4}: {maxRangeFi.Name}");
                    }
                }

                // 3b. Inject maxRange clamping
                if (opcode == OpCodes.Ldfld && operand is FieldInfo ldfldFi && maxRangeField != null && ldfldFi == maxRangeField)
                {
                    if (i + 1 < codes.Count && codes[i + 1].opcode == OpCodes.Call &&
                        codes[i + 1].operand is MethodInfo nextMi && nextMi.Name == "GetRandomZone")
                    {
                        if (BoosterReflection.LocationFields.ContainsKey(currentType))
                        {
                            if (verbose) BoosterDiagnostics.WriteLog($"   [MaxRange] Injecting clamping before GetRandomZone at IL_{i:D4}");

                            var skipLabel = generator.DefineLabel();

                            yield return new CodeInstruction(OpCodes.Ldarg_0);
                            yield return new CodeInstruction(OpCodes.Ldfld, BoosterReflection.LocationFields[currentType]);
                            yield return new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(ZoneLocation), "m_maxDistance"));
                            yield return new CodeInstruction(OpCodes.Ldc_R4, 0f);
                            yield return new CodeInstruction(OpCodes.Ble_Un, skipLabel);

                            yield return new CodeInstruction(OpCodes.Ldarg_0);
                            yield return new CodeInstruction(OpCodes.Ldarg_0);
                            yield return new CodeInstruction(OpCodes.Ldfld, maxRangeField);
                            yield return new CodeInstruction(OpCodes.Ldarg_0);
                            yield return new CodeInstruction(OpCodes.Ldfld, BoosterReflection.LocationFields[currentType]);
                            yield return new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(ZoneLocation), "m_maxDistance"));
                            yield return new CodeInstruction(OpCodes.Ldc_R4, 64f);
                            yield return new CodeInstruction(OpCodes.Add);
                            yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Mathf), nameof(Mathf.Min), new[] { typeof(float), typeof(float) }));
                            yield return new CodeInstruction(OpCodes.Stfld, maxRangeField);

                            if (LocationBooster.DiagnosticMode.Value)
                            {
                                yield return new CodeInstruction(OpCodes.Ldarg_0);
                                yield return new CodeInstruction(OpCodes.Ldfld, BoosterReflection.LocationFields[currentType]);
                                yield return new CodeInstruction(OpCodes.Ldarg_0);
                                yield return new CodeInstruction(OpCodes.Ldfld, maxRangeField);
                                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(BoosterDiagnostics), nameof(BoosterDiagnostics.LogMaxRangeClamping)));
                            }

                            codes[i].labels.Add(skipLabel);
                            if (verbose) BoosterDiagnostics.WriteLog($"   [MaxRange] Clamping complete");
                        }
                    }
                }

                // 4. Capture Loop Counter
                if (BoosterReflection.LimitFields.ContainsKey(currentType) && opcode == OpCodes.Ldfld && operand is FieldInfo fi2 && fi2 == BoosterReflection.LimitFields[currentType])
                {
                    if (i >= 2 && codes[i - 2].opcode == OpCodes.Ldfld)
                    {
                        var counterField = codes[i - 2].operand as FieldInfo;
                        if (!BoosterReflection.CounterFields.ContainsKey(currentType))
                        {
                            BoosterReflection.CounterFields[currentType] = counterField;
                            if (verbose) BoosterDiagnostics.WriteLog($"   [Reflect] Captured Loop Counter: {counterField.Name}");
                        }
                    }
                }

                // 5. Location Context Capture
                if (getRandomZoneIndex >= 0 && i == getRandomZoneIndex)
                {
                    if (BoosterReflection.LocationFields.ContainsKey(currentType))
                    {
                        yield return new CodeInstruction(OpCodes.Ldarg_0);
                        yield return new CodeInstruction(OpCodes.Ldfld, BoosterReflection.LocationFields[currentType]);
                        yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(BoosterReflection), nameof(BoosterReflection.SetCurrentLocation)));
                        if (verbose) BoosterDiagnostics.WriteLog($"   [Context] Injected location context capture at IL_{i:D4} (field={BoosterReflection.LocationFields[currentType].Name})");
                    }
                }

                // 6. Progress Heartbeat Injection
                if (LocationBooster.ProgressInterval.Value > 0 && BoosterReflection.CounterFields.ContainsKey(currentType) &&
                    opcode == OpCodes.Stfld && operand is FieldInfo fi3 && fi3 == BoosterReflection.CounterFields[currentType])
                {
                    if (LocationBooster.DiagnosticMode.Value || verbose)
                        BoosterDiagnostics.WriteLog($"   [Heartbeat] Matched stfld {fi3.Name} at IL_{i:D4}, injecting LogProgress");

                    yield return codes[i];

                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(BoosterDiagnostics), nameof(BoosterDiagnostics.LogProgress)));

                    if (verbose) BoosterDiagnostics.WriteLog($"   [Heartbeat] Injected progress logging at IL_{i:D4}");
                    continue;
                }

                // 7. Inner Loop Boost
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
                        if (verbose) BoosterDiagnostics.WriteLog($"   [Inner] Boosting loop count 20 at IL_{i:D4}");
                        yield return new CodeInstruction(OpCodes.Ldc_I4, 20 * innerMult);
                        continue;
                    }
                }

                // 8. Hook Success
                if (opcode == OpCodes.Call && operand is MethodInfo miReg && miReg.Name == "RegisterLocation")
                {
                    yield return codes[i];
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(BoosterDiagnostics), nameof(BoosterDiagnostics.ReportSuccess)));
                    continue;
                }

                // 9. Altitude/Distance Tracking Injection
                if (opcode == OpCodes.Ldfld && operand is FieldInfo errField)
                {
                    if (i + 3 < codes.Count &&
                        codes[i + 1].opcode == OpCodes.Ldc_I4_1 &&
                        codes[i + 2].opcode == OpCodes.Add)
                    {
                        // Altitude tracking
                        if (errField.Name.Contains("errorAlt"))
                        {
                            if (verbose) BoosterDiagnostics.WriteLog($"   [Track] Found errorAlt increment at IL_{i:D4}, searching for altitude AND point locals...");
                            LocalBuilder altLocal = null;
                            LocalBuilder pointLocal = null;

                            // 1. Find the altitude (float) local - usually close
                            for (int lookback = 1; lookback <= 15 && i - lookback >= 0; lookback++)
                            {
                                var prevCode = codes[i - lookback];
                                if ((prevCode.opcode == OpCodes.Ldloc_S || prevCode.opcode == OpCodes.Ldloc) &&
                                    prevCode.operand is LocalBuilder lb &&
                                    lb.LocalType == typeof(float))
                                {
                                    altLocal = lb;
                                    break;
                                }
                            }

                            // 2. Find the point (Vector3) local - further back
                            // We look for any Ldloc of type Vector3 in the preceding 40 instructions
                            for (int lookback = 1; lookback <= 40 && i - lookback >= 0; lookback++)
                            {
                                var prevCode = codes[i - lookback];
                                if ((prevCode.opcode == OpCodes.Ldloc_S || prevCode.opcode == OpCodes.Ldloc || prevCode.opcode == OpCodes.Ldloca_S) &&
                                    prevCode.operand is LocalBuilder lb &&
                                    lb.LocalType == typeof(Vector3))
                                {
                                    pointLocal = lb;
                                    break; // Assume the most recent Vector3 usage is the point
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
                                yield return new CodeInstruction(OpCodes.Ldloc_S, pointLocal); // Pass the point!
                                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(BoosterDiagnostics), nameof(BoosterDiagnostics.TrackAltitudeFailure)));
                                if (verbose) BoosterDiagnostics.WriteLog($"   [Track] Injected altitude tracking at IL_{i:D4}");
                            }
                            else if (verbose)
                            {
                                BoosterDiagnostics.WriteLog($"   [Track] Failed to find locals for Altitude check. Alt: {altLocal != null}, Point: {pointLocal != null}");
                            }
                        }

                        // Distance tracking
                        if (errField.Name.Contains("errorCenterDistance"))
                        {
                            if (verbose) BoosterDiagnostics.WriteLog($"   [Track] Found errorCenterDistance increment at IL_{i:D4}, searching for distance local...");
                            LocalBuilder distLocal = null;
                            for (int lookback = 1; lookback <= 30 && i - lookback >= 0; lookback++)
                            {
                                if (codes[i - lookback].opcode == OpCodes.Stloc_S &&
                                    codes[i - lookback].operand is LocalBuilder lb &&
                                    lb.LocalType == typeof(float))
                                {
                                    if (i - lookback - 1 >= 0 &&
                                        codes[i - lookback - 1].opcode == OpCodes.Call &&
                                        codes[i - lookback - 1].operand is MethodInfo magnitudeMi &&
                                        magnitudeMi.Name == "get_magnitude")
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
                                if (verbose) BoosterDiagnostics.WriteLog($"   [Track] Injected distance tracking at IL_{i:D4}");
                            }
                        }
                    }
                }

                // 10. Hook Failure
                if (opcode == OpCodes.Ldstr && operand is string str)
                {
                    lastLogString = str.Trim();
                    if (str.Contains("Failed to place all"))
                    {
                        if (verbose) BoosterDiagnostics.WriteLog($"   [Hook] Injecting ReportFailure at IL_{i:D4}");
                        yield return new CodeInstruction(OpCodes.Ldarg_0);
                        yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(BoosterDiagnostics), nameof(BoosterDiagnostics.ReportFailure)));
                    }
                }

                // 11. Inject Shadow Counter Increment
                if (opcode == OpCodes.Stfld && operand is FieldInfo stField)
                {
                    if (stField.Name.StartsWith("<error") && stField.Name.Contains(">"))
                    {
                        bool isIncrement = i > 0 && codes[i - 1].opcode == OpCodes.Add;

                        if (isIncrement)
                        {
                            string fieldName = stField.Name;
                            int start = fieldName.IndexOf('<') + 1;
                            int end = fieldName.IndexOf('>');
                            if (start > 0 && end > start)
                            {
                                string errorName = fieldName.Substring(start, end - start);

                                yield return codes[i];

                                yield return new CodeInstruction(OpCodes.Ldarg_0);
                                yield return new CodeInstruction(OpCodes.Ldstr, errorName);
                                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(BoosterDiagnostics), nameof(BoosterDiagnostics.IncrementShadow)));

                                if (verbose) BoosterDiagnostics.WriteLog($"   [Shadow] Injected overflow tracking for '{errorName}' at IL_{i:D4}");
                                continue;
                            }
                        }
                    }
                }

                // 12. Map Error Fields (must happen after use to capture lastLogString context)
                if (opcode == OpCodes.Ldflda && !string.IsNullOrEmpty(lastLogString) && lastLogString.StartsWith("error"))
                {
                    var field = operand as FieldInfo;
                    if (field != null && !BoosterReflection.ErrorFields.ContainsKey(lastLogString))
                    {
                        BoosterReflection.ErrorFields[lastLogString] = field;
                        if (verbose) BoosterDiagnostics.WriteLog($"   [Reflect] Mapped error field '{lastLogString}' → {field.Name}");
                    }
                    lastLogString = "";
                }

                yield return codes[i];
            }
        }
    }
}