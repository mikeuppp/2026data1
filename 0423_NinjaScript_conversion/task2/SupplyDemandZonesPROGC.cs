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
using NinjaTrader.NinjaScript.Indicators;
#endregion

/*
 * SupplyDemandZonesPROGC — NinjaTrader 8
 * Full logic parity with Pine Script v6 "Supply Demand Zones PRO"
 *
 * Fixes applied vs previous version:
 *   FIX 1 — Session check now uses Time[0] (detection bar) not Time[SwingLen]
 *            to match Pine's f_getSession() which runs at the confirmed bar,
 *            not at the pivot candle itself.
 *   FIX 2 — Zone spacing/merge now supports ATR-based price proximity to avoid
 *            zone clustering by bar-count spacing on lower timeframes.
 *   FIX 3 — Session buckets are mutually exclusive (Asian 0–7, London 8–12,
 *            NY 13–21) so hour 8 is London, not Asian. Labels use Time[0] in
 *            the instrument’s timezone (typically exchange/session time), not
 *            necessarily UTC or broker “market” offsets.
 *   FIX 4 — Absorption arrows: setup bar (volume + wick + strength), optional
 *            one-bar confirm (still in zone, no second volume bar required).
 *            Pending is not cleared on quiet in-zone bars. Arrows at barsAgo 0
 *            on the fire bar, realtime only (no historical back-drawing).
 *   MTF   — Optional higher-timeframe zone overlay (e.g. 15m on 1m chart).
 *            HTF pivots/invalidation run on the secondary series; zones draw on
 *            the chart timeframe. LTF zones keep absorption/execution logic.
 *            Nested LTF-inside-HTF structure can be highlighted; overlay toggles off.
 *   DELTA — Optional tick delta (OnMarketData) with zone divergence labels and dashboard.
 */

namespace NinjaTrader.NinjaScript.Indicators
{
    public class SDZInvalidationConverter : TypeConverter
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
            public double ATRatCreation;

            // Absorption: avoid spam / retro-looking duplicates on same touch
            public bool   AbsorptionBuyPending, AbsorptionSellPending;
            public int    AbsorptionBuyPendingBar, AbsorptionSellPendingBar;
            public bool   AbsorptionFiredThisTouchBuy, AbsorptionFiredThisTouchSell;
            public string LastBuyArrowTag, LastSellArrowTag;
            public bool   DrawnOnChart;

            // HTF overlay: time anchor for drawing on a lower-timeframe chart
            public bool     IsHigherTimeframe;
            public DateTime StartTime;

