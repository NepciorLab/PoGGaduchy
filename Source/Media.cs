using System.IO;
using System.Media;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Windows.Devices.Enumeration;

namespace PoGGaduchy;

/// <summary>Wykrywanie kamery (Windows).</summary>
internal static class CameraService
{
    /// <summary>Tylko do testów: PGG_FAKE_MEDIA=1 udaje kamerę i mikrofon (sztuczny obraz i dźwięk w WebView2).</summary>
    public static bool Fake => Environment.GetEnvironmentVariable("PGG_FAKE_MEDIA") == "1";

    /// <summary>Tylko do testów: PGG_NO_CAMERA=1 udaje brak kamery.</summary>
    public static bool ForceNone => Environment.GetEnvironmentVariable("PGG_NO_CAMERA") == "1";

    public static async Task<bool> HasCameraAsync()
    {
        if (ForceNone) return false;
        if (Fake) return true;
        try
        {
            var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture).AsTask();
            Log.Write("Wykryte kamery: " + devices.Count);
            return devices.Count > 0;
        }
        catch (Exception ex)
        {
            Log.Write("Wykrywanie kamery nie powiodło się (zakładam, że jest): " + ex.Message);
            return true;
        }
    }

    public static async Task<int> CountMicrophonesAsync()
    {
        try { return (await DeviceInformation.FindAllAsync(DeviceClass.AudioCapture).AsTask()).Count; }
        catch { return -1; }
    }
}

/// <summary>Wspólne środowisko WebView2 (jedno na program): dane obok programu, kamera i mikrofon bez pytań.</summary>
internal static class WebViewHost
{
    public const string Origin = "https://poggaduchy.local";
    static Task<CoreWebView2Environment>? _env;

    public static bool ForceRelay => Environment.GetEnvironmentVariable("PGG_FORCE_RELAY") == "1";

    public static string? RuntimeVersion
    {
        get { try { return CoreWebView2Environment.GetAvailableBrowserVersionString(); } catch { return null; } }
    }

    public static Task<CoreWebView2Environment> GetEnvironmentAsync()
    {
        _env ??= CreateAsync();
        return _env;
    }

    static async Task<CoreWebView2Environment> CreateAsync()
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.WebViewDir);
            // Rozmowa startuje bez klikania w stronie, więc dźwięk i obraz muszą mieć prawo grać od razu.
            var args = "--autoplay-policy=no-user-gesture-required";
            if (CameraService.Fake) args += " --use-fake-device-for-media-stream";
            var opts = new CoreWebView2EnvironmentOptions(args) { Language = "pl" };
            return await CoreWebView2Environment.CreateAsync(null, SettingsStore.WebViewDir, opts);
        }
        catch
        {
            _env = null;           // następna próba utworzy środowisko od nowa
            throw;
        }
    }

    public static Stream? OpenResource(string name) =>
        Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
}

/// <summary>Dzwonek: dwa tony na przemian, w pętli. Generowany w pamięci (bez plików).</summary>
internal sealed class Ringtone : IDisposable
{
    SoundPlayer? _player;

    public void Start()
    {
        try
        {
            _player = new SoundPlayer(new MemoryStream(MakeWav()));
            _player.PlayLooping();
        }
        catch (Exception ex) { Log.Write("Dzwonek: " + ex.Message); }
    }

    public void Stop()
    {
        try { _player?.Stop(); } catch { }
        _player?.Dispose();
        _player = null;
    }

    public void Dispose() => Stop();

    static byte[] MakeWav()
    {
        const int rate = 22050;
        var pcm = new List<short>();
        void Tone(double hz, double sec)
        {
            var n = (int)(rate * sec);
            for (var i = 0; i < n; i++)
            {
                var env = Math.Min(1.0, Math.Min(i, n - i) / (rate * 0.02));        // krótkie narastanie i wyciszanie
                pcm.Add((short)(Math.Sin(2 * Math.PI * hz * i / rate) * 0.55 * short.MaxValue * env));
            }
        }
        void Silence(double sec) { for (var i = 0; i < (int)(rate * sec); i++) pcm.Add(0); }
        Tone(880, 0.35); Silence(0.12); Tone(660, 0.35); Silence(0.12);
        Tone(880, 0.35); Silence(0.12); Tone(660, 0.35); Silence(1.6);

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + pcm.Count * 2); w.Write("WAVEfmt "u8);
        w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(pcm.Count * 2);
        foreach (var s in pcm) w.Write(s);
        return ms.ToArray();
    }
}

internal static class PhotoRenderer
{
    /// <summary>Kadr zdjęcia osoby (albo inicjał na niebieskim tle) jako data:image/png dla strony rozmowy.</summary>
    public static string ToDataUrl(Person p, int size = 480)
    {
        try
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x4C, 0x7F, 0xB0)), null, new Rect(0, 0, size, size));
                if (p.Image != null)
                {
                    var brush = new ImageBrush(p.Image) { Viewbox = p.Crop, Stretch = Stretch.UniformToFill };
                    dc.DrawRectangle(brush, null, new Rect(0, 0, size, size));
                }
                else
                {
                    var ft = new FormattedText(p.Initial, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                        new Typeface("Segoe UI Semibold"), size * 0.5, Brushes.White, 1.0);
                    dc.DrawText(ft, new Point((size - ft.Width) / 2, (size - ft.Height) / 2));
                }
            }
            var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
        }
        catch (Exception ex)
        {
            Log.Write("Zdjęcie dla strony rozmowy: " + ex.Message);
            return "";
        }
    }
}
