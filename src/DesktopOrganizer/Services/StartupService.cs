using Microsoft.Win32;

namespace DesktopOrganizer.Services;

/// <summary>Автозапуск через HKCU\...\Run (раздел 5.10 ТЗ, рекомендуемый вариант для MVP).</summary>
public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DesktopOrganizer";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) != null;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            var exe = Environment.ProcessPath;
            if (exe != null) key.SetValue(ValueName, $"\"{exe}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        Logger.Log($"Автозапуск: {(enabled ? "включен" : "выключен")}");
    }
}
