using System.Net.Http;
using System.Text.Json;
using Microsoft.Win32;

namespace PoGGaduchy;

/// <summary>Tryb --diagnoza: tylko odczyt, wynik trafia do log.txt. Nie łączy się jako osoba (nie wypiera działającego programu).</summary>
internal static class Diagnostics
{
    public static async Task RunAsync()
    {
        Log.Write($"DIAGNOZA: wersja {AppInfo.VersionText}, dane: {SettingsStore.DataDir}, system: {Environment.OSVersion.VersionString}");

        var wv = WebViewHost.RuntimeVersion;
        Log.Write("DIAGNOZA: WebView2: " + (wv ?? "BRAK (rozmowy nie będą działać)"));

        Log.Write("DIAGNOZA: kamera wykryta: " + await CameraService.HasCameraAsync());
        Log.Write("DIAGNOZA: mikrofony wykryte: " + await CameraService.CountMicrophonesAsync());
        Log.Write("DIAGNOZA: zgoda Windows na kamerę dla programów klasycznych: " + Consent("webcam"));
        Log.Write("DIAGNOZA: zgoda Windows na mikrofon dla programów klasycznych: " + Consent("microphone"));
        Log.Write("DIAGNOZA: autostart z Windows: " + (Autostart.IsEnabled() ? "włączony" : "wyłączony"));

        var id = Identity.Load();
        if (id == null)
        {
            Log.Write("DIAGNOZA: kod instalacji: BRAK lub nieprawidłowy (" + Identity.FilePath + ")");
            return;
        }
        Log.Write($"DIAGNOZA: kod instalacji: serwer {id.Server}, osoba {id.Id}");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var req = new HttpRequestMessage(HttpMethod.Get, id.HttpUri("/api/status") + "?turn=1");
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + id.Key);
            using var resp = await http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            Log.Write($"DIAGNOZA: serwer odpowiedział {(int)resp.StatusCode}: {(resp.IsSuccessStatusCode ? Summarize(body) : "odmowa (zły kod instalacji?)")}");
        }
        catch (Exception ex) { Log.Write("DIAGNOZA: serwer: BRAK POŁĄCZENIA (" + ex.Message + ")"); }
    }

    static string Summarize(string body)
    {
        try
        {
            using var d = JsonDocument.Parse(body);
            var r = d.RootElement;
            return $"osoba={r.GetProperty("id").GetString()}, protokół={r.GetProperty("proto")}, TURN skonfigurowany={r.GetProperty("turn")}" +
                   (r.TryGetProperty("turnWorks", out var w) ? $", TURN działa={w}" : "");
        }
        catch { return body; }
    }

    static string Consent(string capability)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\" + capability);
            var v = k?.GetValue("Value") as string;
            return v ?? "(brak wpisu – domyślnie dozwolone)";
        }
        catch { return "(nie można odczytać)"; }
    }
}
