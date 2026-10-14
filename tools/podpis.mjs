// Podpisy wydań (ECDSA P-256 + SHA-256). Używane przez generuj-klucz-podpisu.ps1 i podpisz-wydanie.ps1.
//   node podpis.mjs gen  <plik-klucza-prywatnego.pem> <plik-klucza-publicznego.txt>
//   node podpis.mjs sign <plik.zip> <wersja> <plik-podpisu.sig> <plik-klucza-prywatnego.pem>
// Hasło do klucza przychodzi ze zmiennej środowiskowej PGG_KEY_PASS (nigdy z argumentów).
// Podpisywany tekst musi być identyczny z Source/UpdateSignature.cs:  "PoGGaduchy-release\n<wersja>\n<sha256 zipu, małe litery>".
import crypto from "node:crypto";
import fs from "node:fs";

const [cmd, ...args] = process.argv.slice(2);
const pass = process.env.PGG_KEY_PASS;
if (!pass || pass.length < 8) { console.error("Brak hasła w PGG_KEY_PASS (min. 8 znaków)."); process.exit(2); }

if (cmd === "gen") {
  const [privFile, pubFile] = args;
  if (fs.existsSync(privFile)) { console.error("Plik klucza już istnieje, nie nadpisuję: " + privFile); process.exit(2); }
  const { publicKey, privateKey } = crypto.generateKeyPairSync("ec", {
    namedCurve: "prime256v1",
    publicKeyEncoding: { type: "spki", format: "der" },
    privateKeyEncoding: { type: "pkcs8", format: "pem", cipher: "aes-256-cbc", passphrase: pass },
  });
  fs.writeFileSync(privFile, privateKey, { encoding: "utf8", flag: "wx" });
  fs.writeFileSync(pubFile, publicKey.toString("base64") + "\n", "utf8");
  console.log("OK");
} else if (cmd === "sign") {
  const [zipFile, version, sigFile, privFile] = args;
  if (!/^\d+\.\d+\.\d+$/.test(version || "")) { console.error("Wersja ma mieć postać X.Y.Z"); process.exit(2); }
  const sha = crypto.createHash("sha256").update(fs.readFileSync(zipFile)).digest("hex");
  const message = Buffer.from(`PoGGaduchy-release\n${version}\n${sha}`, "utf8");
  let sig;
  try { sig = crypto.sign("sha256", message, { key: fs.readFileSync(privFile, "utf8"), passphrase: pass, dsaEncoding: "ieee-p1363" }); }
  catch { console.error("Nie udało się podpisać (złe hasło lub uszkodzony klucz)."); process.exit(3); }
  fs.writeFileSync(sigFile, sig.toString("base64") + "\n", "utf8");
  console.log("SHA256 " + sha);
} else {
  console.error("Użycie: gen | sign");
  process.exit(2);
}
