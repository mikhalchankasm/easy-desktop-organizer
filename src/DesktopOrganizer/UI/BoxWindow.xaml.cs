using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using DesktopOrganizer.Models;
using DesktopOrganizer.Services;

namespace DesktopOrganizer.UI;

/// <summary>
/// Визуальная коробка поверх рабочего стола: перемещение, ресайз,
/// drag-and-drop, контекстные меню (разделы 5.1–5.3, 9.2–9.3 ТЗ).
/// Окно «приклеено» к нижнему слою z-order и ведет себя как виджет рабочего стола.
/// </summary>
public partial class BoxWindow : Window
{
    private const string InternalDragFormat = "DesktopOrganizer.Item";

    public Box Box { get; }
    public ObservableCollection<BoxItem> Items { get; } = new();

    private readonly DispatcherTimer _saveTimer;
    private Point _dragStart;
    private double _expandedHeight;
    private IntPtr _hwnd;

    public IntPtr Hwnd => _hwnd;

    // Размер значков и ширина ячейки — для биндингов из ItemTemplate.
    public static readonly DependencyProperty IconSizeProperty =
        DependencyProperty.Register(nameof(IconSize), typeof(double), typeof(BoxWindow), new PropertyMetadata(32.0));

    public static readonly DependencyProperty CellWidthProperty =
        DependencyProperty.Register(nameof(CellWidth), typeof(double), typeof(BoxWindow), new PropertyMetadata(84.0));

    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    public double CellWidth
    {
        get => (double)GetValue(CellWidthProperty);
        set => SetValue(CellWidthProperty, value);
    }

    private App OwnerApp => (App)Application.Current;

