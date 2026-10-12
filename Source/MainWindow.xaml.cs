using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace PoGGaduchy;

public sealed class ScaleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double d ? d * double.Parse((string)parameter, CultureInfo.InvariantCulture) : 0d;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public partial class MainWindow : Window
{
    static readonly TimeSpan HoldTime = TimeSpan.FromSeconds(1);
    static readonly TimeSpan IdleBeforeUpdate = TimeSpan.FromMinutes(5);
    static readonly Brush OnlineText = new SolidColorBrush(Color.FromRgb(0x7C, 0xF2, 0x9A));
    static readonly Brush BusyText = new SolidColorBrush(Color.FromRgb(0xFF, 0xB4, 0x5A));
    static readonly Brush OfflineText = new SolidColorBrush(Color.FromRgb(0xC7, 0xD2, 0xDC));

    readonly AppSettings _settings;
    readonly SignalingClient? _sig;
    readonly CallManager? _calls;
    readonly Identity? _identity;
    readonly ICollectionView _view;
    readonly DispatcherTimer _hold = new() { Interval = HoldTime };
    readonly DispatcherTimer _holdHintTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    readonly DispatcherTimer _idleTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    readonly DispatcherTimer _noticeTimer = new();
    bool _holdActive;
    string _notice = "";
    DateTime _lastInput = DateTime.Now;
    Person? _subscribed;

    /// <summary>Tylko do testów układu (--size 1920x1080): okno o stałym rozmiarze zamiast zmaksymalizowanego.</summary>
    public static Size? ForcedSize { get; set; }

