using Microsoft.Win32;

namespace PoGGaduchy;

/// <summary>Uruchamianie razem z Windows: wpis w HKCU\...\Run (bez uprawnień administratora).</summary>
public static class Autostart
{
    const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "PoGGaduchy";

    static string Command => $"\"{AppInfo.ExePath}\" --autostart";

    public static bool IsEnabled()
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(KeyPath); return k?.GetValue(ValueName) != null; }
        catch { return false; }
    }

    /// <summary>Włącza lub wyłącza wpis; przy włączonym odświeża ścieżkę (np. po przeniesieniu programu).</summary>
    public static void Apply(bool enabled)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(KeyPath);
            if (enabled)
            {
                if (!string.Equals(k.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase))
                    k.SetValue(ValueName, Command);
            }
            else if (k.GetValue(ValueName) != null) k.DeleteValue(ValueName, false);
        }
        catch (Exception ex) { Log.Write("Autostart: " + ex.Message); }
    }
}
