using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PoGGaduchy;

public static class AppInfo
{
    /// <summary>Repozytorium GitHub, z którego pobierane są aktualizacje (wydania "Releases").</summary>
    public const string DefaultRepository = "NepciorLab/PoGGaduchy";

    /// <summary>Zmienna środowiskowa PGG_REPO pozwala sprawdzić działanie aktualizacji na innym repozytorium (testy).</summary>
    public static string Repository { get; } = Environment.GetEnvironmentVariable("PGG_REPO") is { Length: > 0 } r ? r : DefaultRepository;

    public static Version Version { get; } = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0);

    public static string VersionText => $"{Version.Major}.{Version.Minor}.{Math.Max(0, Version.Build)}";

    public static string ExePath { get; } = Environment.ProcessPath ?? "";
}

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Osoba z katalogu serwera. Nazwę i obraz ustala serwer (obraz zmienia sama osoba albo administrator).</summary>
public sealed class Person : Observable
{
    string _serverName = "";
    int _avatarVer, _presence;
    bool _autoAnswer;
    ImageSource? _image;
    bool _imageLoaded;

    /// <summary>Identyfikator osoby na serwerze (np. "mama").</summary>
    public string Id { get; set; } = "";

    /// <summary>Nazwa nadana na serwerze (zmienia ją administrator).</summary>
    public string ServerName
    {
        get => _serverName;
        set { if (Set(ref _serverName, value ?? "")) { Raise(nameof(Name)); Raise(nameof(Initial)); } }
    }

    [JsonIgnore] public string Name => _serverName;

    /// <summary>Rozmowa od tej osoby jest odbierana automatycznie (osoba zaufana).</summary>
    public bool AutoAnswer { get => _autoAnswer; set => Set(ref _autoAnswer, value); }

    /// <summary>Numer wersji obrazu, który mamy zapisany na dysku (0 = brak obrazu). Zmiana wymusza ponowne wczytanie.</summary>
    public int AvatarVer
    {
        get => _avatarVer;
        set
        {
            if (!Set(ref _avatarVer, value)) return;
            _imageLoaded = false;
            Raise(nameof(Image)); Raise(nameof(Crop));
        }
    }

    /// <summary>Numer wersji obrazu na serwerze (nie zapisywany; używany do pobierania).</summary>
    [JsonIgnore] public int RemoteAv { get; set; }

    bool _isNew;

    /// <summary>Konto założone samodzielnie, jeszcze niezaakceptowane przez administratora (widoczne tylko dla administratora).</summary>
    [JsonIgnore] public bool IsNew { get => _isNew; set => Set(ref _isNew, value); }

    [JsonIgnore] public string Initial => string.IsNullOrWhiteSpace(Name) ? "?" : Name.Trim()[..1].ToUpperInvariant();

    /// <summary>Wymusza ponowne wczytanie obrazu z dysku (po pobraniu nowego pliku).</summary>
    public void ReloadImage()
    {
        _imageLoaded = false;
        Raise(nameof(Image)); Raise(nameof(Crop));
    }

    // ---------- obecność (nie zapisywana) ----------

    /// <summary>0 = nie ma w sieci, 1 = jest online, 2 = rozmawia.</summary>
    [JsonIgnore]
    public int Presence
    {
        get => _presence;
        set
        {
            if (!Set(ref _presence, value)) return;
            Raise(nameof(IsOnline)); Raise(nameof(StatusText)); Raise(nameof(CanCall));
        }
    }

    [JsonIgnore] public bool IsOnline => _presence != 0;
    [JsonIgnore] public bool CanCall => _presence == 1;

    [JsonIgnore]
    public string StatusText => _presence switch { 1 => "jest online", 2 => "rozmawia", _ => "nie ma w sieci" };

    [JsonIgnore]
    public ImageSource? Image
    {
        get
        {
            if (!_imageLoaded)
            {
                _image = _avatarVer > 0 ? SettingsStore.LoadFile(SettingsStore.AvatarPath(Id)) : null;
                _imageLoaded = true;
            }
            return _image;
        }
    }

    /// <summary>Obraz z serwera jest już przycięty do kwadratu, więc wycinek to cały obraz (potrzebny do ImageBrush.Viewbox).</summary>
    [JsonIgnore] public Rect Crop => new(0, 0, 1, 1);
}

/// <summary>
/// Kadrowanie zdjęcia przed wysłaniem na serwer: przesuwanie, powiększanie, pomniejszanie i obrót o 90°.
/// Wynik to kwadratowy JPEG (to, co widać w ramce).
/// </summary>
public sealed class CropModel : Observable
{
    public const double MinZoom = 0.25, MaxZoom = 6;
    double _zoom = 1, _cx = 0.5, _cy = 0.5;
    int _rotation;
    BitmapSource? _source;
    ImageSource? _image;

