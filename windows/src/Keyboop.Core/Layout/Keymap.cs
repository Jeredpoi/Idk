using System.Text;

namespace Keyboop.Core.Layout;

/// <summary>
/// Соответствие физических клавиш между раскладками «US» и «Русская».
/// Конвертация посимвольная: мы работаем с уже набранной строкой, коды клавиш не нужны.
/// </summary>
public static class Keymap
{
    /// <summary>Базовые пары: символ US → символ ЙЦУКЕН, нижний регистр.</summary>
    private static readonly (char En, char Ru)[] BasePairs =
    [
        ('`', 'ё'), ('q', 'й'), ('w', 'ц'), ('e', 'у'), ('r', 'к'), ('t', 'е'), ('y', 'н'), ('u', 'г'),
        ('i', 'ш'), ('o', 'щ'), ('p', 'з'), ('[', 'х'), (']', 'ъ'),
        ('a', 'ф'), ('s', 'ы'), ('d', 'в'), ('f', 'а'), ('g', 'п'), ('h', 'р'), ('j', 'о'), ('k', 'л'),
        ('l', 'д'), (';', 'ж'), ('\'', 'э'),
        ('z', 'я'), ('x', 'ч'), ('c', 'с'), ('v', 'м'), ('b', 'и'), ('n', 'т'), ('m', 'ь'),
        (',', 'б'), ('.', 'ю'), ('/', '.'),
    ];

    public static readonly IReadOnlyDictionary<char, char> EnToRu = BuildMap(toCyrillic: true);
    public static readonly IReadOnlyDictionary<char, char> RuToEn = BuildMap(toCyrillic: false);

    private static Dictionary<char, char> BuildMap(bool toCyrillic)
    {
        var map = new Dictionary<char, char>();

        foreach (var (en, ru) in BasePairs)
        {
            var from = toCyrillic ? en : ru;
            var to = toCyrillic ? ru : en;
            map[from] = to;

            // Верхний регистр получают только буквы: у знаков «,» и «.» его нет, а попытка
            // положить их дважды перетёрла бы уже готовую пару.
            if (char.IsLetter(en))
            {
                map[char.ToUpperInvariant(from)] = char.ToUpperInvariant(to);
            }
        }

        return map;
    }

    /// <summary>
    /// Перевести строку из одной раскладки в другую. Символы вне таблицы остаются как есть.
    /// </summary>
    public static string Convert(string text, bool toCyrillic)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var map = toCyrillic ? EnToRu : RuToEn;
        var builder = new StringBuilder(text.Length);

        foreach (var ch in text)
        {
            builder.Append(map.TryGetValue(ch, out var mapped) ? mapped : ch);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Знаки препинания, одинаковые в обеих раскладках (просто на разных клавишах). В конце слова
    /// их не конвертируем — иначе «.» превратилась бы в «ю», а «,» в «б».
    /// </summary>
    public static readonly IReadOnlySet<char> TrailingPunctuation =
        new HashSet<char>(".,!?;:…");

    /// <summary>
    /// Умная конвертация: концевую пунктуацию оставляем, ядро переводим — «ghbdtn.» → «привет.»,
    /// а не «приветю».
    ///
    /// НО: клавиши «,», «.» и «;» в US-раскладке — это И знаки препинания, И буквы **б ю ж**.
    /// Поэтому сначала пробуем перевести слово ЦЕЛИКОМ, считая этот символ буквой: если получилось
    /// словарное слово, значит буквой он и был («yj;» → «нож», «[kt,» → «хлеб»). Без этой проверки
    /// такие слова резались в «но;» и «хле,» — то есть портились.
    /// </summary>
    public static string SmartConvert(string word, bool toCyrillic, Func<string, bool>? isValidTarget = null)
    {
        if (string.IsNullOrEmpty(word))
        {
            return word;
        }

        if (toCyrillic && isValidTarget is not null)
        {
            var full = Convert(word, toCyrillic: true);
            if (isValidTarget(full.ToLowerInvariant()))
            {
                return full;
            }
        }

        var end = word.Length;
        while (end > 0 && TrailingPunctuation.Contains(word[end - 1]))
        {
            end--;
        }

        if (end == 0)
        {
            return word;
        }

        return Convert(word[..end], toCyrillic) + word[end..];
    }

    /// <summary>Ядро слова без концевой пунктуации — для анализа детектором.</summary>
    public static string CoreOf(string word)
    {
        if (string.IsNullOrEmpty(word))
        {
            return word;
        }

        var end = word.Length;
        while (end > 0 && TrailingPunctuation.Contains(word[end - 1]))
        {
            end--;
        }

        return word[..end];
    }
}

/// <summary>Проверки алфавита, которыми пользуется весь детектор.</summary>
public static class ScriptExtensions
{
    public static bool HasCyrillic(this string s)
    {
        foreach (var c in s)
        {
            if (c is >= '\u0400' and <= '\u04FF')
            {
                return true;
            }
        }

        return false;
    }

    public static bool HasLatinLetter(this string s)
    {
        foreach (var c in s)
        {
            if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>LAT / CYR / MIX / — для диагностики без содержимого.</summary>
    public static string ScriptClass(this string s)
    {
        var cyr = s.HasCyrillic();
        var lat = s.HasLatinLetter();

        if (cyr && lat)
        {
            return "MIX";
        }

        return cyr ? "CYR" : lat ? "LAT" : "—";
    }
}
