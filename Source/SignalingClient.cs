using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Win32;

namespace PoGGaduchy;

/// <summary>
/// Stałe połączenie z serwerem (WebSocket): obecność osób, dzwonienie, dane negocjacji.
/// Samo wznawia się po każdym zerwaniu (rosnące odstępy do 30 s), po powrocie sieci i po wybudzeniu komputera.
/// Podtrzymanie: co 25 s wysyła "ping"; serwer odpowiada "pong" automatycznie (nie zużywa limitu).
/// </summary>
public sealed class SignalingClient : Observable
{
    static readonly TimeSpan PingEvery = TimeSpan.FromSeconds(25), PongWithin = TimeSpan.FromSeconds(12);
    static readonly int[] Backoff = { 1, 2, 4, 8, 15, 30 };

    readonly Dispatcher _ui;
    readonly object _gate = new();
    Identity? _identity;
    CancellationTokenSource? _stop;
    CancellationTokenSource? _wake;          // przerywa bieżące łączenie/oczekiwanie, żeby połączyć się od razu
    ClientWebSocket? _ws;
    TaskCompletionSource<bool> _connected = NewTcs();
    TaskCompletionSource<JsonElement>? _turn;
    bool _isConnected;
    string _problem = "";
    int _attempt;
    DateTime _pongAt;

    static TaskCompletionSource<bool> NewTcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public SignalingClient(Dispatcher ui) { _ui = ui; }

    /// <summary>Komunikat z serwera (wywoływany w wątku interfejsu).</summary>
    public event Action<JsonElement>? Message;

    /// <summary>Po każdym (po)łączeniu, gdy serwer przywitał nas komunikatem "welcome" (wątek interfejsu).</summary>
    public event Action<JsonElement>? Welcome;

    public bool IsConnected { get => _isConnected; private set { if (Set(ref _isConnected, value)) Raise(nameof(StatusText)); } }

    /// <summary>Opis problemu z połączeniem (puste, gdy wszystko działa).</summary>
    public string Problem { get => _problem; private set { if (Set(ref _problem, value)) Raise(nameof(StatusText)); } }

    public string StatusText => IsConnected ? "" : string.IsNullOrEmpty(Problem) ? "Łączę z serwerem…" : Problem;

    public void Start(Identity identity)
    {
        Stop();
        _identity = identity;
        _stop = new CancellationTokenSource();
        var token = _stop.Token;
        SystemEvents.PowerModeChanged += OnPower;
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += OnNetwork;
        _ = Task.Run(() => RunAsync(identity, token));
    }

    public void Stop()
    {
        SystemEvents.PowerModeChanged -= OnPower;
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged -= OnNetwork;
        try { _stop?.Cancel(); } catch { }
        try { _ws?.Abort(); } catch { }
    }

    void OnPower(object? s, PowerModeChangedEventArgs e) { if (e.Mode == PowerModes.Resume) Reconnect("wznowienie systemu"); }
    void OnNetwork(object? s, System.Net.NetworkInformation.NetworkAvailabilityEventArgs e) { if (e.IsAvailable) Reconnect("powrót sieci"); }

    /// <summary>Zrywa bieżące połączenie i łączy się od razu (po powrocie sieci, po uśpieniu komputera).</summary>
    public void Reconnect(string why)
    {
        Log.Write("Sygnalizacja: łączę ponownie (" + why + ")");
        _attempt = 0;
        try { _wake?.Cancel(); } catch { }
        try { _ws?.Abort(); } catch { }
    }

    /// <summary>Czeka, aż połączenie z serwerem będzie gotowe (po "welcome"). Zwraca false po upływie czasu.</summary>
    public async Task<bool> WaitConnectedAsync(TimeSpan timeout)
    {
        if (IsConnected) return true;
        Task<bool> t; lock (_gate) t = _connected.Task;
        var done = await Task.WhenAny(t, Task.Delay(timeout));
        return done == t && IsConnected;
    }

    public bool Send(object message)
    {
        var ws = _ws;
        if (ws is not { State: WebSocketState.Open } || !IsConnected) return false;
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
        _ = SendRawAsync(ws, bytes);
        return true;
    }

