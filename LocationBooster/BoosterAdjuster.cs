#nullable disable
using System;
using System.Collections.Generic;
using UnityEngine;
using static ZoneSystem;

namespace LocationBudgetBooster
{
    public static class BoosterAdjuster
    {
        public class OriginalStats
        {
            public float MinAlt, MaxAlt, MinDist, MaxDist, MinTerr, MaxTerr, ExtRad;
            public int Quantity;
        }

        public static Dictionary<string, int> RelaxationAttempts = new Dictionary<string, int>();
        private static Dictionary<string, OriginalStats> _originalStats = new Dictionary<string, OriginalStats>();

        public static void Reset()
        {
            RelaxationAttempts.Clear();
            _originalStats.Clear();
        }

        /// <summary>
        /// Restores all m_quantity values that were capped during relaxation.
        /// Called from EndGeneration before final verdict.
        /// </summary>
        public static void RestoreQuantities()
        {
            var zs = ZoneSystem.instance;
            if (zs == null) return;

            foreach (var kvp in _originalStats)
            {
                var loc = zs.m_locations.Find(l => l.m_prefabName == kvp.Key);
                if (loc != null && loc.m_quantity != kvp.Value.Quantity)
                {
                    loc.m_quantity = kvp.Value.Quantity;
                    BoosterDiagnostics.WriteLog($"[Adjuster] Restored {kvp.Key} m_quantity to {kvp.Value.Quantity}.");
                }
            }
        }

        public static bool TryRelax(ReportData data)
        {
            if (data == null || data.Loc == null) return false;

            int maxAttempts = LocationBooster.MaxRelaxationAttempts.Value;
            if (maxAttempts <= 0) return false;

            string prefabName = data.Loc.m_prefabName;

            // 1. Check if this prefab qualifies for relaxation
            if (!BoosterGlobalProgress.NeedsRelaxation(prefabName, data.Placed, data.Loc.m_quantity))
                return false;

            // 2. Track attempts & cache original stats (first time only)
            if (!RelaxationAttempts.ContainsKey(prefabName))
            {
                RelaxationAttempts[prefabName] = 0;
                _originalStats[prefabName] = new OriginalStats
                {
                    MinAlt = data.Loc.m_minAltitude,
                    MaxAlt = data.Loc.m_maxAltitude,
                    MinDist = data.Loc.m_minDistance,
                    MaxDist = data.Loc.m_maxDistance,
                    MinTerr = data.Loc.m_minTerrainDelta,
                    MaxTerr = data.Loc.m_maxTerrainDelta,
                    ExtRad = data.Loc.m_exteriorRadius,
                    Quantity = data.Loc.m_quantity
                };
            }

            int attempts = RelaxationAttempts[prefabName];
            if (attempts >= maxAttempts)
            {
                BoosterDiagnostics.WriteTimestampedLog(
                    $"[Adjuster] {prefabName} failed after {maxAttempts} relaxation attempts. Abandoning.",
                    BepInEx.Logging.LogLevel.Warning);
                return false;
            }

            RelaxationAttempts[prefabName] = attempts + 1;

            // 3. Determine the primary bottleneck
            long maxErr = -1;
            string bottleneck = "Unknown";
            if (data.ErrAlt > maxErr) { maxErr = data.ErrAlt; bottleneck = "Altitude"; }
            if (data.ErrDist > maxErr) { maxErr = data.ErrDist; bottleneck = "Distance"; }
            if (data.ErrTerrain > maxErr) { maxErr = data.ErrTerrain; bottleneck = "Terrain"; }
            if (data.ErrSim + data.ErrNotSim > maxErr) { maxErr = data.ErrSim + data.ErrNotSim; bottleneck = "Similarity"; }

            ApplyRelaxation(data.Loc, bottleneck, attempts + 1, maxAttempts);

            // 4. Cap m_quantity to the minimum needed for playability.
            //    Necessities: place exactly 1 (any 1 makes the game playable).
            //    Secondaries: place just enough to reach the threshold.
            int minimumNeeded = BoosterGlobalProgress.GetMinimumNeededCount(prefabName, _originalStats[prefabName].Quantity);
            int alreadyPlaced = data.Placed;
            int toPlace = Mathf.Max(1, minimumNeeded - alreadyPlaced);
            if (toPlace < data.Loc.m_quantity)
            {
                data.Loc.m_quantity = toPlace;
                BoosterDiagnostics.WriteLog($"   -> Capped m_quantity to {toPlace} for relaxation (need {minimumNeeded}, have {alreadyPlaced}).");
            }

            // 5. Clear the survey cache so the next pass rescans with the new requirements
            BoosterSurveyPlus.ClearCache(data.LocHash);

            // 6. Notify progress system — keeps GUI red until placement is confirmed
            BoosterGlobalProgress.MarkRelaxationPending(prefabName);

            // 7. Re-queue for immediate retry
            var zs = ZoneSystem.instance;
            if (zs != null)
            {
                int currentIndex = zs.m_locations.IndexOf(data.Loc);
                if (currentIndex >= 0 && currentIndex < zs.m_locations.Count)
                    zs.m_locations.Insert(currentIndex + 1, data.Loc);
                else
                    zs.m_locations.Add(data.Loc);
            }

            // 8. Reset the location-log guard so the retry gets a fresh [START] entry in the log
            BoosterPatches.ResetLocationLog();
            //BoosterDiagnostics.WriteLog($"[DEBUG-TR] TryRelax returning true for {prefabName}, re-queued at index {(zs?.m_locations.IndexOf(data.Loc) ?? -1)}"); // DEBUG REMOVE
            return true;
        }

