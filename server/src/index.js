// Serwer PoGGaduchów (Cloudflare Workers + jeden Durable Object).
//
// Serwer NIE widzi obrazu ani dźwięku rozmów (idą bezpośrednio między komputerami, WebRTC, szyfrowane).
// Przekazuje tylko: kto jest w sieci, "X dzwoni do Y", odebrał / odrzucił / rozłączył oraz dane negocjacji połączenia.
// Przechowuje też katalog osób (nazwy) i ich małe obrazy (zdjęcia widoczne na kafelkach u wszystkich).
//
// Dostęp: każda instalacja ma długi, losowy klucz; serwer zna tylko skróty SHA-256 kluczy (tabela members w bazie
// Durable Object; pierwsze osoby można wczytać z sekretu MEMBERS: [{"id","name","hash"}]). Klucz idzie w nagłówku
// "Authorization: Bearer <klucz>" (nigdy w adresie).
// Administrator: osoba, która oprócz swojego klucza pokaże nagłówek "X-Admin-Key: <tajny tekst z pliku Admin.PMa>";
// serwer zna tylko jego skrót (sekret ADMIN_HASH = SHA-256 tekstu, hex).
//
// HTTP:
//   GET  /                          -> "PoGGaduchy: OK"                        (publiczne, bez danych)
//   GET  /api/status                -> {ok, proto, id, name, admin, turn}      (klucz; ?turn=1 sprawdza też TURN)
//   GET  /api/avatar/<id>           -> obraz JPEG osoby albo 404               (klucz)
//   PUT  /api/avatar/<id>           -> zapis obrazu JPEG (≤ 250 KB)            (klucz: tylko swój obraz; admin: dowolny)
//   DELETE /api/avatar/<id>         -> usunięcie obrazu                        (j.w.)
//   POST /api/admin/people          -> {id,name,hash} dodaje osobę             (klucz + admin)
//   PATCH /api/admin/people/<id>    -> {name?, hash?} zmiana nazwy / klucza    (klucz + admin)
//   DELETE /api/admin/people/<id>   -> usuwa osobę (i jej obraz)               (klucz + admin)
//   POST /api/register              -> {name, hash}: samodzielne założenie konta (konto czeka na akceptację, status NEW)  (bez klucza)
//   POST /api/admin/people/<id>/accept -> akceptacja konta NEW                 (klucz + admin)
//   GET  /ws                        -> WebSocket                               (klucz; z X-Admin-Key serwer wie, że to administrator)
//
// WebSocket, ramki tekstowe:
//   "ping" -> "pong"   odpowiada platforma (auto-response), bez budzenia obiektu i bez zużywania limitu
//   pozostałe ramki to JSON {t: "...", ...}
//
// klient -> serwer
//   {t:"call", cid, to, video}        dzwonię do osoby `to` (cid: losowy identyfikator rozmowy)
//   {t:"accept", cid}                 odbieram
//   {t:"end", cid, reason}            odrzucam / rozłączam / rezygnuję (reason: rejected|hangup|cancel|timeout|error)
//   {t:"sig", cid, data}              dane negocjacji WebRTC dla drugiej strony (SDP, ICE)
//   {t:"resume", cid, peer}           po ponownym połączeniu: wracam do trwającej rozmowy
//   {t:"turn"}                        poproś o poświadczenia TURN
// serwer -> klient
//   {t:"welcome", proto, me, directory:[{id,name,av}], online, serverTime}
//   {t:"people", directory}           lista osób / obrazów się zmieniła (av: numer wersji obrazu, 0 = brak obrazu)
//   {t:"presence", online:{id:1|2}}   1 = wolny, 2 = rozmawia
//   {t:"ring", cid, from, video}      ktoś do Ciebie dzwoni
//   {t:"ringing", cid}                dzwonek u drugiej osoby
//   {t:"accepted", cid}               druga osoba odebrała
//   {t:"end", cid, reason}            rozmowa zakończona (rejected|hangup|cancel|timeout|busy|offline|lost|gone|error)
//   {t:"sig", cid, data}              dane negocjacji od drugiej strony
//   {t:"peerlost", cid} / {t:"peerback", cid}   druga strona straciła / odzyskała połączenie z serwerem
//   {t:"resumed", cid}                wznowiono rozmowę
//   {t:"turn", ice, relay}            poświadczenia (relay=false: tylko STUN, TURN niedostępny)
//   {t:"kicked"}                      zalogowano się z innego miejsca tym samym kluczem
import { DurableObject } from "cloudflare:workers";

