using Keyboop.Core.Layout;
using Xunit;

namespace Keyboop.Core.Tests;

public class AmbiguousPairsTests
{
    private static ExceptionStore Store() =>
        new(Path.Combine(Path.GetTempPath(), $"keyboop-pairs-{Guid.NewGuid():N}.json"));

    private static readonly AmbiguousPair Vs = new("vs", "мы");

    [Fact]
    public void DefaultsToContext()
    {
        Assert.Equal(PairChoice.Auto, AmbiguousPairs.ChoiceOf(Vs, Store()));
    }

    [Theory]
    [InlineData(PairChoice.Ru)]
    [InlineData(PairChoice.En)]
    public void RemembersTheChoice(PairChoice choice)
    {
        var store = Store();

        AmbiguousPairs.Choose(Vs, choice, store);

        Assert.Equal(choice, AmbiguousPairs.ChoiceOf(Vs, store));
    }

    /// <summary>
    /// Проигравшая сторона обязана уйти из списка. Останься там обе — записи спорили бы между
    /// собой на разных ветках каскада, и результат зависел бы от порядка проверок.
    /// </summary>
    [Fact]
    public void OnlyOneSideStaysForced()
    {
        var store = Store();

        AmbiguousPairs.Choose(Vs, PairChoice.En, store);
        AmbiguousPairs.Choose(Vs, PairChoice.Ru, store);

        Assert.Contains("мы", store.ForceSwap);
        Assert.DoesNotContain("vs", store.ForceSwap);
    }

    [Fact]
    public void ContextClearsBothSides()
    {
        var store = Store();

        AmbiguousPairs.Choose(Vs, PairChoice.Ru, store);
        AmbiguousPairs.Choose(Vs, PairChoice.Auto, store);

        Assert.Empty(store.ForceSwap);
        Assert.Equal(PairChoice.Auto, AmbiguousPairs.ChoiceOf(Vs, store));
    }

    /// <summary>
    /// Обе стороны пары обязаны быть настоящей раскладочной парой. Опечатка в таблице означала бы,
    /// что человек выбирает победителя для слов, которые вообще не связаны.
    /// </summary>
    [Fact]
    public void EverySideConvertsIntoTheOther()
    {
        foreach (var pair in AmbiguousPairs.All)
        {
            Assert.Equal(pair.Ru, Keymap.Convert(pair.En, toCyrillic: true));
            Assert.Equal(pair.En, Keymap.Convert(pair.Ru, toCyrillic: false));
        }
    }

    [Fact]
    public void NoDuplicates()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var pair in AmbiguousPairs.All)
        {
            Assert.True(seen.Add(pair.En), $"Латинская сторона «{pair.En}» встречается дважды.");
            Assert.True(seen.Add(pair.Ru), $"Русская сторона «{pair.Ru}» встречается дважды.");
        }
    }

    /// <summary>
    /// Выбор победителя снимает слово с «не переключать»: два противоположных указания на одно
    /// слово — это не настройка, а поломка.
    /// </summary>
    [Fact]
    public void ChoosingRemovesTheWordFromIgnored()
    {
        var store = Store();
        store.AddIgnored("мы");

        AmbiguousPairs.Choose(Vs, PairChoice.Ru, store);

        Assert.DoesNotContain("мы", store.Ignored);
    }
}
