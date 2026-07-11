using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace DesktopOrganizer.Services;

/// <summary>
/// Глобальные горячие клавиши через RegisterHotKey на скрытом message-окне (раздел 10 ТЗ).
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    private const int WM_HOTKEY = 0x0312;

    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _actions = new();
    private int _nextId = 1;

    // Explorer рассылает это сообщение всем top-level окнам при своём перезапуске (TaskbarCreated).
    // Регистрируем через RegisterWindowMessage: числовой идентификатор не фиксирован в Win32.
    private readonly uint _taskbarCreatedMsg;

    /// <summary>
    /// Вызывается, когда Explorer перезапустился (TaskbarCreated) — окна-коробки, владельцем
    /// которых был Progman/WorkerW, уничтожаются вместе с ним и нуждаются в перепривязке владельца.
    /// </summary>
    public event Action? TaskbarCreated;

    public HotkeyManager()
    {
        _taskbarCreatedMsg = RegisterWindowMessage("TaskbarCreated");
        var p = new HwndSourceParameters("DesktopOrganizerHotkeyWindow")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0, // невидимое окно
        };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
    }

    /// <summary>Регистрирует комбинацию; возвращает false при конфликте с другим приложением.</summary>
    public bool Register(ModifierKeys mods, Key key, Action action)
    {
        var id = _nextId++;
        uint fsMods = 0;
        if (mods.HasFlag(ModifierKeys.Alt)) fsMods |= 0x1;
        if (mods.HasFlag(ModifierKeys.Control)) fsMods |= 0x2;
        if (mods.HasFlag(ModifierKeys.Shift)) fsMods |= 0x4;
        if (mods.HasFlag(ModifierKeys.Windows)) fsMods |= 0x8;

        var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (!RegisterHotKey(_source.Handle, id, fsMods, vk))
        {
            Logger.Log($"HotkeyManager: не удалось зарегистрировать {mods}+{key} (занята другим приложением)");
            return false;
        }
        _actions[id] = action;
        return true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // TaskbarCreated: RegisterWindowMessage возвращает динамический идентификатор (если он
        // ненулевой — регистрация удалась). При совпадении оповещаем подписчиков о рестарте Explorer.
        if (_taskbarCreatedMsg != 0 && msg == _taskbarCreatedMsg)
        {
            try { TaskbarCreated?.Invoke(); }
            catch (Exception ex) { Logger.Error("TaskbarCreated", ex); }
            handled = true;
        }
        else if (msg == WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            try { action(); }
            catch (Exception ex) { Logger.Error("Hotkey", ex); }
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var id in _actions.Keys) UnregisterHotKey(_source.Handle, id);
        _actions.Clear();
        _source.Dispose();
    }
}
