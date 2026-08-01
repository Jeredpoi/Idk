using Keyboop.Core.Speech;
using Xunit;

namespace Keyboop.Core.Tests;

/// <summary>
/// Тесты фильтра фраз-призраков. Половина из них — про то, что фильтр НЕ должен срабатывать:
/// в этом файле цена ложного срабатывания выше цены пропуска, потому что стирается живая речь.
/// </summary>
public class WhisperGhostsTests
{
    [Theory]
    [InlineData("Субтитры создавал DimaTorzok")]
    [InlineData("Субтитры сделал DimaTorzok.")]
    [InlineData("Редактор субтитров А.Синецкая")]
    [InlineData("Продолжение следует...")]
    [InlineData("Спасибо за просмотр!")]
    [InlineData("Подписывайтесь на канал")]
    [InlineData("Thanks for watching")]
    [InlineData("Subtitles by Amara.org community")]
    public void RemovesGhostWhenItIsTheWholeResult(string ghost)
    {
        Assert.Equal(string.Empty, WhisperGhosts.Clean(ghost));
    }

    [Fact]
    public void RemovesGhostGluedToTheEnd()
    {
        const string input = "Позвони мне завтра утром. Продолжение следует...";
        Assert.Equal("Позвони мне завтра утром.", WhisperGhosts.Clean(input));
    }

    [Fact]
    public void RemovesGhostGluedToTheStart()
    {
        const string input = "Субтитры создавал DimaTorzok. Позвони мне завтра утром.";
        Assert.Equal("Позвони мне завтра утром.", WhisperGhosts.Clean(input));
    }

    /// <summary>
    /// Главное правило файла: живую речь не трогаем. Эти фразы содержат слова шаблона, но
    /// шаблон требует, чтобы предложение совпадало ЦЕЛИКОМ.
    /// </summary>
    [Theory]
    [InlineData("Субтитры к фильму мы сделаем сами.")]
    [InlineData("Продолжение следует за вступлением, так устроена книга.")]
    [InlineData("Я хотел сказать спасибо за просмотр моей работы.")]
    public void KeepsLiveSpeechThatMerelyMentionsGhostWords(string text)
    {
        Assert.Equal(text, WhisperGhosts.Clean(text));
    }

    /// <summary>
    /// Резать по каждой точке нельзя: «А.Синецкая» распалась бы на «А.» и «Синецкая», и половина
    /// титров осталась бы в тексте. Граница предложения — знак плюс ПРОБЕЛ.
    /// </summary>
    [Fact]
    public void DoesNotSplitInitialsIntoSeparateSentences()
    {
        const string input = "Редактор субтитров А.Синецкая Корректор А.Егорова";
        Assert.Equal(string.Empty, WhisperGhosts.Clean(input));
    }

    /// <summary>
    /// После срезки не должно оставаться огрызков из одних знаков: «Продолжение следует...»
    /// раньше давало в остатке « . . », и это уезжало в поле как настоящий текст.
    /// </summary>
    [Fact]
    public void DoesNotLeavePunctuationOnlyFragments()
    {
        Assert.Equal(string.Empty, WhisperGhosts.Clean("Продолжение следует. . ."));
    }

    [Fact]
    public void ReturnsOriginalTextUntouchedWhenNothingWasRemoved()
    {
        const string input = "Первое предложение. Второе предложение.";
        Assert.Same(input, WhisperGhosts.Clean(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void HandlesEmptyInput(string input)
    {
        Assert.Equal(input, WhisperGhosts.Clean(input));
    }
}
