using System.Runtime.InteropServices;
using System.Text;

namespace Keyboop.Windows.Diagnostics;

/// <summary>
/// Отчёт об аварии — отдельный файл на каждое падение, как это делают игры.
///
/// ⚠️ ГЛАВНАЯ ТОНКОСТЬ, РАДИ КОТОРОЙ ЭТОТ КЛАСС УСТРОЕН ИМЕННО ТАК. Часть падений в программе с
/// вызовами Win32 — НЕ управляемые исключения, а нарушения доступа в чужом коде. Такие .NET не
/// перехватывает в принципе: процесс исчезает мгновенно, и никакой обработчик не успевает
/// сработать. Отчёт, который умеет писаться только «в момент аварии», на них бесполезен — а это
/// ровно тот случай, который сейчас и надо поймать.
///
/// Поэтому здесь ДВА механизма:
///
/// 1. Обычный: поймали исключение — сразу сложили отчёт.
/// 2. Метка сеанса: при старте создаём файл, при штатном выходе удаляем. Если при следующем
///    запуске метка на месте, значит прошлый сеанс умер, не попрощавшись, — и отчёт собирается
///    задним числом из того, что успело попасть в лог. Именно так ловятся нативные падения.
///
/// В отчёт не попадает ни одного набранного или продиктованного символа: тот же принцип, что и
/// у лога. Только окружение, стек и этапы обработки.
/// </summary>
internal static class CrashReport
{
    [DllImport("user32.dll")]
    private static extern int GetKeyboardLayoutList(int nBuff, [Out] IntPtr[]? lpList);

    internal static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Keyboop",
        "crash-reports");

    private static string MarkerPath => Path.Combine(Directory, ".session");

    /// <summary>
    /// Отметить начало сеанса. Возвращает путь к отчёту, если ПРОШЛЫЙ сеанс завершился аварийно,
    /// иначе null.
    /// </summary>
    internal static string? BeginSession()
    {
        string? previous = null;

        try
        {
            System.IO.Directory.CreateDirectory(Directory);

            if (File.Exists(MarkerPath))
            {
                // Метка осталась с прошлого раза: программа не дошла до штатного выхода.
                var died = File.ReadAllText(MarkerPath);
                previous = Save(
                    "программа завершилась аварийно, не оставив исключения "
                    + "(обычно это сбой в системном коде — его .NET перехватить не может)",
                    exception: null,
                    extra: $"Прошлый сеанс начался: {died}");
            }

            File.WriteAllText(MarkerPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        }
        catch (Exception)
        {
            // Диагностика не должна мешать запуску программы.
        }

        return previous;
    }

    /// <summary>Штатный выход: метку снимаем, иначе следующий запуск решит, что мы упали.</summary>
    internal static void EndSession()
    {
        try
        {
            if (File.Exists(MarkerPath))
            {
                File.Delete(MarkerPath);
            }
        }
        catch (Exception)
        {
            // Ложный отчёт при следующем запуске — меньшее зло, чем сбой при выходе.
        }
    }

    /// <summary>Сложить отчёт. Возвращает путь к файлу либо null, если записать не удалось.</summary>
    internal static string? Save(string reason, Exception? exception, string? extra = null)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);

            var path = Path.Combine(
                Directory, $"crash-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt");

            File.WriteAllText(path, Compose(reason, exception, extra), Encoding.UTF8);
            Log.Write($"отчёт об аварии: {path}");

            Prune();
            return path;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Compose(string reason, Exception? exception, string? extra)
    {
        var text = new StringBuilder();

        text.AppendLine("=== Keyboop · отчёт об аварии ===");
        text.AppendLine();
        text.AppendLine($"Когда:    {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        text.AppendLine($"Причина:  {reason}");
        text.AppendLine();

        if (extra is not null)
        {
            text.AppendLine(extra);
            text.AppendLine();
        }

        text.AppendLine("--- Исключение ---");
        text.AppendLine(exception?.ToString() ?? "(нет: падение произошло вне управляемого кода)");
        text.AppendLine();

        text.AppendLine("--- Окружение ---");
        AppendEnvironment(text);
        text.AppendLine();

        text.AppendLine("--- Последние записи лога ---");
        text.AppendLine("(содержимого набранного и продиктованного здесь нет — только этапы)");

        // Из файла, а не из памяти: посмертный отчёт пишет уже следующий процесс, и в его
        // памяти нет ни строчки от умершего. См. Log.FileTail.
        text.AppendLine(Log.FileTail());
        text.AppendLine();

        text.AppendLine("Приложите этот файл к сообщению об ошибке целиком.");
        return text.ToString();
    }

    private static void AppendEnvironment(StringBuilder text)
    {
        void Line(string name, string value) => text.AppendLine($"{name,-22}{value}");

        try
        {
            Line("Windows:", Environment.OSVersion.VersionString);
            Line("Разрядность системы:", Environment.Is64BitOperatingSystem ? "64" : "32");
            Line("Разрядность процесса:", Environment.Is64BitProcess ? "64" : "32");
            Line(".NET:", Environment.Version.ToString());
            Line("Среда выполнения:", RuntimeInformation.FrameworkDescription);
            Line("Процессоров:", Environment.ProcessorCount.ToString());
            Line("Память процесса:", $"{Environment.WorkingSet / 1024 / 1024} МБ");
            Line("Программа:",
                typeof(CrashReport).Assembly.GetName().Version?.ToString() ?? "неизвестно");
            Line("Подробный лог:", Log.Verbose ? "включён" : "выключен");

            // Раскладки — прямо по делу: почти всё, что делает программа, зависит от их набора,
            // а «нет нужной раскладки» выглядит для человека как «не работает переключение».
            var count = GetKeyboardLayoutList(0, null);
            if (count > 0)
            {
                var list = new IntPtr[count];
                GetKeyboardLayoutList(count, list);
                Line("Раскладки:", string.Join(", ", list.Select(h => $"0x{h.ToInt64():X}")));
            }
        }
        catch (Exception ex)
        {
            text.AppendLine($"(окружение собрано не полностью: {ex.GetType().Name})");
        }
    }

    /// <summary>
    /// Держим последние двадцать отчётов. Без этого папка растёт бесконечно, а смысл имеют
    /// свежие: старые описывают уже исправленное.
    /// </summary>
    private static void Prune()
    {
        try
        {
            var files = new DirectoryInfo(Directory)
                .GetFiles("crash-*.txt")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(20);

            foreach (var file in files)
            {
                file.Delete();
            }
        }
        catch (Exception)
        {
            // Уборка — необязательная часть.
        }
    }
}
