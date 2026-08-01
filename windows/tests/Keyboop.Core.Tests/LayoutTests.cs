using Keyboop.Core.Layout;
using Xunit;

namespace Keyboop.Core.Tests;

public class KeymapTests
{
    [Theory]
    [InlineData("ghbdtn", "привет")]
    [InlineData("rfr ltkf", "как дела")]
    [InlineData("Ghbdtn", "Привет")]
    public void ConvertsLatinToCyrillic(string typed, string expected)
    {
        Assert.Equal(expected, Keymap.Convert(typed, toCyrillic: true));
    }

    [Theory]
    [InlineData("привет", "ghbdtn")]
    [InlineData("Привет", "Ghbdtn")]
    public void ConvertsCyrillicToLatin(string typed, string expected)
    {
        Assert.Equal(expected, Keymap.Convert(typed, toCyrillic: false));
    }

    [Fact]
    public void ConversionRoundTrips()
    {
        const string typed = "ghbdtn rfr ltkf";
        var there = Keymap.Convert(typed, toCyrillic: true);
        Assert.Equal(typed, Keymap.Convert(there, toCyrillic: false));
    }

    /// <summary>Знаки препинания в конце слова — это знаки, а не буквы «б», «ю», «ж».</summary>
    [Fact]
    public void KeepsTrailingPunctuation()
    {
        Assert.Equal("привет.", Keymap.SmartConvert("ghbdtn.", toCyrillic: true));
    }

    /// <summary>
    /// …но те же клавиши — И буквы. Если полная конверсия даёт словарное слово, символ был буквой.
    /// Без этой проверки «yj;» превращалось в «но;», то есть портилось.
    /// </summary>
    [Theory]
    [InlineData("yj;", "нож")]
    [InlineData("[kt,", "хлеб")]
    public void TreatsPunctuationKeysAsLettersWhenResultIsAWord(string typed, string expected)
    {
        var dictionary = new HashSet<string> { "нож", "хлеб" };
        Assert.Equal(expected, Keymap.SmartConvert(typed, toCyrillic: true, dictionary.Contains));
    }

    [Fact]
    public void WithoutDictionaryTrailingPunctuationIsStripped()
    {
        // Показывает, зачем нужен словарь-валидатор: без него «нож» не собирается.
        Assert.Equal("но;", Keymap.SmartConvert("yj;", toCyrillic: true));
    }

    [Theory]
    [InlineData("привет", "CYR")]
    [InlineData("hello", "LAT")]
    [InlineData("привtn", "MIX")]
    [InlineData("123", "—")]
    public void ClassifiesScript(string word, string expected)
    {
        Assert.Equal(expected, word.ScriptClass());
    }
}

public class KeystrokeBufferTests
{
    [Fact]
    public void CollectsCurrentWord()
    {
        var buffer = new KeystrokeBuffer();
        foreach (var c in "ghbdtn")
        {
            buffer.Append(c.ToString());
        }

        Assert.Equal("ghbdtn", buffer.CurrentWord);
    }

    [Fact]
    public void BoundaryMovesWordToCompleted()
    {
        var buffer = new KeystrokeBuffer();
        buffer.Append("ghbdtn");
        buffer.Boundary(" ");

        Assert.Equal(string.Empty, buffer.CurrentWord);
        Assert.Equal("ghbdtn", buffer.LastWord);
        Assert.Equal(" ", buffer.LastTail);
    }

    /// <summary>
    /// Backspace через границу слова возвращает завершённое слово обратно в набираемое.
    /// Раньше здесь стоял полный сброс, и на границе конвертировалось лишь дописанное окончание —
    /// пользователи описывали это как «переключается только окончание».
    /// </summary>
    [Fact]
    public void BackspaceThroughBoundaryRestoresWholeWord()
    {
        var buffer = new KeystrokeBuffer();
        buffer.Append("привет");
        buffer.Boundary(" ");

        buffer.Backspace();   // съедает пробел
        Assert.Equal("привет", buffer.LastWord);

        buffer.Backspace();   // входит в само слово
        Assert.Equal("приве", buffer.CurrentWord);
        Assert.Equal(string.Empty, buffer.LastWord);
    }

    [Fact]
    public void BackspaceClearingWordDropsStaleContext()
    {
        var buffer = new KeystrokeBuffer();
        buffer.Append("да");
        buffer.Boundary(" ");
        buffer.Append("не");

        buffer.Backspace();
        buffer.Backspace();

        Assert.Equal(string.Empty, buffer.CurrentWord);
        Assert.Equal(string.Empty, buffer.LastWord);
        Assert.Empty(buffer.SessionWords);
    }

