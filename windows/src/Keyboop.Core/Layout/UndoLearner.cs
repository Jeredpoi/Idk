using System.Text.Json;

namespace Keyboop.Core.Layout;

/// <summary>Что копится между запусками: счётчики откатов и отказы.</summary>
public sealed class UndoLearnerData
{
    public Dictionary<string, int> Strikes { get; set; } = [];

    public Dictionary<string, DateTime> LastStrike { get; set; } = [];
}

/// <summary>
/// Обучение на отмене: если человек раз за разом возвращает слово, которое мы «починили», значит
/// чинить его не надо.
///
/// ДВА ЧЕСТНЫХ ЖЕСТА ОТМЕНЫ, оба требуют ТОЧНЫЙ оригинал, поэтому ложных срабатываний почти нет:
///  • ручной ре-флип — мы сделали W→C, человек хоткеем вернул C обратно в W;
///  • стирание и перенабор — он стёр наш вывод целиком и набрал оригинал заново.
///
/// Системную отмену (Ctrl+Z) не детектируем: мы не видим результат текстовой операции и не можем
/// отличить её от любой другой правки.
///
/// ⚠️ ПОРОГ, А НЕ ПЕРВЫЙ ЖЕ ОТКАТ. Случайная отмена бывает у всех, и заносить слово в исключения
/// по ней означало бы тихо разучиться чинить то, что чинить надо. Считаем до трёх, и счётчик
/// затухает: слово, которое не откатывали месяц, начинает счёт заново.
/// </summary>
public sealed class UndoLearner
{
    private enum Stage
    {
        /// <summary>Только что сконвертировали, человек ещё ничего не сделал.</summary>
        Fresh,

        /// <summary>Стирает наш вывод по букве.</summary>
        Deleting,

        /// <summary>Стёр целиком и набирает заново.</summary>
        Retyping,
    }

    private sealed record Candidate(string Original, string Converted, DateTime CreatedAt, Stage Stage);

    /// <summary>Позже этого срока возврат — уже не отмена, а обычная правка текста.</summary>
    private static readonly TimeSpan UndoWindow = TimeSpan.FromSeconds(4);

    /// <summary>Сколько раз надо откатить одно слово, прежде чем занести его в исключения.</summary>
    private const int StrikeThreshold = 3;

    /// <summary>Давно не откатывали — счётчик обнуляем, случайные отмены не копятся вечно.</summary>
    private static readonly TimeSpan StrikeDecay = TimeSpan.FromDays(30);

    private readonly string _path;
    private readonly ExceptionStore _exceptions;
    private readonly Dictionary<string, int> _strikes;
    private readonly Dictionary<string, DateTime> _lastStrike;

    /// <summary>
    /// Слова, восстановленные в этом контексте набора. Не конвертируем их повторно, даже если до
    /// порога обучения далеко: пока человек рядом со словом, спорить с ним нельзя.
    /// Живёт только в памяти и чистится сменой контекста.
    /// </summary>
    private readonly HashSet<string> _sessionProtected = new(StringComparer.Ordinal);

    private Candidate? _candidate;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public UndoLearner(string path, ExceptionStore exceptions, UndoLearnerData? initial = null)
    {
        _path = path;
        _exceptions = exceptions;

        var data = initial ?? Read(path);
        _strikes = new Dictionary<string, int>(data.Strikes, StringComparer.Ordinal);
        _lastStrike = new Dictionary<string, DateTime>(data.LastStrike, StringComparer.Ordinal);
    }

    /// <summary>Инъектируемые часы — иначе поведение по времени нечем проверить.</summary>
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    /// <summary>Слово занесено в исключения — вызывающий говорит об этом человеку.</summary>
    public event Action<string>? Learned;

    public bool Enabled { get; set; } = true;

    /// <summary>Сколько раз слово откатывали (для тестов и диагностики).</summary>
    public int StrikeCount(string word) => _strikes.GetValueOrDefault(word.ToLowerInvariant(), 0);

    /// <summary>Мы сконвертировали слово — заводим кандидата на откат.</summary>
    public void NoteConversion(string original, string converted)
    {
        if (!Enabled)
        {
            _candidate = null;
            return;
        }

        var o = original.ToLowerInvariant();
        var c = converted.ToLowerInvariant();

        if (o == c || o.Length == 0 || c.Length == 0 || _exceptions.Learned.Contains(o))
        {
            _candidate = null;
            return;
        }

        _candidate = new Candidate(o, c, Clock(), Stage.Fresh);
    }

    /// <summary>
    /// Ручная конверсия: человек хоткеем превратил <paramref name="from"/> в <paramref name="to"/>.
    /// Если это точный откат нашей недавней правки — засчитываем.
    /// </summary>
    public bool NoteManualConvert(string from, string to)
    {
        var candidate = LiveCandidate();
        if (candidate is null)
        {
            return false;
        }

        if (from.ToLowerInvariant() != candidate.Converted || to.ToLowerInvariant() != candidate.Original)
        {
            return false;
        }

        _candidate = null;
        return RegisterUndo(candidate.Original);
    }

