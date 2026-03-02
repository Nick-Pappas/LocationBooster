#nullable disable
using BepInEx;
using BepInEx.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using static ZoneSystem;

namespace LocationBudgetBooster
{
    public enum HeartbeatType
    {
        Outer,
        Inner
    }

    public static class BoosterDiagnostics
    {
        private static StreamWriter _logWriter;
        private static long _totalInnerIterations = 0;
        private static long _lastInnerLogValue = 0;

        // --- Data Stores ---
        public static Dictionary<int, Dictionary<Heightmap.Biome, long>> BiomeFailures = new Dictionary<int, Dictionary<Heightmap.Biome, long>>();
        public static Dictionary<int, Dictionary<Heightmap.BiomeArea, long>> BiomeAreaFailures = new Dictionary<int, Dictionary<Heightmap.BiomeArea, long>>();
        public static Dictionary<int, Dictionary<Heightmap.Biome, long>> AltitudeTooHigh = new Dictionary<int, Dictionary<Heightmap.Biome, long>>();
        public static Dictionary<int, Dictionary<Heightmap.Biome, long>> AltitudeTooLow_Standard = new Dictionary<int, Dictionary<Heightmap.Biome, long>>();
        public static Dictionary<int, Dictionary<Heightmap.Biome, long>> AltitudeTooLow_Anomalous = new Dictionary<int, Dictionary<Heightmap.Biome, long>>();
        public static Dictionary<int, Dictionary<Heightmap.Biome, long>> AltitudeTooLow_Underwater = new Dictionary<int, Dictionary<Heightmap.Biome, long>>();

        public class AltitudeStat
        {
            public float Min = float.MaxValue;
            public float Max = float.MinValue;
            public double Sum = 0;
            public long Count = 0;
            public void Add(float value) { if (value < Min) Min = value; if (value > Max) Max = value; Sum += value; Count++; }
            public string GetString() { if (Count == 0) return ""; return $"[Observed: Min {Min:F1}m, Avg {(Sum / Count):F1}m, Max {Max:F1}m]"; }
        }

        public static Dictionary<int, Dictionary<Heightmap.Biome, AltitudeStat>> AltHighStats = new Dictionary<int, Dictionary<Heightmap.Biome, AltitudeStat>>();
        public static Dictionary<int, Dictionary<Heightmap.Biome, AltitudeStat>> AltLowStats_Standard = new Dictionary<int, Dictionary<Heightmap.Biome, AltitudeStat>>();
        public static Dictionary<int, Dictionary<Heightmap.Biome, AltitudeStat>> AltLowStats_Anomalous = new Dictionary<int, Dictionary<Heightmap.Biome, AltitudeStat>>();
        public static Dictionary<int, Dictionary<Heightmap.Biome, AltitudeStat>> AltLowStats_Underwater = new Dictionary<int, Dictionary<Heightmap.Biome, AltitudeStat>>();
        public static Dictionary<int, long> DistanceTooClose = new Dictionary<int, long>();
        public static Dictionary<int, long> DistanceTooFar = new Dictionary<int, long>();
        public static Dictionary<int, Dictionary<string, long>> ShadowCounters = new Dictionary<int, Dictionary<string, long>>();

        public static int FilterTotalCalls = 0;
        public static int FilterAcceptedZones = 0;
        public static float GlobalMinAltitudeSeen = float.MaxValue;
        public static float GlobalMaxAltitudeSeen = float.MinValue;

        public static void Initialize(string version)
        {
            if (LocationBooster.WriteToFile.Value)
            {
                try
                {
                    string logPath = Path.Combine(Paths.BepInExRootPath, "LocationBooster.log");
                    _logWriter = new StreamWriter(logPath, false) { AutoFlush = true };
                    _logWriter.WriteLine($"=== Location Budget Booster v{version} ===");
                }
                catch { }
            }
        }

        public static void Dispose() => _logWriter?.Close();

        public static void WriteLog(string message, LogLevel level = LogLevel.Info)
        {
            LocationBooster.Log.Log(level, message);
            _logWriter?.WriteLine($"[{level}] {message}");
        }

