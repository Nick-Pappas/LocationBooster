#nullable disable
using BepInEx.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using static ZoneSystem;

namespace LocationBudgetBooster
{
    public static class BoosterGlobalProgress
    {
        private static bool _initialized = false;
        private static int _totalRequested = 0;
        private static int _currentProcessed = 0;
        private static int _currentPlaced = 0;
        private static string _modeName = "Vanilla";
        private static DateTime _startTime;

        public static string StaticTopText = "";
        public static string StaticBottomText = "";

        // Failure Trackers
        private static HashSet<string> _failedVitals = new HashSet<string>();
        private static HashSet<string> _failedSecondary = new HashSet<string>();
        private static List<ZoneLocation> _validLocations = new List<ZoneLocation>();

        // Relaxation State:
        //   Queued  = relaxed, sitting at end of list, not yet retried  → amber
        //   Pending = currently being retried right now                 → red
        private static HashSet<string> _relaxationQueued = new HashSet<string>();
        private static HashSet<string> _relaxationPending = new HashSet<string>();

        // Necessities: Unplayable if any are 0 on the map at the end
        private static readonly HashSet<string> _necessities = new HashSet<string>
        {
            "Eikthyrnir", "GDKing", "Bonemass", "Dragonqueen", "GoblinKing",
            "Mistlands_DvergrBossEntrance1", "FaderLocation", "Vendor_BlackForest",
            "Hildir_camp", "BogWitch_Camp", "Hildir_crypt", "Hildir_cave", "Hildir_plainsfortress"
        };

        // Secondaries: Questionable if they fail to meet this percentage of requested quantity
        private static readonly Dictionary<string, float> _secondaryGoals = new Dictionary<string, float>
        {
            { "Crypt", 0.5f }, { "SunkenCrypt", 0.5f }, { "MountainCave", 0.5f },
            { "InfestedMine", 0.5f }, { "TarPit", 0.5f }, { "CharredFortress", 0.5f }
        };

        // --- RELAXATION STATE API ---

        /// <summary>
        /// Called by TryRelax when a location is relaxed and queued at the end of the list.
        /// GUI shows amber: "will retry later."
        /// </summary>
        public static void MarkRelaxationQueued(string prefabName)
        {
            _relaxationQueued.Add(prefabName);
            _relaxationPending.Remove(prefabName);
            UpdateText();
        }

        /// <summary>
        /// Called by GetRandomZonePrefix when a queued location begins its retry pass.
        /// Transitions amber → red: "retrying right now."
        /// No-op if the location was not queued.
        /// </summary>
        public static void TransitionToRetrying(string prefabName)
        {
            if (!_relaxationQueued.Contains(prefabName)) return;
            _relaxationQueued.Remove(prefabName);
            _relaxationPending.Add(prefabName);
            UpdateText();
        }

        /// <summary>
        /// Removes a location from both relaxation tracking sets.
        /// Called when the retry successfully places at least one instance.
        /// </summary>
        public static void ClearRelaxationState(string prefabName)
        {
            _relaxationPending.Remove(prefabName);
            _relaxationQueued.Remove(prefabName);
        }

        // --- HELPERS ---

        public static bool NeedsRelaxation(string prefabName, int placedCount, int requestedCount)
        {
            if (_necessities.Contains(prefabName))
                return placedCount == 0;

            if (_secondaryGoals.TryGetValue(prefabName, out float requiredRate))
                return (float)placedCount / requestedCount < requiredRate;

            return false;
        }

        public static int GetMinimumNeededCount(string prefabName, int requestedCount)
        {
            if (_necessities.Contains(prefabName)) return 1;
            if (_secondaryGoals.TryGetValue(prefabName, out float requiredRate))
                return Mathf.Max(1, Mathf.CeilToInt(requestedCount * requiredRate));
            return requestedCount;
        }

        // --- LIFECYCLE ---

