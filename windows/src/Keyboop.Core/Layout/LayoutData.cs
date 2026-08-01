using System.Text.Json;

namespace Keyboop.Core.Layout;

/// <summary>
/// Языковые данные детектора: триграммные лог-вероятности и словари RU/EN.
/// Формат файлов тот же, что в macOS-версии, поэтому они переиспользуются без конвертации.
/// Источник данных — keyswitcher (MIT, © 2026 Ilya Granin), см. THIRD_PARTY.md.
/// </summary>
public sealed class LayoutData
{
    /// <summary>Штраф за отсутствующую триграмму. Столько же, сколько в macOS-версии.</summary>
    private const double MissingTrigramPenalty = -20.0;

    private readonly Dictionary<string, double> _trigramsRu;
    private readonly Dictionary<string, double> _trigramsEn;

    public IReadOnlySet<string> WordsRu { get; }

    public IReadOnlySet<string> WordsEn { get; }

    /// <summary>Данные пригодны к работе. Пока false — детектор обязан молчать, а не гадать.</summary>
    public bool IsLoaded { get; }

    private LayoutData(string dataDirectory)
    {
        _trigramsRu = LoadDictionary(Path.Combine(dataDirectory, "trigrams_ru.json"));
        _trigramsEn = LoadDictionary(Path.Combine(dataDirectory, "trigrams_en.json"));

        var ru = LoadSet(Path.Combine(dataDirectory, "words_ru.json"));
        ru.UnionWith(ExtraWords.Ru);
        ru.UnionWith(ExtraWords.RuAbbr);
        ru.UnionWith(ExtraWords.RuShort);

        var en = LoadSet(Path.Combine(dataDirectory, "words_en.json"));
        en.UnionWith(ExtraWords.En);

        WordsRu = ru;
        WordsEn = en;
        IsLoaded = _trigramsRu.Count > 0 && en.Count > 0;
    }

    /// <summary>
    /// Загрузка идёт лениво и один раз: файлы вместе весят около пяти мегабайт, и читать их
    /// на каждое слово было бы немыслимо.
    /// </summary>
    private static LayoutData? _shared;
    private static readonly object Gate = new();

    /// <summary>Каталог с json-данными. По умолчанию — папка «data» рядом с приложением.</summary>
    public static string DataDirectory { get; set; } =
        Path.Combine(AppContext.BaseDirectory, "data");

    public static LayoutData Shared
    {
        get
        {
            if (_shared is not null)
            {
                return _shared;
            }

            lock (Gate)
            {
                return _shared ??= new LayoutData(DataDirectory);
            }
        }
    }

    /// <summary>Для тестов: загрузить данные из указанного каталога.</summary>
    public static LayoutData LoadFrom(string directory) => new(directory);

    /// <summary>
    /// Средняя лог-вероятность триграмм слова. Слово дополняется пробелами по краям — так
    /// начало и конец слова тоже участвуют в оценке, а без этого «нщ» в начале и в середине
    /// весили бы одинаково.
    /// </summary>
    public double Plausibility(string word, bool cyrillic)
    {
        var table = cyrillic ? _trigramsRu : _trigramsEn;
        var padded = " " + word.ToLowerInvariant() + " ";

        if (padded.Length < 3)
        {
            return double.NegativeInfinity;
        }

        var sum = 0.0;
        for (var i = 0; i <= padded.Length - 3; i++)
        {
            sum += table.TryGetValue(padded.Substring(i, 3), out var value)
                ? value
                : MissingTrigramPenalty;
        }

        return sum / (padded.Length - 2);
    }

    private static Dictionary<string, double> LoadDictionary(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<Dictionary<string, double>>(stream) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static HashSet<string> LoadSet(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var items = JsonSerializer.Deserialize<List<string>>(stream);
            return items is null ? [] : new HashSet<string>(items, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