    /// <summary>
    /// Авто-конверсия целится в ЗАВЕРШЁННОЕ слово: человек мог уже начать следующее, и без этого
    /// завершённое осиротело бы. Огрызок следующего уходит в хвост, потому что стирать надо от каретки.
    /// </summary>
    [Fact]
    public void CompletedOnlyTargetsFinishedWordAndCountsFromCaret()
    {
        var buffer = new KeystrokeBuffer();
        buffer.Append("ghbdtn");
        buffer.Boundary(" ");
        buffer.Append("x");

        var target = buffer.WordForConversion(completedOnly: true);

        Assert.NotNull(target);
        Assert.Equal("ghbdtn", target!.Value.Word);
        Assert.Equal(" x", target.Value.Tail);
        Assert.Equal("ghbdtn".Length + 2, target.Value.DeleteCount);
    }

    [Fact]
    public void ContextWordLooksAtThePrecedingWord()
    {
        var buffer = new KeystrokeBuffer();
        buffer.Append("привет");
        buffer.Boundary(" ");
        buffer.Append("yt");
        buffer.Boundary(" ");

        Assert.Equal("привет", buffer.ContextWord(forCurrent: false));
    }

    [Fact]
    public void GroupNeedsAtLeastTwoWords()
    {
        var buffer = new KeystrokeBuffer();
        buffer.Append("ghbdtn");
        buffer.Boundary(" ");

        Assert.Null(buffer.GroupForConversion());
    }

    /// <summary>Протухшая сессия: курсор мог уехать, печатать по старой модели нельзя.</summary>
    [Fact]
    public void GroupExpiresAfterIdlePeriod()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var buffer = new KeystrokeBuffer { Clock = () => now };

        buffer.Append("ghbdtn");
        buffer.Boundary(" ");
        buffer.Append("rfr");
        buffer.Boundary(" ");

        Assert.NotNull(buffer.GroupForConversion());

        now = now.AddSeconds(30);
        Assert.Null(buffer.GroupForConversion());
    }
}

public class AntiResonanceGuardTests
{
    [Fact]
    public void AllowsNormalConversions()
    {
        var guard = new AntiResonanceGuard();
        Assert.True(guard.Allow("ghbdtn", "привет"));
        Assert.True(guard.Allow("rfr", "как"));
    }

    /// <summary>
    /// Осцилляция: собираемся произвести форму, которую только что конвертировали прочь.
    /// В авто-режиме такого быть не должно — это петля, и её надо разорвать.
    /// </summary>
    [Fact]
    public void BlocksOscillationBackToPreviousOutput()
    {
        var guard = new AntiResonanceGuard();

        Assert.True(guard.Allow("ghbdtn", "привет"));
        Assert.False(guard.Allow("привет", "ghbdtn"));
    }

    [Fact]
    public void BlocksStormOfConversions()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var guard = new AntiResonanceGuard { Clock = () => now };

        for (var i = 0; i < 6; i++)
        {
            Assert.True(guard.Allow($"w{i}", $"c{i}"));
        }

        Assert.False(guard.Allow("w6", "c6"));
    }

    [Fact]
    public void FreezeExpiresOnItsOwn()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var guard = new AntiResonanceGuard { Clock = () => now };

        guard.Allow("ghbdtn", "привет");
        Assert.False(guard.Allow("привет", "ghbdtn"));
        Assert.True(guard.IsFrozen);

        now = now.AddSeconds(5);
        Assert.False(guard.IsFrozen);
        Assert.True(guard.Allow("rfr", "как"));
    }
}

/// <summary>Реальные словари и триграммы — загружаются один раз на весь класс тестов.</summary>
public sealed class LayoutDataFixture
{
    public LayoutDataFixture()
    {
        Data = LayoutData.LoadFrom(Path.Combine(AppContext.BaseDirectory, "data"));
    }

    public LayoutData Data { get; }
}

public class LayoutDetectorTests : IClassFixture<LayoutDataFixture>
{
    private readonly LayoutData _data;
    private readonly IExceptionStore _exceptions = EmptyExceptionStore.Instance;

    public LayoutDetectorTests(LayoutDataFixture fixture) => _data = fixture.Data;

    [Fact]
    public void LanguageDataIsPresent()
    {
        // Если этот тест красный, остальные бессмысленны: детектор без данных всегда молчит.
        Assert.True(_data.IsLoaded, "Не загружены словари и триграммы из папки data.");
        Assert.Contains("привет", _data.WordsRu);
        Assert.Contains("hello", _data.WordsEn);
    }

