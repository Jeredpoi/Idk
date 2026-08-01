using Microsoft.Win32;
using Keyboop.Windows.Diagnostics;

namespace Keyboop.Windows;

/// <summary>
/// Запуск вместе с системой.
///
/// Пишем в ветку ТЕКУЩЕГО пользователя (HKCU), а не в общесистемную: для HKLM нужны права
/// администратора, а программа сознательно работает без них — см. app.manifest. Заодно это
/// правильно по смыслу: автозапуск это выбор человека, а не машины.
/// </summary>
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Keyboop";

    internal static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is not null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return false;
            }
        }
    }

    internal static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);

            if (key is null)
            {
                return;
            }

            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                Log.Write("автозапуск: выключен");
                return;
            }

            var path = Environment.ProcessPath;
            if (string.IsNullOrEmpty(path))
            {
                Log.Write("автозапуск: не удалось определить путь к программе");
                return;
            }

            // Путь в кавычках: без них пробел в имени папки («C:\Program Files\…») превращает
            // строку запуска в две, и Windows молча не найдёт программу.
            key.SetValue(ValueName, $"\"{path}\"");
            Log.Write("автозапуск: включён");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Реестр могут закрыть политикой домена или антивирусом. Это не повод падать:
            // программа продолжает работать, просто не поднимется сама после перезагрузки.
            Log.Write($"автозапуск: не удалось изменить ({ex.GetType().Name})");
        }
    }
}
