using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace PoGGaduchy;

public partial class SettingsWindow : Window
{
    static readonly Person Sample = new() { ServerName = "Przykładowa osoba" };
    const string DefaultDevice = "Domyślne (systemowe)", Missing = " (niepodłączone)";

    readonly AppSettings _settings = Services.Settings;
    readonly Dictionary<string, FrameworkElement> _panels;
    bool _devicesInit, _fillingDevices, _devicesLoaded;

    ServerApi? Api => Services.Api;

    public SettingsWindow()
    {
        InitializeComponent();
        Display.FitToScreen(this);
        DataContext = _settings;
        _panels = new()
        {
            ["image"] = PanelImage, ["av"] = PanelAv, ["calls"] = PanelCalls, ["look"] = PanelLook,
            ["people"] = PanelPeople, ["program"] = PanelProgram,
        };
        Preview.Content = _settings.Contacts.FirstOrDefault() ?? Sample;
        // lista "odbieraj automatycznie" bez kont NEW (te widzi tylko administrator, w grupie Osoby)
        var auto = (ListCollectionView)new CollectionViewSource { Source = _settings.Contacts }.View;
        auto.Filter = o => o is Person { IsNew: false };
        auto.IsLiveFiltering = true;
        auto.LiveFilteringProperties.Add(nameof(Person.IsNew));
        AutoAnswerList.ItemsSource = auto;
        NoPeopleText.Visibility = _settings.Contacts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PeopleList.ItemsSource = _settings.Contacts;

        VersionText.Text = $"Wersja programu: {AppInfo.VersionText}";
        DataPath.Text = SettingsStore.DataDir;
        RefreshIdentityText();
        LoadMyPhoto();
        var um = UpdateManager.Instance;
        um.PropertyChanged += OnUpdateChanged;
        Closed += (_, _) => um.PropertyChanged -= OnUpdateChanged;
        RefreshUpdateUi();
        _settings.PropertyChanged += OnSettingChanged;
        Closed += (_, _) => { _settings.PropertyChanged -= OnSettingChanged; StopPreview(); SettingsStore.Save(_settings); };
        if (Api != null) { Api.PropertyChanged += OnApiChanged; Closed += (_, _) => Api.PropertyChanged -= OnApiChanged; }
        UpdateAdminUi();
        UpdateNavPeople();
        foreach (var p in _settings.Contacts) p.PropertyChanged += OnAnyPersonChanged;
        NotifyCollectionChangedEventHandler hook = (_, e) =>
        {
            if (e.NewItems != null) foreach (Person p in e.NewItems) p.PropertyChanged += OnAnyPersonChanged;
            if (e.OldItems != null) foreach (Person p in e.OldItems) p.PropertyChanged -= OnAnyPersonChanged;
            UpdateNavPeople();
        };
        _settings.Contacts.CollectionChanged += hook;
        Closed += (_, _) =>
        {
            _settings.Contacts.CollectionChanged -= hook;
            foreach (var p in _settings.Contacts) p.PropertyChanged -= OnAnyPersonChanged;
        };
        Nav.SelectedIndex = 0;
    }

    /// <summary>Otwiera od razu wybraną grupę (tag: image, av, calls, look, people, program) – do testów i skrótów.</summary>
    public void ShowGroup(string tag)
    {
        foreach (ListBoxItem i in Nav.Items) if ((string)i.Tag == tag && i.Visibility == Visibility.Visible) Nav.SelectedItem = i;
    }

    void OnNavChanged(object sender, SelectionChangedEventArgs e)
    {
        var tag = (Nav.SelectedItem as ListBoxItem)?.Tag as string ?? "image";
        foreach (var (k, p) in _panels) p.Visibility = k == tag ? Visibility.Visible : Visibility.Collapsed;
        if (tag == "av") _ = EnsureDevicesAsync(); else StopPreview();
        if (tag == "people" && PeopleList.SelectedItem == null && _settings.Contacts.Count > 0) PeopleList.SelectedIndex = 0;
        if (tag == "program") { RefreshIdentityText(); }
        if (tag == "image") LoadMyPhoto();
    }

