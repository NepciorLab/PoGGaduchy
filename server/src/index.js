// Serwer sygnalizacyjny PoGGaduchów (Cloudflare Workers + jeden Durable Object).
//
// Serwer NIE widzi obrazu ani dźwięku (idą bezpośrednio między komputerami, WebRTC, szyfrowane).
// Przekazuje tylko: kto jest w sieci, "X dzwoni do Y", odebrał / odrzucił / rozłączył oraz dane negocjacji połączenia.
//
// Dostęp: każda instalacja ma długi, losowy klucz. Serwer zna tylko skróty SHA-256 dozwolonych kluczy
// (sekret MEMBERS: [{"id":"mama","name":"Mama","hash":"<sha256 klucza, hex>"}]).
// Klucz jest wysyłany w nagłówku "Authorization: Bearer <klucz>" (nigdy w adresie).
//
// HTTP:
//   GET /              -> "PoGGaduchy: OK"                       (publiczne, bez danych)
//   GET /api/status    -> {ok, proto, id, name, turn}            (wymaga klucza; ?turn=1 sprawdza też TURN)
//   GET /ws            -> WebSocket (wymaga klucza)
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
//   {t:"welcome", proto, me, directory, online, serverTime}
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

const PROTO = 1;
const MAX_FRAME = 16000;            // SDP ma kilka kilobajtów; limit chroni przed śmieciami
const STALE_MS = 100_000;           // klient pinguje co ~25 s; brak życia przez 100 s = zerwane połączenie
const SWEEP_MS = 60_000;            // jak często obiekt sprząta zerwane połączenia (alarm)
const RESUME_MS = 90_000;           // tyle czekamy na powrót strony, która straciła połączenie w trakcie rozmowy
const RATE_WINDOW_MS = 10_000, RATE_MAX = 120;   // ruch ICE przychodzi seriami; to i tak daleko od nadużyć
const END_REASONS = new Set(["rejected", "hangup", "cancel", "timeout", "error"]);
const FALLBACK_ICE = [{ urls: ["stun:stun.cloudflare.com:3478"] }];

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

let membersCache = { raw: null, list: [] };
function members(env) {
  const raw = env.MEMBERS || "[]";
  if (membersCache.raw !== raw) {
    let list = [];
    try {
      list = JSON.parse(raw).filter((m) => m && /^[a-z0-9_-]{1,32}$/.test(m.id) && /^[0-9a-f]{64}$/.test(m.hash))
        .map((m) => ({ id: m.id, name: String(m.name || m.id).slice(0, 60), hash: m.hash }));
    } catch { /* błędny sekret: nikt się nie zaloguje */ }
    membersCache = { raw, list };
  }
  return membersCache.list;
}

async function authenticate(request, env) {
  const m = /^Bearer ([A-Za-z0-9_-]{32,128})$/.exec(request.headers.get("Authorization") || "");
  if (!m) return null;
  const hash = await sha256Hex(m[1]);
  let found = null;
  for (const mem of members(env)) if (safeEqual(mem.hash, hash)) found = mem;   // bez wcześniejszego wyjścia
  return found;
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

// ---------------------------------------------------------------- Worker
export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (url.pathname === "/" && request.method === "GET")
      return new Response("PoGGaduchy: OK\n", { headers: { "content-type": "text/plain; charset=utf-8", "cache-control": "no-store" } });

    if (url.pathname !== "/ws" && url.pathname !== "/api/status") return new Response("Not found", { status: 404 });

    const me = await authenticate(request, env);
    if (!me) {
      // Zła próba: liczymy ją i po przekroczeniu limitu odpowiadamy 429 (bez ujawniania, czy klucz był bliski).
      const ip = request.headers.get("CF-Connecting-IP") || "unknown";
      if (env.AUTH_FAIL_LIMITER) {
        const { success } = await env.AUTH_FAIL_LIMITER.limit({ key: ip });
        if (!success) return new Response("Too many attempts", { status: 429, headers: { "retry-after": "60" } });
      }
      return new Response("Unauthorized", { status: 401 });
    }

    if (url.pathname === "/api/status") {
      const out = { ok: true, proto: PROTO, id: me.id, name: me.name, turn: !!(env.TURN_KEY_ID && env.TURN_KEY_API_TOKEN) };
      if (url.searchParams.get("turn") === "1") { const t = await iceServers(env); out.turnWorks = t.relay; if (t.error) out.turnError = t.error; }
      return json(out);
    }

    if (request.headers.get("Upgrade") !== "websocket") return new Response("WebSocket expected", { status: 426 });
    const headers = new Headers(request.headers);
    headers.delete("Authorization");
    headers.set("X-Member-Id", me.id);
    return env.HUB.get(env.HUB.idFromName("main")).fetch(new Request(request, { headers }));
  },
};

// ---------------------------------------------------------------- Durable Object
export class Hub extends DurableObject {
  constructor(ctx, env) {
    super(ctx, env);
    // Podtrzymywanie połączenia: "ping" -> "pong" bez budzenia obiektu (nie liczy się do limitów).
    ctx.setWebSocketAutoResponse(new WebSocketRequestResponsePair("ping", "pong"));
  }

  async fetch(request) {
    const id = request.headers.get("X-Member-Id");
    const mem = members(this.env).find((m) => m.id === id);
    if (!mem) return new Response("Unauthorized", { status: 401 });

    const pair = new WebSocketPair();
    const [client, server] = Object.values(pair);
    // Hibernacja: obiekt "śpi" między wiadomościami. Znacznik = id osoby (jedno połączenie na osobę).
    this.ctx.acceptWebSocket(server, [mem.id]);
    const now = Date.now();
    server.serializeAttachment({ id: mem.id, t0: now, seen: now, cid: null, peer: null, st: null, lost: 0, rl: [now, 0] });

    for (const old of this.ctx.getWebSockets(mem.id)) {
      if (old === server) continue;
      this.send(old, { t: "kicked" });
      try { old.close(4000, "replaced"); } catch { /* */ }
      this.dropSocket(old, false);
    }
    this.send(server, { t: "welcome", proto: PROTO, me: { id: mem.id, name: mem.name },
      directory: members(this.env).map((m) => ({ id: m.id, name: m.name })), online: this.online(), serverTime: now });
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
    return Date.now() - last < STALE_MS;
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

  broadcastPresence() {
    const msg = { t: "presence", online: this.online() };
    for (const ws of this.ctx.getWebSockets()) this.send(ws, msg);
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
      await this.ctx.storage.setAlarm(Date.now() + SWEEP_MS);
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
    const target = to !== a.id && members(this.env).some((x) => x.id === to) ? this.sockOf(to) : null;
    if (!target) return this.send(ws, { t: "end", cid, reason: "offline" });
    const ta = this.att(target);
    if (ta.cid) return this.send(ws, { t: "end", cid, reason: "busy" });
    const video = !!m.video;
    Object.assign(a, { cid, peer: to, st: "ringing", lost: 0 }); this.save(ws, a);
    Object.assign(ta, { cid, peer: a.id, st: "ringing", lost: 0 }); this.save(target, ta);
    this.send(target, { t: "ring", cid, from: a.id, video });
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
