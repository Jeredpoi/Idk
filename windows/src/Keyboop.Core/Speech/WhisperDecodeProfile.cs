namespace Keyboop.Core.Speech;

/// <summary>
/// Параметры декодирования whisper, перенесённые из macOS-версии один в один.
///
/// Класс намеренно не зависит от Whisper.net: это чистое описание профиля, которое применяет
/// адаптер в Windows-слое. Так набор параметров можно проверить тестом, не поднимая нативную
/// библиотеку, и он не разъезжается между движками.
/// </summary>
public sealed class WhisperDecodeProfile
{
    /// <summary>«ru» / «en» / «auto».</summary>
    public string Language { get; init; } = "auto";

    /// <summary>
    /// Не тащить контекст между диктовками. Анти-залипание: иначе модель продолжает прошлую
    /// реплику. С затравкой совместимо — она присваивается ПОСЛЕ очистки контекста.
    /// </summary>
    public bool NoContext { get; init; } = true;

    /// <summary>
    /// Длинная диктовка (>30 с) обрабатывается несколькими окнами. Одно окно означало бы
    /// потерянный хвост речи.
    /// </summary>
    public bool SingleSegment { get; init; }

    /// <summary>Жадный проход по чистой речи.</summary>
    public float Temperature { get; init; }

    /// <summary>
    /// Fallback по температуре ВКЛЮЧЁН. На вырожденном сегменте (повтор/гиббериш) whisper.cpp
    /// передекодирует его на чуть большей температуре — это штатный механизм разрыва
    /// repetition-loop, когда модель повторяет одно слово десятки раз. Ноль здесь оставлял бы
    /// петли на месте.
    /// </summary>
    public float TemperatureIncrement { get; init; } = 0.2f;

    /// <summary>Порог энтропии для детекта вырожденного сегмента (дефолт whisper.cpp).</summary>
    public float EntropyThreshold { get; init; } = 2.4f;

    /// <summary>Порог «в сегменте нет речи».</summary>
    public float NoSpeechThreshold { get; init; } = 0.6f;

    /// <summary>
    /// Затравка на пунктуацию. Пустая строка означает «без затравки» (для тестов и отладки).
    /// </summary>
    public string Prompt { get; init; } = string.Empty;

    /// <summary>
    /// Переносить затравку в каждое окно. На диктовке длиннее 30 секунд без этого знаки
    /// пропадают начиная со второго окна. Для коротких реплик — no-op.
    /// </summary>
    public bool CarryInitialPrompt { get; init; } = true;

    /// <summary>
    /// Потоки декодера. Оставляем пару ядер системе, иначе на слабой машине диктовка
    /// подвешивает интерфейс.
    /// </summary>
    public int Threads { get; init; } = DefaultThreads();

    /// <summary>Профиль под конкретный язык — единственная точка сборки.</summary>
    public static WhisperDecodeProfile ForLanguage(string language)
    {
        return new WhisperDecodeProfile
        {
            Language = string.IsNullOrWhiteSpace(language) ? "auto" : language,
            Prompt = PunctuationPrompt.For(language),
        };
    }

    internal static int DefaultThreads()
    {
        return Math.Max(1, Math.Min(8, Environment.ProcessorCount - 2));
    }
}
