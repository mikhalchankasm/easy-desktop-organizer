using System.Runtime.InteropServices;
using System.Text;

namespace DesktopOrganizer.Services;

/// <summary>
/// Привязка окон-коробок к рабочему столу. Progman/WorkerW назначается ВЛАДЕЛЬЦЕМ
/// (owner, не parent!) окна: шелл не скрывает (cloak) такие окна при Win+D /
/// «Показать рабочий стол», при этом окно остается top-level и WPF рендерится нормально.
/// (SetParent для WPF недопустим — layered-окна перестают отрисовываться.)
/// </summary>
public static class DesktopHost
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? cls, string? title);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumProc cb, IntPtr l);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr h, StringBuilder sb, int max);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr h, int index, IntPtr value);

    private delegate bool EnumProc(IntPtr h, IntPtr l);

    private const int GWLP_HWNDPARENT = -8;

    private static IntPtr _host;

    /// <summary>
    /// Окно, содержащее SHELLDLL_DefView (иконки рабочего стола):
    /// классически Progman, на новых сборках Windows 11 — один из WorkerW.
    /// </summary>
    public static IntPtr FindDesktopHost()
    {
        if (_host != IntPtr.Zero) return _host;

        var progman = FindWindow("Progman", null);
        if (progman != IntPtr.Zero &&
            FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
            return _host = progman;

        var found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            var sb = new StringBuilder(64);
            GetClassName(h, sb, 64);
            if (sb.ToString() == "WorkerW" &&
                FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
            {
                found = h;
                return false;
            }
            return true;
        }, IntPtr.Zero);

        return _host = found;
    }

    /// <summary>Делает рабочий стол владельцем окна. false — стол не найден.</summary>
    public static bool SetDesktopAsOwner(IntPtr hwnd)
    {
        var host = FindDesktopHost();
        if (host == IntPtr.Zero)
        {
            Logger.Log("DesktopHost: окно рабочего стола не найдено");
            return false;
        }
        SetWindowLongPtr(hwnd, GWLP_HWNDPARENT, host);
        return true;
    }
}
