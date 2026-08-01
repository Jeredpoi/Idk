using System.Globalization;

namespace Keyboop.Core.Layout;

/// <summary>Завершённое слово вместе с хвостом пробелов после него.</summary>
public record struct WordEntry(string Word, string Tail);

/// <summary>Что именно конвертировать по запросу.</summary>
public record struct ConversionTarget(string Word, int DeleteCount, string Tail);

/// <summary>Группа слов для конвертации одним махом.</summary>
public record struct ConversionGroup(IReadOnlyList<WordEntry> Words, int DeleteCount);

/// <summary>
/// Локальный буфер набранного — то, чего приложение нам не отдаёт.
/// Храним текущее слово, последнее завершённое и хвост после него.
/// Сбрасывается на навигации, клике и смене окна, чтобы не чинить чужой текст.
/// </summary>
public sealed class KeystrokeBuffer
{
    private readonly List<WordEntry> _sessionWords = [];

    /// <summary>Защитный предел: не конвертировать гигантскую историю одним махом.</summary>
    private const int GroupMaxChars = 200;

    /// <summary>Групповая сессия протухает без активности — курсор мог уехать.</summary>
    private static readonly TimeSpan GroupMaxIdle = TimeSpan.FromSeconds(8);

    private DateTime _lastActivity = DateTime.UtcNow;

    /// <summary>Инъектируемые часы для тестов.</summary>
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    public string CurrentWord { get; private set; } = string.Empty;

    public string LastWord { get; private set; } = string.Empty;

    public string LastTail { get; private set; } = string.Empty;

    public IReadOnlyList<WordEntry> SessionWords => _sessionWords;

    public void Append(string s)
    {
        CurrentWord += s;
        _lastActivity = Clock();
    }

    public void Backspace()
    {
        _lastActivity = Clock();

        if (CurrentWord.Length > 0)
        {
            CurrentWord = CurrentWord[..^1];
            if (CurrentWord.Length == 0)
            {
                // Слово стёрто целиком — контекст предыдущего больше не достоверен, а групповая
                // история порвана через границу.
                LastWord = string.Empty;
                LastTail = string.Empty;
                _sessionWords.Clear();
            }

            return;
        }

        if (LastTail.Length > 0)
        {
            // Курсор стоит за завершённым словом: стираем его концевой пробел, само слово помним.
            LastTail = LastTail[..^1];
            if (_sessionWords.Count > 0)
            {
                _sessionWords[^1] = _sessionWords[^1] with { Tail = LastTail };
            }

            return;
        }

        if (LastWord.Length > 0)
        {
            // Хвост исчерпан, Backspace вошёл В само завершённое слово. Возвращаем его в
            // CurrentWord, чтобы дальнейшая правка шла по ВСЕМУ слову. Раньше здесь стоял полный
            // сброс, и на границе конвертировалось лишь дописанное окончание.
            CurrentWord = LastWord;
            if (_sessionWords.Count > 0)
            {
                _sessionWords.RemoveAt(_sessionWords.Count - 1);
            }

            LastWord = _sessionWords.Count > 0 ? _sessionWords[^1].Word : string.Empty;
            LastTail = _sessionWords.Count > 0 ? _sessionWords[^1].Tail : string.Empty;

            CurrentWord = CurrentWord[..^1];
            if (CurrentWord.Length == 0)
            {
                LastWord = string.Empty;
                LastTail = string.Empty;
                _sessionWords.Clear();
            }

            return;
        }

        // Правят что-то раньше набранного — безопаснее забыть контекст.
        Clear();
    }

    /// <summary>Завершение слова: пробел, таб или ввод.</summary>
    public void Boundary(string whitespace)
    {
        _lastActivity = Clock();

        if (CurrentWord.Length > 0)
        {
            _sessionWords.Add(new WordEntry(CurrentWord, whitespace));
            LastWord = CurrentWord;
            LastTail = whitespace;
            CurrentWord = string.Empty;
            return;
        }

        if (LastWord.Length > 0)
        {
            LastTail += whitespace;
            if (_sessionWords.Count > 0)
            {
                _sessionWords[^1] = _sessionWords[^1] with { Tail = _sessionWords[^1].Tail + whitespace };
            }
        }
    }

    public void Clear()
    {
        CurrentWord = string.Empty;
        LastWord = string.Empty;
        LastTail = string.Empty;
        _sessionWords.Clear();
    }

    /// <summary>
    /// Мягкий сброс: забываем завершённое слово и группу, но НЕ трогаем набираемое.
    /// Для случая «фокус мигнул» — каретка не двигалась. Полный сброс здесь «сиротил» окончание,
    /// и на границе конвертировалось только дописанное.
    /// </summary>
    public void SoftContextReset()
    {
        LastWord = string.Empty;
        LastTail = string.Empty;
        _sessionWords.Clear();
    }

