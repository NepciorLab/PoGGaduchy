// Tworzy kod-instalacji.txt dla osób testowych (z test/keys.json) w folderach ..\_probe\<id>\ – do uruchamiania kilku kopii programu na jednym komputerze.
// Do folderu osoby "ala" zapisuje też Admin.PMa (testowy tekst administratora), więc "ala" jest administratorem w testach.
//   node test/make-install-codes.mjs [adres-serwera]     (domyślnie http://127.0.0.1:8787)
import { readFileSync, writeFileSync, mkdirSync } from "node:fs";

const server = process.argv[2] || "http://127.0.0.1:8787";
const keys = JSON.parse(readFileSync("test/keys.json", "utf8"));
for (const [id, k] of Object.entries(keys)) {
  if (id === "_admin") { mkdirSync("../_probe/ala", { recursive: true }); writeFileSync("../_probe/ala/Admin.PMa", k); continue; }
  const code = "PGG1." + Buffer.from(JSON.stringify({ s: server, k, i: id })).toString("base64url");
  mkdirSync(`../_probe/${id}`, { recursive: true });
  writeFileSync(`../_probe/${id}/kod-instalacji.txt`, code);
  console.log(`../_probe/${id}/kod-instalacji.txt`);
}
