namespace Keyboop.Core.Speech;

/// <summary>
/// Затравка (initial prompt), которая удерживает whisper в «режиме С пунктуацией».
///
/// ПОЧЕМУ ЭТО ВООБЩЕ НУЖНО. whisper авторегрессионный: он продолжает то, что ему дали началом.
/// На ХОЛОДНОМ декоде (no_context = true, то есть без истории прошлой диктовки) он примерно в
/// трети случаев залипает в «no-punctuation mode» и выдаёт всю реплику сплошняком, без единого
/// знака. Это не дефект аудио: в телеметрии macOS-версии у всех таких случаев вероятность тишины
/// была 0.00, а длина роли не играла — сплошняк приходил и на 526 символов.
///
/// Лечение — показать модели пример нужного стиля. Затравка это натуральная фраза со знаками,
/// не несущая смысла, поэтому риск утечки её слов в расшифровку близок к нулю.
///
/// ПРАВИЛА, ПРОВЕРЕННЫЕ НА macOS-ВЕРСИИ (не ослаблять без замеров):
///  • затравка — НАТУРАЛЬНАЯ фраза, а не «мешок знаков» вида «., ? !». Короткие и атипичные
///    промпты — самый ненадёжный случай, они умеют искажать слова;
///  • затравка ПОД ЯЗЫК речи: русская затравка на английской речи тянет вывод к русскому
///    на пограничном аудио;
///  • БЕЗ ёлочек « » и без : ; — модель принимает ёлочки за маркер прямой речи и заворачивает
///    в них не-речь, а turbo-модели эти знаки всё равно теряют.
///
/// Ссылки: openai/whisper#194 · OpenAI cookbook whisper_prompting_guide.
/// </summary>
public static class PunctuationPrompt
{
    /// <summary>Русская затравка. Используется и для «auto» — см. <see cref="For"/>.</summary>
    public const string Russian =
        "Привет! Как дела? Сегодня хорошая погода, но, кажется, скоро пойдёт дождь. " +
        "Ну что ж, подождём — время ещё есть.";

    /// <summary>Английская затравка.</summary>
    public const string English =
        "Hi! How are you? The weather is nice today, but it looks like it might rain. " +
        "Well, let's wait — there's still time.";

    /// <summary>
    /// Затравка под язык распознавания. Для «auto» отдаём русскую: дефект пунктуации проявлялся
    /// именно на русском, а на чистом аудио языковой сдвиг от затравки минимален.
    /// </summary>
    public static string For(string? language)
    {
        return string.Equals(language, "en", StringComparison.OrdinalIgnoreCase)
            ? English
            : Russian;
    }

    /// <summary>
    /// Срезать эхо затравки. whisper изредка (на почти-тишине) «продолжает» затравку и отдаёт её
    /// началом расшифровки. Срезаем ТОЛЬКО точное ведущее совпадение — живая речь так дословно
    /// не начинается, поэтому ложных срабатываний тут не бывает.
    /// </summary>
    public static string StripEcho(string text, string prompt)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(prompt))
        {
            return text;
        }

        var trimmed = text.Trim();
        if (!trimmed.StartsWith(prompt, StringComparison.Ordinal))
        {
            return trimmed;
        }

        return trimmed[prompt.Length..].Trim();
    }
}
