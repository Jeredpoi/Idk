using System.IO.MemoryMappedFiles;
using System.Text;

namespace Keyboop.Windows.Diagnostics;

/// <summary>
/// След последних событий, ПЕРЕЖИВАЮЩИЙ гибель процесса.
///
/// ⚠️ ЗАЧЕМ ЭТО ПОВЕРХ ОБЫЧНОГО ЛОГА. Обычный лог пишет фоновый поток — иначе файловая операция
/// на каждое нажатие клавиши срывала перехватчику лимит в 300 мс. Но у фоновой записи есть цена:
/// при НАТИВНОМ падении (нарушение доступа в системном коде) процесс исчезает мгновенно, и всё,
/// что стояло в очереди, гибнет вместе с ним. Теряется ровно хвост — то есть единственное, что
/// имеет значение для разбора аварии. Первый же посмертный отчёт вышел пустым.
///
/// Здесь другой механизм. Файл отображён в память, и запись строки — это запись в память,
/// наносекунды: в колбэк перехватчика такое класть можно. А грязные страницы отображённого файла
/// дописывает на диск САМА СИСТЕМА, в том числе после аварийного завершения процесса. Поэтому
/// последние строки остаются, даже когда падение не оставило ни исключения, ни шанса что-то
/// сделать самим.
///
/// Кольцо фиксированного размера: старое затирается новым. Смысл имеет хвост, а не история —
/// история и так в обычном логе.
///
/// Правило приватности то же, что у лога: ни одного набранного или продиктованного символа.
/// </summary>
internal static class Breadcrumbs
{
    /// <summary>Четверти мегабайта хватает на несколько тысяч строк — заведомо больше, чем нужно.</summary>
    private const int Capacity = 256 * 1024;

    /// <summary>Первые четыре байта — куда писать дальше. Дальше само кольцо.</summary>
    private const int HeaderSize = 4;

    private static readonly object Gate = new();
    private static readonly MemoryMappedFile? File_;
    private static readonly MemoryMappedViewAccessor? View;

    internal static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Keyboop",
        "breadcrumbs.bin");

    static Breadcrumbs()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

            File_ = MemoryMappedFile.CreateFromFile(
                FilePath, FileMode.OpenOrCreate, mapName: null, Capacity + HeaderSize);

            View = File_.CreateViewAccessor();
        }
        catch (Exception)
        {
            // Не вышло — программа обязана работать дальше. Диагностика не главнее работы.
            File_ = null;
            View = null;
        }
    }

    /// <summary>
    /// Дописать строку в кольцо. Стоит взятие замка и копирование пары десятков байт — столько
    /// можно позволить себе даже внутри колбэка перехватчика.
    /// </summary>
    internal static void Write(string line)
    {
        var view = View;
        if (view is null)
        {
            return;
        }

        try
        {
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            if (bytes.Length >= Capacity)
            {
                return;
            }

            lock (Gate)
            {
                var offset = view.ReadInt32(0);
                if (offset < 0 || offset >= Capacity)
                {
                    offset = 0;
                }

                // Кольцо: то, что не влезло в конец, продолжается с начала.
                var untilEnd = Math.Min(bytes.Length, Capacity - offset);
                view.WriteArray(HeaderSize + offset, bytes, 0, untilEnd);

                if (untilEnd < bytes.Length)
                {
                    view.WriteArray(HeaderSize, bytes, untilEnd, bytes.Length - untilEnd);
                }

                view.Write(0, (offset + bytes.Length) % Capacity);
            }
        }
        catch (Exception)
        {
            // След — вспомогательная вещь; уронить из-за него программу было бы издевательством.
        }
    }

    /// <summary>
    /// Прочитать след в хронологическом порядке.
    ///
    /// Читаем ЧЕРЕЗ ФАЙЛ, а не через своё отображение: в посмертном отчёте нас интересует след
    /// ПРОШЛОГО, уже мёртвого процесса, а его страницы система дописала в файл.
    /// </summary>
    internal static string ReadAll()
    {
        try
        {
            if (!System.IO.File.Exists(FilePath))
            {
                return "(следа нет: файл ещё не создан)";
            }

            byte[] raw;
            using (var stream = new FileStream(
                       FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                raw = new byte[stream.Length];
                _ = stream.Read(raw, 0, raw.Length);
            }

            if (raw.Length < HeaderSize + 1)
            {
                return "(след пуст)";
            }

            var offset = BitConverter.ToInt32(raw, 0);
            var size = raw.Length - HeaderSize;
            if (offset < 0 || offset >= size)
            {
                offset = 0;
            }

            // Кольцо разворачиваем: сначала то, что после точки записи (оно старее), потом начало.
            var text = Encoding.UTF8.GetString(raw, HeaderSize + offset, size - offset)
                       + Encoding.UTF8.GetString(raw, HeaderSize, offset);

            // Нули — ещё не заполненная часть кольца при первом запуске.
            text = text.Replace("\0", string.Empty).TrimStart('\n');

            return text.Length > 0 ? text : "(след пуст)";
        }
        catch (Exception ex)
        {
            return $"(след не прочитан: {ex.GetType().Name})";
        }
    }

    /// <summary>Отметить границу сеанса — иначе в кольце не видно, где кончился прошлый запуск.</summary>
    internal static void MarkSessionStart() =>
        Write($"===== запуск {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");
}
