#region Using declarations
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
#endregion

// ┌─────────────────────────────────────────────────────────────────────┐
// │  TradingViewBridge — NinjaTrader 8 → LocalChart WebSocket Bridge   │
// │  Puerto HTTP/WS: 4001                                               │
// │  GET  http://localhost:4001/bars?count=1000  → barras históricas    │
// │  WS   ws://localhost:4001/                   → ticks en tiempo real │
// └─────────────────────────────────────────────────────────────────────┘

namespace NinjaTrader.NinjaScript.Strategies
{
    public class TradingViewBridge : Strategy
    {
        private const int PORT = 4001;
        
        // Static server state shared across all indicator instances in NT8
        private static HttpListener _listener;
        private static readonly List<WebSocket> _clients = new List<WebSocket>();
        private static readonly object _serverLock = new object();
        private static CancellationTokenSource _cts;
        private static readonly Dictionary<string, TradingViewBridge> _instances = new Dictionary<string, TradingViewBridge>();

        protected override void OnStateChange()
        {
            LogToFile(string.Format("OnStateChange: State={0}, Instrument={1}", State, Instrument != null ? Instrument.FullName : "null"));
            if (State == State.SetDefaults)
            {
                Description  = "WebSocket bridge — stream bars/ticks a la app de charts local.";
                Name         = "TradingViewBridge";
                Calculate    = Calculate.OnEachTick;
                IsOverlay    = true;
                DisplayInDataBox = false;
                IsUnmanaged  = true;
            }
            else if (State == State.DataLoaded)
            {
                lock (_serverLock)
                {
                    _instances[Instrument.FullName.ToUpper()] = this;
                    if (_listener == null)
                    {
                        StartServer();
                    }
                    else
                    {
                        LogToFile(string.Format("Instancia agregada para {0}. Total: {1}", Instrument.FullName, _instances.Count));
                    }
                }
            }
            else if (State == State.Terminated)
            {
                lock (_serverLock)
                {
                    string key = Instrument.FullName.ToUpper();
                    if (_instances.ContainsKey(key) && _instances[key] == this)
                    {
                        _instances.Remove(key);
                        LogToFile(string.Format("Instancia removida para {0}. Restan: {1}", Instrument.FullName, _instances.Count));
                    }
                    else
                    {
                        LogToFile(string.Format("Instancia temporal descartada para {0}. Restan: {1}", Instrument.FullName, _instances.Count));
                    }
                    if (_instances.Count == 0)
                    {
                        StopServer();
                    }
                }
            }
        }

        // ── Server lifecycle ────────────────────────────────────────────

        private void StartServer()
        {
            _cts = new CancellationTokenSource();
            _listener = new HttpListener();
            _listener.Prefixes.Add(string.Format("http://localhost:{0}/", PORT));

            Task.Run(async () =>
            {
                try
                {
                    _listener.Start();
                    LogToFile(string.Format("Servidor compartido iniciado en http://localhost:{0}/", PORT));
                }
                catch (Exception ex)
                {
                    LogToFile(string.Format("Error al iniciar: {0}\n{1}", ex.Message, ex.StackTrace));
                    _listener = null;
                    return;
                }

                while (!_cts.Token.IsCancellationRequested)
                {
                    HttpListenerContext ctx;
                    try { ctx = await _listener.GetContextAsync(); }
                    catch { break; }
                    _ = Task.Run(() => HandleContext(ctx));
                }
            });
        }

        private void StopServer()
        {
            _cts?.Cancel();
            try { _listener?.Stop(); } catch { }
            _listener = null;
            lock (_serverLock)
            {
                foreach (var ws in _clients)
                    try { ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "stop", CancellationToken.None); } catch { }
                _clients.Clear();
            }
            LogToFile("Servidor compartido detenido.");
        }

        // ── Request handler ─────────────────────────────────────────────

