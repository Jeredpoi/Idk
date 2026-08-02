using Keyboop.Core;
using Xunit;

namespace Keyboop.Core.Tests;

/// <summary>
/// Полнота таблицы переводов. Пропущенная строка — не «мелочь оформления»: в интерфейсе она
/// выглядит как английская фраза посреди русского меню или как голый ключ вроде «tray.quit»
/// вместо названия пункта.
/// </summary>
[Collection(LanguageCollection.Name)]
public class L10nTests
{
    [Fact]
    public void EveryKeyHasBothLanguages()
    {
        foreach (var (key, value) in L10n.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(value.Ru), $"Нет русской строки для «{key}».");
            Assert.False(string.IsNullOrWhiteSpace(value.En), $"Нет английской строки для «{key}».");
        }
    }

    /// <summary>
    /// Совпадающие строки почти всегда означают забытый перевод. Исключения бывают (названия
    /// клавиш, имена собственные), но их надо перечислять явно, а не пропускать молча.
    /// </summary>
    [Fact]
    public void NoAccidentallyUntranslatedStrings()
    {
        foreach (var (key, value) in L10n.All)
        {
            Assert.False(value.Ru == value.En, $"Строка «{key}» одинакова в обоих языках.");
        }
    }

    /// <summary>
    /// Плейсхолдеры обязаны совпадать: строка с «{0}» в одном языке и без него в другом даёт либо
    /// потерянное слово, либо исключение форматирования прямо в диалоге.
    /// </summary>
    [Fact]
    public void PlaceholdersMatchAcrossLanguages()
    {
        foreach (var (key, value) in L10n.All)
        {
            Assert.Equal(CountPlaceholders(value.Ru), CountPlaceholders(value.En));
        }

        static int CountPlaceholders(string s)
        {
            var count = 0;
            for (var i = 0; i + 2 < s.Length; i++)
            {
                if (s[i] == '{' && char.IsDigit(s[i + 1]) && s[i + 2] == '}')
                {
                    count++;
                }
            }

            return count;
        }
    }

    [Theory]
    [InlineData("ru", "en-US", Lang.Ru)]
    [InlineData("en", "ru-RU", Lang.En)]
    [InlineData("auto", "ru-RU", Lang.Ru)]
    [InlineData("auto", "en-GB", Lang.En)]
    [InlineData("auto", null, Lang.En)]
    [InlineData(null, "ru", Lang.Ru)]
    public void SelectsTheRightLanguage(string? preference, string? system, Lang expected)
    {
        L10n.Select(preference, system);
        Assert.Equal(expected, L10n.Current);
    }

    /// <summary>Неизвестный ключ возвращается как есть — пропуск виден сразу, а не «пустеет».</summary>
    [Fact]
    public void UnknownKeyComesBackUnchanged()
    {
        Assert.Equal("нет.такого.ключа", L10n.T("нет.такого.ключа"));
    }

    [Fact]
    public void FormatsArguments()
    {
        L10n.Select("ru", null);
        Assert.Contains("«привет»", L10n.T("notice.learned", "привет"));
    }
}
