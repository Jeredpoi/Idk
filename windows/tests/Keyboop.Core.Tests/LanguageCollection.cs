using Xunit;

namespace Keyboop.Core.Tests;

/// <summary>
/// Тесты, трогающие текущий язык интерфейса, обязаны идти по одному.
///
/// L10n.Current — глобальное состояние: xUnit по умолчанию гоняет разные классы параллельно,
/// и без этой коллекции один тест переключал бы язык под ногами у другого. Ловится такое плохо —
/// падение выглядит случайным.
/// </summary>
[CollectionDefinition(Name)]
public sealed class LanguageCollection
{
    public const string Name = "язык интерфейса";
}
