#nullable disable
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using static ZoneSystem;

namespace LocationBudgetBooster
{
    public static class BoosterReflection
    {
        // Reflection Caching: Fields are captured per state machine type (e.g., d__46 vs d__48)
        public static Dictionary<Type, FieldInfo> LocationFields = new Dictionary<Type, FieldInfo>();
        public static Dictionary<Type, FieldInfo> LimitFields = new Dictionary<Type, FieldInfo>();      // The budget limit (<attempts>)
        public static Dictionary<Type, FieldInfo> CounterFields = new Dictionary<Type, FieldInfo>();    // The loop counter (<i>)
        public static Dictionary<Type, FieldInfo> PlacedFields = new Dictionary<Type, FieldInfo>();     // Current placement count
        public static Dictionary<Type, FieldInfo> ZoneIDFields = new Dictionary<Type, FieldInfo>();     // Zone coordinate (<zoneID>)
        public static Dictionary<string, FieldInfo> ErrorFields = new Dictionary<string, FieldInfo>();  // Error counters (errorBiome, etc.)

        // Track current location being placed for GetRandomZone filter & Diagnostics
        public static ZoneLocation CurrentLocationForFilter = null;

        // Cached spawn zone - guaranteed to be occupied, used for fast rejection
        public static Vector2i? CachedOccupiedZone = null;

        public static void SetCurrentLocation(ZoneLocation location)
        {
            CurrentLocationForFilter = location;
        }

        public static ZoneLocation GetLocation(object instance)
        {
            var type = instance.GetType();
            if (LocationFields.TryGetValue(type, out var field))
            {
                return field.GetValue(instance) as ZoneLocation;
            }
            return null;
        }

        public static long GetVal(object instance, string fieldName)
        {
            int instanceHash = instance.GetHashCode();

            // Read from shadow counters (accurate long values)
            if (BoosterDiagnostics.ShadowCounters.TryGetValue(instanceHash, out var counters))
            {
                if (counters.TryGetValue(fieldName, out var count))
                    return count;
            }

            // Fallback: read from game field (may overflow)
            if (ErrorFields.TryGetValue(fieldName, out var field))
            {
                try { return Convert.ToInt64(field.GetValue(instance)); } catch { }
            }
            return 0;
        }
    }
}