        private async Task HandleContext(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var res = ctx.Response;

            res.Headers["Access-Control-Allow-Origin"]  = "*";
            res.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
            res.Headers["Access-Control-Allow-Headers"] = "Content-Type";
            res.Headers["Cache-Control"]                = "no-cache, no-store, must-revalidate";
            res.Headers["Pragma"]                       = "no-cache";
            res.Headers["Expires"]                      = "0";

            if (req.HttpMethod == "OPTIONS")
            {
                res.StatusCode = 200;
                res.Close();
                return;
            }

            // WebSocket upgrade
            if (ctx.Request.IsWebSocketRequest)
            {
                var wsCtx = await ctx.AcceptWebSocketAsync(null);
                await HandleWebSocket(wsCtx.WebSocket);
                return;
            }

            var path = req.Url.AbsolutePath.ToLower();

            // Find matching instance based on symbol parameter
            string symbolParam = ParseString(req.Url.Query, "symbol", "");
            TradingViewBridge inst = FindInstance(symbolParam);

            // GET /bars?count=N&tf=1m|5m|...&offset=N&symbol=XXX
            if (path == "/bars")
            {
                if (inst == null)
                {
                    SendJson(res, "[]");
                    return;
                }

                int count   = ParseInt(req.Url.Query, "count", 1000);
                count       = Math.Min(count, 5000);
                int offset  = ParseInt(req.Url.Query, "offset", 0);
                offset      = Math.Max(0, offset);
                string tf   = ParseString(req.Url.Query, "tf", "1m");
                int bucket  = TfToSeconds(tf);

                var bars = new System.Text.StringBuilder();
                bars.Append("[");
                int end   = Math.Max(0, inst.CurrentBar - offset);
                int start = Math.Max(0, end - count + 1);

                if (bucket <= 60)
                {
                    bool first = true;
                    for (int i = start; i <= end; i++)
                    {
                        long t = ToUnixSeconds(inst.Times[0].GetValueAt(i));
                        double o = inst.Opens[0].GetValueAt(i);
                        double h = inst.Highs[0].GetValueAt(i);
                        double l = inst.Lows[0].GetValueAt(i);
                        double c = inst.Closes[0].GetValueAt(i);
                        double v = inst.Volumes[0].GetValueAt(i);
                        if (!first) bars.Append(",");
                        bars.Append(string.Format(CultureInfo.InvariantCulture,
                            "{{\"time\":{0},\"open\":{1},\"high\":{2},\"low\":{3},\"close\":{4},\"volume\":{5}}}",
                            t, o, h, l, c, v));
                        first = false;
                    }
                }
                else
                {
                    long curBucket = -1;
                    double bO = 0, bH = 0, bL = 0, bC = 0, bV = 0;
                    bool firstOut = true;

                    void Flush()
                    {
                        if (curBucket < 0) return;
                        if (!firstOut) bars.Append(",");
                        bars.Append(string.Format(CultureInfo.InvariantCulture,
                            "{{\"time\":{0},\"open\":{1},\"high\":{2},\"low\":{3},\"close\":{4},\"volume\":{5}}}",
                            curBucket, bO, bH, bL, bC, bV));
                        firstOut = false;
                    }

                    for (int i = start; i <= end; i++)
                    {
                        long t = ToUnixSeconds(inst.Times[0].GetValueAt(i));
                        long b = (t / bucket) * bucket;
                        double o = inst.Opens[0].GetValueAt(i);
                        double h = inst.Highs[0].GetValueAt(i);
                        double l = inst.Lows[0].GetValueAt(i);
                        double c = inst.Closes[0].GetValueAt(i);
                        double v = inst.Volumes[0].GetValueAt(i);

                        if (b != curBucket)
                        {
                            Flush();
                            curBucket = b;
                            bO = o; bH = h; bL = l; bC = c; bV = v;
                        }
                        else
                        {
                            if (h > bH) bH = h;
                            if (l < bL) bL = l;
                            bC = c;
                            bV += v;
                        }
                    }
                    Flush();
                }

                bars.Append("]");
                SendJson(res, bars.ToString());
                return;
            }

            // GET /instruments
            if (path == "/instruments")
            {
                var list = new List<string>();
                lock (_serverLock)
                {
                    foreach (var key in _instances.Keys)
                    {
                        list.Add(key);
                    }
                }
                var sb = new System.Text.StringBuilder();
                sb.Append("[");
                for (int i = 0; i < list.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    sb.Append(string.Format("\"{0}\"", list[i]));
                }
                sb.Append("]");
                SendJson(res, sb.ToString());
                return;
            }

            // GET /info
            if (path == "/info")
            {
                if (inst == null)
                {
                    lock (_serverLock)
                    {
                        foreach (var kv in _instances)
                        {
                            inst = kv.Value;
                            break;
                        }
                    }
                }

                if (inst == null)
                {
                    SendJson(res, "{\"error\":\"No active instances\"}");
                    return;
                }

                string json = string.Format(CultureInfo.InvariantCulture,
                    "{{\"instrument\":\"{0}\",\"timeframe\":\"{1}{2}\",\"bars\":{3}}}",
                    inst.Instrument.FullName, inst.BarsPeriod.Value, inst.BarsPeriod.BarsPeriodType, inst.CurrentBar + 1);
                SendJson(res, json);
                return;
            }

            // GET /time
            if (path == "/time")
            {
                int currentBarVal = inst != null ? inst.CurrentBar : -1;
                DateTime timeVal = (inst != null && currentBarVal >= 0) ? inst.Times[0].GetValueAt(currentBarVal) : DateTime.Now;
                long nowUtcUnix   = (long)(DateTime.UtcNow  - new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc)).TotalSeconds;
                long nowLocalUnix = ToUnixSeconds(DateTime.Now);
                long bar0Unix     = currentBarVal >= 0 ? ToUnixSeconds(timeVal) : 0;
                string json = string.Format(CultureInfo.InvariantCulture,
                    "{{\"utcNow\":{0},\"localNow\":{1},\"bar0\":{2},\"machineLocalTime\":\"{3}\",\"bar0RawTime\":\"{4}\"}}",
                    nowUtcUnix, nowLocalUnix, bar0Unix,
                    DateTime.Now.ToString("HH:mm:ss"),
                    currentBarVal >= 0 ? timeVal.ToString("HH:mm:ss") : "n/a");
                SendJson(res, json);
                return;
            }

