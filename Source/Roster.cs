using System.IO;
using System.Text.Json;
using System.Windows.Threading;

namespace PoGGaduchy;

/// <summary>
/// Łączy katalog osób, obrazy i obecność z serwera z lokalną listą kafelków. Obrazy osób są pobierane z serwera do
/// folderu images (z ponawianiem) i podmieniane, gdy ktoś zmieni swój obraz.
/// </summary>
public sealed class Roster
{
    readonly AppSettings _settings;
    readonly ServerApi _api;
    readonly DispatcherTimer _retry = new() { Interval = TimeSpan.FromSeconds(30) };
    bool _syncing;
    readonly HashSet<string> _announcedNew = new();

    Roster(AppSettings settings, ServerApi api)
    {
        _settings = settings;
        _api = api;
        _retry.Tick += (_, _) => _ = SyncAvatarsAsync();
    }

    public static void Attach(AppSettings settings, SignalingClient sig, ServerApi api)
    {
        var r = new Roster(settings, api);
        sig.Welcome += m =>
        {
            r.ApplyWelcome(m);
            _ = api.CheckAdminAsync();
        };
        sig.Message += m =>
        {
            if (!m.TryGetProperty("t", out var t)) return;
            switch (t.GetString())
            {
                case "presence" when m.TryGetProperty("online", out var online): ApplyPresence(settings, online); break;
                case "people" when m.TryGetProperty("directory", out var dir): r.ApplyDirectory(dir); break;
            }
        };
        // Bez połączenia z serwerem nie wiemy, kto jest w sieci.
        sig.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SignalingClient.IsConnected) && !sig.IsConnected)
                foreach (var p in settings.Contacts) p.Presence = 0;
        };
    }

    void ApplyWelcome(JsonElement m)
    {
        _settings.MyId = m.GetProperty("me").GetProperty("id").GetString() ?? "";
        _settings.MyName = m.GetProperty("me").GetProperty("name").GetString() ?? "";
        _settings.MyIsNew = m.GetProperty("me").TryGetProperty("st", out var mst) && mst.GetString() == "new";
        ApplyDirectory(m.GetProperty("directory"));
        if (m.TryGetProperty("online", out var online)) ApplyPresence(_settings, online);
    }

    void ApplyDirectory(JsonElement directory)
    {
        var meId = _settings.MyId;
        var seen = new HashSet<string>();
        foreach (var d in directory.EnumerateArray())
        {
            var id = d.GetProperty("id").GetString() ?? "";
            var name = d.GetProperty("name").GetString() ?? id;
            var av = d.TryGetProperty("av", out var a) && a.TryGetInt32(out var n) ? n : 0;
            var isNew = d.TryGetProperty("st", out var st) && st.GetString() == "new";
            if (id == "") continue;
            if (id == meId) { _settings.MyName = name; _settings.MyAv = av; _settings.MyIsNew = isNew; continue; }
            seen.Add(id);
            var p = _settings.Contacts.FirstOrDefault(c => c.Id == id);
            if (p == null) { p = new Person { Id = id, ServerName = name }; _settings.Contacts.Add(p); }
            else p.ServerName = name;
            p.RemoteAv = av;
            p.IsNew = isNew;
            // administrator dowiaduje się o nowym koncie (raz dla każdego)
            if (isNew && _announcedNew.Add(id))
                Services.Calls?.RaiseNotice($"Nowe konto czeka na akceptację: {name}. Zaakceptuj je w Ustawieniach (Osoby).");
        }
        // Osoby, których nie ma już w katalogu serwera, nie mogą być wywołane – znikają też z listy.
        foreach (var gone in _settings.Contacts.Where(c => !seen.Contains(c.Id)).ToList())
        {
            _settings.Contacts.Remove(gone);
            try { File.Delete(SettingsStore.AvatarPath(gone.Id)); } catch { }
        }
        SettingsStore.Save(_settings);
        _ = SyncAvatarsAsync();
    }

    static void ApplyPresence(AppSettings settings, JsonElement online)
    {
        foreach (var p in settings.Contacts)
            p.Presence = online.TryGetProperty(p.Id, out var v) && v.TryGetInt32(out var n) ? n : 0;
    }

    /// <summary>Pobiera brakujące lub zmienione obrazy (po jednym naraz). Niepowodzenie = ponowna próba za 30 s.</summary>
    async Task SyncAvatarsAsync()
    {
        if (_syncing) return;
        _syncing = true;
        var failed = false;
        try
        {
            foreach (var p in _settings.Contacts.Where(c => c.AvatarVer != c.RemoteAv).ToList())
            {
                try
                {
                    if (p.RemoteAv == 0)
                    {
                        try { File.Delete(SettingsStore.AvatarPath(p.Id)); } catch { }
                        p.AvatarVer = 0;
                        continue;
                    }
                    var bytes = await _api.GetAvatarAsync(p.Id);
                    if (bytes == null) { p.AvatarVer = 0; continue; }
                    Directory.CreateDirectory(SettingsStore.ImagesDir);
                    var tmp = SettingsStore.AvatarPath(p.Id) + ".tmp";
                    await File.WriteAllBytesAsync(tmp, bytes);
                    File.Move(tmp, SettingsStore.AvatarPath(p.Id), true);
                    if (p.AvatarVer == p.RemoteAv) p.ReloadImage(); else p.AvatarVer = p.RemoteAv;
                    Log.Write($"Obraz osoby {p.Id} pobrany (wersja {p.RemoteAv}, {bytes.Length / 1024} KB)");
                }
                catch (Exception ex) { failed = true; Log.Write($"Obraz osoby {p.Id}: {ex.Message}"); }
            }
            // własny obraz też jest w katalogu: pobieramy go do podglądu w ustawieniach
            await SyncOwnAvatarAsync();
            SettingsStore.Save(_settings);
        }
        finally { _syncing = false; }
        if (failed) { _retry.Stop(); _retry.Start(); } else _retry.Stop();
    }

    async Task SyncOwnAvatarAsync()
    {
        try
        {
            if (string.IsNullOrEmpty(_settings.MyId) || _settings.MyAvLocal == _settings.MyAv) return;
            if (_settings.MyAv == 0) { try { File.Delete(SettingsStore.AvatarPath(_settings.MyId)); } catch { } _settings.MyAvLocal = 0; return; }
            var bytes = await _api.GetAvatarAsync(_settings.MyId);
            if (bytes == null) { _settings.MyAvLocal = 0; return; }
            Directory.CreateDirectory(SettingsStore.ImagesDir);
            await File.WriteAllBytesAsync(SettingsStore.AvatarPath(_settings.MyId), bytes);
            _settings.MyAvLocal = _settings.MyAv;
        }
        catch (Exception ex) { Log.Write("Własny obraz: " + ex.Message); }
    }
}
