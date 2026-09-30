#region Using declarations
using System;
using System.Collections.Generic;
using NinjaTrader.NinjaScript;
using NinjaTrader.Data;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript.DrawingTools;
using System.Windows.Media;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    public class SupplyDemandZones_PRO_GC : Indicator
    {
        // ================================
        // USER INPUTS
        // ================================
        private int swingLen = 12;
        private int maxZones = 10;
        private int minZoneDistance = 44;
        private double atrMultiplier = 1.0;

        // ================================
        // INTERNALS
        // ================================
        private ATR atr;
        private List<Zone> zones;

        private int lastSupplyBar = -1000;
        private int lastDemandBar = -1000;

        // ================================
        // ZONE CLASS
        // ================================
        private class Zone
        {
            public double Top;
            public double Bottom;
            public int StartBar;
            public int Retests;
            public bool Broken;
            public string Type; // "Supply" or "Demand"
            public bool RetestCounted;
            public string Tag;
        }

        // ================================
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "SupplyDemandZones_PRO_GC";
                Calculate = Calculate.OnBarClose;
                IsOverlay = true;
            }
            else if (State == State.DataLoaded)
            {
                atr = ATR(20);
                zones = new List<Zone>();
            }
        }

        // ================================
        protected override void OnBarUpdate()
        {
            if (CurrentBar < swingLen * 2)
                return;

            double currentATR = atr[0];

            // ================================
            // PIVOT HIGH (SUPPLY)
            // ================================
            bool pivotHigh = true;
            for (int i = 1; i <= swingLen; i++)
            {
                if (High[swingLen] <= High[swingLen + i] || High[swingLen] <= High[swingLen - i])
                {
                    pivotHigh = false;
                    break;
                }
            }

            if (pivotHigh && CurrentBar - lastSupplyBar > minZoneDistance)
            {
                double top = High[swingLen];
                double bot = Low[swingLen];
                double height = top - bot;

                if (height < currentATR * atrMultiplier)
                {
                    double mid = (top + bot) / 2;
                    double forced = currentATR * atrMultiplier;
                    top = mid + forced / 2;
                    bot = mid - forced / 2;
                }

                CreateZone(top, bot, "Supply");
                lastSupplyBar = CurrentBar;
            }

            // ================================
            // PIVOT LOW (DEMAND)
            // ================================
            bool pivotLow = true;
            for (int i = 1; i <= swingLen; i++)
            {
                if (Low[swingLen] >= Low[swingLen + i] || Low[swingLen] >= Low[swingLen - i])
                {
                    pivotLow = false;
                    break;
                }
            }

            if (pivotLow && CurrentBar - lastDemandBar > minZoneDistance)
            {
                double top = High[swingLen];
                double bot = Low[swingLen];
                double height = top - bot;

                if (height < currentATR * atrMultiplier)
                {
                    double mid = (top + bot) / 2;
                    double forced = currentATR * atrMultiplier;
                    top = mid + forced / 2;
                    bot = mid - forced / 2;
                }

                CreateZone(top, bot, "Demand");
                lastDemandBar = CurrentBar;
            }

            // ================================
            // UPDATE ZONES
            // ================================
            foreach (var z in zones)
            {
                if (z.Broken) continue;

                bool inZone = High[0] >= z.Bottom && Low[0] <= z.Top;

                if (inZone && !z.RetestCounted)
                {
                    z.Retests++;
                    z.RetestCounted = true;
                }
                else if (!inZone)
                {
                    z.RetestCounted = false;
                }

                // Invalidation
                if (z.Type == "Supply" && Close[0] > z.Top)
                    z.Broken = true;

                if (z.Type == "Demand" && Close[0] < z.Bottom)
                    z.Broken = true;

                DrawZone(z);
            }
        }

        // ================================
        private void CreateZone(double top, double bottom, string type)
        {
            Zone z = new Zone();
            z.Top = top;
            z.Bottom = bottom;
            z.StartBar = CurrentBar;
            z.Type = type;
            z.Retests = 0;
            z.Broken = false;
            z.Tag = "Zone_" + CurrentBar;

            zones.Add(z);

            if (zones.Count > maxZones)
                zones.RemoveAt(0);
        }

        // ================================
        private void DrawZone(Zone z)
        {
            Brush color = z.Type == "Supply"
                ? Brushes.Red
                : Brushes.Green;

            if (z.Broken)
                color = Brushes.Gray;

            Draw.Rectangle(
                this,
                z.Tag,
                false,
                CurrentBar - z.StartBar,
                z.Top,
                0,
                z.Bottom,
                color,
                color,
                20
            );
        }
    }
}





#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private SupplyDemandZones_PRO_GC[] cacheSupplyDemandZones_PRO_GC;
		public SupplyDemandZones_PRO_GC SupplyDemandZones_PRO_GC()
		{
			return SupplyDemandZones_PRO_GC(Input);
		}

		public SupplyDemandZones_PRO_GC SupplyDemandZones_PRO_GC(ISeries<double> input)
		{
			if (cacheSupplyDemandZones_PRO_GC != null)
				for (int idx = 0; idx < cacheSupplyDemandZones_PRO_GC.Length; idx++)
					if (cacheSupplyDemandZones_PRO_GC[idx] != null &&  cacheSupplyDemandZones_PRO_GC[idx].EqualsInput(input))
						return cacheSupplyDemandZones_PRO_GC[idx];
			return CacheIndicator<SupplyDemandZones_PRO_GC>(new SupplyDemandZones_PRO_GC(), input, ref cacheSupplyDemandZones_PRO_GC);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.SupplyDemandZones_PRO_GC SupplyDemandZones_PRO_GC()
		{
			return indicator.SupplyDemandZones_PRO_GC(Input);
		}

		public Indicators.SupplyDemandZones_PRO_GC SupplyDemandZones_PRO_GC(ISeries<double> input )
		{
			return indicator.SupplyDemandZones_PRO_GC(input);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.SupplyDemandZones_PRO_GC SupplyDemandZones_PRO_GC()
		{
			return indicator.SupplyDemandZones_PRO_GC(Input);
		}

		public Indicators.SupplyDemandZones_PRO_GC SupplyDemandZones_PRO_GC(ISeries<double> input )
		{
			return indicator.SupplyDemandZones_PRO_GC(input);
		}
	}
}

#endregion
