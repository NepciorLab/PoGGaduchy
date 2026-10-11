using System.IO;
using System.Text;
using System.Text.Json;

namespace PoGGaduchy;

/// <summary>
/// Tożsamość tej kopii programu: adres serwera i tajny klucz. Zapisana w pliku kod-instalacji.txt obok programu
/// (przygotowuje go osoba, która konfiguruje rodzinę – skrypt tools\dodaj-osobe.ps1). Format: "PGG1." + JSON w base64url:
/// {"s":"https://adres-serwera","k":"tajny klucz","i":"id osoby"}.
/// </summary>
public sealed record Identity(string Server, string Key, string Id)
{
    const string Prefix = "PGG1.";

    public static string FilePath => Path.Combine(SettingsStore.DataDir, "kod-instalacji.txt");

    public Uri WebSocketUri
    {
        get
        {
            var b = new UriBuilder(Server) { Scheme = Server.StartsWith("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws" };
            b.Path = "/ws";
            return b.Uri;
        }
    }

    public Uri HttpUri(string path) => new UriBuilder(Server) { Path = path }.Uri;

    public static Identity? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            return Parse(File.ReadAllText(FilePath));
        }
        catch (Exception ex) { Log.Write("Kod instalacji: " + ex.Message); return null; }
    }

    public static Identity? Parse(string text)
    {
        text = text.Trim();
        if (!text.StartsWith(Prefix)) return null;
        var b64 = text[Prefix.Length..].Replace('-', '+').Replace('_', '/');
        b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(b64)));
        var r = doc.RootElement;
        var server = r.GetProperty("s").GetString() ?? "";
        var key = r.GetProperty("k").GetString() ?? "";
        var id = r.TryGetProperty("i", out var i) ? i.GetString() ?? "" : "";
        if (!Uri.TryCreate(server, UriKind.Absolute, out var u)) return null;
        // Zwykły internet tylko przez HTTPS; HTTP dopuszczamy wyłącznie dla serwera testowego na tym komputerze.
        if (u.Scheme != Uri.UriSchemeHttps && !(u.Scheme == Uri.UriSchemeHttp && u.IsLoopback)) return null;
        if (key.Length < 32) return null;
        return new Identity(server.TrimEnd('/'), key, id);
    }

    public static void Save(string code)
    {
        Directory.CreateDirectory(SettingsStore.DataDir);
        File.WriteAllText(FilePath, code.Trim());
    }
}
