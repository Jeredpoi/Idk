using System.Collections.Concurrent;
using System.Text;

namespace Keyboop.Windows.Diagnostics;

/// <summary>
/// Лог для разбора жалоб.
///
/// ⚠️ ПРИНЦИП, УНАСЛЕДОВАННЫЙ ОТ macOS-ВЕРСИИ: в лог не пишется НИ ОДНОГО распознанного или
/// набранного слова. Только длины, счётчики и коды. Диктовка — это личная переписка, пароли,
/// медицинские вопросы; текстовый лог такого содержания не должен существовать в принципе,
/// даже если человек сам пришлёт его нам в отчёте. Подробный режим ниже этого правила не
/// отменяет: он добавляет ЭТАПЫ обработки, а не содержимое.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly string LogPath = BuildPath();

    /// <summary>Хвост лога держим в памяти — его показывает окно диагностики.</summary>
    private static readonly Queue<string> Tail = new();

    private const int TailLimit = 300;

    /// <summary>
    /// Очередь для живого окна.
    ///
    /// ⚠️ ИМЕННО ОЧЕРЕДЬ, А НЕ СОБЫТИЕ С ПРЯМЫМ ВЫЗОВОМ. Пишут в лог в том числе из колбэка
    /// перехватчика — с потока, который ждёт Windows и снимает перехват, если тот задумался.
    /// Дёрнуть оттуда обновление окна значит делать работу с интерфейсом внутри колбэка.
    /// Окно само забирает накопленное по таймеру, а запись стоит одну вставку в очередь.
    /// </summary>
    private static readonly ConcurrentQueue<string> Live = new();

    private const int LiveLimit = 2000;

    /// <summary>
    /// Подробный режим: писать этапы обработки нажатий, а не только заметные события.
    ///
    /// Выключен по умолчанию — это несколько строк на каждую клавишу, и держать такой поток
    /// постоянно незачем. Включается на время поиска причины: последняя строка перед обрывом
    /// показывает, докуда дошла обработка.
    /// </summary>
    public static bool Verbose { get; set; }

    private static string BuildPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Keyboop");

        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "keyboop.log");
    }

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff}  {message}";

        // В живую очередь кладём ДО замка: окно обязано увидеть строку даже когда файл занят
        // антивирусом и запись в него буксует.
        Live.Enqueue(line);
        while (Live.Count > LiveLimit && Live.TryDequeue(out _))
        {
            // Смысл живого лога в хвосте, а не в истории: старое отбрасываем.
        }

        lock (Gate)
        {
            Tail.Enqueue(line);
            while (Tail.Count > TailLimit)
            {
                Tail.Dequeue();
            }

            try
            {
                // Дописываем и закрываем на каждой строке. Медленнее буферизации, зато при аварии
                // на диске остаётся всё до последней строки — а лог нужен ровно для аварий.
                File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (IOException)
            {
                // Лог — вспомогательная вещь. Если диск занят или полон, приложение обязано
                // продолжать работать: диктовка важнее записи о диктовке.
            }
            catch (UnauthorizedAccessException)
            {
                // Тот же случай: политика или антивирус закрыли файл.
            }
            catch (Exception)
            {
                // ⚠️ Последний рубеж. Этот метод зовут из обработчиков аварий, в том числе из
                // колбэка перехватчика: исключение отсюда означало бы падение вместо записи
                // о падении — то есть потерю ровно той информации, ради которой лог и заводили.
            }
        }
    }

    /// <summary>Строка подробного режима. Когда режим выключен, стоит одну проверку флага.</summary>
    public static void Trace(string message)
    {
        if (Verbose)
        {
            Write("· " + message);
        }
    }

    /// <summary>Забрать накопленное для живого окна.</summary>
    public static List<string> DrainLive()
    {
        var lines = new List<string>();
        while (Live.TryDequeue(out var line))
        {
            lines.Add(line);
        }

        return lines;
    }

    /// <summary>
    /// Хвост лога ИЗ ФАЙЛА.
    ///
    /// ⚠️ ИМЕННО ИЗ ФАЙЛА, А НЕ ИЗ ПАМЯТИ, И ЭТО НЕ ПРИДИРКА. Посмертный отчёт о падении
    /// собирает СЛЕДУЮЩИЙ запуск программы — у него своя, пустая память, а всё, что писал
    /// умерший процесс, осталось на диске. Первая версия отчёта брала хвост из памяти и выдала
    /// пустой раздел ровно в том единственном случае, ради которого затевалась.
    /// </summary>
    public static string FileTail(int maxLines = 200)
    {
        try
        {
            if (!File.Exists(LogPath))
            {
                return "(файл лога не найден)";
            }

            // Читаем с общим доступом: файл может быть открыт нами же или чужой программой,
            // а отказ прочитать лог означал бы отчёт без самого ценного.
            using var stream = new FileStream(
                LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            var tail = new Queue<string>(maxLines);
            while (reader.ReadLine() is { } line)
            {
                tail.Enqueue(line);
                while (tail.Count > maxLines)
                {
                    tail.Dequeue();
                }
            }

            return tail.Count > 0 ? string.Join(Environment.NewLine, tail) : "(лог пуст)";
        }
        catch (Exception ex)
        {
            return $"(лог не прочитан: {ex.GetType().Name})";
        }
    }

    /// <summary>Хвост лога для окна диагностики и отчёта об ошибке.</summary>
    public static string Snapshot()
    {
        lock (Gate)
        {
            return string.Join(Environment.NewLine, Tail);
        }
    }

    /// <summary>Путь к файлу лога — его открывает пункт меню «Показать лог».</summary>
    public static string FilePath => LogPath;
}
