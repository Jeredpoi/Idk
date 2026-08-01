using Keyboop.Core.Snippets;
using Xunit;

namespace Keyboop.Core.Tests;

public class SnippetStoreTests
{
    private static SnippetStore Make(params (string Trigger, string Expansion)[] pairs)
    {
        var dictionary = pairs.ToDictionary(p => p.Trigger, p => p.Expansion);
        var path = Path.Combine(Path.GetTempPath(), $"keyboop-snippets-{Guid.NewGuid():N}.json");
        return new SnippetStore(path, dictionary);
    }

    [Fact]
    public void ExpandsExactTrigger()
    {
        var store = Make(("адр", "Москва, Тверская 1"));
        Assert.Equal("Москва, Тверская 1", store.Expansion("адр"));
    }

    /// <summary>
    /// Главное свойство: сокращение срабатывает и когда человек забыл переключить раскладку.
    /// Отказать здесь значило бы сломать функцию ровно в той ситуации, ради которой существует
    /// вся программа.
    /// </summary>
    [Fact]
    public void ExpandsTriggerTypedInTheWrongLayout()
    {
        var store = Make(("адр", "Москва, Тверская 1"));
        Assert.Equal("Москва, Тверская 1", store.Expansion("flh"));
    }

    [Fact]
    public void ExpandsLatinTriggerTypedInCyrillic()
    {
        var store = Make(("sig", "С уважением, Иван"));
        Assert.Equal("С уважением, Иван", store.Expansion("ышп"));
    }

    [Theory]
    [InlineData("АДР")]
    [InlineData("Адр")]
    public void IgnoresCase(string typed)
    {
        var store = Make(("адр", "Москва"));
        Assert.Equal("Москва", store.Expansion(typed));
    }

    [Fact]
    public void ReturnsNullForUnknownWord()
    {
        var store = Make(("адр", "Москва"));
        Assert.Null(store.Expansion("привет"));
        Assert.Null(store.Expansion(string.Empty));
    }

    /// <summary>
    /// Раскрытие приходит из файла, который человек мог править руками или получить от коллеги.
    /// Управляющий символ через синтетический ввод повёл бы себя непредсказуемо.
    /// </summary>
    [Fact]
    public void StripsControlCharactersButKeepsLineBreaksAndTabs()
    {
        // \u0007 — звонок терминала. В раскрытии ему делать нечего, а через синтетический
        // ввод он мог бы быть истолкован приложением как команда.
        var store = Make(("подпись", "Иван\u0007Петров\nОтдел\tпродаж"));
        Assert.Equal("ИванПетров\nОтдел\tпродаж", store.Expansion("подпись"));
    }

    [Fact]
    public void ReplaceSwapsTheWholeList()
    {
        var store = Make(("старое", "значение"));
        store.Replace([new KeyValuePair<string, string>("новое", "другое")]);

        Assert.Null(store.Expansion("старое"));
        Assert.Equal("другое", store.Expansion("новое"));
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void ReplaceDropsIncompleteEntries()
    {
        var store = Make();
        store.Replace(
        [
            new KeyValuePair<string, string>("  ", "нет триггера"),
            new KeyValuePair<string, string>("есть", string.Empty),
            new KeyValuePair<string, string>("норм", "значение"),
        ]);

        Assert.Equal(1, store.Count);
        Assert.Equal("значение", store.Expansion("норм"));
    }
}
