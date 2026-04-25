#region Using declarations
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
#endregion

// Drop this file in:
//   Documents\NinjaTrader 8\bin\Custom\AddOns\TVWebhookAddon.cs
// Then in NinjaTrader: Tools -> Edit NinjaScript -> AddOn -> compile (F5).
// Restart NT. Menu: New -> TV Webhook Listener.

namespace NinjaTrader.NinjaScript.AddOns
{
    public class TVWebhookAddon : AddOnBase
    {
        // ============ CONFIG ============
        // All settings live in:
        //   Documents\NinjaTrader 8\bin\Custom\AddOns\tvwebhook.json
        // Edit that file and click "Reload Config" — no recompile needed.
        private static readonly string CONFIG_PATH = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "NinjaTrader 8", "bin", "Custom", "AddOns", "tvwebhook.json");
        // =================================

        private class AccountCfg
        {
            public string Name { get; set; }
            public int    QtyMultiplier { get; set; }
            public Account Acct;
            public bool InTrade;
        }

        private class Config
        {
            public string ListenPrefix { get; set; } = "http://+:5005/";
            public string Secret { get; set; } = "change_me";
            public string DefaultInstrument { get; set; } = "GC 06-26";
            public List<AccountCfg> Accounts { get; set; } = new List<AccountCfg>();
        }

        private Config cfg = new Config();

        private void LoadConfig()
        {
            if (!File.Exists(CONFIG_PATH))
            {
                // First run: write a template the user can edit.
                var template = "{\n"
                    + "  \"ListenPrefix\": \"http://+:5005/\",\n"
                    + "  \"Secret\": \"wickflow_change_me\",\n"
                    + "  \"DefaultInstrument\": \"GC 06-26\",\n"
                    + "  \"Accounts\": [\n"
                    + "    { \"Name\": \"Sim101\",          \"QtyMultiplier\": 1 },\n"
                    + "    { \"Name\": \"Lucid-Funded-1\", \"QtyMultiplier\": 1 },\n"
                    + "    { \"Name\": \"Lucid-Eval-1\",    \"QtyMultiplier\": 1 }\n"
                    + "  ]\n"
                    + "}\n";
                File.WriteAllText(CONFIG_PATH, template);
                NinjaTrader.Code.Output.Process(
                    "[TVWebhook] wrote template config to " + CONFIG_PATH,
                    PrintTo.OutputTab1);
            }

            var raw = File.ReadAllText(CONFIG_PATH);
            cfg = new JavaScriptSerializer().Deserialize<Config>(raw);
            NinjaTrader.Code.Output.Process(
                "[TVWebhook] loaded " + cfg.Accounts.Count + " account(s) from config",
                PrintTo.OutputTab1);
        }

