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
        private static int _currentProcessed = 0; // "Y" (Attempted)
        private static int _currentPlaced = 0;    // "K" (Successes)
        private static string _modeName = "Vanilla";
        private static DateTime _startTime;

        public static string StaticTopText = "";
        public static string StaticBottomText = "";

        // Failure Trackers
        private static HashSet<string> _failedVitals = new HashSet<string>();
        private static HashSet<string> _failedSecondary = new HashSet<string>();
        private static List<ZoneLocation> _validLocations = new List<ZoneLocation>();

        // Necessities: Unplayable if any are 0 on the map at the end
        private static readonly HashSet<string> _necessities = new HashSet<string>
        {
            "Eikthyrnir", "GDKing", "Bonemass", "Dragonqueen", "GoblinKing",
            "Mistlands_DvergrBossEntrance1", "FaderLocation", "Vendor_BlackForest",
            "Hildir_camp", "BogWitch_Camp", "Hildir_crypt", "Hildir_cave", "Hildir_plainsfortress"
        };

        // Secondary: Questionable if they fail to meet this percentage of their requested quantity
        private static readonly Dictionary<string, float> _secondaryGoals = new Dictionary<string, float>
        {
            { "Crypt", 0.5f }, { "SunkenCrypt", 0.5f }, { "MountainCave", 0.5f },
            { "InfestedMine", 0.5f }, { "TarPit", 0.5f }, { "CharredFortress", 0.5f}
        };

        public static void StartGeneration(ZoneSystem zs)
        {
            if (_initialized) return;
            _initialized = true;

            BoosterUI.EnsureInstance();

            _startTime = DateTime.Now;
            _modeName = LocationBooster.Mode.Value.ToString();

            // Filter out invalid AND disabled locations (m_enable) to perfectly match Valheim
            _validLocations.Clear();
            foreach (var loc in zs.m_locations)
            {
                if (loc.m_enable && loc.m_prefab != null && loc.m_prefab.IsValid)
                {
                    _validLocations.Add(loc);
                }
            }

            _totalRequested = _validLocations.Sum(l => l.m_quantity);
            _currentProcessed = 0;
            _currentPlaced = 0;
            _failedVitals.Clear();
            _failedSecondary.Clear();

            UpdateText();
            BoosterDiagnostics.WriteTimestampedLog($"=== GLOBAL START: Generating Locations ({_modeName}) ===");
        }

        private static int GetActualPlacedCount(string prefabName)
        {
            if (ZoneSystem.instance == null) return 0;
            int count = 0;
            foreach (var kvp in ZoneSystem.instance.m_locationInstances)
            {
                if (kvp.Value.m_location.m_prefabName == prefabName) count++;
            }
            return count;
        }

        public static void IncrementProcessed(bool successfullyPlaced)
        {
            _currentProcessed++;
            if (successfullyPlaced) _currentPlaced++;

            // Limit bounds just in case
            if (_currentProcessed > _totalRequested) _currentProcessed = _totalRequested;
            if (_currentPlaced > _currentProcessed) _currentPlaced = _currentProcessed;

            UpdateText();
        }

        public static void RecordFinalLocationStats(string locationName, int placedCount, int requestedCount)
        {
            if (_necessities.Contains(locationName) && placedCount == 0)
            {
                _failedVitals.Add($"{locationName} (0/{requestedCount})");
            }
            else if (_secondaryGoals.TryGetValue(locationName, out float requiredRate))
            {
                float actualRate = (float)placedCount / requestedCount;
                if (actualRate < requiredRate)
                {
                    _failedSecondary.Add($"{locationName} ({placedCount}/{requestedCount})");
                }
            }
            UpdateText();
        }

        public static void UpdateText()
        {
            if (BoosterUI.instance == null) return;

            // Percents with 2 decimal precision
            float attemptedPct = _totalRequested > 0 ? (100f * _currentProcessed / _totalRequested) : 0f;
            float successPct = _currentProcessed > 0 ? (100f * _currentPlaced / _currentProcessed) : 0f;

            string color = "#55FF55"; // Green
            if (_failedVitals.Count > 0) color = "#FF4444"; // Red
            else if (_failedSecondary.Count > 0) color = "#FFB75E"; // Yellow

            // Compile Top Section
            var sbTop = new StringBuilder();
            sbTop.AppendLine($"<color={color}>");
            sbTop.AppendLine($"<size=28><b>Placing locations using {_modeName}</b></size>");
            sbTop.AppendLine($"<size=24>Attempted {_currentProcessed}/{_totalRequested} ({attemptedPct:0.00}%)</size>");
            sbTop.AppendLine($"<size=24>Successfully placed {_currentPlaced}/{_currentProcessed} ({successPct:0.00}%)</size>");
            StaticTopText = sbTop.ToString();

            // Compile Bottom Section (Failures)
            var sbBot = new StringBuilder();
            if (_failedVitals.Count > 0)
            {
                sbBot.AppendLine("\n<size=22><b>VITAL FAILURES:</b></size>");
                foreach (var failure in _failedVitals) sbBot.AppendLine($"<size=20>- {failure}</size>");
            }

            if (_failedSecondary.Count > 0)
            {
                sbBot.AppendLine("\n<size=22><b>QUESTIONABLE:</b></size>");
                foreach (var failure in _failedSecondary) sbBot.AppendLine($"<size=20>- {failure}</size>");
            }
            sbBot.Append("</color>");

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

            // 1. Final Accurate Data Collection
            var finalCounts = new Dictionary<string, int>();
            foreach (var loc in _validLocations)
            {
                // Prevent duplicate prefab entries from ExpandWorld double counting
                if (!finalCounts.ContainsKey(loc.m_prefabName))
                {
                    int count = GetActualPlacedCount(loc.m_prefabName);
                    finalCounts[loc.m_prefabName] = count;
                    totalActualPlaced += count;
                }
            }

            // 2. Playability Analysis
            foreach (string necessity in _necessities)
            {
                if (!finalCounts.TryGetValue(necessity, out int count) || count == 0)
                {
                    failedChecks.Add($"Missing required location: {necessity}");
                }
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

                    int placed = finalCounts[goal.Key];
                    float placedRate = (float)placed / locData.m_quantity;

                    if (placedRate < goal.Value)
                    {
                        failedChecks.Add($"{goal.Key} (placed {placed}/{locData.m_quantity}, required {goal.Value:P0})");
                    }
                }

                if (failedChecks.Count > 0)
                {
                    playabilityVerdict = "Questionable";
                    logLevel = LogLevel.Warning;
                }
                else
                {
                    playabilityVerdict = "Playable";
                }
            }

            int totalFailed = _totalRequested - totalActualPlaced;
            if (totalFailed < 0) totalFailed = 0;
            float successRate = _totalRequested > 0 ? (totalActualPlaced * 100f / _totalRequested) : 100f;

            // 3. Final Summary Report
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

            if (failedChecks.Count > 0)
            {
                summary.AppendLine("-------------------------------------------------");
                summary.AppendLine("  Details:");
                foreach (var failure in failedChecks)
                    summary.AppendLine($"  - {failure}");
            }
            summary.AppendLine("=================================================");

            BoosterDiagnostics.WriteTimestampedLog(summary.ToString(), logLevel);

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
                // CRITICAL FIX: Force OS cursor visibility during Repaint. 
                // This overrides Valheim's input system attempting to lock the cursor.
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;

                if (Event.current.type != EventType.Repaint || string.IsNullOrEmpty(StaticTopText)) return;

                if (_style == null)
                {
                    _style = new GUIStyle(GUI.skin.label)
                    {
                        richText = true,
                        alignment = TextAnchor.UpperLeft, // Keeps bullet points aligned nicely
                        font = _valheimFont
                    };
                }

                // Anchored to Top-Right
                var rect = new Rect(Screen.width - 670, 20, 650, Screen.height - 40);

                // Animated Spinner
                int index = (int)(Time.realtimeSinceStartup * 8f) % _spinner.Length;

                string currentPrefab = BoosterReflection.CurrentLocationForFilter != null ? BoosterReflection.CurrentLocationForFilter.m_prefabName : "Finished";
                string currentLine = $"<size=22>Current: {currentPrefab}  {_spinner[index]}</size>\n";

                string fullMessage = StaticTopText + currentLine + StaticBottomText;

                GUI.Label(rect, fullMessage, _style);
            }
        }
    }
}