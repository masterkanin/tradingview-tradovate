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
    public class TVWebhookAddon : NTWindow, NinjaTrader.Gui.Tools.IInstantiatedOnLoad
    {
        // ============ CONFIG ============
        private const string LISTEN_PREFIX = "http://+:5005/";   // run NT as admin to bind
        private const string SECRET        = "wickflow_change_me";
        private const string INSTRUMENT    = "GC 06-26";         // NT format, not GCM5

        // Add one entry per account you want to copy to.
        // qtyMultiplier scales the qty from the TV alert (e.g. 2 = double size).
        private static readonly List<AccountCfg> ACCOUNTS = new List<AccountCfg>
        {
            new AccountCfg { Name = "Lucid-Funded-1", QtyMultiplier = 1 },
            new AccountCfg { Name = "Lucid-Eval-1",   QtyMultiplier = 1 },
            // new AccountCfg { Name = "Lucid-Funded-2", QtyMultiplier = 2 },
        };
        // =================================

        private class AccountCfg
        {
            public string Name;
            public int    QtyMultiplier;
            public Account Acct;
            public bool InTrade;
        }

        private HttpListener listener;
        private CancellationTokenSource cts;
        private Instrument instrument;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Caption = "TV Webhook Listener";
                Width   = 380;
                Height  = 120;
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
            foreach (var c in ACCOUNTS)
            {
                c.Acct = Account.All.FirstOrDefault(a => a.Name == c.Name);
                if (c.Acct == null)
                    NinjaTrader.Code.Output.Process(
                        "[TVWebhook] account not found: " + c.Name, PrintTo.OutputTab1);
            }
            instrument = Instrument.GetInstrument(INSTRUMENT);

            cts = new CancellationTokenSource();
            listener = new HttpListener();
            listener.Prefixes.Add(LISTEN_PREFIX);
            try { listener.Start(); }
            catch (Exception e)
            {
                NinjaTrader.Code.Output.Process(
                    "[TVWebhook] listener failed: " + e.Message, PrintTo.OutputTab1);
                return;
            }

            Task.Run(() => Loop(cts.Token));
            NinjaTrader.Code.Output.Process(
                "[TVWebhook] listening on " + LISTEN_PREFIX, PrintTo.OutputTab1);
        }

        private void Stop()
        {
            try { cts?.Cancel(); listener?.Stop(); } catch { }
        }

        private async Task Loop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var ctx = await listener.GetContextAsync();
                    _ = Task.Run(() => Handle(ctx));
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

                if (!json.ContainsKey("secret") || (string)json["secret"] != SECRET)
                { Reply(ctx, 401, "{\"error\":\"bad secret\"}"); return; }

                string side = (string)json["side"];                 // "Buy" | "Sell"
                int    qty  = Convert.ToInt32(json.ContainsKey("qty") ? json["qty"] : 1);
                double sl   = Convert.ToDouble(json["sl"]);
                double tp   = Convert.ToDouble(json["tp"]);

                Place(side, qty, sl, tp);
                Reply(ctx, 200, "{\"status\":\"ok\"}");
            }
            catch (Exception e)
            {
                Reply(ctx, 400, "{\"error\":\"" + e.Message + "\"}");
            }
        }

        private void Place(string side, int qty, double sl, double tp)
        {
            var action = side.Equals("Buy", StringComparison.OrdinalIgnoreCase)
                ? OrderAction.Buy : OrderAction.Sell;
            var opp    = action == OrderAction.Buy ? OrderAction.Sell : OrderAction.Buy;

            foreach (var c in ACCOUNTS)
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

                // Auto-clear when entry + brackets all complete
                Account ca = c.Acct; AccountCfg cfg = c;
                EventHandler<OrderEventArgs> handler = null;
                handler = (s, e) =>
                {
                    if (e.Order.OrderState == OrderState.Filled
                        || e.Order.OrderState == OrderState.Cancelled
                        || e.Order.OrderState == OrderState.Rejected)
                    {
                        if (ca.Positions.FirstOrDefault(p =>
                                p.Instrument == instrument)?.Quantity == 0)
                        {
                            cfg.InTrade = false;
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
