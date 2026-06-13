using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DesktopOrganizer.UI;

/// <summary>
/// Прозрачный оверлей с направляющими линиями выравнивания. Под каждый показ позиционируется
/// РОВНО на монитор перетаскиваемой коробки (через SetWindowPos в физических пикселях), а
/// координаты линий считаются по DPI и началу именно этого монитора — поэтому на мультимониторе
/// со смешанным масштабом линии не смещаются. Окно не перехватывает мышь и фокус.
/// </summary>
public sealed class AlignmentOverlay : Window
{
    private readonly Line _vertical;
    private readonly Line _horizontal;
    private readonly Canvas _canvas;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    public AlignmentOverlay()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Topmost = true;
        IsHitTestVisible = false;

        var stroke = new SolidColorBrush(Color.FromArgb(0xE0, 0x00, 0xC8, 0xFF));
        stroke.Freeze();
        _vertical = new Line { Stroke = stroke, StrokeThickness = 1, Visibility = Visibility.Collapsed };
        _horizontal = new Line { Stroke = stroke, StrokeThickness = 1, Visibility = Visibility.Collapsed };

        _canvas = new Canvas();
        _canvas.Children.Add(_vertical);
        _canvas.Children.Add(_horizontal);
        Content = _canvas;

        SourceInitialized += (_, _) => MakeClickThrough();
    }

    private void MakeClickThrough()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
    }

    /// <summary>
    /// Показывает направляющие на мониторе (физ. границы monL/monT/monW/monH, масштаб sx/sy).
    /// guideX/guideY — физические экранные координаты линий; null — соответствующей линии нет.
    /// </summary>
    public void ShowGuides(int monL, int monT, int monW, int monH, double sx, double sy, int? guideX, int? guideY)
    {
        if (guideX == null && guideY == null)
        {
            HideGuides();
            return;
        }

        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        // Покрываем монитор целиком в ФИЗИЧЕСКИХ пикселях — WPF отрисует содержимое в его DPI.
        SetWindowPos(hwnd, HWND_TOPMOST, monL, monT, monW, monH, SWP_NOACTIVATE | SWP_SHOWWINDOW);

        // DIP-размеры клиентской области этого монитора.
        var widthDip = monW / sx;
        var heightDip = monH / sy;

        if (guideX is { } x)
        {
            _vertical.X1 = _vertical.X2 = (x - monL) / sx; // физ → DIP относительно начала монитора
            _vertical.Y1 = 0;
            _vertical.Y2 = heightDip;
            _vertical.Visibility = Visibility.Visible;
        }
        else _vertical.Visibility = Visibility.Collapsed;

        if (guideY is { } y)
        {
            _horizontal.Y1 = _horizontal.Y2 = (y - monT) / sy;
            _horizontal.X1 = 0;
            _horizontal.X2 = widthDip;
            _horizontal.Visibility = Visibility.Visible;
        }
        else _horizontal.Visibility = Visibility.Collapsed;
    }

    public void HideGuides()
    {
        _vertical.Visibility = Visibility.Collapsed;
        _horizontal.Visibility = Visibility.Collapsed;
        if (IsVisible) Hide();
    }
}
