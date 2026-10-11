using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace PoGGaduchy;

public partial class SettingsWindow : Window
{
    static readonly Person Sample = new() { ServerName = "Przykładowa osoba" };
    readonly AppSettings _settings;
    readonly SignalingClient? _sig;
    Identity? _identity;
    Point _dragLast;
    bool _dragging;

    public SettingsWindow(AppSettings settings, SignalingClient? sig, Identity? identity)
    {
        _settings = settings;
        _sig = sig;
        _identity = identity;
        InitializeComponent();
        Display.FitToScreen(this);
        DataContext = settings;
        List.ItemsSource = settings.Contacts;
        if (settings.Contacts.Count > 0) List.SelectedIndex = 0;
        OnContactChanged(null!, null!);

        VersionText.Text = $"Wersja programu: {AppInfo.VersionText}";
        DataPath.Text = SettingsStore.DataDir;
        RefreshIdentityText();
        var um = UpdateManager.Instance;
        um.PropertyChanged += OnUpdateChanged;
        Closed += (_, _) => um.PropertyChanged -= OnUpdateChanged;
        RefreshUpdateUi();
        settings.PropertyChanged += OnSettingChanged;
        Closed += (_, _) => settings.PropertyChanged -= OnSettingChanged;
    }

    Person? Current => List.SelectedItem as Person;

    void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.Autostart)) Autostart.Apply(_settings.Autostart);
    }

    // ---------- osoby ----------

    void OnContactChanged(object sender, SelectionChangedEventArgs e)
    {
        EditCard.IsEnabled = Current != null;
        Preview.Content = Current ?? Sample;
        UpdateInitialVisibility();
    }

    void UpdateInitialVisibility() =>
        PhotoInitial.Visibility = Current is { Image: null } || Current == null ? Visibility.Visible : Visibility.Collapsed;

    void OnMoveUp(object sender, RoutedEventArgs e) => Move(-1);
    void OnMoveDown(object sender, RoutedEventArgs e) => Move(1);

    void Move(int delta)
    {
        var i = List.SelectedIndex;
        var j = i + delta;
        if (i < 0 || j < 0 || j >= _settings.Contacts.Count) return;
        _settings.Contacts.Move(i, j);
        List.SelectedIndex = j;
    }

    // ---------- zdjęcie ----------

    void OnPickImage(object sender, RoutedEventArgs e)
    {
        var c = Current;
        if (c == null) return;
        var dlg = new OpenFileDialog
        {
            Title = "Wybierz zdjęcie osoby",
            Filter = "Obrazy|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp;*.tif;*.tiff|Wszystkie pliki|*.*"
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var newFile = SettingsStore.ImportImage(dlg.FileName);
            var old = c.ImageFile;
            c.SetImage(newFile);
            DeleteImageFile(old);
            UpdateInitialVisibility();
        }
        catch (Exception ex)
        {
            Log.Write("Import obrazu: " + ex.Message);
            MessageBox.Show(this, "Nie udało się wczytać tego zdjęcia.", "Błąd", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    void OnRemoveImage(object sender, RoutedEventArgs e)
    {
        var c = Current;
        if (c == null) return;
        var old = c.ImageFile;
        c.SetImage("");
        DeleteImageFile(old);
        UpdateInitialVisibility();
    }

    void OnResetCrop(object sender, RoutedEventArgs e) => Current?.ResetCrop();

    void OnRotateLeft(object sender, RoutedEventArgs e) { if (Current is { Image: not null } c) c.Rotate(clockwise: false); }
    void OnRotateRight(object sender, RoutedEventArgs e) { if (Current is { Image: not null } c) c.Rotate(clockwise: true); }

    // Przeciąganie zdjęcia w ramce: zdjęcie "jedzie" za myszą. Kółko myszy zmienia powiększenie.
    void OnPhotoDown(object sender, MouseButtonEventArgs e)
    {
        if (Current?.Image == null) return;
        _dragging = true;
        _dragLast = e.GetPosition(PhotoFrame);
        PhotoFrame.CaptureMouse();
    }

    void OnPhotoMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || Current is not { } c) return;
        var p = e.GetPosition(PhotoFrame);
        c.Pan((p.X - _dragLast.X) / PhotoFrame.ActualWidth, (p.Y - _dragLast.Y) / PhotoFrame.ActualHeight);
        _dragLast = p;
    }

    void OnPhotoUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        PhotoFrame.ReleaseMouseCapture();
    }

    void OnPhotoWheel(object sender, MouseWheelEventArgs e)
    {
        if (Current is not { } c || c.Image == null) return;
        c.ImageZoom *= Math.Pow(1.1, e.Delta / 120.0);
        e.Handled = true;
    }

    static void DeleteImageFile(string file)
    {
        if (string.IsNullOrWhiteSpace(file)) return;
        try { File.Delete(Path.Combine(SettingsStore.ImagesDir, file)); } catch { }
    }

    // ---------- kod instalacji ----------

    void RefreshIdentityText()
    {
        var who = string.IsNullOrEmpty(_settings.MyName) ? "" : $"To jest program osoby: {_settings.MyName}. ";
        if (_identity == null) IdentityText.Text = "Brak kodu instalacji – program nie może łączyć się z serwerem.";
        else IdentityText.Text = who + "Serwer: " + _identity.Server + ". Stan: " +
            (_sig is { IsConnected: true } ? "połączono." : (_sig?.StatusText is { Length: > 0 } s ? s : "brak połączenia."));
    }

    void OnSaveCode(object sender, RoutedEventArgs e)
    {
        Identity? id;
        try { id = Identity.Parse(CodeBox.Text); } catch { id = null; }
        if (id == null) { CodeStatus.Text = "To nie jest poprawny kod instalacji."; return; }
        Identity.Save(CodeBox.Text);
        CodeStatus.Text = "Zapisano. Program uruchomi się ponownie, żeby użyć nowego kodu.";
        SettingsStore.Save(_settings);
        var exe = AppInfo.ExePath;
        Process.Start(new ProcessStartInfo(exe, "--after-update " + Environment.ProcessId) { UseShellExecute = true });
        Application.Current.Shutdown();
    }

    // ---------- aktualizacje ----------

    void OnUpdateChanged(object? sender, PropertyChangedEventArgs e) => Dispatcher.BeginInvoke(RefreshUpdateUi);

    void RefreshUpdateUi()
    {
        var um = UpdateManager.Instance;
        UpdateStatus.Text = um.Status;
        CheckUpdateButton.IsEnabled = !um.Busy;
        InstallUpdateButton.Visibility = um.ReadyToInstall ? Visibility.Visible : Visibility.Collapsed;
    }

    async void OnCheckUpdate(object sender, RoutedEventArgs e) => await UpdateManager.Instance.CheckAndDownloadAsync(manual: true);

    void OnInstallUpdate(object sender, RoutedEventArgs e)
    {
        SettingsStore.Save(_settings);
        if (UpdateService.ApplyPendingAndRestart()) Application.Current.Shutdown();
        else MessageBox.Show(this, "Nie udało się zainstalować aktualizacji. Szczegóły są w pliku log.txt.", "Aktualizacja", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    void OnOpenDataFolder(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(SettingsStore.DataDir) { UseShellExecute = true }); } catch { }
    }

    void OnDone(object sender, RoutedEventArgs e) => Close();

    void OnQuit(object sender, RoutedEventArgs e)
    {
        SettingsStore.Save(_settings);
        Application.Current.Shutdown();
    }
}
