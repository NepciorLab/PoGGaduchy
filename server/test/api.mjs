// Test API serwera: obrazy osób, uprawnienia i panel administratora. Serwer lokalny (npm run dev) z .dev.vars z make-dev-vars.mjs.
//   node test/api.mjs        (BASE_URL=http://127.0.0.1:8787)
import { readFileSync } from "node:fs";
import { createHash, randomBytes } from "node:crypto";

const BASE = (process.env.BASE_URL || "http://127.0.0.1:8787").replace(/\/$/, "");
const keys = JSON.parse(readFileSync("test/keys.json", "utf8"));
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
let failed = 0;
const check = (c, m) => { console.log((c ? "ok:   " : "FAIL: ") + m); if (!c) failed++; };
const auth = (id, admin = false, extra = {}) => ({ Authorization: "Bearer " + keys[id], ...(admin ? { "X-Admin-Key": keys._admin } : {}), ...extra });
const jpeg = (n = 300) => { const b = Buffer.alloc(n, 7); b[0] = 0xff; b[1] = 0xd8; b[2] = 0xff; return b; };
const api = (method, path, id, { admin = false, body, headers = {} } = {}) =>
  fetch(BASE + path, { method, headers: auth(id, admin, headers), body });

class Ws {
  constructor(id, key = keys[id], admin = false) { this.q = []; this.id = id; this.key = key; this.admin = admin; }
  async open() {
    this.ws = new WebSocket(BASE.replace(/^http/, "ws") + "/ws", { headers: { Authorization: "Bearer " + this.key, ...(this.admin ? { "X-Admin-Key": keys._admin } : {}) } });
    await new Promise((r, j) => { this.ws.onopen = r; this.ws.onerror = () => j(new Error("ws")); });
    this.ws.onmessage = (e) => { if (e.data !== "pong") this.q.push(JSON.parse(String(e.data))); };
    this.ws.onclose = (e) => { this.closed = e.code; };
    return this;
  }
  async next(t, ms = 3000, pred = () => true) {
    const t0 = Date.now();
    for (;;) {
      const i = this.q.findIndex((m) => m.t === t && pred(m));
      if (i >= 0) return this.q.splice(i, 1)[0];
      if (Date.now() - t0 > ms) return null;
      await sleep(20);
    }
  }
}

// ---- status i administrator
let r = await api("GET", "/api/status", "ala");
let j = await r.json();
check(r.status === 200 && j.admin === false, "bez nagłówka administratora: admin=false");
j = await (await api("GET", "/api/status", "ala", { admin: true })).json();
check(j.admin === true, "z poprawnym kluczem administratora: admin=true");
r = await fetch(BASE + "/api/status", { headers: { ...auth("ala"), "X-Admin-Key": "zly-klucz" } });
check((await r.json()).admin === false, "zły klucz administratora w statusie: admin=false");
r = await api("PUT", "/api/avatar/ala", "ala", { headers: { "X-Admin-Key": "zly-klucz" }, body: jpeg() });
check(r.status === 403, "zły klucz administratora przy zmianie obrazu: 403");

// ---- obrazy (najpierw czyścimy stan po poprzednich uruchomieniach)
for (const id of ["ala", "bartek", "celina"]) await api("DELETE", "/api/avatar/" + id, "ala", { admin: true });
const ala = await new Ws("ala", keys.ala, true).open();      // ala = administrator (gniazdo z kluczem administratora)
const bartek = await new Ws("bartek").open();
const w = await ala.next("welcome");
check(w.directory.every((p) => p.av === 0), "na początku nikt nie ma obrazu (av=0 w katalogu)");
check((await api("GET", "/api/avatar/ala", "bartek")).status === 404, "brak obrazu: 404");
r = await api("PUT", "/api/avatar/ala", "ala", { body: jpeg() });
j = await r.json();
check(r.status === 200 && j.av > 0, "osoba zapisuje swój obraz (dostaje numer wersji)");
const av1 = j.av;
const pe = await bartek.next("people", 3000, (m) => m.directory.find((p) => p.id === "ala")?.av === av1);
check(pe !== null, "pozostali dostają informację o nowym obrazie (people, nowa wersja)");
r = await api("GET", "/api/avatar/ala", "bartek");
const got = Buffer.from(await r.arrayBuffer());
check(r.status === 200 && r.headers.get("content-type") === "image/jpeg" && got.equals(jpeg()), "inna osoba pobiera ten sam obraz");
check((await api("PUT", "/api/avatar/bartek", "ala", { body: jpeg() })).status === 403, "zwykła osoba nie może zmienić cudzego obrazu: 403");
check((await api("PUT", "/api/avatar/bartek", "ala", { admin: true, body: jpeg(500) })).status === 200, "administrator zmienia obraz innej osoby");
check((await api("PUT", "/api/avatar/ala", "ala", { body: Buffer.from("to nie jest jpeg".repeat(20)) })).status === 415, "nie-JPEG jest odrzucany: 415");
check((await api("PUT", "/api/avatar/ala", "ala", { body: jpeg(300 * 1024) })).status === 413, "za duży obraz: 413");
r = await api("DELETE", "/api/avatar/ala", "ala");
check(r.status === 200, "osoba usuwa swój obraz");
check((await api("GET", "/api/avatar/ala", "bartek")).status === 404, "po usunięciu: 404");
const pe2 = await bartek.next("people", 3000, (m) => m.directory.find((p) => p.id === "ala")?.av === 0);
check(pe2 !== null, "po usunięciu katalog ma av=0");

