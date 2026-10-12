using System.Text.Json;
using System.Windows.Interop;
using System.Windows.Threading;

namespace PoGGaduchy;

public enum CallState { Idle, Dialing, Incoming, Connecting, Active }

/// <summary>
/// Cała logika rozmowy po stronie programu: dzwonienie, odbieranie, odrzucanie, auto-odbieranie, wznawianie po zerwaniu
/// połączenia z serwerem, zakończenia. Obraz i dźwięk obsługuje strona WebRTC w oknie rozmowy; ten obiekt przekazuje jej
/// dane negocjacji (przez serwer) i pilnuje czasu.
/// Wszystko dzieje się w wątku interfejsu.
/// </summary>
public sealed class CallManager : Observable
{
    static readonly TimeSpan RingTimeout = TimeSpan.FromSeconds(60);       // tyle dzwonimy
    static readonly TimeSpan IncomingTimeout = TimeSpan.FromSeconds(75);   // tyle czeka okno "Odbierz"
    static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(45);    // od odebrania do połączenia obrazu/dźwięku
    static readonly TimeSpan LostTimeout = TimeSpan.FromSeconds(60);       // tyle próbujemy odzyskać przerwane połączenie

    readonly AppSettings _settings;
    readonly SignalingClient _sig;
    readonly DispatcherTimer _timer = new();          // jeden zegar na bieżącą fazę (dzwonienie / odbieranie / łączenie)
    readonly DispatcherTimer _lostTimer = new();
    readonly List<JsonElement> _queuedSig = new();

    CallState _state;
    string _cid = "", _role = "";
    Person? _peer;
    bool _video, _hasCamera, _pageReady, _peerReady, _accepted, _started, _initSent, _pageFailed;
    CallWindow? _win;
    IncomingCallWindow? _incoming;
    Task<JsonElement>? _iceTask;
    DateTime _startedAt;

    public CallManager(AppSettings settings, SignalingClient sig)
    {
        _settings = settings;
        _sig = sig;
        _sig.Message += OnSignal;
        _sig.Welcome += OnWelcome;
        _timer.Tick += (_, _) => { _timer.Stop(); OnPhaseTimeout(); };
        _lostTimer.Interval = LostTimeout;
        _lostTimer.Tick += (_, _) =>
        {
            _lostTimer.Stop();
            Log.Write("Rozmowa: połączenie nie wróciło w " + LostTimeout.TotalSeconds + " s, kończę.");
            SendEnd("error");
            Teardown($"Połączenie z {PeerName} zostało przerwane.");
        };
    }

    public CallState State
    {
        get => _state;
        private set { if (Set(ref _state, value)) { Raise(nameof(InCall)); StateChanged?.Invoke(value); } }
    }

    public bool InCall => _state != CallState.Idle;

    public event Action<CallState>? StateChanged;

    /// <summary>Użytkownik przytrzymał przycisk ustawień w oknie rozmowy.</summary>
    public event Action? SettingsRequested;

    object DevicePrefs() => new { mic = _settings.MicLabel, speaker = _settings.SpeakerLabel, camera = _settings.CameraLabel };

    /// <summary>Po zmianie urządzeń w ustawieniach: trwająca rozmowa przełącza się na nie od razu.</summary>
    public void ApplyDevices() => _win?.Post(new { type = "devices", devices = DevicePrefs() });

    /// <summary>Krótki komunikat dla użytkownika (np. "Kasia nie odbiera"), wyświetlany w głównym oknie.</summary>
    public event Action<string>? Notice;

    /// <summary>Komunikat spoza rozmowy (np. nowe konto czeka na akceptację) w głównym oknie.</summary>
    public void RaiseNotice(string text) => Notice?.Invoke(text);

    string PeerName => _peer?.Name is { Length: > 0 } n ? n : "tej osoby";

    /// <summary>Okno rozmowy lub komunikatu o rozmowie – do wysunięcia na wierzch, gdy ktoś uruchomi program drugi raz.</summary>
    public bool ActivateCallUi()
    {
        var w = (System.Windows.Window?)_win ?? _incoming;
        if (w == null) return false;
        Native.ForceForeground(new WindowInteropHelper(w).Handle);
        return true;
    }

    static string NewCid() => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();

    void Begin(string role, Person peer, bool video, string cid)
    {
        _role = role; _peer = peer; _video = video; _cid = cid;
        _pageReady = _peerReady = _accepted = _started = _initSent = _pageFailed = false;
        _queuedSig.Clear();
        _startedAt = DateTime.Now;
    }

    // ---------------------------------------------------------------- dzwonienie