    /// <summary>Wczytane zdjęcie (przed obrotem); null, gdy nic nie wybrano.</summary>
    public BitmapSource? Source
    {
        get => _source;
        set { _source = value; _rotation = 0; ResetCrop(); RaiseImage(); }
    }

    public double Zoom
    {
        get => _zoom;
        set { if (Set(ref _zoom, Math.Clamp(value, MinZoom, MaxZoom))) { Raise(nameof(Crop)); Raise(nameof(ZoomSlider)); } }
    }

    /// <summary>Wartość dla suwaka: logarytm z powiększenia (środek suwaka = rozmiar normalny).</summary>
    public double ZoomSlider { get => Math.Log2(_zoom); set => Zoom = Math.Pow(2, value); }

    public ImageSource? Image
    {
        get
        {
            if (_image == null && _source != null)
            {
                _image = _source;
                if (_rotation != 0) { var t = new TransformedBitmap(_source, new RotateTransform(_rotation)); t.Freeze(); _image = t; }
            }
            return _image;
        }
    }

    void RaiseImage() { _image = null; Raise(nameof(Image)); Raise(nameof(Crop)); }

    /// <summary>Kwadratowy wycinek zdjęcia (ułamki całego obrazu), do użycia jako ImageBrush.Viewbox.
    /// Dla powiększenia poniżej 1 wycinek jest większy niż zdjęcie – poza zdjęciem widać tło ramki.</summary>
    public Rect Crop
    {
        get
        {
            if (Image is not BitmapSource img || img.PixelWidth == 0 || img.PixelHeight == 0) return new Rect(0, 0, 1, 1);
            double w = img.PixelWidth, h = img.PixelHeight;
            var side = Math.Min(w, h) / Math.Clamp(_zoom, MinZoom, MaxZoom);
            double sx = side / w, sy = side / h;
            var cx = ClampCenter(_cx, sx);
            var cy = ClampCenter(_cy, sy);
            return new Rect(cx - sx / 2, cy - sy / 2, sx, sy);
        }
    }

    static double ClampCenter(double c, double size)
    {
        var lo = Math.Min(size / 2, 1 - size / 2);
        var hi = Math.Max(size / 2, 1 - size / 2);
        return Math.Clamp(c, lo, hi);
    }

    /// <summary>Przesuwa kadr; dx i dy to przesunięcie myszy jako ułamek wielkości ramki (zdjęcie "jedzie" za myszą).</summary>
    public void Pan(double dx, double dy)
    {
        var c = Crop;
        _cx = Math.Clamp(c.X + c.Width / 2 - dx * c.Width, -1, 2);
        _cy = Math.Clamp(c.Y + c.Height / 2 - dy * c.Height, -1, 2);
        Raise(nameof(Crop));
    }

    /// <summary>Obraca zdjęcie o 90° i przenosi środek kadru tak, by ta sama część zdjęcia została w ramce.</summary>
    public void Rotate(bool clockwise)
    {
        if (_source == null) return;
        var c = Crop;
        double x = c.X + c.Width / 2, y = c.Y + c.Height / 2;
        (_cx, _cy) = clockwise ? (1 - y, x) : (y, 1 - x);
        _rotation = ((_rotation + (clockwise ? 90 : -90)) % 360 + 360) % 360;
        RaiseImage();
    }

    public void ResetCrop()
    {
        _zoom = 1; _cx = 0.5; _cy = 0.5;
        Raise(nameof(Zoom)); Raise(nameof(ZoomSlider)); Raise(nameof(Crop));
    }

    /// <summary>Rysuje to, co widać w ramce, jako kwadratowy JPEG (domyślnie 512 x 512).</summary>
    public byte[]? RenderJpeg(int size = 512, int quality = 85)
    {
        if (Image is not BitmapSource) return null;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x4C, 0x7F, 0xB0)), null, new Rect(0, 0, size, size));
            dc.DrawRectangle(new ImageBrush(Image) { Viewbox = Crop, Stretch = Stretch.UniformToFill }, null, new Rect(0, 0, size, size));
        }
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new JpegBitmapEncoder { QualityLevel = quality };
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }
}

public sealed class AppSettings : Observable
{
    // Domyślne rozmiary rosną razem z wysokością ekranu (pierwsze uruchomienie).
    static readonly double Scale = Math.Clamp(SystemParameters.PrimaryScreenHeight / 1080.0, 1.0, 2.5);
    double _tileWidth = Math.Round(560 * Scale), _tileHeight = Math.Round(160 * Scale), _nameFontSize = Math.Round(36 * Scale);
    bool _autoUpdate = true, _silentUpdate = true, _autostart = true;
    string _micLabel = "", _speakerLabel = "", _cameraLabel = "";