    public BoxWindow(Box box)
    {
        InitializeComponent();
        Box = box;

        Left = box.X;
        Top = box.Y;
        Width = box.Width;
        Height = box.Height;
        _expandedHeight = box.Height;

        ItemsList.ItemsSource = Items;
        ApplyAppearance();

        // Дебаунс-сохранение геометрии при перемещении/ресайзе (раздел 5.8: восстановление после перезапуска).
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); PersistGeometry(); };
        LocationChanged += (_, _) => ScheduleSave();
        SizeChanged += (_, _) => ScheduleSave();

        SourceInitialized += OnSourceInitialized;

        LoadItems();
        if (Box.IsCollapsed) ApplyCollapsed(true, save: false);
    }

    // ---------- Приклейка к нижнему слою z-order (поверх обоев, под окнами приложений) ----------

    private const int WM_WINDOWPOSCHANGING = 0x0046;
    private const int WM_SIZING = 0x0214;
    private const int WM_MOVING = 0x0216;
    private const int WM_EXITSIZEMOVE = 0x0232;
    private const uint SWP_NOZORDER = 0x0004;
    private static readonly IntPtr HWND_BOTTOM = new(1);

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public IntPtr hwnd;
        public IntPtr hwndInsertAfter;
        public int x, y, cx, cy;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int L, T, R, B;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO mi);

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    /// <summary>Рабочая область монитора под окном в ФИЗИЧЕСКИХ пикселях (корректно при mixed-DPI).</summary>
    private bool TryGetWorkAreaPhysical(out RECT work)
    {
        work = default;
        var mon = MonitorFromWindow(_hwnd, MONITOR_DEFAULTTONEAREST);
        if (mon == IntPtr.Zero) return false;
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(mon, ref mi)) return false;
        work = mi.rcWork;
        return true;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var source = (HwndSource)PresentationSource.FromVisual(this)!;
        _hwnd = source.Handle;
        source.AddHook(BottomMostHook);

        // Владелец — рабочий стол: Win+D / «Показать рабочий стол» не скрывает коробки.
        DesktopHost.SetDesktopAsOwner(_hwnd);

        // Win+D / Win+M / «Показать рабочий стол» сворачивают все окна.
        // Коробка — часть рабочего стола, поэтому немедленно разворачиваемся обратно:
        // ее видимостью управляет только персональная клавиша Ctrl+Alt+D (раздел 5.6 ТЗ).
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        };
    }

    private IntPtr BottomMostHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_WINDOWPOSCHANGING)
        {
            var wp = Marshal.PtrToStructure<WINDOWPOS>(lParam);
            if ((wp.flags & SWP_NOZORDER) == 0)
            {
                wp.hwndInsertAfter = HWND_BOTTOM;
                Marshal.StructureToPtr(wp, lParam, false);
            }
        }
        else if (msg == WM_MOVING)
        {
            // Магнитное выравнивание: легкое прилипание + направляющая линия-подсказка.
            var r = Marshal.PtrToStructure<RECT>(lParam);
            SnapToNeighbors(ref r, out var guideX, out var guideY);
            Marshal.StructureToPtr(r, lParam, false);

            // Координаты направляющих — физические пиксели; оверлей переведёт по своему DPI.
            OwnerApp.Overlay.ShowGuidesPhysical(guideX, guideY);

            handled = true;
            return new IntPtr(1);
        }
        else if (msg == WM_SIZING)
        {
            // Прилипание при изменении размера: тянущаяся грань магнитится к соседям.
            var r = Marshal.PtrToStructure<RECT>(lParam);
            SnapResize(ref r, wParam.ToInt32(), out var guideX, out var guideY);
            Marshal.StructureToPtr(r, lParam, false);

            // Координаты направляющих — физические пиксели; оверлей переведёт по своему DPI.
            OwnerApp.Overlay.ShowGuidesPhysical(guideX, guideY);

            handled = true;
            return new IntPtr(1);
        }
        else if (msg == WM_EXITSIZEMOVE)
        {
            OwnerApp.Overlay.HideGuides();
        }
        return IntPtr.Zero;
    }

    /// <summary>Грани всех соседних коробок и рабочей области монитора в физических пикселях.</summary>
    private (List<int> Xs, List<int> Ys) CollectSnapTargets()
    {
        var xs = new List<int>();
        var ys = new List<int>();
        foreach (var other in OwnerApp.GetBoxWindows())
        {
            if (ReferenceEquals(other, this) || !other.IsVisible) continue;
            if (!GetWindowRect(other.Hwnd, out var o)) continue;
            xs.Add(o.L);
            xs.Add(o.R);
            ys.Add(o.T);
            ys.Add(o.B);
        }
        if (TryGetWorkAreaPhysical(out var wa))
        {
            xs.Add(wa.L);
            xs.Add(wa.R);
            ys.Add(wa.T);
            ys.Add(wa.B);
        }
        return (xs, ys);
    }

    // Коды граней wParam у WM_SIZING.
    private const int WMSZ_LEFT = 1, WMSZ_RIGHT = 2, WMSZ_TOP = 3, WMSZ_TOPLEFT = 4,
        WMSZ_TOPRIGHT = 5, WMSZ_BOTTOM = 6, WMSZ_BOTTOMLEFT = 7, WMSZ_BOTTOMRIGHT = 8;

    private void SnapResize(ref RECT r, int edge, out int? guideX, out int? guideY)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var threshold = (int)Math.Round(8 * dpi.DpiScaleX);
        var minW = (int)Math.Round(MinWidth * dpi.DpiScaleX);
        var minH = (int)Math.Round(MinHeight * dpi.DpiScaleY);
        var (xs, ys) = CollectSnapTargets();

        var movesLeft = edge is WMSZ_LEFT or WMSZ_TOPLEFT or WMSZ_BOTTOMLEFT;
        var movesRight = edge is WMSZ_RIGHT or WMSZ_TOPRIGHT or WMSZ_BOTTOMRIGHT;
        var movesTop = edge is WMSZ_TOP or WMSZ_TOPLEFT or WMSZ_TOPRIGHT;
        var movesBottom = edge is WMSZ_BOTTOM or WMSZ_BOTTOMLEFT or WMSZ_BOTTOMRIGHT;

        guideX = null;
        guideY = null;

        var ourX = movesLeft ? r.L : movesRight ? r.R : (int?)null;
        if (ourX != null)
        {
            var best = FindNearest(ourX.Value, xs, threshold);
            if (best != null)
            {
                var newL = movesLeft ? best.Value : r.L;
                var newR = movesRight ? best.Value : r.R;
                if (newR - newL >= minW)
                {
                    r.L = newL;
                    r.R = newR;
                    guideX = best.Value;
                }
            }
        }

        var ourY = movesTop ? r.T : movesBottom ? r.B : (int?)null;
        if (ourY != null)
        {
            var best = FindNearest(ourY.Value, ys, threshold);
            if (best != null)
            {
                var newT = movesTop ? best.Value : r.T;
                var newB = movesBottom ? best.Value : r.B;
                if (newB - newT >= minH)
                {
                    r.T = newT;
                    r.B = newB;
                    guideY = best.Value;
                }
            }
        }
    }

    private static int? FindNearest(int value, List<int> targets, int threshold)
    {
        int? best = null;
        foreach (var t in targets)
        {
            var d = Math.Abs(t - value);
            if (d <= threshold && (best == null || d < Math.Abs(best.Value - value)))
                best = t;
        }
        return best;
    }

    private void SnapToNeighbors(ref RECT r, out int? guideX, out int? guideY)
    {
        // Небольшой порог — легкий магнетизм, из которого просто выйти.
        var dpi = VisualTreeHelper.GetDpi(this);
        var threshold = (int)Math.Round(8 * dpi.DpiScaleX);

        var width = r.R - r.L;
        var height = r.B - r.T;
        int? dx = null, dy = null;
        int gx = 0, gy = 0;

        void ConsiderX(int delta, int line)
        {
            if (Math.Abs(delta) <= threshold && (dx == null || Math.Abs(delta) < Math.Abs(dx.Value)))
            {
                dx = delta;
                gx = line;
            }
        }

        void ConsiderY(int delta, int line)
        {
            if (Math.Abs(delta) <= threshold && (dy == null || Math.Abs(delta) < Math.Abs(dy.Value)))
            {
                dy = delta;
                gy = line;
            }
        }

        foreach (var other in OwnerApp.GetBoxWindows())
        {
            if (ReferenceEquals(other, this) || !other.IsVisible) continue;
            if (!GetWindowRect(other.Hwnd, out var o)) continue;

            // Левая/правая грань — к обеим граням соседа (встык и заподлицо).
            ConsiderX(o.L - r.L, o.L);
            ConsiderX(o.R - r.L, o.R);
            ConsiderX(o.L - r.R, o.L);
            ConsiderX(o.R - r.R, o.R);
            // Верхняя/нижняя грань.
            ConsiderY(o.T - r.T, o.T);
            ConsiderY(o.B - r.T, o.B);
            ConsiderY(o.T - r.B, o.T);
            ConsiderY(o.B - r.B, o.B);
        }

        // Края рабочей области монитора (в физических пикселях — корректно при mixed-DPI).
        if (TryGetWorkAreaPhysical(out var wa))
        {
            ConsiderX(wa.L - r.L, wa.L);
            ConsiderX(wa.R - r.R, wa.R);
            ConsiderY(wa.T - r.T, wa.T);
            ConsiderY(wa.B - r.B, wa.B);
        }

        if (dx != null) { r.L += dx.Value; r.R = r.L + width; }
        if (dy != null) { r.T += dy.Value; r.B = r.T + height; }
        guideX = dx != null ? gx : null;
        guideY = dy != null ? gy : null;
    }

    // ---------- Внешний вид и состояние ----------

    public void ApplyAppearance()
    {
        TitleText.Text = Box.Name;
        Title = Box.Name;
        try
        {
            var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(Box.BackgroundColor)!;
            brush.Freeze();
            RootBorder.Background = brush;
        }
        catch { /* некорректный цвет в БД — оставляем цвет по умолчанию */ }
        Opacity = Math.Clamp(Box.Opacity, 0.2, 1.0);

        IconSize = Box.IconSize;
        CellWidth = Box.IconSize switch { <= 24 => 68, <= 32 => 84, _ => 104 };
    }

    private int IconPixelSize => Box.IconSize > 32 ? 48 : 32;

    private void SetIconSize(int size)
    {
        Box.IconSize = size;
        App.Db.UpdateBox(Box);
        ApplyAppearance();
        LoadItems(); // перечитываем иконки в нужном разрешении
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void PersistGeometry()
    {
        Box.X = Left;
        Box.Y = Top;
        Box.Width = Width;
        if (!Box.IsCollapsed) Box.Height = Height;
        App.Db.UpdateBox(Box);
    }

    public void PersistNow() => PersistGeometry();

    public void LoadItems()
    {
        Items.Clear();
        foreach (var it in App.Db.GetItems(Box.Id))
        {
            var isDir = it.ItemType == "Folder";
            var missing = isDir ? !Directory.Exists(it.FullPath) : !File.Exists(it.FullPath);
            if (missing != it.IsMissing)
            {
                it.IsMissing = missing;
                App.Db.SetItemMissing(it.Id, missing);
            }
            it.Icon = IconCache.GetIcon(it.FullPath, isDir, IconPixelSize);
            Items.Add(it);
        }
    }

    // ---------- Перемещение и сворачивание ----------

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ApplyCollapsed(!Box.IsCollapsed);
            return;
        }
        if (!Box.IsLocked)
        {
            try { DragMove(); }
            catch (InvalidOperationException) { /* кнопка уже отпущена */ }
        }
    }

    private void CollapseButton_Click(object sender, RoutedEventArgs e) => ApplyCollapsed(!Box.IsCollapsed);

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private void OpenSettings() => new BoxSettingsWindow(this).ShowDialog();

    private void ApplyCollapsed(bool collapsed, bool save = true)
    {
        Box.IsCollapsed = collapsed;
        if (collapsed)
        {
            if (Height > 40) _expandedHeight = Height;
            ItemsList.Visibility = Visibility.Collapsed;
            MinHeight = 34;
            Height = 34;
        }
        else
        {
            ItemsList.Visibility = Visibility.Visible;
            Height = Math.Max(_expandedHeight, 80);
        }
        if (save) App.Db.UpdateBox(Box);
    }

    private void SetLocked(bool locked)
    {
        Box.IsLocked = locked;
        var chrome = WindowChrome.GetWindowChrome(this);
        chrome.ResizeBorderThickness = locked ? new Thickness(0) : new Thickness(6);
        App.Db.UpdateBox(Box);
    }

    // ---------- Drag-and-drop (раздел 5.3 ТЗ) ----------

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(InternalDragFormat))
            e.Effects = DragDropEffects.Move;
        else if (e.Data.GetDataPresent(DataFormats.FileDrop))
            e.Effects = DragDropEffects.Link;
        else
            e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        try
        {
            if (e.Data.GetDataPresent(InternalDragFormat))
            {
                var payload = (string)e.Data.GetData(InternalDragFormat)!;
                var parts = payload.Split('|');
                var itemId = long.Parse(parts[0]);
                var sourceBoxId = long.Parse(parts[1]);
                var hovered = GetItemUnderMouse(e.OriginalSource);

                if (sourceBoxId == Box.Id)
                {
                    // Ручная сортировка: перетаскивание элемента на новое место внутри коробки.
                    var item = Items.FirstOrDefault(i => i.Id == itemId);
                    if (item != null)
                    {
                        var from = Items.IndexOf(item);
                        var to = hovered != null ? Items.IndexOf(hovered) : Items.Count - 1;
                        if (to >= 0 && from != to)
                        {
                            Items.Move(from, to);
                            PersistOrder();
                        }
                    }
                }
                else
                {
                    // Перенос из другой коробки (ссылка, файл не трогаем) — вставка в место сброса.
                    App.Db.MoveItemToBox(itemId, Box.Id);
                    OwnerApp.RefreshBox(sourceBoxId);
                    LoadItems();
                    var moved = Items.FirstOrDefault(i => i.Id == itemId);
                    if (moved != null && hovered != null)
                    {
                        var to = Items.ToList().FindIndex(i => i.Id == hovered.Id);
                        var from = Items.IndexOf(moved);
                        if (to >= 0 && from != to) Items.Move(from, to);
                    }
                    PersistOrder();
                }
            }
            else if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                // Из Проводника/рабочего стола: добавляем только ссылки, файлы не перемещаем.
                var paths = (string[])e.Data.GetData(DataFormats.FileDrop)!;
                var publicDenied = new List<BoxItem>();
                foreach (var path in paths)
                {
                    var (item, denied) = AddItemFromPath(path);
                    if (denied && item != null) publicDenied.Add(item);
                }
                OwnerApp.HandlePublicDesktopItems(publicDenied);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Drop", ex);
        }
        e.Handled = true;
    }

    /// <summary>
    /// Добавляет элемент в коробку. Возвращает элемент и признак «общий ярлык,
    /// скрыть без прав администратора нельзя» — вызывающий собирает такие в пачку
    /// и предлагает перенос одним запросом UAC (App.HandlePublicDesktopItems).
    /// </summary>
    public (BoxItem? Item, bool PublicAccessDenied) AddItemFromPath(string path)
    {
        // Повторное перетаскивание уже добавленного элемента: «доскрываем» его со стола
        // (например, ссылки, добавленные до появления move-логики).
        var existing = Items.FirstOrDefault(i => string.Equals(i.FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            if (!existing.HiddenByApp && DesktopIconService.IsOnDesktop(path))
            {
                switch (DesktopIconService.Hide(path, out var added))
                {
                    case HideResult.Hidden:
                        // recovery-состояние пишем сразу после скрытия; при сбое — компенсация.
                        try
                        {
                            App.Db.SetItemHidden(existing.Id, true, added);
                            existing.HiddenByApp = true;
                            existing.AddedAttributes = added;
                            DesktopIconService.RefreshDesktop();
                        }
                        catch (Exception ex)
                        {
                            Logger.Error("AddItem.SetItemHidden(existing)", ex);
                            DesktopIconService.Restore(path, added); // не оставляем «осиротевший» скрытый файл
                        }
                        break;
                    case HideResult.AccessDenied:
                        return (existing, true);
                }
            }
            return (existing, false);
        }

        var item = AutoSortService.CreateItemFromPath(path, Box.Id);
        item.DisplayOrder = Items.Count;

        // Сначала создаём строку в БД (как НЕ скрытую) — чтобы файл не оказался скрытым
        // без recovery-записи, если вставка упадёт.
        App.Db.InsertItem(item);

        // Затем «переезд»: скрываем иконку на столе (файл физически на месте) и фиксируем биты.
        var publicDenied = false;
        if (DesktopIconService.IsOnDesktop(path))
        {
            switch (DesktopIconService.Hide(path, out var added))
            {
                case HideResult.Hidden:
                    try
                    {
                        App.Db.SetItemHidden(item.Id, true, added);
                        item.HiddenByApp = true;
                        item.AddedAttributes = added;
                        DesktopIconService.RefreshDesktop();
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("AddItem.SetItemHidden", ex);
                        DesktopIconService.Restore(path, added); // компенсация: возвращаем файл на стол
                    }
                    break;
                case HideResult.AccessDenied:
                    publicDenied = true;
                    break;
            }
        }

        item.Icon = IconCache.GetIcon(item.FullPath, item.ItemType == "Folder", IconPixelSize);
        Items.Add(item);
        return (item, publicDenied);
    }

    /// <summary>Записывает текущий порядок элементов коллекции в DisplayOrder.</summary>
    private void PersistOrder()
    {
        for (var i = 0; i < Items.Count; i++)
        {
            Items[i].DisplayOrder = i;
            App.Db.UpdateItemOrder(Items[i].Id, i);
        }
    }

    private void ItemsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        _dragStart = e.GetPosition(null);

    private void ItemsList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        if (GetItemUnderMouse(e.OriginalSource) is not BoxItem item) return;

        var data = new DataObject();
        data.SetData(InternalDragFormat, $"{item.Id}|{Box.Id}");
        if (!item.IsMissing)
            data.SetFileDropList(new System.Collections.Specialized.StringCollection { item.FullPath });

        DragDrop.DoDragDrop(ItemsList, data, DragDropEffects.Move | DragDropEffects.Copy | DragDropEffects.Link);
    }

    private BoxItem? GetItemUnderMouse(object originalSource)
    {
        var dep = originalSource as DependencyObject;
        while (dep != null && dep is not ListBoxItem)
            dep = VisualTreeHelper.GetParent(dep);
        return (dep as ListBoxItem)?.DataContext as BoxItem;
    }

    // ---------- Действия с элементами (раздел 9.3 ТЗ) ----------

    private void ItemsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (GetItemUnderMouse(e.OriginalSource) is BoxItem item) OpenItem(item);
    }

    private void OpenItem(BoxItem item)
    {
        try
        {
            Process.Start(new ProcessStartInfo(item.FullPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Error("OpenItem", ex);
            MessageBox.Show($"Не удалось открыть:\n{item.FullPath}\n\n{ex.Message}",
                "Desktop Organizer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenItemLocation(BoxItem item)
    {
        try
        {
            if (Directory.Exists(item.FullPath))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{item.FullPath}\"") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.FullPath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Error("OpenItemLocation", ex);
        }
    }

    /// <summary>
    /// Убирает элемент из коробки. Если ярлык был скрыт нами — сначала возвращаем его на стол;
    /// при неудаче элемент НЕ удаляется из БД, чтобы не потерять путь из recovery-списка.
    /// </summary>
    private void RemoveItemFromBox(BoxItem item)
    {
        if (item.HiddenByApp)
        {
            var res = DesktopIconService.Restore(item.FullPath, item.AddedAttributes);
            DesktopIconService.RefreshDesktop();
            if (res == RestoreResult.Failed)
            {
                MessageBox.Show(
                    $"Не удалось вернуть ярлык на рабочий стол:\n{item.FullPath}\n\nЭлемент оставлен в коробке.",
                    "Desktop Organizer", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        App.Db.DeleteItem(item.Id);
        Items.Remove(item);
    }

    private void DeleteItemFile(BoxItem item)
    {
        var isReparse = DesktopIconService.IsReparsePoint(item.FullPath);

        // Удаление файла с диска — только с явным подтверждением (разделы 6.2, 18.11 ТЗ).
        var prompt = isReparse
            ? $"Это символьная ссылка / junction. Будет удалена только сама ссылка (цель не затрагивается):\n\n{item.FullPath}"
            : $"Удалить в корзину?\n\n{item.FullPath}";
        var answer = MessageBox.Show(prompt, "Подтверждение удаления",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            // Снимаем наши атрибуты, чтобы в корзине/после удаления не осталось «скрытого+системного».
            if (item.HiddenByApp) DesktopIconService.Restore(item.FullPath, item.AddedAttributes);

            if (isReparse)
            {
                // Reparse-точку удаляем как ссылку, не рекурсивно — иначе можно задеть цель.
                if (Directory.Exists(item.FullPath)) Directory.Delete(item.FullPath, recursive: false);
                else if (File.Exists(item.FullPath)) File.Delete(item.FullPath);
            }
            else if (Directory.Exists(item.FullPath))
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(item.FullPath,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            else if (File.Exists(item.FullPath))
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(item.FullPath,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);

            // Файл удалён — Restore вернёт FileGone, строку удалим корректно.
            App.Db.DeleteItem(item.Id);
            Items.Remove(item);
            DesktopIconService.RefreshDesktop();
            Logger.Log($"Удалено: {item.FullPath}");
        }
        catch (Exception ex)
        {
            Logger.Error("DeleteItemFile", ex);
            MessageBox.Show($"Не удалось удалить файл:\n{ex.Message}", "Desktop Organizer",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RenameItem(BoxItem item)
    {
        var dlg = new InputDialog("Переименовать элемент", "Отображаемое имя:", item.Name);
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.Value))
        {
            item.Name = dlg.Value.Trim();
            App.Db.RenameItem(item.Id, item.Name);
            LoadItems();
        }
    }

    // ---------- Контекстные меню ----------

    private void Window_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        e.Handled = true;
        var item = GetItemUnderMouse(e.OriginalSource);
        var menu = item != null ? BuildItemMenu(item) : BuildBoxMenu();
        menu.PlacementTarget = this;
        menu.IsOpen = true;
    }

    private ContextMenu BuildItemMenu(BoxItem item)
    {
        var menu = new ContextMenu();
        AddMenuItem(menu, "Открыть", () => OpenItem(item));
        AddMenuItem(menu, "Открыть расположение", () => OpenItemLocation(item));
        menu.Items.Add(new Separator());
        AddMenuItem(menu, "Переименовать", () => RenameItem(item));
        AddMenuItem(menu, "Копировать путь", () => Clipboard.SetText(item.FullPath));

        var moveMenu = new MenuItem { Header = "Переместить в коробку" };
        foreach (var target in OwnerApp.GetBoxWindows().Where(w => w.Box.Id != Box.Id))
        {
            var t = target;
            var mi = new MenuItem { Header = t.Box.Name };
            mi.Click += (_, _) =>
            {
                App.Db.MoveItemToBox(item.Id, t.Box.Id);
                Items.Remove(item);
                t.LoadItems();
            };
            moveMenu.Items.Add(mi);
        }
        if (moveMenu.Items.Count > 0) menu.Items.Add(moveMenu);

        menu.Items.Add(new Separator());
        AddMenuItem(menu, item.HiddenByApp ? "Вернуть на рабочий стол" : "Убрать из коробки",
            () => RemoveItemFromBox(item));
        AddMenuItem(menu, "Удалить файл (в корзину)…", () => DeleteItemFile(item));
        return menu;
    }

    private ContextMenu BuildBoxMenu()
    {
        var menu = new ContextMenu();
        AddMenuItem(menu, "Настройки коробки…", OpenSettings);
        AddMenuItem(menu, "Переименовать коробку", RenameBox);
        AddMenuItem(menu, Box.IsLocked ? "Открепить" : "Закрепить позицию", () => SetLocked(!Box.IsLocked));
        AddMenuItem(menu, Box.IsCollapsed ? "Развернуть" : "Свернуть", () => ApplyCollapsed(!Box.IsCollapsed));
        menu.Items.Add(new Separator());

        var sizeMenu = new MenuItem { Header = "Размер значков" };
        foreach (var (label, size) in new[] { ("Мелкие", 24), ("Обычные", 32), ("Крупные", 48) })
        {
            var s = size;
            var mi = new MenuItem { Header = label, IsCheckable = true, IsChecked = Box.IconSize == s };
            mi.Click += (_, _) => SetIconSize(s);
            sizeMenu.Items.Add(mi);
        }
        menu.Items.Add(sizeMenu);

        AddMenuItem(menu, "Сортировать по имени", SortByName);
        AddMenuItem(menu, "Очистить коробку…", ClearBox);
        menu.Items.Add(new Separator());
        AddMenuItem(menu, "Новая коробка", () => OwnerApp.CreateNewBoxInteractive());
        AddMenuItem(menu, "Удалить коробку…", () => OwnerApp.DeleteBoxInteractive(this));
        return menu;
    }

    private static void AddMenuItem(ContextMenu menu, string header, Action action)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => action();
        menu.Items.Add(mi);
    }

    private void RenameBox()
    {
        var dlg = new InputDialog("Переименовать коробку", "Название:", Box.Name);
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.Value))
        {
            Box.Name = dlg.Value.Trim();
            App.Db.UpdateBox(Box);
            ApplyAppearance();
        }
    }

    private void SortByName()
    {
        var sorted = Items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            sorted[i].DisplayOrder = i;
            App.Db.UpdateItemOrder(sorted[i].Id, i);
        }
        Items.Clear();
        foreach (var it in sorted) Items.Add(it);
    }

    private void ClearBox()
    {
        var answer = MessageBox.Show(
            $"Убрать все элементы из коробки «{Box.Name}»?\n\nФайлы на диске затронуты не будут, скрытые ярлыки вернутся на рабочий стол.",
            "Очистка коробки", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        var kept = 0;
        foreach (var it in Items.ToList())
        {
            if (it.HiddenByApp &&
                DesktopIconService.Restore(it.FullPath, it.AddedAttributes) == RestoreResult.Failed)
            {
                kept++; // не удалось вернуть на стол — оставляем в коробке
                continue;
            }
            App.Db.DeleteItem(it.Id);
            Items.Remove(it);
        }
        DesktopIconService.RefreshDesktop();

        if (kept > 0)
            MessageBox.Show(
                $"{kept} элемент(ов) не удалось вернуть на рабочий стол — они оставлены в коробке. Подробности в логе.",
                "Очистка коробки", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
