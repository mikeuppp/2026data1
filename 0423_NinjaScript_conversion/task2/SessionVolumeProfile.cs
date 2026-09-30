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
		
		/// <summary>
		/// Frozen copy of the last completed session's histogram (survives ResetSessionData).
		/// Aligned with yesterday VAH/VAL/POC and anchored to that session's time range.
		/// </summary>
		private SessionHistogramSnapshot priorSessionHistogram;
		
		// Bumps when prior-session VA / snapshot is updated — throttles redundant Draw.* calls
		private int priorSessionVisualVersion;
		private int lastDrawnPriorVaVersion = -1;
		private long lastDrawnHistogramSessionTicks = long.MinValue;
		
		// Appearance settings
		private bool extendYesterdayOverToday = true;
		private bool showValueArea = true;
		private bool showHistogram = true;
		private bool showLabels = true;
		private bool showVolumeOnHistogram = false;
		private bool showDevelopingValueArea = true;
		
		private SessionIterator sessionIterator;
		private bool useInstrumentTradingHours = true;
		
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
				UseInstrumentTradingHours					= true;
				CustomSessionBegin							= new DateTime(2000, 1, 1, 9, 30, 0);
				CustomSessionEnd							= new DateTime(2000, 1, 1, 16, 0, 0);
				
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
				ShowDevelopingValueArea						= true;
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
				useInstrumentTradingHours = UseInstrumentTradingHours;
				// Force one-time redraw after property / series change
				lastDrawnPriorVaVersion = -1;
				lastDrawnHistogramSessionTicks = long.MinValue;
			}
			else if (State == State.DataLoaded)
			{
				sessionIterator = new SessionIterator(Bars);
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
			
			// Session just ended (e.g. first bar after RTH) — finalize profile for display & prior-session snapshot
			bool sessionJustEnded = wasInSession && !isInSession;
			
			if (isNewSession)
			{
				// Finalize prior session before copying to "yesterday" (covers charts with no post-RTH bars)
				if (sessionHighs.Count > 0)
					CalculateVolumeProfile();
				
				// Process completed session
				ProcessCompletedSession();
				
				// Reset for new session
				ResetSessionData();
				startTime = Time[0];
			}
			else if (sessionJustEnded && sessionHighs.Count > 0)
			{
				CalculateVolumeProfile();
				ProcessCompletedSession();
			}
			
			// Collect data during session
			if (isInSession)
			{
				// Anchor profile to the first in-session bar. Without this, charts that load
				// mid-session never set startTime (it stays DateTime.MinValue) and Draw.* fails silently.
				if (startTime == DateTime.MinValue)
					startTime = Time[0];
				
				sessionHighs.Add(High[0]);
				sessionLows.Add(Low[0]);
				sessionVolumes.Add(Volume[0]);
				sessionTimes.Add(Time[0]);
				endTime = Time[0];
			}
			
			// Build and draw developing profile for the current in-progress session
			// (histogram + POC/VA), similar to real-time session profile tools.
			if (isInSession && sessionHighs.Count > 0)
			{
				CalculateVolumeProfile();
				
				if (showDevelopingValueArea)
				{
					Values[0][0] = valueAreaHigh;
					Values[1][0] = pointOfControl;
					Values[2][0] = valueAreaLow;
				}
				
				UpdateCurrentSessionStaticDrawings();
			}
			else
			{
				ClearCurrentSessionDrawObjects();
			}
			
			// End of historical data while still in session — no later bar to trigger session exit
			if (isInSession && sessionHighs.Count > 0 && IsLastBarOfSeries())
			{
				CalculateVolumeProfile();
				ProcessCompletedSession();
			}
			
			UpdatePriorSessionStaticDrawings();
			
			wasInSession = isInSession;
		}
		
		/// <summary>
		/// Immutable histogram state for one completed session (time axis + volume rows + VA band levels).
		/// </summary>
		private sealed class SessionHistogramSnapshot
		{
			public Dictionary<int, double> VolumeRows;
			public double Step;
			public double DayHigh;
			public double DayLow;
			public DateTime StartTime;
			public DateTime EndTime;
			public int ValueAreaHighLevel;
			public int ValueAreaLowLevel;
			
			public bool IsValid
			{
				get
				{
					return VolumeRows != null && VolumeRows.Count > 0
						&& !double.IsNaN(Step) && Step > 0
						&& !double.IsNaN(DayHigh) && !double.IsNaN(DayLow);
				}
			}
		}
		
		#region Helper Methods
		
		/// <summary>
		/// Determines if the current bar lies inside the configured session window.
		/// Default: NinjaTrader data series trading-hours template (futures, forex, etc.).
		/// Optional: fixed clock times (same chart/local time as bar timestamps).
		/// </summary>
		private bool IsInRegularSession()
		{
			if (useInstrumentTradingHours)
			{
				if (sessionIterator == null)
					return IsInCustomClockSession();
				
				// includesEndTimeStamp: include :00 boundaries on intraday series
				bool isIntraDay = IsIntradayBarsPeriod();
				if (sessionIterator.IsInSession(Time[0], true, isIntraDay))
					return true;
				
				// Template missing or no match on this bar — fall back to custom clock window
				// so profiles still plot (many charts have no template assigned until user sets one).
				return IsInCustomClockSession();
			}
			
			return IsInCustomClockSession();
		}
		
		private static bool IsIntradayBarsPeriod(BarsPeriod barsPeriod)
		{
			if (barsPeriod == null)
				return true;
			
			switch (barsPeriod.BarsPeriodType)
			{
				case BarsPeriodType.Tick:
				case BarsPeriodType.Volume:
				case BarsPeriodType.Range:
				case BarsPeriodType.Second:
				case BarsPeriodType.Minute:
					return true;
				default:
					return false;
			}
		}
		
		private bool IsIntradayBarsPeriod()
		{
			return IsIntradayBarsPeriod(Bars != null ? Bars.BarsPeriod : null);
		}
		
		/// <summary>
		/// Fixed session window using TimeOfDay only (date portion ignored).
		/// If begin &gt; end, session spans midnight (e.g. 17:00–16:00 next calendar day).
		/// </summary>
		private bool IsInCustomClockSession()
		{
			TimeSpan t = Time[0].TimeOfDay;
			TimeSpan open = CustomSessionBegin.TimeOfDay;
			TimeSpan close = CustomSessionEnd.TimeOfDay;
			
			if (open <= close)
				return t >= open && t <= close;
			
			return t >= open || t <= close;
		}
		
		/// <summary>
		/// Detects if a new session has started
		/// Maps to PineScript session detection logic
		/// </summary>
		private bool DetectNewSession(bool isInSession)
		{
			if (CurrentBar < 1)
				return false;
			
			// Instrument trading-hours template: start a new profile when we *enter* the template
			// window (covers mid-chart loads and RTH-only templates nested inside a longer NT session),
			// or when NinjaTrader marks the first bar of an instrument session *while* that bar is in-window.
			// Using IsFirstBarOfSession alone misses RTH open when the template's session started earlier
			// (e.g. ETH) and would also fire on session starts where isInSession is false.
			if (useInstrumentTradingHours && Bars != null)
			{
				bool enteringTemplateWindow = isInSession && !wasInSession;
				bool firstBarOfNtSessionInWindow = Bars.IsFirstBarOfSession && isInSession;
				return enteringTemplateWindow || firstBarOfNtSessionInWindow;
			}
			
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
		/// True on the final bar of the primary series (historical tail or realtime).
		/// Used to finalize the session profile when no out-of-session bar exists.
		/// </summary>
		private bool IsLastBarOfSeries()
		{
			if (CurrentBar < 0 || Bars == null || Bars.Count == 0)
				return false;
			return CurrentBar == Bars.Count - 1;
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
			
			CapturePriorSessionHistogram();
			priorSessionVisualVersion++;
		}
		
		/// <summary>
		/// Copies current profile into priorSessionHistogram so histogram survives session reset.
		/// </summary>
		private void CapturePriorSessionHistogram()
		{
			if (volumeRows == null || volumeRows.Count == 0 || double.IsNaN(step) || step <= 0)
				return;
			
			var rowsCopy = new Dictionary<int, double>(volumeRows);
			priorSessionHistogram = new SessionHistogramSnapshot
			{
				VolumeRows = rowsCopy,
				Step = step,
				DayHigh = dayHigh,
				DayLow = dayLow,
				StartTime = startTime,
				EndTime = endTime,
				ValueAreaHighLevel = valueAreaHighLevel,
				ValueAreaLowLevel = valueAreaLowLevel
			};
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
		
		#region Static drawing (OnBarUpdate — not OnRender)
		
		private const int HistogramDrawCleanupCap = 128;
		
		/// <summary>
		/// Updates prior-session NinjaTrader draw objects once per bar when needed.
		/// Avoids hundreds of Draw.* evaluations per second from OnRender.
		/// </summary>
		private void UpdatePriorSessionStaticDrawings()
		{
			bool hasPriorVa = !double.IsNaN(yesterdayVAH) && !double.IsNaN(yesterdayVAL) && !double.IsNaN(yesterdayPOC);
			
			if (!showValueArea || !hasPriorVa)
			{
				RemovePriorValueAreaDrawObjects();
				lastDrawnPriorVaVersion = -1;
			}
			else
			{
				bool needVaRedraw = extendYesterdayOverToday || showLabels || priorSessionVisualVersion != lastDrawnPriorVaVersion;
				if (needVaRedraw)
				{
					DrawPriorValueAreas();
					lastDrawnPriorVaVersion = priorSessionVisualVersion;
				}
			}
			
			if (!showHistogram || priorSessionHistogram == null || !priorSessionHistogram.IsValid)
			{
				ClearPriorHistogramDrawObjects();
				lastDrawnHistogramSessionTicks = long.MinValue;
				return;
			}
			
			long histTicks = priorSessionHistogram.StartTime.Ticks;
			if (histTicks != lastDrawnHistogramSessionTicks)
			{
				ClearPriorHistogramDrawObjects();
				DrawPriorSessionHistogram(priorSessionHistogram);
				lastDrawnHistogramSessionTicks = histTicks;
			}
		}
		
		private void UpdateCurrentSessionStaticDrawings()
		{
			if (sessionHighs.Count == 0 || volumeRows == null || volumeRows.Count == 0 || double.IsNaN(step) || step <= 0)
				return;
			
			if (startTime == DateTime.MinValue)
				return;
			
			DateTime drawEnd = endTime == DateTime.MinValue ? Time[0] : endTime;
			if (drawEnd < startTime)
				drawEnd = startTime;
			
			if (showValueArea && !double.IsNaN(valueAreaHigh) && !double.IsNaN(valueAreaLow) && !double.IsNaN(pointOfControl))
				DrawCurrentSessionValueAreas(startTime, drawEnd);
			else
				RemoveCurrentValueAreaDrawObjects();
			
			if (showHistogram)
				DrawCurrentSessionHistogram(startTime, drawEnd);
			else
				ClearCurrentHistogramDrawObjects();
		}
		
		private void ClearCurrentSessionDrawObjects()
		{
			RemoveCurrentValueAreaDrawObjects();
			ClearCurrentHistogramDrawObjects();
		}
		
		private void RemoveCurrentValueAreaDrawObjects()
		{
			const string tag = "SVP_Current";
			RemoveDrawObject(tag + "_VAH");
			RemoveDrawObject(tag + "_VAL");
			RemoveDrawObject(tag + "_POC");
			RemoveDrawObject(tag + "_VAH_Label");
			RemoveDrawObject(tag + "_VAL_Label");
			RemoveDrawObject(tag + "_POC_Label");
		}
		
		private void ClearCurrentHistogramDrawObjects()
		{
			for (int i = 0; i < HistogramDrawCleanupCap; i++)
			{
				RemoveDrawObject("SVP_Current_Hist_R_" + i);
				RemoveDrawObject("SVP_Current_Hist_R_" + i + "_Text");
			}
			RemoveDrawObject("SVP_Current_HistBg");
		}
		
		private void DrawCurrentSessionValueAreas(DateTime drawStart, DateTime drawEnd)
		{
			const string tag = "SVP_Current";
			
			/// Draw.Line(this, tag + "_VAH", false, drawStart, valueAreaHigh, drawEnd, valueAreaHigh,
			/// 	VAHLColor, VAHLDashStyle, VAHLWidth);
			/// Draw.Line(this, tag + "_VAL", false, drawStart, valueAreaLow, drawEnd, valueAreaLow,
			///	VAHLColor, VAHLDashStyle, VAHLWidth);
			Draw.Line(this, tag + "_POC", false, drawStart, pointOfControl, drawEnd, pointOfControl,
				POCColor, POCDashStyle, POCWidth);
			
			if (showLabels)
			{
				double percentVAH = ((valueAreaHigh - Close[0]) / Close[0]) * 100;
				double percentVAL = ((valueAreaLow - Close[0]) / Close[0]) * 100;
				double percentPOC = ((pointOfControl - Close[0]) / Close[0]) * 100;
				
				Draw.Text(this, tag + "_VAH_Label", true,
					string.Format("{0:F2} cVAH ({1:F2}%)", valueAreaHigh, percentVAH),
					0, valueAreaHigh, 10, VAHLColor, new SimpleFont(), TextAlignment.Left,
					Brushes.Transparent, Brushes.Transparent, 0);
				
				Draw.Text(this, tag + "_VAL_Label", true,
					string.Format("{0:F2} cVAL ({1:F2}%)", valueAreaLow, percentVAL),
					0, valueAreaLow, 10, VAHLColor, new SimpleFont(), TextAlignment.Left,
					Brushes.Transparent, Brushes.Transparent, 0);
				
				Draw.Text(this, tag + "_POC_Label", true,
					string.Format("{0:F2} cPOC ({1:F2}%)", pointOfControl, percentPOC),
					0, pointOfControl, 10, POCColor, new SimpleFont(), TextAlignment.Left,
					Brushes.Transparent, Brushes.Transparent, 0);
			}
		}
		
		private void DrawCurrentSessionHistogram(DateTime drawStart, DateTime drawEnd)
		{
			double totalVol = volumeRows.Values.Sum();
			if (totalVol <= 0)
			{
				ClearCurrentHistogramDrawObjects();
				return;
			}
			
			TimeSpan barRange = drawEnd - drawStart;
			if (barRange.Ticks <= 0)
				barRange = TimeSpan.FromMinutes(1);
			
			double stepSpacer = step * 0.05;
			
			foreach (var kvp in volumeRows)
			{
				int level = kvp.Key;
				double vol = kvp.Value;
				
				double histPrice = dayLow + (level * step);
				double volWidthPercent = vol / totalVol;
				TimeSpan histWidth = TimeSpan.FromTicks((long)(barRange.Ticks * volWidthPercent * 2));
				DateTime histRightTime = drawStart + histWidth;
				
				int opacity = (level < valueAreaLowLevel || level > valueAreaHighLevel) ? 75 : 45;
				Brush histBrush = new SolidColorBrush(((SolidColorBrush)HistogramColor).Color) { Opacity = opacity / 100.0 };
				histBrush.Freeze();
				
				string tag = "SVP_Current_Hist_R_" + level;
				Draw.Rectangle(this, tag, false, drawStart, histPrice + step - stepSpacer,
					histRightTime, histPrice + stepSpacer, histBrush, histBrush, 0);
				
				if (showVolumeOnHistogram)
				{
					Draw.Text(this, tag + "_Text", true, string.Format("{0:F0}", vol),
						0, histPrice + (step / 2), 0, Brushes.White, new SimpleFont("Arial", 8),
						TextAlignment.Left, Brushes.Transparent, Brushes.Transparent, 0);
				}
			}
			
			Brush bgBrush = new SolidColorBrush(((SolidColorBrush)HistogramColor).Color) { Opacity = 0.08 };
			bgBrush.Freeze();
			Draw.Rectangle(this, "SVP_Current_HistBg", false,
				drawStart, dayHigh, drawEnd, dayLow, bgBrush, bgBrush, 0);
		}
		
		private void RemovePriorValueAreaDrawObjects()
		{
			const string tag = "SVP_Yesterday";
			RemoveDrawObject(tag + "_VAH");
			RemoveDrawObject(tag + "_VAL");
			RemoveDrawObject(tag + "_POC");
			RemoveDrawObject(tag + "_VAH_Label");
			RemoveDrawObject(tag + "_VAL_Label");
			RemoveDrawObject(tag + "_POC_Label");
		}
		
		private void ClearPriorHistogramDrawObjects()
		{
			for (int i = 0; i < HistogramDrawCleanupCap; i++)
			{
				RemoveDrawObject("SVP_Hist_R_" + i);
				RemoveDrawObject("SVP_Hist_R_" + i + "_Text");
			}
			RemoveDrawObject("SVP_HistBg");
		}
		
		/// <summary>
		/// Draws previous session's value area lines and zones
		/// Maps to PineScript drawPriorValueAreas() function
		/// </summary>
		private void DrawPriorValueAreas()
		{
			if (double.IsNaN(yesterdayVAH) || double.IsNaN(yesterdayVAL) || double.IsNaN(yesterdayPOC))
				return;
			
			// Invalid anchor times prevent NinjaTrader draw objects from appearing.
			if (yesterdayStartTime == DateTime.MinValue || yesterdayEndTime == DateTime.MinValue)
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
		/// Draws the volume profile histogram for a completed session snapshot.
		/// Uses stable tags (row index) so redraw replaces prior session geometry without orphaned objects.
		/// </summary>
		private void DrawPriorSessionHistogram(SessionHistogramSnapshot snap)
		{
			if (snap == null || !snap.IsValid)
				return;
			
			double totalVol = snap.VolumeRows.Values.Sum();
			if (totalVol <= 0)
				return;
			
			if (snap.StartTime == DateTime.MinValue)
				return;
			
			TimeSpan barRange = snap.EndTime - snap.StartTime;
			if (barRange.Ticks <= 0)
				barRange = TimeSpan.FromMinutes(1);
			
			double stepSpacer = snap.Step * 0.05;
			
			// Draw each histogram bar
			foreach (var kvp in snap.VolumeRows)
			{
				int level = kvp.Key;
				double vol = kvp.Value;
				
				double histPrice = snap.DayLow + (level * snap.Step);
				double volWidthPercent = vol / totalVol;
				TimeSpan histWidth = TimeSpan.FromTicks((long)(barRange.Ticks * volWidthPercent * 2));
				DateTime histRightTime = snap.StartTime + histWidth;
				
				// Determine opacity based on whether it's in value area
				int opacity = (level < snap.ValueAreaLowLevel || level > snap.ValueAreaHighLevel) ? 70 : 40;
				Brush histBrush = new SolidColorBrush(((SolidColorBrush)HistogramColor).Color) { Opacity = opacity / 100.0 };
				histBrush.Freeze();
				
				// Stable tag per row so new session replaces same objects (caller clears extras up to cap)
				string tag = "SVP_Hist_R_" + level;
				Draw.Rectangle(this, tag, false, snap.StartTime, histPrice + snap.Step - stepSpacer, 
					histRightTime, histPrice + stepSpacer, histBrush, histBrush, 0);
				
				// Optionally show volume on histogram
				if (showVolumeOnHistogram)
				{
					Draw.Text(this, tag + "_Text", true, string.Format("{0:F0}", vol), 
						0, histPrice + (snap.Step / 2), 0, Brushes.White, new SimpleFont("Arial", 8), 
						TextAlignment.Left, Brushes.Transparent, Brushes.Transparent, 0);
				}
			}
			
			// Draw background box
			Brush bgBrush = new SolidColorBrush(((SolidColorBrush)HistogramColor).Color) { Opacity = 0.1 };
			bgBrush.Freeze();
			Draw.Rectangle(this, "SVP_HistBg", false, 
				snap.StartTime, snap.DayHigh, snap.EndTime, snap.DayLow, bgBrush, bgBrush, 0);
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
		
		[NinjaScriptProperty]
		[Display(Name="Use instrument trading hours", Description="Use the Trading Hours template assigned to this chart series (recommended). Uncheck to use custom session times below.", Order=3, GroupName="01. General Settings")]
		public bool UseInstrumentTradingHours
		{ get; set; }
		
		[Display(Name="Custom session begin", Description="Time-of-day only; used when Use instrument trading hours is disabled. Compared to bar time using the chart series timezone.", Order=4, GroupName="01. General Settings")]
		[PropertyEditor("NinjaTrader.Gui.Tools.TimeEditorKey")]
		public DateTime CustomSessionBegin
		{ get; set; }
		
		[Display(Name="Custom session end", Description="Time-of-day only; inclusive. If begin is later than end, the window wraps past midnight.", Order=5, GroupName="01. General Settings")]
		[PropertyEditor("NinjaTrader.Gui.Tools.TimeEditorKey")]
		public DateTime CustomSessionEnd
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
		public SessionVolumeProfile SessionVolumeProfile(int numberOfRows, int valueAreaCoverage, bool useInstrumentTradingHours)
		{
			return SessionVolumeProfile(Input, numberOfRows, valueAreaCoverage, useInstrumentTradingHours);
		}

		public SessionVolumeProfile SessionVolumeProfile(ISeries<double> input, int numberOfRows, int valueAreaCoverage, bool useInstrumentTradingHours)
		{
			if (cacheSessionVolumeProfile != null)
				for (int idx = 0; idx < cacheSessionVolumeProfile.Length; idx++)
					if (cacheSessionVolumeProfile[idx] != null && cacheSessionVolumeProfile[idx].NumberOfRows == numberOfRows && cacheSessionVolumeProfile[idx].ValueAreaCoverage == valueAreaCoverage && cacheSessionVolumeProfile[idx].UseInstrumentTradingHours == useInstrumentTradingHours && cacheSessionVolumeProfile[idx].EqualsInput(input))
						return cacheSessionVolumeProfile[idx];
			return CacheIndicator<SessionVolumeProfile>(new SessionVolumeProfile(){ NumberOfRows = numberOfRows, ValueAreaCoverage = valueAreaCoverage, UseInstrumentTradingHours = useInstrumentTradingHours }, input, ref cacheSessionVolumeProfile);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.SessionVolumeProfile SessionVolumeProfile(int numberOfRows, int valueAreaCoverage, bool useInstrumentTradingHours)
		{
			return indicator.SessionVolumeProfile(Input, numberOfRows, valueAreaCoverage, useInstrumentTradingHours);
		}

		public Indicators.SessionVolumeProfile SessionVolumeProfile(ISeries<double> input , int numberOfRows, int valueAreaCoverage, bool useInstrumentTradingHours)
		{
			return indicator.SessionVolumeProfile(input, numberOfRows, valueAreaCoverage, useInstrumentTradingHours);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.SessionVolumeProfile SessionVolumeProfile(int numberOfRows, int valueAreaCoverage, bool useInstrumentTradingHours)
		{
			return indicator.SessionVolumeProfile(Input, numberOfRows, valueAreaCoverage, useInstrumentTradingHours);
		}

		public Indicators.SessionVolumeProfile SessionVolumeProfile(ISeries<double> input , int numberOfRows, int valueAreaCoverage, bool useInstrumentTradingHours)
		{
			return indicator.SessionVolumeProfile(input, numberOfRows, valueAreaCoverage, useInstrumentTradingHours);
		}
	}
}

#endregion