        public static void WriteTimestampedLog(string message, LogLevel level = LogLevel.Info)
        {
            string ts = DateTime.Now.ToString("HH:mm:ss.fff");
            string msg = $"[{ts}] {message}";
            LocationBooster.Log.Log(level, msg);
            _logWriter?.WriteLine($"[{level}]{msg}");
        }

        public static void ResetInnerLoopCounter()
        {
            _totalInnerIterations = 0;
            _lastInnerLogValue = 0;
        }

        public static void LogInnerLoopProgress(object instance)
        {
            _totalInnerIterations++;
            int interval = LocationBooster.InnerProgressInterval.Value;
            if (interval <= 0 || _totalInnerIterations < _lastInnerLogValue + interval) return;

            _lastInnerLogValue = _totalInnerIterations;

            var data = BoosterAnalyzer.Analyze(instance);
            BoosterReporter.WriteReport(data, true, HeartbeatType.Inner);
        }

        public static void LogLocationStart(ZoneSystem.ZoneLocation location, BoosterMode mode)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"[START] Placing [{location.m_prefabName}]: {location.m_quantity} locations");

            // Build requirements line
            var reqs = new List<string>();
            if (location.m_biome != 0) reqs.Add($"{location.m_biome}");
            float maxDist = location.m_maxDistance > 0.1f ? location.m_maxDistance : LocationBooster.WorldRadius.Value;
            if (location.m_minDistance > 0 || maxDist < LocationBooster.WorldRadius.Value)
                reqs.Add($"Distance: {location.m_minDistance:F0}-{maxDist:F0}m");
            if (location.m_minAltitude > -1000 || location.m_maxAltitude < 10000)
                reqs.Add($"Altitude: {location.m_minAltitude:F0}-{location.m_maxAltitude:F0}m");
            if (location.m_minTerrainDelta > 0 || location.m_maxTerrainDelta < 100)
                reqs.Add($"Terrain: {location.m_minTerrainDelta:F1}-{location.m_maxTerrainDelta:F1}");
            if (location.m_inForest)
                reqs.Add($"Forest: {location.m_forestTresholdMin:F2}-{location.m_forestTresholdMax:F2}");
            if (reqs.Count > 0)
                sb.AppendLine($"       Requires: {string.Join(" | ", reqs)}");

            // Survey mode specific: show candidate count
            if (mode == BoosterMode.Survey)
            {
                int candidateCount = BoosterSurvey.GetCandidateCount(location);
                sb.AppendLine($"       Valid Zones: {candidateCount:N0}");
            }

            // World altitude profile
            if (GlobalMaxAltitudeSeen > float.MinValue)
                sb.AppendLine($"       World Altitude: Min {GlobalMinAltitudeSeen:F1}m, Max {GlobalMaxAltitudeSeen:F1}m");

            sb.AppendLine("*****************************************");