    /// <summary>
    /// Сбросить только групповую историю. Зовётся, когда курсор мог сместиться или экран изменился
    /// не нашей группой (стрелки, клик, вставка) — чтобы группа не печатала по устаревшей модели.
    /// </summary>
    public void InvalidateGroupHistory() => _sessionWords.Clear();

    /// <summary>
    /// Группа для конвертации нескольких слов. null, если группа недействительна.
    /// Защиты: протухшая сессия, составные графемы (Backspace считает их иначе), перенос строки
    /// или таб в хвосте (на экране это не один символ), меньше двух слов, превышен предел длины.
    /// </summary>
    public ConversionGroup? GroupForConversion()
    {
        if (Clock() - _lastActivity > GroupMaxIdle)
        {
            return null;
        }

        var words = new List<WordEntry>(_sessionWords);
        if (CurrentWord.Length > 0)
        {
            words.Add(new WordEntry(CurrentWord, string.Empty));
        }

        if (words.Count < 2)
        {
            return null;
        }

        foreach (var (word, tail) in words)
        {
            if (VisualLength(word) != word.Length)
            {
                return null;   // составные графемы или суррогаты
            }

            if (tail.Contains('\n') || tail.Contains('\t'))
            {
                return null;
            }
        }

        var total = words.Sum(e => e.Word.Length + e.Tail.Length);
        return total > 0 && total <= GroupMaxChars
            ? new ConversionGroup(words, total)
            : null;
    }

    /// <summary>
    /// Что конвертировать: текущее слово без хвоста, иначе последнее завершённое с хвостом.
    ///
    /// <paramref name="completedOnly"/> целится строго в завершённое слово. Это нужно потому, что
    /// авто-конверсия стартует с задержкой, и человек успевает начать следующее слово: обычный
    /// порядок вернул бы его огрызок, а завершённое слово осиротело бы и молча не починилось.
    /// Огрызок уходит в хвост — замена считается от каретки, поэтому перепечатываем оба куска.
    /// </summary>
    public ConversionTarget? WordForConversion(bool completedOnly = false)
    {
        if (completedOnly)
        {
            if (LastWord.Length == 0)
            {
                return null;
            }

            return new ConversionTarget(
                LastWord,
                LastWord.Length + LastTail.Length + CurrentWord.Length,
                LastTail + CurrentWord);
        }

        if (CurrentWord.Length > 0)
        {
            return new ConversionTarget(CurrentWord, CurrentWord.Length, string.Empty);
        }

        if (LastWord.Length > 0)
        {
            return new ConversionTarget(LastWord, LastWord.Length + LastTail.Length, LastTail);
        }

        return null;
    }

    /// <summary>Пара к completedOnly: обновить завершённое слово, не трогая начатое следующее.</summary>
    public void ApplyCompletedConversion(string converted)
    {
        if (LastWord.Length == 0)
        {
            return;
        }

        LastWord = converted;
        if (_sessionWords.Count > 0)
        {
            _sessionWords[^1] = _sessionWords[^1] with { Word = converted };
        }
    }

    /// <summary>
    /// После замены синхронизируем состояние. Историю обновляем тоже: она обязана отражать ЭКРАН,
    /// а не оригинал набора — на этом же стоит контекстный приор детектора.
    /// </summary>
    public void ApplyConversion(string converted)
    {
        if (CurrentWord.Length > 0)
        {
            CurrentWord = converted;
            return;
        }

        if (LastWord.Length > 0)
        {
            LastWord = converted;
            if (_sessionWords.Count > 0)
            {
                _sessionWords[^1] = _sessionWords[^1] with { Word = converted };
            }
        }
    }

    /// <summary>
    /// Слово, предшествующее тому, что сейчас решает детектор, — как оно выглядит на экране.
    /// Для набираемого это последнее завершённое; для только что завершённого — предыдущее.
    /// </summary>
    public string? ContextWord(bool forCurrent)
    {
        if (forCurrent)
        {
            return _sessionWords.Count > 0 ? _sessionWords[^1].Word : null;
        }

        return _sessionWords.Count > 1 ? _sessionWords[^2].Word : null;
    }

    /// <summary>
    /// Сколько «символов на экране» в строке, то есть сколько нажатий Backspace её сотрут.
    /// Это НЕ длина строки: «é» из буквы и комбинирующего знака занимает две единицы UTF-16,
    /// но стирается одним нажатием.
    /// </summary>
    public static int VisualLength(string s) => new StringInfo(s).LengthInTextElements;
}