            // POST /order
            if (path == "/order" && req.HttpMethod == "POST")
            {
                string body = new System.IO.StreamReader(req.InputStream).ReadToEnd();
                try
                {
                    string action     = ParseJsonString(body, "action");
                    int    qty        = ParseJsonInt(body, "qty", 1);
                    string instrName  = ParseJsonString(body, "instrument");
                    if (string.IsNullOrEmpty(instrName))
                        instrName = inst != null ? inst.Instrument.FullName : "";

                    if (string.IsNullOrEmpty(instrName))
                        throw new Exception("No active instrument");

                    OrderAction orderAction = action.ToUpper() == "SELL"
                        ? OrderAction.Sell
                        : OrderAction.Buy;

                    var targetInst = inst ?? FindInstance(instrName);
                    if (targetInst == null)
                    {
                        lock (_serverLock)
                        {
                            foreach (var kv in _instances)
                            {
                                targetInst = kv.Value;
                                break;
                            }
                        }
                    }

                    if (targetInst == null)
                        throw new Exception("No active TradingViewBridge indicator instances found to route order");

                    Order o = targetInst.EnterOrder(orderAction, qty);
                    string orderId = o != null ? o.OrderId.ToString() : "submitted";
                    LogToFile(string.Format("/order {0} {1} {2} → {3}", action, qty, instrName, orderId));
                    SendJson(res, string.Format("{{\"orderId\":\"{0}\",\"status\":\"Accepted\"}}", orderId));
                }
                catch (Exception ex)
                {
                    res.StatusCode = 500;
                    SendJson(res, string.Format("{{\"error\":\"{0}\"}}", ex.Message.Replace("\"", "'")));
                }
                return;
            }

            // POST /flatten
            if (path == "/flatten" && req.HttpMethod == "POST")
            {
                try
                {
                    if (inst == null)
                        throw new Exception("No active instance found for symbol to flatten");
                    inst.FlattenAll();
                    LogToFile(string.Format("/flatten executed for {0}", inst.Instrument.FullName));
                    SendJson(res, "{\"status\":\"flattened\"}");
                }
                catch (Exception ex)
                {
                    res.StatusCode = 500;
                    SendJson(res, string.Format("{{\"error\":\"{0}\"}}", ex.Message.Replace("\"", "'")));
                }
                return;
            }

