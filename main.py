import os, time, json, asyncio, httpx
from dotenv import load_dotenv
from fastapi import FastAPI, HTTPException, Request

load_dotenv()
app = FastAPI()

TV_SECRET = os.getenv("TV_SECRET")
SYMBOL    = os.getenv("SYMBOL", "GCM5")
BASE      = os.getenv("TRADOVATE_BASE", "https://live.tradovateapi.com/v1")

# Accounts loaded from TRADOVATE_ACCOUNTS env (JSON array).
# Each entry: {"name","user","pass","app_id","app_secret","device_id","account_id"}
try:
    ACCOUNTS = json.loads(os.getenv("TRADOVATE_ACCOUNTS", "[]"))
except json.JSONDecodeError as e:
    raise RuntimeError(f"TRADOVATE_ACCOUNTS not valid JSON: {e}")

if not ACCOUNTS:
    raise RuntimeError("TRADOVATE_ACCOUNTS is empty — add at least one account.")

# Per-account in-memory state
state = {a["name"]: {"in_trade": False, "token": None, "expires": 0} for a in ACCOUNTS}


async def auth(acc: dict) -> str:
    s = state[acc["name"]]
    if s["token"] and time.time() < s["expires"] - 60:
        return s["token"]
    payload = {
        "name": acc["user"], "password": acc["pass"],
        "appId": acc["app_id"], "appVersion": "1.0",
        "cid": acc["app_secret"], "sec": acc["app_secret"],
        "deviceId": acc["device_id"],
    }
    async with httpx.AsyncClient(timeout=15) as c:
        r = await c.post(f"{BASE}/auth/accesstokenrequest", json=payload)
        r.raise_for_status()
        d = r.json()
        if "accessToken" not in d:
            raise HTTPException(502, f"Auth failed for {acc['name']}: {d}")
        s["token"] = d["accessToken"]
        s["expires"] = time.time() + int(d.get("expirationTime", 4800))
        return s["token"]


async def place_bracket(acc: dict, side: str, qty: int, sl: float, tp: float):
    token = await auth(acc)
    headers = {"Authorization": f"Bearer {token}"}
    opp = "Sell" if side == "Buy" else "Buy"
    body = {
        "accountSpec": acc["user"],
        "accountId": int(acc["account_id"]),
        "action": side,
        "symbol": SYMBOL,
        "orderQty": qty,
        "orderType": "Market",
        "isAutomated": True,
        "bracket": {
            "profitTarget": {"action": opp, "orderType": "Limit", "price": tp},
            "stopLoss":     {"action": opp, "orderType": "Stop",  "stopPrice": sl},
        },
    }
    async with httpx.AsyncClient(timeout=15) as c:
        r = await c.post(f"{BASE}/order/placeoso", headers=headers, json=body)
        r.raise_for_status()
        return r.json()


async def fan_out(side, qty, sl, tp):
    """Fire to every account that's not already in a trade. Concurrent."""
    targets = [a for a in ACCOUNTS if not state[a["name"]]["in_trade"]]
    if not targets:
        return {"status": "ignored", "reason": "all accounts in trade"}

    async def one(a):
        try:
            res = await place_bracket(a, side, qty, sl, tp)
            state[a["name"]]["in_trade"] = True
            return {"account": a["name"], "status": "filled", "result": res}
        except httpx.HTTPStatusError as e:
            return {"account": a["name"], "status": "error", "error": e.response.text}
        except Exception as e:
            return {"account": a["name"], "status": "error", "error": str(e)}

    results = await asyncio.gather(*[one(a) for a in targets])
    return {"status": "ok", "results": results}


@app.post("/webhook")
async def webhook(req: Request):
    data = await req.json()
    if data.get("secret") != TV_SECRET:
        raise HTTPException(401, "bad secret")
    return await fan_out(
        side=data["side"],
        qty=int(data.get("qty", 1)),
        sl=float(data["sl"]),
        tp=float(data["tp"]),
    )


@app.post("/reset")
async def reset(req: Request):
    data = await req.json()
    if data.get("secret") != TV_SECRET:
        raise HTTPException(401, "bad secret")
    target = data.get("account")  # optional: reset just one
    for name, s in state.items():
        if target is None or name == target:
            s["in_trade"] = False
    return {"status": "reset", "target": target or "all"}


@app.post("/close")
async def close(req: Request):
    data = await req.json()
    if data.get("secret") != TV_SECRET:
        raise HTTPException(401, "bad secret")
    target = data.get("account")
    targets = [a for a in ACCOUNTS if target is None or a["name"] == target]

    async def one(a):
        token = await auth(a)
        headers = {"Authorization": f"Bearer {token}"}
        async with httpx.AsyncClient(timeout=15) as c:
            r = await c.post(f"{BASE}/order/liquidatePosition",
                             headers=headers,
                             json={"accountId": int(a["account_id"]),
                                   "symbol": SYMBOL, "admin": False})
        state[a["name"]]["in_trade"] = False
        return {"account": a["name"], "result": r.json()}

    results = await asyncio.gather(*[one(a) for a in targets])
    return {"status": "closed", "results": results}


@app.get("/")
def root():
    return {
        "ok": True,
        "symbol": SYMBOL,
        "accounts": [{"name": a["name"], "in_trade": state[a["name"]]["in_trade"]}
                     for a in ACCOUNTS],
    }