        public static void StartGeneration(ZoneSystem zs)
        {
            if (_initialized) return;
            _initialized = true;

            BoosterUI.EnsureInstance();
            BoosterAdjuster.Reset();

            _startTime = DateTime.Now;
            _modeName = LocationBooster.Mode.Value.ToString();

            _validLocations.Clear();
            foreach (var loc in zs.m_locations)
            {
                if (loc.m_enable && loc.m_prefab != null && loc.m_prefab.IsValid)
                    _validLocations.Add(loc);
            }

            _totalRequested = _validLocations.Sum(l => l.m_quantity);
            _currentProcessed = 0;
            _currentPlaced = 0;
            _failedVitals.Clear();
            _failedSecondary.Clear();
            _relaxationQueued.Clear();
            _relaxationPending.Clear();

            UpdateText();
            BoosterDiagnostics.WriteTimestampedLog($"=== GLOBAL START: Generating Locations ({_modeName}) ===");
        }

        public static void IncrementProcessed(bool successfullyPlaced)
        {
            _currentProcessed++;
            if (successfullyPlaced) _currentPlaced++;
            if (_currentProcessed > _totalRequested) _currentProcessed = _totalRequested;
            if (_currentPlaced > _currentProcessed) _currentPlaced = _currentProcessed;
            UpdateText();
        }

        public static void RecordFinalLocationStats(string locationName, int placedCount, int requestedCount)
        {
            // If this was a pending retry and it placed something, clear the relaxation state
            if (_relaxationPending.Contains(locationName) && placedCount > 0)
                ClearRelaxationState(locationName);

            if (_necessities.Contains(locationName) && placedCount == 0)
                _failedVitals.Add($"{locationName} (0/{requestedCount})");
            else if (_secondaryGoals.TryGetValue(locationName, out float requiredRate))
            {
                float actualRate = (float)placedCount / requestedCount;
                if (actualRate < requiredRate)
                    _failedSecondary.Add($"{locationName} ({placedCount}/{requestedCount})");
            }

            UpdateText();
        }

        private static int GetActualPlacedCount(string prefabName)
        {
            if (ZoneSystem.instance == null) return 0;
            int count = 0;
            foreach (var kvp in ZoneSystem.instance.m_locationInstances)
                if (kvp.Value.m_location.m_prefabName == prefabName) count++;
            return count;
        }

        public static void UpdateText()
        {
            if (BoosterUI.instance == null) return;

            float attemptedPct = _totalRequested > 0 ? (100f * _currentProcessed / _totalRequested) : 0f;
            float successPct = _currentProcessed > 0 ? (100f * _currentPlaced / _currentProcessed) : 0f;

            bool hasRelaxations = BoosterAdjuster.RelaxationAttempts.Any(kvp => kvp.Value > 0);
            bool hasQueued = _relaxationQueued.Count > 0;
            bool hasPending = _relaxationPending.Count > 0;

            // Priority: Red > Yellow > Blue > Green
            string color;
            if (_failedVitals.Count > 0)
                color = "#FF4444";  // Red — vital failure
            else if (_failedSecondary.Count > 0)
                color = "#FFB75E";  // Yellow — secondary shortfall
            else if (hasRelaxations)
                color = "#55AAFF";  // Blue — relaxation resolved
            else
                color = "#55FF55";  // Green

            var sbTop = new StringBuilder();
            sbTop.AppendLine($"<color={color}>");
            sbTop.AppendLine($"<size=28><b>Placing locations using {_modeName}</b></size>");
            sbTop.AppendLine($"<size=24>Attempted {_currentProcessed}/{_totalRequested} ({attemptedPct:0.00}%)</size>");
            sbTop.AppendLine($"<size=24>Successfully placed {_currentPlaced}/{_currentProcessed} ({successPct:0.00}%)</size>");
            StaticTopText = sbTop.ToString();

            var sbBot = new StringBuilder();

            if (_failedVitals.Count > 0)
            {
                sbBot.AppendLine("\n<size=22><b>VITAL FAILURES:</b></size>");
                foreach (var f in _failedVitals) sbBot.AppendLine($"<size=20>- {f}</size>");
            }

            if (_failedSecondary.Count > 0)
            {
                sbBot.AppendLine("\n<size=22><b>QUESTIONABLE:</b></size>");
                foreach (var f in _failedSecondary) sbBot.AppendLine($"<size=20>- {f}</size>");
            }

            sbBot.Append("</color>");

            // Amber block — queued for end-of-run retry
            if (hasQueued)
            {
                sbBot.AppendLine("\n<color=#FF8C00><size=22><b>QUEUED FOR RETRY (relaxed):</b></size>");
                foreach (var name in _relaxationQueued)
                {
                    var locData = ZoneSystem.instance?.m_locations.FirstOrDefault(l => l.m_prefabName == name);
                    string summary = locData != null ? BoosterAdjuster.GetRelaxationSummary(name, locData) : "";
                    sbBot.AppendLine($"<size=20>- {name} {summary}</size>");
                }
                sbBot.Append("</color>");
            }

            // Red block — actively retrying right now
            if (hasPending)
            {
                sbBot.AppendLine("\n<color=#FF4444><size=22><b>RETRYING (relaxed):</b></size>");
                foreach (var name in _relaxationPending)
                    sbBot.AppendLine($"<size=20>- {name}</size>");
                sbBot.Append("</color>");
            }

            // Blue block — relaxations that are fully resolved
            var resolved = BoosterAdjuster.RelaxationAttempts
                .Where(kvp => kvp.Value > 0
                    && !_relaxationQueued.Contains(kvp.Key)
                    && !_relaxationPending.Contains(kvp.Key))
                .ToList();

            if (resolved.Count > 0)
            {
                sbBot.AppendLine("\n<color=#55AAFF><size=22><b>RELAXED REQUIREMENTS:</b></size>");
                foreach (var kvp in resolved)
                {
                    var locData = ZoneSystem.instance?.m_locations.FirstOrDefault(l => l.m_prefabName == kvp.Key);
                    if (locData != null)
                        sbBot.AppendLine($"<size=20>- {kvp.Key} {BoosterAdjuster.GetRelaxationSummary(kvp.Key, locData)}</size>");
                }
                sbBot.Append("</color>");
            }

            StaticBottomText = sbBot.ToString();
        }

