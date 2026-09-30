#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Core;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

/*
 * SupplyDemandZonesPROGC — NinjaTrader 8
 * Full logic parity with Pine Script v6 "Supply Demand Zones PRO"
 *
 * Fixes applied vs previous version:
 *   FIX 1 — Session check now uses Time[0] (detection bar) not Time[SwingLen]
 *            to match Pine's f_getSession() which runs at the confirmed bar,
 *            not at the pivot candle itself.
 *   FIX 2 — _lastSupplyBar / _lastDemandBar now only update when a NEW zone
 *            is created, not on merge. Matches Pine where lastSupplyBar is
 *            only set inside the "if not mergedWithExisting" block.
 */

namespace NinjaTrader.NinjaScript.Indicators
{
    public class InvalidationConverter : TypeConverter
    {
        public override bool GetStandardValuesSupported(ITypeDescriptorContext ctx) => true;
        public override bool GetStandardValuesExclusive(ITypeDescriptorContext ctx) => true;
        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext ctx)
            => new StandardValuesCollection(new[] { "Close", "Wick" });
    }

    public class SupplyDemandZonesPROGC : Indicator
    {
        // ====================================================================
        // ZONE DATA CLASS
        // ====================================================================
        private class Zone
        {
            public double Top, Bottom;
            public int    StartBar;
            public string Session, Type;
            public int    Retests;
            public bool   Broken, RetestCounted;
            public string BoxTag, InfoTag, RetestTag;
        }

        // ====================================================================
        // PRIVATE STATE
        // ====================================================================
        private ATR        _atr;
        private List<Zone> _zones;
        private int        _lastSupplyBar = -9999;
        private int        _lastDemandBar = -9999;
        private SimpleFont _labelFont;
        private Brush      _transparent;

        // ====================================================================
        // INPUTS — Zone Detection
        // ====================================================================
        [Range(3, 50)]
        [Display(Name = "Swing Length (Sensitivity)",
                 Description = "Higher = fewer, more significant zones",
                 GroupName = "Zone Detection", Order = 1)]
        public int SwingLen { get; set; }

        [Range(3, 30)]
        [Display(Name = "Max Zones to Display",
                 GroupName = "Zone Detection", Order = 2)]
        public int MaxZones { get; set; }

        [Range(0.5, 10.0)]
        [Display(Name = "Max Zone Height (ATR Multiplier)",
                 Description = "Zones taller than this are clamped",
                 GroupName = "Zone Detection", Order = 3)]
        public double MaxZoneHeightATR { get; set; }

        [Range(0.1, 2.0)]
        [Display(Name = "Min Zone Height (ATR Multiplier)",
                 Description = "Zones smaller than this are rejected",
                 GroupName = "Zone Detection", Order = 4)]
        public double MinZoneHeightATR { get; set; }

        [Range(0.2, 3.0)]
        [Display(Name = "Force Zone Height (ATR Multiplier)",
                 Description = "Thin zones are expanded to at least this height",
                 GroupName = "Zone Detection", Order = 5)]
        public double ForceZoneHeightATR { get; set; }

        [Range(1, 100)]
        [Display(Name = "Min Distance Between Zones (bars)",
                 GroupName = "Zone Detection", Order = 6)]
        public int MinZoneDistance { get; set; }

        // ====================================================================
        // INPUTS — Zone Settings
        // ====================================================================
        [TypeConverter(typeof(InvalidationConverter))]
        [Display(Name = "Zone Invalidation",
                 Description = "Close: candle close past zone edge  |  Wick: wick past zone edge",
                 GroupName = "Zone Settings", Order = 1)]
        public string InvalidationMethod { get; set; }

        [Display(Name = "Show Historic Zones",
                 GroupName = "Zone Settings", Order = 2)]
        public bool ShowHistoric { get; set; }

        [Range(100, 5000)]
        [Display(Name = "Active Zones Lookback (bars)",
                 GroupName = "Zone Settings", Order = 3)]
        public int ActiveLookback { get; set; }

        [Range(100, 5000)]
        [Display(Name = "Historic Zones Lookback (bars)",
                 GroupName = "Zone Settings", Order = 4)]
        public int HistoricBars { get; set; }

        // ====================================================================
        // INPUTS — Display
        // ====================================================================
        [Display(Name = "Show Active Zone Labels",
                 GroupName = "Display", Order = 1)]
        public bool ShowActiveLabels { get; set; }

        [Display(Name = "Show Historic Zone Labels",
                 GroupName = "Display", Order = 2)]
        public bool ShowHistoricLabels { get; set; }

        // ====================================================================
        // INPUTS — Colors
        // ====================================================================
        [XmlIgnore]
        [Display(Name = "Active Supply Color", GroupName = "Colors", Order = 1)]
        public Brush ActiveSupplyBrush { get; set; }
        [Browsable(false)]
        public string ActiveSupplyBrushSerializable
        {
            get { return Serialize.BrushToString(ActiveSupplyBrush); }
            set { ActiveSupplyBrush = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Active Demand Color", GroupName = "Colors", Order = 2)]
        public Brush ActiveDemandBrush { get; set; }
        [Browsable(false)]
        public string ActiveDemandBrushSerializable
        {
            get { return Serialize.BrushToString(ActiveDemandBrush); }
            set { ActiveDemandBrush = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Historic Zones Color", GroupName = "Colors", Order = 3)]
        public Brush HistoricBrush { get; set; }
        [Browsable(false)]
        public string HistoricBrushSerializable
        {
            get { return Serialize.BrushToString(HistoricBrush); }
            set { HistoricBrush = Serialize.StringToBrush(value); }
        }

        // ====================================================================
        // INPUTS — Retests
        // ====================================================================
        [Display(Name = "Show Retest Markers",
                 GroupName = "Retests", Order = 1)]
        public bool ShowRetestMarkers { get; set; }

        [XmlIgnore]
        [Display(Name = "Demand Retest Color", GroupName = "Retests", Order = 2)]
        public Brush RetestBullBrush { get; set; }
        [Browsable(false)]
        public string RetestBullBrushSerializable
        {
            get { return Serialize.BrushToString(RetestBullBrush); }
            set { RetestBullBrush = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Supply Retest Color", GroupName = "Retests", Order = 3)]
        public Brush RetestBearBrush { get; set; }
        [Browsable(false)]
        public string RetestBearBrushSerializable
        {
            get { return Serialize.BrushToString(RetestBearBrush); }
            set { RetestBearBrush = Serialize.StringToBrush(value); }
        }

        // ====================================================================
        // INPUTS — Alerts
        // ====================================================================
        [Display(Name = "Enable Alerts", GroupName = "Alerts", Order = 1)]
        public bool AlertsOn { get; set; }

        // ====================================================================
        // LIFECYCLE
        // ====================================================================
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name        = "SupplyDemandZonesPROGC";
                Calculate   = Calculate.OnBarClose;
                IsOverlay   = true;

                SwingLen           = 12;
                MaxZones           = 10;
                MaxZoneHeightATR   = 1.0;
                MinZoneHeightATR   = 1.0;
                ForceZoneHeightATR = 1.0;
                MinZoneDistance    = 44;
                InvalidationMethod = "Close";
                ShowHistoric       = true;
                ActiveLookback     = 1000;
                HistoricBars       = 1000;
                ShowActiveLabels   = true;
                ShowHistoricLabels = false;
                ShowRetestMarkers  = true;
                AlertsOn           = true;

                ActiveSupplyBrush = new SolidColorBrush(Color.FromRgb(220, 50, 50));
                ActiveDemandBrush = new SolidColorBrush(Color.FromRgb(50, 200, 50));
                HistoricBrush     = new SolidColorBrush(Color.FromRgb(150, 150, 150));
                RetestBullBrush   = new SolidColorBrush(Color.FromRgb(50, 200, 50));
                RetestBearBrush   = new SolidColorBrush(Color.FromRgb(220, 50, 50));

                ActiveSupplyBrush.Freeze();
                ActiveDemandBrush.Freeze();
                HistoricBrush.Freeze();
                RetestBullBrush.Freeze();
                RetestBearBrush.Freeze();
            }
            else if (State == State.Configure)
            {
                _labelFont   = new SimpleFont("Arial", 9) { Bold = false };
                _transparent = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
                _transparent.Freeze();
            }
            else if (State == State.DataLoaded)
            {
                _atr   = ATR(20);
                _zones = new List<Zone>();
            }
        }

        // ====================================================================
        // MAIN BAR UPDATE
        // ====================================================================
        protected override void OnBarUpdate()
        {
            if (CurrentBar < SwingLen * 2 + 2) return;

            double curATR = _atr[0];

            // ----------------------------------------------------------------
            // PIVOT HIGH → SUPPLY
            // ----------------------------------------------------------------
            if (IsPivotHigh(SwingLen) && CurrentBar - _lastSupplyBar >= MinZoneDistance)
            {
                // FIX 1: Use Time[0] (detection/confirmed bar) to match Pine's
                // f_getSession() which runs at the current bar, not the pivot bar
                string sess = GetSession(Time[0]);

                if (sess != "Other")
                {
                    double top = High[SwingLen];
                    double bot = Low[SwingLen];

                    if (NormalizeHeight(ref top, ref bot, curATR))
                    {
                        // FIX 2: _lastSupplyBar only updates on NEW zone creation,
                        // not on merge — matches Pine "if not mergedWithExisting" block
                        if (!TryMerge(top, bot, "Supply"))
                        {
                            CreateZone(top, bot, "Supply", sess, CurrentBar - SwingLen);
                            if (AlertsOn)
                                Alert("SDZ_Sup_" + CurrentBar, Priority.Medium,
                                    Instrument.FullName + " — New Supply Zone @ " + Close[0].ToString("F5"),
                                    NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert1.wav",
                                    10, Brushes.Red, Brushes.White);

                            // INSIDE the if block — only set on new zone, not merge
                            _lastSupplyBar = CurrentBar;
                        }
                    }
                }
            }

            // ----------------------------------------------------------------
            // PIVOT LOW → DEMAND
            // ----------------------------------------------------------------
            if (IsPivotLow(SwingLen) && CurrentBar - _lastDemandBar >= MinZoneDistance)
            {
                // FIX 1: Use Time[0] (detection/confirmed bar) to match Pine
                string sess = GetSession(Time[0]);

                if (sess != "Other")
                {
                    double top = High[SwingLen];
                    double bot = Low[SwingLen];

                    if (NormalizeHeight(ref top, ref bot, curATR))
                    {
                        // FIX 2: _lastDemandBar only updates on NEW zone creation,
                        // not on merge — matches Pine "if not mergedWithExisting" block
                        if (!TryMerge(top, bot, "Demand"))
                        {
                            CreateZone(top, bot, "Demand", sess, CurrentBar - SwingLen);
                            if (AlertsOn)
                                Alert("SDZ_Dem_" + CurrentBar, Priority.Medium,
                                    Instrument.FullName + " — New Demand Zone @ " + Close[0].ToString("F5"),
                                    NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert1.wav",
                                    10, Brushes.Green, Brushes.White);

                            // INSIDE the if block — only set on new zone, not merge
                            _lastDemandBar = CurrentBar;
                        }
                    }
                }
            }

            // ----------------------------------------------------------------
            // UPDATE ALL ZONES
            // ----------------------------------------------------------------
            int activeSupCount = 0, activeDeCount = 0;
            int histSupCount   = 0, histDeCount   = 0;

            foreach (Zone z in _zones)
            {
                // Retest tracking (debounced — one increment per continuous touch)
                if (!z.Broken)
                {
                    bool inZone = High[0] >= z.Bottom && Low[0] <= z.Top;
                    if (inZone && !z.RetestCounted) { z.Retests++; z.RetestCounted = true; }
                    else if (!inZone) z.RetestCounted = false;
                }

                // Invalidation
                if (!z.Broken)
                {
                    if (z.Type == "Supply")
                    {
                        double bl = (InvalidationMethod == "Close") ? Close[0] : High[0];
                        if (bl > z.Top) z.Broken = true;
                    }
                    else
                    {
                        double bl = (InvalidationMethod == "Close") ? Close[0] : Low[0];
                        if (bl < z.Bottom) z.Broken = true;
                    }
                }

                // Visibility logic — mirrors Pine shouldHide block exactly
                int    age    = CurrentBar - z.StartBar;
                double height = z.Top - z.Bottom;
                bool   hide   = false;

                if (z.Broken && !ShowHistoric)         hide = true;
                if (z.Broken && age > HistoricBars)    hide = true;
                if (!z.Broken && age > ActiveLookback) hide = true;

                if (z.Broken && ShowHistoric && !hide)
                {
                    if (height < _atr[0] * MinZoneHeightATR) hide = true;
                    if (!hide)
                    {
                        if (z.Type == "Supply") { if (histSupCount >= MaxZones) hide = true; else histSupCount++; }
                        else                    { if (histDeCount  >= MaxZones) hide = true; else histDeCount++;  }
                    }
                }

                if (!z.Broken && !hide)
                {
                    if (z.Type == "Supply") { if (activeSupCount >= MaxZones) hide = true; else activeSupCount++; }
                    else                    { if (activeDeCount  >= MaxZones) hide = true; else activeDeCount++;  }
                }

                DrawZone(z, hide, age);
            }
        }

        // ====================================================================
        // PIVOT DETECTION
        // Equivalent to ta.pivothigh(high, len, len) / ta.pivotlow(low, len, len)
        // Checks len bars on each side of bar at index [len]
        // ====================================================================
        private bool IsPivotHigh(int len)
        {
            for (int i = 1; i <= len; i++)
                if (High[len] <= High[len + i] || High[len] <= High[len - i]) return false;
            return true;
        }

        private bool IsPivotLow(int len)
        {
            for (int i = 1; i <= len; i++)
                if (Low[len] >= Low[len + i] || Low[len] >= Low[len - i]) return false;
            return true;
        }

        // ====================================================================
        // ZONE HEIGHT NORMALISATION
        // Mirrors Pine pipeline exactly: Force → Clamp → Reject
        // Returns false if zone should be rejected after all steps
        // ====================================================================
        private bool NormalizeHeight(ref double top, ref double bot, double curATR)
        {
            double mid = (top + bot) / 2.0;
            double h   = top - bot;

            // Step 1 — Force minimum height (expand thin zones)
            if (h < curATR * ForceZoneHeightATR)
            {
                double fh = curATR * ForceZoneHeightATR;
                top = mid + fh / 2;
                bot = mid - fh / 2;
                h   = fh;
            }

            // Step 2 — Clamp maximum height (shrink huge zones)
            if (h > curATR * MaxZoneHeightATR)
            {
                double mh = curATR * MaxZoneHeightATR;
                top = mid + mh / 2;
                bot = mid - mh / 2;
                h   = mh;
            }

            // Step 3 — Reject if still below minimum after forcing
            return h >= curATR * MinZoneHeightATR;
        }

        // ====================================================================
        // OVERLAP MERGE  (mirrors Pine: checks last 10 same-type active zones)
        // Returns true if merged into existing zone — no new zone created
        // ====================================================================
        private bool TryMerge(double top, double bot, string type)
        {
            int start = Math.Max(0, _zones.Count - 10);
            for (int i = _zones.Count - 1; i >= start; i--)
            {
                Zone z = _zones[i];
                if (z.Type != type || z.Broken) continue;
                if (top >= z.Bottom && bot <= z.Top)
                {
                    z.Top    = Math.Max(top, z.Top);
                    z.Bottom = Math.Min(bot, z.Bottom);
                    return true;
                }
            }
            return false;
        }

        // ====================================================================
        // CREATE ZONE
        // Soft cap: MaxZones * 4, oldest removed (mirrors Pine array.shift)
        // ====================================================================
        private void CreateZone(double top, double bot, string type, string sess, int startBar)
        {
            string id = type + "_" + startBar;
            _zones.Add(new Zone
            {
                Top = top, Bottom = bot, StartBar = startBar,
                Session = sess, Type = type,
                BoxTag    = "SDZ_Box_"  + id,
                InfoTag   = "SDZ_Info_" + id,
                RetestTag = "SDZ_Ret_"  + id
            });
            while (_zones.Count > MaxZones * 4) _zones.RemoveAt(0);
        }

        // ====================================================================
        // DRAW ZONE  (called every bar — same tag updates in place)
        // areaOpacity 20 = active zones (Pine color.new(col, 80))
        // areaOpacity 15 = historic zones (Pine color.new(col, 85))
        // ====================================================================
        private void DrawZone(Zone z, bool hide, int age)
        {
            int leftBars = CurrentBar - z.StartBar;

            if (hide)
            {
                Draw.Rectangle(this, z.BoxTag, false, leftBars, z.Top, 0, z.Bottom,
                               _transparent, _transparent, 0);
                Draw.Text(this, z.InfoTag, false, " ", 0, Close[0], 0,
                          _transparent, _labelFont, TextAlignment.Left,
                          _transparent, _transparent, 0);
                return;
            }

            // Zone box
            Brush fill    = z.Broken ? HistoricBrush : (z.Type == "Supply" ? ActiveSupplyBrush : ActiveDemandBrush);
            int   opacity = z.Broken ? 15 : 20;

            Draw.Rectangle(this, z.BoxTag, false, leftBars, z.Top, 0, z.Bottom,
                           _transparent, fill, opacity);

            // Info label
            bool showLabel = z.Broken ? ShowHistoricLabels : ShowActiveLabels;
            if (showLabel)
            {
                double hPips  = ToPips(z.Top - z.Bottom);
                double dPips  = z.Type == "Supply" ? ToPips(z.Bottom - Close[0]) : ToPips(Close[0] - z.Top);
                int    str    = CalcStrength(z.Retests, age);
                double mid    = (z.Top + z.Bottom) / 2.0;
                Brush  tBrush = z.Broken ? Brushes.Gray : (z.Type == "Supply" ? Brushes.Red : Brushes.Green);

                string txt = string.Format(
                    "{0:F0} pips  |  {1}  |  Strength {2}/10  |  {3}  |  {4} bars  |  {5:F0} pips away",
                    hPips, z.Type, str, z.Session, age, Math.Abs(dPips));

                Draw.Text(this, z.InfoTag, false, txt, leftBars - 10, mid, 0,
                          tBrush, _labelFont, TextAlignment.Left,
                          _transparent, _transparent, 0);
            }
            else
            {
                Draw.Text(this, z.InfoTag, false, " ", 0, Close[0], 0,
                          _transparent, _labelFont, TextAlignment.Left,
                          _transparent, _transparent, 0);
            }

            // Retest marker
            if (ShowRetestMarkers && z.Retests > 0)
            {
                int retBars = Math.Max(0, CurrentBar - z.StartBar - 3);
                if (z.Type == "Supply")
                    Draw.ArrowDown(this, z.RetestTag, false, retBars, z.Top, RetestBearBrush);
                else
                    Draw.ArrowUp(this, z.RetestTag, false, retBars, z.Bottom, RetestBullBrush);
            }
            else if (z.Retests == 0)
            {
                Draw.Text(this, z.RetestTag, false, " ", 0, Close[0], 0,
                          _transparent, _labelFont, TextAlignment.Left,
                          _transparent, _transparent, 0);
            }
        }

        // ====================================================================
        // HELPERS
        // ====================================================================

        // Mirrors Pine f_getSession() — h >= 0 && h < 9 etc.
        private string GetSession(DateTime t)
        {
            int h = t.Hour;
            if (h >= 0  && h < 9)  return "Asian";
            if (h >= 8  && h < 13) return "London";
            if (h >= 13 && h < 22) return "New York";
            return "Other";
        }

        // Mirrors Pine f_getPips() — detects instrument type by name, no InstrumentType enum needed
        private double GetPipSize()
        {
            string name = Instrument.MasterInstrument.Name.ToUpper();
            // Gold
            if (name == "XAUUSD" || name == "GOLD") return 0.1;
            // JPY pairs
            if (name.Contains("JPY")) return 0.01;
            // Standard forex pairs (6 chars: EURUSD, GBPUSD etc.)
            // and most CFDs — default to 0.0001
            return 0.0001;
        }

        // Mirrors Pine f_toPips()
        private double ToPips(double val) => Math.Abs(val) / GetPipSize();

        // Mirrors Pine f_calcStrength(): 8 - retests*0.8 - min(age/500, 2) clamped [0,10]
        private int CalcStrength(int retests, int age)
        {
            double s = 8.0 - retests * 0.8 - Math.Min(age / 500.0, 2.0);
            return (int)Math.Max(0, Math.Min(10, s));
        }
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private SupplyDemandZonesPROGC[] cacheSupplyDemandZonesPROGC;
		public SupplyDemandZonesPROGC SupplyDemandZonesPROGC()
		{
			return SupplyDemandZonesPROGC(Input);
		}

		public SupplyDemandZonesPROGC SupplyDemandZonesPROGC(ISeries<double> input)
		{
			if (cacheSupplyDemandZonesPROGC != null)
				for (int idx = 0; idx < cacheSupplyDemandZonesPROGC.Length; idx++)
					if (cacheSupplyDemandZonesPROGC[idx] != null &&  cacheSupplyDemandZonesPROGC[idx].EqualsInput(input))
						return cacheSupplyDemandZonesPROGC[idx];
			return CacheIndicator<SupplyDemandZonesPROGC>(new SupplyDemandZonesPROGC(), input, ref cacheSupplyDemandZonesPROGC);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.SupplyDemandZonesPROGC SupplyDemandZonesPROGC()
		{
			return indicator.SupplyDemandZonesPROGC(Input);
		}

		public Indicators.SupplyDemandZonesPROGC SupplyDemandZonesPROGC(ISeries<double> input )
		{
			return indicator.SupplyDemandZonesPROGC(input);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.SupplyDemandZonesPROGC SupplyDemandZonesPROGC()
		{
			return indicator.SupplyDemandZonesPROGC(Input);
		}

		public Indicators.SupplyDemandZonesPROGC SupplyDemandZonesPROGC(ISeries<double> input )
		{
			return indicator.SupplyDemandZonesPROGC(input);
		}
	}
}

#endregion
