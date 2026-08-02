using Keyboop.Core;
using Keyboop.Core.Speech;
using Xunit;

namespace Keyboop.Core.Tests;

/// <summary>
/// Каталог моделей. Проверяем не «оформление», а два свойства, от которых зависит безопасность:
/// адрес закреплён на неизменяемую ревизию, и у каждой модели есть контрольная сумма.
/// </summary>
[Collection(LanguageCollection.Name)]
public class ModelCatalogTests
{
    [Fact]
    public void EveryModelHasAChecksum()
    {
        foreach (var model in ModelCatalog.All)
        {
            Assert.Equal(64, model.Sha256.Length);
            Assert.True(
                model.Sha256.All(c => char.IsAsciiHexDigitLower(c)),
                $"Сумма модели «{model.Name}» не выглядит как SHA-256 в нижнем регистре.");
        }
    }

    /// <summary>
    /// Ссылка обязана указывать на КОММИТ, а не на ветку. Изменяемый адрес означает, что завтра
    /// по нему может лежать другой файл, и никакая проверка суммы уже не поможет — сумма зашита
    /// под конкретные байты.
    /// </summary>
    [Fact]
    public void UrlsArePinnedToAnImmutableRevision()
    {
        Assert.Equal(40, ModelCatalog.PinnedRevision.Length);

        foreach (var model in ModelCatalog.All)
        {
            var url = ModelCatalog.Url(model.Name);

            Assert.StartsWith("https://", url, StringComparison.Ordinal);
            Assert.Contains(ModelCatalog.PinnedRevision, url, StringComparison.Ordinal);
            Assert.DoesNotContain("/main/", url, StringComparison.Ordinal);
            Assert.EndsWith($"ggml-{model.Name}.bin", url, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ModelNamesAreUnique()
    {
        var names = ModelCatalog.All.Select(m => m.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EveryNoteHasATranslation()
    {
        foreach (var model in ModelCatalog.All)
        {
            Assert.True(L10n.All.ContainsKey(model.NoteKey),
                $"Для модели «{model.Name}» нет строки «{model.NoteKey}».");
        }
    }

    [Fact]
    public void SizesAreShownInTheInterfaceLanguage()
    {
        L10n.Select("ru", null);
        Assert.Equal("1,5 ГБ", ModelCatalog.LocalizedSize("1,5 ГБ"));

        L10n.Select("en", null);
        Assert.Equal("1.5 GB", ModelCatalog.LocalizedSize("1,5 ГБ"));
    }
}
