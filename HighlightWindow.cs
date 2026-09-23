using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace WordLens;

/// <summary>Рамка вокруг слова, которое сейчас переводится. Клики проходят сквозь неё.</summary>
internal sealed class HighlightWindow : Window
{
    private static readonly SolidColorBrush Stroke = new(Color.FromRgb(0x7C, 0xC4, 0xFF));
    private static readonly SolidColorBrush Fill = new(Color.FromArgb(0x22, 0x7C, 0xC4, 0xFF));

    private readonly Canvas _canvas = new();
    private IntPtr _hwnd;

    public HighlightWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        Focusable = false;
        ResizeMode = ResizeMode.NoResize;
        Left = Top = -10000;
        Width = Height = 10;
        Content = _canvas;
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Native.MakeOverlay(_hwnd, clickThrough: true);
        };
    }

    public void ShowAt(Rectangle word) => ShowRects([word]);

    /// <summary>Подсвечивает несколько кусков сразу — по одному на каждую строку выделения.
    /// Окно растягивается на всё выделение, а рамки рисуются внутри него.</summary>
    public void ShowRects(IReadOnlyList<Rectangle> rects)
    {
        if (rects.Count == 0) { Hide(); return; }
        if (!IsVisible) Show();

        var bounds = rects.Aggregate(Rectangle.Union);
        bounds.Inflate(6, 5);
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, bounds.X, bounds.Y, bounds.Width, bounds.Height,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);

        // Координаты слов — в пикселях экрана, а WPF рисует в своих единицах; при масштабе Windows 125–150% они различаются.
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        _canvas.Children.Clear();
        foreach (var r in rects)
        {
            var frame = new Border
            {
                BorderBrush = Stroke,
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(4),
                Background = Fill,
                Width = (r.Width + 10) / scale,
                Height = (r.Height + 8) / scale,
            };
            Canvas.SetLeft(frame, (r.X - 5 - bounds.X) / scale);
            Canvas.SetTop(frame, (r.Y - 4 - bounds.Y) / scale);
            _canvas.Children.Add(frame);
        }
    }
}
