namespace Keyboop.Core.Speech;

/// <summary>Опции оформления готовой расшифровки.</summary>
public sealed class DictationOutputOptions
{
    /// <summary>Снимать одиночную точку в самом конце реплики.</summary>
    public bool DropFinalPeriod { get; init; }

    /// <summary>Опускать регистр первой буквы (дописываешь в середину чужого предложения).</summary>
    public bool DropLeadingCapital { get; init; }

    /// <summary>
    /// Добавлять пробел в конце, чтобы следующая фраза не слиплась с этой.
    /// Гасится авто-Enter'ом: иначе сообщение уходит с пробелом на конце, и это видно получателю.
    /// </summary>
    public bool TrailingSpace { get; init; } = true;

    /// <summary>Отправлять Enter сразу после текста.</summary>
    public bool AutoEnter { get; init; }
}

/// <summary>
/// Приведение расшифровки к тому, что реально уедет в поле ввода.
/// Порядок операций один в один как в macOS-версии и он важен — см. <see cref="Apply"/>.
/// </summary>
public static class TranscriptPostProcessor
{
    /// <summary>
    /// Полный путь от сырой расшифровки до строки для вставки:
    /// призраки → опции оформления → хвостовой пробел.
    /// </summary>
    public static string Prepare(string? rawText, DictationOutputOptions options)
    {
        var clean = WhisperGhosts.Clean(rawText ?? string.Empty).Trim();
        if (clean.Length == 0)
        {
            return string.Empty;
        }

        var shaped = Apply(clean, options);
        var needsSpace = options.TrailingSpace && !options.AutoEnter;
        return needsSpace ? shaped + " " : shaped;
    }

    /// <summary>
    /// ПОРЯДОК ВАЖЕН: сначала убираем точку, потом регистр. Иначе строка из одной буквы с точкой
    /// («А.») после снятия точки осталась бы заглавной.
    ///
    /// Точку снимаем ТОЛЬКО одиночную в самом конце: «...» и «?»/«!» несут смысл, а многоточие
    /// вдобавок часто ставит сама модель на оборванной фразе.
    ///
    /// Регистр опускаем ТОЛЬКО у первой буквы и только если вторая строчная — иначе аббревиатуры
    /// («МФЦ», «ГОСТ») превратились бы в «мФЦ». Это ровно тот случай, где «умное» правило без
    /// оговорки портит редкий, но очень заметный ввод.
    /// </summary>
    public static string Apply(string? text, DictationOutputOptions options)
    {
        var outText = text ?? string.Empty;

        if (options.DropFinalPeriod
            && outText.EndsWith('.')
            && !outText.EndsWith("..", StringComparison.Ordinal))
        {
            outText = outText[..^1].TrimEnd(' ');
        }

        if (options.DropLeadingCapital && outText.Length > 0 && char.IsUpper(outText[0]))
        {
            var secondIsUpper = outText.Length > 1 && char.IsUpper(outText[1]);
            if (!secondIsUpper)
            {
                outText = char.ToLowerInvariant(outText[0]) + outText[1..];
            }
        }

        return outText;
    }

    /// <summary>
    /// Сколько знаков препинания в расшифровке. Только счётчик, без содержимого — по нему видно,
    /// пришло ли распознавание «сплошняком» (пунктуация = 0), то есть сработала ли затравка.
    /// </summary>
    public static int CountPunctuation(string text)
    {
        const string marks = ".,!?;:…—–";
        return string.IsNullOrEmpty(text) ? 0 : text.Count(marks.Contains);
    }
}
