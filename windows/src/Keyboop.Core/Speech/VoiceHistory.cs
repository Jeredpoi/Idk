using System.Security.Cryptography;
using System.Text.Json;

namespace Keyboop.Core.Speech;

/// <summary>Одна запись истории.</summary>
public sealed record VoiceHistoryEntry(DateTime At, string Text);

/// <summary>
/// Шифрование истории на диске.
///
/// Отдельным интерфейсом, потому что настоящее шифрование здесь системное и переносимого
/// аналога у него нет: на Windows это DPAPI, привязанный к учётной записи. Ядро обязано
/// собираться и проверяться где угодно, поэтому реализация приходит снаружи.
/// </summary>
public interface IHistoryCipher
{
    byte[] Protect(byte[] plain);

    byte[] Unprotect(byte[] cipher);
}

/// <summary>
/// Пустышка: пишем как есть.
///
/// ⚠️ Годится ТОЛЬКО для тестов. Настоящее приложение обязано подставить системное шифрование:
/// это расшифровки чужой речи, и лежать открытым текстом им незачем.
/// </summary>
public sealed class PlainHistoryCipher : IHistoryCipher
{
    public static readonly PlainHistoryCipher Instance = new();

    public byte[] Protect(byte[] plain) => plain;

    public byte[] Unprotect(byte[] cipher) => cipher;
}

/// <summary>
/// История распознанного.
///
/// ⚠️ СУЩЕСТВУЕТ НЕ РАДИ УДОБСТВА, А ЧТОБЫ ТЕКСТ НЕ ПРОПАДАЛ. Вставка может не состояться по
/// причинам, которые от человека не зависят: активно поле пароля, программа не принимает
/// синтетический ввод, окно закрылось за время распознавания. Без истории продиктованное в этот
/// момент исчезает совсем — человек говорил минуту, а получил всплывающую подсказку «не удалось».
///
/// ⚠️ И РОВНО ПОЭТОМУ ЕЙ НУЖЕН СРОК ЖИЗНИ И ШИФРОВАНИЕ. Это расшифровки чужой речи: переписка,
/// пароли, произнесённые вслух, разговоры о здоровье. Держать их вечно и открытым текстом — не
/// бережливость, а безответственность. По умолчанию храним час, не больше двухсот записей, и
/// файл шифруем средствами системы.
/// </summary>
public sealed class VoiceHistory
{
    private readonly string _path;
    private readonly IHistoryCipher _cipher;
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

    public VoiceHistory(
        string path, IEnumerable<VoiceHistoryEntry>? initial = null, IHistoryCipher? cipher = null)
    {
        _path = path;
        _cipher = cipher ?? PlainHistoryCipher.Instance;
        _entries = [.. initial ?? Read()];

        // ⚠️ ФАЙЛ, КОТОРЫЙ НЕ ЧИТАЕТСЯ, ПЕРЕЗАПИСЫВАЕМ НЕМЕДЛЕННО. Именно так выглядит история,
        // оставшаяся от версии без шифрования: расшифровки лежат на диске открытым текстом, и
        // просто «начать с пустой» означало бы оставить их там навсегда — мы ведь больше в этот
        // файл не заглянем, а следующая запись случится неизвестно когда.
        if (_unreadable)
        {
            Save();
        }
    }

    /// <summary>Файл существовал, но прочесть его не удалось.</summary>
    private bool _unreadable;

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

            var plain = JsonSerializer.SerializeToUtf8Bytes(_entries, Json);
            File.WriteAllBytes(_path, _cipher.Protect(plain));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            // История — не та причина, по которой стоит ронять приложение.
        }
    }

    private List<VoiceHistoryEntry> Read()
    {
        try
        {
            if (File.Exists(_path))
            {
                var plain = _cipher.Unprotect(File.ReadAllBytes(_path));
                return JsonSerializer.Deserialize<List<VoiceHistoryEntry>>(plain, Json) ?? [];
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException
                                       or CryptographicException)
        {
            // Битый файл, чужой профиль или файл из прежней незашифрованной версии. Ни один из
            // случаев не повод мешать запуску — но и оставить его лежать нельзя (см. конструктор).
            _unreadable = true;
        }

        return [];
    }
}
