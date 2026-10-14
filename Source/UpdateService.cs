using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PoGGaduchy;

public sealed record UpdateInfo(Version Version, string Tag, string AssetName, string DownloadUrl, string? Sha256, string? Sha256Url, string SigUrl, string PageUrl);

/// <summary>
/// Aktualizacje z wydań (GitHub Releases) repozytorium <see cref="AppInfo.Repository"/>.
/// Przepis na wydanie: tag "vX.Y.Z" + plik "PoGGaduchy-X.Y.Z-win-x64.zip" (z PoGGaduchy.exe w środku) i plik sumy
/// "PoGGaduchy-X.Y.Z-win-x64.zip.sha256". Bez sumy SHA-256 (z GitHuba lub z pliku .sha256) aktualizacja nie jest instalowana.
/// Dodatkowo wymagany jest podpis autora "PoGGaduchy-X.Y.Z-win-x64.zip.sig" (patrz <see cref="UpdateSignature"/>): suma chroni
/// przed uszkodzeniem pliku, podpis przed podmianą wydania przez kogoś, kto przejąłby konto GitHub.
/// Dopóki w repozytorium nie ma żadnego wydania, sprawdzanie kończy się po cichu.
/// </summary>
public static class UpdateService
{
    static readonly HttpClient Http = CreateClient();
    static readonly Regex ZipAsset = new(@"^PoGGaduchy-.*win-x64\.zip$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static string ExePath => AppInfo.ExePath;
    static string NewPath => ExePath + ".new";
    static string OldPath => ExePath + ".old";
    static string TagPath => ExePath + ".new.txt";

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("PoGGaduchy/" + AppInfo.VersionText);
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    /// <summary>Pyta GitHub o najnowsze wydanie. Zwraca null, gdy brak nowszej wersji (lub brak wydań).</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        var url = $"https://api.github.com/repos/{AppInfo.Repository}/releases/latest";
        using var resp = await Http.GetAsync(url, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) { Log.Write("Aktualizacje: brak opublikowanych wydań."); return null; }
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) { Log.Write("Aktualizacje: nieznany format tagu " + tag); return null; }
        if (version <= Normalize(AppInfo.Version)) return null;

