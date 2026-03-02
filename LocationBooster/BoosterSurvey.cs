#nullable disable
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using static ZoneSystem;

namespace LocationBudgetBooster
{
    public static class BoosterSurvey
    {
        private static Dictionary<Heightmap.Biome, List<Vector2i>> _biomeIndex;
        private static bool _initialized = false;
        private static int _worldRadiusZones;

        private static Dictionary<int, List<Vector2i>> _candidateCache = new Dictionary<int, List<Vector2i>>();
        private static Dictionary<int, Dictionary<Vector2i, int>> _zoneAttemptCounters = new Dictionary<int, Dictionary<Vector2i, int>>();
        private static HashSet<int> _gaveUp = new HashSet<int>();

        public static bool SurveyExhausted = false;

        public static int GetCandidateCount(ZoneLocation location)
        {
            Initialize();
            int locHash = location.GetHashCode();

            if (_candidateCache.TryGetValue(locHash, out var cached))
                return cached.Count;

            var candidates = BuildCandidateList(location);
            _candidateCache[locHash] = candidates;
            return candidates.Count;
        }

        private static List<Vector2i> BuildCandidateList(ZoneLocation location)
        {
            var allCandidates = new List<Vector2i>();
            var requiredBiomes = new List<Heightmap.Biome>();

            foreach (var biomeKvp in _biomeIndex)
            {
                if ((location.m_biome & biomeKvp.Key) != Heightmap.Biome.None)
                    requiredBiomes.Add(biomeKvp.Key);
            }

            float minD = location.m_minDistance;
            float maxD = location.m_maxDistance > 0.1f ? location.m_maxDistance : LocationBooster.WorldRadius.Value;

            foreach (var biome in requiredBiomes)
            {
                if (!_biomeIndex.ContainsKey(biome)) continue;
                foreach (var zone in _biomeIndex[biome])
                {
                    Vector3 center = ZoneSystem.GetZonePos(zone);
                    if (center.magnitude < minD || center.magnitude > maxD) continue;
                    if ((location.m_biomeArea & WorldGenerator.instance.GetBiomeArea(center)) == 0) continue;
                    allCandidates.Add(zone);
                }
            }

            return allCandidates;
        }

        public static bool GetZone(ZoneLocation location, out Vector2i result)
        {
            Initialize();
            int locHash = location.GetHashCode();

            if (!_candidateCache.TryGetValue(locHash, out var allCandidates))
            {
                allCandidates = BuildCandidateList(location);
                _candidateCache[locHash] = allCandidates;
                BoosterDiagnostics.WriteLog($"[BoosterSurvey] Analyzed world: {allCandidates.Count:N0} valid potential zones for {location.m_prefabName}.");
            }

            if (allCandidates.Count == 0)
            {
                SurveyExhausted = true;
                result = Vector2i.zero;
                return false;
            }

            if (!_zoneAttemptCounters.ContainsKey(locHash)) _zoneAttemptCounters[locHash] = new Dictionary<Vector2i, int>();
            var attemptCounter = _zoneAttemptCounters[locHash];
            int limit = LocationBooster.SurveyVisitLimit.Value;

            for (int i = 0; i < 50; i++)
            {
                var candidate = allCandidates[UnityEngine.Random.Range(0, allCandidates.Count)];
                attemptCounter.TryGetValue(candidate, out int count);
                if (count < limit) { attemptCounter[candidate] = count + 1; result = candidate; return true; }
            }

            foreach (var candidate in allCandidates)
            {
                attemptCounter.TryGetValue(candidate, out int count);
                if (count < limit) { attemptCounter[candidate] = count + 1; result = candidate; return true; }
            }

            if (!_gaveUp.Contains(locHash))
            {
                BoosterDiagnostics.WriteTimestampedLog($"Exhausted all {allCandidates.Count:N0} candidate zones for {location.m_prefabName} ({limit} visits each). Stopping search.", BepInEx.Logging.LogLevel.Warning);
                _gaveUp.Add(locHash);
            }

            SurveyExhausted = true;
            result = Vector2i.zero;
            return false;
        }

        private static void Initialize()
        {
            if (_initialized) return;
            BoosterDiagnostics.WriteTimestampedLog("Phase A: Starting World Survey (Acquisition of Eyes)...");
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            _biomeIndex = new Dictionary<Heightmap.Biome, List<Vector2i>>();
            _worldRadiusZones = (int)(LocationBooster.WorldRadius.Value / 64f);

            for (int y = -_worldRadiusZones; y <= _worldRadiusZones; y++)
            {
                for (int x = -_worldRadiusZones; x <= _worldRadiusZones; x++)
                {
                    if (x * x + y * y > _worldRadiusZones * _worldRadiusZones) continue;
                    Vector2i z = new Vector2i(x, y);
                    Heightmap.Biome b = WorldGenerator.instance.GetBiome(ZoneSystem.GetZonePos(z));
                    if (!_biomeIndex.ContainsKey(b)) _biomeIndex[b] = new List<Vector2i>();
                    _biomeIndex[b].Add(z);
                }
            }
            _initialized = true;
            stopwatch.Stop();
            BoosterDiagnostics.WriteTimestampedLog($"Survey Complete in {stopwatch.ElapsedMilliseconds}ms. Biome Distribution:");
            foreach (var kvp in _biomeIndex.OrderByDescending(x => x.Value.Count))
            {
                BoosterDiagnostics.WriteLog($"   - {kvp.Key,-15}: {kvp.Value.Count,7:N0} zones");
            }
        }
    }
}