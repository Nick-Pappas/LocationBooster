#nullable disable
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
        private class FunnelStep { public string Name; public string ConfigInfo; public string PassedContext; public long Input; public long Failures; public Action<StringBuilder, string> FailurePrinter; public long Passed => Input - Failures; }

        public static void WriteReport(ReportData data, bool isHeartbeat)
        {
            if (data == null) return;
            if (isHeartbeat) LogHeartbeat(data); else LogFullReport(data);
        }

        private static void LogHeartbeat(ReportData data)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine($"[PROGRESS] {data.Loc.m_prefabName}: {data.Placed}/{data.Loc.m_quantity}. Cost: {data.CurrentOuter:N0}/{data.LimitOuter:N0}");
            if (BoosterDiagnostics.GlobalMaxAltitudeSeen > float.MinValue) sb.AppendLine($"(World Altitude Profile: Min {BoosterDiagnostics.GlobalMinAltitudeSeen:F1}m, Max {BoosterDiagnostics.GlobalMaxAltitudeSeen:F1}m)");

            if (data.ErrZone > 0 || data.ErrArea > 0) { sb.AppendLine(" PHASE 1 FAILURES (Zone Search):"); if (data.ErrZone > 0) sb.AppendLine($"       - Zone Occupied            : {data.ErrZone,12:N0}"); if (data.ErrArea > 0) { sb.AppendLine($"       - Wrong Biome Area         : {data.ErrArea,12:N0}"); BoosterReporter.PrintDict(sb, "          └─ ", BoosterDiagnostics.BiomeAreaFailures, data.LocHash); } }

            sb.AppendLine(" PHASE 2 FAILURES (Placement Filters):");
            var failures = new List<(string Name, long Count, Action<StringBuilder, string> DetailsPrinter)>();
            if (data.ErrDist > 0) failures.Add(("Distance Filter", data.ErrDist, (s, pad) => BoosterReporter.PrintDist(s, pad, data.InstanceHash)));
            if (data.ErrBiome > 0) failures.Add(("Wrong Biome Type", data.ErrBiome, (s, pad) => BoosterReporter.PrintDict(s, pad, BoosterDiagnostics.BiomeFailures, data.LocHash)));
            if (data.ErrAlt > 0) failures.Add(("Wrong Altitude", data.ErrAlt, (s, pad) => BoosterReporter.PrintAlt(s, "          ", data.InstanceHash)));
            if (data.ErrForest > 0) failures.Add(("Forest Check", data.ErrForest, null));
            if (data.ErrTerrain > 0) failures.Add(("Terrain Check", data.ErrTerrain, null));
            if (data.ErrSim + data.ErrNotSim > 0) failures.Add(("Similarity Check", data.ErrSim + data.ErrNotSim, null));
            if (data.ErrVeg > 0) failures.Add(("Vegetation Density", data.ErrVeg, null));

            foreach (var fail in failures.OrderByDescending(x => x.Count).Take(5)) { sb.AppendLine($"       - {fail.Name.PadRight(25)}: {fail.Count,12:N0}"); fail.DetailsPrinter?.Invoke(sb, "          └─ "); }
            BoosterDiagnostics.WriteTimestampedLog(sb.ToString().TrimEnd());
        }

        private static void LogFullReport(ReportData data)
        {
            StringBuilder report = new StringBuilder();
            string status = data.IsComplete ? "COMPLETE" : "FAILURE";
            LogLevel level = data.IsComplete ? LogLevel.Info : LogLevel.Warning;

            report.AppendLine($"[{status}] {data.Loc.m_prefabName}: {data.Placed}/{data.Loc.m_quantity}. Cost: {data.CurrentOuter:N0}/{data.LimitOuter:N0} outer loop budget and {data.InDist:N0} inner loop iterations.");
            if (BoosterDiagnostics.GlobalMaxAltitudeSeen > float.MinValue) report.AppendLine($"(World Altitude Profile: Min {BoosterDiagnostics.GlobalMinAltitudeSeen:F1}m, Max {BoosterDiagnostics.GlobalMaxAltitudeSeen:F1}m)");
            report.AppendLine("────────────────────────────────────────────────────────");

            report.AppendLine($"PHASE 1 (Zone Search): {data.CurrentOuter:N0} Checks");
            if (data.ErrZone > 0) report.AppendLine($"[x] Occupied Zones: {data.ErrZone:N0}");
            if (data.ErrArea > 0) { report.AppendLine($"[x] Wrong Biome Area: {data.ErrArea:N0}"); BoosterReporter.PrintDict(report, "    └─ ", BoosterDiagnostics.BiomeAreaFailures, data.LocHash); }
            string zoneAreaName = data.Loc.m_biomeArea.ToString();
            report.AppendLine($"[!] Valid Zones: {data.ValidZones:N0}"); report.AppendLine($"    └─ {zoneAreaName}");

            if (data.ValidZones <= 0 || data.InDist <= 0) { report.AppendLine("────────────────────────────────────────────────────────"); BoosterDiagnostics.WriteTimestampedLog(report.ToString(), level); return; }

            report.AppendLine(); report.AppendLine($"PHASE 2 (Placement): {data.InDist:N0} Points Sampled in the {data.ValidZones:N0} {zoneAreaName} zones");
            List<FunnelStep> steps = new List<FunnelStep>();
            float effectiveMax = data.Loc.m_maxDistance > 0.1f ? data.Loc.m_maxDistance : LocationBooster.WorldRadius.Value;

            steps.Add(new FunnelStep { Name = "DISTANCE FILTER", ConfigInfo = $"(Min: {data.Loc.m_minDistance:F0}, Max: {effectiveMax:F0})", PassedContext = $"Range {data.Loc.m_minDistance:F0}-{effectiveMax:F0}", Input = data.InDist, Failures = data.ErrDist, FailurePrinter = (sb, indent) => BoosterReporter.PrintDist(sb, indent, data.InstanceHash) });
            steps.Add(new FunnelStep { Name = "BIOME MATCH", ConfigInfo = $"(Required: {data.Loc.m_biome})", PassedContext = $"{data.Loc.m_biome}", Input = data.InBiome, Failures = data.ErrBiome, FailurePrinter = (sb, indent) => BoosterReporter.PrintDict(sb, $"{indent}    └─ ", BoosterDiagnostics.BiomeFailures, data.LocHash) });
            steps.Add(new FunnelStep { Name = "ALTITUDE CHECK", ConfigInfo = $"(Min: {data.Loc.m_minAltitude:F0}, Max: {data.Loc.m_maxAltitude:F0})", PassedContext = $"Alt {data.Loc.m_minAltitude:F0} to {data.Loc.m_maxAltitude:F0}", Input = data.InAlt, Failures = data.ErrAlt, FailurePrinter = (sb, indent) => BoosterReporter.PrintAlt(sb, $"{indent}    ", data.InstanceHash) });
            if (data.Loc.m_inForest) { steps.Add(new FunnelStep { Name = "FOREST FACTOR", ConfigInfo = $"(Min: {data.Loc.m_forestTresholdMin:F2}, Max: {data.Loc.m_forestTresholdMax:F2})", PassedContext = $"Forest {data.Loc.m_forestTresholdMin:F2}-{data.Loc.m_forestTresholdMax:F2}", Input = data.InForest, Failures = data.ErrForest }); }
            steps.Add(new FunnelStep { Name = "TERRAIN DELTA", ConfigInfo = $"(Min: {data.Loc.m_minTerrainDelta:F1}, Max: {data.Loc.m_maxTerrainDelta:F1})", PassedContext = $"Delta {data.Loc.m_minTerrainDelta:F1} to {data.Loc.m_maxTerrainDelta:F1}", Input = data.InTerr, Failures = data.ErrTerrain, FailurePrinter = (sb, indent) => sb.AppendLine($"{indent}    └─ Slope/Flatness mismatch: {data.ErrTerrain:N0}") });
            string groupName = string.IsNullOrEmpty(data.Loc.m_group) ? "Default" : data.Loc.m_group;
            steps.Add(new FunnelStep { Name = "SIMILARITY CHECK", ConfigInfo = $"(Group: {groupName})", PassedContext = "Proximity Clear", Input = data.InSim, Failures = data.ErrSim + data.ErrNotSim, FailurePrinter = (sb, indent) => { if (data.ErrSim > 0) sb.AppendLine($"{indent}    └─ Too Close: {data.ErrSim:N0}"); if (data.ErrNotSim > 0) sb.AppendLine($"{indent}    └─ Too Far: {data.ErrNotSim:N0}"); } });
            steps.Add(new FunnelStep { Name = "VEGETATION DENSITY", ConfigInfo = $"(Min: {data.Loc.m_minimumVegetation:F2}, Max: {data.Loc.m_maximumVegetation:F2})", PassedContext = "Density Match", Input = data.InVeg, Failures = data.ErrVeg });

            string indent = "";
            for (int i = 0; i < steps.Count; i++) { var step = steps[i]; if (i > 0 && steps[i - 1].Passed == 0) break; bool allFuturePerfect = steps.Skip(i).All(s => s.Failures == 0); if (allFuturePerfect) { string joinedNames = string.Join(" -> ", steps.Skip(i).Select(s => s.Name.Replace(" CHECK", "").Replace(" FILTER", "").Replace(" MATCH", ""))); report.AppendLine($"{indent}└─ PASSED REMAINING CHECKS ({joinedNames}): {step.Passed:N0}"); break; } if (i == 0) report.AppendLine($"1. {step.Name} {step.ConfigInfo}"); else report.AppendLine($"{indent}└─ {i + 1}. {step.Name} {step.ConfigInfo}: {step.Input:N0} points checked"); string statusIndent = (i == 0) ? "" : indent + "   "; if (step.Failures > 0) { report.AppendLine($"{statusIndent}[x] Failed: {step.Failures:N0}"); step.FailurePrinter?.Invoke(report, statusIndent); } if (step.Passed > 0) { report.AppendLine($"{statusIndent}[!] Passed: {step.Passed:N0}"); report.AppendLine($"{statusIndent}    └─ {step.PassedContext}"); indent += "       "; report.AppendLine($"{indent}|"); } }
            report.AppendLine("────────────────────────────────────────────────────────");
            BoosterDiagnostics.WriteTimestampedLog(report.ToString(), level);
        }

        private static void PrintDict<T>(StringBuilder sb, string prefix, Dictionary<int, Dictionary<T, long>> source, int hash) { if (source.TryGetValue(hash, out var dict)) { foreach (var kvp in dict.OrderByDescending(x => x.Value).Take(5)) { sb.AppendLine($"{prefix}{kvp.Key}: {kvp.Value:N0}"); } } }
        private static void PrintDist(StringBuilder sb, string prefix, int hash) { if (BoosterDiagnostics.DistanceTooClose.TryGetValue(hash, out long tooClose) && tooClose > 0) sb.AppendLine($"{prefix}Below Min: {tooClose:N0}"); if (BoosterDiagnostics.DistanceTooFar.TryGetValue(hash, out long tooFar) && tooFar > 0) sb.AppendLine($"{prefix}Above Max: {tooFar:N0}"); }
        private static void PrintAlt(StringBuilder sb, string prefix, int hash)
        {
            var allBiomes = new HashSet<Heightmap.Biome>();
            if (BoosterDiagnostics.AltitudeTooLow_Standard.ContainsKey(hash)) foreach (var b in BoosterDiagnostics.AltitudeTooLow_Standard[hash].Keys) allBiomes.Add(b);
            if (BoosterDiagnostics.AltitudeTooLow_Anomalous.ContainsKey(hash)) foreach (var b in BoosterDiagnostics.AltitudeTooLow_Anomalous[hash].Keys) allBiomes.Add(b);
            if (BoosterDiagnostics.AltitudeTooLow_Underwater.ContainsKey(hash)) foreach (var b in BoosterDiagnostics.AltitudeTooLow_Underwater[hash].Keys) allBiomes.Add(b);
            if (BoosterDiagnostics.AltitudeTooHigh.ContainsKey(hash)) foreach (var b in BoosterDiagnostics.AltitudeTooHigh[hash].Keys) allBiomes.Add(b);

            long totalLow = (BoosterDiagnostics.AltitudeTooLow_Standard.ContainsKey(hash) ? BoosterDiagnostics.AltitudeTooLow_Standard[hash].Values.Sum() : 0) + (BoosterDiagnostics.AltitudeTooLow_Anomalous.ContainsKey(hash) ? BoosterDiagnostics.AltitudeTooLow_Anomalous[hash].Values.Sum() : 0) + (BoosterDiagnostics.AltitudeTooLow_Underwater.ContainsKey(hash) ? BoosterDiagnostics.AltitudeTooLow_Underwater[hash].Values.Sum() : 0);

            if (totalLow > 0) { sb.AppendLine($"{prefix}└─ Too Low: {totalLow:N0}"); foreach (var biome in allBiomes.OrderBy(b => b.ToString())) { bool hasWater = BoosterDiagnostics.AltitudeTooLow_Underwater.TryGetValue(hash, out var watDict) && watDict.ContainsKey(biome); bool hasAnom = BoosterDiagnostics.AltitudeTooLow_Anomalous.TryGetValue(hash, out var anoDict) && anoDict.ContainsKey(biome); bool hasStd = BoosterDiagnostics.AltitudeTooLow_Standard.TryGetValue(hash, out var stdDict) && stdDict.ContainsKey(biome); if (!hasWater && !hasAnom && !hasStd) continue; sb.AppendLine($"{prefix}   └─ {biome}:"); if (hasWater) { string stats = BoosterDiagnostics.AltLowStats_Underwater[hash][biome].GetString(); string lineEnd = (hasAnom || hasStd) ? "├─" : "└─"; sb.AppendLine($"{prefix}      {lineEnd} Underwater (<0m): {watDict[biome]:N0} {stats}"); } if (hasAnom) { string stats = BoosterDiagnostics.AltLowStats_Anomalous[hash][biome].GetString(); float floor = BoosterDiagnostics.GetAnomalyFloor(biome); string lineEnd = hasStd ? "├─" : "└─"; sb.AppendLine($"{prefix}      {lineEnd} Anomalous (0m to {floor:F0}m): {anoDict[biome]:N0} {stats}"); } if (hasStd) { string stats = BoosterDiagnostics.AltLowStats_Standard[hash][biome].GetString(); sb.AppendLine($"{prefix}      └─ Standard Failures: {stdDict[biome]:N0} {stats}"); } } }
            if (BoosterDiagnostics.AltitudeTooHigh.TryGetValue(hash, out var highDict)) { long totalHigh = highDict.Values.Sum(); if (totalHigh > 0) { sb.AppendLine($"{prefix}└─ Too High: {totalHigh:N0}"); foreach (var kvp in highDict.OrderByDescending(x => x.Value)) { string stats = ""; if (BoosterDiagnostics.AltHighStats.TryGetValue(hash, out var statDict) && statDict.TryGetValue(kvp.Key, out var s)) stats = s.GetString(); sb.AppendLine($"{prefix}   └─ {kvp.Key}: {kvp.Value:N0} {stats}"); } } }
        }
    }
}