using BepInEx.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace LocationBudgetBooster
{
    public static class BoosterReporter
    {
        private class FunnelStep
        {
            public string Name;
            public string ConfigInfo;
            public string PassedContext;
            public long Input;
            public long Failures;
            public Action<StringBuilder, string> FailurePrinter;

            public long Passed => Input - Failures;
        }

        public static void WriteReport(ReportData data, bool isHeartbeat)
        {
            if (data == null) return;

            if (isHeartbeat)
            {
                LogHeartbeat(data);
            }
            else
            {
                LogFullReport(data);
            }
        }

        private static void LogHeartbeat(ReportData data)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"[PROGRESS] {data.Loc.m_prefabName}: {data.Placed}/{data.Loc.m_quantity}. Cost: {data.CurrentOuter:N0}/{data.LimitOuter:N0}");

            // Phase 1
            if (data.ErrZone > 0 || data.ErrArea > 0)
            {
                sb.AppendLine(" PHASE 1 FAILURES (Zone Search):");
                if (data.ErrZone > 0) sb.AppendLine($"       - Zone Occupied            : {data.ErrZone,12:N0}");
                if (data.ErrArea > 0)
                {
                    sb.AppendLine($"       - Wrong Biome Area         : {data.ErrArea,12:N0}");
                    PrintDict(sb, "          └─ ", BoosterDiagnostics.BiomeAreaFailures, data.LocHash);
                }
            }

            // Phase 2
            sb.AppendLine(" PHASE 2 FAILURES (Placement Filters):");
            var failures = new List<(string Name, long Count, Action<StringBuilder, string> DetailsPrinter)>();
            if (data.ErrDist > 0) failures.Add(("Distance Filter", data.ErrDist, (s, pad) => PrintDist(s, pad, data.InstanceHash)));
            if (data.ErrBiome > 0) failures.Add(("Wrong Biome Type", data.ErrBiome, (s, pad) => PrintDict(s, pad, BoosterDiagnostics.BiomeFailures, data.LocHash)));
            if (data.ErrAlt > 0) failures.Add(("Wrong Altitude", data.ErrAlt, (s, pad) => PrintAlt(s, pad, data.InstanceHash)));
            if (data.ErrForest > 0) failures.Add(("Forest Check", data.ErrForest, null));
            if (data.ErrTerrain > 0) failures.Add(("Terrain Check", data.ErrTerrain, null));
            if (data.ErrSim + data.ErrNotSim > 0) failures.Add(("Similarity Check", data.ErrSim + data.ErrNotSim, null));
            if (data.ErrVeg > 0) failures.Add(("Vegetation Density", data.ErrVeg, null));

            foreach (var fail in failures.OrderByDescending(x => x.Count).Take(5))
            {
                sb.AppendLine($"       - {fail.Name.PadRight(25)}: {fail.Count,12:N0}");
                fail.DetailsPrinter?.Invoke(sb, "          └─ ");
            }
            BoosterDiagnostics.WriteLog(sb.ToString().TrimEnd());
        }

        private static void LogFullReport(ReportData data)
        {
            StringBuilder report = new StringBuilder();
            string status = data.IsComplete ? "COMPLETE" : "FAILURE";
            LogLevel level = data.IsComplete ? LogLevel.Info : LogLevel.Warning;

            report.AppendLine($"[{status}] {data.Loc.m_prefabName}: {data.Placed}/{data.Loc.m_quantity}. Cost: {data.CurrentOuter:N0}/{data.LimitOuter:N0} outer loop budget and {data.InDist:N0} inner loop iterations.");
            report.AppendLine("────────────────────────────────────────────────────────");

            // --- PHASE 1 ---
            report.AppendLine($"PHASE 1 (Zone Search): {data.CurrentOuter:N0} Checks");
            if (data.ErrZone > 0) report.AppendLine($"[x] Occupied Zones: {data.ErrZone:N0}");
            if (data.ErrArea > 0)
            {
                report.AppendLine($"[x] Wrong Biome Area: {data.ErrArea:N0}");
                PrintDict(report, "    └─ ", BoosterDiagnostics.BiomeAreaFailures, data.LocHash);
            }
            string zoneAreaName = data.Loc.m_biomeArea.ToString();

            report.AppendLine($"[!] Valid Zones: {data.ValidZones:N0}");
            report.AppendLine($"    └─ {zoneAreaName}");

            if (data.ValidZones <= 0 || data.InDist <= 0)
            {
                report.AppendLine("────────────────────────────────────────────────────────");
                BoosterDiagnostics.WriteLog(report.ToString(), level);
                return;
            }

            // --- PHASE 2 ---
            report.AppendLine();
            report.AppendLine($"PHASE 2 (Placement): {data.InDist:N0} Points Sampled in the {data.ValidZones:N0} {zoneAreaName} zones");

            List<FunnelStep> steps = new List<FunnelStep>();

            // Calculate Dynamic Max Distance
            float effectiveMax = data.Loc.m_maxDistance > 0.1f ? data.Loc.m_maxDistance : LocationBooster.WorldRadius.Value;

            // 1. Distance
            steps.Add(new FunnelStep
            {
                Name = "DISTANCE FILTER",
                ConfigInfo = $"(Min: {data.Loc.m_minDistance:F0}, Max: {effectiveMax:F0})",
                PassedContext = $"Range {data.Loc.m_minDistance:F0}-{effectiveMax:F0}",
                Input = data.InDist,
                Failures = data.ErrDist,
                FailurePrinter = (sb, indent) => PrintDist(sb, indent, data.InstanceHash)
            });

            // 2. Biome
            steps.Add(new FunnelStep
            {
                Name = "BIOME MATCH",
                ConfigInfo = $"(Required: {data.Loc.m_biome})",
                PassedContext = $"{data.Loc.m_biome}",
                Input = data.InBiome,
                Failures = data.ErrBiome,
                FailurePrinter = (sb, indent) => PrintDict(sb, $"{indent}    └─ ", BoosterDiagnostics.BiomeFailures, data.LocHash)
            });

            // 3. Altitude
            steps.Add(new FunnelStep
            {
                Name = "ALTITUDE CHECK",
                ConfigInfo = $"(Min: {data.Loc.m_minAltitude:F0}, Max: {data.Loc.m_maxAltitude:F0})",
                PassedContext = $"Alt {data.Loc.m_minAltitude:F0} to {data.Loc.m_maxAltitude:F0}",
                Input = data.InAlt,
                Failures = data.ErrAlt,
                FailurePrinter = (sb, indent) => PrintAlt(sb, $"{indent}    └─ ", data.InstanceHash)
            });

            // 4. Forest
            if (data.Loc.m_inForest)
            {
                steps.Add(new FunnelStep
                {
                    Name = "FOREST FACTOR",
                    ConfigInfo = $"(Min: {data.Loc.m_forestTresholdMin:F2}, Max: {data.Loc.m_forestTresholdMax:F2})",
                    PassedContext = $"Forest {data.Loc.m_forestTresholdMin:F2}-{data.Loc.m_forestTresholdMax:F2}",
                    Input = data.InForest,
                    Failures = data.ErrForest,
                    FailurePrinter = null
                });
            }

            // 5. Terrain
            steps.Add(new FunnelStep
            {
                Name = "TERRAIN DELTA",
                ConfigInfo = $"(Min: {data.Loc.m_minTerrainDelta:F1}, Max: {data.Loc.m_maxTerrainDelta:F1})",
                PassedContext = $"Delta {data.Loc.m_minTerrainDelta:F1} to {data.Loc.m_maxTerrainDelta:F1}",
                Input = data.InTerr,
                Failures = data.ErrTerrain,
                FailurePrinter = (sb, indent) => sb.AppendLine($"{indent}    └─ Slope/Flatness mismatch: {data.ErrTerrain:N0}")
            });

            // 6. Similarity
            string groupName = string.IsNullOrEmpty(data.Loc.m_group) ? "Default" : data.Loc.m_group;
            steps.Add(new FunnelStep
            {
                Name = "SIMILARITY CHECK",
                ConfigInfo = $"(Group: {groupName})",
                PassedContext = "Proximity Clear",
                Input = data.InSim,
                Failures = data.ErrSim + data.ErrNotSim,
                FailurePrinter = (sb, indent) => {
                    if (data.ErrSim > 0) sb.AppendLine($"{indent}    └─ Too Close: {data.ErrSim:N0}");
                    if (data.ErrNotSim > 0) sb.AppendLine($"{indent}    └─ Too Far: {data.ErrNotSim:N0}");
                }
            });

            // 7. Vegetation
            steps.Add(new FunnelStep
            {
                Name = "VEGETATION DENSITY",
                ConfigInfo = $"(Min: {data.Loc.m_minimumVegetation:F2}, Max: {data.Loc.m_maximumVegetation:F2})",
                PassedContext = "Density Match",
                Input = data.InVeg,
                Failures = data.ErrVeg,
                FailurePrinter = null
            });

            // Render
            string indent = "";

            for (int i = 0; i < steps.Count; i++)
            {
                var step = steps[i];
                int stepNum = i + 1;

                // Smart Collapse
                bool allFuturePerfect = true;
                for (int j = i; j < steps.Count; j++)
                {
                    if (steps[j].Failures > 0)
                    {
                        allFuturePerfect = false;
                        break;
                    }
                }

                if (allFuturePerfect)
                {
                    List<string> remainingNames = new List<string>();
                    for (int j = i; j < steps.Count; j++)
                        remainingNames.Add(steps[j].Name.Replace(" CHECK", "").Replace(" FILTER", "").Replace(" MATCH", ""));

                    string joinedNames = string.Join(" -> ", remainingNames);
                    report.AppendLine($"{indent}└─ PASSED REMAINING CHECKS ({joinedNames}): {step.Passed:N0}");
                    break;
                }

                // Normal Printing
                if (i == 0)
                {
                    report.AppendLine($"1. {step.Name} {step.ConfigInfo}");
                }
                else
                {
                    report.AppendLine($"{indent}└─ {stepNum}. {step.Name} {step.ConfigInfo}: {step.Input:N0} points checked");
                }

                string statusIndent = (i == 0) ? "" : indent + "   ";

                if (step.Failures > 0)
                {
                    report.AppendLine($"{statusIndent}[x] Failed: {step.Failures:N0}");
                    step.FailurePrinter?.Invoke(report, statusIndent);
                }

                if (step.Passed > 0)
                {
                    report.AppendLine($"{statusIndent}[!] Passed: {step.Passed:N0}");
                    report.AppendLine($"{statusIndent}    └─ {step.PassedContext}");
                    indent += "       ";
                    report.AppendLine($"{indent}|");
                }
                else
                {
                    break;
                }
            }

            report.AppendLine("────────────────────────────────────────────────────────");
            BoosterDiagnostics.WriteLog(report.ToString(), level);
        }

        // Helpers
        private static void PrintDict<T>(StringBuilder sb, string prefix, Dictionary<int, Dictionary<T, long>> source, int hash)
        {
            if (source.TryGetValue(hash, out var dict))
            {
                foreach (var kvp in dict.OrderByDescending(x => x.Value).Take(5))
                {
                    sb.AppendLine($"{prefix}{kvp.Key}: {kvp.Value:N0}");
                }
            }
        }

        private static void PrintDist(StringBuilder sb, string prefix, int hash)
        {
            long tooFar = BoosterDiagnostics.DistanceTooFar.ContainsKey(hash) ? BoosterDiagnostics.DistanceTooFar[hash] : 0;
            long tooClose = BoosterDiagnostics.DistanceTooClose.ContainsKey(hash) ? BoosterDiagnostics.DistanceTooClose[hash] : 0;
            if (tooClose > 0) sb.AppendLine($"{prefix}Below Min: {tooClose:N0}");
            if (tooFar > 0) sb.AppendLine($"{prefix}Above Max: {tooFar:N0}");
        }

        private static void PrintAlt(StringBuilder sb, string prefix, int hash)
        {
            // Print Low Failures
            if (BoosterDiagnostics.AltitudeTooLow.TryGetValue(hash, out var lowDict))
            {
                long totalLow = lowDict.Values.Sum();
                if (totalLow > 0)
                {
                    sb.AppendLine($"{prefix}Too Low: {totalLow:N0}");
                    foreach (var kvp in lowDict.OrderByDescending(x => x.Value))
                    {
                        var biome = kvp.Key;
                        string stats = "";
                        if (BoosterDiagnostics.AltLowStats.TryGetValue(hash, out var statDict) && statDict.TryGetValue(biome, out var s))
                        {
                            stats = s.GetString();
                        }
                        sb.AppendLine($"{prefix}   └─ {biome}: {kvp.Value:N0} {stats}");
                    }
                }
            }

            // Print High Failures
            if (BoosterDiagnostics.AltitudeTooHigh.TryGetValue(hash, out var highDict))
            {
                long totalHigh = highDict.Values.Sum();
                if (totalHigh > 0)
                {
                    sb.AppendLine($"{prefix}Too High: {totalHigh:N0}");
                    foreach (var kvp in highDict.OrderByDescending(x => x.Value))
                    {
                        var biome = kvp.Key;
                        string stats = "";
                        if (BoosterDiagnostics.AltHighStats.TryGetValue(hash, out var statDict) && statDict.TryGetValue(biome, out var s))
                        {
                            stats = s.GetString();
                        }
                        sb.AppendLine($"{prefix}   └─ {biome}: {kvp.Value:N0} {stats}");
                    }
                }
            }
        }
    }
}