    public async Task StartCallAsync(Person person, bool video)
    {
        if (InCall) return;
        Begin("caller", person, video, NewCid());
        State = CallState.Dialing;
        Log.Write($"Dzwonię do {person.Id} (wideo: {video}), rozmowa {_cid}");
        var windowOk = OpenCallWindow();
        _iceTask = _sig.RequestIceAsync();
        var cid = _cid;
        // Dzwonimy dopiero, gdy okno rozmowy działa (nie dzwonimy do kogoś, z kim nie moglibyśmy rozmawiać).
        if (!await windowOk)
        {
            if (_cid == cid) FailWindow(sendEnd: false);
            return;
        }
        if (!await _sig.WaitConnectedAsync(TimeSpan.FromSeconds(20)))
        {
            if (_cid == cid) Teardown("Nie udało się połączyć z serwerem. Sprawdź internet – program łączy się dalej sam, spróbuj za chwilę.");
            return;
        }
        if (_cid != cid || State != CallState.Dialing) return;
        SendCall();
        StartTimer(RingTimeout);
    }

    void SendCall() => _sig.Send(new { t = "call", cid = _cid, to = _peer!.Id, video = _video });

    // ---------------------------------------------------------------- wiadomości z serwera

    void OnWelcome(JsonElement _)
    {
        switch (State)
        {
            case CallState.Dialing when !_accepted:
                SendCall();                                    // połączenie z serwerem było zerwane: ponawiamy dzwonienie
                break;
            case CallState.Incoming:
                Teardown(null);                                // serwer zapomniał o rozmowie (dzwoniący zniknął)
                break;
            case CallState.Connecting or CallState.Active or CallState.Dialing:
                _sig.Send(new { t = "resume", cid = _cid, peer = _peer!.Id });
                break;
        }
    }

    void OnSignal(JsonElement m)
    {
        var type = m.TryGetProperty("t", out var tt) ? tt.GetString() : null;
        string? Cid() => m.TryGetProperty("cid", out var c) ? c.GetString() : null;
        switch (type)
        {
            case "ring": OnRing(m); break;
            case "ringing" when Cid() == _cid && State == CallState.Dialing:
                _win?.Post(new { type = "status", text = $"Dzwonię do: {PeerName}…" });
                break;
            case "accepted" when Cid() == _cid && _role == "caller":
                _accepted = true; State = CallState.Connecting;
                StartTimer(ConnectTimeout);
                _win?.Post(new { type = "status", text = "Łączę…" });
                MaybeStart();
                break;
            case "end" when Cid() == _cid && InCall: OnEnded(m.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : ""); break;
            case "sig" when Cid() == _cid && InCall:
                if (_peerReady && m.TryGetProperty("data", out var data)) _win?.Post(new { type = "sig", data });
                else _queuedSig.Add(m.GetProperty("data").Clone());
                break;
            case "peerlost" when Cid() == _cid: Log.Write("Rozmowa: druga strona straciła połączenie z serwerem (rozmowa trwa)."); break;
            case "peerback" when Cid() == _cid: Log.Write("Rozmowa: druga strona wróciła do serwera."); break;
            case "resumed" when Cid() == _cid: Log.Write("Rozmowa: wznowiono sygnalizację."); break;
        }
    }

    async void OnRing(JsonElement m)
    {
        var from = m.GetProperty("from").GetString() ?? "";
        var cid = m.GetProperty("cid").GetString() ?? "";
        var video = m.TryGetProperty("video", out var v) && v.GetBoolean();
        if (InCall)
        {
            _sig.Send(new { t = "end", cid, reason = "rejected" });     // serwer nie powinien tego dopuścić, ale na wszelki wypadek
            return;
        }
        var person = _settings.Contacts.FirstOrDefault(p => p.Id == from) ?? new Person { Id = from, ServerName = m.TryGetProperty("name", out var fn) && fn.GetString() is { Length: > 0 } nm ? nm : from };
        Begin("callee", person, video, cid);
        State = CallState.Incoming;
        Log.Write($"Dzwoni {from} (wideo: {video}), rozmowa {cid}");
        _iceTask = _sig.RequestIceAsync();                 // poświadczenia TURN pobieramy już w czasie dzwonka
        _hasCamera = await CameraService.HasCameraAsync();
        if (_cid != cid || State != CallState.Incoming) return;

        if (person.AutoAnswer)
        {
            Log.Write("Automatyczne odebranie rozmowy od " + from);
            Accept();
            return;
        }
        _incoming = new IncomingCallWindow(person, video, _hasCamera);
        _incoming.Answered += Accept;
        _incoming.Declined += Decline;
        _incoming.Show();
        StartTimer(IncomingTimeout);
    }

