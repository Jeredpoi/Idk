using Keyboop.Core.Layout;
using Xunit;

namespace Keyboop.Core.Tests;

/// <summary>
/// Правка посреди слова. Тесты здесь важнее среднего: на живой машине эту функцию никто не
/// проверял, а ошибается она не «лишним переключением», а порчей набираемого слова.
/// </summary>
public class LiveFixTests : IClassFixture<LayoutDataFixture>
{
    private readonly LayoutData _data;
    private readonly IExceptionStore _exceptions = EmptyExceptionStore.Instance;

    public LiveFixTests(LayoutDataFixture fixture) => _data = fixture.Data;

    private LiveFixer Fixer() => new();

    [Fact]
    public void FixesWordTypedInTheWrongLayout()
    {
        var fixer = Fixer();

        // Человек набирает «привет», раскладка стоит английская. На экране «ghbdt», нажата «n».
        var plan = fixer.Plan("ghbdt", "n", _data, _exceptions);

        Assert.NotNull(plan);
        Assert.Equal("привет", plan!.Value.Word);
        Assert.True(plan.Value.ToCyrillic);
        Assert.False(plan.Value.IsHeal);

        // Стираем ровно то, что на экране: нажатую букву мы проглатываем и печатаем сами.
        Assert.Equal(5, plan.Value.DeleteCount);
        Assert.Equal("привет", plan.Value.Text);
    }

    /// <summary>
    /// Главный инвариант всей функции: на экране прибавляется ровно один символ, как при обычном
    /// наборе. Разойдись он — и групповые операции печатали бы по неверной модели.
    /// </summary>
    [Fact]
    public void ScreenGrowsByExactlyThePressedCharacter()
    {
        var fixer = Fixer();
        var plan = fixer.Plan("ghbdt", "n", _data, _exceptions);

        Assert.NotNull(plan);
        Assert.Equal(1, plan!.Value.Text.Length - plan.Value.DeleteCount);
    }

    [Fact]
    public void KeepsWordsShorterThanTheMinimum()
    {
        var fixer = Fixer();

        // Три символа — детектор такое не судит: цена ошибки посреди слова слишком велика.
        Assert.Null(fixer.Plan("gh", "b", _data, _exceptions));
    }

    [Fact]
    public void KeepsWordsLongerThanTheMaximum()
    {
        var fixer = Fixer();
        var tooLong = new string('g', LiveFixer.MaxLength);

        Assert.Null(fixer.Plan(tooLong, "h", _data, _exceptions));
    }

    /// <summary>Валидное английское слово переключать посреди набора нельзя ни при каких условиях.</summary>
    [Theory]
    [InlineData("hell", "o")]
    [InlineData("wor", "d")]
    [InlineData("compute", "r")]
    public void NeverTouchesValidEnglishWords(string onScreen, string pending)
    {
        var fixer = Fixer();
        Assert.Null(fixer.Plan(onScreen, pending, _data, _exceptions));
    }

    [Fact]
    public void DoesNotConvertTheSameResultTwice()
    {
        var fixer = Fixer();
        var first = fixer.Plan("ghbdt", "n", _data, _exceptions);
        Assert.NotNull(first);

        fixer.Applied(first!.Value);

        // Слово на экране теперь «привет». Повторно его трогать нечего.
        Assert.Null(fixer.Plan("приве", "т", _data, _exceptions));
    }

    /// <summary>
    /// Лечение собственного артефакта: мы починили начало и переключили раскладку, но следующие
    /// нажатия успели декодироваться ещё латиницей.
    /// </summary>
    [Fact]
    public void HealsItsOwnMixedWordArtifact()
    {
        var fixer = Fixer();
        var first = fixer.Plan("ghbdt", "n", _data, _exceptions);
        fixer.Applied(first!.Value);   // якорь = «привет»

        // Человек продолжает набирать «ствие», а первая буква пришла ещё латиницей.
        var heal = fixer.Plan("привет", "c", _data, _exceptions);

        Assert.NotNull(heal);
        Assert.True(heal!.Value.IsHeal);
        Assert.Equal("приветс", heal.Value.Word);
        Assert.Equal(0, heal.Value.DeleteCount);   // латинской буквы на экране ещё нет
        Assert.Equal("с", heal.Value.Text);
    }

    [Fact]
    public void HealsAMixedTailAlreadyOnScreen()
    {
        var fixer = Fixer();
        var first = fixer.Plan("ghbdt", "n", _data, _exceptions);
        fixer.Applied(first!.Value);

        // Два латинских символа успели попасть на экран, третий сейчас нажали.
        var heal = fixer.Plan("приветcn", "d", _data, _exceptions);

        Assert.NotNull(heal);
        Assert.True(heal!.Value.IsHeal);
        Assert.Equal("приветств", heal.Value.Word);
        Assert.Equal(2, heal.Value.DeleteCount);
        Assert.Equal("ств", heal.Value.Text);
    }

    /// <summary>
    /// Смешанное слово, которого мы не печатали, — это намеренный билингв. Трогать его нельзя:
    /// «API-ключ» и «Wi-Fiроутер» человек написал именно так, как хотел.
    /// </summary>
    [Fact]
    public void DoesNotHealTextItDidNotWrite()
    {
        var fixer = Fixer();   // якорь пуст — мы ничего не печатали
        Assert.Null(fixer.Plan("ключwi", "f", _data, _exceptions));
    }

    [Fact]
    public void AnchorIsForgottenOnReset()
    {
        var fixer = Fixer();
        var first = fixer.Plan("ghbdt", "n", _data, _exceptions);
        fixer.Applied(first!.Value);
        Assert.Equal("привет", fixer.Anchor);

        fixer.Reset();
        Assert.Equal(string.Empty, fixer.Anchor);

        // Без якоря лечение невозможно — это и есть защита чужого смешанного текста.
        Assert.Null(fixer.Plan("привет", "c", _data, _exceptions));
    }

    /// <summary>
    /// Составные графемы стираются одним Backspace, а в строке занимают две единицы. Считать
    /// удаление по длине строки в таком слове нельзя — значит, не трогаем вовсе.
    /// </summary>
    [Fact]
    public void RefusesWordsWithCombiningCharacters()
    {
        var fixer = Fixer();
        // «b» с комбинирующим акутом: одна графема на экране, две единицы в строке.
        Assert.Null(fixer.Plan("ghb\u0301dt", "n", _data, _exceptions));
    }

    [Fact]
    public void RefusesWhenDataIsNotLoaded()
    {
        var fixer = Fixer();
        var empty = LayoutData.LoadFrom(Path.Combine(Path.GetTempPath(), $"keyboop-none-{Guid.NewGuid():N}"));

        Assert.False(empty.IsLoaded);
        Assert.Null(fixer.Plan("ghbdt", "n", empty, _exceptions));
    }
}
