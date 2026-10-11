using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace PoGGaduchy;

/// <summary>Okno rozmowy: zmaksymalizowane, w środku strona z WebRTC (call.html). Czerwona słuchawka jest częścią strony.</summary>
public partial class CallWindow : Window
{
    bool _closingByApp;

    /// <summary>Wiadomość ze strony rozmowy (już sparsowana).</summary>
    public event Action<JsonElement>? PageMessage;

    public CallWindow()
    {
        InitializeComponent();
        Closing += (_, e) =>
        {
            if (_closingByApp) return;
            e.Cancel = true;                                       // Alt+F4 = rozłączenie
            PageMessage?.Invoke(JsonDocument.Parse("{\"type\":\"hangup\"}").RootElement.Clone());
        };
    }

    /// <summary>Uruchamia WebView2 i ładuje stronę rozmowy. Zwraca false, gdy się nie udało (powód w logu i na ekranie).</summary>
    public async Task<bool> InitializeAsync()
    {
        try
        {
            var env = await WebViewHost.GetEnvironmentAsync();
            await Web.EnsureCoreWebView2Async(env);
            var core = Web.CoreWebView2;
            var s = core.Settings;
            s.AreDefaultContextMenusEnabled = false;
            s.AreDevToolsEnabled = Environment.GetEnvironmentVariable("PGG_DEVTOOLS") == "1";
            s.IsZoomControlEnabled = false;
            s.IsStatusBarEnabled = false;
            s.IsSwipeNavigationEnabled = false;
            s.IsPinchZoomEnabled = false;
            s.AreBrowserAcceleratorKeysEnabled = false;
            s.IsGeneralAutofillEnabled = false;
            s.IsPasswordAutosaveEnabled = false;

            // Strona rozmowy jest wbudowana w program i serwowana pod "fałszywym" adresem https (bezpieczny kontekst dla kamery).
            core.AddWebResourceRequestedFilter(WebViewHost.Origin + "/*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += OnResourceRequested;
            core.PermissionRequested += OnPermissionRequested;
            core.NavigationStarting += (_, e) => { if (!e.Uri.StartsWith(WebViewHost.Origin + "/", StringComparison.Ordinal)) e.Cancel = true; };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.WebMessageReceived += (_, e) =>
            {
                try { PageMessage?.Invoke(JsonDocument.Parse(e.WebMessageAsJson).RootElement.Clone()); }
                catch (Exception ex) { Log.Write("Wiadomość ze strony: " + ex.Message); }
            };
            core.ProcessFailed += (_, e) => Log.Write("WebView2: proces " + e.ProcessFailedKind + " zakończył się błędem (" + e.Reason + ")");
            core.Navigate(WebViewHost.Origin + "/call.html");
            Web.Visibility = Visibility.Visible;
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("WebView2: " + ex.Message);
            ShowError("Do rozmów wideo potrzebny jest składnik Microsoft Edge WebView2. Program spróbuje go przygotować; jeśli to się nie uda, trzeba go zainstalować.");
            return false;
        }
    }

    void OnResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var core = Web.CoreWebView2;
        var path = new Uri(e.Request.Uri).AbsolutePath.TrimStart('/');
        var stream = path == "call.html" ? WebViewHost.OpenResource("Web.call.html") : null;
        if (stream == null)
        {
            e.Response = core.Environment.CreateWebResourceResponse(null, 404, "Not Found", "");
            return;
        }
        e.Response = core.Environment.CreateWebResourceResponse(stream, 200, "OK",
            "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nPermissions-Policy: camera=(self), microphone=(self)");
    }

    // Kamera i mikrofon: zgoda raz na stałe, tylko dla wbudowanej strony rozmowy – żadnych pytań w trakcie rozmowy.
    void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        var ours = e.Uri.StartsWith(WebViewHost.Origin + "/", StringComparison.Ordinal);
        var media = e.PermissionKind is CoreWebView2PermissionKind.Camera or CoreWebView2PermissionKind.Microphone;
        e.State = ours && media ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
        e.SavesInProfile = true;
    }

    public void Post(object message)
    {
        try { Web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(message)); }
        catch (Exception ex) { Log.Write("Wysyłanie do strony: " + ex.Message); }
    }

    public void ShowError(string text)
    {
        Web.Visibility = Visibility.Collapsed;
        ErrorText.Text = text;
        ErrorPanel.Visibility = Visibility.Visible;
    }

    void OnErrorHangup(object sender, RoutedEventArgs e) =>
        PageMessage?.Invoke(JsonDocument.Parse("{\"type\":\"hangup\"}").RootElement.Clone());

    /// <summary>Zamyka okno bez udawania rozłączenia.</summary>
    public void CloseForReal()
    {
        _closingByApp = true;
        try { Web.Dispose(); } catch { }
        Close();
    }

    /// <summary>Zrzut strony rozmowy do pliku PNG (testy).</summary>
    public async Task CaptureAsync(string path)
    {
        await using var fs = File.Create(path);
        await Web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs);
    }
}
