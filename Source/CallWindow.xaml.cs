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
            Web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x0B, 0x1B, 0x2B);     // bez białego błysku przed załadowaniem strony
            await Web.EnsureCoreWebView2Async(env);
            var core = Web.CoreWebView2;
            WebViewHost.Attach(core);
            core.WebMessageReceived += (_, e) =>
            {
                try { PageMessage?.Invoke(JsonDocument.Parse(e.WebMessageAsJson).RootElement.Clone()); }
                catch (Exception ex) { Log.Write("Wiadomość ze strony: " + ex.Message); }
            };
            // Okno rozmowy bywa tworzone zaraz po innym pełnoekranowym oknie (np. komunikacie "Dzwoni"); wtedy WebView2 potrafi
            // zostać pusty na ekranie, mimo że strona działa (brak przycisków i obrazu). Po załadowaniu strony wymuszamy więc
            // ponowne ułożenie kontrolki (przesunięcie o 1 piksel i z powrotem) – bez migotania.
            core.NavigationCompleted += async (_, _) => { foreach (var ms in new[] { 250, 600, 1500 }) { await Task.Delay(ms); RefreshLayout(); } };
            core.Navigate(WebViewHost.Origin + "/call.html");
            RefreshLayout();
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("WebView2: " + ex.Message);
            ShowError("Do rozmów wideo potrzebny jest składnik Microsoft Edge WebView2. Program spróbuje go przygotować; jeśli to się nie uda, trzeba go zainstalować.");
            return false;
        }
    }

    /// <summary>Wymusza ponowne ułożenie kontrolki WebView2 w oknie (patrz komentarz przy NavigationCompleted).</summary>
    void RefreshLayout()
    {
        if (!IsLoaded) return;
        Web.Margin = new Thickness(0, 0, 1, 1);
        Web.UpdateLayout();
        Web.Margin = new Thickness(0);
        Web.UpdateLayout();
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