    void OnEnded(string reason)
    {
        var name = PeerName;
        var wasIncoming = State == CallState.Incoming;
        var missed = $"Nieodebrane połączenie od: {name} ({_startedAt:HH:mm}).";
        string? text = reason switch
        {
            "offline" => $"{name} nie jest teraz w sieci. Spróbuj później.",
            "busy" => $"{name} rozmawia teraz. Spróbuj za chwilę.",
            "rejected" => $"{name} nie może teraz rozmawiać.",
            "timeout" or "cancel" when wasIncoming => missed,
            "lost" when wasIncoming => missed,
            "hangup" => $"Rozmowa z: {name} zakończona.",
            "gone" when State == CallState.Active => null,    // brak sygnalizacji, ale obraz i dźwięk idą dalej
            "gone" => $"Połączenie z: {name} zostało przerwane.",
            "lost" or "error" => $"Połączenie z: {name} zostało przerwane.",
            "timeout" or "cancel" => $"{name} zakończył(a) rozmowę.",
            _ => null,
        };
        if (reason == "gone" && State == CallState.Active) { Log.Write("Rozmowa: serwer nie zna już tej rozmowy; trwa połączenie bezpośrednie."); return; }
        Teardown(text);
    }

    // ---------------------------------------------------------------- odbieranie i odrzucanie

    async void Accept()
    {
        if (State != CallState.Incoming) return;
        _timer.Stop();
        _incoming?.CloseQuietly(); _incoming = null;
        State = CallState.Connecting;
        _accepted = true;
        var cid = _cid;
        await Task.Delay(300);        // okno "Dzwoni" ma chwilę, żeby całkiem zniknąć, zanim powstanie okno rozmowy
        if (_cid != cid) return;
        if (!await OpenCallWindow())
        {
            if (_cid == cid) FailWindow(sendEnd: true);
            return;
        }
        if (_cid != cid) return;
        _sig.Send(new { t = "accept", cid });
        StartTimer(ConnectTimeout);
    }

    void Decline()
    {
        if (State != CallState.Incoming) return;
        _sig.Send(new { t = "end", cid = _cid, reason = "rejected" });
        Teardown(null);
    }

    // ---------------------------------------------------------------- okno rozmowy i strona WebRTC

    /// <summary>Otwiera okno rozmowy i uruchamia w nim stronę WebRTC. Zwraca false, gdy strony nie dało się uruchomić.</summary>
    Task<bool> OpenCallWindow()
    {
        var win = new CallWindow();
        _win = win;
        win.PageMessage += m => { if (_win == win) OnPage(m); };
        win.Show();
        Native.ForceForeground(new WindowInteropHelper(win).Handle);
        return win.InitializeAsync();
    }

    /// <summary>Strona rozmowy nie wystartowała (np. brak WebView2): okno z komunikatem zostaje chwilę, potem rozmowa się kończy.</summary>
    void FailWindow(bool sendEnd)
    {
        Log.Write("Okno rozmowy: nie udało się uruchomić strony.");
        if (sendEnd) SendEnd("error");
        _pageFailed = true;
        StartTimer(TimeSpan.FromSeconds(12));
    }

    async Task SendInitAsync()
    {
        if (_initSent || _win == null || _peer == null) return;
        _initSent = true;
        var win = _win;
        var cid = _cid;
        var iceMsg = await _iceTask!;
        var ice = iceMsg.TryGetProperty("ice", out var i) ? i : default;
        var relay = iceMsg.TryGetProperty("relay", out var rl) && rl.GetBoolean();
        if (_win != win || _cid != cid) return;
        var wantVideo = _role == "caller" ? _video : await CameraService.HasCameraAsync();
        var peer = _peer;
        Log.Write($"Rozmowa {cid}: start strony (TURN: {relay}, wymuszony przekaźnik: {WebViewHost.ForceRelay}, kamera: {wantVideo})");
        win.Post(new
        {
            type = "init", role = _role, video = wantVideo, peerName = peer.Name, peerPhoto = PhotoRenderer.ToDataUrl(peer),
            ice, opts = new { forceRelay = WebViewHost.ForceRelay }, devices = DevicePrefs(),
            status = _role == "caller" ? (State == CallState.Dialing ? $"Dzwonię do: {peer.Name}…" : "Łączę…") : "Łączę…",
        });
    }

    void MaybeStart()
    {
        if (_role == "caller" && _accepted && _peerReady && !_started)
        {
            _started = true;
            _win?.Post(new { type = "start" });
        }
    }