const PROTO = 2;
const MAX_FRAME = 16000;            // SDP ma kilka kilobajtów; limit chroni przed śmieciami
const MAX_AVATAR = 250 * 1024;      // obraz osoby: kwadratowy JPEG ok. 512 px, zwykle 30-80 KB
const MAX_MEMBERS = 30;
const MAX_PENDING = 5;              // tyle kont może jednocześnie czekać na akceptację
const PENDING_DAYS = 14;            // nieakceptowane konto znika po tylu dniach
const MEMBER_CACHE_MS = 30_000;     // Worker pamięta listę osób tyle czasu (zmiany admina widać po tym czasie we wszystkich lokalizacjach)
const STALE_MS = 100_000;           // klient pinguje co ~25 s; brak życia przez 100 s = zerwane połączenie
const SWEEP_MS = 60_000;            // jak często obiekt sprząta zerwane połączenia (alarm)
const RESUME_MS = 90_000;           // tyle czekamy na powrót strony, która straciła połączenie w trakcie rozmowy
const RATE_WINDOW_MS = 10_000, RATE_MAX = 120;   // ruch ICE przychodzi seriami; to i tak daleko od nadużyć
const END_REASONS = new Set(["rejected", "hangup", "cancel", "timeout", "error"]);
const FALLBACK_ICE = [{ urls: ["stun:stun.cloudflare.com:3478", "stun:stun.l.google.com:19302"] }];
const ID_RE = /^[a-z0-9][a-z0-9_-]{0,31}$/;

// ---------------------------------------------------------------- pomocnicze
async function sha256Hex(text) {
  const d = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(text));
  return [...new Uint8Array(d)].map((b) => b.toString(16).padStart(2, "0")).join("");
}

function safeEqual(a, b) {
  if (a.length !== b.length) return false;
  let r = 0;
  for (let i = 0; i < a.length; i++) r |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return r === 0;
}

const hub = (env) => env.HUB.get(env.HUB.idFromName("main"));

let memberCache = { at: 0, list: [] };
async function members(env, force = false) {
  if (!force && Date.now() - memberCache.at < MEMBER_CACHE_MS) return memberCache.list;
  memberCache = { at: Date.now(), list: await hub(env).listMembers() };
  return memberCache.list;
}

async function authenticate(request, env) {
  const m = /^Bearer ([A-Za-z0-9_-]{32,128})$/.exec(request.headers.get("Authorization") || "");
  if (!m) return null;
  const hash = await sha256Hex(m[1]);
  let found = null;
  for (const mem of await members(env)) if (safeEqual(mem.hash, hash)) found = mem;   // bez wcześniejszego wyjścia
  return found;
}

// Administrator: nagłówek X-Admin-Key musi mieć skrót zgodny z sekretem ADMIN_HASH.
async function isAdmin(request, env) {
  const key = (request.headers.get("X-Admin-Key") || "").trim();
  if (!key || key.length > 256 || !env.ADMIN_HASH) return false;
  return safeEqual((await sha256Hex(key)).toLowerCase(), String(env.ADMIN_HASH).trim().toLowerCase());
}

async function iceServers(env) {
  if (!env.TURN_KEY_ID || !env.TURN_KEY_API_TOKEN) return { ice: FALLBACK_ICE, relay: false };
  const ttl = Math.max(600, parseInt(env.TURN_TTL || "21600", 10) || 21600);
  try {
    const r = await fetch(`https://rtc.live.cloudflare.com/v1/turn/keys/${env.TURN_KEY_ID}/credentials/generate-ice-servers`, {
      method: "POST",
      headers: { Authorization: `Bearer ${env.TURN_KEY_API_TOKEN}`, "Content-Type": "application/json" },
      body: JSON.stringify({ ttl }),
      signal: AbortSignal.timeout(6000),
    });
    if (!r.ok) throw new Error("TURN HTTP " + r.status);
    const body = await r.json();
    const ice = (body.iceServers || []).map((s) => ({ ...s, urls: [].concat(s.urls).filter((u) => !/:53(\?|$)/.test(u)) }));
    if (!ice.length) throw new Error("TURN: pusta odpowiedź");
    return { ice, relay: ice.some((s) => s.username) };
  } catch (e) {
    return { ice: FALLBACK_ICE, relay: false, error: String(e.message || e) };
  }
}

