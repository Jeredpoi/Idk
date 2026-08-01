using System.Text;
using System.Text.Json;
using Keyboop.Core.Layout;

namespace Keyboop.Core.Snippets;

/// <summary>
/// Автозамена: набрал сокращение — получил подпись, адрес, готовую фразу.
///
/// ⚠️ РАСКЛАДКА И РЕГИСТР НЕ ВАЖНЫ. Человек, который завёл сокращение «адр», наберёт его и как
/// «адр», и как «flh» — если забыл переключить язык. Отказать во втором случае значит сделать
/// функцию бесполезной ровно в той ситуации, ради которой существует вся программа.
/// </summary>
public sealed class SnippetStore
{
    private readonly string _path;

    /// <summary>Триггер в нормальной форме → раскрытие.</summary>
    private readonly Dictionary<string, string> _snippets;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public SnippetStore(string path, IDictionary<string, string>? initial = null)
    {
        _path = path;
        _snippets = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (trigger, expansion) in initial ?? Read(path))
        {
            var key = Normalize(trigger);
            if (key.Length > 0)
            {
                _snippets[key] = expansion;
            }
        }
    }

    public int Count => _snippets.Count;

    /// <summary>Все сокращения для показа в настройках.</summary>
    public IReadOnlyDictionary<string, string> All => _snippets;

    /// <summary>
    /// Раскрытие для набранного слова, либо null.
    ///
    /// Проверяем три формы: как набрано, и переведённое в обе раскладки. Так «flh» находит
    /// сокращение «адр», заведённое по-русски.
    /// </summary>
    public string? Expansion(string typed)
    {
        if (string.IsNullOrEmpty(typed))
        {
            return null;
        }

        var direct = Normalize(typed);
        if (direct.Length == 0)
        {
            return null;
        }

        if (_snippets.TryGetValue(direct, out var found))
        {
            return Sanitize(found);
        }

        foreach (var toCyrillic in new[] { true, false })
        {
            var swapped = Normalize(Keymap.Convert(typed, toCyrillic));
            if (swapped != direct && _snippets.TryGetValue(swapped, out found))
            {
                return Sanitize(found);
            }
        }

        return null;
    }

    public void Set(string trigger, string expansion)
    {
        var key = Normalize(trigger);
        if (key.Length == 0)
        {
            return;
        }

        _snippets[key] = expansion;
        Save();
    }

    public void Remove(string trigger)
    {
        _snippets.Remove(Normalize(trigger));
        Save();
    }

    /// <summary>Заменить весь список — из окна настроек.</summary>
    public void Replace(IEnumerable<KeyValuePair<string, string>> pairs)
    {
        _snippets.Clear();

        foreach (var (trigger, expansion) in pairs)
        {
            var key = Normalize(trigger);
            if (key.Length > 0 && expansion.Length > 0)
            {
                _snippets[key] = expansion;
            }
        }

        Save();
    }

    private static string Normalize(string trigger) => trigger.Trim().ToLowerInvariant();

    /// <summary>
    /// Режем управляющие символы, кроме переноса строки и табуляции.
    ///
    /// Раскрытие приходит из файла, который человек мог править руками или получить от коллеги.
    /// Управляющий символ, отправленный через синтетический ввод, повёл бы себя непредсказуемо —
    /// вплоть до того, что приложение приняло бы его за команду.
    /// </summary>
    private static string Sanitize(string expansion)
    {
        var builder = new StringBuilder(expansion.Length);

        foreach (var c in expansion)
        {
            if (c == '\n' || c == '\t' || !char.IsControl(c))
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(_path, JsonSerializer.Serialize(_snippets, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Не та причина, по которой стоит ронять приложение.
        }
    }

    private static Dictionary<string, string> Read(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path), Json)
                       ?? [];
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Битый файл не должен мешать запуску.
        }

        return [];
    }
}
