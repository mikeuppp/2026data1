#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.SuperDom;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

//This namespace holds Indicators in this folder and is required. Do not change it. 
namespace NinjaTrader.NinjaScript.Indicators
{
	/// <summary>
	/// Session Volume Profile - Analyzes price & volume during regular trading hours to provide a session volume profile analysis.
	/// Converted from PineScript (https://www.tradingview.com/script/5ubxXb8z-SessionVolumeProfile/)
	/// </summary>
	public class SessionVolumeProfile : Indicator
	{
		#region Variables
		
		// Configuration Settings (from PineScript Object type)
		private int numberOfRows = 24;
		private int valueAreaCoverage = 70;
		private bool trackDevelopingVA = false;
		
		// Values we are searching for
		private double valueAreaHigh = double.NaN;
		private double pointOfControl = double.NaN;
		private double valueAreaLow = double.NaN;
		private DateTime startTime = DateTime.MinValue;
		private DateTime endTime = DateTime.MinValue;
		
		// Private properties for tracking things
		private double dayHigh = double.NaN;
		private double dayLow = double.NaN;
		private double step = double.NaN;
		private int pointOfControlLevel = -1;
		private int valueAreaHighLevel = -1;
		private int valueAreaLowLevel = -1;
		private DateTime lastTime = DateTime.MinValue;
		
		// Collections to track volume rows and session data
		private Dictionary<int, double> volumeRows = new Dictionary<int, double>();
		private List<double> sessionHighs = new List<double>();
		private List<double> sessionLows = new List<double>();
		private List<double> sessionVolumes = new List<double>();
		private List<DateTime> sessionTimes = new List<DateTime>();
		
		// Previous day values for drawing
		private double yesterdayVAH = double.NaN;
		private double yesterdayVAL = double.NaN;
		private double yesterdayPOC = double.NaN;
		private DateTime yesterdayStartTime = DateTime.MinValue;
		private DateTime yesterdayEndTime = DateTime.MinValue;
		
		// Current session tracking
		private DateTime currentSessionDate = DateTime.MinValue;
		private bool wasInSession = false;
		
		// Drawing objects tracking
		private Dictionary<string, DrawingTool> historicalDrawings = new Dictionary<string, DrawingTool>();
		
		// Appearance settings
		private bool extendYesterdayOverToday = true;
		private bool showValueArea = true;
		private bool showHistogram = true;
		private bool showLabels = true;
		private bool showVolumeOnHistogram = false;
		private bool showDevelopingValueArea = false;
		
		#endregion
		
		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description									= @"Session Volume Profile - Analyzes price & volume during regular trading hours";
				Name										= "SessionVolumeProfile";
				Calculate									= Calculate.OnBarClose;
				IsOverlay									= true;
				DisplayInDataBox							= true;
				DrawOnPricePanel							= true;
				DrawHorizontalGridLines						= true;
				DrawVerticalGridLines						= true;
				PaintPriceMarkers							= true;
				ScaleJustification							= NinjaTrader.Gui.Chart.ScaleJustification.Right;
				//Disable this property if your indicator requires custom values that cumulate with each new market data event. 
				//See Help Guide for additional information.
				IsSuspendedWhileInactive					= true;
				
				// General Settings (mapped from PineScript)
				NumberOfRows								= 24;
				ValueAreaCoverage							= 70;
				
				// Appearance Settings
				ExtendYesterdayOverToday					= true;
				ShowValueArea								= true;
				VAColor										= Brushes.DarkCyan;
				VAOpacity									= 20;
				POCColor									= Brushes.Magenta;
				POCWidth									= 1;
				POCDashStyle								= DashStyleHelper.Solid;
				VAHLColor									= Brushes.DodgerBlue;
				VAHLWidth									= 1;
				VAHLDashStyle								= DashStyleHelper.Solid;
				ShowLabels									= true;
				ShowHistogram								= true;
				HistogramColor								= Brushes.DodgerBlue;
				ShowVolumeOnHistogram						= false;
				ShowDevelopingValueArea						= false;
				DevelopingVAColor							= Brushes.Fuchsia;
				