            // GET /account
            if (path == "/account")
            {
                try
                {
                    double unrealized = 0;
                    double realized   = 0;
                    int    netPos     = 0;

                    if (inst == null)
                    {
                        lock (_serverLock)
                        {
                            foreach (var kv in _instances)
                            {
                                inst = kv.Value;
                                break;
                            }
                        }
                    }

                    if (inst != null)
                    {
                        var acct = inst.Account;
                        if (acct != null)
                        {
                            unrealized = acct.Get(AccountItem.UnrealizedProfitLoss, Currency.UsDollar);
                            realized   = acct.Get(AccountItem.RealizedProfitLoss,   Currency.UsDollar);
                            foreach (var pos in acct.Positions)
                            {
                                if (pos.Instrument == inst.Instrument)
                                    netPos += (int)(pos.MarketPosition == MarketPosition.Long ? pos.Quantity : -pos.Quantity);
                            }
                        }
                    }

                    string json = string.Format(CultureInfo.InvariantCulture,
                        "{{\"unrealizedPnl\":{0},\"realizedPnl\":{1},\"position\":{2}}}",
                        unrealized, realized, netPos);
                    SendJson(res, json);
                }
                catch (Exception ex)
                {
                    res.StatusCode = 500;
                    SendJson(res, string.Format("{{\"error\":\"{0}\"}}", ex.Message.Replace("\"", "'")));
                }
                return;
            }

