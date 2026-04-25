import os, time, httpx
from fastapi import FastAPI, HTTPException, Request

app = FastAPI()

TV_SECRET   = os.getenv("TV_SECRET")
TV_USER     = os.getenv("TRADOVATE_USER")
TV_PASS     = os.getenv("TRADOVATE_PASS")
APP_ID      = os.getenv("TRADOVATE_APP_ID")
APP_SECRET  = os.getenv("TRADOVATE_APP_SECRET")
DEVICE_ID   = os.getenv("TRADOVATE_DEVICE_ID")
ACCOUNT_ID  = int(os.getenv("TRADOVATE_ACCOUNT_ID", "0"))
SYMBOL      = os.getenv("SYMBOL", "GCM5")
BASE        = os.getenv("TRADOVATE_BASE", "https://live.tradovateapi.com/v1")

state = {"in_trade": False, "token": None, "expires": 0}


async def auth():
    if state["token"] and time.time() < state["expires"] - 60:
        return state["token"]
    payload = {
        "name": TV_USER, "password": TV_PASS,
        "appId": APP_ID, "appVersion": "1.0",
        "cid": APP_SECRET, "sec": APP_SECRET,
        "deviceId": DEVICE_ID,
    }
    async with httpx.AsyncClient(timeout=15) as c:
        r = await c.post(f"{BASE}/auth/accesstokenrequest", json=payload)
        r.raise_for_status()
        d = r.json()
        if "accessToken" not in d:
            raise HTTPException(502, f"Auth failed: {d}")
        state["token"] = d["accessToken"]
        state["expires"] = time.time() + int(d.get("expirationTime", 4800))
        return state["token"]


async def place_bracket(side: str, qty: int, entry: float, sl: float, tp: float):
    token = await auth()
    headers = {"Authorization": f"Bearer {token}"}
    body = {
        "accountSpec": TV_USER,
        "accountId": ACCOUNT_ID,
        "action": side,                       # "Buy" | "Sell"
        "symbol": SYMBOL,
        "orderQty": qty,
        "orderType": "Market",
        "isAutomated": True,
        "bracket": {
            "profitTarget": {"action": "Sell" if side == "Buy" else "Buy",
                             "orderType": "Limit", "price": tp},
            "stopLoss":     {"action": "Sell" if side == "Buy" else "Buy",
                             "orderType": "Stop",  "stopPrice": sl},
        },
    }
    async with httpx.AsyncClient(timeout=15) as c:
        r = await c.post(f"{BASE}/order/placeoso", headers=headers, json=body)
        r.raise_for_status()
        return r.json()


@app.post("/webhook")
async def webhook(req: Request):
    data = await req.json()
    if data.get("secret") != TV_SECRET:
        raise HTTPException(401, "bad secret")
    if state["in_trade"]:
        return {"status": "ignored", "reason": "already in trade"}
    try:
        result = await place_bracket(
            side=data["side"],
            qty=int(data.get("qty", 1)),
            entry=float(data["entry"]),
            sl=float(data["sl"]),
            tp=float(data["tp"]),
        )
        state["in_trade"] = True
        return {"status": "filled", "tradovate": result}
    except httpx.HTTPStatusError as e:
        raise HTTPException(502, f"Tradovate error: {e.response.text}")


@app.post("/reset")
async def reset(req: Request):
    data = await req.json()
    if data.get("secret") != TV_SECRET:
        raise HTTPException(401, "bad secret")
    state["in_trade"] = False
    return {"status": "reset"}


@app.post("/close")
async def close(req: Request):
    data = await req.json()
    if data.get("secret") != TV_SECRET:
        raise HTTPException(401, "bad secret")
    token = await auth()
    headers = {"Authorization": f"Bearer {token}"}
    async with httpx.AsyncClient(timeout=15) as c:
        r = await c.post(f"{BASE}/order/liquidatePosition",
                         headers=headers,
                         json={"accountId": ACCOUNT_ID, "symbol": SYMBOL,
                               "admin": False})
    state["in_trade"] = False
    return {"status": "closed", "tradovate": r.json()}


@app.get("/")
def root():
    return {"ok": True, "in_trade": state["in_trade"], "symbol": SYMBOL}