const json = (obj, status = 200) =>
  new Response(JSON.stringify(obj), { status, headers: { "content-type": "application/json; charset=utf-8", "cache-control": "no-store" } });
const text = (msg, status) => new Response(msg, { status, headers: { "cache-control": "no-store" } });

// ---------------------------------------------------------------- Worker
export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (url.pathname === "/" && request.method === "GET") return text("PoGGaduchy: OK\n", 200);

    if (url.pathname === "/api/register" && request.method === "POST") return registerApi(request, env);

    const isApi = url.pathname === "/api/status" || url.pathname.startsWith("/api/avatar/") || url.pathname.startsWith("/api/admin/");
    if (url.pathname !== "/ws" && !isApi) return text("Not found", 404);

    const ip = request.headers.get("CF-Connecting-IP") || "unknown";
    // Zła próba (zły klucz osoby lub zły klucz administratora): liczymy ją i po przekroczeniu limitu odpowiadamy 429.
    const failed = async (status, msg) => {
      if (env.AUTH_FAIL_LIMITER) {
        const { success } = await env.AUTH_FAIL_LIMITER.limit({ key: ip });
        if (!success) return text("Too many attempts", 429);
      }
      return text(msg, status);
    };

    const me = await authenticate(request, env);
    if (!me) return failed(401, "Unauthorized");
    // Konto NEW (niezaakceptowane) nigdy nie dostaje uprawnień administratora, nawet z poprawnym tekstem Admin.PMa.
    const admin = request.headers.has("X-Admin-Key") && me.status !== "pending" ? await isAdmin(request, env) : false;
    if (request.headers.has("X-Admin-Key") && !admin && url.pathname !== "/api/status" && url.pathname !== "/ws") return failed(403, "Forbidden");

    if (url.pathname === "/api/status") {
      if (admin) await hub(env).noteAdmin(me.id);
      const out = { ok: true, proto: PROTO, id: me.id, name: me.name, admin, pending: me.status === "pending", turn: !!(env.TURN_KEY_ID && env.TURN_KEY_API_TOKEN) };
      if (url.searchParams.get("turn") === "1") { const t = await iceServers(env); out.turnWorks = t.relay; if (t.error) out.turnError = t.error; }
      return json(out);
    }

    if (url.pathname.startsWith("/api/avatar/")) return avatarApi(request, env, url, me, admin);
    if (url.pathname.startsWith("/api/admin/")) return adminApi(request, env, url, me, admin);

    if (request.headers.get("Upgrade") !== "websocket") return text("WebSocket expected", 426);
    const headers = new Headers(request.headers);
    headers.delete("Authorization");
    headers.delete("X-Admin-Key");
    headers.set("X-Member-Id", me.id);
    if (admin) headers.set("X-Admin", "1");
    return hub(env).fetch(new Request(request, { headers }));
  },
};

async function registerApi(request, env) {
  const ip = request.headers.get("CF-Connecting-IP") || "unknown";
  if (env.REGISTER_LIMITER) {
    const { success } = await env.REGISTER_LIMITER.limit({ key: ip });
    if (!success) return text("Too many attempts", 429);
  }
  if (parseInt(request.headers.get("content-length") || "0", 10) > 1024) return text("Too large", 413);
  let body;
  try { body = await request.json(); } catch { return text("Bad JSON", 400); }
  const name = typeof body.name === "string" ? body.name.replace(/[\u0000-\u001f\u007f]/g, "").trim() : "";
  if (name.length < 1 || name.length > 40 || !/^[0-9a-f]{64}$/.test(body.hash || "")) return text("Bad data", 400);
  const r = await hub(env).register(name, body.hash);
  if (r === "pending-full" || r === "full") return text("Too many pending accounts", 429);
  memberCache.at = 0;
  return json({ ok: true, id: r, pending: true });
}