    public double TileWidth { get => _tileWidth; set => Set(ref _tileWidth, Math.Clamp(value, 260, 1000)); }
    public double TileHeight { get => _tileHeight; set => Set(ref _tileHeight, Math.Clamp(value, 80, 320)); }
    public double NameFontSize { get => _nameFontSize; set => Set(ref _nameFontSize, Math.Clamp(value, 14, 90)); }

    /// <summary>Automatyczne sprawdzanie i pobieranie aktualizacji z GitHuba.</summary>
    public bool AutoUpdate { get => _autoUpdate; set => Set(ref _autoUpdate, value); }

    /// <summary>Cicha aktualizacja: pobrana wersja jest instalowana bez pytania (przy bezczynności lub przy starcie).</summary>
    public bool SilentUpdate { get => _silentUpdate; set => Set(ref _silentUpdate, value); }

    /// <summary>Uruchamianie razem z Windows.</summary>
    public bool Autostart { get => _autostart; set => Set(ref _autostart, value); }

    /// <summary>Wybrane urządzenia (nazwy, jak w Windows; puste = domyślne urządzenie systemu).</summary>
    public string MicLabel { get => _micLabel; set => Set(ref _micLabel, value ?? ""); }
    public string SpeakerLabel { get => _speakerLabel; set => Set(ref _speakerLabel, value ?? ""); }
    public string CameraLabel { get => _cameraLabel; set => Set(ref _cameraLabel, value ?? ""); }

    /// <summary>Moje id i nazwa (z ostatniego połączenia z serwerem).</summary>
    public string MyId { get; set; } = "";
    public string MyName { get; set; } = "";

    bool _myIsNew;

    /// <summary>Moje konto czeka na akceptację administratora (do tego czasu rozmawiam tylko z administratorem).</summary>
    [JsonIgnore] public bool MyIsNew { get => _myIsNew; set => Set(ref _myIsNew, value); }

    /// <summary>Wersja mojego obrazu na serwerze (0 = brak) i ta zapisana na dysku (do podglądu w ustawieniach).</summary>
    [JsonIgnore] public int MyAv { get; set; }
    public int MyAvLocal { get; set; }

    public ObservableCollection<Person> Contacts { get; set; } = new();
}

public static class SettingsStore
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Folder danych: obok programu (jeśli da się tam zapisywać), inaczej %APPDATA%\PoGGaduchy.</summary>
    public static string DataDir { get; } = ResolveDataDir();

    static string FallbackDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PoGGaduchy");

    static string ResolveDataDir()
    {
        var env = Environment.GetEnvironmentVariable("PGG_DATA");
        if (!string.IsNullOrWhiteSpace(env)) return Path.GetFullPath(env);
        var exeDir = Path.GetDirectoryName(AppInfo.ExePath);
        if (!string.IsNullOrEmpty(exeDir) && CanWrite(exeDir)) return exeDir;
        return FallbackDir;
    }

    static bool CanWrite(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, ".write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    public static string ImagesDir => Path.Combine(DataDir, "images");
    public static string WebViewDir => Path.Combine(DataDir, "webview2");
    static string SettingsPath => Path.Combine(DataDir, "settings.json");

    /// <summary>Plik z obrazem osoby pobranym z serwera.</summary>
    public static string AvatarPath(string id) => Path.Combine(ImagesDir, "avatar-" + id + ".jpg");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Write("Nie udało się wczytać ustawień: " + ex.Message);
            try { File.Copy(SettingsPath, SettingsPath + ".bad", true); } catch { }
        }
        return new AppSettings();
    }

    public static void Save(AppSettings s)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            var tmp = SettingsPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(s, Json));
            File.Move(tmp, SettingsPath, true);
        }
        catch (Exception ex) { Log.Write("Nie udało się zapisać ustawień: " + ex.Message); }
    }

    /// <summary>Wczytuje obraz z pliku (dowolnej ścieżki). Duże zdjęcia z aparatu zmniejsza przy wczytywaniu, ale zostawia zapas na powiększanie.</summary>
    public static BitmapSource? LoadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var uri = new Uri(path);
            var natural = BitmapDecoder.Create(uri, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0].PixelWidth;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;       // nie blokuje pliku
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            if (natural > 1800) bmp.DecodePixelWidth = 1800;
            bmp.UriSource = uri;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex)
        {
            Log.Write("Nie udało się wczytać obrazu " + path + ": " + ex.Message);
            return null;
        }
    }
}

public static class Log
{
    static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(SettingsStore.DataDir);
                var path = Path.Combine(SettingsStore.DataDir, "log.txt");
                if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024)
                    File.Move(path, path + ".old", true);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
