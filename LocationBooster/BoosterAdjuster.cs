#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
        public static object CapturedOuterLoop = null;

        public static void CaptureStateMachine(object sm)
        {
            CapturedOuterLoop = sm;
        }

        public static void Reset()
        {
            RelaxationAttempts.Clear();
            _originalStats.Clear();
            CapturedOuterLoop = null;
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

            // 4. Cap m_quantity to the minimum needed for playability
            int minimumNeeded = BoosterGlobalProgress.GetMinimumNeededCount(prefabName, _originalStats[prefabName].Quantity);
            int alreadyPlaced = data.Placed;
            int toPlace = Mathf.Max(1, minimumNeeded - alreadyPlaced);
            if (toPlace < data.Loc.m_quantity)
            {
                data.Loc.m_quantity = toPlace;
                BoosterDiagnostics.WriteLog($"   -> Capped m_quantity to {toPlace} for relaxation (need {minimumNeeded}, have {alreadyPlaced}).");
            }

            // 5. Clear the survey cache so the retry rescans with new requirements
            BoosterSurveyPlus.ClearCache(data.LocHash);

            // 6. Insert immediately after the current index in the outer loop's <ordered>5__4 list.
            //    This makes the retry run next, while all previously-placed prioritized locations
            //    are already settled — giving relaxation the most relevant world state possible.
            bool inserted = false;
            if (CapturedOuterLoop != null)
            {
                var smType = CapturedOuterLoop.GetType();
                var orderedField = smType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                    .FirstOrDefault(f => f.FieldType == typeof(List<ZoneLocation>) && f.Name.Contains("ordered"));
                var indexField = smType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                    .FirstOrDefault(f => f.FieldType == typeof(int) && f.Name.Contains("<i>"));

                if (orderedField != null && indexField != null)
                {
                    var ordered = orderedField.GetValue(CapturedOuterLoop) as List<ZoneLocation>;
                    int idx = (int)indexField.GetValue(CapturedOuterLoop);
                    if (ordered != null)
                    {
                        int insertAt = Math.Min(idx + 1, ordered.Count);
                        ordered.Insert(insertAt, data.Loc);
                        inserted = true;
                        BoosterDiagnostics.WriteLog($"[Adjuster] {prefabName} inserted at index {insertAt} in <ordered> (current i={idx}) for immediate retry.");
                    }
                }
                else
                {
                    BoosterDiagnostics.WriteLog($"[Adjuster] WARNING: Could not reflect <ordered> or <i> fields on {CapturedOuterLoop.GetType().Name}.");
                }
            }

            if (!inserted)
            {
                BoosterDiagnostics.WriteLog($"[Adjuster] WARNING: Falling back to m_locations.Add for {prefabName} (CapturedOuterLoop={(CapturedOuterLoop == null ? "null" : "set")}).");
                var zs = ZoneSystem.instance;
                if (zs != null) zs.m_locations.Add(data.Loc);
            }

            // 7. Reset the location-log guard so the retry logs a fresh [START] entry
            BoosterPatches.ResetLocationLog();

            // 8. Notify GUI: amber "QUEUED FOR RETRY" until the retry actually starts
            BoosterGlobalProgress.MarkRelaxationQueued(prefabName);

            BoosterDiagnostics.WriteLog($"[Adjuster] {prefabName} re-queued for retry.");

            return true;
        }

        private static void ApplyRelaxation(ZoneLocation loc, string bottleneck, int attemptNumber, int maxAttempts)
        {
            float mag = LocationBooster.RelaxationMagnitude.Value;

            BoosterDiagnostics.WriteTimestampedLog(
                $"[Adjuster] RELAXING {loc.m_prefabName} (Attempt {attemptNumber}/{maxAttempts}). Bottleneck: {bottleneck}. Queued at end of list.",
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