        private HttpListener listener;
        private CancellationTokenSource cts;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "TV Webhook Listener";
                Description = "Receives TradingView webhooks and fans out to multiple accounts.";
            }
            else if (State == State.Active)
            {
                Start();
            }
            else if (State == State.Terminated)
            {
                Stop();
            }
        }

        private void Start()
        {
            LoadConfig();
            foreach (var c in cfg.Accounts)
            {
                c.Acct = Account.All.FirstOrDefault(a => a.Name == c.Name);
                if (c.Acct == null)
                    NinjaTrader.Code.Output.Process(
                        "[TVWebhook] account not found: " + c.Name, PrintTo.OutputTab1);
            }

            cts = new CancellationTokenSource();
            listener = new HttpListener();
            listener.Prefixes.Add(cfg.ListenPrefix);
            try { listener.Start(); }
            catch (Exception e)
            {
                NinjaTrader.Code.Output.Process(
                    "[TVWebhook] listener failed: " + e.Message, PrintTo.OutputTab1);
                return;
            }

            Task.Run(() => Loop(cts.Token));
            NinjaTrader.Code.Output.Process(
                "[TVWebhook] listening on " + cfg.ListenPrefix, PrintTo.OutputTab1);
        }

        private void Stop()
        {
            try
            {
                if (cts != null) cts.Cancel();
                if (listener != null) listener.Stop();
            }
            catch { }
        }

        private async Task Loop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var ctx = await listener.GetContextAsync();
                    Task.Run(() => Handle(ctx));
                }
                catch (Exception e)
                {
                    if (!token.IsCancellationRequested)
                        NinjaTrader.Code.Output.Process(
                            "[TVWebhook] loop err: " + e.Message, PrintTo.OutputTab1);
                }
            }
        }

        private void Handle(HttpListenerContext ctx)
        {
            string body;
            using (var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                body = sr.ReadToEnd();

            try
            {
                var json = new JavaScriptSerializer()
                    .Deserialize<Dictionary<string, object>>(body);

                if (!json.ContainsKey("secret") || (string)json["secret"] != cfg.Secret)
                { Reply(ctx, 401, "{\"error\":\"bad secret\"}"); return; }

                // Allow runtime config reload via webhook
                if (json.ContainsKey("action") && (string)json["action"] == "reload")
                {
                    LoadConfig();
                    foreach (var c in cfg.Accounts)
                        c.Acct = Account.All.FirstOrDefault(a => a.Name == c.Name);
                    Reply(ctx, 200, "{\"status\":\"reloaded\"}");
                    return;
                }

                string side   = (string)json["side"];                 // "Buy" | "Sell"
                int    qty    = Convert.ToInt32(json.ContainsKey("qty") ? json["qty"] : 1);
                double sl     = Convert.ToDouble(json["sl"]);
                double tp     = Convert.ToDouble(json["tp"]);
                string symbol = json.ContainsKey("symbol")
                    ? (string)json["symbol"] : cfg.DefaultInstrument;

                Place(symbol, side, qty, sl, tp);
                Reply(ctx, 200, "{\"status\":\"ok\"}");
            }
            catch (Exception e)
            {
                Reply(ctx, 400, "{\"error\":\"" + e.Message + "\"}");
            }
        }

        private void Place(string symbol, string side, int qty, double sl, double tp)
        {
            var instrument = Instrument.GetInstrument(symbol);
            if (instrument == null)
            {
                NinjaTrader.Code.Output.Process(
                    "[TVWebhook] unknown instrument: " + symbol, PrintTo.OutputTab1);
                return;
            }

            var action = side.Equals("Buy", StringComparison.OrdinalIgnoreCase)
                ? OrderAction.Buy : OrderAction.Sell;
            var opp    = action == OrderAction.Buy ? OrderAction.Sell : OrderAction.Buy;

            foreach (var c in cfg.Accounts)
            {
                if (c.Acct == null) continue;
                if (c.InTrade)
                {
                    NinjaTrader.Code.Output.Process(
                        "[TVWebhook] " + c.Name + " skipped (in trade)", PrintTo.OutputTab1);
                    continue;
                }

                int scaled = qty * c.QtyMultiplier;
                NinjaTrader.Code.Output.Process(
                    "[TVWebhook] " + c.Name + " " + side + " " + scaled
                    + " sl=" + sl + " tp=" + tp, PrintTo.OutputTab1);

                var entry = c.Acct.CreateOrder(
                    instrument, action, OrderType.Market, OrderEntry.Automated,
                    TimeInForce.Day, scaled, 0, 0, "", "TV_entry", DateTime.MaxValue, null);

                var slOrd = c.Acct.CreateOrder(
                    instrument, opp, OrderType.StopMarket, OrderEntry.Automated,
                    TimeInForce.Day, scaled, 0, sl, entry.Oco, "TV_sl", DateTime.MaxValue, null);

                var tpOrd = c.Acct.CreateOrder(
                    instrument, opp, OrderType.Limit, OrderEntry.Automated,
                    TimeInForce.Day, scaled, tp, 0, entry.Oco, "TV_tp", DateTime.MaxValue, null);

                c.Acct.Submit(new[] { entry, slOrd, tpOrd });
                c.InTrade = true;

                // Auto-clear when position is flat
                Account ca = c.Acct;
                AccountCfg accCfg = c;
                EventHandler<OrderEventArgs> handler = null;
                handler = (s, e) =>
                {
                    if (e.Order.OrderState == OrderState.Filled
                        || e.Order.OrderState == OrderState.Cancelled
                        || e.Order.OrderState == OrderState.Rejected)
                    {
                        var pos = ca.Positions.FirstOrDefault(p => p.Instrument == instrument);
                        if (pos == null || pos.Quantity == 0)
                        {
                            accCfg.InTrade = false;
                            ca.OrderUpdate -= handler;
                        }
                    }
                };
                ca.OrderUpdate += handler;
            }
        }

        private void Reply(HttpListenerContext ctx, int code, string body)
        {
            try
            {
                var b = Encoding.UTF8.GetBytes(body);
                ctx.Response.StatusCode = code;
                ctx.Response.ContentType = "application/json";
                ctx.Response.OutputStream.Write(b, 0, b.Length);
                ctx.Response.OutputStream.Close();
            }
            catch { }
        }
    }
}
