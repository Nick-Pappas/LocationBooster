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
            public ulong AltitudeMask; // 64-bit dynamic altitude representation
        }

        private static ZoneProfile[] _worldData;
        private static Dictionary<Vector2i, int> _zoneToIndex = new Dictionary<Vector2i, int>();
        private static bool _initialized = false;

        private static Dictionary<int, List<Vector2i>> _candidateCache = new Dictionary<int, List<Vector2i>>();
        // Cached per-location altitude masks (target, lower, upper) — computed once, never changes
        private static Dictionary<int, (ulong target, ulong lower, ulong upper)> _altMaskCache = new Dictionary<int, (ulong, ulong, ulong)>();
        private static Dictionary<int, int> _roundRobinIndex = new Dictionary<int, int>();
        private static Dictionary<int, int> _exploitScanPos = new Dictionary<int, int>(); // amortized scan start
        private static HashSet<int> _exhaustedLocations = new HashSet<int>();

        public static bool SurveyExhausted = false;
        public static int CurrentActiveZoneIndex = -1; // O(1) dart eavesdropping target

        // --- 64-BIT ALTITUDE MASK HELPERS ---
        private static int GetAltitudeBit(float alt)
        {
            if (alt < -5f) return 0;
            if (alt < 0f) return 1;
            if (alt >= 300f) return 63;
            int bit = 2 + (int)((alt / 300f) * 61f);
            return bit > 62 ? 62 : bit;
        }

        private static ulong GetMaskRange(int startBit, int endBit)
        {
            if (startBit > endBit) return 0;
            ulong mask = 0;
            for (int i = startBit; i <= endBit; i++)
                mask |= (1UL << i);
            return mask;
        }

        /// <summary>
        /// Called by BoosterDiagnostics.TrackGlobalAltitude on every inner loop dart.
        /// Enriches the active zone's AltitudeMask for free as Valheim throws placement attempts.
        /// </summary>
        public static void RecordAltitude(float alt)
        {
            if (!LocationBooster.EnableAltitudeMapping.Value) return;
            if (CurrentActiveZoneIndex >= 0 && _worldData != null && CurrentActiveZoneIndex < _worldData.Length)
                _worldData[CurrentActiveZoneIndex].AltitudeMask |= (1UL << GetAltitudeBit(alt));
        }
        // -------------------------------------

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
                CurrentActiveZoneIndex = -1;
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
                CurrentActiveZoneIndex = -1;
                return false;
            }

            // Reshuffle at each full traversal if configured
            if (callCount > 0 && callCount % candidates.Count == 0 && LocationBooster.ReshuffleOnExhaustion.Value)
                Shuffle(candidates);

            int pos = callCount % candidates.Count;

            // --- EPSILON-GREEDY ALTITUDE EXPLOIT ---
            // One shared index. Exploit scans forward from current position for next qualifying zone.
            // Falls back to the current position as-is if no qualifying zone found (explore).
            // VisitLimit enforced identically for both paths via totalBudget.
            if (LocationBooster.EnableAltitudeMapping.Value)
            {
                float exploreRate = LocationBooster.ExplorationRate.Value;
                if (BoosterAdjuster.RelaxationAttempts.TryGetValue(location.m_prefabName, out int relaxAttempts) && relaxAttempts > 0)
                    exploreRate = 0f;

                if (UnityEngine.Random.value >= exploreRate)
                {
                    if (!_altMaskCache.TryGetValue(locHash, out var masks))
                    {
                        float maxAlt = location.m_maxAltitude > 0.1f ? location.m_maxAltitude : 10000f;
                        int minBit = GetAltitudeBit(location.m_minAltitude);
                        int maxBit = GetAltitudeBit(maxAlt);
                        masks = (GetMaskRange(minBit, maxBit), GetMaskRange(0, minBit - 1), GetMaskRange(maxBit + 1, 63));
                        _altMaskCache[locHash] = masks;
                    }
                    (ulong targetMask, ulong lowerMask, ulong upperMask) = masks;

                    // Amortized scan: start from where we left off last call.
                    // Total work per full traversal = O(N), amortized O(1) per call.
                    int scanStart = _exploitScanPos.TryGetValue(locHash, out int sp) ? sp : 0;
                    int n = candidates.Count;

                    for (int i = 0; i < n; i++)
                    {
                        int idx = (scanStart + i) % n;
                        var c = candidates[idx];
                        if (!_zoneToIndex.TryGetValue(c, out int wi)) continue;
                        ulong zMask = _worldData[wi].AltitudeMask;
                        if (zMask == 0) continue;

                        bool directHit = (zMask & targetMask) != 0;
                        bool slopeHit = (lowerMask != 0 && upperMask != 0) && ((zMask & lowerMask) != 0) && ((zMask & upperMask) != 0);

                        if (directHit || slopeHit)
                        {
                            _exploitScanPos[locHash] = (idx + 1) % n; // resume after this hit next call
                            result = c;
                            _roundRobinIndex[locHash] = callCount + 1;
                            CurrentActiveZoneIndex = wi;
                            return true;
                        }
                    }
                    // No qualifying zone found — fall through to explore
                }
            }
            // ----------------------------------------

            result = candidates[pos];
            _roundRobinIndex[locHash] = callCount + 1;
            CurrentActiveZoneIndex = LocationBooster.EnableAltitudeMapping.Value && _zoneToIndex.TryGetValue(result, out int zoneIdx) ? zoneIdx : -1;
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

            bool mapAltitude = LocationBooster.EnableAltitudeMapping.Value;

            BoosterDiagnostics.WriteTimestampedLog($"Phase A: Starting SurveyPlus (Resolution {gridSize}x{gridSize}, AltitudeMapping={mapAltitude})...");
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            int radiusZones = (int)(LocationBooster.WorldRadius.Value / 64f);
            var tempList = new List<ZoneProfile>();
            _zoneToIndex.Clear();

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

            int indexCounter = 0;

            for (int y = -radiusZones; y <= radiusZones; y++)
            {
                for (int x = -radiusZones; x <= radiusZones; x++)
                {
                    if (x * x + y * y > radiusZones * radiusZones) continue;

                    Vector2i zoneID = new Vector2i(x, y);
                    Vector3 zoneCenter = ZoneSystem.GetZonePos(zoneID);

                    int bMask = 0;
                    int aMask = 0;
                    ulong altMask = 0;

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

                            if (mapAltitude)
                            {
                                float alt = WorldGenerator.instance.GetHeight(samplePos.x, samplePos.z);
                                altMask |= (1UL << GetAltitudeBit(alt));
                            }
                        }
                    }

                    tempList.Add(new ZoneProfile { ID = zoneID, BiomeMask = bMask, AreaMask = aMask, AltitudeMask = altMask });
                    _zoneToIndex[zoneID] = indexCounter;
                    indexCounter++;
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
            _candidateCache.Remove(locHash);
            _altMaskCache.Remove(locHash);
            _roundRobinIndex.Remove(locHash);
            _exploitScanPos.Remove(locHash);
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