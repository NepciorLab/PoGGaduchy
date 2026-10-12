using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PoGGaduchy;

/// <summary>
/// Plik administratora "Admin.PMa" w katalogu programu. Jego zawartość (tajny tekst) jest wysyłana do serwera, który zna tylko
/// jego skrót (sekret ADMIN_HASH). Samo istnienie pliku nic nie daje: o uprawnieniach decyduje serwer.
/// </summary>
public static class AdminFile
{
    public const string FileName = "Admin.PMa";

    public static string FilePath => Path.Combine(SettingsStore.DataDir, FileName);

    /// <summary>Tajny tekst z pliku (bez białych znaków na brzegach) albo null, gdy pliku nie ma lub jest pusty.</summary>
    public static string? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var t = File.ReadAllText(FilePath, Encoding.UTF8).Trim();
            return t.Length is > 0 and <= 256 ? t : null;
        }
        catch (Exception ex) { Log.Write("Admin.PMa: " + ex.Message); return null; }
    }
}

/// <summary>Wyniki wywołań serwera, które mogą się nie udać z różnych powodów.</summary>
public enum ApiResult { Ok, NotFound, Forbidden, Conflict, TooLarge, BadRequest, Unauthorized, NetworkError }

/// <summary>Wywołania HTTP serwera: obrazy osób i panel administratora (klucz osoby w nagłówku, nigdy w adresie).</summary>
public sealed class ServerApi : Observable
{
    readonly Identity _identity;
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    bool _isAdmin;
    string _adminNote = "";

    public ServerApi(Identity identity)
    {
        _identity = identity;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("PoGGaduchy/" + AppInfo.VersionText);
    }

    /// <summary>Serwer potwierdził, że ta kopia jest administratorem (plik Admin.PMa pasuje).</summary>
    public bool IsAdmin { get => _isAdmin; private set => Set(ref _isAdmin, value); }

    /// <summary>Wyjaśnienie, gdy plik administratora istnieje, ale serwer go nie przyjmuje (do Ustawień).</summary>
    public string AdminNote { get => _adminNote; private set => Set(ref _adminNote, value); }

    HttpRequestMessage Req(HttpMethod m, string path, bool admin = false)
    {
        var r = new HttpRequestMessage(m, _identity.HttpUri(path));
        r.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _identity.Key);
        if (admin && AdminFile.Load() is { } k) r.Headers.TryAddWithoutValidation("X-Admin-Key", k);
        return r;
    }

    static ApiResult Map(HttpStatusCode code) => code switch
    {
        HttpStatusCode.OK => ApiResult.Ok,
        HttpStatusCode.NotFound => ApiResult.NotFound,
        HttpStatusCode.Forbidden => ApiResult.Forbidden,
        HttpStatusCode.Unauthorized => ApiResult.Unauthorized,
        HttpStatusCode.Conflict => ApiResult.Conflict,
        HttpStatusCode.RequestEntityTooLarge => ApiResult.TooLarge,
        HttpStatusCode.TooManyRequests => ApiResult.Forbidden,
        _ => ApiResult.BadRequest,
    };

    async Task<ApiResult> SendAsync(HttpRequestMessage req)
    {
        try
        {
            using var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) Log.Write($"Serwer: {req.Method} {req.RequestUri!.AbsolutePath} -> {(int)resp.StatusCode}");
            return Map(resp.StatusCode);
        }
        catch (Exception ex)
        {
            Log.Write($"Serwer: {req.Method} {req.RequestUri!.AbsolutePath}: {ex.Message}");
            return ApiResult.NetworkError;
        }
    }

    /// <summary>Pyta serwer, czy plik Admin.PMa daje uprawnienia administratora (wynik w IsAdmin i AdminNote).</summary>
    public async Task CheckAdminAsync()
    {
        var key = AdminFile.Load();
        if (key == null) { IsAdmin = false; AdminNote = ""; return; }
        try
        {
            using var resp = await _http.SendAsync(Req(HttpMethod.Get, "/api/status", admin: true));
            if (!resp.IsSuccessStatusCode) return;               // brak połączenia/odmowa: zostawiamy poprzedni stan
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var admin = doc.RootElement.TryGetProperty("admin", out var a) && a.GetBoolean();
            IsAdmin = admin;
            AdminNote = admin ? "" : "Plik Admin.PMa nie pasuje do tego serwera, więc ta kopia nie ma uprawnień administratora.";
            Log.Write("Administrator: " + (admin ? "potwierdzony przez serwer" : "plik Admin.PMa odrzucony przez serwer"));
        }
        catch (Exception ex) { Log.Write("Sprawdzanie administratora: " + ex.Message); }
    }

    /// <summary>Pobiera obraz osoby (JPEG). null przy braku obrazu (404); wyjątek przy problemie sieci.</summary>
    public async Task<byte[]?> GetAvatarAsync(string id)
    {
        using var resp = await _http.SendAsync(Req(HttpMethod.Get, "/api/avatar/" + Uri.EscapeDataString(id)));
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync();
    }

    /// <summary>Zapisuje obraz osoby (swój albo – jako administrator – cudzy).</summary>
    public Task<ApiResult> PutAvatarAsync(string id, byte[] jpeg, bool asAdmin)
    {
        var r = Req(HttpMethod.Put, "/api/avatar/" + Uri.EscapeDataString(id), asAdmin);
        r.Content = new ByteArrayContent(jpeg);
        r.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        return SendAsync(r);
    }

    public Task<ApiResult> DeleteAvatarAsync(string id, bool asAdmin) =>
        SendAsync(Req(HttpMethod.Delete, "/api/avatar/" + Uri.EscapeDataString(id), asAdmin));

    static StringContent JsonBody(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");

    public Task<ApiResult> AddPersonAsync(string id, string name, string hash)
    {
        var r = Req(HttpMethod.Post, "/api/admin/people", true);
        r.Content = JsonBody(new { id, name, hash });
        return SendAsync(r);
    }

    public Task<ApiResult> RenamePersonAsync(string id, string name)
    {
        var r = Req(HttpMethod.Patch, "/api/admin/people/" + Uri.EscapeDataString(id), true);
        r.Content = JsonBody(new { name });
        return SendAsync(r);
    }

    public Task<ApiResult> SetKeyHashAsync(string id, string hash)
    {
        var r = Req(HttpMethod.Patch, "/api/admin/people/" + Uri.EscapeDataString(id), true);
        r.Content = JsonBody(new { hash });
        return SendAsync(r);
    }

    public Task<ApiResult> RemovePersonAsync(string id) =>
        SendAsync(Req(HttpMethod.Delete, "/api/admin/people/" + Uri.EscapeDataString(id), true));

    // ---------- klucze ----------

    /// <summary>Nowy tajny klucz osoby (256 bitów, base64url) i jego skrót SHA-256 (hex) – serwer zna tylko skrót.</summary>
    public static (string Key, string Hash) NewKey()
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (key, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant());
    }
}