        public static void EndGeneration()
        {
            if (!_initialized) return;

            var endTime = DateTime.Now;
            var elapsedTime = endTime - _startTime;
            string timeString = $"{(int)elapsedTime.TotalMinutes}m {elapsedTime.Seconds}.{elapsedTime.Milliseconds / 100}s";

            int totalActualPlaced = 0;
            var failedChecks = new List<string>();
            LogLevel logLevel = LogLevel.Info;

            // Clean up duplicates from re-queuing
            if (ZoneSystem.instance != null)
            {
                var distinctList = ZoneSystem.instance.m_locations.Distinct().ToList();
                ZoneSystem.instance.m_locations.Clear();
                ZoneSystem.instance.m_locations.AddRange(distinctList);
            }

            // Restore any m_quantity values capped during relaxation
            BoosterAdjuster.RestoreQuantities();

            // Count placements — query world truth directly
            var finalCounts = new Dictionary<string, int>();
            foreach (var loc in _validLocations)
            {
                if (!finalCounts.ContainsKey(loc.m_prefabName))
                {
                    int count = GetActualPlacedCount(loc.m_prefabName);
                    finalCounts[loc.m_prefabName] = count;
                    totalActualPlaced += count;
                }
            }

            // Playability — necessities checked via world truth
            foreach (string necessity in _necessities)
            {
                if (GetActualPlacedCount(necessity) == 0)
                    failedChecks.Add($"Missing required location: {necessity}");
            }

            string playabilityVerdict;
            if (failedChecks.Count > 0)
            {
                playabilityVerdict = "Unplayable";
                logLevel = LogLevel.Error;
            }
            else
            {
                foreach (var goal in _secondaryGoals)
                {
                    var locData = _validLocations.FirstOrDefault(l => l.m_prefabName == goal.Key);
                    if (locData == null || locData.m_quantity == 0) continue;
                    if (!finalCounts.TryGetValue(goal.Key, out int placed)) continue;
                    float placedRate = (float)placed / locData.m_quantity;
                    if (placedRate < goal.Value)
                        failedChecks.Add($"{goal.Key} (placed {placed}/{locData.m_quantity}, required {goal.Value:P0})");
                }

                playabilityVerdict = failedChecks.Count > 0 ? "Questionable" : "Playable";
                if (failedChecks.Count > 0) logLevel = LogLevel.Warning;
            }

            int totalFailed = _totalRequested - totalActualPlaced;
            if (totalFailed < 0) totalFailed = 0;
            float successRate = _totalRequested > 0 ? (totalActualPlaced * 100f / _totalRequested) : 100f;

            BoosterDiagnostics.WriteTimestampedLog($"=== GLOBAL END: Generating Locations ({_modeName}) ===");

            var summary = new StringBuilder();
            summary.AppendLine();
            summary.AppendLine("=================================================");
            summary.AppendLine("===      WORLD GENERATION SUMMARY             ===");
            summary.AppendLine("=================================================");
            summary.AppendLine($"  Method Used:      {_modeName}");
            summary.AppendLine($"  Total Time:       {timeString}");
            summary.AppendLine($"  Total Requested:  {_totalRequested:N0}");
            summary.AppendLine($"  Total Placed:     {totalActualPlaced:N0} ({successRate:F2}%)");
            summary.AppendLine($"  Total Failed:     {totalFailed:N0}");
            summary.AppendLine($"  Playability:      {playabilityVerdict}");

            var relaxedItems = BoosterAdjuster.RelaxationAttempts.Where(kvp => kvp.Value > 0).ToList();
            if (relaxedItems.Count > 0)
            {
                summary.AppendLine("-------------------------------------------------");
                summary.AppendLine("  Relaxations Applied:");
                foreach (var kvp in relaxedItems)
                {
                    var locData = ZoneSystem.instance?.m_locations.FirstOrDefault(l => l.m_prefabName == kvp.Key);
                    if (locData != null)
                        summary.AppendLine($"  - {kvp.Key} {BoosterAdjuster.GetRelaxationSummary(kvp.Key, locData)}");
                }
            }

            if (failedChecks.Count > 0)
            {
                summary.AppendLine("-------------------------------------------------");
                summary.AppendLine("  Details:");
                foreach (var failure in failedChecks)
                    summary.AppendLine($"  - {failure}");
            }
            summary.AppendLine("=================================================");

            BoosterDiagnostics.WriteTimestampedLog(summary.ToString(), logLevel);

            BoosterDiagnostics.DumpPlacementsToFile();
            BoosterUI.DestroyInstance();
            _initialized = false;
        }