    void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.Autostart)) Autostart.Apply(_settings.Autostart);
    }

    void OnApiChanged(object? sender, PropertyChangedEventArgs e) => Dispatcher.BeginInvoke(UpdateAdminUi);

    void OnAnyPersonChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Person.IsNew)) return;
        Dispatcher.BeginInvoke(() => { UpdateNavPeople(); UpdateNewUi(); });
    }

    /// <summary>Etykieta grupy administratora z liczbą kont czekających na akceptację.</summary>
    void UpdateNavPeople()
    {
        var n = _settings.Contacts.Count(p => p.IsNew);
        var label = n > 0 ? $"Osoby · NEW: {n}" : "Osoby (administrator)";
        NavPeople.Content = label;
        System.Windows.Automation.AutomationProperties.SetName(NavPeople, label);
    }

    void UpdateNewUi()
    {
        var isNew = CurrentPerson is { IsNew: true };
        AcceptButton.Visibility = isNew ? Visibility.Visible : Visibility.Collapsed;
        NewInfo.Visibility = isNew ? Visibility.Visible : Visibility.Collapsed;
    }

    void UpdateAdminUi()
    {
        var admin = Api is { IsAdmin: true };
        NavPeople.Visibility = admin ? Visibility.Visible : Visibility.Collapsed;
        if (!admin && ReferenceEquals(Nav.SelectedItem, NavPeople)) Nav.SelectedIndex = 0;
        RefreshIdentityText();
    }

    // ---------- mój obraz ----------

    void LoadMyPhoto()
    {
        var path = SettingsStore.AvatarPath(_settings.MyId);
        MyPhoto.LoadExisting(string.IsNullOrEmpty(_settings.MyId) ? null : SettingsStore.LoadFile(path), _settings.MyName);
    }

    static string Describe(ApiResult r) => r switch
    {
        ApiResult.Ok => "Zapisane.",
        ApiResult.NetworkError => "Brak połączenia z serwerem. Sprawdź internet i spróbuj za chwilę.",
        ApiResult.Forbidden => "Serwer odmówił (brak uprawnień).",
        ApiResult.Unauthorized => "Serwer nie rozpoznaje kodu instalacji tego programu.",
        ApiResult.TooLarge => "Obraz jest za duży.",
        ApiResult.Conflict => "Taka osoba już istnieje albo nie można tego zrobić.",
        ApiResult.Busy => "Serwer chwilowo odmawia (zbyt wiele prób). Spróbuj za chwilę.",
        ApiResult.NotFound => "Nie znaleziono (osoba mogła zostać usunięta).",
        _ => "Nie udało się. Spróbuj jeszcze raz.",
    };

    async void OnSaveMyImage(object sender, RoutedEventArgs e)
    {
        if (Api == null || string.IsNullOrEmpty(_settings.MyId)) { MyImageStatus.Text = "Brak połączenia z serwerem. Spróbuj za chwilę."; return; }
        var jpeg = MyPhoto.GetJpeg();
        if (jpeg == null) { MyImageStatus.Text = "Najpierw wybierz zdjęcie (przycisk „Wybierz zdjęcie…”)."; return; }
        MyImageStatus.Text = "Zapisuję…";
        var r = await Api.PutAvatarAsync(_settings.MyId, jpeg, asAdmin: false);
        MyImageStatus.Text = r == ApiResult.Ok ? "Zapisane. Twój obraz zobaczą teraz wszystkie osoby." : Describe(r);
        if (r == ApiResult.Ok)
        {
            try { Directory.CreateDirectory(SettingsStore.ImagesDir); File.WriteAllBytes(SettingsStore.AvatarPath(_settings.MyId), jpeg); } catch { }
            _settings.MyAvLocal = -1;                          // następne połączenie z serwerem pobierze aktualną wersję
        }
    }

    async void OnRemoveMyImage(object sender, RoutedEventArgs e)
    {
        if (Api == null || string.IsNullOrEmpty(_settings.MyId)) { MyImageStatus.Text = "Brak połączenia z serwerem. Spróbuj za chwilę."; return; }
        var r = await Api.DeleteAvatarAsync(_settings.MyId, asAdmin: false);
        if (r is ApiResult.Ok or ApiResult.NotFound)
        {
            try { File.Delete(SettingsStore.AvatarPath(_settings.MyId)); } catch { }
            _settings.MyAvLocal = 0;
            MyPhoto.LoadExisting(null, _settings.MyName);
            MyImageStatus.Text = "Obraz usunięty. Inne osoby zobaczą na Twoim kafelku literę.";
        }
        else MyImageStatus.Text = Describe(r);
    }

    // ---------- audio i wideo ----------

    async Task EnsureDevicesAsync()
    {
        if (_devicesInit) { StartPreview(); return; }
        _devicesInit = true;
        try
        {
            var env = await WebViewHost.GetEnvironmentAsync();
            await DeviceView.EnsureCoreWebView2Async(env);
            var core = DeviceView.CoreWebView2;
            WebViewHost.Attach(core);
            core.WebMessageReceived += OnDeviceMessage;
            core.Navigate(WebViewHost.Origin + "/devices.html");
            DeviceView.Visibility = Visibility.Visible;
            // jak w oknie rozmowy: po pokazaniu ukrytej kontrolki wymuszamy ponowne ułożenie (inaczej WebView2 bywa pusty)
            DeviceView.Margin = new Thickness(0, 0, 1, 1); DeviceView.UpdateLayout(); DeviceView.Margin = new Thickness(0); DeviceView.UpdateLayout();
            DeviceStatus.Text = "Szukam urządzeń…";
        }
        catch (Exception ex)
        {
            Log.Write("Urządzenia: " + ex.Message);
            DeviceStatus.Text = "Nie można odczytać urządzeń (brak składnika WebView2).";
        }
    }

    void OnDeviceMessage(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        using var doc = JsonDocument.Parse(e.WebMessageAsJson);
        var m = doc.RootElement;
        switch (m.TryGetProperty("type", out var t) ? t.GetString() : null)
        {
            case "devices":
                Fill(MicBox, m, "mic", _settings.MicLabel);
                Fill(SpeakerBox, m, "speaker", _settings.SpeakerLabel);
                Fill(CameraBox, m, "camera", _settings.CameraLabel);
                _devicesLoaded = true;
                DeviceStatus.Text = "";
                break;
            case "ready": StartPreview(); break;
            case "preview":
                var cam = m.GetProperty("camera").GetBoolean(); var mic = m.GetProperty("mic").GetBoolean();
                DeviceStatus.Text = (cam ? "" : "Kamera: nie wykryto albo używa jej inny program. ") + (mic ? "" : "Mikrofon: nie wykryto.");
                break;
            case "error": DeviceStatus.Text = "Nie można odczytać urządzeń."; break;
        }
    }

    void Fill(ComboBox box, JsonElement m, string key, string current)
    {
        _fillingDevices = true;
        box.Items.Clear();
        box.Items.Add(DefaultDevice);
        foreach (var l in m.GetProperty(key).EnumerateArray()) box.Items.Add(l.GetString() ?? "");
        var idx = string.IsNullOrEmpty(current) ? 0 : box.Items.IndexOf(current);
        if (idx < 0) { box.Items.Add(current + Missing); idx = box.Items.Count - 1; }
        box.SelectedIndex = idx;
        _fillingDevices = false;
    }

    static string LabelOf(ComboBox box) =>
        box.SelectedIndex <= 0 || box.SelectedItem is not string s ? "" : s.EndsWith(Missing) ? s[..^Missing.Length] : s;

    void OnDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingDevices || !_devicesLoaded) return;
        _settings.MicLabel = LabelOf(MicBox);
        _settings.SpeakerLabel = LabelOf(SpeakerBox);
        _settings.CameraLabel = LabelOf(CameraBox);
        SettingsStore.Save(_settings);
        Services.Calls?.ApplyDevices();                        // trwająca rozmowa przełącza się od razu
        StartPreview();
    }

    void StartPreview()
    {
        if (!_devicesLoaded && !_devicesInit) return;
        if (Services.Calls is { InCall: true })
        {
            DeviceStatus.Text = "Trwa rozmowa: podgląd jest wyłączony, ale zmiany działają od razu.";
            DeviceView.Visibility = Visibility.Hidden;        // kamera jest zajęta przez rozmowę – pusty podgląd tylko by mylił
            return;
        }
        DeviceView.Visibility = Visibility.Visible;
        Post(new { type = "preview", mic = _settings.MicLabel, camera = _settings.CameraLabel });
    }

    void StopPreview() => Post(new { type = "stop" });

    void Post(object o)
    {
        try { DeviceView.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(o)); } catch { }
    }

    void OnTestSpeakers(object sender, RoutedEventArgs e) => Post(new { type = "tone", speaker = _settings.SpeakerLabel });
    void OnRefreshPreview(object sender, RoutedEventArgs e) => StartPreview();

    // ---------- osoby (administrator) ----------

    Person? CurrentPerson => PeopleList.SelectedItem as Person;

    void OnPersonChanged(object sender, SelectionChangedEventArgs e)
    {
        var p = CurrentPerson;
        PersonEdit.IsEnabled = p != null;
        PersonNameBox.Text = p?.Name ?? "";
        PersonPhoto.LoadExisting(p == null ? null : SettingsStore.LoadFile(SettingsStore.AvatarPath(p.Id)) is { } img && p.AvatarVer > 0 ? img : null, p?.Name ?? "");
        PeopleStatus.Text = "";
        UpdateNewUi();
    }

    async void OnSavePersonName(object sender, RoutedEventArgs e)
    {
        var p = CurrentPerson; var name = PersonNameBox.Text.Trim();
        if (p == null || Api == null || name.Length == 0) return;
        var r = await Api.RenamePersonAsync(p.Id, name);
        PeopleStatus.Text = r == ApiResult.Ok ? "Nazwa zapisana (wszyscy zobaczą ją za chwilę)." : Describe(r);
        if (r == ApiResult.Ok) p.ServerName = name;
    }

    async void OnSavePersonImage(object sender, RoutedEventArgs e)
    {
        var p = CurrentPerson;
        if (p == null || Api == null) return;
        var jpeg = PersonPhoto.GetJpeg();
        if (jpeg == null) { PeopleStatus.Text = "Najpierw wybierz zdjęcie (przycisk „Wybierz zdjęcie…”)."; return; }
        PeopleStatus.Text = "Zapisuję…";
        var r = await Api.PutAvatarAsync(p.Id, jpeg, asAdmin: true);
        PeopleStatus.Text = r == ApiResult.Ok ? $"Obraz osoby „{p.Name}” zapisany i widoczny u wszystkich." : Describe(r);
    }

    async void OnRemovePersonImage(object sender, RoutedEventArgs e)
    {
        var p = CurrentPerson;
        if (p == null || Api == null) return;
        var r = await Api.DeleteAvatarAsync(p.Id, asAdmin: true);
        PeopleStatus.Text = r is ApiResult.Ok or ApiResult.NotFound ? "Obraz usunięty." : Describe(r);
        if (r is ApiResult.Ok or ApiResult.NotFound) PersonPhoto.LoadExisting(null, p.Name);
    }

    static string Slug(string name)
    {
        var sb = new StringBuilder();
        foreach (var ch in name.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            var c = ch switch { 'ł' or 'Ł' => 'l', _ => char.ToLowerInvariant(ch) };
            sb.Append(c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : '-');
        }
        var s = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), "-+", "-").Trim('-');
        return s.Length == 0 ? "osoba" : s[..Math.Min(s.Length, 26)].Trim('-');
    }

    async void OnAddPerson(object sender, RoutedEventArgs e)
    {
        if (Api == null || Services.Identity == null) { PeopleStatus.Text = "Brak połączenia z serwerem."; return; }
        var name = InputDialog.Ask(this, "Nazwa nowej osoby (np. „Ciocia Hela”):", "Dodaj");
        if (name == null) return;
        var baseId = Slug(name);
        var id = baseId;
        for (var n = 2; _settings.Contacts.Any(c => c.Id == id) || id == _settings.MyId; n++) id = baseId + "-" + n;
        var (key, hash) = ServerApi.NewKey();
        PeopleStatus.Text = "Dodaję osobę…";
        var r = await Api.AddPersonAsync(id, name, hash);
        if (r != ApiResult.Ok) { PeopleStatus.Text = Describe(r); return; }
        var file = CreateInstallFolder(id, key);
        PeopleStatus.Text = $"Dodano osobę „{name}”. Plik z jej kodem instalacji jest gotowy.";
        ShowFile(file);
        MessageBox.Show(this,
            $"Osoba „{name}” została dodana.\n\nPlik do przekazania jej: {file}\n\nTo jej tajny kod instalacji. Ma go położyć obok PoGGaduchy.exe na swoim komputerze. " +
            "Przekaż go tylko jej – kod daje dostęp do rozmów.", "Nowa osoba", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>Zapisuje kod-instalacji.txt nowej osoby w folderze "Nowe osoby\id" (bez kopii programu i bez zipa). Zwraca ścieżkę pliku.</summary>
    string CreateInstallFolder(string id, string key)
    {
        var dir = Path.Combine(SettingsStore.DataDir, "Nowe osoby", id);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "kod-instalacji.txt");
        File.WriteAllText(file, Identity.CreateCode(Services.Identity!.Server, key, id));
        return file;
    }

    static void ShowFile(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); } catch { }
    }

    async void OnNewCode(object sender, RoutedEventArgs e)
    {
        var p = CurrentPerson;
        if (p == null || Api == null || Services.Identity == null) return;
        if (MessageBox.Show(this, $"Wydać nowy kod instalacji dla osoby „{p.Name}”?\n\nStary kod przestanie działać od razu, więc trzeba będzie przekazać jej nowy plik.",
                "Nowy kod instalacji", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var (key, hash) = ServerApi.NewKey();
        var r = await Api.SetKeyHashAsync(p.Id, hash);
        if (r != ApiResult.Ok) { PeopleStatus.Text = Describe(r); return; }
        var file = CreateInstallFolder(p.Id, key);
        PeopleStatus.Text = $"Nowy kod dla „{p.Name}” gotowy: {file}";
        ShowFile(file);
    }

    async void OnAcceptPerson(object sender, RoutedEventArgs e)
    {
        var p = CurrentPerson;
        if (p == null || Api == null) return;
        var r = await Api.AcceptPersonAsync(p.Id);
        PeopleStatus.Text = r == ApiResult.Ok ? $"Konto „{p.Name}” zaakceptowane. Od teraz widzi je cała rodzina." : Describe(r);
    }

    async void OnRemovePerson(object sender, RoutedEventArgs e)
    {
        var p = CurrentPerson;
        if (p == null || Api == null) return;
        if (MessageBox.Show(this, $"Czy usunąć osobę „{p.Name}”?\n\nStraci dostęp do rozmów, a jej obraz zostanie skasowany.",
                "Usuwanie osoby", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var r = await Api.RemovePersonAsync(p.Id);
        PeopleStatus.Text = r == ApiResult.Ok ? "Osoba usunięta." : Describe(r);
    }

    // ---------- kod instalacji i administrator ----------

    void RefreshIdentityText()
    {
        var id = Services.Identity; var sig = Services.Sig;
        var who = string.IsNullOrEmpty(_settings.MyName) ? "" : $"To jest program osoby: {_settings.MyName}. ";
        if (id == null) IdentityText.Text = "Brak kodu instalacji – program nie może łączyć się z serwerem.";
        else IdentityText.Text = who + "Serwer: " + id.Server + ". Stan: " +
            (sig is { IsConnected: true } ? "połączono." : (sig?.StatusText is { Length: > 0 } s ? s : "brak połączenia."));
        AdminText.Text = Api is { IsAdmin: true } ? "Ta kopia jest administratorem (plik Admin.PMa potwierdzony przez serwer)."
            : AdminFile.Load() != null ? (Api?.AdminNote is { Length: > 0 } n ? n : "Plik Admin.PMa jest, ale serwer jeszcze go nie potwierdził.") : "";
    }

    void OnSaveCode(object sender, RoutedEventArgs e)
    {
        Identity? id;
        try { id = Identity.Parse(CodeBox.Text); } catch { id = null; }
        if (id == null) { CodeStatus.Text = "To nie jest poprawny kod instalacji."; return; }
        Identity.Save(CodeBox.Text);
        CodeStatus.Text = "Zapisano. Program uruchomi się ponownie, żeby użyć nowego kodu.";
        SettingsStore.Save(_settings);
        Process.Start(new ProcessStartInfo(AppInfo.ExePath, "--after-update " + Environment.ProcessId) { UseShellExecute = true });
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
        if (Services.Calls is { InCall: true }) { UpdateStatus.Text = "Trwa rozmowa – nowa wersja zainstaluje się po jej zakończeniu."; return; }
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
