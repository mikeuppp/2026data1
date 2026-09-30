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
        // DELTA ENGINE
        // ================================
        private double cumDelta = 0;
        private double prevCumDelta = 0;
        private Series<double> deltaSeries;

        // ================================
        // DIVERGENCE TRACKING
        // ================================
        private double lastSwingHighPrice = 0;
        private double lastSwingHighDelta = 0;

        private double lastSwingLowPrice = 0;
        private double lastSwingLowDelta = 0;

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
            public string Type;
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
                deltaSeries = new Series<double>(this);
            }
        }

        // ================================
        protected override void OnBarUpdate()
        {
            if (CurrentBar < swingLen * 2)
                return;

            double currentATR = atr[0];

            // ================================
            // DELTA CALCULATION (BASIC)
            // ================================
            double barDelta = Close[0] > Open[0] ? Volume[0] : -Volume[0];

            prevCumDelta = cumDelta;
            cumDelta += barDelta;
            deltaSeries[0] = cumDelta;

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

                // ================================
                // DIVERGENCE (BEARISH)
                // ================================
                double currentDelta = deltaSeries[swingLen];

                if (lastSwingHighPrice != 0)
                {
                    bool bearishDiv =
                        High[swingLen] > lastSwingHighPrice &&
                        currentDelta < lastSwingHighDelta;

                    if (bearishDiv)
                    {
                        Draw.Text(this, "DivSell_" + CurrentBar,
                            "▼ DIV",
                            CurrentBar - swingLen,
                            High[swingLen] + TickSize * 5,
                            Brushes.Red);
                    }
                }

                lastSwingHighPrice = High[swingLen];
                lastSwingHighDelta = currentDelta;

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

                // ================================
                // DIVERGENCE (BULLISH)
                // ================================
                double currentDelta = deltaSeries[swingLen];

                if (lastSwingLowPrice != 0)
                {
                    bool bullishDiv =
                        Low[swingLen] < lastSwingLowPrice &&
                        currentDelta > lastSwingLowDelta;

                    if (bullishDiv)
                    {
                        Draw.Text(this, "DivBuy_" + CurrentBar,
                            "▲ DIV",
                            CurrentBar - swingLen,
                            Low[swingLen] - TickSize * 5,
                            Brushes.Lime);
                    }
                }

                lastSwingLowPrice = Low[swingLen];
                lastSwingLowDelta = currentDelta;

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

                // ================================
                // ZONE + DELTA SIGNAL
                // ================================
                if (inZone)
                {
                    if (z.Type == "Supply" && deltaSeries[0] < prevCumDelta)
                    {
                        Draw.Dot(this, "Sell_" + CurrentBar,
                            false,
                            0,
                            High[0] + TickSize * 3,
                            Brushes.Red);
                    }

                    if (z.Type == "Demand" && deltaSeries[0] > prevCumDelta)
                    {
                        Draw.Dot(this, "Buy_" + CurrentBar,
                            false,
                            0,
                            Low[0] - TickSize * 3,
                            Brushes.Lime);
                    }
                }

                // ================================
                // ABSORPTION (SIMPLE)
                // ================================
                double range = High[0] - Low[0];
                bool highVol = Volume[0] > SMA(Volume, 20)[0] * 1.5;
                bool smallRange = range < ATR(14)[0] * 0.5;
                bool absorption = highVol && smallRange;

                if (inZone && absorption)
                {
                    Draw.TriangleUp(this, "Absorb_" + CurrentBar,
                        false,
                        0,
                        Low[0] - TickSize * 6,
                        Brushes.Gold);
                }

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
            Brush color = z.Type == "Supply" ? Brushes.Red : Brushes.Green;

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