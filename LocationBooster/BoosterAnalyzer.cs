using System;
using System.Collections.Generic;
using UnityEngine;
using static ZoneSystem;

namespace LocationBudgetBooster
{
    public class ReportData
    {
        public ZoneLocation Loc;
        public object Instance;
        public int InstanceHash;
        public int LocHash;

        // Counters
        public long CurrentOuter;
        public long LimitOuter;
        public int Placed;
        public bool IsComplete;

        // Phase 1 Stats
        public long ErrZone;
        public long ErrArea;
        public long ValidZones;

        // Phase 2 Stats
        public long InDist;
        public long InBiome;
        public long InAlt;
        public long InForest;
        public long InTerr;
        public long InSim;
        public long InVeg;

        // Errors
        public long ErrDist;
        public long ErrBiome;
        public long ErrAlt;
        public long ErrForest;
        public long ErrTerrain;
        public long ErrSim;
        public long ErrNotSim;
        public long ErrVeg;
    }

    public static class BoosterAnalyzer
    {
        public static ReportData Analyze(object instance, int overridePlaced = -1)
        {
            var loc = BoosterReflection.GetLocation(instance);
            if (loc == null) return null;

            var data = new ReportData
            {
                Loc = loc,
                Instance = instance,
                InstanceHash = instance.GetHashCode(),
                LocHash = loc.GetHashCode(),
                CurrentOuter = Convert.ToInt64(BoosterReflection.CounterFields[instance.GetType()].GetValue(instance)),
                LimitOuter = Convert.ToInt64(BoosterReflection.LimitFields[instance.GetType()].GetValue(instance)),
                Placed = overridePlaced > -1 ? overridePlaced : (int)BoosterReflection.PlacedFields[instance.GetType()].GetValue(instance)
            };

            data.IsComplete = data.Placed >= loc.m_quantity;

            // Extract Errors
            data.ErrZone = BoosterReflection.GetVal(instance, "errorLocationInZone");
            data.ErrArea = BoosterReflection.GetVal(instance, "errorBiomeArea");
            data.ErrDist = BoosterReflection.GetVal(instance, "errorCenterDistance");
            data.ErrBiome = BoosterReflection.GetVal(instance, "errorBiome");
            data.ErrAlt = BoosterReflection.GetVal(instance, "errorAlt");
            data.ErrForest = BoosterReflection.GetVal(instance, "errorForest");
            data.ErrTerrain = BoosterReflection.GetVal(instance, "errorTerrainDelta");
            data.ErrSim = BoosterReflection.GetVal(instance, "errorSimilar");
            data.ErrNotSim = BoosterReflection.GetVal(instance, "errorNotSimilar");
            data.ErrVeg = BoosterReflection.GetVal(instance, "errorVegetation");

            // Calculate Flow (Bottom-Up)
            long currentPassed = data.Placed;

            data.InVeg = currentPassed + data.ErrVeg;
            currentPassed = data.InVeg;

            data.InSim = currentPassed + data.ErrSim + data.ErrNotSim;
            currentPassed = data.InSim;

            data.InTerr = currentPassed + data.ErrTerrain;
            currentPassed = data.InTerr;

            data.InForest = currentPassed;
            if (loc.m_inForest)
            {
                data.InForest = currentPassed + data.ErrForest;
                currentPassed = data.InForest;
            }

            data.InAlt = currentPassed + data.ErrAlt;
            currentPassed = data.InAlt;

            data.InBiome = currentPassed + data.ErrBiome;
            currentPassed = data.InBiome;

            data.InDist = currentPassed + data.ErrDist;

            // Phase 1 Flow
            data.ValidZones = data.CurrentOuter - data.ErrZone - data.ErrArea;

            return data;
        }
    }
}