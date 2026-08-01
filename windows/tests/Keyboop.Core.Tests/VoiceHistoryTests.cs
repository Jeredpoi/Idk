using Keyboop.Core.Speech;
using Xunit;

namespace Keyboop.Core.Tests;

public class VoiceHistoryTests
{
    private DateTime _now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private VoiceHistory Make() =>
        new(Path.Combine(Path.GetTempPath(), $"keyboop-history-{Guid.NewGuid():N}.json"), [])
        {
            Clock = () => _now,
        };

    [Fact]
    public void KeepsNewestFirst()
    {
        var history = Make();

        history.Add("первое");
        _now = _now.AddSeconds(1);
        history.Add("второе");

        Assert.Equal("второе", history.Entries[0].Text);
        Assert.Equal("первое", history.Entries[1].Text);
    }

    [Fact]
    public void IgnoresEmptyText()
    {
        var history = Make();

        history.Add("   ");
        history.Add(string.Empty);

        Assert.Empty(history.Entries);
    }

    [Fact]
    public void TrimsSurroundingWhitespace()
    {
        var history = Make();
        history.Add("  привет  ");

        Assert.Equal("привет", history.Entries[0].Text);
    }

    /// <summary>
    /// Срок жизни — не мелочь оформления. Это расшифровки чужой речи, и держать их на диске
    /// дольше необходимого нельзя.
    /// </summary>
    [Fact]
    public void ForgetsEntriesOlderThanRetention()
    {
        var history = Make();
        history.Add("старое");

        _now = _now.AddHours(2);
        Assert.Empty(history.Entries);
    }

    [Fact]
    public void KeepsEntriesInsideRetention()
    {
        var history = Make();
        history.Add("свежее");

        _now = _now.AddMinutes(30);
        Assert.Single(history.Entries);
    }

    [Fact]
    public void CapsTheNumberOfEntries()
    {
        var history = Make();
        history.MaxEntries = 3;

        foreach (var i in Enumerable.Range(1, 10))
        {
            history.Add($"запись {i}");
        }

        Assert.Equal(3, history.Entries.Count);
        Assert.Equal("запись 10", history.Entries[0].Text);
    }

    [Fact]
    public void ClearRemovesEverything()
    {
        var history = Make();
        history.Add("что-то");

        history.Clear();
        Assert.Empty(history.Entries);
    }
}
