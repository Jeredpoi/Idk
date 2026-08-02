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

    /// <summary>
    /// Шифрование прозрачно для остального кода: что записали, то и прочли. Проверяем на
    /// обратимой подстановке — настоящий DPAPI на не-Windows машине не запустится, а свойство,
    /// которое здесь важно, от алгоритма не зависит.
    /// </summary>
    [Fact]
    public void SurvivesEncryptionRoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"keyboop-history-{Guid.NewGuid():N}.bin");
        var cipher = new ReversingCipher();

        var written = new VoiceHistory(path, [], cipher) { Clock = () => _now };
        written.Add("продиктованное");

        var read = new VoiceHistory(path, null, cipher) { Clock = () => _now };

        Assert.Equal("продиктованное", read.Entries[0].Text);
    }

    /// <summary>
    /// Файл, который мы не можем расшифровать (чужой профиль, остаток от прежней версии без
    /// шифрования), не должен мешать запуску — и не должен остаться лежать. Второе важнее
    /// первого: именно так выглядит история, записанная когда-то открытым текстом.
    /// </summary>
    [Fact]
    public void UnreadableFileIsOverwrittenInsteadOfLeftBehind()
    {
        var path = Path.Combine(Path.GetTempPath(), $"keyboop-history-{Guid.NewGuid():N}.bin");
        File.WriteAllText(path, "[{\"At\":\"2026-01-01T12:00:00Z\",\"Text\":\"секрет\"}]");

        var history = new VoiceHistory(path, null, new FailingCipher());

        Assert.Empty(history.Entries);
        Assert.DoesNotContain("секрет", File.ReadAllText(path), StringComparison.Ordinal);
    }

    private sealed class ReversingCipher : IHistoryCipher
    {
        public byte[] Protect(byte[] plain) => plain.Reverse().ToArray();

        public byte[] Unprotect(byte[] cipher) => cipher.Reverse().ToArray();
    }

    private sealed class FailingCipher : IHistoryCipher
    {
        public byte[] Protect(byte[] plain) => plain;

        public byte[] Unprotect(byte[] cipher) =>
            throw new System.Security.Cryptography.CryptographicException("не наш файл");
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
