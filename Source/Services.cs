namespace PoGGaduchy;

/// <summary>Wspólne usługi programu (jedna kopia na proces), dostępne z okien bez przekazywania ich w kółko.</summary>
public static class Services
{
    public static AppSettings Settings { get; set; } = new();
    public static Identity? Identity { get; set; }
    public static SignalingClient? Sig { get; set; }
    public static CallManager? Calls { get; set; }
    public static ServerApi? Api { get; set; }

    /// <summary>Okno ustawień (jedno naraz), żeby drugie kliknięcie tylko je wysuwało.</summary>
    public static SettingsWindow? SettingsWindow { get; set; }
}
