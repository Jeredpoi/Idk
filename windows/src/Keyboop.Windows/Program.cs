using Keyboop.Windows.Diagnostics;

namespace Keyboop.Windows;

internal static class Program
{
    /// <summary>
    /// Единственный экземпляр. Два процесса поставили бы два перехватчика на один хоткей, и
    /// диктовка запускалась бы дважды с одного нажатия — с двумя записями одного микрофона.
    /// </summary>
    private static Mutex? _single;

    [STAThread]
    private static void Main()
    {
        _single = new Mutex(initiallyOwned: true, "Keyboop.Windows.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            return;
        }

        // ⚠️ ЛОВУШКИ СТАВИМ ДО ВСЕГО ОСТАЛЬНОГО. Без них любое непойманное исключение закрывает
        // программу молча: ни окна, ни строчки в логе — человек видит только исчезнувший значок
        // в трее. Ровно так и выглядел первый запуск на живой машине.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Report("необработанное исключение", e.ExceptionObject as Exception);

        Application.ThreadException += (_, e) =>
            Report("исключение в потоке интерфейса", e.Exception);

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        ApplicationConfiguration.Initialize();

        Log.Write("=== Keyboop запущен ===");

        try
        {
            Application.Run(new TrayApp());
        }
        catch (Exception ex)
        {
            Report("сбой при работе", ex);
            throw;
        }

        Log.Write("=== Keyboop завершён ===");

        _single.ReleaseMutex();
        _single.Dispose();
    }

    /// <summary>
    /// Записать аварию так, чтобы её можно было разобрать по чужому компьютеру.
    ///
    /// Пишем ТИП, СООБЩЕНИЕ И СТЕК целиком: без стека строка «NullReferenceException» не говорит
    /// ничего — таких мест в программе сотни. Содержимого набранного или продиктованного в стеке
    /// не бывает, так что принцип «в лог не попадает ни одного слова человека» не нарушается.
    /// </summary>
    private static void Report(string what, Exception? ex)
    {
        try
        {
            Log.Write($"АВАРИЯ ({what}): {ex}");
        }
        catch
        {
            // Если не пишется даже лог — показать сообщение всё равно важнее.
        }

        try
        {
            MessageBox.Show(
                $"Keyboop наткнулся на ошибку и, возможно, работает неправильно.\n\n"
                + $"{ex?.GetType().Name}: {ex?.Message}\n\n"
                + $"Подробности записаны в {Log.FilePath}",
                "Keyboop",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch
        {
            // Окно могло не показаться (сеанс без рабочего стола). Лог уже написан.
        }
    }
}
