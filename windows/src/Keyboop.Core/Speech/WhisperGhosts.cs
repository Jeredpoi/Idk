using System.Text.RegularExpressions;

namespace Keyboop.Core.Speech;

/// <summary>
/// Фразы-призраки whisper: обрывки титров из обучающих данных, которые модель выдаёт на тишине,
/// шуме и обрезанных хвостах («Субтитры создавал…», «Продолжение следует», «Thanks for watching»).
/// Проблема не наша: whisper учили в том числе на ютуб-субтитрах, и без разборчивой речи он
/// дописывает самое вероятное продолжение такого текста.
///
/// ⚠️ ГЛАВНОЕ ПРАВИЛО ЭТОГО ФАЙЛА: удалить чужое слово хуже, чем пропустить призрак.
/// Поэтому шаблон срабатывает, только когда фраза стоит ЦЕЛИКОМ отдельным предложением, и режем
/// мы исключительно с КРАЁВ. Внутри живой реплики не вырезаем ничего и никогда — «спасибо за
/// просмотр» человек может сказать и всерьёз.
/// </summary>
public static class WhisperGhosts
{
    /// <summary>
    /// Шаблоны целых предложений. Регистр не важен, знаки по краям снимает нормализация.
    ///
    /// ⚠️ У «субтитров» ОБЯЗАТЕЛЕН глагол авторства: шаблон без него убивал живую фразу
    /// «Субтитры к фильму мы сделаем сами». Список глаголов расширять можно, а до «любого
    /// продолжения» ослаблять нельзя.
    ///
    /// ⚠️ Шаблоны без хвоста «.*» требуют, чтобы фраза была предложением целиком: «Продолжение
    /// следует за вступлением, так устроена книга» — живая речь, и с открытым хвостом она стиралась.
    /// </summary>
    private static readonly string[] Patterns =
    [
        @"^субтитры (создал|создавал|сделал|делал|подготовил|подготовлены|предоставлены|by)\b.*$",
        @"^редактор субтитров\b.*$",
        @"^продолжение следует$",
        @"^спасибо за просмотр$",
        @"^спасибо за внимание$",
        @"^подписывайтесь на канал$",
        @"^подписывайся на канал$",
        @"^ставьте лайк$",
        @"^не забудьте подписаться$",
        @"^thanks? (you )?for watching$",
        @"^subtitles by\b.*$",
        @"^subtitles? and translation by\b.*$",
        @"^please subscribe$",
        @"^amara\.org\b.*$",
        @"^www\.[a-z0-9.-]+$",
    ];

    private static readonly Regex[] Regexes = Patterns
        .Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        .ToArray();

    /// <summary>Граница предложения — знак конца плюс ОБЯЗАТЕЛЬНЫЙ пробел после него.</summary>
    private static readonly Regex SentenceBoundary =
        new(@"([.!?…]+)\s+", RegexOptions.CultureInvariant);

    private static readonly Regex WhitespaceRun =
        new(@"\s+", RegexOptions.CultureInvariant);

    /// <summary>Знаки, которые снимаем с краёв предложения перед сверкой с шаблоном.</summary>
    private const string EdgeTrim = " \t\n\r.,!?…-–—:;\"'«»()";

    /// <summary>
    /// Служебный разделитель для разрезания по границам предложений. Управляющий символ U+0001
    /// в расшифровке появиться не может, поэтому он безопасен как метка.
    /// </summary>
    private const char Sentinel = '\u0001';

    /// <summary>
    /// Убрать призраки. Может вернуть пустую строку — значит, речи не было вовсе.
    /// Если ничего не срезано, возвращаем ИСХОДНЫЙ текст, чтобы не терять авторское форматирование.
    /// </summary>
    public static string Clean(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var sentences = Split(text);
        if (sentences.Count == 0)
        {
            return text;
        }

        var kept = new List<string>(sentences);

        // Срезаем только с краёв и пока срезается: призрак почти всегда приклеен спереди или сзади,
        // а вырезание из середины разорвало бы живую фразу пополам.
        while (kept.Count > 0 && IsGhost(kept[0]))
        {
            kept.RemoveAt(0);
        }

        while (kept.Count > 0 && IsGhost(kept[^1]))
        {
            kept.RemoveAt(kept.Count - 1);
        }

        // Огрызки из одних знаков после срезки оставлять нельзя: «Продолжение следует...» давало
        // в остатке « . . », и это уезжало в поле как настоящий текст.
        kept = kept.Where(s => s.Any(char.IsLetterOrDigit)).ToList();

        if (kept.Count == sentences.Count)
        {
            return text;
        }

        return string.Join(" ", kept).Trim();
    }

    /// <summary>Сколько предложений-призраков было бы срезано (для лога — без содержимого).</summary>
    public static int CountGhosts(string text)
    {
        var sentences = Split(text);
        return sentences.Count(IsGhost);
    }

    /// <summary>
    /// Разбить на предложения. Пробел после знака ОБЯЗАТЕЛЕН, и это не придирка: резать по каждой
    /// точке значит рвать «А.Синецкая» на «А.» и «Синецкая», а «Продолжение следует...» — на фразу
    /// и два огрызка «.». В первом случае половина титров оставалась в тексте, во втором в поле
    /// уезжали голые точки.
    /// </summary>
    private static List<string> Split(string text)
    {
        var result = new List<string>();

        foreach (var line in text.Split('\n', '\r'))
        {
            var marked = SentenceBoundary.Replace(line, "$1" + Sentinel);
            foreach (var part in marked.Split(Sentinel))
            {
                var trimmed = part.Trim();
                if (trimmed.Length > 0)
                {
                    result.Add(trimmed);
                }
            }
        }

        return result;
    }

    private static bool IsGhost(string sentence)
    {
        // Нормализуем: снимаем знаки по краям и схлопываем пробелы, иначе «Субтитры…» с многоточием
        // и «субтитры» с точкой требовали бы отдельных шаблонов.
        var s = WhitespaceRun.Replace(sentence.Trim(EdgeTrim.ToCharArray()), " ");
        return s.Length > 0 && Regexes.Any(r => r.IsMatch(s));
    }
}
