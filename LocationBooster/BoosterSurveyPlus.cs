#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using static ZoneSystem;

namespace LocationBudgetBooster
{
    public static class BoosterSurveyPlus
    {
        private struct ZoneProfile
        {
            public Vector2i ID;
            public int BiomeMask;
            public int AreaMask;
        }

        private static ZoneProfile[] _worldData;
        private static bool _initialized = false;

        private static Dictionary<int, List<Vector2i>> _candidateCache = new Dictionary<int, List<Vector2i>>();
        // Round-robin: tracks how many times GetZone has been called for each location hash.
        // Index into the shuffled candidate list = callCount % candidates.Count
        // Each zone is visited exactly VisitLimit times before exhaustion.
        private static Dictionary<int, int> _roundRobinIndex = new Dictionary<int, int>();
        private static HashSet<int> _exhaustedLocations = new HashSet<int>();

        public static bool SurveyExhausted = false;

        public static bool GetZone(ZoneLocation location, out Vector2i result, int resolutionOverride = -1)
        {
            Initialize();

            int locHash = location.GetHashCode();

            if (!_candidateCache.TryGetValue(locHash, out var candidates))
            {
                candidates = ScanWorldForCandidates(location);
                _candidateCache[locHash] = candidates;
                if (LocationBooster.DiagnosticMode.Value)
                    BoosterDiagnostics.WriteLog($"[SurveyPlus] Mapped {candidates.Count:N0} candidate zones for {location.m_prefabName}.");
            }

            if (candidates.Count == 0)
            {
                SurveyExhausted = true;
                result = Vector2i.zero;
                return false;
            }

            if (!_roundRobinIndex.ContainsKey(locHash))
                _roundRobinIndex[locHash] = 0;

            int limit = LocationBooster.SurveyVisitLimit.Value;
            int totalBudget = candidates.Count * limit;
            int callCount = _roundRobinIndex[locHash];

            if (callCount >= totalBudget)
            {
                if (!_exhaustedLocations.Contains(locHash))
                {
                    _exhaustedLocations.Add(locHash);
                    BoosterDiagnostics.WriteTimestampedLog(
                        $"[SurveyPlus] Exhausted all {candidates.Count:N0} candidates for {location.m_prefabName} ({limit} visits each).",
                        BepInEx.Logging.LogLevel.Warning);
                }
                SurveyExhausted = true;
                result = Vector2i.zero;
                return false;
            }

            result = candidates[callCount % candidates.Count];
            _roundRobinIndex[locHash] = callCount + 1;
            return true;
        }

        private static List<Vector2i> ScanWorldForCandidates(ZoneLocation location)
        {
            var results = new List<Vector2i>();

            int requiredBiome = (int)location.m_biome;
            int requiredArea = (int)location.m_biomeArea;

            float minD = location.m_minDistance;
            float maxD = location.m_maxDistance > 0.1f ? location.m_maxDistance : LocationBooster.WorldRadius.Value;

            for (int i = 0; i < _worldData.Length; i++)
            {
                var zone = _worldData[i];

                if ((zone.BiomeMask & requiredBiome) == 0) continue;
                if ((zone.AreaMask & requiredArea) == 0) continue;

                Vector3 center = ZoneSystem.GetZonePos(zone.ID);
                float dist = center.magnitude;
                if (dist < minD || dist > maxD) continue;

                results.Add(zone.ID);
            }

            // Shuffle once at scan time. The round-robin then walks this shuffled list,
            // guaranteeing uniform geographic distribution across all VisitLimit passes.
            Shuffle(results);
            return results;
        }

        private static void Initialize()
        {
            if (_initialized) return;

            int gridSize = LocationBooster.SurveyScanResolution.Value;
            if (gridSize < 1) gridSize = 1;

            BoosterDiagnostics.WriteTimestampedLog($"Phase A: Starting SurveyPlus (Resolution {gridSize}x{gridSize})...");
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            int radiusZones = (int)(LocationBooster.WorldRadius.Value / 64f);
            var tempList = new List<ZoneProfile>();

            float safeExtent = 25f;
            float[] offsets = new float[gridSize];

            if (gridSize == 1)
            {
                offsets[0] = 0f;
            }
            else
            {
                float step = (safeExtent * 2f) / (gridSize - 1);
                for (int i = 0; i < gridSize; i++)
                    offsets[i] = -safeExtent + (i * step);
            }

            for (int y = -radiusZones; y <= radiusZones; y++)
            {
                for (int x = -radiusZones; x <= radiusZones; x++)
                {
                    if (x * x + y * y > radiusZones * radiusZones) continue;

                    Vector2i zoneID = new Vector2i(x, y);
                    Vector3 zoneCenter = ZoneSystem.GetZonePos(zoneID);

                    int bMask = 0;
                    int aMask = 0;

                    for (int ox = 0; ox < gridSize; ox++)
                    {
                        for (int oz = 0; oz < gridSize; oz++)
                        {
                            Vector3 samplePos = new Vector3(
                                zoneCenter.x + offsets[ox],
                                0,
                                zoneCenter.z + offsets[oz]
                            );
                            bMask |= (int)WorldGenerator.instance.GetBiome(samplePos);
                            aMask |= (int)WorldGenerator.instance.GetBiomeArea(samplePos);
                        }
                    }

                    tempList.Add(new ZoneProfile { ID = zoneID, BiomeMask = bMask, AreaMask = aMask });
                }
            }

            _worldData = tempList.ToArray();
            _initialized = true;
            stopwatch.Stop();

            var counts = new Dictionary<Heightmap.Biome, int>();
            foreach (Heightmap.Biome b in Enum.GetValues(typeof(Heightmap.Biome)))
            {
                if (b == Heightmap.Biome.None) continue;
                int bFlag = (int)b;
                int count = 0;
                for (int i = 0; i < _worldData.Length; i++)
                    if ((_worldData[i].BiomeMask & bFlag) != 0) count++;
                if (count > 0) counts[b] = count;
            }

            BoosterDiagnostics.WriteTimestampedLog($"SurveyPlus Complete in {stopwatch.ElapsedMilliseconds}ms. Biome Distribution (Multi-Bucket):");
            foreach (var kvp in counts.OrderByDescending(x => x.Value))
                BoosterDiagnostics.WriteLog($"   - {kvp.Key,-15}: {kvp.Value,7:N0} zones");
        }

        public static void ClearCache(int locHash)
        {
            if (_candidateCache.ContainsKey(locHash))
                _candidateCache.Remove(locHash);

            if (_roundRobinIndex.ContainsKey(locHash))
                _roundRobinIndex.Remove(locHash);

            if (_exhaustedLocations.Contains(locHash))
                _exhaustedLocations.Remove(locHash);

            SurveyExhausted = false;
        }

        private static void Shuffle<T>(List<T> list)
        {
            int n = list.Count;
            while (n > 1)
            {
                n--;
                int k = UnityEngine.Random.Range(0, n + 1);
                T value = list[k];
                list[k] = list[n];
                list[n] = value;
            }
        }
    }
}