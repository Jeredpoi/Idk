using System.Text.Json;
using System.Text.Json.Serialization;

namespace Keyboop.Core.Layout;

/// <summary>Сериализуемое содержимое списков исключений.</summary>
public sealed class ExceptionData
{
    /// <summary>Слова, которые никогда не переключаем.</summary>
    public List<string> Ignored { get; set; } = [];

    /// <summary>Слова, которые переключаем всегда.</summary>
    public List<string> ForceSwap { get; set; } = [];

    /// <summary>Слова, выученные на отмене. Отдельно от ручных — так это видно и обратимо.</summary>
    public List<string> Learned { get; set; } = [];

    /// <summary>Режим для программы: bundle/exe → «off» либо «soft».</summary>
    public Dictionary<string, string> AppModes { get; set; } = [];
}

/// <summary>
/// Пользовательские исключения в JSON-файле.
///
/// Все слова хранятся в нижнем регистре: детектор сверяет тоже в нижнем, и без нормализации
/// «ВК» и «вк» вели бы себя по-разному, чего человек не ожидает.
/// </summary>
public sealed class ExceptionStore : IExceptionStore
{
    private readonly string _path;
    private readonly HashSet<string> _ignored;
    private readonly HashSet<string> _forceSwap;
    private readonly HashSet<string> _learned;
    private readonly Dictionary<string, string> _appModes;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public ExceptionStore(string path, ExceptionData? initial = null)
    {
        _path = path;
        var data = initial ?? Read(path);

        _ignored = Normalize(data.Ignored);
        _forceSwap = Normalize(data.ForceSwap);
        _learned = Normalize(data.Learned);
        _appModes = new Dictionary<string, string>(data.AppModes, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlySet<string> Ignored => _ignored;

    public IReadOnlySet<string> ForceSwap => _forceSwap;

    public IReadOnlySet<string> Learned => _learned;

    /// <summary>
    /// Режим для программы: «off» — не трогать совсем, «soft» — мягко, «» — обычный.
    ///
    /// ⚠️ Метод НЕ должен называться AppMode: тогда он перекрывает одноимённый статический класс,
    /// и внутри этого файла AppMode.Off перестаёт компилироваться (CS0119). Ровно на это уже
    /// наступили.
    /// </summary>
    public string ModeFor(string appId) =>
        _appModes.TryGetValue(appId, out var mode) ? mode : string.Empty;

    public void SetAppMode(string appId, string mode)
    {
        if (string.IsNullOrEmpty(mode))
        {
            _appModes.Remove(appId);
        }
        else
        {
            _appModes[appId] = mode;
        }

        Save();
    }

    /// <summary>Добавить в «не переключать». Слово одновременно снимается с «переключать всегда».</summary>
    public void AddIgnored(string word)
    {
        var w = word.Trim().ToLowerInvariant();
        if (w.Length == 0)
        {
            return;
        }

        _ignored.Add(w);
        _forceSwap.Remove(w);
        Save();
    }

    /// <summary>Добавить в «переключать всегда». Слово снимается с «не переключать».</summary>
    public void AddForceSwap(string word)
    {
        var w = word.Trim().ToLowerInvariant();
        if (w.Length == 0)
        {
            return;
        }

        _forceSwap.Add(w);
        _ignored.Remove(w);
        Save();
    }

    public void RemoveIgnored(string word)
    {
        _ignored.Remove(word.Trim().ToLowerInvariant());
        Save();
    }

    public void RemoveForceSwap(string word)
    {
        _forceSwap.Remove(word.Trim().ToLowerInvariant());
        Save();
    }

    public void AddLearned(string word)
    {
        var w = word.Trim().ToLowerInvariant();
        if (w.Length == 0)
        {
            return;
        }

        _learned.Add(w);
        Save();
    }

    /// <summary>Пары «программа → режим» для показа в настройках.</summary>
    public IReadOnlyDictionary<string, string> AppModePairs() => _appModes;

    /// <summary>
    /// Заменить список «не переключать» целиком — из текстового поля настроек.
    /// Пустые строки и пробелы по краям отбрасываем: человек их набирает случайно.
    /// </summary>
    public void ReplaceIgnored(IEnumerable<string> words)
    {
        _ignored.Clear();
        foreach (var word in words)
        {
            var w = word.Trim().ToLowerInvariant();
            if (w.Length > 0)
            {
                _ignored.Add(w);
            }
        }

        Save();
    }

    /// <summary>
    /// Заменить список программ-исключений. Формат строки: «имя.exe=off» либо «имя.exe=soft».
    ///
    /// Строку с неизвестным режимом молча пропускаем, а не считаем за «off»: опечатка в режиме
    /// не должна тихо выключать программу там, где человек этого не просил.
    /// </summary>
    public void ReplaceAppModes(IEnumerable<string> lines)
    {
        _appModes.Clear();
        foreach (var line in lines)
        {
            var parts = line.Split('=', 2);
            if (parts.Length != 2)
            {
                continue;
            }

            var app = parts[0].Trim();
            var mode = parts[1].Trim().ToLowerInvariant();

            if (app.Length > 0 && (mode == AppMode.Off || mode == AppMode.Soft))
            {
                _appModes[app] = mode;
            }
        }

        Save();
    }

    public void Save()
    {
        var data = new ExceptionData
        {
            Ignored = _ignored.OrderBy(x => x, StringComparer.Ordinal).ToList(),
            ForceSwap = _forceSwap.OrderBy(x => x, StringComparer.Ordinal).ToList(),
            Learned = _learned.OrderBy(x => x, StringComparer.Ordinal).ToList(),
            AppModes = _appModes,
        };

        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(_path, JsonSerializer.Serialize(data, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Список исключений — не то, ради чего стоит ронять приложение.
        }
    }

    private static ExceptionData Read(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<ExceptionData>(File.ReadAllText(path), Json)
                       ?? new ExceptionData();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Битый файл не должен мешать запуску.
        }

        return new ExceptionData();
    }

    private static HashSet<string> Normalize(IEnumerable<string> words) =>
        new(words.Select(w => w.Trim().ToLowerInvariant()).Where(w => w.Length > 0),
            StringComparer.Ordinal);
}

/// <summary>Пустые списки — для тестов и для случая, когда файла ещё нет.</summary>
public sealed class EmptyExceptionStore : IExceptionStore
{
    public static readonly EmptyExceptionStore Instance = new();

    public IReadOnlySet<string> Ignored { get; } = new HashSet<string>();

    public IReadOnlySet<string> Learned { get; } = new HashSet<string>();

    public IReadOnlySet<string> ForceSwap { get; } = new HashSet<string>();
}
