// Test protokołu serwera. Najpierw uruchom serwer (npm run dev), potem:  node test/protocol.mjs
//   WS_URL=ws://127.0.0.1:8787   (adres serwera, bez /ws)   KEYS=test/keys.json
// Klucze testowe powstają w node test/make-dev-vars.mjs.
import { readFileSync } from "node:fs";

const BASE = (process.env.BASE_URL || "http://127.0.0.1:8787").replace(/\/$/, "");
const WS = BASE.replace(/^http/, "ws") + "/ws";
const keys = JSON.parse(readFileSync(process.env.KEYS || "test/keys.json", "utf8"));
let failed = 0;
const check = (cond, msg) => { console.log((cond ? "ok:   " : "FAIL: ") + msg); if (!cond) failed++; };
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const rnd = () => "c" + Math.random().toString(36).slice(2, 12).padEnd(10, "x");

class Client {
  constructor(id, key = keys[id]) { this.id = id; this.q = []; this.key = key; }
  async open() {
    this.ws = new WebSocket(WS, { headers: { Authorization: "Bearer " + this.key } });
    await new Promise((res, rej) => { this.ws.onopen = res; this.ws.onerror = () => rej(new Error("nie można połączyć")); });
    this.ws.onmessage = (e) => { if (e.data !== "pong") this.q.push(JSON.parse(String(e.data))); else this.pong = true; };
    this.ws.onclose = (e) => { this.closed = e.code; };
    return this;
  }
  send(o) { this.ws.send(typeof o === "string" ? o : JSON.stringify(o)); }
  async next(t, ms = 2000, pred = () => true) {
    const t0 = Date.now();
    for (;;) {
      const i = this.q.findIndex((m) => m.t === t && pred(m));
      if (i >= 0) return this.q.splice(i, 1)[0];
      if (Date.now() - t0 > ms) return null;
      await sleep(15);
    }
  }
  async none(t, ms = 400) { await sleep(ms); return !this.q.some((m) => m.t === t); }
  close() { try { this.ws.close(); } catch { /* */ } }
}

// ---- HTTP: dostęp
let r = await fetch(BASE + "/");
check(r.status === 200, "GET / odpowiada bez klucza");
r = await fetch(BASE + "/api/status");
check(r.status === 401, "GET /api/status bez klucza: 401");
r = await fetch(BASE + "/api/status", { headers: { Authorization: "Bearer " + "x".repeat(43) } });
check(r.status === 401, "zły klucz: 401");
r = await fetch(BASE + "/api/status", { headers: { Authorization: "Bearer " + keys.ala } });
const st = await r.json();
check(r.status === 200 && st.id === "ala" && st.proto === 1, "dobry klucz: status z id osoby");
check((await fetch(BASE + "/cokolwiek")).status === 404, "nieznany adres: 404");
let bad = false;
try { const c = new Client("zly", "y".repeat(43)); await c.open(); } catch { bad = true; }
check(bad, "WebSocket ze złym kluczem jest odrzucany");

// ---- obecność
const ala = await new Client("ala").open();
const w = await ala.next("welcome");
check(w && w.me.id === "ala" && w.directory.length === 3, "welcome: moje id i katalog osób");
check(w.online.ala === 1, "jestem na liście obecnych");
const bartek = await new Client("bartek").open();
await bartek.next("welcome");
const p = await ala.next("presence", 2000, (m) => m.online.bartek === 1);
check(p !== null, "Ala widzi, że Bartek jest online");
ala.send("ping");
await sleep(300);
check(ala.pong === true, "auto-odpowiedź ping -> pong działa");

// ---- dzwonienie: odbiera
let cid = rnd();
ala.send({ t: "call", cid, to: "bartek", video: true });
const ring = await bartek.next("ring");
check(ring && ring.cid === cid && ring.from === "ala" && ring.video === true, "Bartek dostaje ring od Ali");
check((await ala.next("ringing"))?.cid === cid, "Ala dostaje potwierdzenie dzwonienia");
check((await ala.next("presence", 1500, (m) => m.online.ala === 2 && m.online.bartek === 2)) !== null, "obie osoby są 'rozmawia'");

const celina = await new Client("celina").open();
await celina.next("welcome");
const cid2 = rnd();
celina.send({ t: "call", cid: cid2, to: "bartek", video: false });
check((await celina.next("end"))?.reason === "busy", "dzwonienie do zajętej osoby: busy");

