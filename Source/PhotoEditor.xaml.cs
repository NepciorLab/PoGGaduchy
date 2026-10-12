using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace PoGGaduchy;

/// <summary>Wybór zdjęcia i kadrowanie (przesuwanie, powiększanie, obrót). Wynik: kwadratowy JPEG do wysłania na serwer.</summary>
public partial class PhotoEditor : UserControl
{
    readonly CropModel _model = new();
    Point _last;
    bool _dragging;

    public PhotoEditor()
    {
        InitializeComponent();
        DataContext = _model;
        _model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(CropModel.Image)) UpdateInitial();
            if (e.PropertyName is nameof(CropModel.Crop) or nameof(CropModel.Zoom) or nameof(CropModel.Image)) Dirty = _loading ? Dirty : true;
        };
    }

    bool _loading;

    /// <summary>Użytkownik coś zmienił (wybrał zdjęcie albo je przesunął/powiększył/obrócił), więc jest co zapisać.</summary>
    public bool Dirty { get; private set; }

    public bool HasImage => _model.Image != null;

    /// <summary>Pokazuje obecny obraz (bez uznawania go za zmianę) i literę, gdy obrazu nie ma.</summary>
    public void LoadExisting(BitmapSource? image, string initial)
    {
        _loading = true;
        _model.Source = image;
        _loading = false;
        Dirty = false;
        InitialText.Text = string.IsNullOrWhiteSpace(initial) ? "?" : initial.Trim()[..1].ToUpperInvariant();
        UpdateInitial();
    }

    void UpdateInitial() => InitialText.Visibility = _model.Image == null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>To, co widać w ramce, jako JPEG 512 x 512; null, gdy nie ma zdjęcia.</summary>
    public byte[]? GetJpeg() => _model.RenderJpeg();

    void OnPick(object sender, RoutedEventArgs e)
    {
        // PGG_TEST_PHOTO: tylko do testów automatycznych (pomija okno wyboru pliku).
        var path = Environment.GetEnvironmentVariable("PGG_TEST_PHOTO");
        if (string.IsNullOrEmpty(path))
        {
            var dlg = new OpenFileDialog
            {
                Title = "Wybierz zdjęcie",
                Filter = "Obrazy|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp;*.tif;*.tiff|Wszystkie pliki|*.*"
            };
            if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
            path = dlg.FileName;
        }
        var img = SettingsStore.LoadFile(path);
        if (img == null)
        {
            MessageBox.Show(Window.GetWindow(this), "Nie udało się wczytać tego zdjęcia.", "Błąd", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _model.Source = img;
        Dirty = true;
        UpdateInitial();
    }

    void OnReset(object sender, RoutedEventArgs e) { _model.ResetCrop(); Dirty = true; }
    void OnRotateLeft(object sender, RoutedEventArgs e) => _model.Rotate(clockwise: false);
    void OnRotateRight(object sender, RoutedEventArgs e) => _model.Rotate(clockwise: true);

    // Przeciąganie zdjęcia w ramce: zdjęcie "jedzie" za myszą. Kółko myszy zmienia powiększenie.
    void OnPhotoDown(object sender, MouseButtonEventArgs e)
    {
        if (_model.Image == null) return;
        _dragging = true;
        _last = e.GetPosition(PhotoFrame);
        PhotoFrame.CaptureMouse();
    }

    void OnPhotoMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var p = e.GetPosition(PhotoFrame);
        _model.Pan((p.X - _last.X) / PhotoFrame.ActualWidth, (p.Y - _last.Y) / PhotoFrame.ActualHeight);
        _last = p;
    }

    void OnPhotoUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        PhotoFrame.ReleaseMouseCapture();
    }

    void OnPhotoWheel(object sender, MouseWheelEventArgs e)
    {
        if (_model.Image == null) return;
        _model.Zoom *= Math.Pow(1.1, e.Delta / 120.0);
        e.Handled = true;
    }
}
