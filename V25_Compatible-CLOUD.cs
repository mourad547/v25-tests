using System;
using System.Collections.Generic;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class V25_Compatible : Robot
    {
        [Parameter("Base Jump Points", DefaultValue = 18, MinValue = 10, MaxValue = 35, Step = 1)]
        public int InpJump { get; set; }

        [Parameter("Base Window MS", DefaultValue = 60000, MinValue = 30000, MaxValue = 120000, Step = 10000)]
        public int InpWindowMS { get; set; }

        [Parameter("Base Shotgun Count", DefaultValue = 5, MinValue = 3, MaxValue = 8, Step = 1)]
        public int InpShotgunCount { get; set; }

        [Parameter("Base Total TP $", DefaultValue = 12.0, MinValue = 5, MaxValue = 20, Step = 1)]
        public double InpTotalTP { get; set; }

        [Parameter("Base Lot", DefaultValue = 0.01, MinValue = 0.01, MaxValue = 0.05, Step = 0.01)]
        public double InpBaseLot { get; set; }

        [Parameter("Base SL Points", DefaultValue = 300, MinValue = 200, MaxValue = 600, Step = 50)]
        public int InpBaseSL_Pts { get; set; }

        [Parameter("Daily Max Loss %", DefaultValue = 5.0, MinValue = 1, MaxValue = 10, Step = 0.5)]
        public double InpDailyMaxLossPct { get; set; }

        [Parameter("Reverse Entry (Mean Revert)", DefaultValue = true)]
        public bool InpReverse { get; set; }

        [Parameter("Max Basket Hours", DefaultValue = 4, MinValue = 2, MaxValue = 12, Step = 1)]
        public int InpMaxHours { get; set; }

        private const string Label = "V25_C";
        private double _dayStartBalance;
        private DateTime _currentDay;
        private bool _dayLocked;
        private bool _isClosingBasket;
        private readonly List<double> _priceHist = new List<double>();
        private readonly List<DateTime> _timeHist = new List<DateTime>();
        private double _dynJumpPoints;
        private int _dynWindowMs;
        private double _dynTpUsd;
        private double _dynSlPoints;
        private double _dynLot;
        private int _dynShotgunCount;
        private AverageTrueRange _atr;
        private DateTime _lastLogTime = DateTime.MinValue;
        private DateTime _basketOpenTime = DateTime.MinValue;

        protected override void OnStart()
        {
            _currentDay = Server.Time.Date;
            _dayStartBalance = Account.Balance;
            _atr = Indicators.AverageTrueRange(14, MovingAverageType.Exponential);
            Timer.Start(TimeSpan.FromMilliseconds(500));
            Print("V25 Compatible Started | Balance {0:F2} | Reverse={1} | ATR ON | cTrader 5.9 OK", _dayStartBalance, InpReverse);
        }

        protected override void OnTick()
        {
            if (Server.Time.Date != _currentDay)
            {
                _currentDay = Server.Time.Date;
                _dayStartBalance = Account.Balance;
                _dayLocked = false;
                _isClosingBasket = false;
                _priceHist.Clear();
                _timeHist.Clear();
                Print("NEW DAY {0} Balance {1:F2}", _currentDay, _dayStartBalance);
            }
            if (_dayLocked) return;

            if (_dayStartBalance > 0)
            {
                double dd = (_dayStartBalance - Account.Equity) / _dayStartBalance * 100.0;
                if (dd >= InpDailyMaxLossPct)
                {
                    Print("DAILY LOSS HIT {0:F2}% LOCKING", dd);
                    _dayLocked = true;
                    CloseAllPositions();
                    return;
                }
            }

            CalculateDynamicValues();

            var positions = GetPositions();
            if (positions.Length > 0)
            {
                double basketProfit = positions.Sum(x => x.NetProfit);
                if (_basketOpenTime != DateTime.MinValue && (Server.Time - _basketOpenTime).TotalHours >= InpMaxHours)
                {
                    Print("TIME STOP {0:F1}h Profit {1:F2}", (Server.Time - _basketOpenTime).TotalHours, basketProfit);
                    CloseAllPositions();
                    return;
                }
                if (basketProfit >= _dynTpUsd)
                {
                    Print("BASKET TP HIT {0:F2} / {1:F2}", basketProfit, _dynTpUsd);
                    CloseAllPositions();
                    return;
                }
                return;
            }

            if (_isClosingBasket)
            {
                if (GetPositions().Length == 0)
                {
                    _isClosingBasket = false;
                    Print("BASKET FULLY CLOSED");
                }
                return;
            }

            if (Symbol.Spread > 2.0) return;

            double bid = Symbol.Bid;
            DateTime now = Server.Time;
            _priceHist.Add(bid);
            _timeHist.Add(now);
            while (_timeHist.Count > 0 && (now - _timeHist[0]).TotalMilliseconds > _dynWindowMs)
            {
                _priceHist.RemoveAt(0);
                _timeHist.RemoveAt(0);
            }
            if (_priceHist.Count < 2) return;

            double oldest = _priceHist[0];
            double jump = Math.Abs(bid - oldest) / Symbol.TickSize;

            if (jump >= _dynJumpPoints)
            {
                bool priceUp = bid > oldest;
                bool buy = InpReverse ? !priceUp : priceUp;
                _dynShotgunCount = (jump >= _dynJumpPoints * 2.2) ? Math.Min(8, InpShotgunCount + 2) : InpShotgunCount;

                Print("ENTRY | Jump={0:F1} Thr={1:F1} Buy={2} Orders={3} ATR={4:F1}pts", jump, _dynJumpPoints, buy, _dynShotgunCount, _atr.Result.LastValue / Symbol.TickSize);
                OpenShotgunBasket(buy);
                _basketOpenTime = Server.Time;
                _priceHist.Clear();
                _timeHist.Clear();
            }
        }

        protected override void OnTimer()
        {
            if (_isClosingBasket && GetPositions().Length == 0)
            {
                _isClosingBasket = false;
                Print("BASKET CLOSED via Timer");
            }
        }

        private void CalculateDynamicValues()
        {
            double atrPrice = _atr.Result.LastValue;
            if (double.IsNaN(atrPrice) || atrPrice <= 0) atrPrice = Symbol.PipSize * 8;
            double atrPoints = atrPrice / Symbol.TickSize;

            // Compatible dynamic: filtered noise
            _dynJumpPoints = Math.Max(12, Math.Min(35, atrPoints * 2.2));
            // Override with base param as minimum
            if (_dynJumpPoints < InpJump) _dynJumpPoints = InpJump;

            _dynWindowMs = InpWindowMS;
            if (atrPoints > 25) _dynWindowMs = (int)(_dynWindowMs * 0.8);
            if (_dynWindowMs < 30000) _dynWindowMs = 30000;

            _dynTpUsd = Account.Balance * 0.0012;
            _dynTpUsd = Math.Max(8, Math.Min(20, _dynTpUsd));
            if (_dynTpUsd < InpTotalTP) _dynTpUsd = InpTotalTP;

            _dynSlPoints = atrPoints * 3.5;
            if (_dynSlPoints < 200) _dynSlPoints = 200;
            if (_dynSlPoints > 600) _dynSlPoints = 600;
            if (_dynSlPoints < InpBaseSL_Pts) _dynSlPoints = InpBaseSL_Pts;

            _dynLot = InpBaseLot;

            if ((Server.Time - _lastLogTime).TotalSeconds > 60)
            {
                _lastLogTime = Server.Time;
                Print("DYN V25 | ATR={0:F1}pts Jump={1:F1} TP=${2:F2} SL={3:F0}pts Lot={4:F3}", atrPoints, _dynJumpPoints, _dynTpUsd, _dynSlPoints, _dynLot);
            }
        }

        private void OpenShotgunBasket(bool buy)
        {
            double volume = Symbol.QuantityToVolumeInUnits(_dynLot);
            volume = Symbol.NormalizeVolumeInUnits(volume, RoundingMode.Down);
            if (volume < Symbol.VolumeInUnitsMin) return;
            double slPips = _dynSlPoints * Symbol.TickSize / Symbol.PipSize;
            TradeType type = buy ? TradeType.Buy : TradeType.Sell;
            int success = 0;
            for (int i = 0; i < _dynShotgunCount; i++)
            {
                var result = ExecuteMarketOrder(type, SymbolName, volume, Label, slPips, null);
                if (result.IsSuccessful) success++;
            }
            Print("SHOTGUN DONE {0}/{1} {2}", success, _dynShotgunCount, buy ? "BUY" : "SELL");
        }

        private Position[] GetPositions() => Positions.Where(x => x.Label == Label && x.SymbolName == SymbolName).ToArray();

        private void CloseAllPositions()
        {
            var pos = GetPositions();
            if (pos.Length == 0) { _isClosingBasket = false; return; }
            _isClosingBasket = true;
            foreach (var p in pos) ClosePositionAsync(p);
        }

        protected override void OnStop() { Timer.Stop(); Print("V25 Stopped"); }
    }
}
