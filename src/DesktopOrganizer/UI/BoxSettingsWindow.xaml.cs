using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DesktopOrganizer.UI;

/// <summary>
/// Настройки коробки: имя, цвет фона, прозрачность (раздел 5.12 ТЗ).
/// Цвет и прозрачность применяются к коробке сразу (живой предпросмотр);
/// «Отмена» возвращает исходные значения.
/// </summary>
public partial class BoxSettingsWindow : Window
{
    // Разноцветная палитра. Альфа CC — фирменная полупрозрачность фона.
    private static readonly string[] Palette =
    {
        "#CC1E242C", // графит (по умолчанию)
        "#CC5A5A5A", // серый
        "#CCB71C1C", // красный
        "#CCE64A19", // оранжевый
        "#CCF9A825", // желтый
        "#CC2E7D32", // зеленый
        "#CC00897B", // бирюзовый
        "#CC0277BD", // голубой
        "#CC1A4FBA", // синий
        "#CC6A1B9A", // фиолетовый
        "#CCC2185B", // розовый
        "#CC5D4037", // коричневый
    };

    private readonly BoxWindow _target;
    private readonly string _origColor;
    private readonly double _origOpacity;
    private bool _accepted;

    public BoxSettingsWindow(BoxWindow target)
    {
        InitializeComponent();
        _target = target;
        _origColor = target.Box.BackgroundColor;
        _origOpacity = target.Box.Opacity;

        NameBox.Text = target.Box.Name;
        OpacitySlider.Value = target.Box.Opacity;
        OpacityLabel.Text = $"{(int)(target.Box.Opacity * 100)}%";

        foreach (var hex in Palette)
        {
            var swatch = new Button
            {
                Width = 36,
                Height = 28,
                Margin = new Thickness(0, 0, 6, 6),
                Background = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!,
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(string.Equals(hex, _origColor, StringComparison.OrdinalIgnoreCase) ? 2 : 1),
                Tag = hex,
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            swatch.Click += Swatch_Click;
            SwatchPanel.Children.Add(swatch);
        }

        Closed += (_, _) =>
        {
            if (_accepted) return;
            // Отмена — откатываем живой предпросмотр.
            _target.Box.BackgroundColor = _origColor;
            _target.Box.Opacity = _origOpacity;
            _target.ApplyAppearance();
        };

        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    private void Swatch_Click(object sender, RoutedEventArgs e)
    {
        var hex = (string)((Button)sender).Tag;
        _target.Box.BackgroundColor = hex;
        _target.ApplyAppearance();
        foreach (Button b in SwatchPanel.Children)
            b.BorderThickness = new Thickness(ReferenceEquals(b, sender) ? 2 : 1);
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_target == null) return; // событие при инициализации XAML
        _target.Box.Opacity = e.NewValue;
        _target.ApplyAppearance();
        if (OpacityLabel != null) OpacityLabel.Text = $"{(int)(e.NewValue * 100)}%";
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(NameBox.Text))
            _target.Box.Name = NameBox.Text.Trim();
        _target.ApplyAppearance();
        App.Db.UpdateBox(_target.Box);
        _accepted = true;
        DialogResult = true;
    }
}
