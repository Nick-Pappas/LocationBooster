#nullable disable
using UnityEngine;
using static ZoneSystem;

namespace LocationBudgetBooster
{
    public static class BoosterForce
    {
        /// <summary>
        /// FORCE STRATEGY:
        /// Mathematically generates a valid zone coordinate strictly within the donut/annulus.
        /// 100% acceptance rate for distance checks.
        /// </summary>
        public static Vector2i GenerateDonut(float minDistance, float maxDistance)
        {
            // Pad the distance slightly (1 zone width) to ensure we are safely inside
            float safeMin = minDistance + 64f;
            float safeMax = maxDistance - 64f;

            // Safety check for weird configs
            if (safeMax <= safeMin) safeMax = safeMin + 1f;

            float minR = safeMin / 64f;
            float maxR = safeMax / 64f;

            // Uniform distribution in annulus: r = sqrt(random(Rmin^2, Rmax^2))
            float angle = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
            float r = Mathf.Sqrt(UnityEngine.Random.Range(minR * minR, maxR * maxR));

            int x = Mathf.RoundToInt(r * Mathf.Cos(angle));
            int z = Mathf.RoundToInt(r * Mathf.Sin(angle));

            return new Vector2i(x, z);
        }
    }
}