async function avatarApi(request, env, url, me, admin) {
  const id = decodeURIComponent(url.pathname.slice("/api/avatar/".length));
  if (!ID_RE.test(id)) return text("Bad id", 400);
  const h = hub(env);
  if (request.method === "GET") {
    const a = await h.getAvatar(id);
    if (!a) return text("No avatar", 404);
    return new Response(a.bytes, { headers: { "content-type": "image/jpeg", "cache-control": "no-store", "x-avatar-version": String(a.av) } });
  }
  if (request.method !== "PUT" && request.method !== "DELETE") return text("Method not allowed", 405);
  if (id !== me.id && !admin) return text("Forbidden", 403);               // każdy zmienia tylko swój obraz
  if (request.method === "DELETE") {
    const r = await h.clearAvatar(id);
    return r ? json({ ok: true }) : text("Not found", 404);
  }
  const len = parseInt(request.headers.get("content-length") || "0", 10);
  if (len > MAX_AVATAR) return text("Too large", 413);
  const buf = await request.arrayBuffer();
  if (buf.byteLength < 100 || buf.byteLength > MAX_AVATAR) return text("Bad size", 413);
  const b = new Uint8Array(buf);
  if (!(b[0] === 0xff && b[1] === 0xd8 && b[2] === 0xff)) return text("JPEG expected", 415);
  const av = await h.setAvatar(id, buf);
  if (av === null) return text("Not found", 404);
  return json({ ok: true, av });
}

