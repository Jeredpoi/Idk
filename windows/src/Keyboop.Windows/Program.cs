using Keyboop.Windows.Diagnostics;

namespace Keyboop.Windows;

internal static class Program
{
    /// <summary>
    /// Единственный экземпляр. Два процесса поставили бы два перехватчика на один хоткей, и
    /// диктовка запускалась бы дважды с одного нажатия — с двумя записями одного микрофона.
    /// </summary>
    private static Mutex? _single;

    /// <summary>Ключ, которым запускается отдельное окно лога.</summary>
    internal const string LogViewerArgument = "--log";

    [STAThread]
    private static void Main(string[] args)
    {
        // ⚠️ РЕЖИМ ПРОСМОТРЩИКА — ДО ВСЕГО ОСТАЛЬНОГО, И ДО ЗАМКА ЕДИНСТВЕННОГО ЭКЗЕМПЛЯРА.
        // Это тот же файл программы, запущенный вторым процессом ради одного окна: ни
        // перехватчика, ни звука, ни распознавания он не поднимает. Смысл в том, чтобы окно
        // пережило смерть основного процесса — иначе лог исчезает ровно тогда, когда на него
        // смотрят. Замок трогать нельзя: он на то и единственный экземпляр.
        if (args.Contains(LogViewerArgument, StringComparer.OrdinalIgnoreCase))
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new Ui.LogWindow(standalone: true));
            return;
        }

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

        // Метка сеанса. Если прошлый запуск не дошёл до штатного выхода, здесь появится путь
        // к отчёту, собранному задним числом.
        var previousCrash = CrashReport.BeginSession();

        // Поворот — только здесь: в процессе окна лога это переименовало бы файл под пишущей
        // рукой основного.
        Log.Rotate();
        Log.Write("=== Keyboop запущен ===");

        if (previousCrash is not null)
        {
            // ⚠️ После падения САМИ включаем подробный лог. Человек не обязан догадываться, что
            // перед повторением аварии надо было что-то настроить, а без следа обработки отчёт
            // о нативном падении говорит только «упало», не говоря где.
            Log.Verbose = true;
            Log.Write("подробный режим включён автоматически: прошлый сеанс завершился аварийно");
            OfferReport(previousCrash);
        }

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
        CrashReport.EndSession();

        _single.ReleaseMutex();
        _single.Dispose();
    }

    /// <summary>
    /// Сказать про отчёт и предложить открыть папку с ним. Спрашиваем, а не открываем сами:
    /// человек мог запускать программу по делу, и папка поверх его работы — не помощь.
    /// </summary>
    private static void OfferReport(string path)
    {
        try
        {
            var answer = MessageBox.Show(
                "В прошлый раз Keyboop завершился аварийно.\n\n"
                + "Отчёт со всеми подробностями сохранён:\n"
                + path + "\n\n"
                + "Открыть папку с отчётами?",
                "Keyboop",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (answer == DialogResult.Yes)
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(CrashReport.Directory)
                    {
                        UseShellExecute = true,
                    });
            }
        }
        catch (Exception)
        {
            // Не показалось — путь всё равно записан в лог.
        }
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
        string? report = null;

        try
        {
            Log.Write($"АВАРИЯ ({what}): {ex}");

            // Дождаться, пока фоновый писатель допишет: иначе отчёт получит лог без последних
            // строк — то есть без самого нужного.
            Log.Flush();
            report = CrashReport.Save(what, ex);
        }
        catch
        {
            // Если не пишется даже лог — показать сообщение всё равно важнее.
        }

        try
        {
            MessageBox.Show(
                "Keyboop наткнулся на ошибку и, возможно, работает неправильно.\n\n"
                + $"{ex?.GetType().Name}: {ex?.Message}\n\n"
                + "Отчёт: " + (report ?? Log.FilePath),
                "Keyboop",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch
        {
            // Окно могло не показаться (сеанс без рабочего стола). Отчёт уже написан.
        }
    }
}
