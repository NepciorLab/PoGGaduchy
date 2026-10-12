using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace PoGGaduchy;

/// <summary>
/// Pokazywane zamiast komunikatu o braku kodu instalacji: nowa osoba wybiera nazwę (i zdjęcie), program zakłada konto na serwerze
/// (konto czeka na akceptację administratora), zapisuje plik kod-instalacji.txt i uruchamia się ponownie już jako ta osoba.
/// </summary>
public partial class RegistrationWindow : Window
{
    bool _busy;

    public RegistrationWindow()
    {
        InitializeComponent();
        Photo.LoadExisting(null, "");
        Loaded += (_, _) => NameBox.Focus();
    }

    void OnNameChanged(object sender, TextChangedEventArgs e) { if (!Photo.HasImage) Photo.LoadExisting(null, NameBox.Text); }

    async void OnCreate(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var name = NameBox.Text.Trim();
        if (name.Length == 0) { StatusText.Text = "Wpisz swoje imię."; NameBox.Focus(); return; }
        _busy = true; CreateButton.IsEnabled = false;
        try
        {
            StatusText.Text = "Łączę z serwerem…";
            var server = await ServerLocator.ResolveAsync();
            if (server == null) { StatusText.Text = "Nie udało się znaleźć serwera. Sprawdź internet i naciśnij „Utwórz konto” jeszcze raz."; return; }

            var (key, hash) = ServerApi.NewKey();
            StatusText.Text = "Zakładam konto…";
            var (result, id) = await ServerApi.RegisterAsync(server, name, hash);
            if (result != ApiResult.Ok || id == null)
            {
                StatusText.Text = result switch
                {
                    ApiResult.Busy => "Teraz zbyt wiele kont czeka na akceptację. Spróbuj za kilka minut albo poproś administratora.",
                    ApiResult.NetworkError => "Brak połączenia z serwerem. Sprawdź internet i spróbuj jeszcze raz.",
                    _ => "Nie udało się założyć konta. Spróbuj jeszcze raz.",
                };
                return;
            }

            // plik kod-instalacji.txt tej osoby (stary, jeśli był uszkodzony, zostaje jako kopia)
            var identity = new Identity(server, key, id);
            try { if (File.Exists(Identity.FilePath)) File.Copy(Identity.FilePath, Identity.FilePath + ".bak", true); } catch { }
            Identity.Save(Identity.CreateCode(server, key, id));
            Log.Write($"Założono konto {id} na serwerze {server} (czeka na akceptację).");

            if (Photo.Dirty && Photo.GetJpeg() is { } jpeg)
            {
                StatusText.Text = "Wysyłam zdjęcie…";
                var r = await new ServerApi(identity).PutAvatarAsync(id, jpeg, asAdmin: false);
                if (r != ApiResult.Ok) Log.Write("Zdjęcie przy zakładaniu konta nie zostało wysłane: " + r + " (można je ustawić później w Ustawieniach).");
            }

            StatusText.Text = "Gotowe! Uruchamiam program…";
            await Task.Delay(900);
            Process.Start(new ProcessStartInfo(AppInfo.ExePath, "--after-update " + Environment.ProcessId) { UseShellExecute = true });
            Application.Current.Shutdown();
        }
        finally { _busy = false; CreateButton.IsEnabled = true; }
    }

    void OnExit(object sender, RoutedEventArgs e) => Application.Current.Shutdown();
}