async function adminApi(request, env, url, me, admin) {
  if (!admin) return text("Forbidden", 403);
  const h = hub(env);
  const rest = decodeURIComponent(url.pathname.slice("/api/admin/people".length)).replace(/^\//, "");
  if (!url.pathname.startsWith("/api/admin/people")) return text("Not found", 404);
  let body = {};
  const isAccept = /\/accept$/.test(rest);                     // akceptacja nie ma ciała żądania
  if ((request.method === "POST" || request.method === "PATCH") && !isAccept) {
    if (parseInt(request.headers.get("content-length") || "0", 10) > 4096) return text("Too large", 413);
    try { body = await request.json(); } catch { return text("Bad JSON", 400); }
  }
  const okName = (n) => typeof n === "string" && n.trim().length >= 1 && n.trim().length <= 60;
  const okHash = (x) => typeof x === "string" && /^[0-9a-f]{64}$/.test(x);
  let r;
  const acc = /^([a-z0-9][a-z0-9_-]{0,31})\/accept$/.exec(rest);
  if (request.method === "POST" && acc) {
    r = await h.acceptMember(acc[1]);
  } else if (request.method === "POST" && rest === "") {
    if (!ID_RE.test(body.id || "") || !okName(body.name) || !okHash(body.hash)) return text("Bad data", 400);
    r = await h.addMember(body.id, body.name.trim(), body.hash);
  } else if (request.method === "PATCH" && ID_RE.test(rest)) {
    if (body.name !== undefined && !okName(body.name)) return text("Bad name", 400);
    if (body.hash !== undefined && !okHash(body.hash)) return text("Bad hash", 400);
    r = await h.updateMember(rest, body.name === undefined ? null : body.name.trim(), body.hash ?? null);
  } else if (request.method === "DELETE" && ID_RE.test(rest)) {
    if (rest === me.id) return text("Cannot remove yourself", 409);
    r = await h.removeMember(rest);
  } else return text("Not found", 404);
  memberCache.at = 0;                                                       // ta lokalizacja od razu zna zmianę
  if (r === "exists") return text("Exists", 409);
  if (r === "full") return text("Too many people", 409);
  if (r === "none") return text("Not found", 404);
  return json({ ok: true });
}

// ---------------------------------------------------------------- Durable Object
export class Hub extends DurableObject {
  constructor(ctx, env) {
    super(ctx, env);
    this.sql = ctx.storage.sql;
    this.sql.exec(`CREATE TABLE IF NOT EXISTS members(
      id TEXT PRIMARY KEY, name TEXT NOT NULL, hash TEXT NOT NULL, avatar BLOB, av INTEGER NOT NULL DEFAULT 0, created INTEGER NOT NULL)`);
    try { this.sql.exec("ALTER TABLE members ADD COLUMN status TEXT NOT NULL DEFAULT 'active'"); } catch { /* kolumna już jest */ }
    this.sql.exec("CREATE TABLE IF NOT EXISTS meta(k TEXT PRIMARY KEY, v TEXT)");
    this.sql.exec("CREATE TABLE IF NOT EXISTS admins(id TEXT PRIMARY KEY)");
    this.seed();
    // Podtrzymywanie połączenia: "ping" -> "pong" bez budzenia obiektu (nie liczy się do limitów).
    ctx.setWebSocketAutoResponse(new WebSocketRequestResponsePair("ping", "pong"));
  }

  // Pierwsze uruchomienie: osoby z sekretu MEMBERS trafiają do bazy (potem baza jest jedynym źródłem; MEMBERS jest ignorowany).
  seed() {
    const done = this.sql.exec("SELECT v FROM meta WHERE k = 'seeded'").toArray()[0];
    if (done) return;
    try {
      for (const m of JSON.parse(this.env.MEMBERS || "[]")) {
        if (m && ID_RE.test(m.id) && /^[0-9a-f]{64}$/.test(m.hash))
          this.sql.exec("INSERT OR IGNORE INTO members(id, name, hash, created) VALUES(?, ?, ?, ?)", m.id, String(m.name || m.id).slice(0, 60), m.hash, Date.now());
      }
    } catch { /* błędny sekret: zaczynamy z pustą listą */ }
    this.sql.exec("INSERT OR REPLACE INTO meta(k, v) VALUES('seeded', '1')");
  }

  // ---------------------------------------------------------------- osoby i obrazy (wołane z Workera przez RPC)
  // Nieakceptowane konta znikają po PENDING_DAYS dniach.
  purgeExpired() {
    this.sql.exec("DELETE FROM members WHERE status = 'pending' AND created < ?", Date.now() - PENDING_DAYS * 86400_000);
  }

  listMembers() {
    this.purgeExpired();
    return this.sql.exec("SELECT id, name, hash, status, CASE WHEN avatar IS NULL THEN 0 ELSE av END AS av FROM members ORDER BY rowid").toArray();
  }

  adminIds() { return new Set(this.sql.exec("SELECT id FROM admins").toArray().map((r) => r.id)); }

  noteAdmin(id) {
    if (this.sql.exec("SELECT 1 FROM members WHERE id = ? AND status = 'active'", id).toArray().length)
      this.sql.exec("INSERT OR IGNORE INTO admins(id) VALUES(?)", id);
  }

  // Lista osób widoczna dla danej osoby: zwykła osoba widzi aktywne konta; osoba z kontem NEW – tylko administratorów (i siebie);
  // administrator widzi wszystkich, konta NEW oznaczone st:"new".
  directoryFor(viewerId, viewerIsAdmin, rows = this.listMembers()) {
    const admins = this.adminIds();
    const viewer = rows.find((m) => m.id === viewerId);
    const view = (m) => ({ id: m.id, name: m.name, av: m.av, ...(m.status === "pending" ? { st: "new" } : {}) });
    if (viewerIsAdmin) return rows.map(view);
    if (viewer?.status === "pending") return rows.filter((m) => m.id === viewerId || (m.status === "active" && admins.has(m.id))).map(view);
    return rows.filter((m) => m.status === "active" || m.id === viewerId).map((m) => ({ id: m.id, name: m.name, av: m.av }));
  }

  broadcastPeople() {
    const rows = this.listMembers();
    for (const ws of this.ctx.getWebSockets()) {
      const a = this.att(ws);
      this.send(ws, { t: "people", directory: this.directoryFor(a.id, !!a.adm, rows) });
    }
  }

  // Samodzielne założenie konta: konto czeka na akceptację administratora. Zwraca id nowej osoby albo "pending-full" / "full".
  register(name, hash) {
    this.purgeExpired();
    if (this.sql.exec("SELECT COUNT(*) AS n FROM members WHERE status = 'pending'").toArray()[0].n >= MAX_PENDING) return "pending-full";
    if (this.sql.exec("SELECT COUNT(*) AS n FROM members").toArray()[0].n >= MAX_MEMBERS) return "full";
    const base = name.normalize("NFD").replace(/[\u0300-\u036f]/g, "").replace(/ł/gi, "l").toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "").slice(0, 22) || "osoba";
    let id;
    do { id = base + "-" + [...crypto.getRandomValues(new Uint8Array(2))].map((x) => x.toString(16).padStart(2, "0")).join(""); }
    while (this.sql.exec("SELECT 1 FROM members WHERE id = ?", id).toArray().length);
    this.sql.exec("INSERT INTO members(id, name, hash, status, created) VALUES(?, ?, ?, 'pending', ?)", id, name, hash, Date.now());
    this.broadcastPeople();
    return id;
  }

  acceptMember(id) {
    if (!this.sql.exec("SELECT 1 FROM members WHERE id = ?", id).toArray().length) return "none";
    this.sql.exec("UPDATE members SET status = 'active' WHERE id = ?", id);
    this.broadcastPeople();
    return "ok";
  }

  addMember(id, name, hash) {
    if (this.sql.exec("SELECT 1 FROM members WHERE id = ?", id).toArray().length) return "exists";
    if (this.sql.exec("SELECT COUNT(*) AS n FROM members").toArray()[0].n >= MAX_MEMBERS) return "full";
    this.sql.exec("INSERT INTO members(id, name, hash, created) VALUES(?, ?, ?, ?)", id, name, hash, Date.now());
    this.broadcastPeople();
    return "ok";
  }

  updateMember(id, name, hash) {
    if (!this.sql.exec("SELECT 1 FROM members WHERE id = ?", id).toArray().length) return "none";
    if (name !== null) this.sql.exec("UPDATE members SET name = ? WHERE id = ?", name, id);
    if (hash !== null) {
      this.sql.exec("UPDATE members SET hash = ? WHERE id = ?", hash, id);
      this.dropSockets(id, "new key");                                       // stary klucz przestaje działać od razu
    }
    this.broadcastPeople();
    return "ok";
  }

  removeMember(id) {
    if (!this.sql.exec("SELECT 1 FROM members WHERE id = ?", id).toArray().length) return "none";
    this.sql.exec("DELETE FROM members WHERE id = ?", id);
    this.sql.exec("DELETE FROM admins WHERE id = ?", id);
    this.dropSockets(id, "removed");
    this.broadcastPeople();
    this.broadcastPresence();
    return "ok";
  }

  dropSockets(id, why) {
    for (const ws of this.ctx.getWebSockets(id)) {
      this.send(ws, { t: "kicked", reason: why });
      try { ws.close(4003, why); } catch { /* */ }
      this.dropSocket(ws, false);
    }
  }

  getAvatar(id) {
    const r = this.sql.exec("SELECT avatar, av FROM members WHERE id = ?", id).toArray()[0];
    return r && r.avatar ? { bytes: r.avatar, av: r.av } : null;
  }

  setAvatar(id, bytes) {
    const r = this.sql.exec("SELECT av FROM members WHERE id = ?", id).toArray()[0];
    if (!r) return null;
    const av = r.av + 1;
    this.sql.exec("UPDATE members SET avatar = ?, av = ? WHERE id = ?", bytes, av, id);
    this.broadcastPeople();
    return av;
  }

  clearAvatar(id) {
    const r = this.sql.exec("SELECT av FROM members WHERE id = ?", id).toArray()[0];
    if (!r) return false;
    this.sql.exec("UPDATE members SET avatar = NULL, av = ? WHERE id = ?", r.av + 1, id);
    this.broadcastPeople();
    return true;
  }

  // ---------------------------------------------------------------- WebSocket
  async fetch(request) {
    const id = request.headers.get("X-Member-Id");
    const mem = this.listMembers().find((m) => m.id === id);
    if (!mem) return new Response("Unauthorized", { status: 401 });

    const pair = new WebSocketPair();
    const [client, server] = Object.values(pair);
    // Hibernacja: obiekt "śpi" między wiadomościami. Znacznik = id osoby (jedno połączenie na osobę).
    this.ctx.acceptWebSocket(server, [mem.id]);
    const now = Date.now();
    const isAdmin = request.headers.get("X-Admin") === "1" && mem.status === "active";
    if (isAdmin) this.noteAdmin(mem.id);
    server.serializeAttachment({ id: mem.id, adm: isAdmin, t0: now, seen: now, cid: null, peer: null, st: null, lost: 0, rl: [now, 0] });

    for (const old of this.ctx.getWebSockets(mem.id)) {
      if (old === server) continue;
      this.send(old, { t: "kicked" });
      try { old.close(4000, "replaced"); } catch { /* */ }
      this.dropSocket(old, false);
    }
    this.send(server, { t: "welcome", proto: PROTO, me: { id: mem.id, name: mem.name, ...(mem.status === "pending" ? { st: "new" } : {}) },
      directory: this.directoryFor(mem.id, isAdmin), online: this.onlineFor(mem.id, isAdmin), serverTime: now });
    this.broadcastPresence();
    await this.ensureAlarm();
    return new Response(null, { status: 101, webSocket: client });
  }

  // ---------------------------------------------------------------- stan
  att(ws) { try { return ws.deserializeAttachment() || {}; } catch { return {}; } }
  save(ws, a) { try { ws.serializeAttachment(a); } catch { /* gniazdo zamknięte */ } }
  send(ws, obj) { try { ws.send(JSON.stringify(obj)); } catch { /* gniazdo zamknięte */ } }

  sockOf(id) {
    return this.ctx.getWebSockets(id).find((w) => this.fresh(w)) || null;
  }

  fresh(ws) {
    const a = this.att(ws);
    const auto = this.ctx.getWebSocketAutoResponseTimestamp(ws);
    const last = Math.max(a.t0 || 0, a.seen || 0, auto ? auto.getTime() : 0);
    return Date.now() - last < (parseInt(this.env.STALE_MS, 10) || STALE_MS);
  }

  online() {
    const out = {};
    for (const ws of this.ctx.getWebSockets()) {
      if (!this.fresh(ws)) continue;
      const a = this.att(ws);
      out[a.id] = a.cid ? 2 : 1;
    }
    return out;
  }

  // Obecność tylko osób, które dana osoba widzi na swojej liście (konta NEW nie wychodzą poza administratorów).
  onlineFor(viewerId, viewerIsAdmin) {
    const visible = new Set(this.directoryFor(viewerId, viewerIsAdmin).map((p) => p.id));
    return Object.fromEntries(Object.entries(this.online()).filter(([id]) => visible.has(id)));
  }

  broadcastPresence() {
    for (const ws of this.ctx.getWebSockets()) {
      const a = this.att(ws);
      this.send(ws, { t: "presence", online: this.onlineFor(a.id, !!a.adm) });
    }
  }

  clearCall(ws) {
    const a = this.att(ws);
    a.cid = null; a.peer = null; a.st = null; a.lost = 0;
    this.save(ws, a);
  }

  // Zakończenie rozmowy dla obu stron; `ws` to strona, która kończy (lub zniknęła).
  endCall(ws, reason, notifyPeer = true) {
    const a = this.att(ws);
    if (!a.cid) return;
    const cid = a.cid, peerId = a.peer;
    this.clearCall(ws);
    for (const pw of this.ctx.getWebSockets(peerId)) {
      const pa = this.att(pw);
      if (pa.cid !== cid) continue;
      this.clearCall(pw);
      if (notifyPeer) this.send(pw, { t: "end", cid, reason });
    }
  }

  // Gniazdo zniknęło (zamknięte lub zerwane).
  dropSocket(ws, broadcast = true) {
    const a = this.att(ws);
    if (a.cid) {
      if (a.st === "active") {
        // rozmowa trwa: media idą bezpośrednio, więc dajemy stronie czas na powrót (resume)
        for (const pw of this.ctx.getWebSockets(a.peer)) {
          const pa = this.att(pw);
          if (pa.cid !== a.cid) continue;
          pa.lost = Date.now(); this.save(pw, pa);
          this.send(pw, { t: "peerlost", cid: a.cid });
        }
      } else {
        this.endCall(ws, "lost");
      }
    }
    if (broadcast) this.broadcastPresence();
  }

  async ensureAlarm() {
    if (this.ctx.getWebSockets().length && (await this.ctx.storage.getAlarm()) === null)
      await this.ctx.storage.setAlarm(Date.now() + (parseInt(this.env.SWEEP_MS, 10) || SWEEP_MS));
  }

  // Sprzątanie: zerwane połączenia, rozmowy bez drugiej strony.
  async alarm() {
    let changed = false;
    for (const ws of this.ctx.getWebSockets()) {
      if (!this.fresh(ws)) {
        try { ws.close(4001, "stale"); } catch { /* */ }
        this.dropSocket(ws, false);
        changed = true;
        continue;
      }
      const a = this.att(ws);
      if (a.cid && a.lost && Date.now() - a.lost > RESUME_MS) { this.endCall(ws, "lost"); changed = true; }
    }
    if (changed) this.broadcastPresence();
    await this.ensureAlarm();
  }

  // ---------------------------------------------------------------- zdarzenia
  async webSocketMessage(ws, message) {
    if (typeof message !== "string" || message.length > MAX_FRAME) return;
    const a = this.att(ws);
    const now = Date.now();
    // ograniczenie liczby wiadomości z jednego połączenia
    if (now - a.rl[0] > RATE_WINDOW_MS) a.rl = [now, 0];
    if (++a.rl[1] > RATE_MAX) { try { ws.close(4008, "rate"); } catch { /* */ } this.dropSocket(ws); return; }
    a.seen = now;
    this.save(ws, a);

    let m;
    try { m = JSON.parse(message); } catch { return; }
    if (!m || typeof m.t !== "string") return;
    try {
      switch (m.t) {
        case "call": return this.onCall(ws, a, m);
        case "accept": return this.onAccept(ws, a, m);
        case "end": return this.onEnd(ws, a, m);
        case "sig": return this.onSig(ws, a, m);
        case "resume": return this.onResume(ws, a, m);
        case "turn": return this.send(ws, { t: "turn", ...(await this.turn()) });
        default: return;
      }
    } catch {
      this.send(ws, { t: "error" });
    }
  }

  async turn() {
    const r = await iceServers(this.env);
    return { ice: r.ice, relay: r.relay };
  }

  webSocketClose(ws) { this.dropSocket(ws); }
  webSocketError(ws) { this.dropSocket(ws); }

  onCall(ws, a, m) {
    const cid = String(m.cid || "").slice(0, 64), to = String(m.to || "");
    if (!/^[A-Za-z0-9_-]{8,64}$/.test(cid)) return;
    if (a.cid) return this.send(ws, { t: "end", cid, reason: "busy" });          // sam już rozmawiam
    const rows = this.listMembers();
    const caller = rows.find((m) => m.id === a.id), callee = rows.find((m) => m.id === to);
    const admins = this.adminIds();
    // konto NEW dzwoni tylko do administratora, a dzwonić do konta NEW może tylko administrator
    const allowed = !!caller && !!callee && to !== a.id
      && (caller.status !== "pending" || (callee.status === "active" && admins.has(to)))
      && (callee.status !== "pending" || !!a.adm);
    const target = allowed ? this.sockOf(to) : null;
    if (!target) return this.send(ws, { t: "end", cid, reason: "offline" });
    const ta = this.att(target);
    if (ta.cid) return this.send(ws, { t: "end", cid, reason: "busy" });
    const video = !!m.video;
    Object.assign(a, { cid, peer: to, st: "ringing", lost: 0 }); this.save(ws, a);
    Object.assign(ta, { cid, peer: a.id, st: "ringing", lost: 0 }); this.save(target, ta);
    this.send(target, { t: "ring", cid, from: a.id, name: caller.name, video });
    this.send(ws, { t: "ringing", cid });
    this.broadcastPresence();
  }

  onAccept(ws, a, m) {
    if (!a.cid || a.cid !== m.cid || a.st !== "ringing") return;
    // odbiera tylko ten, do kogo dzwoniono: jego peer też ma "ringing" i jest dzwoniącym
    for (const pw of this.ctx.getWebSockets(a.peer)) {
      const pa = this.att(pw);
      if (pa.cid !== a.cid) continue;
      pa.st = "active"; this.save(pw, pa);
      a.st = "active"; this.save(ws, a);
      this.send(pw, { t: "accepted", cid: a.cid });
      return;
    }
    this.endCall(ws, "lost");
  }

  onEnd(ws, a, m) {
    if (!a.cid || a.cid !== m.cid) return;
    const reason = END_REASONS.has(m.reason) ? m.reason : "hangup";
    this.endCall(ws, reason);
    this.broadcastPresence();
  }

  onSig(ws, a, m) {
    if (!a.cid || a.cid !== m.cid || a.st !== "active" && a.st !== "ringing") return;
    if (m.data === undefined) return;
    for (const pw of this.ctx.getWebSockets(a.peer)) {
      const pa = this.att(pw);
      if (pa.cid === a.cid) { this.send(pw, { t: "sig", cid: a.cid, data: m.data }); return; }
    }
    this.send(ws, { t: "peerlost", cid: a.cid });
  }

  onResume(ws, a, m) {
    const cid = String(m.cid || ""), peer = String(m.peer || "");
    for (const pw of this.ctx.getWebSockets(peer)) {
      const pa = this.att(pw);
      if (pa.cid !== cid || pa.peer !== a.id || pa.st !== "active") continue;
      Object.assign(a, { cid, peer, st: "active", lost: 0 }); this.save(ws, a);
      pa.lost = 0; this.save(pw, pa);
      this.send(ws, { t: "resumed", cid });
      this.send(pw, { t: "peerback", cid });
      this.broadcastPresence();
      return;
    }
    this.send(ws, { t: "end", cid, reason: "gone" });
  }
}
