using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DesktopOrganizer.UI;

/// <summary>
/// Полноэкранный прозрачный оверлей с направляющими линиями выравнивания —
/// тонкая подсказка появляется, когда грань перетаскиваемой коробки совпадает
/// с гранью соседней коробки или краем экрана. Не перехватывает мышь и фокус.
/// </summary>
public sealed class AlignmentOverlay : Window
{
    private readonly Line _vertical;
    private readonly Line _horizontal;

    public AlignmentOverlay()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        IsHitTestVisible = false;

        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        var stroke = new SolidColorBrush(Color.FromArgb(0xE0, 0x00, 0xC8, 0xFF));
        stroke.Freeze();
        _vertical = new Line { Stroke = stroke, StrokeThickness = 1, Visibility = Visibility.Collapsed };
        _horizontal = new Line { Stroke = stroke, StrokeThickness = 1, Visibility = Visibility.Collapsed };

        var canvas = new Canvas();
        canvas.Children.Add(_vertical);
        canvas.Children.Add(_horizontal);
        Content = canvas;

        SourceInitialized += (_, _) => MakeClickThrough();
    }

    private void MakeClickThrough()
    {
        var hwnd = ((HwndSource)PresentationSource.FromVisual(this)!).Handle;
        const int GWL_EXSTYLE = -20;
        const int WS_EX_TRANSPARENT = 0x20;
        const int WS_EX_TOOLWINDOW = 0x80;
        const int WS_EX_NOACTIVATE = 0x08000000;
        var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    /// <summary>Показывает направляющие. Координаты — экранные, в DIP; null — линии нет.</summary>
    public void ShowGuides(double? screenX, double? screenY)
    {
        if (screenX == null && screenY == null)
        {
            HideGuides();
            return;
        }
        if (!IsVisible) Show();

        if (screenX is { } x)
        {
            _vertical.X1 = _vertical.X2 = x - Left;
            _vertical.Y1 = 0;
            _vertical.Y2 = Height;
            _vertical.Visibility = Visibility.Visible;
        }
        else
        {
            _vertical.Visibility = Visibility.Collapsed;
        }

        if (screenY is { } y)
        {
            _horizontal.Y1 = _horizontal.Y2 = y - Top;
            _horizontal.X1 = 0;
            _horizontal.X2 = Width;
            _horizontal.Visibility = Visibility.Visible;
        }
        else
        {
            _horizontal.Visibility = Visibility.Collapsed;
        }
    }

    public void HideGuides()
    {
        _vertical.Visibility = Visibility.Collapsed;
        _horizontal.Visibility = Visibility.Collapsed;
        if (IsVisible) Hide();
    }
}