        private static void ApplyRelaxation(ZoneLocation loc, string bottleneck, int attemptNumber, int maxAttempts)
        {
            float mag = LocationBooster.RelaxationMagnitude.Value;

            BoosterDiagnostics.WriteTimestampedLog(
                $"[Adjuster] RELAXING {loc.m_prefabName} (Attempt {attemptNumber}/{maxAttempts}). Bottleneck: {bottleneck}. Re-queueing immediately.",
                BepInEx.Logging.LogLevel.Message);

            if (bottleneck == "Altitude")
            {
                float stepDown = loc.m_minAltitude - Mathf.Max(5f, Mathf.Abs(loc.m_minAltitude) * mag);

                if (LocationBooster.EnableAltitudeMapping.Value &&
                    BoosterDiagnostics.GlobalMaxAltitudeSeen > float.MinValue &&
                    loc.m_minAltitude > 0)
                {
                    float dataDriven = BoosterDiagnostics.GlobalMaxAltitudeSeen - 2f;
                    float original = loc.m_minAltitude;
                    loc.m_minAltitude = Mathf.Min(stepDown, dataDriven);
                    BoosterDiagnostics.WriteLog($"   -> MinAltitude adjusted from {original:F0}m to {loc.m_minAltitude:F0}m (Data-Driven Max was {BoosterDiagnostics.GlobalMaxAltitudeSeen:F0}m)");
                }
                else
                {
                    float original = loc.m_minAltitude;
                    loc.m_minAltitude = stepDown;
                    BoosterDiagnostics.WriteLog($"   -> MinAltitude stepped down from {original:F0}m to {loc.m_minAltitude:F0}m");
                }

                loc.m_maxAltitude += Mathf.Max(10f, Mathf.Abs(loc.m_maxAltitude) * mag);
            }
            else if (bottleneck == "Distance")
            {
                float maxDist = loc.m_maxDistance > 0.1f ? loc.m_maxDistance : LocationBooster.WorldRadius.Value;
                loc.m_maxDistance = maxDist + (maxDist * mag);
                loc.m_minDistance = Mathf.Max(0f, loc.m_minDistance - (loc.m_minDistance * mag));
            }
            else if (bottleneck == "Terrain")
            {
                loc.m_maxTerrainDelta += Mathf.Max(2f, loc.m_maxTerrainDelta * mag);
                loc.m_minTerrainDelta = Mathf.Max(0f, loc.m_minTerrainDelta - (loc.m_minTerrainDelta * mag));
            }
            else if (bottleneck == "Similarity")
            {
                loc.m_exteriorRadius = Mathf.Max(0f, loc.m_exteriorRadius - (loc.m_exteriorRadius * mag));
            }
            else
            {
                loc.m_maxTerrainDelta += 5f;
                float maxDist = loc.m_maxDistance > 0.1f ? loc.m_maxDistance : LocationBooster.WorldRadius.Value;
                loc.m_maxDistance = maxDist * 1.1f;
                loc.m_minAltitude -= 10f;
            }
        }

        public static string GetRelaxationSummary(string prefabName, ZoneLocation currentLoc)
        {
            if (!RelaxationAttempts.TryGetValue(prefabName, out int attempts) || attempts == 0) return "";
            if (!_originalStats.TryGetValue(prefabName, out var orig)) return $"(Relaxed {attempts} times)";

            var changes = new List<string>();
            if (Mathf.Abs(currentLoc.m_minAltitude - orig.MinAlt) > 1f) changes.Add($"MinAlt: {orig.MinAlt:F0}->{currentLoc.m_minAltitude:F0}");
            if (Mathf.Abs(currentLoc.m_maxDistance - orig.MaxDist) > 1f) changes.Add($"MaxDist: {orig.MaxDist:F0}->{currentLoc.m_maxDistance:F0}");
            if (Mathf.Abs(currentLoc.m_minDistance - orig.MinDist) > 1f) changes.Add($"MinDist: {orig.MinDist:F0}->{currentLoc.m_minDistance:F0}");
            if (Mathf.Abs(currentLoc.m_maxTerrainDelta - orig.MaxTerr) > 0.1f) changes.Add($"MaxTerr: {orig.MaxTerr:F1}->{currentLoc.m_maxTerrainDelta:F1}");
            if (Mathf.Abs(currentLoc.m_exteriorRadius - orig.ExtRad) > 1f) changes.Add($"ExtRadius: {orig.ExtRad:F0}->{currentLoc.m_exteriorRadius:F0}");

            if (changes.Count == 0) return $"(Relaxed {attempts} times)";
            return $"(Relaxed {attempts}x: {string.Join(", ", changes)})";
        }
    }
}