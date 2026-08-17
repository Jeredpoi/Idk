using System.Collections.Concurrent;
using System.Text;

namespace Keyboop.Windows.Diagnostics;

/// <summary>
/// Лог для разбора жалоб.
///
/// ⚠️ ПРИНЦИП, УНАСЛЕДОВАННЫЙ ОТ macOS-ВЕРСИИ: в лог не пишется НИ ОДНОГО распознанного или
/// набранного слова. Только длины, счётчики и коды. Диктовка — это личная переписка, пароли,
/// медицинские вопросы; текстовый лог такого содержания не должен существовать в принципе,
/// даже если человек сам пришлёт его нам в отчёте. Подробные записи этого правила не отменяют:
/// они добавляют ЭТАПЫ обработки, а не содержимое.
///
/// ⚠️ ЗАПИСЬ НА ДИСК УШЛА В ОТДЕЛЬНЫЙ ПОТОК, И ЭТО НЕ ОПТИМИЗАЦИЯ. Раньше каждая строка
/// открывала, дописывала и закрывала файл ПРЯМО НА МЕСТЕ ВЫЗОВА — а зовут отсюда в том числе из
/// колбэка перехватчика, которого Windows ждёт и снимает через 300 мс. При включённых подробных
/// записях это давало файловую операцию на КАЖДОЕ нажатие клавиши: на занятом диске или под
/// антивирусом такое легко выходит за лимит, и перехватчик отваливается посреди работы. Именно
/// так выглядит «то вылетает, то нет». Теперь вызов стоит одну вставку в очередь, а разгребает
/// её фоновый поток.
/// </summary>
public static class Log
{
    private static readonly string LogPath = BuildPath();

    /// <summary>Очередь на запись в файл. Разгребается фоновым потоком.</summary>
    private static readonly BlockingCollection<string> Pending = new(new ConcurrentQueue<string>());

    /// <summary>Очередь для окна лога, если оно открыто в этом же процессе.</summary>
    private static readonly ConcurrentQueue<string> Live = new();

    /// <summary>Хвост в памяти — для отчёта об аварии, пойманной в этом же процессе.</summary>
    private static readonly Queue<string> Tail = new();

    private const int TailLimit = 400;
    private const int LiveLimit = 2000;

    /// <summary>Больше двух мегабайт держать незачем: смысл имеет хвост, а не история.</summary>
    private const long RotateAtBytes = 2 * 1024 * 1024;

    /// <summary>
    /// Писать этапы обработки нажатий.
    ///
    /// ⚠️ ВКЛЮЧЕНО ПО УМОЛЧАНИЮ, и это осознанно. Плавающую аварию нельзя поймать режимом,
    /// который человек должен догадаться включить ЗАРАНЕЕ: к моменту, когда стало ясно, что
    /// нужен след, падение уже произошло. Запись стоит одну вставку в очередь, поэтому
    /// постоянный след себе позволить можно.
    /// </summary>
    public static bool Verbose { get; set; } = true;

    static Log()
    {
        var writer = new Thread(WriterLoop)
        {
            IsBackground = true,
            Name = "keyboop-log",
            // Ниже обычного: лог не должен соревноваться за процессор с обработкой ввода.
            Priority = ThreadPriority.BelowNormal,
        };

        writer.Start();
    }

