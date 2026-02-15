#nullable disable
using UnityEngine;
using static ZoneSystem;

namespace LocationBudgetBooster
{
    public static class BoosterFilter
    {
        /// <summary>
        /// FILTER STRATEGY:
        /// Generates standard square coordinates but loops internally until one lands in the donut.
        /// This burns CPU time here to save the Outer Loop from processing invalid zones.
        /// </summary>
        public static Vector2i GenerateSieve(float minDistance, float maxDistance, float worldRadius)
        {
            int maxZoneRadius = (int)(worldRadius / 64f);
            Vector2i zone;
            Vector3 center;
            float dist;
            int attempts = 0;

            do
            {
                // Vanilla-like square generation
                zone = new Vector2i(
                    UnityEngine.Random.Range(-maxZoneRadius, maxZoneRadius),
                    UnityEngine.Random.Range(-maxZoneRadius, maxZoneRadius)
                );

                center = ZoneSystem.GetZonePos(zone);
                dist = center.magnitude;
                attempts++;

                // Sanity break to prevent infinite loops if config is impossible
                if (attempts > 1000) break;

            } while (dist < minDistance || dist > maxDistance); // Keep trying if outside donut

            return zone;
        }
    }
}