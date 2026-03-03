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
        private static Dictionary<int, int> _cursorPositions = new Dictionary<int, int>();
        private static HashSet<int> _exhaustedLocations = new HashSet<int>();

        public static bool SurveyExhausted = false;

        public static bool GetZone(ZoneLocation location, out Vector2i result)
        {
            Initialize();

            int locHash = location.GetHashCode();

            if (!_candidateCache.TryGetValue(locHash, out var candidates))
            {
                candidates = ScanWorldForCandidates(location);
                _candidateCache[locHash] = candidates;
                _cursorPositions[locHash] = 0;

                // Diagnostic log for this specific location type
                BoosterDiagnostics.WriteLog($"[SurveyPlus] Mapped {candidates.Count:N0} candidate zones for {location.m_prefabName} (High-Res scan).");
            }

            if (candidates.Count == 0)
            {
                SurveyExhausted = true;
                result = Vector2i.zero;
                return false;
            }

            int cursor = _cursorPositions[locHash];

            if (cursor >= candidates.Count)
            {
                if (!_exhaustedLocations.Contains(locHash))
                {
                    _exhaustedLocations.Add(locHash);
                    BoosterDiagnostics.WriteTimestampedLog($"[SurveyPlus] Exhausted all {candidates.Count} candidates for {location.m_prefabName}.", BepInEx.Logging.LogLevel.Warning);
                }

                SurveyExhausted = true;
                result = Vector2i.zero;
                return false;
            }

            result = candidates[cursor];
            _cursorPositions[locHash] = cursor + 1;
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

                // Bitwise checks against the mask
                if ((zone.BiomeMask & requiredBiome) == 0) continue;
                if ((zone.AreaMask & requiredArea) == 0) continue;

                Vector3 center = ZoneSystem.GetZonePos(zone.ID);
                float dist = center.magnitude;
                if (dist < minD || dist > maxD) continue;

                results.Add(zone.ID);
            }

            Shuffle(results);
            return results;
        }

        private static void Initialize()
        {
            if (_initialized) return;

            BoosterDiagnostics.WriteTimestampedLog("Phase A: Starting SurveyPlus (9-Point High-Res Scan)...");
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            int radiusZones = (int)(LocationBooster.WorldRadius.Value / 64f);
            var tempList = new List<ZoneProfile>();
            float[] offsets = new float[] { -25f, 0f, 25f };

            // 1. Scan the world
            for (int y = -radiusZones; y <= radiusZones; y++)
            {
                for (int x = -radiusZones; x <= radiusZones; x++)
                {
                    if (x * x + y * y > radiusZones * radiusZones) continue;

                    Vector2i zoneID = new Vector2i(x, y);
                    Vector3 zoneCenter = ZoneSystem.GetZonePos(zoneID);

                    int bMask = 0;
                    int aMask = 0;

                    for (int ox = 0; ox < 3; ox++)
                    {
                        for (int oz = 0; oz < 3; oz++)
                        {
                            Vector3 samplePos = new Vector3(zoneCenter.x + offsets[ox], 0, zoneCenter.z + offsets[oz]);
                            bMask |= (int)WorldGenerator.instance.GetBiome(samplePos);
                            aMask |= (int)WorldGenerator.instance.GetBiomeArea(samplePos);
                        }
                    }

                    tempList.Add(new ZoneProfile
                    {
                        ID = zoneID,
                        BiomeMask = bMask,
                        AreaMask = aMask
                    });
                }
            }

            _worldData = tempList.ToArray();
            _initialized = true;
            stopwatch.Stop();

            // 2. Compile Distribution Report
            var counts = new Dictionary<Heightmap.Biome, int>();
            foreach (Heightmap.Biome b in Enum.GetValues(typeof(Heightmap.Biome)))
            {
                if (b == Heightmap.Biome.None) continue;
                int bFlag = (int)b;
                int count = 0;

                // Fast linear scan to count occurrences
                for (int i = 0; i < _worldData.Length; i++)
                {
                    if ((_worldData[i].BiomeMask & bFlag) != 0) count++;
                }

                if (count > 0) counts[b] = count;
            }

            // 3. Print Report
            BoosterDiagnostics.WriteTimestampedLog($"SurveyPlus Complete in {stopwatch.ElapsedMilliseconds}ms. Biome Distribution (Zones containing at least 1 pixel of):");
            foreach (var kvp in counts.OrderByDescending(x => x.Value))
            {
                BoosterDiagnostics.WriteLog($"   - {kvp.Key,-15}: {kvp.Value,7:N0} zones");
            }
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