// ---- administrator: osoby
const newKey = randomBytes(32).toString("base64url");
const hash = createHash("sha256").update(newKey).digest("hex");
const add = { id: "ciocia", name: "Ciocia Hela", hash };
check((await api("POST", "/api/admin/people", "ala", { body: JSON.stringify(add) })).status === 403, "zwykła osoba nie dodaje osób: 403");
r = await api("POST", "/api/admin/people", "ala", { admin: true, body: JSON.stringify(add) });
check(r.status === 200, "administrator dodaje osobę");
check((await api("POST", "/api/admin/people", "ala", { admin: true, body: JSON.stringify(add) })).status === 409, "dodanie tej samej osoby drugi raz: 409");
check((await api("POST", "/api/admin/people", "ala", { admin: true, body: JSON.stringify({ id: "Zle Id", name: "x", hash }) })).status === 400, "złe id: 400");
const pe3 = await bartek.next("people", 3000, (m) => m.directory.some((p) => p.id === "ciocia"));
check(pe3 !== null, "wszyscy dostają zaktualizowany katalog z nową osobą");
keys.ciocia = newKey;
const cw = await new Ws("ciocia").open();
const cwel = await cw.next("welcome");
check(cwel?.me.name === "Ciocia Hela", "nowa osoba loguje się swoim kluczem");
r = await api("PATCH", "/api/admin/people/ciocia", "ala", { admin: true, body: JSON.stringify({ name: "Ciocia Helena" }) });
check(r.status === 200 && (await bartek.next("people", 3000, (m) => m.directory.find((p) => p.id === "ciocia")?.name === "Ciocia Helena")) !== null, "zmiana nazwy osoby trafia do wszystkich");
const key2 = randomBytes(32).toString("base64url");
r = await api("PATCH", "/api/admin/people/ciocia", "ala", { admin: true, body: JSON.stringify({ hash: createHash("sha256").update(key2).digest("hex") }) });
await sleep(400);
check(r.status === 200 && (cw.closed === 4003 || cw.ws.readyState >= 2 || cw.q.some((m) => m.t === "kicked")), "nowy klucz: stare połączenie osoby jest zamykane");
check((await fetch(BASE + "/api/status", { headers: { Authorization: "Bearer " + newKey } })).status === 401, "stary klucz przestaje działać");
r = await api("DELETE", "/api/admin/people/ciocia", "ala", { admin: true });
check(r.status === 200, "administrator usuwa osobę");
check((await bartek.next("people", 3000, (m) => !m.directory.some((p) => p.id === "ciocia"))) !== null, "katalog po usunięciu nie ma tej osoby");
check((await api("DELETE", "/api/admin/people/ala", "ala", { admin: true })).status === 409, "administrator nie może usunąć sam siebie: 409");
check((await api("DELETE", "/api/admin/people/nikt", "ala", { admin: true })).status === 404, "usuwanie nieistniejącej osoby: 404");

