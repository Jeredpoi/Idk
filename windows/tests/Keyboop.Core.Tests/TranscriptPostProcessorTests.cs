using Keyboop.Core.Speech;
using Xunit;

namespace Keyboop.Core.Tests;

public class TranscriptPostProcessorTests
{
    private static DictationOutputOptions Options(
        bool dropPeriod = false,
        bool dropCapital = false,
        bool trailingSpace = false,
        bool autoEnter = false) => new()
    {
        DropFinalPeriod = dropPeriod,
        DropLeadingCapital = dropCapital,
        TrailingSpace = trailingSpace,
        AutoEnter = autoEnter,
    };

    [Fact]
    public void DropsSingleFinalPeriod()
    {
        Assert.Equal("Привет", TranscriptPostProcessor.Apply("Привет.", Options(dropPeriod: true)));
    }

    /// <summary>Многоточие несёт смысл, его модель часто ставит на оборванной фразе.</summary>
    [Fact]
    public void KeepsEllipsis()
    {
        Assert.Equal("Привет...", TranscriptPostProcessor.Apply("Привет...", Options(dropPeriod: true)));
    }

    [Theory]
    [InlineData("Что?")]
    [InlineData("Стой!")]
    public void KeepsMeaningfulTerminators(string text)
    {
        Assert.Equal(text, TranscriptPostProcessor.Apply(text, Options(dropPeriod: true)));
    }

    [Fact]
    public void DropsLeadingCapital()
    {
        Assert.Equal("привет как дела",
            TranscriptPostProcessor.Apply("Привет как дела", Options(dropCapital: true)));
    }

    /// <summary>
    /// Аббревиатуры не трогаем: «МФЦ» → «мФЦ» было бы куда заметнее, чем незаменённая заглавная.
    /// </summary>
    [Theory]
    [InlineData("МФЦ работает до шести")]
    [InlineData("ГОСТ требует иного")]
    public void KeepsCapitalWhenSecondLetterIsAlsoUpper(string text)
    {
        Assert.Equal(text, TranscriptPostProcessor.Apply(text, Options(dropCapital: true)));
    }

    /// <summary>
    /// Порядок операций: сначала точка, потом регистр. Если поменять местами, строка «А.»
    /// после снятия точки осталась бы заглавной.
    /// </summary>
    [Fact]
    public void AppliesPeriodRemovalBeforeCaseChange()
    {
        var result = TranscriptPostProcessor.Apply("А.", Options(dropPeriod: true, dropCapital: true));
        Assert.Equal("а", result);
    }

    [Fact]
    public void AddsTrailingSpaceSoNextPhraseDoesNotStick()
    {
        Assert.Equal("привет ", TranscriptPostProcessor.Prepare("привет", Options(trailingSpace: true)));
    }

    /// <summary>
    /// Авто-Enter гасит хвостовой пробел: иначе сообщение уходит с пробелом на конце и это
    /// видно получателю. Саму настройку пробела при этом не трогаем.
    /// </summary>
    [Fact]
    public void SuppressesTrailingSpaceWhenAutoEnterIsOn()
    {
        Assert.Equal("привет",
            TranscriptPostProcessor.Prepare("привет", Options(trailingSpace: true, autoEnter: true)));
    }

    [Fact]
    public void PrepareStripsGhostsBeforeShaping()
    {
        var result = TranscriptPostProcessor.Prepare(
            "Позвони мне. Продолжение следует...",
            Options(trailingSpace: true));

        Assert.Equal("Позвони мне. ", result);
    }

    /// <summary>Пустой результат — сигнал «речи не было», вставлять нечего.</summary>
    [Fact]
    public void PrepareReturnsEmptyWhenOnlyGhostsWereRecognised()
    {
        Assert.Equal(string.Empty,
            TranscriptPostProcessor.Prepare("Продолжение следует...", Options(trailingSpace: true)));
    }

    [Fact]
    public void CountsPunctuationForDiagnostics()
    {
        Assert.Equal(0, TranscriptPostProcessor.CountPunctuation("привет как дела все хорошо"));
        Assert.Equal(3, TranscriptPostProcessor.CountPunctuation("Привет, как дела? Хорошо."));
    }
}

public class PunctuationPromptTests
{
    [Fact]
    public void UsesEnglishPromptForEnglishSpeech()
    {
        Assert.Equal(PunctuationPrompt.English, PunctuationPrompt.For("en"));
    }

    /// <summary>
    /// Для «auto» отдаём русскую затравку: дефект пунктуации проявлялся именно на русском,
    /// а на чистом аудио языковой сдвиг от затравки минимален.
    /// </summary>
    [Theory]
    [InlineData("ru")]
    [InlineData("auto")]
    [InlineData(null)]
    public void FallsBackToRussianPrompt(string? language)
    {
        Assert.Equal(PunctuationPrompt.Russian, PunctuationPrompt.For(language));
    }

    /// <summary>
    /// Затравка обязана содержать все ключевые знаки — иначе она не показывает модели тот стиль,
    /// ради которого существует.
    /// </summary>
    [Theory]
    [InlineData(PunctuationPrompt.Russian)]
    [InlineData(PunctuationPrompt.English)]
    public void PromptDemonstratesEveryKeyMark(string prompt)
    {
        Assert.Contains(",", prompt);
        Assert.Contains(".", prompt);
        Assert.Contains("!", prompt);
        Assert.Contains("?", prompt);
        Assert.Contains("—", prompt);
    }

    /// <summary>
    /// Ёлочки и двоеточие с точкой с запятой запрещены: модель принимает « » за маркер прямой
    /// речи и заворачивает в них не-речь, а turbo-модели эти знаки всё равно теряют.
    /// </summary>
    [Theory]
    [InlineData(PunctuationPrompt.Russian)]
    [InlineData(PunctuationPrompt.English)]
    public void PromptAvoidsMarksThatDestabiliseDecoding(string prompt)
    {
        Assert.DoesNotContain("«", prompt);
        Assert.DoesNotContain("»", prompt);
        Assert.DoesNotContain(":", prompt);
        Assert.DoesNotContain(";", prompt);
    }

    [Fact]
    public void StripsPromptEchoFromTranscript()
    {
        var text = PunctuationPrompt.Russian + " Позвони мне завтра.";
        Assert.Equal("Позвони мне завтра.", PunctuationPrompt.StripEcho(text, PunctuationPrompt.Russian));
    }

    [Fact]
    public void KeepsTranscriptThatMerelyStartsSimilarly()
    {
        const string text = "Привет! Как дела у тебя сегодня?";
        Assert.Equal(text, PunctuationPrompt.StripEcho(text, PunctuationPrompt.Russian));
    }
}
