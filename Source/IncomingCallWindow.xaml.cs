using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace PoGGaduchy;

/// <summary>Pełnoekranowy komunikat o rozmowie przychodzącej: duże zdjęcie i nazwa, wielki zielony "Odbierz" i czerwony "Odrzuć".</summary>
public partial class IncomingCallWindow : Window
{
    readonly Ringtone _ring = new();
    bool _done;

    public event Action? Answered;
    public event Action? Declined;

    public IncomingCallWindow(Person person, bool video, bool hasCamera)
    {
        InitializeComponent();
        DataContext = person;
        Initial.Visibility = person.Image == null ? Visibility.Visible : Visibility.Collapsed;
        KindText.Text = video ? "Rozmowa z obrazem (wideo)" : "Rozmowa bez obrazu (tylko głos)";
        NoCameraText.Visibility = hasCamera ? Visibility.Collapsed : Visibility.Visible;
        Loaded += (_, _) =>
        {
            Native.ForceForeground(new WindowInteropHelper(this).Handle);
            _ring.Start();
        };
        Closed += (_, _) => _ring.Dispose();
    }

    void Finish(Action? raise)
    {
        if (_done) return;
        _done = true;
        _ring.Stop();
        raise?.Invoke();
    }

    void OnAnswer(object sender, RoutedEventArgs e) => Finish(Answered);
    void OnDecline(object sender, RoutedEventArgs e) => Finish(Declined);

    void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space) { Finish(Answered); e.Handled = true; }
        else if (e.Key == Key.Escape) { Finish(Declined); e.Handled = true; }
    }

    /// <summary>Zamknięcie przez program (np. dzwoniący zrezygnował) – bez wywoływania Answered/Declined.</summary>
    public void CloseQuietly()
    {
        _done = true;
        _ring.Stop();
        Close();
    }
}