            // Delta markers at zones
            public int  DeltaConsecutiveCount;
            public bool DeltaDivergenceDrawnThisTouch;
        }

        // ====================================================================
        // PRIVATE STATE
        // ====================================================================
        private ATR        _atr;
        private SMA        _volSma;
        private List<Zone> _zones;
        private SimpleFont _labelFont;
        private Brush      _transparent;
        private double     _pipSize;
        private const string ProximityPanelTag = "SDZ_ProximityDashboard";
        private const int    HtfSeriesIndex = 1;

        private ATR        _htfAtr;
        private List<Zone> _htfZones;
        private bool       _htfSeriesReady;

        // Order flow — per-tick delta accumulation (OnMarketData)
        private double         _currentBarDelta;
        private double         _cumulativeDelta;
        private double         _prevBarDelta;
        private Series<double> _deltaSeries;

        // Liquidity sweep — recent swing levels for stop-run detection
        private List<double> _swingHighs;
        private List<double> _swingLows;
        private List<int>    _swingHighBars;
        private List<int>    _swingLowBars;
        private int          _lastSweptSwingHighBar;
        private int          _lastSweptSwingLowBar;

        // Delta vs price — swing pivots with bar delta at formation
        private double _lastSwingHighPrice;
        private double _lastSwingLowPrice;
        private double _deltaAtLastSwingHigh;
        private double _deltaAtLastSwingLow;
        private double _prevSwingHighPrice;
        private double _prevSwingLowPrice;
        private double _deltaAtPrevSwingHigh;
        private double _deltaAtPrevSwingLow;

        // ====================================================================
        // INPUTS — Absorption (volume + momentum signals)
        // ====================================================================
        [NinjaScriptProperty]
        [Range(1.0, double.MaxValue)]
        [Display(Name = "Volume Threshold Multiplier",
                 Description = "Bar volume must exceed SMA(volume) by this multiple",
                 GroupName = "Absorption", Order = 1)]
        public double VolumeThresholdMultiplier { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Volume Lookback (bars)",
                 GroupName = "Absorption", Order = 2)]
        public int VolumeLookback { get; set; }

        [NinjaScriptProperty]
        [Range(0, 10)]
        [Display(Name = "Min Zone Strength for Absorption (0–10)",
                 Description = "0 = disabled. Mirrors label strength; higher = fewer arrows.",
                 GroupName = "Absorption", Order = 3)]
        public int MinZoneStrengthForAbsorption { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Require 1-Bar Confirm After Setup",
                 Description = "Setup bar needs volume + wick + momentum. If on, the next bar only needs " +
                               "price still in zone (no second volume spike). Off = arrow on setup bar.",
                 GroupName = "Absorption", Order = 4)]
        public bool RequireConsecutiveAbsorptionBars { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Require Close Inside Zone",
                 Description = "If true, Close must be inside the zone (not only wick overlap). Reduces ‘wick only’ false signals.",
                 GroupName = "Absorption", Order = 5)]
        public bool RequireCloseInZoneForAbsorption { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Volume SMA Extra Warmup (bars)",
                 Description = "Require CurrentBar >= VolumeLookback + this before volume gate applies. Reduces early-series flips on reload.",
                 GroupName = "Absorption", Order = 6)]
        public int VolumeSmaExtraWarmupBars { get; set; }

        // ====================================================================
        // INPUTS — Order Flow (delta accumulation)
        // ====================================================================
        [NinjaScriptProperty]
        [Display(Name = "Enable Delta Accumulation",
                 Description = "Accumulate aggressive buy/sell volume per tick via OnMarketData.",
                 GroupName = "Order Flow", Order = 1)]
        public bool EnableDelta { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Delta Markers at Zones",
                 Description = "Green/red dot below/above the signal candle when in a zone and |delta| exceeds threshold.",
                 GroupName = "Order Flow", Order = 2)]
        public bool ShowDeltaDivergence { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 500000.0)]
        [Display(Name = "Delta Threshold (contracts/volume)",
                 Description = "Minimum |bar delta| required to plot a delta marker at a zone.",
                 GroupName = "Order Flow", Order = 3)]
        public double DeltaThreshold { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Delta on Proximity Dashboard",
                 GroupName = "Order Flow", Order = 4)]
        public bool ShowDeltaOnDashboard { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 1000000.0)]
        [Display(Name = "Delta Min Bar Volume",
                 Description = "Minimum total volume on the delta bar (e.g. 100 for NQ).",
                 GroupName = "Order Flow", Order = 5)]
        public double DeltaMinVolume { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 1.0)]
        [Display(Name = "Delta Dominance Ratio",
                 Description = "|delta| / bar volume must exceed this (e.g. 0.6 = 60% one-sided).",
                 GroupName = "Order Flow", Order = 6)]
        public double DeltaDominanceRatio { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Require Delta Price Divergence",
                 Description = "HH+lower delta at supply, LL+higher delta at demand vs last swing.",
                 GroupName = "Order Flow", Order = 7)]
        public bool RequireDeltaPriceDivergence { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Require Delta Consecutive Bars",
                 Description = "Delta must pass volume filters on N bars in a row inside the zone.",
                 GroupName = "Order Flow", Order = 8)]
        public bool RequireDeltaConsecutiveBars { get; set; }

        [NinjaScriptProperty]
        [Range(2, 5)]
        [Display(Name = "Delta Consecutive Bars Required",
                 GroupName = "Order Flow", Order = 9)]
        public int DeltaConsecutiveBarsRequired { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Require Zone Rejection for Delta",
                 Description = "Demand: touch bottom + close higher. Supply: touch top + close lower.",
                 GroupName = "Order Flow", Order = 10)]
        public bool RequireDeltaZoneRejection { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Liquidity Sweep Detection",
                 GroupName = "Order Flow", Order = 11)]
        public bool EnableSweepDetection { get; set; }

        [NinjaScriptProperty]
        [Range(3, 20)]
        [Display(Name = "Sweep Swing Length",
                 Description = "Pivot length for swing highs/lows used in sweep detection.",
                 GroupName = "Order Flow", Order = 12)]
        public int SweepSwingLen { get; set; }

        [NinjaScriptProperty]
        [Range(5, 100)]
        [Display(Name = "Sweep Lookback (bars)",
                 Description = "How far back to keep swing levels for sweep tests.",
                 GroupName = "Order Flow", Order = 13)]
        public int SweepLookback { get; set; }

        [NinjaScriptProperty]
        [Range(0.5, 5.0)]
        [Display(Name = "Sweep Confirmation (ATR)",
                 Description = "0 = strict close reclaim only. >0 allows close within this ATR of the swing after a wick through.",
                 GroupName = "Order Flow", Order = 14)]
        public double SweepConfirmationATR { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Alert on Sweep",
                 GroupName = "Order Flow", Order = 15)]
        public bool AlertOnSweep { get; set; }

        // ====================================================================
        // INPUTS — Zone Detection
        // ====================================================================
        [NinjaScriptProperty]
        [Range(3, 50)]
        [Display(Name = "Swing Length (Sensitivity)",
                 Description = "Higher = fewer, more significant zones",
                 GroupName = "Zone Detection", Order = 1)]
        public int SwingLen { get; set; }

        [NinjaScriptProperty]
        [Range(3, 30)]
        [Display(Name = "Max Zones to Display",
                 Description = "Lower values reduce chart load time (fewer draw objects per bar).",
                 GroupName = "Zone Detection", Order = 2)]
        public int MaxZones { get; set; }

        [NinjaScriptProperty]
        [Range(0.5, 10.0)]
        [Display(Name = "Max Zone Height (ATR Multiplier)",
                 Description = "Zones taller than this are clamped",
                 GroupName = "Zone Detection", Order = 3)]
        public double MaxZoneHeightATR { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 2.0)]
        [Display(Name = "Min Zone Height (ATR Multiplier)",
                 Description = "Zones smaller than this are rejected",
                 GroupName = "Zone Detection", Order = 4)]
        public double MinZoneHeightATR { get; set; }

        [NinjaScriptProperty]
        [Range(0.2, 3.0)]
        [Display(Name = "Force Zone Height (ATR Multiplier)",
                 Description = "Thin zones are expanded to at least this height",
                 GroupName = "Zone Detection", Order = 5)]
        public double ForceZoneHeightATR { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 10.0)]
        [Display(Name = "Min Zone Separation (ATR)",
                 Description = "Minimum center-to-center distance between same-type active zones. " +
                               "0 disables separation. Nearby candidates are merged.",
                 GroupName = "Zone Detection", Order = 6)]
        public double MinZoneSeparationATR { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Strict Pivot",
                 Description = "On: highs/lows must be strictly higher/lower than both sides " +
                               "(TradingView pivot style). Off: ties on either side still count.",
                 GroupName = "Zone Detection", Order = 7)]
        public bool StrictPivot { get; set; }

        [NinjaScriptProperty]
        [Range(0, 500)]
        [Display(Name = "Merge Zone Lookback",
                 Description = "Most-recent zones in the internal list checked for overlap merge. " +
                               "Lower = faster; use 20+ on noisy charts if duplicates appear. " +
                               "0 = scan every active zone of that type.",
                 GroupName = "Zone Detection", Order = 8)]
        public int MaxMergeLookback { get; set; }

        // ====================================================================
        // INPUTS — Zone Settings
        // ====================================================================
        [NinjaScriptProperty]
        [TypeConverter(typeof(SDZInvalidationConverter))]
        [Display(Name = "Zone Invalidation",
                 Description = "Close: candle close past zone edge  |  Wick: wick past zone edge",
                 GroupName = "Zone Settings", Order = 1)]
        public string InvalidationMethod { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Historic Zones",
                 GroupName = "Zone Settings", Order = 2)]
        public bool ShowHistoric { get; set; }

        [NinjaScriptProperty]
        [Range(100, 5000)]
        [Display(Name = "Active Zones Lookback (bars)",
                 Description = "Lower values (e.g. 300–500) speed initial chart load.",
                 GroupName = "Zone Settings", Order = 3)]
        public int ActiveLookback { get; set; }

        [NinjaScriptProperty]
        [Range(100, 5000)]
        [Display(Name = "Historic Zones Lookback (bars)",
                 Description = "Lower values speed load; disable Show Historic Zones if not needed.",
                 GroupName = "Zone Settings", Order = 4)]
        public int HistoricBars { get; set; }

        // ====================================================================
        // INPUTS — Display
        // ====================================================================
        [NinjaScriptProperty]
        [Display(Name = "Show Active Zone Labels",
                 GroupName = "Display", Order = 1)]
        public bool ShowActiveLabels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Historic Zone Labels",
                 GroupName = "Display", Order = 2)]
        public bool ShowHistoricLabels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Proximity Dashboard",
                 Description = "Top-right supply/demand distance panel. Off saves draw work each bar.",
                 GroupName = "Display", Order = 3)]
        public bool ShowProximityDashboard { get; set; }

        // ====================================================================
        // INPUTS — Multi-Timeframe (HTF overlay on chart timeframe)
        // ====================================================================
        [NinjaScriptProperty]
        [Display(Name = "Enable HTF Zone Overlay",
                 Description = "Draw higher-timeframe supply/demand zones on this chart (e.g. 15m on 1m). " +
                               "Turn off to avoid clutter; LTF zones and absorption stay on the chart period.",
                 GroupName = "Multi-Timeframe", Order = 1)]
        public bool EnableHtfOverlay { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "HTF Bars Period Type",
                 Description = "Period type for the overlay series (default Minute for 15m on 1m).",
                 GroupName = "Multi-Timeframe", Order = 2)]
        public BarsPeriodType HtfBarsPeriodType { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "HTF Bars Period Value",
                 Description = "Period value for overlay (e.g. 15 with Minute = 15-minute HTF on a 1-minute chart).",
                 GroupName = "Multi-Timeframe", Order = 3)]
        public int HtfBarsPeriodValue { get; set; }

        [NinjaScriptProperty]
        [Range(1, 30)]
        [Display(Name = "HTF Max Zones (per side)",
                 Description = "Max active supply and demand zones shown from the higher timeframe.",
                 GroupName = "Multi-Timeframe", Order = 4)]
        public int HtfMaxZones { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show HTF Zone Labels",
                 GroupName = "Multi-Timeframe", Order = 5)]
        public bool ShowHtfLabels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Highlight Nested LTF Zones",
                 Description = "Emphasize chart-timeframe zones that sit inside an active HTF zone (nested structure).",
                 GroupName = "Multi-Timeframe", Order = 6)]
        public bool HighlightNestedZones { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "HTF in Proximity Dashboard",
                 Description = "Include nearest HTF supply/demand distances in the top-right panel.",
                 GroupName = "Multi-Timeframe", Order = 7)]
        public bool ShowHtfInProximityDashboard { get; set; }

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
        [NinjaScriptProperty]
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

        [XmlIgnore]
        [Display(Name = "HTF Supply Color", GroupName = "Colors", Order = 4)]
        public Brush HtfSupplyBrush { get; set; }
        [Browsable(false)]
        public string HtfSupplyBrushSerializable
        {
            get { return Serialize.BrushToString(HtfSupplyBrush); }
            set { HtfSupplyBrush = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "HTF Demand Color", GroupName = "Colors", Order = 5)]
        public Brush HtfDemandBrush { get; set; }
        [Browsable(false)]
        public string HtfDemandBrushSerializable
        {
            get { return Serialize.BrushToString(HtfDemandBrush); }
            set { HtfDemandBrush = Serialize.StringToBrush(value); }
        }

        // ====================================================================
        // INPUTS — Alerts
        // ====================================================================
        [NinjaScriptProperty]
        [Display(Name = "Enable Alerts",
                 Description = "Realtime only. Turn off for faster initial chart load.",
                 GroupName = "Alerts", Order = 1)]
        public bool AlertsOn { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Absorption Alerts",
                 Description = "Alerts on momentum-confirmed absorption BUY/SELL arrows",
                 GroupName = "Alerts", Order = 2)]
        public bool AbsorptionAlertsOn { get; set; }

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
                MinZoneSeparationATR = 0.5;
                StrictPivot        = true;
                MaxMergeLookback   = 10;
                InvalidationMethod = "Close";
                ShowHistoric       = true;
                ActiveLookback     = 1000;
                HistoricBars       = 1000;
                ShowActiveLabels   = true;
                ShowHistoricLabels     = false;
                ShowProximityDashboard = true;
                ShowRetestMarkers      = true;
                AlertsOn               = true;
                AbsorptionAlertsOn     = false;

                EnableHtfOverlay            = false;
                HtfBarsPeriodType           = BarsPeriodType.Minute;
                HtfBarsPeriodValue          = 15;
                HtfMaxZones                 = 8;
                ShowHtfLabels               = true;
                HighlightNestedZones        = true;
                ShowHtfInProximityDashboard = true;

                VolumeThresholdMultiplier = 1.5;
                VolumeLookback            = 20;
                MinZoneStrengthForAbsorption = 7;
                RequireConsecutiveAbsorptionBars = true;
                RequireCloseInZoneForAbsorption  = true;
                VolumeSmaExtraWarmupBars         = 5;

                EnableDelta           = false;
                ShowDeltaDivergence   = true;
                DeltaThreshold        = 10.0;
                ShowDeltaOnDashboard  = true;
                DeltaMinVolume        = 100.0;
                DeltaDominanceRatio   = 0.6;
                RequireDeltaPriceDivergence = true;
                RequireDeltaConsecutiveBars = true;
                DeltaConsecutiveBarsRequired = 2;
                RequireDeltaZoneRejection     = true;

                EnableSweepDetection  = true;
                SweepSwingLen         = 12;
                SweepLookback         = 50;
                SweepConfirmationATR  = 0;
                AlertOnSweep          = false;

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

                HtfSupplyBrush = new SolidColorBrush(Color.FromArgb(180, 180, 80, 80));
                HtfDemandBrush = new SolidColorBrush(Color.FromArgb(180, 80, 160, 80));
                HtfSupplyBrush.Freeze();
                HtfDemandBrush.Freeze();
            }
            else if (State == State.Configure)
            {
                _labelFont   = new SimpleFont("Arial", 9) { Bold = false };
                _transparent = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
                _transparent.Freeze();

                if (EnableHtfOverlay)
                    AddDataSeries(HtfBarsPeriodType, HtfBarsPeriodValue);
            }
            else if (State == State.DataLoaded)
            {
                _atr    = ATR(20);
                _volSma = SMA(Volume, VolumeLookback);
                _pipSize = GetPipSize();
                _zones  = new List<Zone>();

                _htfSeriesReady = false;
                _htfZones       = new List<Zone>();
                if (EnableHtfOverlay && BarsArray.Length > HtfSeriesIndex)
                {
                    _htfAtr = ATR(BarsArray[HtfSeriesIndex], 20);
                    _htfSeriesReady = IsHtfHigherThanChart();
                }

                _currentBarDelta  = 0;
                _cumulativeDelta  = 0;
                _prevBarDelta     = 0;
                _deltaSeries      = null;
                if (EnableDelta)
                    _deltaSeries = new Series<double>(this);

                _swingHighs     = new List<double>();
                _swingLows      = new List<double>();
                _swingHighBars  = new List<int>();
                _swingLowBars   = new List<int>();
                _lastSweptSwingHighBar = -1;
                _lastSweptSwingLowBar  = -1;

                _lastSwingHighPrice = _lastSwingLowPrice = 0;
                _deltaAtLastSwingHigh = _deltaAtLastSwingLow = 0;
                _prevSwingHighPrice = _prevSwingLowPrice = 0;
                _deltaAtPrevSwingHigh = _deltaAtPrevSwingLow = 0;
            }
            else if (State == State.Terminated)
            {
                RemoveDrawObjects();
            }
        }

        // ====================================================================
        // MAIN BAR UPDATE
        // ====================================================================
        protected override void OnBarUpdate()
        {
            if (EnableHtfOverlay && _htfSeriesReady && BarsArray.Length > HtfSeriesIndex)
            {
                if (BarsInProgress == HtfSeriesIndex)
                {
                    ProcessHtfBarUpdate();
                    return;
                }
                if (BarsInProgress != 0)
                    return;
            }

            FinalizeBarDelta();

            if (EnableDelta)
                UpdateDeltaSwingPivots();

            if (CurrentBar < SwingLen * 2 + 2) return;

            double avgVolume = 0;
            bool   highVolume = false;
            int    volWarmupNeed = Math.Max(0, VolumeLookback - 1) + VolumeSmaExtraWarmupBars;
            if (CurrentBar >= volWarmupNeed)
            {
                avgVolume  = _volSma[0];
                highVolume = avgVolume > 0 && Volume[0] > avgVolume * VolumeThresholdMultiplier;
            }

            bool bullishShift = CurrentBar >= 1 && Close[0] > Open[0] && Close[0] > Close[1];
            bool bearishShift = CurrentBar >= 1 && Close[0] < Open[0] && Close[0] < Close[1];

            double curATR = _atr[0];

            if (EnableSweepDetection)
                ProcessLiquiditySweeps(curATR);

            // ----------------------------------------------------------------
            // PIVOT HIGH → SUPPLY
            // ----------------------------------------------------------------
            if (IsPivotHigh(SwingLen, 0))
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
                        if (!TryMerge(top, bot, "Supply", curATR, _zones))
                        {
                            CreateZone(top, bot, "Supply", sess, CurrentBar - SwingLen, _zones, false, 0, _atr);
                            if (AlertsOn && State == State.Realtime)
                                Alert("SDZ_Sup_" + CurrentBar, Priority.Medium,
                                    Instrument.FullName + " — New Supply Zone @ " + Close[0].ToString("F5"),
                                    NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert1.wav",
                                    10, Brushes.Red, Brushes.White);

                        }
                    }
                }
            }

            // ----------------------------------------------------------------
            // PIVOT LOW → DEMAND
            // ----------------------------------------------------------------
            if (IsPivotLow(SwingLen, 0))
            {
                // FIX 1: Use Time[0] (detection/confirmed bar) to match Pine
                string sess = GetSession(Time[0]);

                if (sess != "Other")
                {
                    double top = High[SwingLen];
                    double bot = Low[SwingLen];

                    if (NormalizeHeight(ref top, ref bot, curATR))
                    {
                        if (!TryMerge(top, bot, "Demand", curATR, _zones))
                        {
                            CreateZone(top, bot, "Demand", sess, CurrentBar - SwingLen, _zones, false, 0, _atr);
                            if (AlertsOn && State == State.Realtime)
                                Alert("SDZ_Dem_" + CurrentBar, Priority.Medium,
                                    Instrument.FullName + " — New Demand Zone @ " + Close[0].ToString("F5"),
                                    NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert1.wav",
                                    10, Brushes.Green, Brushes.White);

                        }
                    }
                }
            }

            // ----------------------------------------------------------------
            // UPDATE ALL ZONES
            // ----------------------------------------------------------------
            PruneZones();

            int activeSupCount = 0, activeDeCount = 0;
            int histSupCount   = 0, histDeCount   = 0;
            bool shouldRender  = ShouldRenderChart();

            foreach (Zone z in _zones)
            {
                // Retest tracking (debounced — one increment per continuous touch)
                if (!z.Broken)
                {
                    bool inZone = High[0] >= z.Bottom && Low[0] <= z.Top;
                    if (inZone && !z.RetestCounted) { z.Retests++; z.RetestCounted = true; }
                    else if (!inZone)
                    {
                        z.RetestCounted = false;
                        // New approach to zone: allow another absorption signal after full exit
                        z.AbsorptionFiredThisTouchBuy  = false;
                        z.AbsorptionFiredThisTouchSell = false;
                        z.AbsorptionBuyPending         = false;
                        z.AbsorptionSellPending        = false;
                        z.DeltaConsecutiveCount        = 0;
                        z.DeltaDivergenceDrawnThisTouch = false;
                    }

                    if (inZone)
                    {
                        ProcessZoneAbsorption(z, highVolume, bullishShift, bearishShift);
                        if (shouldRender)
                            ProcessDeltaAtZone(z);
                    }
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
                    if (height < z.ATRatCreation * MinZoneHeightATR) hide = true;
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

                if (shouldRender)
                    DrawZone(z, hide, age);
                else if (hide && z.DrawnOnChart)
                {
                    RemoveZoneVisualDrawObjects(z);
                    z.DrawnOnChart = false;
                }
            }

            if (shouldRender)
            {
                if (EnableHtfOverlay && _htfSeriesReady)
                    RenderHtfZonesOnChart();
                UpdateZoneProximityDashboard();
            }
            else
            {
                if (!ShowProximityDashboard)
                    RemoveDrawObject(ProximityPanelTag);
                if (EnableHtfOverlay && _htfSeriesReady)
                    HideAllHtfZoneDrawings();
            }
        }

        // ====================================================================
        // PIVOT DETECTION
        // Equivalent to symmetric ta.pivothigh(high, len, len) / ta.pivotlow on bar close:
        // one candidate per bar (High[len] / Low[len]); StrictPivot matches strict ties.
        // Calculate is OnBarClose, so pivots already run once per new bar—not per tick.
        // Wing extrema cannot be slid bar-to-bar: the pivot center moves, so the L-bar
        // windows on older vs newer pivots overlap but are not the same deque step.
        // StrictPivot is fixed for the whole check (hoisted), with early exit on fail.
        // ====================================================================
        private bool IsPivotHigh(int len, int bip)
        {
            double c = Highs[bip][len];
            if (StrictPivot)
            {
                for (int i = 1; i <= len; i++)
                {
                    if (c <= Highs[bip][len + i] || c <= Highs[bip][len - i])
                        return false;
                }
            }
            else
            {
                for (int i = 1; i <= len; i++)
                {
                    if (c < Highs[bip][len + i] || c < Highs[bip][len - i])
                        return false;
                }
            }
            return true;
        }

        private bool IsPivotLow(int len, int bip)
        {
            double c = Lows[bip][len];
            if (StrictPivot)
            {
                for (int i = 1; i <= len; i++)
                {
                    if (c >= Lows[bip][len + i] || c >= Lows[bip][len - i])
                        return false;
                }
            }
            else
            {
                for (int i = 1; i <= len; i++)
                {
                    if (c > Lows[bip][len + i] || c > Lows[bip][len - i])
                        return false;
                }
            }
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
        // OVERLAP/PROXIMITY MERGE — scans recent _zones slice (same-type, !Broken); see MaxMergeLookback.
        // If candidate overlaps OR is within MinZoneSeparationATR centers, merge to avoid clustering.
        // Returns true if merged into existing zone — no new zone created
        // ====================================================================
        private bool TryMerge(double top, double bot, string type, double curATR, List<Zone> zones)
        {
            double mid = (top + bot) / 2.0;
            double minCenterDist = curATR * MinZoneSeparationATR;
            int window = MaxMergeLookback <= 0 ? zones.Count : MaxMergeLookback;
            int start = Math.Max(0, zones.Count - window);
            for (int i = zones.Count - 1; i >= start; i--)
            {
                Zone z = zones[i];
                if (z.Type != type || z.Broken) continue;
                bool overlap = top >= z.Bottom && bot <= z.Top;
                double zMid = (z.Top + z.Bottom) / 2.0;
                bool tooClose = MinZoneSeparationATR > 0 && Math.Abs(mid - zMid) < minCenterDist;
                if (overlap || tooClose)
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
        // Soft cap: MaxZones * 2, oldest/broken removed
        // ====================================================================
        private void CreateZone(double top, double bot, string type, string sess, int startBar,
            List<Zone> zones, bool isHtf, int bip, ATR atrSeries)
        {
            string prefix = isHtf ? "SDZ_HTF_" : "SDZ_";
            string id = type + "_" + startBar;
            zones.Add(new Zone
            {
                Top = top, Bottom = bot, StartBar = startBar,
                Session = sess, Type = type,
                BoxTag    = prefix + "Box_"  + id,
                InfoTag   = prefix + "Info_" + id,
                RetestTag = prefix + "Ret_"  + id,
                ATRatCreation = atrSeries[0],
                IsHigherTimeframe = isHtf,
                StartTime = isHtf ? Times[bip][SwingLen] : default(DateTime)
            });

            int cap = Math.Max(1, (isHtf ? HtfMaxZones : MaxZones) * 2);
            while (zones.Count > cap)
            {
                int removeIdx = zones.FindIndex(zone => zone.Broken);
                if (removeIdx < 0) removeIdx = 0;

                RemoveZoneDrawObjects(zones[removeIdx]);
                zones.RemoveAt(removeIdx);
            }
        }

        // Prunes stale/broken zones before per-bar processing to keep runtime bounded.
        private void PruneZones(List<Zone> zones, int currentBar, int maxZonesCap)
        {
            int staleBrokenAge = Math.Max(HistoricBars * 2, ActiveLookback * 2);
            for (int i = zones.Count - 1; i >= 0; i--)
            {
                Zone z = zones[i];
                int age = currentBar - z.StartBar;
                if (z.Broken && age > staleBrokenAge)
                {
                    RemoveZoneDrawObjects(z);
                    zones.RemoveAt(i);
                }
            }

            int cap = Math.Max(1, maxZonesCap * 2);
            while (zones.Count > cap)
            {
                int removeIdx = zones.FindIndex(zone => zone.Broken);
                if (removeIdx < 0) removeIdx = 0;

                RemoveZoneDrawObjects(zones[removeIdx]);
                zones.RemoveAt(removeIdx);
            }
        }

        private void PruneZones()
        {
            PruneZones(_zones, CurrentBar, MaxZones);
        }

        private void RemoveZoneVisualDrawObjects(Zone z)
        {
            RemoveDrawObject(z.BoxTag);
            RemoveDrawObject(z.InfoTag);
            RemoveDrawObject(z.RetestTag);
        }

        // Drops box, info, retest, and absorption arrows (used when trimming _zones).
        private void RemoveZoneDrawObjects(Zone z)
        {
            RemoveZoneVisualDrawObjects(z);
            if (!string.IsNullOrEmpty(z.LastBuyArrowTag))
                RemoveDrawObject(z.LastBuyArrowTag);
            if (!string.IsNullOrEmpty(z.LastSellArrowTag))
                RemoveDrawObject(z.LastSellArrowTag);
            z.DrawnOnChart = false;
        }

        private bool ShouldRenderChart()
        {
            return State == State.Realtime
                || (State == State.Historical && CurrentBar >= Count - 2);
        }

        // ====================================================================
        // DRAW ZONE  (same tag updates in place when visible)
        // areaOpacity 20 = active zones (Pine color.new(col, 80))
        // areaOpacity 15 = historic zones (Pine color.new(col, 85))
        // ====================================================================
        private void DrawZone(Zone z, bool hide, int age)
        {
            int leftBars = CurrentBar - z.StartBar;

            if (hide)
            {
                if (z.DrawnOnChart)
                {
                    RemoveZoneVisualDrawObjects(z);
                    z.DrawnOnChart = false;
                }
                return;
            }

            // Zone box
            Brush fill    = z.Broken ? HistoricBrush : (z.Type == "Supply" ? ActiveSupplyBrush : ActiveDemandBrush);
            int   opacity = z.Broken ? 15 : 20;

            Zone nestedHtf = null;
            if (!z.Broken && HighlightNestedZones && EnableHtfOverlay && _htfSeriesReady)
                nestedHtf = FindContainingHtfZone(z);
            if (nestedHtf != null)
                opacity = z.Broken ? 15 : 35;

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

                string nestedSuffix = nestedHtf != null
                    ? string.Format("  |  nested in HTF {0}", nestedHtf.Type)
                    : string.Empty;

                string txt = string.Format(
                    "{0:F0} pips  |  {1}  |  Strength {2}/10  |  {3}  |  {4} bars  |  {5:F0} pips away{6}",
                    hPips, z.Type, str, z.Session, age, Math.Abs(dPips), nestedSuffix);

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
            else if (z.Retests == 0 && z.DrawnOnChart)
                RemoveDrawObject(z.RetestTag);

            z.DrawnOnChart = true;
        }

        private void UpdateZoneProximityDashboard()
        {
            if (!ShowProximityDashboard)
            {
                RemoveDrawObject(ProximityPanelTag);
                return;
            }

            Zone nearestSupply = null;
            Zone nearestDemand = null;
            double nearestSupplyDist = double.MaxValue;
            double nearestDemandDist = double.MaxValue;
            string supplyDirection = "-";
            string demandDirection = "-";
            double close = Close[0];

            foreach (Zone z in _zones)
            {
                if (z.Broken)
                    continue;

                double distance;
                string direction;

                if (z.Type == "Supply")
                {
                    if (close < z.Bottom)
                    {
                        distance = z.Bottom - close;
                        direction = "above";
                    }
                    else if (close > z.Top)
                    {
                        distance = close - z.Top;
                        direction = "below";
                    }
                    else
                    {
                        distance = 0;
                        direction = "inside";
                    }

                    if (distance < nearestSupplyDist)
                    {
                        nearestSupplyDist = distance;
                        nearestSupply = z;
                        supplyDirection = direction;
                    }
                }
                else if (z.Type == "Demand")
                {
                    if (close > z.Top)
                    {
                        distance = close - z.Top;
                        direction = "below";
                    }
                    else if (close < z.Bottom)
                    {
                        distance = z.Bottom - close;
                        direction = "above";
                    }
                    else
                    {
                        distance = 0;
                        direction = "inside";
                    }

                    if (distance < nearestDemandDist)
                    {
                        nearestDemandDist = distance;
                        nearestDemand = z;
                        demandDirection = direction;
                    }
                }
            }

            string supplyText = "n/a";
            if (nearestSupply != null)
            {
                int supplyStrength = CalcStrength(nearestSupply.Retests, CurrentBar - nearestSupply.StartBar);
                supplyText = string.Format("{0:F0} pips (Str {1}) [{2}]",
                    ToPips(nearestSupplyDist), supplyStrength, supplyDirection);
            }

            string demandText = "n/a";
            if (nearestDemand != null)
            {
                int demandStrength = CalcStrength(nearestDemand.Retests, CurrentBar - nearestDemand.StartBar);
                demandText = string.Format("{0:F0} pips (Str {1}) [{2}]",
                    ToPips(nearestDemandDist), demandStrength, demandDirection);
            }

            string panelText = string.Format("↑ Supply {0} | ↓ Demand {1}", supplyText, demandText);

            if (EnableHtfOverlay && _htfSeriesReady && ShowHtfInProximityDashboard && _htfZones != null)
            {
                Zone htfSupply = null, htfDemand = null;
                double htfSupplyDist = double.MaxValue, htfDemandDist = double.MaxValue;
                foreach (Zone z in _htfZones)
                {
                    if (z.Broken) continue;
                    double dist = DistanceToZone(close, z);
                    if (z.Type == "Supply" && dist < htfSupplyDist) { htfSupplyDist = dist; htfSupply = z; }
                    if (z.Type == "Demand" && dist < htfDemandDist) { htfDemandDist = dist; htfDemand = z; }
                }
                string htfSupTxt = htfSupply != null
                    ? string.Format("{0:F0} pips [{1}]", ToPips(htfSupplyDist), "HTF") : "n/a";
                string htfDemTxt = htfDemand != null
                    ? string.Format("{0:F0} pips [{1}]", ToPips(htfDemandDist), "HTF") : "n/a";
                panelText += string.Format("\nHTF ↑ {0} | HTF ↓ {1}", htfSupTxt, htfDemTxt);
            }

            if (EnableDelta && ShowDeltaOnDashboard)
                panelText += string.Format("\nΔ bar {0:F0} | cum {1:F0}", _prevBarDelta, _cumulativeDelta);

            Draw.TextFixed(this, ProximityPanelTag, panelText, TextPosition.TopRight,
                Brushes.Red, _labelFont, Brushes.Black, Brushes.White, 100);
        }

        // ====================================================================
        // LIQUIDITY SWEEP — wick through most recent swing + close reclaim (stop run)
        // ====================================================================
        private void ProcessLiquiditySweeps(double curATR)
        {
            if (_swingHighs == null || CurrentBar < SweepSwingLen * 2 + 2)
                return;

            if (IsPivotHigh(SweepSwingLen, 0))
            {
                _swingHighs.Add(High[SweepSwingLen]);
                _swingHighBars.Add(CurrentBar - SweepSwingLen);
            }
            if (IsPivotLow(SweepSwingLen, 0))
            {
                _swingLows.Add(Low[SweepSwingLen]);
                _swingLowBars.Add(CurrentBar - SweepSwingLen);
            }

            int minBar = CurrentBar - SweepLookback;
            while (_swingHighBars.Count > 0 && _swingHighBars[0] < minBar)
            {
                if (_swingHighBars[0] == _lastSweptSwingHighBar)
                    _lastSweptSwingHighBar = -1;
                _swingHighBars.RemoveAt(0);
                _swingHighs.RemoveAt(0);
            }
            while (_swingLowBars.Count > 0 && _swingLowBars[0] < minBar)
            {
                if (_swingLowBars[0] == _lastSweptSwingLowBar)
                    _lastSweptSwingLowBar = -1;
                _swingLowBars.RemoveAt(0);
                _swingLows.RemoveAt(0);
            }

            double confirmBand = curATR * SweepConfirmationATR;
            bool   sweepOccurred = false;
            string sweepType     = "";
            double sweepPrice    = 0;

            // Sell sweep: only the latest swing high — must trade above it then close back below
            if (_swingHighs.Count > 0)
            {
                int    hiIdx     = _swingHighs.Count - 1;
                double swingHigh = _swingHighs[hiIdx];
                int    swingBar  = _swingHighBars[hiIdx];
                if (swingBar != _lastSweptSwingHighBar
                    && CurrentBar > swingBar
                    && High[0] > swingHigh
                    && CloseReclaimedBelow(swingHigh, confirmBand))
                {
                    sweepOccurred = true;
                    sweepType     = "Sell Sweep (stop run)";
                    sweepPrice    = swingHigh;
                    _lastSweptSwingHighBar = swingBar;
                }
            }

            // Buy sweep: only the latest swing low — must trade below it then close back above
            if (!sweepOccurred && _swingLows.Count > 0)
            {
                int    loIdx     = _swingLows.Count - 1;
                double swingLow  = _swingLows[loIdx];
                int    swingBar  = _swingLowBars[loIdx];
                if (swingBar != _lastSweptSwingLowBar
                    && CurrentBar > swingBar
                    && Low[0] < swingLow
                    && CloseReclaimedAbove(swingLow, confirmBand))
                {
                    sweepOccurred = true;
                    sweepType     = "Buy Sweep (stop run)";
                    sweepPrice    = swingLow;
                    _lastSweptSwingLowBar = swingBar;
                }
            }

            if (!sweepOccurred)
                return;

            // Draw sweeps in realtime only — avoids cyan/orange arrows across full history on reload
            if (State != State.Realtime)
                return;

            string sweepTag = "Sweep_" + CurrentBar;
            if (sweepType.Contains("Sell"))
                Draw.ArrowDown(this, sweepTag, false, 0, High[0] + TickSize * 2, Brushes.Orange);
            else
                Draw.ArrowUp(this, sweepTag, false, 0, Low[0] + TickSize * 2, Brushes.Cyan);

            if (AlertOnSweep && State == State.Realtime)
                Alert("SweepAlert_" + CurrentBar, Priority.Medium,
                    Instrument.FullName + " — " + sweepType + " at " + sweepPrice.ToString("F2"),
                    NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert2.wav",
                    10, Brushes.White, Brushes.Black);
        }

        /// <summary>Close finished back below swing high after wick ran stops above.</summary>
        private bool CloseReclaimedBelow(double swingHigh, double confirmBand)
        {
            if (Close[0] < swingHigh)
                return true;
            if (confirmBand <= 0)
                return false;
            double distBelow = swingHigh - Close[0];
            return distBelow >= 0 && distBelow < confirmBand;
        }

        /// <summary>Close finished back above swing low after wick ran stops below.</summary>
        private bool CloseReclaimedAbove(double swingLow, double confirmBand)
        {
            if (Close[0] > swingLow)
                return true;
            if (confirmBand <= 0)
                return false;
            double distAbove = Close[0] - swingLow;
            return distAbove >= 0 && distAbove < confirmBand;
        }

        // ====================================================================
        // ORDER FLOW — tick delta via OnMarketData
        // ====================================================================
        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (!EnableDelta)
                return;
            if (e.MarketDataType != MarketDataType.Last)
                return;

            double ask = e.Ask;
            double bid = e.Bid;
            if (ask <= 0)
                ask = GetCurrentAsk();
            if (bid <= 0)
                bid = GetCurrentBid();

            bool isBuy;
            if (ask > 0 && e.Price >= ask)
                isBuy = true;
            else if (bid > 0 && e.Price <= bid)
                isBuy = false;
            else
                return;

            if (isBuy)
                _currentBarDelta += e.Volume;
            else
                _currentBarDelta -= e.Volume;
        }

        private void FinalizeBarDelta()
        {
            if (!EnableDelta || _deltaSeries == null)
                return;

            if (CurrentBar > 0)
                _deltaSeries[0] = _currentBarDelta;

            _prevBarDelta     = _currentBarDelta;
            _cumulativeDelta += _currentBarDelta;
            _currentBarDelta  = 0;
        }

        private void UpdateDeltaSwingPivots()
        {
            if (_deltaSeries == null || CurrentBar < SweepSwingLen * 2 + 2)
                return;

            int len = SweepSwingLen;
            if (IsPivotHigh(len, 0))
            {
                _prevSwingHighPrice     = _lastSwingHighPrice;
                _deltaAtPrevSwingHigh   = _deltaAtLastSwingHigh;
                _lastSwingHighPrice     = High[len];
                _deltaAtLastSwingHigh   = _deltaSeries[len];
            }
            if (IsPivotLow(len, 0))
            {
                _prevSwingLowPrice      = _lastSwingLowPrice;
                _deltaAtPrevSwingLow    = _deltaAtLastSwingLow;
                _lastSwingLowPrice      = Low[len];
                _deltaAtLastSwingLow    = _deltaSeries[len];
            }
        }

        private bool PassesDeltaVolumeFilters()
        {
            double barVol = Volume[0];
            if (barVol < DeltaMinVolume)
                return false;
            if (barVol <= 0)
                return false;
            double deltaRatio = Math.Abs(_prevBarDelta) / barVol;
            return Math.Abs(_prevBarDelta) > DeltaThreshold && deltaRatio >= DeltaDominanceRatio;
        }

        private bool HasDeltaPriceDivergence(Zone z)
        {
            if (!RequireDeltaPriceDivergence)
                return true;

            if (z.Type == "Demand")
            {
                if (_lastSwingLowPrice <= 0)
                    return false;
                bool priceLL = Low[0] < _lastSwingLowPrice;
                bool deltaHL = _prevBarDelta > _deltaAtLastSwingLow;
                bool twoSwingBull = _prevSwingLowPrice > 0
                    && _lastSwingLowPrice < _prevSwingLowPrice
                    && _deltaAtLastSwingLow > _deltaAtPrevSwingLow;
                return (priceLL && deltaHL) || twoSwingBull;
            }

            if (_lastSwingHighPrice <= 0)
                return false;
            bool priceHH = High[0] > _lastSwingHighPrice;
            bool deltaLH = _prevBarDelta < _deltaAtLastSwingHigh;
            bool twoSwingBear = _prevSwingHighPrice > 0
                && _lastSwingHighPrice > _prevSwingHighPrice
                && _deltaAtLastSwingHigh < _deltaAtPrevSwingHigh;
            return (priceHH && deltaLH) || twoSwingBear;
        }

        private bool PassesDeltaZoneRejection(Zone z)
        {
            if (!RequireDeltaZoneRejection)
                return true;

            if (z.Type == "Demand")
            {
                bool touchedBottom = Low[0] <= z.Bottom;
                bool closedHigher  = Close[0] > Low[0];
                bool bullishDelta  = _prevBarDelta > DeltaThreshold;
                return touchedBottom && closedHigher && bullishDelta;
            }

            bool touchedTop     = High[0] >= z.Top;
            bool closedLower    = Close[0] < High[0];
            bool bearishDelta   = _prevBarDelta < -DeltaThreshold;
            return touchedTop && closedLower && bearishDelta;
        }

        private bool DeltaDirectionMatchesZone(Zone z)
        {
            return z.Type == "Demand" ? _prevBarDelta > 0 : _prevBarDelta < 0;
        }

        private void ProcessDeltaAtZone(Zone z)
        {
            if (!EnableDelta || !ShowDeltaDivergence)
                return;
            if (z.DeltaDivergenceDrawnThisTouch)
                return;

            if (!PassesDeltaVolumeFilters())
            {
                z.DeltaConsecutiveCount = 0;
                return;
            }

            if (!DeltaDirectionMatchesZone(z))
            {
                z.DeltaConsecutiveCount = 0;
                return;
            }

            if (!HasDeltaPriceDivergence(z) || !PassesDeltaZoneRejection(z))
            {
                z.DeltaConsecutiveCount = 0;
                return;
            }

            if (RequireDeltaConsecutiveBars)
            {
                z.DeltaConsecutiveCount++;
                if (z.DeltaConsecutiveCount < DeltaConsecutiveBarsRequired)
                    return;
            }

            z.DeltaDivergenceDrawnThisTouch = true;
            DrawDeltaMarkerAtZone(z);
        }

        private void DrawDeltaMarkerAtZone(Zone z)
        {
            bool bullish = _prevBarDelta > 0;
            string divTag = "DeltaDot_" + z.BoxTag + "_" + CurrentBar;
            double y      = bullish ? Low[0] - TickSize * 2 : High[0] + TickSize * 2;
            Brush dotBrush = bullish ? Brushes.LimeGreen : Brushes.Red;
            Draw.Dot(this, divTag, false, 0, y, dotBrush);
        }

        // ====================================================================
        // MULTI-TIMEFRAME — HTF detection on secondary series, draw on chart TF
        // ====================================================================
        private bool IsHtfHigherThanChart()
        {
            BarsPeriod primary = BarsPeriod;
            BarsPeriod htf     = BarsArray[HtfSeriesIndex].BarsPeriod;
            if ((int)htf.BarsPeriodType > (int)primary.BarsPeriodType)
                return true;
            if (htf.BarsPeriodType == primary.BarsPeriodType)
                return htf.Value > primary.Value;
            return false;
        }

        private void ProcessHtfBarUpdate()
        {
            int bip = HtfSeriesIndex;
            int cb  = CurrentBars[bip];
            if (cb < SwingLen * 2 + 2) return;

            double curATR = _htfAtr[0];

            if (IsPivotHigh(SwingLen, bip))
            {
                string sess = GetSession(Times[bip][0]);
                if (sess != "Other")
                {
                    double top = Highs[bip][SwingLen];
                    double bot = Lows[bip][SwingLen];
                    if (NormalizeHeight(ref top, ref bot, curATR)
                        && !TryMerge(top, bot, "Supply", curATR, _htfZones))
                        CreateZone(top, bot, "Supply", sess, cb - SwingLen, _htfZones, true, bip, _htfAtr);
                }
            }

            if (IsPivotLow(SwingLen, bip))
            {
                string sess = GetSession(Times[bip][0]);
                if (sess != "Other")
                {
                    double top = Highs[bip][SwingLen];
                    double bot = Lows[bip][SwingLen];
                    if (NormalizeHeight(ref top, ref bot, curATR)
                        && !TryMerge(top, bot, "Demand", curATR, _htfZones))
                        CreateZone(top, bot, "Demand", sess, cb - SwingLen, _htfZones, true, bip, _htfAtr);
                }
            }

            PruneZones(_htfZones, cb, HtfMaxZones);
            UpdateHtfZoneStates(bip, cb);
        }

        private void UpdateHtfZoneStates(int bip, int cb)
        {
            foreach (Zone z in _htfZones)
            {
                if (!z.Broken)
                {
                    bool inZone = Highs[bip][0] >= z.Bottom && Lows[bip][0] <= z.Top;
                    if (inZone && !z.RetestCounted) { z.Retests++; z.RetestCounted = true; }
                    else if (!inZone) z.RetestCounted = false;
                }

                if (!z.Broken)
                {
                    if (z.Type == "Supply")
                    {
                        double bl = (InvalidationMethod == "Close") ? Closes[bip][0] : Highs[bip][0];
                        if (bl > z.Top) z.Broken = true;
                    }
                    else
                    {
                        double bl = (InvalidationMethod == "Close") ? Closes[bip][0] : Lows[bip][0];
                        if (bl < z.Bottom) z.Broken = true;
                    }
                }
            }
        }

        private void RenderHtfZonesOnChart()
        {
            if (!EnableHtfOverlay || !_htfSeriesReady || _htfZones == null) return;

            int cbHtf = CurrentBars[HtfSeriesIndex];
            int activeSupCount = 0, activeDeCount = 0;
            int histSupCount   = 0, histDeCount   = 0;

            foreach (Zone z in _htfZones)
            {
                int    age    = cbHtf - z.StartBar;
                double height = z.Top - z.Bottom;
                bool   hide   = false;

                if (z.Broken && !ShowHistoric)         hide = true;
                if (z.Broken && age > HistoricBars)    hide = true;
                if (!z.Broken && age > ActiveLookback) hide = true;

                if (z.Broken && ShowHistoric && !hide)
                {
                    if (height < z.ATRatCreation * MinZoneHeightATR) hide = true;
                    if (!hide)
                    {
                        if (z.Type == "Supply") { if (histSupCount >= HtfMaxZones) hide = true; else histSupCount++; }
                        else                    { if (histDeCount  >= HtfMaxZones) hide = true; else histDeCount++;  }
                    }
                }

                if (!z.Broken && !hide)
                {
                    if (z.Type == "Supply") { if (activeSupCount >= HtfMaxZones) hide = true; else activeSupCount++; }
                    else                    { if (activeDeCount  >= HtfMaxZones) hide = true; else activeDeCount++;  }
                }

                DrawHtfZone(z, hide, age);
            }
        }

        private void DrawHtfZone(Zone z, bool hide, int age)
        {
            if (hide)
            {
                if (z.DrawnOnChart)
                {
                    RemoveZoneVisualDrawObjects(z);
                    z.DrawnOnChart = false;
                }
                return;
            }

            Brush fill    = z.Broken ? HistoricBrush : (z.Type == "Supply" ? HtfSupplyBrush : HtfDemandBrush);
            int   opacity = z.Broken ? 10 : 12;
            int   leftBars  = GetPrimaryBarsAgo(z.StartTime);

            Draw.Rectangle(this, z.BoxTag, false, leftBars, z.Top, 0, z.Bottom,
                           _transparent, fill, opacity);

            if (ShowHtfLabels)
            {
                double hPips = ToPips(z.Top - z.Bottom);
                double dPips = z.Type == "Supply" ? ToPips(z.Bottom - Close[0]) : ToPips(Close[0] - z.Top);
                int    str   = CalcStrength(z.Retests, age);
                double mid   = (z.Top + z.Bottom) / 2.0;
                Brush  tBrush = z.Broken ? Brushes.Gray : (z.Type == "Supply" ? Brushes.IndianRed : Brushes.DarkSeaGreen);

                string periodLabel = string.Format("{0} {1}", HtfBarsPeriodValue,
                    HtfBarsPeriodType.ToString().ToLowerInvariant());

                string txt = string.Format(
                    "HTF {0}  |  {1:F0} pips  |  {2}  |  Str {3}/10  |  {4}  |  {5:F0} pips away",
                    periodLabel, hPips, z.Type, str, z.Session, Math.Abs(dPips));

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

            if (ShowRetestMarkers && z.Retests > 0)
            {
                int retBars = Math.Max(0, leftBars - 3);
                if (z.Type == "Supply")
                    Draw.ArrowDown(this, z.RetestTag, false, retBars, z.Top, RetestBearBrush);
                else
                    Draw.ArrowUp(this, z.RetestTag, false, retBars, z.Bottom, RetestBullBrush);
            }
            else if (z.Retests == 0 && z.DrawnOnChart)
                RemoveDrawObject(z.RetestTag);

            z.DrawnOnChart = true;
        }

        private void HideAllHtfZoneDrawings()
        {
            if (_htfZones == null) return;
            foreach (Zone z in _htfZones)
            {
                if (z.DrawnOnChart)
                {
                    RemoveZoneVisualDrawObjects(z);
                    z.DrawnOnChart = false;
                }
            }
        }

        private Zone FindContainingHtfZone(Zone ltf)
        {
            if (_htfZones == null) return null;
            foreach (Zone h in _htfZones)
            {
                if (h.Broken) continue;
                if (ltf.Top <= h.Top && ltf.Bottom >= h.Bottom)
                    return h;
            }
            return null;
        }

        /// <summary>Maps an HTF zone start time to barsAgo on the chart (primary) series.</summary>
        private int GetPrimaryBarsAgo(DateTime t)
        {
            int barIdx = Bars.GetBar(t);
            if (barIdx < 0)
                return 0;
            return Math.Max(0, CurrentBar - barIdx);
        }

        private static double DistanceToZone(double price, Zone z)
        {
            if (z.Type == "Supply")
            {
                if (price < z.Bottom) return z.Bottom - price;
                if (price > z.Top)    return price - z.Top;
                return 0;
            }
            if (price > z.Top)    return price - z.Top;
            if (price < z.Bottom) return z.Bottom - price;
            return 0;
        }

        // ====================================================================
        // ABSORPTION — setup bar + optional next-bar confirm (no retroactive draw)
        // ====================================================================
        private void ProcessZoneAbsorption(Zone z, bool highVolume, bool bullishShift, bool bearishShift)
        {
            if (State != State.Realtime)
                return;

            int zoneAge = CurrentBar - z.StartBar;
            int str     = CalcStrength(z.Retests, zoneAge);
            bool strengthOk = MinZoneStrengthForAbsorption <= 0 || str >= MinZoneStrengthForAbsorption;
            bool closeInZone = Close[0] >= z.Bottom && Close[0] <= z.Top;
            bool closeOk = !RequireCloseInZoneForAbsorption || closeInZone;

            ExpireAbsorptionPending(z);

            // Confirm bar: still in zone + quality filters only (no second volume spike / wick bar)
            if (RequireConsecutiveAbsorptionBars && strengthOk && closeOk && z.Retests <= 2)
            {
                if (z.Type == "Demand"
                    && z.AbsorptionBuyPending
                    && CurrentBar == z.AbsorptionBuyPendingBar + 1
                    && !z.AbsorptionFiredThisTouchBuy)
                {
                    z.AbsorptionBuyPending = false;
                    z.AbsorptionFiredThisTouchBuy = true;
                    DrawAbsorptionArrow(z, true);
                    return;
                }
                if (z.Type == "Supply"
                    && z.AbsorptionSellPending
                    && CurrentBar == z.AbsorptionSellPendingBar + 1
                    && !z.AbsorptionFiredThisTouchSell)
                {
                    z.AbsorptionSellPending = false;
                    z.AbsorptionFiredThisTouchSell = true;
                    DrawAbsorptionArrow(z, false);
                    return;
                }
            }

            // Setup / trigger bar: volume + wick rejection + momentum
            if (!highVolume || z.Retests > 2 || !strengthOk || !closeOk)
                return;

            double body      = Math.Abs(Close[0] - Open[0]);
            double upperWick = High[0] - Math.Max(Open[0], Close[0]);
            double lowerWick = Math.Min(Open[0], Close[0]) - Low[0];

            if (z.Type == "Demand")
            {
                bool rawBuy = bullishShift && lowerWick > body;
                if (!rawBuy || z.AbsorptionFiredThisTouchBuy)
                    return;

                if (RequireConsecutiveAbsorptionBars)
                {
                    z.AbsorptionBuyPending    = true;
                    z.AbsorptionBuyPendingBar = CurrentBar;
                }
                else
                {
                    z.AbsorptionFiredThisTouchBuy = true;
                    DrawAbsorptionArrow(z, true);
                }
            }
            else if (z.Type == "Supply")
            {
                bool rawSell = bearishShift && upperWick > body;
                if (!rawSell || z.AbsorptionFiredThisTouchSell)
                    return;

                if (RequireConsecutiveAbsorptionBars)
                {
                    z.AbsorptionSellPending    = true;
                    z.AbsorptionSellPendingBar = CurrentBar;
                }
                else
                {
                    z.AbsorptionFiredThisTouchSell = true;
                    DrawAbsorptionArrow(z, false);
                }
            }
        }

        private void ExpireAbsorptionPending(Zone z)
        {
            if (z.AbsorptionBuyPending && CurrentBar > z.AbsorptionBuyPendingBar + 1)
                z.AbsorptionBuyPending = false;
            if (z.AbsorptionSellPending && CurrentBar > z.AbsorptionSellPendingBar + 1)
                z.AbsorptionSellPending = false;
        }

        /// <summary>Realtime only; barsAgo 0 on the current bar — never backdated.</summary>
        private void DrawAbsorptionArrow(Zone z, bool isBuy)
        {
            if (State != State.Realtime)
                return;

            if (isBuy)
            {
                if (!string.IsNullOrEmpty(z.LastBuyArrowTag))
                    RemoveDrawObject(z.LastBuyArrowTag);
                string buyTag = "BUY_" + z.BoxTag + "_" + CurrentBar;
                z.LastBuyArrowTag = buyTag;
                Draw.ArrowUp(this, buyTag, false, 0, Low[0] - TickSize * 2, Brushes.Lime);
                if (AbsorptionAlertsOn && State == State.Realtime)
                    Alert("AbsorptionBuy_" + z.BoxTag + "_" + CurrentBar, Priority.Medium,
                        Instrument.FullName + " — BUY Signal (absorption)",
                        NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert1.wav",
                        10, Brushes.Lime, Brushes.Black);
            }
            else
            {
                if (!string.IsNullOrEmpty(z.LastSellArrowTag))
                    RemoveDrawObject(z.LastSellArrowTag);
                string sellTag = "SELL_" + z.BoxTag + "_" + CurrentBar;
                z.LastSellArrowTag = sellTag;
                Draw.ArrowDown(this, sellTag, false, 0, High[0] + TickSize * 2, Brushes.Red);
                if (AbsorptionAlertsOn && State == State.Realtime)
                    Alert("AbsorptionSell_" + z.BoxTag + "_" + CurrentBar, Priority.Medium,
                        Instrument.FullName + " — SELL Signal (absorption)",
                        NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert1.wav",
                        10, Brushes.Red, Brushes.White);
            }
        }

        // ====================================================================
        // HELPERS
        // ====================================================================

        // Session labels from hour-of-day; ranges do not overlap (see FIX 3 header).
        // t is NinjaTrader session time for the chart series (instrument/exchange).
        private string GetSession(DateTime t)
        {
            int h = t.Hour;
            if (h >= 0  && h < 8)  return "Asian";
            if (h >= 8  && h < 13) return "London";
            if (h >= 13 && h < 22) return "New York";
            return "Other";
        }

        // Mirrors Pine f_getPips() — detects instrument type by name, no InstrumentType enum needed
        private double GetPipSize()
        {
            string name = Instrument.MasterInstrument.Name.ToUpper();
            // Gold futures / spot
            if (name.Contains("GC") || name.Contains("XAU") || name.Contains("GOLD"))
                return 0.1;

            // JPY forex pairs
            if (name.Contains("JPY"))
                return 0.01;

            // Default forex
            return 0.0001;
        }

        // Mirrors Pine f_toPips()
        private double ToPips(double val) => _pipSize > 0 ? Math.Abs(val) / _pipSize : Math.Abs(val);

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