            WriteTimestampedLog(sb.ToString().TrimEnd());
        }

        public static void TrackGlobalAltitude(float altitude)
        {
            if (altitude < GlobalMinAltitudeSeen) GlobalMinAltitudeSeen = altitude;
            if (altitude > GlobalMaxAltitudeSeen) GlobalMaxAltitudeSeen = altitude;
        }

        public static void IncrementShadow(object instance, string fieldName)
        {
            int hash = instance.GetHashCode();
            if (!ShadowCounters.ContainsKey(hash)) ShadowCounters[hash] = new Dictionary<string, long>();
            if (!ShadowCounters[hash].ContainsKey(fieldName)) ShadowCounters[hash][fieldName] = 0;
            ShadowCounters[hash][fieldName]++;
        }

        public static void CaptureWrongBiome(Vector3 point, Heightmap.Biome __result)
        {
            if (BoosterReflection.CurrentLocationForFilter == null) return;
            if ((BoosterReflection.CurrentLocationForFilter.m_biome & __result) != 0) return;
            int hash = BoosterReflection.CurrentLocationForFilter.GetHashCode();
            if (!BiomeFailures.ContainsKey(hash)) BiomeFailures[hash] = new Dictionary<Heightmap.Biome, long>();
            if (!BiomeFailures[hash].ContainsKey(__result)) BiomeFailures[hash][__result] = 0;
            BiomeFailures[hash][__result]++;
        }

        public static void CaptureWrongBiomeArea(Vector3 point, Heightmap.BiomeArea __result)
        {
            if (BoosterReflection.CurrentLocationForFilter == null) return;
            if ((BoosterReflection.CurrentLocationForFilter.m_biomeArea & __result) != 0) return;
            int hash = BoosterReflection.CurrentLocationForFilter.GetHashCode();
            if (!BiomeAreaFailures.ContainsKey(hash)) BiomeAreaFailures[hash] = new Dictionary<Heightmap.BiomeArea, long>();
            if (!BiomeAreaFailures[hash].ContainsKey(__result)) BiomeAreaFailures[hash][__result] = 0;
            BiomeAreaFailures[hash][__result]++;
        }

        public static float GetAnomalyFloor(Heightmap.Biome biome)
        {
            switch (biome) { case Heightmap.Biome.Mountain: return 50f; case Heightmap.Biome.Plains: case Heightmap.Biome.BlackForest: case Heightmap.Biome.Meadows: case Heightmap.Biome.Swamp: return 1f; default: return -10000f; }
        }

        public static void TrackAltitudeFailure(object instance, float height, float minAlt, float maxAlt, Vector3 point)
        {
            int hash = instance.GetHashCode();
            Heightmap.Biome biome = WorldGenerator.instance.GetBiome(point);
            if (height > maxAlt)
            {
                if (!AltitudeTooHigh.ContainsKey(hash)) AltitudeTooHigh[hash] = new Dictionary<Heightmap.Biome, long>();
                if (!AltitudeTooHigh[hash].ContainsKey(biome)) AltitudeTooHigh[hash][biome] = 0;
                AltitudeTooHigh[hash][biome]++;
                if (!AltHighStats.ContainsKey(hash)) AltHighStats[hash] = new Dictionary<Heightmap.Biome, AltitudeStat>();
                if (!AltHighStats[hash].ContainsKey(biome)) AltHighStats[hash][biome] = new AltitudeStat();
                AltHighStats[hash][biome].Add(height);
            }
            else if (height < minAlt)
            {
                if (height < 0f)
                {
                    if (!AltitudeTooLow_Underwater.ContainsKey(hash)) AltitudeTooLow_Underwater[hash] = new Dictionary<Heightmap.Biome, long>();
                    if (!AltitudeTooLow_Underwater[hash].ContainsKey(biome)) AltitudeTooLow_Underwater[hash][biome] = 0;
                    AltitudeTooLow_Underwater[hash][biome]++;
                    if (!AltLowStats_Underwater.ContainsKey(hash)) AltLowStats_Underwater[hash] = new Dictionary<Heightmap.Biome, AltitudeStat>();
                    if (!AltLowStats_Underwater[hash].ContainsKey(biome)) AltLowStats_Underwater[hash][biome] = new AltitudeStat();
                    AltLowStats_Underwater[hash][biome].Add(height);
                }
                else
                {
                    float anomalyFloor = GetAnomalyFloor(biome);
                    if (height < anomalyFloor)
                    {
                        if (!AltitudeTooLow_Anomalous.ContainsKey(hash)) AltitudeTooLow_Anomalous[hash] = new Dictionary<Heightmap.Biome, long>();
                        if (!AltitudeTooLow_Anomalous[hash].ContainsKey(biome)) AltitudeTooLow_Anomalous[hash][biome] = 0;
                        AltitudeTooLow_Anomalous[hash][biome]++;
                        if (!AltLowStats_Anomalous.ContainsKey(hash)) AltLowStats_Anomalous[hash] = new Dictionary<Heightmap.Biome, AltitudeStat>();
                        if (!AltLowStats_Anomalous[hash].ContainsKey(biome)) AltLowStats_Anomalous[hash][biome] = new AltitudeStat();
                        AltLowStats_Anomalous[hash][biome].Add(height);
                    }
                    else
                    {
                        if (!AltitudeTooLow_Standard.ContainsKey(hash)) AltitudeTooLow_Standard[hash] = new Dictionary<Heightmap.Biome, long>();
                        if (!AltitudeTooLow_Standard[hash].ContainsKey(biome)) AltitudeTooLow_Standard[hash][biome] = 0;
                        AltitudeTooLow_Standard[hash][biome]++;
                        if (!AltLowStats_Standard.ContainsKey(hash)) AltLowStats_Standard[hash] = new Dictionary<Heightmap.Biome, AltitudeStat>();
                        if (!AltLowStats_Standard[hash].ContainsKey(biome)) AltLowStats_Standard[hash][biome] = new AltitudeStat();
                        AltLowStats_Standard[hash][biome].Add(height);
                    }
                }
            }
        }

        public static void TrackDistanceFailure(object instance, float distance, float minDist, float maxDist)
        {
            int hash = instance.GetHashCode();
            if (maxDist != 0f && distance > maxDist) { if (!DistanceTooFar.ContainsKey(hash)) DistanceTooFar[hash] = 0; DistanceTooFar[hash]++; }
            else if (distance < minDist) { if (!DistanceTooClose.ContainsKey(hash)) DistanceTooClose[hash] = 0; DistanceTooClose[hash]++; }
        }

        public static void LogProgress(object instance)
        {
            int interval = LocationBooster.ProgressInterval.Value;
            if (interval <= 0) return;
            var type = instance.GetType();
            if (!BoosterReflection.CounterFields.TryGetValue(type, out var field)) return;
            long current = Convert.ToInt64(field.GetValue(instance));
            if (current < interval || current % interval != 0) return;
            var data = BoosterAnalyzer.Analyze(instance);
            BoosterReporter.WriteReport(data, true, HeartbeatType.Outer);
        }

        public static void ReportFailure(object instance)
        {
            var data = BoosterAnalyzer.Analyze(instance);
            BoosterReporter.WriteReport(data, false);
            Cleanup(data?.InstanceHash ?? 0, data?.LocHash ?? 0);
        }

        public static void ReportSuccess(object instance)
        {
            if (!LocationBooster.LogSuccesses.Value) return;
            var loc = BoosterReflection.GetLocation(instance);
            if (BoosterReflection.CachedOccupiedZone == null && BoosterReflection.ZoneIDFields.TryGetValue(instance.GetType(), out var zField))
                BoosterReflection.CachedOccupiedZone = (Vector2i)zField.GetValue(instance);

            var type = instance.GetType();
            int placed = (int)BoosterReflection.PlacedFields[type].GetValue(instance);
            if (placed + 1 < loc.m_quantity) return;
            var data = BoosterAnalyzer.Analyze(instance, placed + 1);
            BoosterReporter.WriteReport(data, false);
            Cleanup(data?.InstanceHash ?? 0, data?.LocHash ?? 0);
        }

        private static void Cleanup(int instanceHash, int locHash)
        {
            if (instanceHash != 0)
            {
                ShadowCounters.Remove(instanceHash); AltitudeTooHigh.Remove(instanceHash); AltitudeTooLow_Standard.Remove(instanceHash);
                AltitudeTooLow_Anomalous.Remove(instanceHash); AltitudeTooLow_Underwater.Remove(instanceHash); DistanceTooClose.Remove(instanceHash);
                DistanceTooFar.Remove(instanceHash); AltHighStats.Remove(instanceHash); AltLowStats_Standard.Remove(instanceHash);
                AltLowStats_Anomalous.Remove(instanceHash); AltLowStats_Underwater.Remove(instanceHash);
            }
            if (locHash != 0) { if (BiomeFailures.ContainsKey(locHash)) BiomeFailures[locHash].Clear(); if (BiomeAreaFailures.ContainsKey(locHash)) BiomeAreaFailures[locHash].Clear(); }
        }
    }
}