// Test sprzątania zerwanych połączeń. Serwer musi działać ze skróconymi czasami:
//   wrangler dev --port 8788 --var STALE_MS:4000 --var SWEEP_MS:1500      i potem:   BASE_URL=http://127.0.0.1:8788 node test/stale.mjs
import { readFileSync } from "node:fs";
const BASE = (process.env.BASE_URL || "http://127.0.0.1:8788").replace(/^http/, "ws") + "/ws";
const keys = JSON.parse(readFileSync("test/keys.json", "utf8"));
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
let failed = 0;
const check = (c, m) => { console.log((c ? "ok:   " : "FAIL: ") + m); if (!c) failed++; };
const open = async (id) => {
  const ws = new WebSocket(BASE, { headers: { Authorization: "Bearer " + keys[id] } });
  const c = { id, ws, q: [] };
  await new Promise((r, j) => { ws.onopen = r; ws.onerror = j; });
  ws.onmessage = (e) => { if (e.data !== "pong") c.q.push(JSON.parse(String(e.data))); };
  ws.onclose = (e) => { c.closed = e.code; };
  return c;
};
const last = (c, t) => [...c.q].reverse().find((m) => m.t === t);

const ala = await open("ala");
const bartek = await open("bartek");
await sleep(500);
check(last(ala, "presence")?.online.bartek === 1, "Ala widzi Bartka online");

// Bartek "zamarza": nie pinguje i nie zamyka połączenia. Ala pinguje, żeby jej samej nie uznano za zerwaną.
for (let i = 0; i < 8; i++) { ala.ws.send("ping"); await sleep(1000); }
const p = last(ala, "presence");
check(p && p.online.bartek === undefined, "Bartek bez życia przez STALE_MS znika z listy obecnych");
check(p && p.online.ala === 1, "Ala (która pinguje) nadal jest online");
await sleep(3000);
check(bartek.ws.readyState >= 2, "zamarznięte połączenie jest zamykane przez serwer (readyState=" + bartek.ws.readyState + ")");
ala.ws.close();
process.exit(failed ? 1 : 0);
