using System.Text;

namespace Keyboop.Windows.Diagnostics;

/// <summary>
/// Лог для разбора жалоб.
///
/// ⚠️ ПРИНЦИП, УНАСЛЕДОВАННЫЙ ОТ macOS-ВЕРСИИ: в лог не пишется НИ ОДНОГО распознанного или
/// набранного слова. Только длины, счётчики и коды. Диктовка — это личная переписка, пароли,
/// медицинские вопросы; текстовый лог такого содержания не должен существовать в принципе,
/// даже если человек сам пришлёт его нам в отчёте.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly string LogPath = BuildPath();

    /// <summary>Хвост лога держим в памяти — его показывает окно диагностики.</summary>
    private static readonly Queue<string> Tail = new();

    private const int TailLimit = 300;

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

        lock (Gate)
        {
            Tail.Enqueue(line);
            while (Tail.Count > TailLimit)
            {
                Tail.Dequeue();
            }

            try
            {
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
