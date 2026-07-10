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
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class IctAmdBot : Strategy
    {
        #region Propiedades de Riesgo (Fijo)
        [NinjaScriptProperty]
        [Display(Name="Contratos Fijos", Description="Cantidad fija de contratos", Order=1, GroupName="1. Gestión de Riesgo")]
        public int FixedContracts { get; set; } = 5;

        [NinjaScriptProperty]
        [Display(Name="Stop Loss ($)", Description="Stop loss total en dólares", Order=2, GroupName="1. Gestión de Riesgo")]
        public double FixedStopLoss { get; set; } = 500;

        [NinjaScriptProperty]
        [Display(Name="Distancia Trailing Stop (Pts)", Description="Para el último contrato (Runner)", Order=3, GroupName="1. Gestión de Riesgo")]
        public int TrailingStopPoints { get; set; } = 35;

        [NinjaScriptProperty]
        [Display(Name="Pérdida Máxima Diaria ($)", Description="Detiene la operativa si la pérdida diaria supera este monto", Order=4, GroupName="1. Gestión de Riesgo")]
        public double MaxDailyLoss { get; set; } = 1000;
        #endregion

        #region Propiedades Operativas
        [NinjaScriptProperty]
        [Display(Name="Hora Inicio NY", Description="Hora en formato HHMMSS (Hora Chart)", Order=1, GroupName="2. Horario")]
        public int StartTime { get; set; } = 93000; // 9:30 AM NY

        [NinjaScriptProperty]
        [Display(Name="Hora Fin NY", Description="Hora en formato HHMMSS (Hora Chart)", Order=2, GroupName="2. Horario")]
        public int EndTime { get; set; } = 120000; // 12:00 PM NY

        [NinjaScriptProperty]
        [Display(Name="Max Trades por Día", Description="Límite de operaciones diarias", Order=3, GroupName="2. Horario")]
        public int MaxTradesPerDay { get; set; } = 2;

        [NinjaScriptProperty]
        [Display(Name="Hora Inicio Londres", Description="Hora en formato HHMMSS (Hora Chart)", Order=4, GroupName="2. Horario")]
        public int LondonStartTime { get; set; } = 30000; // 3:00 AM NY

        [NinjaScriptProperty]
        [Display(Name="Hora Fin Londres", Description="Hora en formato HHMMSS (Hora Chart)", Order=5, GroupName="2. Horario")]
        public int LondonEndTime { get; set; } = 93000; // 9:30 AM NY

        [NinjaScriptProperty]
        [Display(Name="Detener si gana el primero", Description="Si es true, no toma más trades tras una ganancia", Order=6, GroupName="2. Horario")]
        public bool StopOnFirstWin { get; set; } = true;
        
        [NinjaScriptProperty]
        [Display(Name="Puntos Parcial (Split)", Description="Puntos (no ticks) para tomar parciales", Order=1, GroupName="3. Gestión de Trade")]
        public int PartialPoints { get; set; } = 62;
        [NinjaScriptProperty]
        [Display(Name="Min Desplazamiento (Pts)", Description="Filtro para evitar falsos MSS (ej. 10 puntos)", Order=2, GroupName="3. Gestión de Trade")]
        public int MinDisplacementPoints { get; set; } = 10;

        [NinjaScriptProperty]
        [Display(Name="Retraso OTE (%)", Description="Nivel Fibonacci de entrada (ej. 0.62)", Order=3, GroupName="3. Gestión de Trade")]
        public double OteRetracement { get; set; } = 0.62;

        [NinjaScriptProperty]
        [Display(Name="Periodos M15 Liquidez", Description="Velas M15 para buscar BSL/SSL (ej. 8 = 2 hrs)", Order=4, GroupName="3. Gestión de Trade")]
        public int LiquidityLookback { get; set; } = 8;
        #endregion

        // Variables de Estado
        private bool isBullishBias = false;
        private bool isBearishBias = false;
        private double m15BSL = 0; // Buy Side Liquidity
        private double m15SSL = 0; // Sell Side Liquidity
        private bool manipulationOccurred = false;
        private double manipulationExtremum = 0;
        private double impulseExtremum = 0;
        private bool isPartialTaken = false;
        private int tradesToday = 0;
        private bool hasWonToday = false;
        private double dailyPnL = 0;
        private int lastTradeCount = 0;
        private double activeOteEntry = 0;
        private double activeStopLoss = 0;
        private bool isOrderPending = false;
        
        // Trailing Stop Variables
        private double highestPriceSinceEntry = 0;
        private double lowestPriceSinceEntry = double.MaxValue;
        private double currentStopPrice = 0;
        
        // Visual References
        private double londonHigh = 0;
        private double londonLow = 0;
        
        // Orders Tracking
        private Order partialEntryOrder = null;
        private Order runnerEntryOrder = null;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description                 = "Bot institucional basado en el modelo AMD/PO3 de ICT. Optimizado para MNQ.";
                Name                        = "IctAmdBot";
                Calculate                   = Calculate.OnBarClose;
                EntriesPerDirection         = 2;
                EntryHandling               = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy= true;
                ExitOnSessionCloseSeconds   = 30;
                IsFillLimitOnTouch          = false;
                TraceOrders                 = false;
                BarsRequiredToTrade         = 20;
            }
            else if (State == State.Configure)
            {
                // Resoluciones Múltiples (Top-Down Analysis)
                // Default (BarsInProgress == 0) -> LTF (ej. M1)
                AddDataSeries(Data.BarsPeriodType.Minute, 240);  // BarsInProgress == 1 (H4)
                AddDataSeries(Data.BarsPeriodType.Minute, 15);   // BarsInProgress == 2 (M15)
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBars[0] < 20 || CurrentBars[1] < 20 || CurrentBars[2] < 20) return;

            // --- 1. Top-Down Analysis (Macro/Dirección) ---
            if (BarsInProgress == 1)
            {
                DetermineDailyBias();
                return;
            }

            // --- 2. ITF: Estructura y Liquidez M15 ---
            if (BarsInProgress == 2)
            {
                IdentifyLiquidityPools();
                return;
            }

            // --- 3. LTF: Ejecución M1-M5 ---
            if (BarsInProgress == 0)
            {
                int timeNow = ToTime(Time[0]);
                
                // Reiniciar niveles de Londres al iniciar nueva sesión (6:00 PM EST)
                if (Bars.IsFirstBarOfSession)
                {
                    londonHigh = 0;
                    londonLow = 0;
                }

                // Calcular Alto y Bajo de Londres
                if (timeNow >= LondonStartTime && timeNow <= LondonEndTime)
                {
                    if (londonHigh == 0 || High[0] > londonHigh) londonHigh = High[0];
                    if (londonLow == 0 || Low[0] < londonLow) londonLow = Low[0];
                }

                // Dibujar Líneas Visuales
                if (CurrentBars[0] > 50)
                {
                    try { Draw.HorizontalLine(this, "PDH", PriorDayOHLC().PriorHigh[0], Brushes.DodgerBlue); } catch { }
                    try { Draw.HorizontalLine(this, "PDL", PriorDayOHLC().PriorLow[0], Brushes.DodgerBlue); } catch { }
                    
                    if (londonHigh > 0 && londonLow > 0)
                    {
                        try { Draw.HorizontalLine(this, "LondonHigh", londonHigh, Brushes.Gold); } catch { }
                        try { Draw.HorizontalLine(this, "LondonLow", londonLow, Brushes.Gold); } catch { }
                    }
                }
                
                // Filtro de Tiempo (Time & Price) - NY Session
                bool inSession = (timeNow >= StartTime && timeNow <= EndTime);

                if (!inSession && Position.MarketPosition == MarketPosition.Flat)
                {
                    ResetSetup();
                }

                // Si no hay posición, buscar setup AMD
                bool canTrade = tradesToday < MaxTradesPerDay;
                if (StopOnFirstWin && hasWonToday) canTrade = false;
                if (dailyPnL <= -MaxDailyLoss) canTrade = false;

                if (Position.MarketPosition == MarketPosition.Flat && canTrade && inSession)
                {
                    // Paso 2: Turtle Soup (Manipulación)
                    CheckManipulation();

                    // Pasos 3, 4 y 5: MSS, FVG, OTE Entry
                    if (manipulationOccurred)
                    {
                        CheckEntryTriggers();
                    }
                }
                else if (Position.MarketPosition != MarketPosition.Flat)
                {
                    // --- 4. Gestión del Trade y Salidas ---
                    ManageOpenTrade();
                }
            }
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity, MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution.Order != null && execution.Order.OrderState == OrderState.Filled && 
               (execution.Order.Name == "EntradaParcial" || execution.Order.Name == "EntradaRunner"))
            {
                if (isOrderPending)
                {
                    tradesToday++;
                    isOrderPending = false;
                }
            }
        }

        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity, int filled, double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string nativeError)
        {
            if (order.Name == "EntradaParcial")
            {
                if (orderState == OrderState.Working || orderState == OrderState.Accepted)
                    partialEntryOrder = order;
                else if (orderState == OrderState.Filled || orderState == OrderState.Cancelled || orderState == OrderState.Rejected)
                    partialEntryOrder = null;
            }
            else if (order.Name == "EntradaRunner")
            {
                if (orderState == OrderState.Working || orderState == OrderState.Accepted)
                    runnerEntryOrder = order;
                else if (orderState == OrderState.Filled || orderState == OrderState.Cancelled || orderState == OrderState.Rejected)
                    runnerEntryOrder = null;
            }
        }

        protected override void OnPositionUpdate(Position position, double averagePrice, int quantity, MarketPosition marketPosition)
        {
            if (position.MarketPosition == MarketPosition.Flat && SystemPerformance.AllTrades.Count > lastTradeCount)
            {
                int newTrades = SystemPerformance.AllTrades.Count - lastTradeCount;
                for (int i = 0; i < newTrades; i++)
                {
                    Trade t = SystemPerformance.AllTrades[SystemPerformance.AllTrades.Count - 1 - i];
                    dailyPnL += t.ProfitCurrency;
                    if (t.ProfitCurrency > 0) hasWonToday = true;
                }
                lastTradeCount = SystemPerformance.AllTrades.Count;
            }
        }

        private void DetermineDailyBias()
        {
            // Bias basado puramente en tendencia direccional cruzada (H4)
            double emaFast = EMA(BarsArray[1], 9)[0];
            double emaSlow = EMA(BarsArray[1], 21)[0];
            
            isBullishBias = emaFast > emaSlow;
            isBearishBias = emaFast < emaSlow;
        }

        private void IdentifyLiquidityPools()
        {
            // Identificar máximos y mínimos previos (Liquidity Pools) en M15
            m15BSL = MAX(High, Math.Max(1, LiquidityLookback))[1]; // Buy Side Liquidity
            m15SSL = MIN(Low, Math.Max(1, LiquidityLookback))[1];  // Sell Side Liquidity
        }

        private void CheckManipulation()
        {
            if (isBullishBias)
            {
                // Buscamos barrido de Sell Stops
                if (Low[0] < m15SSL)
                {
                    manipulationOccurred = true;
                    if (manipulationExtremum == 0 || Low[0] < manipulationExtremum)
                    {
                        manipulationExtremum = Low[0];
                        impulseExtremum = High[0]; 
                    }
                }
                
                if (manipulationOccurred)
                {
                    if (impulseExtremum == 0 || High[0] > impulseExtremum)
                        impulseExtremum = High[0];
                }
            }
            else if (isBearishBias)
            {
                // Buscamos barrido de Buy Stops
                if (High[0] > m15BSL)
                {
                    manipulationOccurred = true;
                    if (manipulationExtremum == 0 || High[0] > manipulationExtremum)
                    {
                        manipulationExtremum = High[0];
                        impulseExtremum = Low[0];
                    }
                }

                if (manipulationOccurred)
                {
                    if (impulseExtremum == 0 || Low[0] < impulseExtremum)
                        impulseExtremum = Low[0];
                }
            }
        }

        private void CheckEntryTriggers()
        {
            // Mantener orden límite activa y actualizar el nivel OTE dinámicamente si el impulso se extiende
            if (isOrderPending)
            {
                if (isBullishBias)
                {
                    activeOteEntry = Instrument.MasterInstrument.RoundToTickSize(impulseExtremum - ((impulseExtremum - manipulationExtremum) * OteRetracement));
                    if (Low[0] < manipulationExtremum) { ResetSetup(); return; } // Invalida si la mecha rompe el mínimo
                }
                else if (isBearishBias)
                {
                    activeOteEntry = Instrument.MasterInstrument.RoundToTickSize(impulseExtremum + ((manipulationExtremum - impulseExtremum) * OteRetracement));
                    if (High[0] > manipulationExtremum) { ResetSetup(); return; } // Invalida si la mecha rompe el máximo
                }

                if (FixedContracts > 0)
                {
                    // Stop Loss Estructural en el extremo (con límite máximo en dólares)
                    double maxRiskPoints = FixedStopLoss / (FixedContracts * Instrument.MasterInstrument.PointValue);
                    double slPrice = isBullishBias ? (manipulationExtremum - TickSize) : (manipulationExtremum + TickSize);

                    // Limita el SL al FixedStopLoss si el extremo está muy lejos
                    if (isBullishBias) slPrice = Math.Max(slPrice, activeOteEntry - maxRiskPoints);
                    else slPrice = Math.Min(slPrice, activeOteEntry + maxRiskPoints);

                    int partialQty = (int)Math.Floor(FixedContracts * 0.80);
                    int runnerQty = FixedContracts - partialQty;

                    if (partialQty > 0)
                    {
                        SetStopLoss("EntradaParcial", CalculationMode.Price, slPrice, false);
                        SetProfitTarget("EntradaParcial", CalculationMode.Ticks, PartialPoints * 4, false);
                    }
                    if (runnerQty > 0)
                    {
                        SetStopLoss("EntradaRunner", CalculationMode.Price, slPrice, false);
                    }

                    if (isBullishBias)
                    {
                        if (partialQty > 0) 
                        {
                            if (partialEntryOrder != null && (partialEntryOrder.OrderState == OrderState.Working || partialEntryOrder.OrderState == OrderState.Accepted) && partialEntryOrder.LimitPrice != activeOteEntry)
                                ChangeOrder(partialEntryOrder, partialQty, activeOteEntry, 0);
                            else if (partialEntryOrder == null)
                                EnterLongLimit(partialQty, activeOteEntry, "EntradaParcial");
                        }
                        if (runnerQty > 0)
                        {
                            if (runnerEntryOrder != null && (runnerEntryOrder.OrderState == OrderState.Working || runnerEntryOrder.OrderState == OrderState.Accepted) && runnerEntryOrder.LimitPrice != activeOteEntry)
                                ChangeOrder(runnerEntryOrder, runnerQty, activeOteEntry, 0);
                            else if (runnerEntryOrder == null)
                                EnterLongLimit(runnerQty, activeOteEntry, "EntradaRunner");
                        }
                    }
                    else
                    {
                        if (partialQty > 0) 
                        {
                            if (partialEntryOrder != null && (partialEntryOrder.OrderState == OrderState.Working || partialEntryOrder.OrderState == OrderState.Accepted) && partialEntryOrder.LimitPrice != activeOteEntry)
                                ChangeOrder(partialEntryOrder, partialQty, activeOteEntry, 0);
                            else if (partialEntryOrder == null)
                                EnterShortLimit(partialQty, activeOteEntry, "EntradaParcial");
                        }
                        if (runnerQty > 0)
                        {
                            if (runnerEntryOrder != null && (runnerEntryOrder.OrderState == OrderState.Working || runnerEntryOrder.OrderState == OrderState.Accepted) && runnerEntryOrder.LimitPrice != activeOteEntry)
                                ChangeOrder(runnerEntryOrder, runnerQty, activeOteEntry, 0);
                            else if (runnerEntryOrder == null)
                                EnterShortLimit(runnerQty, activeOteEntry, "EntradaRunner");
                        }
                    }
                }
                return;
            }

            if (isBullishBias)
            {
                bool fvgAlcista = Low[0] > High[2] && Close[1] > High[2]; 
                bool trueDisplacement = (impulseExtremum - manipulationExtremum) >= (MinDisplacementPoints * 4 * TickSize);
                bool momentumM1 = Close[0] > EMA(21)[0]; // Confirmación de momentum para evitar falsos rebotes
                
                if (fvgAlcista && trueDisplacement && momentumM1)
                {
                    activeOteEntry = Instrument.MasterInstrument.RoundToTickSize(impulseExtremum - ((impulseExtremum - manipulationExtremum) * OteRetracement)); 
                    isOrderPending = true;
                }
            }
            else if (isBearishBias)
            {
                bool fvgBajista = High[0] < Low[2] && Close[1] < Low[2];
                bool trueDisplacement = (manipulationExtremum - impulseExtremum) >= (MinDisplacementPoints * 4 * TickSize);
                bool momentumM1 = Close[0] < EMA(21)[0];
                
                if (fvgBajista && trueDisplacement && momentumM1)
                {
                    activeOteEntry = Instrument.MasterInstrument.RoundToTickSize(impulseExtremum + ((manipulationExtremum - impulseExtremum) * OteRetracement));
                    isOrderPending = true;
                }
            }
        }

        private void ManageOpenTrade()
        {
            if (!isPartialTaken)
            {
                double maxProfitPoints = 0;
                
                if (Position.MarketPosition == MarketPosition.Long)
                    maxProfitPoints = High[0] - Position.AveragePrice;
                else if (Position.MarketPosition == MarketPosition.Short)
                    maxProfitPoints = Position.AveragePrice - Low[0];

                if (maxProfitPoints >= PartialPoints)
                {
                    // El TP nativo cierra el parcial intra-vela. Aquí solo actualizamos el SL del runner.
                    currentStopPrice = Position.AveragePrice;
                    SetStopLoss("EntradaRunner", CalculationMode.Price, currentStopPrice, false);
                    isPartialTaken = true;
                    
                    highestPriceSinceEntry = High[0];
                    lowestPriceSinceEntry = Low[0];
                }
            }
            else
            {
                // Gestionar Trailing Stop para el Runner
                if (Position.MarketPosition == MarketPosition.Long)
                {
                    if (High[0] > highestPriceSinceEntry) highestPriceSinceEntry = High[0];
                    double trailPrice = highestPriceSinceEntry - (TrailingStopPoints * 4 * TickSize);
                    
                    if (trailPrice > currentStopPrice)
                    {
                        currentStopPrice = trailPrice;
                        SetStopLoss("EntradaRunner", CalculationMode.Price, currentStopPrice, false);
                    }
                }
                else if (Position.MarketPosition == MarketPosition.Short)
                {
                    if (Low[0] < lowestPriceSinceEntry) lowestPriceSinceEntry = Low[0];
                    double trailPrice = lowestPriceSinceEntry + (TrailingStopPoints * 4 * TickSize);
                    
                    if (trailPrice < currentStopPrice || currentStopPrice == 0)
                    {
                        currentStopPrice = trailPrice;
                        SetStopLoss("EntradaRunner", CalculationMode.Price, currentStopPrice, false);
                    }
                }
            }
        }

        private void ResetSetup()
        {
            manipulationOccurred = false;
            manipulationExtremum = 0;
            impulseExtremum = 0;
            isPartialTaken = false;
            tradesToday = 0;
            hasWonToday = false;
            dailyPnL = 0;
            activeOteEntry = 0;
            activeStopLoss = 0;
            isOrderPending = false;
            highestPriceSinceEntry = 0;
            lowestPriceSinceEntry = double.MaxValue;
            currentStopPrice = 0;
            
            if (partialEntryOrder != null && partialEntryOrder.OrderState == OrderState.Working)
                CancelOrder(partialEntryOrder);
                
            if (runnerEntryOrder != null && runnerEntryOrder.OrderState == OrderState.Working)
                CancelOrder(runnerEntryOrder);
        }
    }
}