    readonly SemaphoreSlim _sendLock = new(1, 1);
    async Task SendRawAsync(ClientWebSocket ws, byte[] bytes)
    {
        await _sendLock.WaitAsync();
        try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None); }
        catch (Exception ex) { Log.Write("Sygnalizacja: wysyłanie nie powiodło się: " + ex.Message); try { ws.Abort(); } catch { } }
        finally { _sendLock.Release(); }
    }

    /// <summary>Prosi serwer o poświadczenia TURN (z ponawianiem). Przy niepowodzeniu zwraca same publiczne serwery STUN.</summary>
    public async Task<JsonElement> RequestIceAsync(CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (!await WaitConnectedAsync(TimeSpan.FromSeconds(10))) continue;
            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _turn = tcs;
            if (!Send(new { t = "turn" })) continue;
            var done = await Task.WhenAny(tcs.Task, Task.Delay(8000, ct));
            if (done == tcs.Task) return tcs.Task.Result;
            Log.Write("Sygnalizacja: brak odpowiedzi TURN, ponawiam (" + (attempt + 1) + ")");
        }
        using var doc = JsonDocument.Parse("{\"t\":\"turn\",\"relay\":false,\"ice\":[{\"urls\":[\"stun:stun.cloudflare.com:3478\",\"stun:stun.l.google.com:19302\"]}]}");
        return doc.RootElement.Clone();
    }

    // ---------------------------------------------------------------- pętla połączenia

    async Task RunAsync(Identity id, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            var wait = TimeSpan.Zero;
            try
            {
                wait = await ConnectOnceAsync(id, stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                Log.Write("Sygnalizacja: " + ex.GetType().Name + ": " + ex.Message);
            }
            await SetConnectedAsync(false);
            if (stop.IsCancellationRequested) return;
            if (wait == TimeSpan.Zero) wait = TimeSpan.FromSeconds(Backoff[Math.Min(_attempt, Backoff.Length - 1)]);
            _attempt++;
            _wake = CancellationTokenSource.CreateLinkedTokenSource(stop);
            try { await Task.Delay(wait, _wake.Token); } catch (OperationCanceledException) { }
        }
    }

    async Task SetConnectedAsync(bool connected)
    {
        await _ui.InvokeAsync(() =>
        {
            if (IsConnected == connected) return;
            IsConnected = connected;
            lock (_gate)
            {
                if (connected) _connected.TrySetResult(true);
                else _connected = NewTcs();
            }
        });
    }

    /// <summary>Jedno połączenie, od nawiązania do zerwania. Zwraca czas oczekiwania przed następną próbą (0 = domyślny).</summary>
    async Task<TimeSpan> ConnectOnceAsync(Identity id, CancellationToken stop)
    {
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", "Bearer " + id.Key);
        if (AdminFile.Load() is { } adminKey) ws.Options.SetRequestHeader("X-Admin-Key", adminKey);   // serwer wie wtedy, że to administrator
        ws.Options.KeepAliveInterval = TimeSpan.Zero;           // własny ping tekstowy (patrz niżej)
        _ws = ws;
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(stop);
        connectCts.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await ws.ConnectAsync(id.WebSocketUri, connectCts.Token);
        }
        catch (WebSocketException ex) when (ex.Message.Contains("401"))
        {
            await _ui.InvokeAsync(() => Problem = "Ten program ma nieprawidłowy kod instalacji.");
            Log.Write("Sygnalizacja: serwer odrzucił klucz (401).");
            return TimeSpan.FromMinutes(10);
        }
        catch (WebSocketException ex) when (ex.Message.Contains("429"))
        {
            await _ui.InvokeAsync(() => Problem = "Serwer chwilowo odmawia połączenia. Ponawiam…");
            return TimeSpan.FromSeconds(60);
        }
        catch (Exception ex) when (!stop.IsCancellationRequested)
        {
            await _ui.InvokeAsync(() => Problem = "Brak połączenia z serwerem. Sprawdź internet; program łączy się dalej sam.");
            Log.Write("Sygnalizacja: nie można połączyć: " + ex.Message);
            return TimeSpan.Zero;
        }

        _pongAt = DateTime.UtcNow;
        using var lifeCts = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var pinger = Task.Run(() => PingLoopAsync(ws, lifeCts.Token));
        var wait = TimeSpan.Zero;
        var buffer = new byte[16 * 1024];
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var sb = new StringBuilder();
                WebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(buffer, stop);
                    if (r.MessageType == WebSocketMessageType.Close)
                    {
                        if (r.CloseStatus == (WebSocketCloseStatus)4000) wait = TimeSpan.FromSeconds(60);   // wyparł nas inny komputer
                        await _ui.InvokeAsync(() => { if (wait > TimeSpan.Zero) Problem = "Program jest uruchomiony na innym komputerze z tym samym kodem."; });
                        return wait;
                    }
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, r.Count));
                } while (!r.EndOfMessage && sb.Length < 200_000);

                var text = sb.ToString();
                if (text == "pong") { _pongAt = DateTime.UtcNow; continue; }
                JsonElement msg;
                try { using var doc = JsonDocument.Parse(text); msg = doc.RootElement.Clone(); } catch { continue; }
                var type = msg.TryGetProperty("t", out var tt) ? tt.GetString() : null;
                if (type == "welcome")
                {
                    _attempt = 0;
                    await _ui.InvokeAsync(() => { Problem = ""; });
                    await SetConnectedAsync(true);
                    await _ui.InvokeAsync(() => Welcome?.Invoke(msg));
                }
                else if (type == "turn") _turn?.TrySetResult(msg);
                await _ui.InvokeAsync(() => Message?.Invoke(msg));
            }
        }
        finally
        {
            lifeCts.Cancel();
            try { await pinger; } catch { }
            try { ws.Abort(); } catch { }
        }
        return wait;
    }

    async Task PingLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(PingEvery, ct);
                if (ws.State != WebSocketState.Open) return;
                var sent = DateTime.UtcNow;
                await SendRawAsync(ws, Encoding.UTF8.GetBytes("ping"));
                await Task.Delay(PongWithin, ct);
                if (_pongAt < sent)
                {
                    Log.Write("Sygnalizacja: brak odpowiedzi serwera na ping, zrywam połączenie.");
                    ws.Abort();
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
    }
}