        JsonElement? zip = null, sum = null, sig = null;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            if (ZipAsset.IsMatch(name)) zip = a;
            else if (name.EndsWith(".zip.sha256", StringComparison.OrdinalIgnoreCase)) sum = a;
            else if (name.EndsWith(".zip.sig", StringComparison.OrdinalIgnoreCase)) sig = a;
        }
        if (zip is not { } asset) { Log.Write("Aktualizacje: wydanie " + tag + " nie ma pasującego pliku."); return null; }

        var download = asset.GetProperty("browser_download_url").GetString() ?? "";
        if (!IsTrustedUrl(download)) { Log.Write("Aktualizacje: odrzucono adres " + download); return null; }

        string? sha = null, shaUrl = null;
        if (asset.TryGetProperty("digest", out var d) && d.GetString() is { } digest && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            sha = digest[7..];
        if (sum is { } s && s.GetProperty("browser_download_url").GetString() is { } su && IsTrustedUrl(su)) shaUrl = su;
        if (sha == null && shaUrl == null) { Log.Write("Aktualizacje: wydanie " + tag + " nie ma sumy SHA-256, pomijam."); return null; }

        // Podpis dodaje autor po zbudowaniu wydania, więc przez chwilę może go brakować: wtedy sprawdzimy ponownie przy następnej okazji.
        if (UpdateSignature.EmbeddedKey() == null) { Log.Write("Aktualizacje: program nie ma klucza publicznego do sprawdzania podpisów, pomijam."); return null; }
        if (sig is not { } sg || sg.GetProperty("browser_download_url").GetString() is not { } sigUrl || !IsTrustedUrl(sigUrl))
        { Log.Write("Aktualizacje: wydanie " + tag + " nie ma podpisu autora (jeszcze), pomijam."); return null; }

        return new UpdateInfo(version, tag, asset.GetProperty("name").GetString() ?? "", download, sha, shaUrl, sigUrl,
            root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "");
    }

    static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    static bool IsTrustedUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) return false;
        var host = u.Host.ToLowerInvariant();
        return host == "github.com" || host.EndsWith(".github.com") || host.EndsWith(".githubusercontent.com");
    }

    /// <summary>Pobiera aktualizację do pliku PoGGaduchy.exe.new obok programu. Nie podmienia działającego pliku.</summary>
    public static async Task DownloadAsync(UpdateInfo info, CancellationToken ct = default)
    {
        var tmp = Path.Combine(Path.GetDirectoryName(ExePath)!, "update-download.tmp");
        try
        {
            using (var resp = await Http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                var final = resp.RequestMessage?.RequestUri;
                if (final == null || !IsTrustedUrl(final.ToString())) throw new InvalidOperationException("Nieoczekiwany adres pobierania.");
                await using var fs = File.Create(tmp);
                await resp.Content.CopyToAsync(fs, ct);
            }

            var expected = info.Sha256;
            if (expected == null && info.Sha256Url != null)
            {
                var text = await Http.GetStringAsync(info.Sha256Url, ct);
                expected = Regex.Match(text, "[0-9a-fA-F]{64}").Value;
            }
            if (string.IsNullOrEmpty(expected)) throw new InvalidOperationException("Brak sumy kontrolnej wydania.");
            string actual;
            await using (var fs = File.OpenRead(tmp))
            {
                actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Suma kontrolna pobranego pliku się nie zgadza.");
            }

            var signature = await Http.GetStringAsync(info.SigUrl, ct);
            var v = info.Version;
            if (!UpdateSignature.Verify(UpdateSignature.EmbeddedKey(), $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}", actual, signature))
                throw new InvalidOperationException("Podpis autora wydania jest nieprawidłowy, odrzucam aktualizację.");

            if (File.Exists(NewPath)) File.Delete(NewPath);
            using (var zip = ZipFile.OpenRead(tmp))
            {
                var entry = zip.Entries.FirstOrDefault(e => e.Name.Equals("PoGGaduchy.exe", StringComparison.OrdinalIgnoreCase))
                            ?? throw new InvalidOperationException("W archiwum nie ma PoGGaduchy.exe.");
                entry.ExtractToFile(NewPath, true);
            }

            if (!LooksLikeProgram(NewPath)) { File.Delete(NewPath); throw new InvalidOperationException("Pobrany plik nie jest programem."); }
            File.WriteAllText(TagPath, info.Tag);
            Log.Write("Pobrano aktualizację " + info.Tag);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    static bool LooksLikeProgram(string path)
    {
        var fi = new FileInfo(path);
        if (fi.Length < 1_000_000) return false;
        using var fs = fi.OpenRead();
        return fs.ReadByte() == 'M' && fs.ReadByte() == 'Z';
    }

    public static bool IsUpdateReady => File.Exists(NewPath);

    public static string PendingTag { get { try { return File.Exists(TagPath) ? File.ReadAllText(TagPath).Trim() : ""; } catch { return ""; } } }

    /// <summary>Podmienia program na pobraną wersję i uruchamia ją. Zwraca true, gdy bieżący proces ma się zakończyć.</summary>
    public static bool ApplyPendingAndRestart()
    {
        try
        {
            if (!File.Exists(NewPath) || !LooksLikeProgram(NewPath)) return false;
            if (File.Exists(OldPath)) File.Delete(OldPath);
            File.Move(ExePath, OldPath);              // działający plik można przemianować
            File.Move(NewPath, ExePath);
            try { File.Delete(TagPath); } catch { }
            Process.Start(new ProcessStartInfo(ExePath, $"--after-update {Environment.ProcessId}") { UseShellExecute = true });
            Log.Write("Zainstalowano aktualizację, uruchamiam ponownie.");
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("Nie udało się zainstalować aktualizacji: " + ex.Message);
            try { if (!File.Exists(ExePath) && File.Exists(OldPath)) File.Move(OldPath, ExePath); } catch { }
            return false;
        }
    }

    public static void CleanupOld()
    {
        try { if (File.Exists(OldPath)) File.Delete(OldPath); } catch { }
    }
}

/// <summary>Stan aktualizacji dla okna ustawień i działania w tle.</summary>
public sealed class UpdateManager : Observable
{
    public static UpdateManager Instance { get; } = new();

    string _status = "";
    bool _busy;

    public string Status { get => _status; private set => Set(ref _status, value); }
    public bool Busy { get => _busy; private set => Set(ref _busy, value); }
    public bool ReadyToInstall => UpdateService.IsUpdateReady;

    /// <summary>Numer pobranej, czekającej wersji (np. "1.0.1") albo pusty tekst.</summary>
    public string PendingVersion => UpdateService.PendingTag.TrimStart('v', 'V');

    public async Task CheckAndDownloadAsync(bool manual)
    {
        if (Busy) return;
        Busy = true;
        try
        {
            if (manual) Status = "Sprawdzam…";
            var info = await UpdateService.CheckAsync();
            if (info == null)
            {
                Status = UpdateService.IsUpdateReady ? "Nowa wersja jest pobrana i czeka na instalację." : "Masz najnowszą wersję.";
            }
            else
            {
                Status = $"Pobieram wersję {info.Version.Major}.{info.Version.Minor}.{info.Version.Build}…";
                await UpdateService.DownloadAsync(info);
                Status = $"Wersja {info.Version.Major}.{info.Version.Minor}.{info.Version.Build} jest pobrana i czeka na instalację.";
            }
        }
        catch (Exception ex)
        {
            Log.Write("Aktualizacja: " + ex.Message);
            Status = manual ? "Nie udało się sprawdzić aktualizacji (brak internetu?)." : Status;
        }
        finally
        {
            Busy = false;
            Raise(nameof(ReadyToInstall));
            Raise(nameof(PendingVersion));
        }
    }
}