bartek.send({ t: "accept", cid });
check((await ala.next("accepted"))?.cid === cid, "Ala dowiaduje się, że Bartek odebrał");
ala.send({ t: "sig", cid, data: { sdp: "oferta" } });
check((await bartek.next("sig"))?.data.sdp === "oferta", "dane negocjacji docierają do drugiej strony");
bartek.send({ t: "sig", cid, data: { ice: "kandydat" } });
check((await ala.next("sig"))?.data.ice === "kandydat", "...i w drugą stronę");
celina.send({ t: "sig", cid, data: { zly: 1 } });
check(await bartek.none("sig"), "osoba spoza rozmowy nie może wysyłać danych do rozmowy");

// ---- rozłączanie
bartek.send({ t: "end", cid, reason: "hangup" });
check((await ala.next("end"))?.reason === "hangup", "rozłączenie dociera do Ali");
check((await ala.next("presence", 1500, (m) => m.online.ala === 1 && m.online.bartek === 1)) !== null, "po rozmowie obie osoby są wolne");

// ---- odrzucenie i rezygnacja
cid = rnd();
ala.send({ t: "call", cid, to: "bartek", video: false });
await bartek.next("ring");
bartek.send({ t: "end", cid, reason: "rejected" });
check((await ala.next("end"))?.reason === "rejected", "odrzucenie dociera do dzwoniącego");
cid = rnd();
ala.send({ t: "call", cid, to: "bartek", video: false });
await bartek.next("ring");
ala.send({ t: "end", cid, reason: "cancel" });
check((await bartek.next("end"))?.reason === "cancel", "rezygnacja dzwoniącego dociera do odbierającego");

// ---- offline, nieznana osoba, rozłączenie w trakcie dzwonienia
cid = rnd();
ala.send({ t: "call", cid, to: "nieistnieje", video: true });
check((await ala.next("end"))?.reason === "offline", "dzwonienie do nieznanej osoby: offline");
celina.close();
await sleep(300);
ala.send({ t: "call", cid: rnd(), to: "celina", video: true });
check((await ala.next("end"))?.reason === "offline", "dzwonienie do osoby, której nie ma w sieci: offline");
cid = rnd();
ala.send({ t: "call", cid, to: "bartek", video: false });
await bartek.next("ring");
bartek.close();
check((await ala.next("end"))?.reason === "lost", "zerwanie połączenia w czasie dzwonienia kończy rozmowę");

// ---- wznowienie rozmowy po zerwaniu serwera sygnalizacyjnego
const bartek2 = await new Client("bartek").open();
await bartek2.next("welcome");
cid = rnd();
ala.send({ t: "call", cid, to: "bartek", video: true });
await bartek2.next("ring");
bartek2.send({ t: "accept", cid });
await ala.next("accepted");
bartek2.close();
check((await ala.next("peerlost"))?.cid === cid, "trwająca rozmowa: Ala dostaje peerlost, ale rozmowa nie jest kończona");
check(await ala.none("end", 500), "rozmowa trwa mimo braku sygnalizacji jednej ze stron");
const bartek3 = await new Client("bartek").open();
await bartek3.next("welcome");
bartek3.send({ t: "resume", cid, peer: "ala" });
check((await bartek3.next("resumed"))?.cid === cid, "Bartek wznawia rozmowę po ponownym połączeniu");
check((await ala.next("peerback"))?.cid === cid, "Ala dowiaduje się, że Bartek wrócił");
ala.send({ t: "sig", cid, data: { n: 1 } });
check((await bartek3.next("sig"))?.data.n === 1, "po wznowieniu negocjacja znów działa");
ala.send({ t: "end", cid, reason: "hangup" });
await bartek3.next("end");

// ---- logowanie z drugiego miejsca
const ala2 = await new Client("ala").open();
await ala2.next("welcome");
check((await ala.next("kicked")) !== null, "starsze połączenie dostaje 'kicked' po zalogowaniu z innego miejsca");

// ---- TURN
ala2.send({ t: "turn" });
const turn = await ala2.next("turn", 8000);
check(turn && Array.isArray(turn.ice) && turn.ice.length > 0, "serwer wydaje listę serwerów ICE (relay=" + turn?.relay + ")");
if (process.env.EXPECT_RELAY) check(turn.relay === true, "TURN działa (są dane uwierzytelniające)");

// ---- śmieciowe ramki nie psują serwera
ala2.send("{nie json");
ala2.send(JSON.stringify({ t: "call" }));
ala2.send(JSON.stringify({ t: 5 }));
ala2.send("x".repeat(20000));
await sleep(300);
ala2.send({ t: "call", cid: rnd(), to: "ala", video: false });
check((await ala2.next("end"))?.reason === "offline", "serwer żyje po śmieciowych ramkach; dzwonienie do siebie jest odrzucane");

for (const c of [ala, ala2, bartek, bartek3]) c.close();
console.log(failed ? `\nNIEPOWODZENIA: ${failed}` : "\nWszystko w porządku.");
process.exit(failed ? 1 : 0);