    void OnPage(JsonElement m)
    {
        var type = m.TryGetProperty("type", out var t) ? t.GetString() : null;
        switch (type)
        {
            case "ready":
                _pageReady = true;
                _win?.Post(new { type = "status", text = State == CallState.Dialing ? "Łączę z serwerem…" : "Łączę…" });
                _ = SendInitAsync();
                break;
            case "peer-ready":
                _peerReady = true;
                foreach (var d in _queuedSig) _win?.Post(new { type = "sig", data = d });
                _queuedSig.Clear();
                MaybeStart();
                break;
            case "sig":
                if (m.TryGetProperty("data", out var data)) _sig.Send(new { t = "sig", cid = _cid, data });
                break;
            case "state": OnPageState(m.GetProperty("state").GetString() ?? "", m.TryGetProperty("ice", out var ic) ? ic.GetString() ?? "" : ""); break;
            case "hangup": UserHangup(); break;
            case "settings": SettingsRequested?.Invoke(); break;
            case "stats": Log.Write($"Rozmowa {_cid}: [statystyki] " + m.GetProperty("text").GetString()); break;
            case "media":
                Log.Write($"Rozmowa {_cid}: lokalne media – kamera: {m.GetProperty("video")}, mikrofon: {m.GetProperty("audio")}" + (m.TryGetProperty("videoError", out var ve) && ve.GetString() is { Length: > 0 } vs ? " (błąd kamery: " + vs + ")" : ""));
                break;
            case "path":
                Log.Write($"Rozmowa {_cid}: wybrana ścieżka połączenia – lokalna: {m.GetProperty("local")}, zdalna: {m.GetProperty("remote")}, protokół: {m.GetProperty("protocol")}");
                break;
            case "log": Log.Write("[strona rozmowy] " + m.GetProperty("msg").GetString()); break;
        }
    }

    void OnPageState(string state, string ice)
    {
        Log.Write($"Rozmowa {_cid}: połączenie {state} (ICE: {ice})");
        if (state == "connected")
        {
            _timer.Stop(); _lostTimer.Stop();
            if (State != CallState.Active)
            {
                State = CallState.Active;
                // Tylko do testów: PGG_SHOT_DIR – zrzut strony rozmowy kilka sekund po połączeniu (zrzut ekranu nie łapie WebView2).
                if (Environment.GetEnvironmentVariable("PGG_SHOT_DIR") is { Length: > 0 } dir && _win is { } w)
                {
                    var cid = _cid;
                    var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                    t.Tick += async (_, _) =>
                    {
                        t.Stop();
                        if (_cid != cid) return;
                        try { await w.CaptureAsync(System.IO.Path.Combine(dir, $"call-{_settings.MyId}.png")); } catch (Exception ex) { Log.Write("Zrzut rozmowy: " + ex.Message); }
                    };
                    t.Start();
                }
            }
        }
        else if (state is "disconnected" or "failed" && State is CallState.Active or CallState.Connecting)
        {
            if (State == CallState.Active && !_lostTimer.IsEnabled) _lostTimer.Start();
        }
    }

    void UserHangup()
    {
        if (!InCall) return;
        SendEnd(State == CallState.Dialing ? "cancel" : "hangup");
        Teardown(null);
    }

    void SendEnd(string reason)
    {
        if (!string.IsNullOrEmpty(_cid)) _sig.Send(new { t = "end", cid = _cid, reason });
    }

    // ---------------------------------------------------------------- czas

    void StartTimer(TimeSpan span) { _timer.Stop(); _timer.Interval = span; _timer.Start(); }

    void OnPhaseTimeout()
    {
        if (_pageFailed) { Teardown("Nie udało się uruchomić rozmowy. Do rozmów potrzebny jest składnik Microsoft Edge WebView2."); return; }
        switch (State)
        {
            case CallState.Dialing:
                SendEnd("timeout");
                Teardown($"{PeerName} nie odbiera. Spróbuj później.");
                break;
            case CallState.Incoming:
                SendEnd("timeout");
                Teardown($"Nieodebrane połączenie od: {PeerName} ({_startedAt:HH:mm}).");
                break;
            case CallState.Connecting:
                Log.Write("Rozmowa: nie udało się połączyć w " + ConnectTimeout.TotalSeconds + " s.");
                SendEnd("error");
                Teardown($"Nie udało się połączyć z: {PeerName}. Spróbuj jeszcze raz.");
                break;
        }
    }

    void Teardown(string? notice)
    {
        _timer.Stop(); _lostTimer.Stop();
        try { _incoming?.CloseQuietly(); } catch { }
        _incoming = null;
        var win = _win;
        _win = null;
        if (win != null)
        {
            try { win.Post(new { type = "close" }); } catch { }
            try { win.CloseForReal(); } catch { }
        }
        _queuedSig.Clear();
        var peer = _peer;
        _cid = ""; _peer = null; _role = "";
        State = CallState.Idle;
        if (!string.IsNullOrEmpty(notice)) Notice?.Invoke(notice);
        Log.Write("Rozmowa zakończona" + (notice != null ? ": " + notice : "") + (peer != null ? " (" + peer.Id + ")" : ""));
    }
}