            res.StatusCode = 404;
            res.Close();
        }

        // ── Order helpers ───────────────────────────────────────────────

        private Order EnterOrder(OrderAction action, int qty)
        {
            return SubmitOrderUnmanaged(0, action, OrderType.Market, 0, 0, qty, "TradingViewBridge");
        }

        private void FlattenAll()
        {
            if (Account != null)
            {
                Account.Flatten(new[] { Instrument });
            }
        }

        // ── WebSocket client handler ────────────────────────────────────

        private async Task HandleWebSocket(WebSocket ws)
        {
            lock (_serverLock) _clients.Add(ws);
            LogToFile(string.Format("Cliente conectado al WS compartido. Total: {0}", _clients.Count));

            var buf = new byte[256];
            try
            {
                while (ws.State == WebSocketState.Open && !_cts.Token.IsCancellationRequested)
                {
                    var result = await ws.ReceiveAsync(new ArraySegment<byte>(buf), _cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                        break;
                }
            }
            catch { }
            finally
            {
                lock (_serverLock) _clients.Remove(ws);
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
                LogToFile(string.Format("Cliente desconectado del WS compartido. Total: {0}", _clients.Count));
            }
        }

        // ── Bar + tick broadcast ────────────────────────────────────────

        protected override void OnBarUpdate()
        {
            if (CurrentBar < 1) return;
            long t = ToUnixSeconds(Time[0]);

            if (IsFirstTickOfBar)
            {
                long tPrev = ToUnixSeconds(Time[1]);
                string closed = string.Format(CultureInfo.InvariantCulture,
                    "{{\"type\":\"bar_close\",\"symbol\":\"{0}\",\"time\":{1},\"open\":{2},\"high\":{3},\"low\":{4},\"close\":{5},\"volume\":{6}}}",
                    Instrument.FullName, tPrev, Open[1], High[1], Low[1], Close[1], Volume[1]);
                Broadcast(closed);
            }

            string msg = string.Format(CultureInfo.InvariantCulture,
                "{{\"type\":\"bar\",\"symbol\":\"{0}\",\"time\":{1},\"open\":{2},\"high\":{3},\"low\":{4},\"close\":{5},\"volume\":{6}}}",
                Instrument.FullName, t, Open[0], High[0], Low[0], Close[0], Volume[0]);
            Broadcast(msg);
        }

        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (e.MarketDataType != MarketDataType.Last) return;
            long t = ToUnixSeconds(e.Time);
            string msg = string.Format(CultureInfo.InvariantCulture,
                "{{\"type\":\"tick\",\"symbol\":\"{0}\",\"price\":{1},\"volume\":{2},\"time\":{3}}}",
                Instrument.FullName, e.Price, e.Volume, t);
            Broadcast(msg);
        }

        private static void Broadcast(string message)
        {
            var bytes = Encoding.UTF8.GetBytes(message);
            var segment = new ArraySegment<byte>(bytes);
            lock (_serverLock)
            {
                foreach (var ws in _clients.ToArray())
                {
                    if (ws.State == WebSocketState.Open)
                        ws.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None);
                }
            }
        }

        // ── Helpers ─────────────────────────────────────────────────────

        private static TradingViewBridge FindInstance(string symbol)
        {
            if (string.IsNullOrEmpty(symbol)) return null;
            string search = symbol.Split('=')[0].Split('-')[0].ToUpper();
            lock (_serverLock)
            {
                foreach (var kv in _instances)
                {
                    if (kv.Key.StartsWith(search))
                        return kv.Value;
                }
            }
            return null;
        }

        private static long ToUnixSeconds(DateTime dt)
        {
            DateTime utc;
            if (dt.Kind == DateTimeKind.Utc)
                utc = dt;
            else if (dt.Kind == DateTimeKind.Local)
                utc = dt.ToUniversalTime();
            else
                utc = DateTime.SpecifyKind(dt, DateTimeKind.Local).ToUniversalTime();
            return (long)(utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }

        private static string ParseString(string query, string key, string def)
        {
            if (string.IsNullOrEmpty(query) || query.Length < 2) return def;
            foreach (var pair in query.Substring(1).Split('&'))
            {
                var kv = pair.Split('=');
                if (kv.Length == 2 && kv[0] == key) return kv[1];
            }
            return def;
        }

        private static int TfToSeconds(string tf)
        {
            switch (tf)
            {
                case "1m":  return 60;
                case "5m":  return 300;
                case "15m": return 900;
                case "30m": return 1800;
                case "1h":  return 3600;
                case "1d":  return 86400;
                case "1w":  return 604800;
                default:    return 60;
            }
        }

        private static int ParseInt(string query, string key, int def)
        {
            if (string.IsNullOrEmpty(query) || query.Length < 2) return def;
            foreach (var pair in query.Substring(1).Split('&'))
            {
                var kv = pair.Split('=');
                if (kv.Length == 2 && kv[0] == key && int.TryParse(kv[1], out int v))
                    return v;
            }
            return def;
        }

        private static string ParseJsonString(string json, string key)
        {
            string search = "\"" + key + "\"";
            int ki = json.IndexOf(search);
            if (ki < 0) return "";
            int colon = json.IndexOf(':', ki + search.Length);
            if (colon < 0) return "";
            int q1 = json.IndexOf('"', colon + 1);
            if (q1 < 0) return "";
            int q2 = json.IndexOf('"', q1 + 1);
            if (q2 < 0) return "";
            return json.Substring(q1 + 1, q2 - q1 - 1);
        }

        private static int ParseJsonInt(string json, string key, int def)
        {
            string search = "\"" + key + "\"";
            int ki = json.IndexOf(search);
            if (ki < 0) return def;
            int colon = json.IndexOf(':', ki + search.Length);
            if (colon < 0) return def;
            int start = colon + 1;
            while (start < json.Length && (json[start] == ' ' || json[start] == '\t')) start++;
            int end = start;
            while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '-')) end++;
            return int.TryParse(json.Substring(start, end - start), out int v) ? v : def;
        }

        private static void SendJson(HttpListenerResponse res, string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            res.ContentType     = "application/json";
            res.ContentEncoding = Encoding.UTF8;
            res.ContentLength64 = bytes.Length;
            res.OutputStream.Write(bytes, 0, bytes.Length);
            res.OutputStream.Close();
        }

        private static void LogToFile(string message)
        {
            try
            {
                string path = @"C:\Users\yoban\OneDrive\voiptocall\NinjaTrader 8\TradingViewBridge_debug.log";
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                System.IO.File.AppendAllText(path, string.Format("[{0}] {1}\r\n", timestamp, message));
            }
            catch { }
        }
    }
}
