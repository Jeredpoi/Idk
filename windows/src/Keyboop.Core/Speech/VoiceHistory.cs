using System.Text.Json;

namespace Keyboop.Core.Speech;

/// <summary>Одна запись истории.</summary>
public sealed record VoiceHistoryEntry(DateTime At, string Text);

/// <summary>
/// История распознанного.
///
/// ⚠️ СУЩЕСТВУЕТ НЕ РАДИ УДОБСТВА, А ЧТОБЫ ТЕКСТ НЕ ПРОПАДАЛ. Вставка может не состояться по
/// причинам, которые от человека не зависят: активно поле пароля, программа не принимает
/// синтетический ввод, окно закрылось за время распознавания. Без истории продиктованное в этот
/// момент исчезает совсем — человек говорил минуту, а получил всплывающую подсказку «не удалось».
///
/// ⚠️ И РОВНО ПОЭТОМУ ЕЙ НУЖЕН СРОК ЖИЗНИ. Это расшифровки чужой речи: переписка, пароли,
/// произнесённые вслух, разговоры о здоровье. Держать их вечно на диске — не бережливость,
/// а безответственность. По умолчанию храним час и не больше двухсот записей.
/// </summary>
public sealed class VoiceHistory
{
    private readonly string _path;
    private readonly List<VoiceHistoryEntry> _entries;

    /// <summary>Сколько записей держим максимум.</summary>
    public int MaxEntries { get; set; } = 200;

    /// <summary>Сколько времени живёт запись.</summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Инъектируемые часы — иначе срок жизни нечем проверить.</summary>
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public VoiceHistory(string path, IEnumerable<VoiceHistoryEntry>? initial = null)
    {
        _path = path;
        _entries = [.. initial ?? Read(path)];
    }

    /// <summary>Записи от новых к старым, уже без протухших.</summary>
    public IReadOnlyList<VoiceHistoryEntry> Entries
    {
        get
        {
            Prune();
            return _entries.AsReadOnly();
        }
    }

    public void Add(string text)
    {
        var clean = text.Trim();
        if (clean.Length == 0)
        {
            return;
        }

        _entries.Insert(0, new VoiceHistoryEntry(Clock(), clean));
        Prune();
        Save();
    }

    /// <summary>Стереть всё. Пункт «Очистить» в интерфейсе обязан быть — это личные данные.</summary>
    public void Clear()
    {
        _entries.Clear();
        Save();
    }

    private void Prune()
    {
        var cutoff = Clock() - Retention;
        _entries.RemoveAll(e => e.At < cutoff);

        if (_entries.Count > MaxEntries)
        {
            _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
        }
    }

    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(_path, JsonSerializer.Serialize(_entries, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // История — не та причина, по которой стоит ронять приложение.
        }
    }

    private static List<VoiceHistoryEntry> Read(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<List<VoiceHistoryEntry>>(File.ReadAllText(path), Json)
                       ?? [];
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Битый файл не должен мешать запуску.
        }

        return [];
    }
}
