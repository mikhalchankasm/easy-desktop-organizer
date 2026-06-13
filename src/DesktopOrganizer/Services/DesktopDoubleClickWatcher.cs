using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace DesktopOrganizer.Services;

/// <summary>
/// Отслеживает двойной клик по пустой области рабочего стола (фирменный жест iTop,
/// раздел 4.3/5.6 ТЗ) через низкоуровневый хук мыши WH_MOUSE_LL.
/// Срабатывает только если под курсором именно область иконок стола (SysListView32
/// внутри Progman/WorkerW) и на этом месте нет иконки — иначе это открытие иконки
/// или клик по коробке/окну, и жест игнорируется.
/// </summary>
public sealed class DesktopDoubleClickWatcher : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int LVM_FIRST = 0x1000;
    private const int LVM_HITTEST = LVM_FIRST + 18;
    private const uint GA_ROOT = 2;
    private const int SM_CXDOUBLECLK = 36, SM_CYDOUBLECLK = 37;
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private const uint PROCESS_VM_OPERATION = 0x0008, PROCESS_VM_WRITE = 0x0020, PROCESS_VM_READ = 0x0010;
    private const uint MEM_COMMIT = 0x1000, MEM_RESERVE = 0x2000, MEM_RELEASE = 0x8000;
    private const uint PAGE_READWRITE = 0x04;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LVHITTESTINFO
    {
        public POINT pt;
        public uint flags;
        public int iItem;
        public int iSubItem;
        public int iGroup;
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr hMod, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] private static extern uint GetDoubleClickTime();
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int idx);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr h, int msg, IntPtr w, IntPtr l, uint flags, uint timeoutMs, out IntPtr result);

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr VirtualAllocEx(IntPtr h, IntPtr addr, IntPtr size, uint type, uint protect);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualFreeEx(IntPtr h, IntPtr addr, IntPtr size, uint type);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, IntPtr buf, IntPtr size, out IntPtr written);

    private readonly HookProc _proc;
    private readonly Dispatcher _dispatcher;
    private readonly Action _onDesktopDoubleClick;
    private IntPtr _hook;
    private uint _lastDownTime;
    private int _lastX, _lastY;

    public DesktopDoubleClickWatcher(Dispatcher dispatcher, Action onDesktopDoubleClick)
    {
        _dispatcher = dispatcher;
        _onDesktopDoubleClick = onDesktopDoubleClick;
        _proc = HookCallback;
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, IntPtr.Zero, 0);
        if (_hook == IntPtr.Zero) Logger.Log("DesktopDoubleClickWatcher: не удалось установить хук мыши");
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && wParam.ToInt32() == WM_LBUTTONDOWN)
        {
            var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            var dt = data.time - _lastDownTime;
            var isDouble = dt <= GetDoubleClickTime()
                           && Math.Abs(data.pt.X - _lastX) <= GetSystemMetrics(SM_CXDOUBLECLK)
                           && Math.Abs(data.pt.Y - _lastY) <= GetSystemMetrics(SM_CYDOUBLECLK);
            if (isDouble)
            {
                _lastDownTime = 0; // не реагировать на тройной клик
                var pt = data.pt;
                // Тяжёлую проверку (cross-process к Explorer) выполняем ВНЕ hook через dispatcher,
                // иначе при зависшем Explorer подвиснет глобальный ввод и Windows снимет хук.
                _dispatcher.BeginInvoke(() => { if (IsEmptyDesktop(pt)) _onDesktopDoubleClick(); });
            }
            else
            {
                _lastDownTime = data.time;
                _lastX = data.pt.X;
                _lastY = data.pt.Y;
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private static bool IsEmptyDesktop(POINT screenPt)
    {
        try
        {
            var h = WindowFromPoint(screenPt);
            if (h == IntPtr.Zero) return false;
            if (ClassName(h) != "SysListView32") return false; // не область иконок стола
            var rootCls = ClassName(GetAncestor(h, GA_ROOT));
            if (rootCls != "Progman" && rootCls != "WorkerW") return false; // окно Проводника, а не стол

            // Fail-closed: жест срабатывает, только если ТОЧНО установлено, что иконки нет.
            // Если hit-test недоступен (null) — не выполняем жест.
            return IconUnderCursor(h, screenPt) == false;
        }
        catch (Exception ex)
        {
            Logger.Error("IsEmptyDesktop", ex);
            return false;
        }
    }

    /// <summary>
    /// Иконка стола под курсором (cross-process LVM_HITTEST в explorer.exe):
    /// true — иконка есть, false — пусто, null — определить не удалось.
    /// </summary>
    private static bool? IconUnderCursor(IntPtr listView, POINT screenPt)
    {
        var clientPt = screenPt;
        if (!ScreenToClient(listView, ref clientPt)) return null;

        GetWindowThreadProcessId(listView, out var pid);
        var hProc = OpenProcess(PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE, false, pid);
        if (hProc == IntPtr.Zero)
        {
            Logger.Log($"IconUnderCursor: OpenProcess(explorer) не удался err={Marshal.GetLastWin32Error()}");
            return null;
        }

        var size = Marshal.SizeOf<LVHITTESTINFO>();
        var remote = VirtualAllocEx(hProc, IntPtr.Zero, (IntPtr)size, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        var local = Marshal.AllocHGlobal(size);
        try
        {
            if (remote == IntPtr.Zero)
            {
                Logger.Log($"IconUnderCursor: VirtualAllocEx не удался err={Marshal.GetLastWin32Error()}");
                return null;
            }
            Marshal.StructureToPtr(new LVHITTESTINFO { pt = clientPt }, local, false);
            if (!WriteProcessMemory(hProc, remote, local, (IntPtr)size, out var written) || written != (IntPtr)size)
            {
                Logger.Log($"IconUnderCursor: WriteProcessMemory не удался err={Marshal.GetLastWin32Error()}");
                return null;
            }
            // С таймаутом, чтобы зависший Explorer не блокировал нас.
            if (SendMessageTimeout(listView, LVM_HITTEST, IntPtr.Zero, remote, SMTO_ABORTIFHUNG, 200, out var lr) == IntPtr.Zero)
            {
                Logger.Log("IconUnderCursor: Explorer не ответил на LVM_HITTEST");
                return null;
            }
            return lr.ToInt32() >= 0;
        }
        finally
        {
            if (remote != IntPtr.Zero) VirtualFreeEx(hProc, remote, IntPtr.Zero, MEM_RELEASE);
            Marshal.FreeHGlobal(local);
            CloseHandle(hProc);
        }
    }

    private static string ClassName(IntPtr h)
    {
        var sb = new StringBuilder(64);
        GetClassName(h, sb, 64);
        return sb.ToString();
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