    /// <summary>
    /// Наблюдение за набором. Зовётся после каждого печатного символа и Backspace с текущим словом
    /// из буфера. Возвращает true, если откат подтверждён.
    /// </summary>
    public bool Observe(string currentWord)
    {
        var candidate = LiveCandidate();
        if (candidate is null)
        {
            return false;
        }

        var current = currentWord.ToLowerInvariant();

        if (candidate.Stage == Stage.Retyping)
        {
            if (current == candidate.Original)
            {
                _candidate = null;
                return RegisterUndo(candidate.Original);
            }

            // Ещё строит оригинал — ждём. Ушёл в сторону — это не откат.
            if (!candidate.Original.StartsWith(current, StringComparison.Ordinal))
            {
                _candidate = null;
            }

            return false;
        }

        if (current.Length == 0)
        {
            _candidate = candidate with { Stage = Stage.Retyping };   // наш вывод стёрт целиком
        }
        else if (current.Length < candidate.Converted.Length
                 && candidate.Converted.StartsWith(current, StringComparison.Ordinal))
        {
            _candidate = candidate with { Stage = Stage.Deleting };   // стирают по букве
        }
        else
        {
            _candidate = null;   // продолжил печатать — значит, конверсию принял
        }

        return false;
    }

    /// <summary>
    /// Человек прямо сейчас восстанавливает оригинал — конверсию глушим, чтобы не драться с ним
    /// на полпути.
    /// </summary>
    public bool ShouldSuppress(string currentWord)
    {
        var candidate = LiveCandidate();
        if (candidate is null || candidate.Stage != Stage.Retyping)
        {
            return false;
        }

        var current = currentWord.ToLowerInvariant();
        return current.Length > 0
            && current != candidate.Original
            && candidate.Original.StartsWith(current, StringComparison.Ordinal);
    }

    /// <summary>Слово тронули вручную в этом контексте — автоматике его не отдаём.</summary>
    public bool IsProtected(string word) => _sessionProtected.Contains(word.ToLowerInvariant());

    /// <summary>
    /// Защитить слово от немедленной повторной конверсии. Не зависит от того, включено ли
    /// обучение: «только что тронул вручную — не трогай повторно» это базовая корректность.
    /// </summary>
    public void Protect(string word)
    {
        var w = word.ToLowerInvariant();
        if (w.Length > 0)
        {
            _sessionProtected.Add(w);
        }
    }

    /// <summary>Смена контекста набора: клик, навигация, другое окно.</summary>
    public void ResetContext()
    {
        _candidate = null;
        _sessionProtected.Clear();
    }

    /// <summary>Засчитать откат. true — накоплен порог и слово занесено в исключения.</summary>
    private bool RegisterUndo(string word)
    {
        _sessionProtected.Add(word);

        if (!Enabled)
        {
            return false;
        }

        var now = Clock();

        if (_lastStrike.TryGetValue(word, out var last) && now - last > StrikeDecay)
        {
            _strikes[word] = 0;
        }

        var count = _strikes.GetValueOrDefault(word, 0) + 1;
        _strikes[word] = count;
        _lastStrike[word] = now;
        Save();

        if (count < StrikeThreshold || _exceptions.Learned.Contains(word))
        {
            return false;
        }

        // Порог достигнут. Слово переезжает в ВИДИМЫЙ и редактируемый список — человек может
        // убрать его в настройках, поэтому решение обратимо.
        //
        // ⚠️ Отличие от macOS-версии, где на этом месте показывают баннер с кнопками «Добавить» и
        // «Не надо». Здесь такого баннера нет, и городить модальное окно поверх чужой работы —
        // худший из вариантов: оно перехватит фокус ровно посреди набора. Порог в три отката это
        // и есть согласие, высказанное трижды; о результате сообщаем и оставляем возможность
        // отменить.
        _exceptions.AddLearned(word);
        _strikes.Remove(word);
        _lastStrike.Remove(word);
        Save();

        Learned?.Invoke(word);
        return true;
    }

    private Candidate? LiveCandidate()
    {
        if (_candidate is null)
        {
            return null;
        }

        if (Clock() - _candidate.CreatedAt > UndoWindow)
        {
            _candidate = null;
            return null;
        }

        return _candidate;
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

            var data = new UndoLearnerData { Strikes = _strikes, LastStrike = _lastStrike };
            File.WriteAllText(_path, JsonSerializer.Serialize(data, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Счётчики откатов — не та причина, по которой стоит ронять приложение.
        }
    }

    private static UndoLearnerData Read(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<UndoLearnerData>(File.ReadAllText(path), Json)
                       ?? new UndoLearnerData();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Битый файл не должен мешать запуску.
        }

        return new UndoLearnerData();
    }
}
