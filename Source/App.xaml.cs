using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PoGGaduchy;

public partial class App : Application
{
    Mutex? _single;
    EventWaitHandle? _showEvent;
    DispatcherTimer? _updateTimer;
    SignalingClient? _sig;
    CallManager? _calls;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;

        // Po aktualizacji (lub zmianie kodu) nowa kopia czeka, aż stara się zamknie (inaczej zadziałałaby blokada "jedna kopia").
        var after = args.SkipWhile(a => a != "--after-update").Skip(1).FirstOrDefault();
        if (int.TryParse(after, out var oldPid))
        {
            try { Process.GetProcessById(oldPid).WaitForExit(15000); } catch { }
        }

        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Write("Nieobsłużony wyjątek: " + ex.Exception);
            ex.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, ex) => { Log.Write("Wyjątek w tle: " + ex.Exception); ex.SetObserved(); };

        // Diagnostyka (tylko odczyt, nic nie zmienia): wynik trafia do log.txt. Działa też obok uruchomionego programu.
        if (args.Contains("--diagnoza"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Task.Run(async () =>
            {
                try { await Diagnostics.RunAsync(); } catch (Exception ex) { Log.Write("DIAGNOZA: błąd: " + ex); }
                Dispatcher.Invoke(Shutdown);
            });
            return;
        }

        var demo = args.Contains("--demo");
        var devRun = demo || args.Contains("--screenshot");

        // Jedna kopia programu na jeden folder danych; druga kopia tylko wysuwa pierwszą (albo okno trwającej rozmowy).
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(SettingsStore.DataDir.ToLowerInvariant())))[..16];
        if (!devRun)
        {
            _single = new Mutex(true, "PoGGaduchy.SingleInstance." + hash, out var first);
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "PoGGaduchy.Show." + hash);
            if (!first) { _showEvent.Set(); Shutdown(); return; }
            new Thread(() =>
            {
                while (true)
                {
                    _showEvent.WaitOne();
                    Dispatcher.BeginInvoke(ShowFirstInstance);
                }
            }) { IsBackground = true }.Start();
        }

        // Test układu: --size 1536x864 (rozmiar w jednostkach niezależnych od urządzenia, tj. po uwzględnieniu skalowania Windows).
        var sizeArg = args.SkipWhile(a => a != "--size").Skip(1).FirstOrDefault()?.Split('x');
        if (sizeArg is { Length: 2 } && double.TryParse(sizeArg[0], out var fw) && double.TryParse(sizeArg[1], out var fh))
        {
            PoGGaduchy.MainWindow.ForcedSize = new Size(fw, fh);
            Display.Override = new Rect(0, 0, fw, fh - 48);   // jak ekran z paskiem zadań
        }
        UpdateService.CleanupOld();

        var settings = SettingsStore.Load();

        // Pobrana wcześniej aktualizacja jest instalowana od razu przy starcie (jeśli cicha aktualizacja jest włączona).
        if (!devRun && settings.SilentUpdate && UpdateService.IsUpdateReady && UpdateService.ApplyPendingAndRestart()) { Shutdown(); return; }

        if (demo) FillDemo(settings);

        var identity = demo ? new Identity("http://127.0.0.1:1", new string('x', 43), "demo") : Identity.Load();
        if (!devRun && Environment.GetEnvironmentVariable("PGG_DATA") == null) Autostart.Apply(settings.Autostart);

        if (!devRun && identity != null)
        {
            _sig = new SignalingClient(Dispatcher);
            Roster.Attach(settings, _sig);
            _calls = new CallManager(settings, _sig);
            _sig.Start(identity);
            Log.Write($"Start programu {AppInfo.VersionText}, serwer {identity.Server}, dane: {SettingsStore.DataDir}");
        }
        else if (!devRun) Log.Write("Start programu bez kodu instalacji.");

        var window = new MainWindow(settings, _sig, _calls, identity);
        window.Closed += (_, _) => { if (!devRun) SettingsStore.Save(settings); };
        MainWindow = window;
        window.Show();

        if (!devRun)
        {
            StartUpdateChecks(settings);
            // Składnik WebView2 przygotowujemy z wyprzedzeniem, żeby pierwsza rozmowa nie czekała.
            var warm = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            warm.Tick += async (_, _) =>
            {
                warm.Stop();
                try { await WebViewHost.GetEnvironmentAsync(); Log.Write("WebView2 gotowy: " + WebViewHost.RuntimeVersion); }
                catch (Exception ex) { Log.Write("WebView2 niedostępny: " + ex.Message); }
            };
            warm.Start();
        }

        // Narzędzia deweloperskie: zrzut ekranu okna i zakończenie.
        var shot = args.SkipWhile(a => a != "--screenshot").Skip(1).FirstOrDefault();
        if (shot != null) ScheduleScreenshot(args, settings, window, shot);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _sig?.Stop(); } catch { }
        base.OnExit(e);
    }

    void ShowFirstInstance()
    {
        if (_calls?.ActivateCallUi() == true) return;        // trwa rozmowa: wysuwamy jej okno, nie główne
        if (MainWindow is not { } w) return;
        w.Show();
        Native.Maximize(new WindowInteropHelper(w).Handle);
    }

    /// <summary>Sprawdza aktualizacje kilka sekund po starcie, a potem co 6 godzin (gdy włączono automatyczne aktualizacje).</summary>
    void StartUpdateChecks(AppSettings settings)
    {
        async void Check()
        {
            if (settings.AutoUpdate && _calls is not { InCall: true }) await UpdateManager.Instance.CheckAndDownloadAsync(manual: false);
        }
        var first = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        first.Tick += (_, _) => { first.Stop(); Check(); };
        first.Start();
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        _updateTimer.Tick += (_, _) => Check();
        _updateTimer.Start();
    }

    // ---------------------------------------------------------------- narzędzia deweloperskie (zrzuty ekranu do sprawdzania układu)

    void ScheduleScreenshot(string[] args, AppSettings settings, Window window, string shot)
    {
        var select = args.Contains("--select");
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var list = (System.Windows.Controls.ListBox)window.FindName("ContactList");
            if (select && settings.Contacts.Count > 0) list.SelectedIndex = int.TryParse(args.SkipWhile(a => a != "--select").Skip(1).FirstOrDefault(), out var idx) ? idx : 1;
            Window target = window;
            if (args.Contains("--settings")) { target = new SettingsWindow(settings, null, null) { Owner = window }; target.Show(); }
            if (args.Contains("--incoming"))
            {
                var inc = new IncomingCallWindow(settings.Contacts[1], true, !args.Contains("--nocamera")) { Topmost = false };
                inc.Show(); target = inc;
            }
            if (args.Contains("--nocamera") && !args.Contains("--incoming"))
            {
                ((FrameworkElement)window.FindName("DetailPanel")).Visibility = Visibility.Collapsed;
                ((FrameworkElement)window.FindName("NoCameraPanel")).Visibility = Visibility.Visible;
            }
            if (args.Contains("--holdhint")) ((FrameworkElement)window.FindName("HoldHint")).Visibility = Visibility.Visible;
            var banner = args.SkipWhile(a => a != "--banner").Skip(1).FirstOrDefault();
            if (banner != null) ((MainWindow)window).ShowNotice(banner);
            var t2 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            t2.Tick += (_, _) => { t2.Stop(); Capture(target, shot); Shutdown(); };
            t2.Start();
        };
        timer.Start();
    }

    static void Capture(Window w, string path)
    {
        var rtb = new RenderTargetBitmap((int)w.ActualWidth, (int)w.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(w);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    static void FillDemo(AppSettings s)
    {
        s.Contacts.Clear();
        var names = new[] { "Babcia Ania", "Córka Kasia", "Syn Tomek z rodziną", "Wnuczek Jaś", "Doktor Nowak", "Sąsiadka Basia", "Brat Marek", "Siostra Ela" };
        for (var i = 0; i < names.Length; i++)
            s.Contacts.Add(new Person { Id = "osoba" + i, ServerName = names[i], Presence = i % 3 == 2 ? 0 : (i % 4 == 3 ? 2 : 1) });
        if (File.Exists(Path.Combine(SettingsStore.ImagesDir, "demo.png")))
        {
            foreach (var c in s.Contacts.Take(2)) c.ImageFile = "demo.png";
            s.Contacts[1].ImageZoom = 2.6; s.Contacts[1].ImageCenterX = 0.72; s.Contacts[1].ImageCenterY = 0.38;
            s.Contacts[2].ImageFile = "demo.png"; s.Contacts[2].ImageZoom = 0.55; s.Contacts[2].ImageRotation = 90;
        }
    }
}