    public MainWindow(AppSettings settings, SignalingClient? sig, CallManager? calls, Identity? identity)
    {
        _settings = settings;
        _sig = sig; _calls = calls; _identity = identity;
        InitializeComponent();
        if (ForcedSize is { } fs)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Normal;
            Width = fs.Width; Height = fs.Height;
            Left = 0; Top = 0;
        }
        DataContext = settings;
        _view = CollectionViewSource.GetDefaultView(settings.Contacts);
        ContactList.ItemsSource = _view;
        foreach (var p in settings.Contacts) p.PropertyChanged += OnPersonChanged;
        settings.Contacts.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null) foreach (Person p in e.NewItems) p.PropertyChanged += OnPersonChanged;
            if (e.OldItems != null) foreach (Person p in e.OldItems) p.PropertyChanged -= OnPersonChanged;
            UpdateDetail();
        };
        _settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppSettings.TileWidth) or nameof(AppSettings.NameFontSize)) UpdateScales();
            if (e.PropertyName is nameof(AppSettings.SilentUpdate)) RefreshBanner();
        };
        _noticeTimer.Tick += (_, _) => { _noticeTimer.Stop(); _notice = ""; RefreshBanner(); };
        _holdHintTimer.Tick += (_, _) => { _holdHintTimer.Stop(); HoldHint.Visibility = Visibility.Collapsed; };
        _hold.Tick += (_, _) => { _hold.Stop(); _holdActive = false; ResetHoldBar(); HoldHint.Visibility = Visibility.Collapsed; OpenSettings(); };
        _idleTimer.Tick += (_, _) => InstallUpdateWhenIdle();
        _idleTimer.Start();
        if (_sig != null) _sig.PropertyChanged += (_, _) => RefreshBanner();
        if (_calls != null)
        {
            _calls.Notice += text => ShowNotice(text);
            _calls.StateChanged += OnCallStateChanged;
        }
        UpdateManager.Instance.PropertyChanged += (_, _) => Dispatcher.BeginInvoke(RefreshBanner);
        UpdateDetail();
        RefreshBanner();
    }

    Person? Selected => ContactList.SelectedItem as Person;

    // Program ma się zawsze uruchamiać (i pozostawać) zmaksymalizowany.
    void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Normal && ForcedSize == null) WindowState = WindowState.Maximized;
    }

    void OnUserInput(object sender, EventArgs e) => _lastInput = DateTime.Now;

    void OnSizeChanged(object sender, SizeChangedEventArgs e) => UpdateScales();

    void OnRightPaneSizeChanged(object sender, SizeChangedEventArgs e) => UpdateScales();

    // Prawa część (duży obraz, przycisk rozmowy) rośnie razem z wysokością okna, ale nie szerzej niż mieści się w prawej kolumnie
    // – dzięki temu układ jest czytelny zarówno na dużym monitorze, jak i przy 1920x1080 ze skalowaniem Windows 125–150%.
    void UpdateScales()
    {
        // Kafelki szersze niż 45% okna zmniejszamy proporcjonalnie (np. zapisane na dużym monitorze, uruchomione na małym).
        var listNatural = _settings.TileWidth + 34 /* pasek przewijania */ + 32 /* marginesy */;
        var ls = Math.Min(1.0, ActualWidth * 0.45 / listNatural);
        ContactList.LayoutTransform = ls < 1 ? new ScaleTransform(ls, ls) : Transform.Identity;

        var paneWidth = RightPane.ActualWidth > 0 ? RightPane.ActualWidth : ActualWidth - listNatural * ls;
        // Skala prawej części: wg wysokości okna, szerokości kolumny i tego, ile miejsca zajmuje stos (zdjęcie, nazwa, stan, przycisk).
        var nameHeight = _settings.NameFontSize * 1.5 * 1.35;
        var paneHeight = RightPane.ActualHeight > 0 ? RightPane.ActualHeight : ActualHeight;
        var kHeight = Math.Clamp(ActualHeight / 900.0, 0.45, 2.2);
        var kFit = (paneHeight - (40 + 22 + nameHeight + 6 + 52 + 18 + 70)) / (340 + 190 + 36);
        var k = Math.Max(0.4, Math.Min(Math.Min(kHeight, kFit), paneWidth / 640.0));

        BigAvatar.Width = BigAvatar.Height = 340 * k;
        BigInitial.FontSize = 160 * k;
        BigName.MaxWidth = Math.Max(200, paneWidth - 60);
        BigStatus.FontSize = 30 * k;
        BigStatus.Margin = new Thickness(0, 0, 0, 18 * k);
        CallButton.Width = 520 * k;
        CallButton.Height = 190 * k;
        VideoIcon.FontSize = 110 * k;
        PhoneIcon.FontSize = 96 * k;
        PhoneIcon.Margin = new Thickness(36 * k, 0, 0, 0);
        CallHint.FontSize = 28 * k;
        BannerText.FontSize = Math.Max(18, 30 * Math.Min(k, 1.3));
        BannerText.MaxWidth = Math.Max(240, Math.Min(1100, ActualWidth - 380));
        UpdateButton.Height = 90 * Math.Min(k, 1.3);
        BannerPanel.Margin = new Thickness(30, 0, 260, 14);
        // Przycisk ustawień ma połowę dawnej wielkości (na małych ekranach nie schodzimy poniżej tej wielkości).
        var sb = 0.5 * Math.Max(k, 1.0);
        SettingsButton.LayoutTransform = new ScaleTransform(sb, sb);
        // Panel "brak kamery" ma naturalną szerokość ok. 1130 px – zmieszczamy go w prawej kolumnie.
        // ...i pod przyciskiem X (128 + marginesy), żeby nie nachodził na niego na małych ekranach.
        var cam = Math.Max(0.3, Math.Min(Math.Min(k, (paneWidth - 40) / 1130.0), (paneHeight - 250) / 700.0));
        NoCameraPanel.LayoutTransform = new ScaleTransform(cam, cam);
    }

    // ---------- wybór osoby ----------

    void OnContactSelected(object sender, SelectionChangedEventArgs e) => UpdateDetail();

    void OnPersonChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender == _subscribed && e.PropertyName is nameof(Person.Presence) or nameof(Person.Name)) Dispatcher.BeginInvoke(UpdateCallButton);
    }

    void UpdateDetail()
    {
        var c = Selected;
        if (!ReferenceEquals(_subscribed, c)) _subscribed = c;
        NoCameraPanel.Visibility = Visibility.Collapsed;
        DetailPanel.DataContext = c;
        DetailPanel.Visibility = c == null ? Visibility.Collapsed : Visibility.Visible;
        BigInitial.Visibility = c != null && c.Image == null ? Visibility.Visible : Visibility.Collapsed;
        UpdateCallButton();
        if (c != null) { EmptyHint.Text = ""; return; }
        EmptyHint.Text = _settings.Contacts.Count == 0
            ? (_identity == null
                ? "Ten program nie ma jeszcze kodu instalacji.\nPoproś osobę, która go przygotowała, o plik „kod-instalacji.txt”."
                : "Czekam na listę osób z serwera…")
            : "Wybierz osobę z listy po lewej stronie.";
    }

    void UpdateCallButton()
    {
        var c = Selected;
        if (c == null) return;
        BigStatus.Foreground = c.Presence switch { 1 => OnlineText, 2 => BusyText, _ => OfflineText };
        CallButton.Background = new SolidColorBrush(c.CanCall ? Color.FromRgb(0x2E, 0xA0, 0x43) : Color.FromRgb(0x6B, 0x7B, 0x8A));
        CallHint.Text = c.Presence switch
        {
            1 => "Naciśnij zielony przycisk, aby zadzwonić",
            2 => $"{c.Name} rozmawia teraz. Spróbuj za chwilę.",
            _ => $"{c.Name} nie ma teraz w sieci. Zadzwoń, gdy zmieni się na „jest online”.",
        };
    }

    // ---------- komunikaty ----------

    public void ShowNotice(string text)
    {
        _notice = text;
        _noticeTimer.Stop();
        _noticeTimer.Interval = text.StartsWith("Nieodebrane") ? TimeSpan.FromMinutes(10) : TimeSpan.FromSeconds(20);
        _noticeTimer.Start();
        RefreshBanner();
    }

    void RefreshBanner()
    {
        string? text = null;
        if (_identity == null) text = "Brak kodu instalacji. Poproś osobę, która przygotowała program, o plik „kod-instalacji.txt”.";
        else if (_sig is { IsConnected: false }) text = _sig.StatusText;
        else if (!string.IsNullOrEmpty(_notice)) text = _notice;
        var update = UpdateManager.Instance.ReadyToInstall && !_settings.SilentUpdate && _calls is not { InCall: true };
        UpdateButton.Visibility = update ? Visibility.Visible : Visibility.Collapsed;
        if (text == null && update) text = $"Jest nowa wersja programu{(UpdateManager.Instance.PendingVersion is { Length: > 0 } v ? " (" + v + ")" : "")}.";
        BannerText.Text = text ?? "";
        BannerBorder.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
        BannerPanel.Visibility = text == null && !update ? Visibility.Collapsed : Visibility.Visible;
    }

    void OnCallStateChanged(CallState state)
    {
        if (state == CallState.Idle)
        {
            _lastInput = DateTime.Now;
            SettingsStore.Save(_settings);
            WindowState = WindowState.Maximized;
            UpdateDetail();
        }
        RefreshBanner();
    }

    // ---------- ustawienia (przytrzymanie 1 s) i wyjście ----------

    // Krótkie kliknięcie tylko pokazuje podpowiedź na 3 sekundy – tak samo jak w SeniorDesktop.
    void Settings_Down(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _holdActive = true;
        _holdHintTimer.Stop();
        HoldHint.Visibility = Visibility.Collapsed;
        _hold.Start();
        HoldScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0, 1, new Duration(HoldTime)));
    }

    void Settings_Up(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_holdActive) return;
        _holdActive = false;
        if (_hold.IsEnabled)
        {
            _hold.Stop();
            ResetHoldBar();
            HoldHint.Visibility = Visibility.Visible;
            _holdHintTimer.Stop();
            _holdHintTimer.Start();
        }
    }

    void ResetHoldBar()
    {
        HoldScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        HoldScale.ScaleX = 0;
    }

    void OpenSettings()
    {
        var selectedId = Selected?.Id;
        ((App)Application.Current).OpenSettings(this);
        SettingsStore.Save(_settings);
        ContactList.SelectedItem = _settings.Contacts.FirstOrDefault(c => c.Id == selectedId);
        UpdateDetail();
        RefreshBanner();
    }

    void OnExit(object sender, RoutedEventArgs e)
    {
        SettingsStore.Save(_settings);
        Application.Current.Shutdown();
    }

    // ---------- aktualizacja ----------

    /// <summary>Cicha aktualizacja: restart tylko wtedy, gdy nikt nie korzysta z programu i nie trwa rozmowa.</summary>
    void InstallUpdateWhenIdle()
    {
        if (!_settings.SilentUpdate || !UpdateManager.Instance.ReadyToInstall) return;
        if (_calls is { InCall: true } || DateTime.Now - _lastInput < IdleBeforeUpdate) return;
        if (Application.Current.Windows.OfType<SettingsWindow>().Any()) return;
        SettingsStore.Save(_settings);
        if (UpdateService.ApplyPendingAndRestart()) Application.Current.Shutdown();
    }

    void OnInstallUpdate(object sender, RoutedEventArgs e)
    {
        if (_calls is { InCall: true }) return;
        SettingsStore.Save(_settings);
        if (UpdateService.ApplyPendingAndRestart()) Application.Current.Shutdown();
        else ShowNotice("Nie udało się zainstalować nowej wersji. Spróbuję jeszcze raz później.");
    }

    // ---------- rozmowa ----------

    async void OnCall(object sender, RoutedEventArgs e)
    {
        var p = Selected;
        if (p == null || _calls == null || _calls.InCall) return;
        if (!CheckCallable(p)) return;

        if (!await CameraService.HasCameraAsync())
        {
            DetailPanel.Visibility = Visibility.Collapsed;
            NoCameraPanel.Visibility = Visibility.Visible;
            return;
        }
        await _calls.StartCallAsync(p, video: true);
    }

    async void OnVoiceCall(object sender, RoutedEventArgs e)
    {
        var p = Selected;
        NoCameraPanel.Visibility = Visibility.Collapsed;
        DetailPanel.Visibility = p == null ? Visibility.Collapsed : Visibility.Visible;
        if (p == null || _calls == null || _calls.InCall) return;
        if (!CheckCallable(p)) return;
        await _calls.StartCallAsync(p, video: false);
    }

    void OnNoCameraBack(object sender, RoutedEventArgs e) => UpdateDetail();

    bool CheckCallable(Person p)
    {
        if (_identity == null) { ShowNotice("Ten program nie ma kodu instalacji, więc nie może dzwonić."); return false; }
        if (p.Presence == 2) { ShowNotice($"{p.Name} rozmawia teraz. Spróbuj za chwilę."); return false; }
        if (p.Presence == 0)
        {
            // Obecność mogła się zmienić, a my jeszcze o tym nie wiemy – serwer sprawdzi to przy dzwonieniu, jeśli mamy połączenie.
            if (_sig is { IsConnected: true }) { ShowNotice($"{p.Name} nie jest teraz w sieci. Spróbuj później."); return false; }
        }
        return true;
    }
}
