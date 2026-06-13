using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using DesktopOrganizer.Data;
using DesktopOrganizer.Models;
using DesktopOrganizer.Services;
using DesktopOrganizer.UI;
using WinForms = System.Windows.Forms;

namespace DesktopOrganizer;

/// <summary>
/// Точка входа: главного окна нет (раздел 9.1 ТЗ) — приложение живет в трее,
/// коробки отображаются как отдельные окна поверх рабочего стола.
/// </summary>
public partial class App : Application
{
    public static Db Db { get; private set; } = null!;

    private static Mutex? _singleInstanceMutex;

    private readonly Dictionary<long, BoxWindow> _boxWindows = new();
    private WinForms.NotifyIcon? _tray;
    private HotkeyManager? _hotkeys;
    private SearchWindow? _searchWindow;
    private AlignmentOverlay? _overlay;
    private DesktopDoubleClickWatcher? _desktopWatcher;
    private bool _boxesHidden;

    /// <summary>Оверлей направляющих линий выравнивания (общий для всех коробок).</summary>
    public AlignmentOverlay Overlay => _overlay ??= new AlignmentOverlay();

    protected override void OnStartup(StartupEventArgs e)
    {
        // Единственный экземпляр.
        _singleInstanceMutex = new Mutex(true, "DesktopOrganizer_SingleInstance", out var isNew);
        if (!isNew)
        {
            MessageBox.Show("Desktop Organizer уже запущен (значок в системном трее).",
                "Desktop Organizer", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);
        Logger.Log("=== Запуск приложения ===");

        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Logger.Error("AppDomain", (args.ExceptionObject as Exception) ?? new Exception("unknown"));

        Db = new Db();

        // Элементы, лежащие в коробках, снова скрываем с рабочего стола
        // (при прошлом выходе их иконки были возвращены на стол).
        var hiddenPaths = Db.GetHiddenItemPaths();
        foreach (var path in hiddenPaths) DesktopIconService.Hide(path);
        if (hiddenPaths.Count > 0) DesktopIconService.RefreshDesktop();

        LoadAndShowBoxes();
        InitTray();
        InitHotkeys();

        // Двойной клик по пустому рабочему столу — скрыть/показать коробки (раздел 4.3 ТЗ).
        _desktopWatcher = new DesktopDoubleClickWatcher(Dispatcher, ToggleBoxesVisibility);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Logger.Error("UI", e.Exception);
        e.Handled = true; // не роняем приложение из-за исключения UI (раздел 6.2)
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Db может быть null, если это второй экземпляр и мы вышли из OnStartup досрочно.
        if (Db != null)
        {
            foreach (var w in _boxWindows.Values) w.PersistNow();

            // При выходе возвращаем все скрытые ярлыки на рабочий стол —
            // без запущенного приложения пользователь не должен терять доступ к файлам.
            var hiddenPaths = Db.GetHiddenItemPaths();
            foreach (var path in hiddenPaths) DesktopIconService.Restore(path);
            if (hiddenPaths.Count > 0) DesktopIconService.RefreshDesktop();
        }
        _hotkeys?.Dispose();
        _desktopWatcher?.Dispose();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        Db?.Dispose();
        Logger.Log("=== Завершение приложения ===");
        base.OnExit(e);
    }

    // ---------- Коробки ----------

    public IReadOnlyCollection<BoxWindow> GetBoxWindows() => _boxWindows.Values;

    private void LoadAndShowBoxes()
    {
        var boxes = Db.GetBoxes();
        if (boxes.Count == 0)
        {
            // Первый запуск — создаем приветственную коробку.
            var welcome = new Box { Name = "Моя первая коробка", X = 120, Y = 120 };
            Db.InsertBox(welcome);
            boxes.Add(welcome);
        }

        foreach (var box in boxes)
        {
            ClampToVirtualScreen(box);
            OpenBoxWindow(box);
        }
        Logger.Log($"Загружено коробок: {boxes.Count}");
    }

    private BoxWindow OpenBoxWindow(Box box)
    {
        var w = new BoxWindow(box);
        _boxWindows[box.Id] = w;
        if (!box.IsHidden && !_boxesHidden) w.Show();
        return w;
    }

    /// <summary>Не допускаем появления коробок за пределами видимой области (раздел 5.9 ТЗ).</summary>
    private static void ClampToVirtualScreen(Box box)
    {
        var left = SystemParameters.VirtualScreenLeft;
        var top = SystemParameters.VirtualScreenTop;
        var right = left + SystemParameters.VirtualScreenWidth;
        var bottom = top + SystemParameters.VirtualScreenHeight;

        if (box.X + box.Width < left + 60) box.X = left + 20;
        if (box.X > right - 60) box.X = right - box.Width - 20;
        if (box.Y < top) box.Y = top + 20;
        if (box.Y > bottom - 60) box.Y = bottom - box.Height - 20;
    }

    public void CreateNewBoxInteractive()
    {
        var dlg = new InputDialog("Новая коробка", "Название коробки:", $"Коробка {_boxWindows.Count + 1}");
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.Value)) return;
        CreateBox(dlg.Value.Trim());
    }

    public BoxWindow CreateBox(string name)
    {
        var offset = (_boxWindows.Count % 8) * 48;
        var box = new Box
        {
            Name = name,
            X = SystemParameters.WorkArea.Left + 120 + offset,
            Y = SystemParameters.WorkArea.Top + 120 + offset,
        };
        Db.InsertBox(box);
        Logger.Log($"Создана коробка «{name}» (Id={box.Id})");
        return OpenBoxWindow(box);
    }

    public void DeleteBoxInteractive(BoxWindow w)
    {
        var answer = MessageBox.Show(
            $"Удалить коробку «{w.Box.Name}»?\n\nФайлы на диске затронуты не будут — скрытые ярлыки вернутся на рабочий стол.",
            "Удаление коробки", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        foreach (var it in w.Items.Where(i => i.HiddenByApp))
            DesktopIconService.Restore(it.FullPath);
        DesktopIconService.RefreshDesktop();

        Db.DeleteBox(w.Box.Id);
        _boxWindows.Remove(w.Box.Id);
        w.Close();
        Logger.Log($"Удалена коробка «{w.Box.Name}»");
    }

    public void RefreshBox(long boxId)
    {
        if (_boxWindows.TryGetValue(boxId, out var w)) w.LoadItems();
    }

    /// <summary>Режим «чистый рабочий стол»: скрыть/показать все коробки (раздел 5.6 ТЗ).</summary>
    public void ToggleBoxesVisibility()
    {
        _boxesHidden = !_boxesHidden;
        foreach (var w in _boxWindows.Values)
        {
            if (_boxesHidden) w.Hide();
            else if (!w.Box.IsHidden) w.Show();
        }
        Logger.Log(_boxesHidden ? "Коробки скрыты" : "Коробки показаны");
    }

    // ---------- Поиск ----------

    public void ShowSearch()
    {
        if (_searchWindow is { IsLoaded: true })
        {
            _searchWindow.Activate();
            return;
        }
        _searchWindow = new SearchWindow();
        _searchWindow.Closed += (_, _) => _searchWindow = null;
        _searchWindow.Show();
        _searchWindow.Activate();
    }

    /// <summary>
    /// Общие ярлыки (Public Desktop) нельзя скрыть без прав администратора.
    /// Предлагаем перенести их на личный рабочий стол (один запрос UAC на пачку),
    /// после чего они скрываются обычным способом.
    /// </summary>
    public void HandlePublicDesktopItems(List<BoxItem> denied)
    {
        if (denied.Count == 0) return;

        var names = string.Join(", ", denied.Select(d => $"«{d.Name}»"));
        var answer = MessageBox.Show(
            $"Ярлыки {names} лежат на общем рабочем столе (для всех пользователей компьютера) — " +
            "скрыть их оттуда без прав администратора нельзя.\n\n" +
            "Перенести их на ваш личный рабочий стол и спрятать в коробку?\n" +
            "(потребуется одно подтверждение прав администратора)\n\n" +
            "При отказе ярлыки останутся в коробке как ссылки и будут видны на столе.",
            "Общие ярлыки", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        var migrated = DesktopIconService.MigratePublicItemsToUserDesktop(denied);
        foreach (var it in migrated)
        {
            Db.UpdateItemPath(it.Id, it.FullPath);
            if (DesktopIconService.Hide(it.FullPath) == HideResult.Hidden)
            {
                it.HiddenByApp = true;
                Db.SetItemHiddenFlag(it.Id, true);
            }
            RefreshBox(it.BoxId);
        }
        DesktopIconService.RefreshDesktop();

        if (migrated.Count < denied.Count)
            MessageBox.Show(
                $"Перенесено {migrated.Count} из {denied.Count} ярлыков. Подробности в логе.",
                "Общие ярлыки", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    // ---------- Автоорганизация ----------

    public void RunAutoSort()
    {
        try
        {
            var plan = AutoSortService.BuildPlan(Db);
            var total = plan.Values.Sum(v => v.Count);
            if (total == 0)
            {
                MessageBox.Show("Новых элементов на рабочем столе не найдено.",
                    "Автоорганизация", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var summary = new StringBuilder();
            summary.AppendLine($"Найдено элементов: {total}. Будут добавлены ссылки в коробки:");
            summary.AppendLine();
            foreach (var (cat, paths) in plan.OrderByDescending(p => p.Value.Count))
                summary.AppendLine($"  • {cat}: {paths.Count}");
            summary.AppendLine();
            summary.AppendLine("Ярлыки исчезнут с рабочего стола и появятся в коробках.");
            summary.AppendLine("Файлы физически НЕ перемещаются; при выходе из программы всё вернется на стол.");

            // Подтверждение перед массовой операцией (раздел 6.2 ТЗ).
            var answer = MessageBox.Show(summary.ToString(), "Автоорганизация",
                MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK) return;

            var col = 0;
            var publicDenied = new List<BoxItem>();
            foreach (var (category, paths) in plan)
            {
                var window = _boxWindows.Values.FirstOrDefault(w =>
                    w.Box.Name.Equals(category, StringComparison.OrdinalIgnoreCase));
                if (window == null)
                {
                    window = CreateBox(category);
                    window.Box.X = SystemParameters.WorkArea.Left + 40 + (col % 4) * 340;
                    window.Box.Y = SystemParameters.WorkArea.Top + 40 + (col / 4) * 270;
                    window.Left = window.Box.X;
                    window.Top = window.Box.Y;
                    col++;
                }
                foreach (var path in paths)
                {
                    var (item, deniedFlag) = window.AddItemFromPath(path);
                    if (deniedFlag && item != null) publicDenied.Add(item);
                }
                window.PersistNow();
            }
            Logger.Log($"Автоорганизация: добавлено {total} элементов в {plan.Count} коробок");
            HandlePublicDesktopItems(publicDenied);
        }
        catch (Exception ex)
        {
            Logger.Error("AutoSort", ex);
            MessageBox.Show($"Ошибка автоорганизации:\n{ex.Message}", "Desktop Organizer",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---------- Трей (раздел 5.11 ТЗ) ----------

    private void InitTray()
    {
        System.Drawing.Icon trayIcon;
        try
        {
            trayIcon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!)
                       ?? System.Drawing.SystemIcons.Application;
        }
        catch
        {
            trayIcon = System.Drawing.SystemIcons.Application;
        }

        _tray = new WinForms.NotifyIcon
        {
            Icon = trayIcon,
            Text = "Desktop Organizer",
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ToggleBoxesVisibility);

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Показать/скрыть коробки\tCtrl+Alt+D", null, (_, _) => Dispatcher.Invoke(ToggleBoxesVisibility));
        menu.Items.Add("Поиск\tCtrl+Space", null, (_, _) => Dispatcher.Invoke(ShowSearch));
        menu.Items.Add("Новая коробка\tCtrl+Alt+N", null, (_, _) => Dispatcher.Invoke(CreateNewBoxInteractive));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Автоорганизация рабочего стола…", null, (_, _) => Dispatcher.Invoke(RunAutoSort));
        menu.Items.Add(new WinForms.ToolStripSeparator());

        var autostart = new WinForms.ToolStripMenuItem("Автозапуск с Windows")
        {
            Checked = StartupService.IsEnabled(),
            CheckOnClick = true,
        };
        autostart.CheckedChanged += (_, _) =>
        {
            try { StartupService.SetEnabled(autostart.Checked); }
            catch (Exception ex) { Logger.Error("Autostart", ex); }
        };
        menu.Items.Add(autostart);

        menu.Items.Add("Открыть папку логов", null, (_, _) =>
        {
            try
            {
                System.IO.Directory.CreateDirectory(Logger.LogDir);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{Logger.LogDir}\"") { UseShellExecute = true });
            }
            catch (Exception ex) { Logger.Error("OpenLogs", ex); }
        });

        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => Dispatcher.Invoke(Shutdown));
        _tray.ContextMenuStrip = menu;
    }

    // ---------- Горячие клавиши (раздел 10 ТЗ) ----------

    private void InitHotkeys()
    {
        _hotkeys = new HotkeyManager();
        var ok1 = _hotkeys.Register(ModifierKeys.Control | ModifierKeys.Alt, Key.D, ToggleBoxesVisibility);
        var ok2 = _hotkeys.Register(ModifierKeys.Control, Key.Space, ShowSearch);
        var ok3 = _hotkeys.Register(ModifierKeys.Control | ModifierKeys.Alt, Key.N, CreateNewBoxInteractive);
        if (!ok1 || !ok2 || !ok3)
            Logger.Log("Часть горячих клавиш не зарегистрирована (конфликт с другими приложениями)");
    }
}