    [Theory]
    [InlineData("ghbdtn")]
    [InlineData("rfr")]
    [InlineData("ghbdtn.")]
    public void FixesGibberishTypedInWrongLayout(string typed)
    {
        var decision = LayoutDetector.Decide(typed, _data, _exceptions);
        Assert.True(decision.ShouldConvert, $"«{typed}» должно было переключиться");
        Assert.True(decision.ToCyrillic);
    }

    /// <summary>
    /// STRICT-GATE. Слово, валидное в языке, на котором набрано, не переключаем никогда — даже
    /// если его раскладочная пара тоже валидное слово. «her» не должно становиться «рук».
    /// </summary>
    [Theory]
    [InlineData("her")]
    [InlineData("hello")]
    [InlineData("world")]
    [InlineData("привет")]
    [InlineData("дела")]
    public void NeverTouchesAValidWordOfItsOwnLanguage(string word)
    {
        Assert.False(LayoutDetector.Decide(word, _data, _exceptions).ShouldConvert);
    }

    /// <summary>Бренды и сервисы: «вк» не должно превращаться в «dr».</summary>
    [Theory]
    [InlineData("вк")]
    [InlineData("тг")]
    [InlineData("мск")]
    public void KeepsCuratedBrands(string word)
    {
        Assert.False(LayoutDetector.Decide(word, _data, _exceptions).ShouldConvert);
    }

    /// <summary>Аббревиатуру, набранную осознанно, оставляем как есть.</summary>
    [Theory]
    [InlineData("sql")]
    [InlineData("http")]
    [InlineData("json")]
    public void KeepsDeliberateAbbreviations(string word)
    {
        Assert.False(LayoutDetector.Decide(word, _data, _exceptions).ShouldConvert);
    }

    /// <summary>Короткие цифро-токены не наш случай: «h2o» и «b2b» трогать нельзя.</summary>
    [Theory]
    [InlineData("h2o")]
    [InlineData("b2b")]
    [InlineData("i18n")]
    public void KeepsShortTokensWithDigits(string word)
    {
        Assert.False(LayoutDetector.Decide(word, _data, _exceptions).ShouldConvert);
    }

    /// <summary>Дефисные слова: «что-то» и «из-за» должны остаться нетронутыми.</summary>
    [Theory]
    [InlineData("что-то")]
    [InlineData("из-за")]
    [InlineData("по-русски")]
    public void KeepsHyphenatedRussianWords(string word)
    {
        Assert.False(LayoutDetector.Decide(word, _data, _exceptions).ShouldConvert);
    }

    /// <summary>
    /// Спасение смешанного слова — артефакт переключения посреди набора: начало починили,
    /// а хвост успел уехать в другой алфавит.
    /// </summary>
    [Fact]
    public void RescuesMixedWordProducedByOurOwnSwitch()
    {
        var decision = LayoutDetector.MixedRescue("привtn", _data);
        Assert.True(decision.ShouldConvert);
        Assert.True(decision.ToCyrillic);
    }

    /// <summary>
    /// Намеренный билингв не становится словом ни в одну сторону — значит, это не наш артефакт,
    /// и трогать его нельзя.
    /// </summary>
    [Theory]
    [InlineData("helloмир")]
    [InlineData("ноутбукmac")]
    public void DoesNotGuessOnDeliberateBilingualTokens(string word)
    {
        Assert.False(LayoutDetector.MixedRescue(word, _data).ShouldConvert);
    }

    /// <summary>Ведущие скобки и кавычки не должны мешать разбору: «(tckb» — это «(если».</summary>
    [Theory]
    [InlineData("(tckb", "tckb")]
    [InlineData("tckb)", "tckb")]
    [InlineData("«если»", "если")]
    public void StripsBracketsAndQuotesFromCore(string raw, string expected)
    {
        Assert.Equal(expected, LayoutDetector.LetterCore(raw));
    }

    /// <summary>Правка на лету судит строже: неоконченное слово трогаем только при явной каше.</summary>
    [Fact]
    public void LiveDecideKeepsValidWordsBeingTyped()
    {
        Assert.False(LayoutDetector.LiveDecide("привет", _data, _exceptions).ShouldConvert);
        Assert.False(LayoutDetector.LiveDecide("hello", _data, _exceptions).ShouldConvert);
    }

    /// <summary>
    /// Пользовательское исключение сильнее любой статистики: человек уже сказал, что это слово
    /// трогать не надо.
    /// </summary>
    [Fact]
    public void RespectsUserExceptions()
    {
        var store = new ExceptionStore(
            Path.Combine(Path.GetTempPath(), $"keyboop-test-{Guid.NewGuid():N}.json"),
            new ExceptionData { Ignored = ["ghbdtn"] });

        Assert.False(LayoutDetector.Decide("ghbdtn", _data, store).ShouldConvert);
    }
}