// ---- samodzielne zakładanie kont (NEW)
const reg = (name, hash) => fetch(BASE + "/api/register", { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ name, hash }) });
const newKey2 = randomBytes(32).toString("base64url");
r = await reg("Nowy Jan Łukasz", createHash("sha256").update(newKey2).digest("hex"));
j = await r.json();
check(r.status === 200 && /^nowy-jan-lukasz-[0-9a-f]{4}$/.test(j.id) && j.pending === true, "rejestracja bez klucza tworzy konto NEW (id z nazwy: " + j.id + ")");
const newId = j.id;
keys[newId] = newKey2;
check((await reg("", "a".repeat(64))).status === 400, "rejestracja bez nazwy: 400");
check((await reg("Ktoś", "zły-skrót")).status === 400, "rejestracja ze złym skrótem klucza: 400");
const adminPeople = await ala.next("people", 3000, (m) => m.directory.some((p) => p.id === newId && p.st === "new"));
check(adminPeople !== null, "administrator dostaje listę z nowym kontem oznaczonym st:new");
await sleep(300);
check(!bartek.q.some((m) => m.t === "people" && m.directory.some((p) => p.id === newId)), "zwykła osoba (bartek) NIE widzi konta NEW na liście");
check((await api("GET", "/api/status", newId)).status === 200 && (await (await api("GET", "/api/status", newId)).json()).pending === true, "konto NEW ma ważny klucz i status pending");

const nw = await new Ws(newId).open();
const nwel = await nw.next("welcome");
check(nwel?.me.st === "new", "konto NEW dostaje welcome z me.st=new");
check(nwel.directory.some((p) => p.id === "ala") && !nwel.directory.some((p) => p.id === "bartek"), "konto NEW widzi administratora (ala), ale nie widzi zwykłych osób");
nw.ws.send(JSON.stringify({ t: "call", cid: "cidnew00001", to: "bartek", video: false }));
check((await nw.next("end"))?.reason === "offline", "konto NEW nie może dzwonić do zwykłej osoby");
bartek.ws.send(JSON.stringify({ t: "call", cid: "cidbart0001", to: newId, video: false }));
check((await bartek.next("end"))?.reason === "offline", "zwykła osoba nie może dzwonić do konta NEW");
nw.ws.send(JSON.stringify({ t: "call", cid: "cidnew00002", to: "ala", video: true }));
const ring = await ala.next("ring");
check(ring && ring.from === newId && ring.name === "Nowy Jan Łukasz", "konto NEW może zadzwonić do administratora (ring z nazwą dzwoniącego)");
ala.ws.send(JSON.stringify({ t: "end", cid: "cidnew00002", reason: "rejected" }));
await nw.next("end");

check((await api("POST", "/api/admin/people/" + newId + "/accept", "bartek")).status === 403, "zwykła osoba nie może zaakceptować konta");
r = await api("POST", "/api/admin/people/" + newId + "/accept", "ala", { admin: true });
check(r.status === 200, "administrator akceptuje konto");
check((await bartek.next("people", 3000, (m) => m.directory.some((p) => p.id === newId && !p.st))) !== null, "po akceptacji zwykła osoba widzi nową osobę");
const nwPeople = await nw.next("people", 3000, (m) => m.directory.some((p) => p.id === "bartek"));
check(nwPeople !== null, "po akceptacji nowa osoba widzi wszystkich");
nw.ws.send(JSON.stringify({ t: "call", cid: "cidnew00003", to: "bartek", video: false }));
check((await bartek.next("ring")) !== null, "po akceptacji nowa osoba może dzwonić do zwykłej osoby");
nw.ws.send(JSON.stringify({ t: "end", cid: "cidnew00003", reason: "cancel" }));
check((await api("DELETE", "/api/admin/people/" + newId, "ala", { admin: true })).status === 200, "administrator usuwa konto");
nw.ws.close();

// limit kont oczekujących
const made = [];
for (let i = 0; i < 6; i++) { const rr = await reg("Gość " + i, createHash("sha256").update("k" + i + randomBytes(4).toString("hex")).digest("hex")); made.push(rr.status === 200 ? (await rr.json()).id : rr.status); }
check(made.filter((x) => typeof x === "string").length === 5 && made[5] === 429, "najwyżej 5 kont czeka na akceptację (szóste: 429)");
for (const id of made.filter((x) => typeof x === "string")) await api("DELETE", "/api/admin/people/" + id, "ala", { admin: true });

ala.ws.close(); bartek.ws.close();
console.log(failed ? `\nNIEPOWODZENIA: ${failed}` : "\nWszystko w porządku.");
process.exit(failed ? 1 : 0);
