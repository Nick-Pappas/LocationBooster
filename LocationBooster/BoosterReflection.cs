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
        public static Dictionary<Type, FieldInfo> LocationFields = new Dictionary<Type, FieldInfo>();
        public static Dictionary<Type, FieldInfo> LimitFields = new Dictionary<Type, FieldInfo>();
        public static Dictionary<Type, FieldInfo> CounterFields = new Dictionary<Type, FieldInfo>();
        public static Dictionary<Type, FieldInfo> InnerCounterFields = new Dictionary<Type, FieldInfo>();
        public static Dictionary<Type, FieldInfo> PlacedFields = new Dictionary<Type, FieldInfo>();
        public static Dictionary<Type, FieldInfo> ZoneIDFields = new Dictionary<Type, FieldInfo>();
        public static Dictionary<string, FieldInfo> ErrorFields = new Dictionary<string, FieldInfo>();

        // NEW: Cache for the ZPackage field so we can write to it when killing the loop
        public static FieldInfo IterationsPkgField = null;

        public static ZoneLocation CurrentLocationForFilter = null;
        public static Vector2i? CachedOccupiedZone = null;

        public static void SetCurrentLocation(ZoneLocation location)
        {
            if (CurrentLocationForFilter != location)
            {
                CurrentLocationForFilter = location;
            }
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
            if (BoosterDiagnostics.ShadowCounters.TryGetValue(instanceHash, out var counters))
            {
                if (counters.TryGetValue(fieldName, out var count))
                    return count;
            }
            if (ErrorFields.TryGetValue(fieldName, out var field))
            {
                try { return Convert.ToInt64(field.GetValue(instance)); } catch { }
            }
            return 0;
        }
    }
}