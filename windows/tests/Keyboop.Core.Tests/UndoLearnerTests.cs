using Keyboop.Core.Layout;
using Xunit;

namespace Keyboop.Core.Tests;

public class UndoLearnerTests
{
    private DateTime _now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private (UndoLearner Undo, ExceptionStore Exceptions) Make()
    {
        var id = Guid.NewGuid().ToString("N");
        var exceptions = new ExceptionStore(
            Path.Combine(Path.GetTempPath(), $"keyboop-exc-{id}.json"), new ExceptionData());

        var undo = new UndoLearner(
            Path.Combine(Path.GetTempPath(), $"keyboop-undo-{id}.json"),
            exceptions,
            new UndoLearnerData())
        {
            Clock = () => _now,
        };

        return (undo, exceptions);
    }

    /// <summary>Ручной ре-флип, точно отменяющий нашу правку, — канонический жест «не надо так».</summary>
    [Fact]
    public void CountsManualReflipAsUndo()
    {
        var (undo, _) = Make();

        undo.NoteConversion("ghbdtn", "привет");
        undo.NoteManualConvert("привет", "ghbdtn");

        Assert.Equal(1, undo.StrikeCount("ghbdtn"));
    }

    /// <summary>Ре-флип другого слова к нашей правке отношения не имеет.</summary>
    [Fact]
    public void IgnoresReflipOfAnUnrelatedWord()
    {
        var (undo, _) = Make();

        undo.NoteConversion("ghbdtn", "привет");
        undo.NoteManualConvert("hello", "руддщ");

        Assert.Equal(0, undo.StrikeCount("ghbdtn"));
    }

    /// <summary>Стереть наш вывод целиком и набрать оригинал заново — второй честный жест.</summary>
    [Fact]
    public void CountsDeleteAndRetypeAsUndo()
    {
        var (undo, _) = Make();
        undo.NoteConversion("ghbdtn", "привет");

        // Стираем «привет» по букве.
        foreach (var prefix in new[] { "приве", "прив", "при", "пр", "п", "" })
        {
            undo.Observe(prefix);
        }

        // И набираем оригинал заново.
        Assert.False(undo.Observe("g"));
        Assert.False(undo.Observe("gh"));
        Assert.False(undo.Observe("ghbdt"));
        Assert.True(undo.Observe("ghbdtn"));

        Assert.Equal(1, undo.StrikeCount("ghbdtn"));
    }

    /// <summary>Продолжил печатать поверх нашей правки — значит, принял её.</summary>
    [Fact]
    public void TypingOnKeepsTheConversion()
    {
        var (undo, _) = Make();
        undo.NoteConversion("ghbdtn", "привет");

        undo.Observe("приветы");
        undo.Observe(string.Empty);
        undo.Observe("ghbdtn");

        Assert.Equal(0, undo.StrikeCount("ghbdtn"));
    }

    /// <summary>Возврат через минуту — уже не отмена, а обычная правка текста.</summary>
    [Fact]
    public void IgnoresUndoOutsideTheWindow()
    {
        var (undo, _) = Make();
        undo.NoteConversion("ghbdtn", "привет");

        _now = _now.AddSeconds(30);
        undo.NoteManualConvert("привет", "ghbdtn");

        Assert.Equal(0, undo.StrikeCount("ghbdtn"));
    }

    /// <summary>
    /// Порог, а не первый же откат: случайная отмена бывает у всех, и заносить слово по ней
    /// значило бы тихо разучиться чинить то, что чинить надо.
    /// </summary>
    [Fact]
    public void LearnsOnlyAfterThreeUndos()
    {
        var (undo, exceptions) = Make();
        var learned = new List<string>();
        undo.Learned += learned.Add;

        for (var i = 0; i < 2; i++)
        {
            undo.NoteConversion("ghbdtn", "привет");
            undo.NoteManualConvert("привет", "ghbdtn");
        }

        Assert.Empty(learned);
        Assert.DoesNotContain("ghbdtn", exceptions.Learned);

        undo.NoteConversion("ghbdtn", "привет");
        undo.NoteManualConvert("привет", "ghbdtn");

        Assert.Single(learned);
        Assert.Equal("ghbdtn", learned[0]);
        Assert.Contains("ghbdtn", exceptions.Learned);
    }

    /// <summary>Слово, которое давно не откатывали, начинает счёт заново.</summary>
    [Fact]
    public void StrikesDecayOverTime()
    {
        var (undo, exceptions) = Make();

        undo.NoteConversion("ghbdtn", "привет");
        undo.NoteManualConvert("привет", "ghbdtn");
        Assert.Equal(1, undo.StrikeCount("ghbdtn"));

        _now = _now.AddDays(60);

        undo.NoteConversion("ghbdtn", "привет");
        undo.NoteManualConvert("привет", "ghbdtn");

        Assert.Equal(1, undo.StrikeCount("ghbdtn"));
        Assert.DoesNotContain("ghbdtn", exceptions.Learned);
    }

    /// <summary>Любой откат сразу защищает слово в этом контексте, не дожидаясь порога.</summary>
    [Fact]
    public void ProtectsTheWordImmediatelyAfterAnyUndo()
    {
        var (undo, _) = Make();

        undo.NoteConversion("ghbdtn", "привет");
        undo.NoteManualConvert("привет", "ghbdtn");

        Assert.True(undo.IsProtected("ghbdtn"));
        Assert.True(undo.IsProtected("GHBDTN"));
    }

    /// <summary>Пока человек восстанавливает оригинал, конверсию глушим — иначе выйдет драка.</summary>
    [Fact]
    public void SuppressesWhileTheOriginalIsBeingRebuilt()
    {
        var (undo, _) = Make();
        undo.NoteConversion("ghbdtn", "привет");
        undo.Observe(string.Empty);

        Assert.True(undo.ShouldSuppress("ghb"));
        Assert.False(undo.ShouldSuppress("ghbdtn"));   // уже собрал — глушить больше нечего
        Assert.False(undo.ShouldSuppress("xyz"));      // это не оригинал
    }

    [Fact]
    public void ContextResetClearsProtection()
    {
        var (undo, _) = Make();

        undo.Protect("привет");
        Assert.True(undo.IsProtected("привет"));

        undo.ResetContext();
        Assert.False(undo.IsProtected("привет"));
    }

    /// <summary>
    /// Защита «тронул вручную — не трогай повторно» это базовая корректность и от тумблера
    /// обучения не зависит.
    /// </summary>
    [Fact]
    public void ProtectionWorksEvenWhenLearningIsOff()
    {
        var (undo, _) = Make();
        undo.Enabled = false;

        undo.Protect("привет");
        Assert.True(undo.IsProtected("привет"));
    }

    [Fact]
    public void DisabledLearnerDoesNotCountStrikes()
    {
        var (undo, exceptions) = Make();
        undo.Enabled = false;

        for (var i = 0; i < 5; i++)
        {
            undo.NoteConversion("ghbdtn", "привет");
            undo.NoteManualConvert("привет", "ghbdtn");
        }

        Assert.Equal(0, undo.StrikeCount("ghbdtn"));
        Assert.Empty(exceptions.Learned);
    }
}