				// Developing VA plots
				AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "DevVAH");
				AddPlot(new Stroke(Brushes.Transparent, 2), PlotStyle.Line, "DevPOC");
				AddPlot(new Stroke(Brushes.Transparent, 1), PlotStyle.Line, "DevVAL");
			}
			else if (State == State.Configure)
			{
				numberOfRows = NumberOfRows;
				valueAreaCoverage = ValueAreaCoverage;
				trackDevelopingVA = ShowDevelopingValueArea;
				extendYesterdayOverToday = ExtendYesterdayOverToday;
				showValueArea = ShowValueArea;
				showHistogram = ShowHistogram;
				showLabels = ShowLabels;
				showVolumeOnHistogram = ShowVolumeOnHistogram;
				showDevelopingValueArea = ShowDevelopingValueArea;
			}
			else if (State == State.DataLoaded)
			{
				// Initialize collections
				ClearSessionData();
			}
		}

		protected override void OnBarUpdate()
		{
			if (CurrentBar < 1)
				return;
			
			// Check if we're in a regular trading session
			bool isInSession = IsInRegularSession();
			
			// Detect new session
			bool isNewSession = DetectNewSession(isInSession);
			
			if (isNewSession)
			{
				// Process completed session
				ProcessCompletedSession();
				
				// Reset for new session
				ResetSessionData();
				startTime = Time[0];
			}
			
			// Collect data during session
			if (isInSession)
			{
				sessionHighs.Add(High[0]);
				sessionLows.Add(Low[0]);
				sessionVolumes.Add(Volume[0]);
				sessionTimes.Add(Time[0]);
				endTime = Time[0];
			}
			
			// Calculate volume profile (developing or on session end)
			if (isInSession && (trackDevelopingVA || IsLastBarOfSession()))
			{
				CalculateVolumeProfile();
				
				// Update developing VA plots
				if (showDevelopingValueArea)
				{
					Values[0][0] = valueAreaHigh;
					Values[1][0] = pointOfControl;
					Values[2][0] = valueAreaLow;
				}
			}
			
			wasInSession = isInSession;
		}
		
		#region Helper Methods
		
		/// <summary>
		/// Determines if current bar is in regular trading session
		/// Maps to PineScript Session.ismarket() function
		/// </summary>
		private bool IsInRegularSession()
		{
			// For stocks/equity: 9:30 AM - 4:00 PM ET
			// For futures: Depends on instrument
			// This is a simplified version - customize based on your needs
			
			TimeSpan currentTime = Time[0].TimeOfDay;
			
			// Example for US equity market hours (adjust as needed)
			TimeSpan marketOpen = new TimeSpan(9, 30, 0);
			TimeSpan marketClose = new TimeSpan(16, 0, 0);
			
			return currentTime >= marketOpen && currentTime <= marketClose;
		}
		
		/// <summary>
		/// Detects if a new session has started
		/// Maps to PineScript session detection logic
		/// </summary>
		private bool DetectNewSession(bool isInSession)
		{
			if (CurrentBar < 1)
				return false;
			
			DateTime currentDate = Time[0].Date;
			
			// New session when:
			// 1. Day changes AND we're in session
			// 2. We enter session after being out of session (if tracking extended hours)
			bool dayChanged = currentDate != currentSessionDate;
			bool enteringSession = isInSession && !wasInSession;
			
			if (dayChanged && isInSession)
			{
				currentSessionDate = currentDate;
				return true;
			}
			
			if (enteringSession)
			{
				currentSessionDate = currentDate;
				return true;
			}
			
			return false;
		}
		
		/// <summary>
		/// Checks if this is the last bar of the session
		/// </summary>
		private bool IsLastBarOfSession()
		{
			// This would need to be enhanced based on your specific session detection needs
			// For now, return false to process only on trackDevelopingVA
			return false;
		}
		
		/// <summary>
		/// Resets session data for new session
		/// </summary>
		private void ResetSessionData()
		{
			ClearSessionData();
			valueAreaHigh = double.NaN;
			pointOfControl = double.NaN;
			valueAreaLow = double.NaN;
		}
		
		/// <summary>
		/// Clears session data collections
		/// </summary>
		private void ClearSessionData()
		{
			sessionHighs.Clear();
			sessionLows.Clear();
			sessionVolumes.Clear();
			sessionTimes.Clear();
			volumeRows.Clear();
		}
		
		/// <summary>
		/// Processes completed session and stores values for drawing
		/// </summary>
		private void ProcessCompletedSession()
		{
			if (sessionHighs.Count == 0)
				return;
			
			// Store yesterday's values
			yesterdayVAH = valueAreaHigh;
			yesterdayVAL = valueAreaLow;
			yesterdayPOC = pointOfControl;
			yesterdayStartTime = startTime;
			yesterdayEndTime = endTime;
		}
		
		/// <summary>
		/// Main volume profile calculation
		/// Maps to PineScript get() function
		/// </summary>
		private void CalculateVolumeProfile()
		{
			if (sessionHighs.Count == 0)
				return;
			
			// Calculate day high/low
			dayHigh = sessionHighs.Max();
			dayLow = sessionLows.Min();
			
			// Calculate step size for rows
			step = (dayHigh - dayLow) / numberOfRows;
			
			if (step <= 0)
				return;
			
			// Clear volume rows
			volumeRows.Clear();
			Dictionary<int, double> priceLevelRows = new Dictionary<int, double>();
			
			int pocLevel = 0;
			double highestVol = 0;
			
			// Build volume profile by levels
			// Maps to PineScript loop: for priceLevel = volumeProfile.dayLow to volumeProfile.dayHigh by volumeProfile.step
			for (int level = 0; level < numberOfRows; level++)
			{
				double priceLevel = dayLow + (level * step);
				priceLevelRows[level] = priceLevel;
				
				double levelVol = 0;
				
				// Sum volume for all candles that intersect this price level
				for (int i = 0; i < sessionHighs.Count; i++)
				{
					double h = sessionHighs[i];
					double l = sessionLows[i];
					double v = sessionVolumes[i];
					
					if (h >= priceLevel && l < (priceLevel + step))
					{
						levelVol += GetVolumeForRow(priceLevel + step, priceLevel, h, l, v);
					}
				}
				
				volumeRows[level] = levelVol;
				
				// Track POC (highest volume level)
				if (levelVol > highestVol)
				{
					highestVol = levelVol;
					pocLevel = level;
				}
			}
			
			// Calculate Value Area
			// Maps to PineScript value area calculation logic
			CalculateValueArea(pocLevel, priceLevelRows);
		}
		
		/// <summary>
		/// Calculates volume distribution for a row based on candle intersection
		/// Maps to PineScript getVolumeForRow() function
		/// </summary>
		private double GetVolumeForRow(double rowHigh, double rowLow, double candleHigh, double candleLow, double candleVolume)
		{
			double rowRange = rowHigh - rowLow;
			double candleRange = candleHigh - candleLow;
			double pricePortion = 0;
			
			if (candleHigh > rowHigh && candleLow < rowLow)
			{
				// The candle engulfed the row
				pricePortion = rowRange;
			}
			else if (candleHigh <= rowHigh && candleLow >= rowLow)
			{
				// The candle is completely within the row
				pricePortion = candleRange;
			}
			else if (candleHigh > rowHigh && candleLow >= rowLow)
			{
				// Top of candle is above row, bottom is in row
				pricePortion = rowHigh - candleLow;
			}
			else if (candleHigh <= rowHigh && candleLow < rowLow)
			{
				// Top of candle is in row, bottom is below row
				pricePortion = candleHigh - rowLow;
			}
			
			// Return portion of volume from candle relative to price intersection
			if (candleRange > 0)
				return (pricePortion * candleVolume) / candleRange;
			else
				return 0;
		}
		
		/// <summary>
		/// Calculates the Value Area High and Low based on volume distribution
		/// Maps to PineScript value area calculation
		/// </summary>
		private void CalculateValueArea(int pocLevel, Dictionary<int, double> priceLevelRows)
		{
			double valueAreaCoveragePercent = valueAreaCoverage / 100.0;
			double totalVol = volumeRows.Values.Sum();
			double valueAreaVol = totalVol * valueAreaCoveragePercent;
			
			int vahLevel = pocLevel;
			int valLevel = pocLevel;
			
			double valueAreaTracking = volumeRows.ContainsKey(pocLevel) ? volumeRows[pocLevel] : 0;
			
			int loopCount = 0;
			
			// Expand value area up and down from POC
			// Maps to PineScript: while valueAreaTracking < valueAreaVol
			while (valueAreaTracking < valueAreaVol && loopCount <= numberOfRows)
			{
				loopCount++;
				
				// Break if we've exceeded bounds
				if (valLevel <= 0 && vahLevel >= numberOfRows - 1)
					break;
				
				double volumeAbovePoc = (vahLevel >= numberOfRows - 1) ? 0 : 
					(volumeRows.ContainsKey(vahLevel + 1) ? volumeRows[vahLevel + 1] : 0);
				double volumeBelowPoc = (valLevel <= 0) ? 0 : 
					(volumeRows.ContainsKey(valLevel - 1) ? volumeRows[valLevel - 1] : 0);
				
				// Expand in direction of higher volume
				if (volumeAbovePoc >= volumeBelowPoc)
				{
					valueAreaTracking += volumeAbovePoc;
					vahLevel++;
				}
				else
				{
					valueAreaTracking += volumeBelowPoc;
					valLevel--;
				}
			}
			
			// Constrain to bounds
			if (valLevel < 0)
				valLevel = 0;
			if (vahLevel >= numberOfRows - 1)
				vahLevel = numberOfRows - 1;
			
			// Set final values
			pointOfControlLevel = pocLevel;
			valueAreaHighLevel = vahLevel;
			valueAreaLowLevel = valLevel;
			
			if (priceLevelRows.ContainsKey(pocLevel))
				pointOfControl = priceLevelRows[pocLevel] + (step / 2);
			
			if (priceLevelRows.ContainsKey(vahLevel))
				valueAreaHigh = priceLevelRows[vahLevel] + step;
			
			if (priceLevelRows.ContainsKey(valLevel))
				valueAreaLow = priceLevelRows[valLevel];
		}
		
		#endregion
		
		#region OnRender - Drawing Logic
		
		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			base.OnRender(chartControl, chartScale);
			
			// Draw previous day's value area
			DrawPriorValueAreas(chartControl, chartScale);
			
			// Draw histogram
			if (showHistogram && !double.IsNaN(valueAreaHigh))
			{
				DrawHistogram(chartControl, chartScale);
			}
		}
		
		/// <summary>
		/// Draws previous session's value area lines and zones
		/// Maps to PineScript drawPriorValueAreas() function
		/// </summary>
		private void DrawPriorValueAreas(ChartControl chartControl, ChartScale chartScale)
		{
			if (double.IsNaN(yesterdayVAH) || double.IsNaN(yesterdayVAL) || double.IsNaN(yesterdayPOC))
				return;
			
			if (!showValueArea)
				return;
			
			// Draw using NinjaTrader drawing tools
			string tag = "SVP_Yesterday";
			
			// Draw Value Area High line
			Draw.Line(this, tag + "_VAH", false, yesterdayStartTime, yesterdayVAH, 
				extendYesterdayOverToday ? Time[0] : yesterdayEndTime, yesterdayVAH, 
				VAHLColor, VAHLDashStyle, VAHLWidth);
			
			// Draw Value Area Low line
			Draw.Line(this, tag + "_VAL", false, yesterdayStartTime, yesterdayVAL, 
				extendYesterdayOverToday ? Time[0] : yesterdayEndTime, yesterdayVAL, 
				VAHLColor, VAHLDashStyle, VAHLWidth);
			
			// Draw POC line
			Draw.Line(this, tag + "_POC", false, yesterdayStartTime, yesterdayPOC, 
				extendYesterdayOverToday ? Time[0] : yesterdayEndTime, yesterdayPOC, 
				POCColor, POCDashStyle, POCWidth);
			
			// Draw labels if enabled
			if (showLabels)
			{
				double percentVAH = ((yesterdayVAH - Close[0]) / Close[0]) * 100;
				double percentVAL = ((yesterdayVAL - Close[0]) / Close[0]) * 100;
				double percentPOC = ((yesterdayPOC - Close[0]) / Close[0]) * 100;
				
				DateTime labelTime = extendYesterdayOverToday ? Time[0] : yesterdayEndTime;
				
				Draw.Text(this, tag + "_VAH_Label", true, 
					string.Format("{0:F2} VAH ({1:F2}%)", yesterdayVAH, percentVAH), 
					0, yesterdayVAH, 10, VAHLColor, new SimpleFont(), TextAlignment.Left, 
					Brushes.Transparent, Brushes.Transparent, 0);
				
				Draw.Text(this, tag + "_VAL_Label", true, 
					string.Format("{0:F2} VAL ({1:F2}%)", yesterdayVAL, percentVAL), 
					0, yesterdayVAL, 10, VAHLColor, new SimpleFont(), TextAlignment.Left, 
					Brushes.Transparent, Brushes.Transparent, 0);
				
				Draw.Text(this, tag + "_POC_Label", true, 
					string.Format("{0:F2} POC ({1:F2}%)", yesterdayPOC, percentPOC), 
					0, yesterdayPOC, 10, POCColor, new SimpleFont(), TextAlignment.Left, 
					Brushes.Transparent, Brushes.Transparent, 0);
			}
		}
		
		/// <summary>
		/// Draws the volume profile histogram
		/// Maps to PineScript drawHistogram() function
		/// </summary>
		private void DrawHistogram(ChartControl chartControl, ChartScale chartScale)
		{
			if (volumeRows.Count == 0 || double.IsNaN(step))
				return;
			
			double totalVol = volumeRows.Values.Sum();
			if (totalVol <= 0)
				return;
			
			TimeSpan barRange = endTime - startTime;
			double stepSpacer = step * 0.05;
			
			// Draw each histogram bar
			foreach (var kvp in volumeRows)
			{
				int level = kvp.Key;
				double vol = kvp.Value;
				
				double histPrice = dayLow + (level * step);
				double volWidthPercent = vol / totalVol;
				TimeSpan histWidth = TimeSpan.FromTicks((long)(barRange.Ticks * volWidthPercent * 2));
				DateTime histRightTime = startTime + histWidth;
				
				// Determine opacity based on whether it's in value area
				int opacity = (level < valueAreaLowLevel || level > valueAreaHighLevel) ? 70 : 40;
				Brush histBrush = new SolidColorBrush(HistogramColor.Color) { Opacity = opacity / 100.0 };
				histBrush.Freeze();
				
				// Draw histogram bar using rectangle
				string tag = string.Format("SVP_Hist_{0}_{1}", startTime.Ticks, level);
				Draw.Rectangle(this, tag, false, startTime, histPrice + step - stepSpacer, 
					histRightTime, histPrice + stepSpacer, histBrush, histBrush, 0);
				
				// Optionally show volume on histogram
				if (showVolumeOnHistogram)
				{
					Draw.Text(this, tag + "_Text", true, string.Format("{0:F0}", vol), 
						0, histPrice + (step / 2), 0, Brushes.White, new SimpleFont("Arial", 8), 
						TextAlignment.Left, Brushes.Transparent, Brushes.Transparent, 0);
				}
			}
			
			// Draw background box
			Brush bgBrush = new SolidColorBrush(HistogramColor.Color) { Opacity = 0.1 };
			bgBrush.Freeze();
			Draw.Rectangle(this, "SVP_HistBg_" + startTime.Ticks, false, 
				startTime, dayHigh, endTime, dayLow, bgBrush, bgBrush, 0);
		}
		
		#endregion
		
		#region Properties
		
		// General Settings
		[NinjaScriptProperty]
		[Range(5, 100)]
		[Display(Name="Number of Rows", Description="Number of rows for volume profile", Order=1, GroupName="01. General Settings")]
		public int NumberOfRows
		{ get; set; }
		
		[NinjaScriptProperty]
		[Range(1, 100)]
		[Display(Name="Value Area Coverage %", Description="Percentage of volume covered by value area", Order=2, GroupName="01. General Settings")]
		public int ValueAreaCoverage
		{ get; set; }
		
		// Appearance Settings
		[NinjaScriptProperty]
		[Display(Name="Extend Yesterday Over Today", Description="Extend yesterday's VA lines over current day", Order=1, GroupName="02. Appearance")]
		public bool ExtendYesterdayOverToday
		{ get; set; }
		
		[NinjaScriptProperty]
		[Display(Name="Show Value Area", Description="Show value area zone", Order=2, GroupName="02. Appearance")]
		public bool ShowValueArea
		{ get; set; }
		
		[XmlIgnore]
		[Display(Name="Value Area Color", Description="Color for value area zone", Order=3, GroupName="02. Appearance")]
		public Brush VAColor
		{ get; set; }
		
		[Browsable(false)]
		public string VAColorSerializable
		{
			get { return Serialize.BrushToString(VAColor); }
			set { VAColor = Serialize.StringToBrush(value); }
		}
		
		[Range(0, 100)]
		[Display(Name="Value Area Opacity", Description="Opacity of value area zone (0-100)", Order=4, GroupName="02. Appearance")]
		public int VAOpacity
		{ get; set; }
		
		[XmlIgnore]
		[Display(Name="POC Color", Description="Color for Point of Control line", Order=5, GroupName="02. Appearance")]
		public Brush POCColor
		{ get; set; }
		
		[Browsable(false)]
		public string POCColorSerializable
		{
			get { return Serialize.BrushToString(POCColor); }
			set { POCColor = Serialize.StringToBrush(value); }
		}
		
		[Range(1, 6)]
		[Display(Name="POC Width", Description="Width of POC line", Order=6, GroupName="02. Appearance")]
		public int POCWidth
		{ get; set; }
		
		[Display(Name="POC Dash Style", Description="Line style for POC", Order=7, GroupName="02. Appearance")]
		public DashStyleHelper POCDashStyle
		{ get; set; }
		
		[XmlIgnore]
		[Display(Name="VA High/Low Color", Description="Color for Value Area High/Low lines", Order=8, GroupName="02. Appearance")]
		public Brush VAHLColor
		{ get; set; }
		
		[Browsable(false)]
		public string VAHLColorSerializable
		{
			get { return Serialize.BrushToString(VAHLColor); }
			set { VAHLColor = Serialize.StringToBrush(value); }
		}
		
		[Range(1, 6)]
		[Display(Name="VA High/Low Width", Description="Width of VA High/Low lines", Order=9, GroupName="02. Appearance")]
		public int VAHLWidth
		{ get; set; }
		
		[Display(Name="VA High/Low Dash Style", Description="Line style for VA High/Low", Order=10, GroupName="02. Appearance")]
		public DashStyleHelper VAHLDashStyle
		{ get; set; }
		
		[NinjaScriptProperty]
		[Display(Name="Show Labels", Description="Show price labels on lines", Order=11, GroupName="02. Appearance")]
		public bool ShowLabels
		{ get; set; }
		
		[NinjaScriptProperty]
		[Display(Name="Show Histogram", Description="Show volume profile histogram", Order=12, GroupName="02. Appearance")]
		public bool ShowHistogram
		{ get; set; }
		
		[XmlIgnore]
		[Display(Name="Histogram Color", Description="Base color for histogram", Order=13, GroupName="02. Appearance")]
		public Brush HistogramColor
		{ get; set; }
		
		[Browsable(false)]
		public string HistogramColorSerializable
		{
			get { return Serialize.BrushToString(HistogramColor); }
			set { HistogramColor = Serialize.StringToBrush(value); }
		}
		
		[NinjaScriptProperty]
		[Display(Name="Show Volume on Histogram", Description="Display volume values on histogram bars", Order=14, GroupName="02. Appearance")]
		public bool ShowVolumeOnHistogram
		{ get; set; }
		
		[NinjaScriptProperty]
		[Display(Name="Show Developing Value Area", Description="Show developing VA as it forms", Order=15, GroupName="02. Appearance")]
		public bool ShowDevelopingValueArea
		{ get; set; }
		
		[XmlIgnore]
		[Display(Name="Developing VA Color", Description="Color for developing value area", Order=16, GroupName="02. Appearance")]
		public Brush DevelopingVAColor
		{ get; set; }
		
		[Browsable(false)]
		public string DevelopingVAColorSerializable
		{
			get { return Serialize.BrushToString(DevelopingVAColor); }
			set { DevelopingVAColor = Serialize.StringToBrush(value); }
		}
		
		#endregion
	}
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private SessionVolumeProfile[] cacheSessionVolumeProfile;
		public SessionVolumeProfile SessionVolumeProfile(int numberOfRows, int valueAreaCoverage)
		{
			return SessionVolumeProfile(Input, numberOfRows, valueAreaCoverage);
		}

		public SessionVolumeProfile SessionVolumeProfile(ISeries<double> input, int numberOfRows, int valueAreaCoverage)
		{
			if (cacheSessionVolumeProfile != null)
				for (int idx = 0; idx < cacheSessionVolumeProfile.Length; idx++)
					if (cacheSessionVolumeProfile[idx] != null && cacheSessionVolumeProfile[idx].NumberOfRows == numberOfRows && cacheSessionVolumeProfile[idx].ValueAreaCoverage == valueAreaCoverage && cacheSessionVolumeProfile[idx].EqualsInput(input))
						return cacheSessionVolumeProfile[idx];
			return CacheIndicator<SessionVolumeProfile>(new SessionVolumeProfile(){ NumberOfRows = numberOfRows, ValueAreaCoverage = valueAreaCoverage }, input, ref cacheSessionVolumeProfile);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.SessionVolumeProfile SessionVolumeProfile(int numberOfRows, int valueAreaCoverage)
		{
			return indicator.SessionVolumeProfile(Input, numberOfRows, valueAreaCoverage);
		}

		public Indicators.SessionVolumeProfile SessionVolumeProfile(ISeries<double> input , int numberOfRows, int valueAreaCoverage)
		{
			return indicator.SessionVolumeProfile(input, numberOfRows, valueAreaCoverage);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.SessionVolumeProfile SessionVolumeProfile(int numberOfRows, int valueAreaCoverage)
		{
			return indicator.SessionVolumeProfile(Input, numberOfRows, valueAreaCoverage);
		}

		public Indicators.SessionVolumeProfile SessionVolumeProfile(ISeries<double> input , int numberOfRows, int valueAreaCoverage)
		{
			return indicator.SessionVolumeProfile(input, numberOfRows, valueAreaCoverage);
		}
	}
}

#endregion