        public class BoosterUI : MonoBehaviour
        {
            public static BoosterUI instance;
            private GUIStyle _style;
            private Font _valheimFont;
            private readonly string[] _spinner = new string[] { "|", "/", "-", "\\" };

            public static void EnsureInstance()
            {
                if (instance == null)
                {
                    var go = new GameObject("BoosterProgressOverlay");
                    DontDestroyOnLoad(go);
                    instance = go.AddComponent<BoosterUI>();
                }
            }

            public static void DestroyInstance()
            {
                if (instance != null)
                {
                    Destroy(instance.gameObject);
                    instance = null;
                }
            }

            void Awake()
            {
                if (instance != null && instance != this) { Destroy(gameObject); return; }
                instance = this;
            }

            void Start()
            {
                _valheimFont = Resources.FindObjectsOfTypeAll<Font>().FirstOrDefault(x => x.name == "AveriaSerifLibre-Bold");
            }

            void OnGUI()
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;

                if (Event.current.type != EventType.Repaint || string.IsNullOrEmpty(StaticTopText)) return;

                if (_style == null)
                {
                    _style = new GUIStyle(GUI.skin.label)
                    {
                        richText = true,
                        alignment = TextAnchor.UpperLeft,
                        font = _valheimFont
                    };
                }

                var rect = new Rect(Screen.width - 670, 20, 650, Screen.height - 40);

                int index = (int)(Time.realtimeSinceStartup * 8f) % _spinner.Length;
                string currentPrefab = BoosterReflection.CurrentLocationForFilter != null
                    ? BoosterReflection.CurrentLocationForFilter.m_prefabName
                    : "Finished";
                string currentLine = $"<size=22>Current: {currentPrefab}  {_spinner[index]}</size>\n";

                string fullMessage = StaticTopText + currentLine + StaticBottomText;

                GUI.Label(rect, fullMessage, _style);
            }
        }
    }
}