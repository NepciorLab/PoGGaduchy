// Tworzy .dev.vars (sekrety lokalnego serwera testowego) i test/keys.json (klucze testowe) z trzema osobami testowymi.
// Uruchomienie:  node test/make-dev-vars.mjs        (klucze są losowe; plik .dev.vars NIE trafia do repozytorium)
import { createHash, randomBytes } from "node:crypto";
import { writeFileSync, existsSync, readFileSync } from "node:fs";

const names = { ala: "Ala", bartek: "Bartek", celina: "Celina" };
const keys = existsSync("test/keys.json") ? JSON.parse(readFileSync("test/keys.json", "utf8")) : {};
for (const id of Object.keys(names)) keys[id] ??= randomBytes(32).toString("base64url");
const members = Object.keys(names).map((id) => ({ id, name: names[id], hash: createHash("sha256").update(keys[id]).digest("hex") }));
writeFileSync("test/keys.json", JSON.stringify(keys, null, 2));
let vars = `MEMBERS=${JSON.stringify(members)}\n`;
// Prawdziwy TURN do testów lokalnych: dopisz ręcznie TURN_KEY_ID=... i TURN_KEY_API_TOKEN=... (nigdy do repozytorium).
if (existsSync(".dev.vars")) {
  const old = readFileSync(".dev.vars", "utf8").split(/\r?\n/).filter((l) => /^TURN_/.test(l));
  if (old.length) vars += old.join("\n") + "\n";
}
writeFileSync(".dev.vars", vars);
console.log("Zapisano .dev.vars i test/keys.json (osoby: " + Object.keys(names).join(", ") + ")");