    private static string BuildPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Keyboop");

        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "keyboop.log");
    }

    /// <summary>
    /// Повернуть лог, если он разросся. Один предыдущий файл сохраняем: авария могла случиться
    /// в прошлом сеансе.
    ///
    /// ⚠️ ЗОВЁТ ЭТО ТОЛЬКО ОСНОВНОЙ ПРОЦЕСС, И ТОЛЬКО ОДИН РАЗ ПРИ СТАРТЕ. Окно лога живёт
    /// отдельным процессом; повернув файл из него, мы переименовали бы лог прямо под пишущей
    /// рукой основного — то есть потеряли бы записи ровно во время поиска аварии.
    /// </summary>
    public static void Rotate()
    {
        try
        {
            var file = new FileInfo(LogPath);
            if (file.Exists && file.Length > RotateAtBytes)
            {
                File.Move(LogPath, LogPath + ".old", overwrite: true);
            }
        }
        catch (Exception)
        {
            // Не повернулось — будем дописывать в прежний. Не повод мешать запуску.
        }
    }

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff}  {message}";

        // ⚠️ ПЕРВЫМ ДЕЛОМ — В КОЛЬЦО, ПЕРЕЖИВАЮЩЕЕ ПАДЕНИЕ. Всё остальное ниже либо в памяти
        // (гибнет вместе с процессом), либо в очереди фонового писателя (не успевает дойти до
        // диска при нативной аварии). Ради посмертного разбора важна именно эта строка.
        Breadcrumbs.Write(line);

        Live.Enqueue(line);
        while (Live.Count > LiveLimit && Live.TryDequeue(out _))
        {
            // Смысл живого лога в хвосте.
        }

        lock (Tail)
        {
            Tail.Enqueue(line);
            while (Tail.Count > TailLimit)
            {
                Tail.Dequeue();
            }
        }

        try
        {
            Pending.Add(line);
        }
        catch (Exception)
        {
            // Очередь закрыта при выходе — писать больше некуда, и это нормально.
        }
    }

    /// <summary>Этап обработки. Когда подробности выключены, стоит одну проверку флага.</summary>
    public static void Trace(string message)
    {
        if (Verbose)
        {
            Write("· " + message);
        }
    }

    /// <summary>
    /// Фоновый писатель. Держит файл открытым и сбрасывает после каждой порции: при аварии на
    /// диске должно остаться всё, кроме разве что последних миллисекунд.
    /// </summary>
    private static void WriterLoop()
    {
        foreach (var line in Pending.GetConsumingEnumerable())
        {
            try
            {
                using var stream = new FileStream(
                    LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream, Encoding.UTF8);

                writer.WriteLine(line);

                // Догребаем то, что накопилось, пока открывали файл: при быстром наборе это
                // десятки строк, и открывать файл под каждую было бы тем же злом, что и раньше.
                while (Pending.TryTake(out var more))
                {
                    writer.WriteLine(more);
                }

                writer.Flush();
            }
            catch (Exception)
            {
                // Диск занят, полон, закрыт политикой. Программа обязана работать дальше:
                // диктовка важнее записи о диктовке.
            }
        }
    }

    /// <summary>
    /// Дождаться, пока очередь опустеет. Зовётся перед отчётом об аварии: иначе в отчёт попал бы
    /// лог без последних строк — то есть без самого важного.
    /// </summary>
    public static void Flush(int millisecondsTimeout = 1500)
    {
        var deadline = Environment.TickCount64 + millisecondsTimeout;

        while (Pending.Count > 0 && Environment.TickCount64 < deadline)
        {
            Thread.Sleep(15);
        }
    }

    /// <summary>Забрать накопленное для окна лога в этом же процессе.</summary>
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
    /// ⚠️ ИМЕННО ИЗ ФАЙЛА, А НЕ ИЗ ПАМЯТИ. Посмертный отчёт о падении собирает СЛЕДУЮЩИЙ запуск
    /// программы — у него своя, пустая память, а всё, что писал умерший процесс, осталось на
    /// диске. Первая версия отчёта брала хвост из памяти и выдала пустой раздел ровно в том
    /// единственном случае, ради которого затевалась.
    /// </summary>
    public static string FileTail(int maxLines = 300)
    {
        try
        {
            if (!File.Exists(LogPath))
            {
                return "(файл лога не найден)";
            }

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

    /// <summary>Хвост лога из памяти — для аварии, пойманной в этом же процессе.</summary>
    public static string Snapshot()
    {
        lock (Tail)
        {
            return string.Join(Environment.NewLine, Tail);
        }
    }

    public static string FilePath => LogPath;
}
