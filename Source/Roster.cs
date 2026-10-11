using System.IO;
using System.Text.Json;

namespace PoGGaduchy;

/// <summary>Łączy katalog osób i obecność z serwera z lokalną listą kafelków (własne nazwy i zdjęcia zostają).</summary>
public static class Roster
{
    public static void Attach(AppSettings settings, SignalingClient sig)
    {
        sig.Welcome += m => ApplyWelcome(settings, m);
        sig.Message += m =>
        {
            if (m.TryGetProperty("t", out var t) && t.GetString() == "presence" && m.TryGetProperty("online", out var online))
                ApplyPresence(settings, online);
        };
        // Bez połączenia z serwerem nie wiemy, kto jest w sieci.
        sig.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SignalingClient.IsConnected) && !sig.IsConnected)
                foreach (var p in settings.Contacts) p.Presence = 0;
        };
    }

    static void ApplyWelcome(AppSettings settings, JsonElement m)
    {
        var meId = m.GetProperty("me").GetProperty("id").GetString() ?? "";
        settings.MyId = meId;
        settings.MyName = m.GetProperty("me").GetProperty("name").GetString() ?? "";

        var seen = new HashSet<string>();
        foreach (var d in m.GetProperty("directory").EnumerateArray())
        {
            var id = d.GetProperty("id").GetString() ?? "";
            var name = d.GetProperty("name").GetString() ?? id;
            if (id == "" || id == meId) continue;
            seen.Add(id);
            var p = settings.Contacts.FirstOrDefault(c => c.Id == id);
            if (p == null) settings.Contacts.Add(new Person { Id = id, ServerName = name });
            else p.ServerName = name;
        }
        // Osoby, których nie ma już w katalogu serwera, nie mogą być wywołane – znikają też z listy.
        foreach (var gone in settings.Contacts.Where(c => !seen.Contains(c.Id)).ToList())
        {
            settings.Contacts.Remove(gone);
            if (!string.IsNullOrEmpty(gone.ImageFile))
                try { File.Delete(Path.Combine(SettingsStore.ImagesDir, gone.ImageFile)); } catch { }
        }
        if (m.TryGetProperty("online", out var online)) ApplyPresence(settings, online);
        SettingsStore.Save(settings);
    }

    static void ApplyPresence(AppSettings settings, JsonElement online)
    {
        foreach (var p in settings.Contacts)
            p.Presence = online.TryGetProperty(p.Id, out var v) && v.TryGetInt32(out var n) ? n : 0;
    }
}
