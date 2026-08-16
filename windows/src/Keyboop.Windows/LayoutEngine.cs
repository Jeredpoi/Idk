using Keyboop.Core.Layout;
using Keyboop.Core.Snippets;
using Keyboop.Windows.Diagnostics;
using Keyboop.Windows.Interop;

namespace Keyboop.Windows;

/// <summary>
/// Исправление раскладки: следит за набором, на границе слова спрашивает детектор и, если слово
/// набрано не в той раскладке, перепечатывает его и переключает язык.
///
/// ⚠️ ГРАНИЦА ОТВЕТСТВЕННОСТИ. Всё, что решает, ЧТО делать, живёт в Keyboop.Core и покрыто
/// тестами. Здесь только то, что умеет одна Windows: прочитать клавишу, напечатать замену,
/// переключить раскладку. Держать эту границу важно — она единственная причина, по которой
/// логику детектора можно проверять без Windows вообще.
/// </summary>
internal sealed class LayoutEngine
{
    private const uint VK_BACK = 0x08;
    private const uint VK_TAB = 0x09;
    private const uint VK_RETURN = 0x0D;
    private const uint VK_ESCAPE = 0x1B;
    private const uint VK_SPACE = 0x20;
    private const uint VK_PRIOR = 0x21;   // PageUp
    private const uint VK_NEXT = 0x22;    // PageDown
    private const uint VK_END = 0x23;
    private const uint VK_HOME = 0x24;
    private const uint VK_LEFT = 0x25;
    private const uint VK_UP = 0x26;
    private const uint VK_RIGHT = 0x27;
    private const uint VK_DOWN = 0x28;
    private const uint VK_DELETE = 0x2E;

    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;     // Alt
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;

    private readonly KeystrokeBuffer _buffer = new();
    private readonly AntiResonanceGuard _antiResonance = new();
    private readonly LiveFixer _live = new();
    private readonly LayoutData _data;
    private readonly IExceptionStore _exceptions;
    private readonly ForegroundApp _foreground;
    private readonly SnippetStore _snippets;

    private readonly UndoLearner _undo;

    internal LayoutEngine(
        LayoutData data,
        IExceptionStore exceptions,
        ForegroundApp foreground,
        SnippetStore snippets,
        UndoLearner undo)
    {
        _data = data;
        _exceptions = exceptions;
        _foreground = foreground;
        _snippets = snippets;
        _undo = undo;
        _antiResonance.Logger = Log.Write;
    }

    /// <summary>Исправлять раскладку автоматически на границе слова.</summary>
    internal bool AutoEnabled { get; set; } = true;

    /// <summary>
    /// Чинить раскладку прямо посреди слова, не дожидаясь пробела.
    ///
    /// ⚠️ ПО УМОЛЧАНИЮ ВЫКЛЮЧЕНО, и это сознательное расхождение с macOS-версией, где включено.
    /// Там у функции полтора месяца обкатки на живых людях; здесь она не запускалась ни разу.
    /// Ошибка на границе слова читается как «поправило зря» — обидно, но текст цел. Ошибка
    /// посреди слова читается как «съело текст». Пока никто не проверил вторую на реальной машине,
    /// включать её за человека нельзя.
    /// </summary>
    internal bool LiveFixEnabled { get; set; }

    /// <summary>
    /// Человек правит слово руками (стирает и перенабирает середину). Пока правит — посреди слова
    /// не вмешиваемся вовсе: спорить с человеком в момент, когда он сам исправляет опечатку, —
    /// худшее, что программа может сделать. На границе слова обычная логика работает как всегда.
    /// </summary>
    private bool _wordEdited;

    /// <summary>Слово исправлено: сколько символов заменили и в какую сторону (для лога и трея).</summary>
    internal event Action<bool>? Converted;

    /// <summary>
    /// Нажатие клавиши. Вызывается из колбэка хука, поэтому здесь нет ничего дорогого:
    /// одно чтение раскладки активного окна, один <c>ToUnicodeEx</c> и работа с памятью.
    ///
    /// Возвращает true, если клавишу надо проглотить: так бывает ровно в одном случае — правка
    /// посреди слова напечатала эту букву сама, внутри общего атомарного пакета.
    /// </summary>
    internal bool OnKeyDown(uint virtualKey, uint scanCode)
    {
        Log.Trace($"клавиша vk=0x{virtualKey:X2}");

        // Ctrl или Alt означают сочетание, а не текст. Буфер после такого недостоверен.
        if (IsHeld(VK_CONTROL) || IsHeld(VK_MENU))
        {
            _buffer.Clear();
            _live.Reset();
            _wordEdited = false;
            return false;
        }

        switch (virtualKey)
        {
            case VK_BACK:
                _buffer.Backspace();
                _undo.Observe(_buffer.CurrentWord);

                // Стирание внутри слова — признак ручной правки. Якорь после него недействителен:
                // на экране уже не то, что мы печатали.
                _live.Reset();
                _wordEdited = true;
                return false;

            case VK_SPACE:
            case VK_TAB:
            case VK_RETURN:
                HandleBoundary(virtualKey);

                // Слово кончилось — и правка, и якорь начинаются с чистого листа.
                _live.Reset();
                _wordEdited = false;
                return false;

            case VK_LEFT or VK_RIGHT or VK_UP or VK_DOWN:
            case VK_HOME or VK_END or VK_PRIOR or VK_NEXT:
            case VK_ESCAPE or VK_DELETE:
                // Курсор уехал — дальше мы уже не знаем, что на экране.
                _buffer.Clear();
                _live.Reset();
                _wordEdited = false;
                return false;
        }

        var layout = KeyboardLayoutSwitcher.ForegroundLayout();
        Log.Trace($"раскладка окна hkl=0x{layout.ToInt64():X}");

        var chars = KeyDecoder.Decode(virtualKey, scanCode, layout);
        Log.Trace($"декодировано {chars.Length} симв.");

        if (!KeyDecoder.IsPrintable(chars))
        {
            return false;
        }

        // Правка на лету печатает эту букву сама — вместе с заменой, одним пакетом.
        if (TryLiveFix(chars))
        {
            return true;
        }

        _buffer.Append(chars);
        Log.Trace($"буфер {_buffer.CurrentWord.Length} симв.");

        // Стирание нашего вывода и перенабор оригинала — один из двух честных жестов отмены.
        _undo.Observe(_buffer.CurrentWord);
        Log.Trace("наблюдатель отката отработал");
        return false;
    }

    /// <summary>Клик мышью или смена окна: где каретка — мы больше не знаем.</summary>
    internal void ResetContext()
    {
        _buffer.Clear();
        _undo.ResetContext();
        _antiResonance.ResetHistory();
        _live.Reset();
        _wordEdited = false;
    }

    /// <summary>
    /// Ручное переключение по хоткею: направление определяем по содержимому слова, а не по
    /// детектору — человек уже решил сам.
    /// </summary>
    internal void ConvertManually()
    {
        // ⚠️ ГРУППА ТОЛЬКО ПРИ ВЫКЛЮЧЕННОЙ АВТОМАТИКЕ. При включённой авто чинит каждое слово
        // по отдельности и не обновляет историю сессии — та расходится с экраном, и групповая
        // печать шла бы по устаревшей модели, портя текст. При авто группа к тому же не нужна:
        // чинить обычно уже нечего.
        // Ручной хоткей рвёт слово: то, что мы печатали посреди него, больше не якорь.
        _live.Reset();

        if (!AutoEnabled && ConvertGroup())
        {
            return;
        }

        var target = _buffer.WordForConversion();
        if (target is null)
        {
            return;
        }

        var item = target.Value;
        var toCyrillic = item.Word.HasCyrillic()
            ? false
            : item.Word.HasLatinLetter() || !KeyboardLayoutSwitcher.ForegroundIsCyrillic();

        var converted = Keymap.Convert(item.Word, toCyrillic);
        if (converted == item.Word)
        {
            return;
        }

        // ⚠️ Ручной хоткей НЕ проверяет режим приложения — сознательно. Человек нажал клавишу сам,
        // глядя на конкретное слово; отказать ему здесь означало бы «программа меня не слушается».
        // Гейт по режиму существует ради АВТОМАТИКИ, которая срабатывает без спроса.
        Apply(item, converted, toCyrillic, completedOnly: false, reason: "хоткей");

        // Ре-флип, точно отменяющий нашу недавнюю правку, — это и есть жест «не надо так».
        _undo.NoteManualConvert(item.Word, converted);

        // Результат ручной правки защищаем: следующий пробел не должен вернуть всё назад.
        _undo.Protect(converted);
    }

    /// <summary>
    /// Попытка починить слово посреди набора. true — букву напечатали мы сами, и её нажатие
    /// вызывающий обязан проглотить.
    ///
    /// ⚠️ ПОРЯДОК ПРОВЕРОК ЗДЕСЬ — ЭТО И ЕСТЬ БЕЗОПАСНОСТЬ ФУНКЦИИ. Сначала отказываемся по
    /// дешёвым и грубым признакам (выключено, человек правит руками, зажат модификатор, программа
    /// в исключениях) и только потом трогаем словари. Каждая пропущенная проверка стоит не
    /// «лишнего срабатывания», а испорченного текста в чужом окне.
    /// </summary>
    private bool TryLiveFix(string pending)
    {
        if (!LiveFixEnabled || !AutoEnabled || !_data.IsLoaded || _wordEdited)
        {
            return false;
        }

        // ⚠️ ЗАЖАТЫЙ МОДИФИКАТОР — ОТКАЗ. Наши Backspace'ы уедут вместе с ним: Ctrl+Backspace
        // стирает слово целиком, Shift+Backspace в разных программах означает что угодно.
        // Заглавную букву человек набирает с зажатым Shift, то есть случай совершенно обычный.
        // «Обнулить» модификатор нельзя: программа читает ГЛОБАЛЬНОЕ состояние клавиатуры,
        // а не флаги нашего события. Единственный безопасный ход — не стрелять.
        if (IsHeld(VK_SHIFT) || IsHeld(VK_LWIN) || IsHeld(VK_RWIN))
        {
            return false;
        }

        // Посреди слова молчим и в мягком режиме тоже, не только в выключенных программах:
        // мягкий режим стоит на редакторах кода, а там синтетика внутри незаконченного
        // идентификатора мешает автодополнению сильнее, чем помогает раскладка.
        if (_foreground.Mode != AppMode.Normal)
        {
            return false;
        }

        var onScreen = _buffer.CurrentWord;
        var plan = _live.Plan(onScreen, pending, _data, _exceptions);

        if (plan is null)
        {
            return false;
        }

        var fix = plan.Value;
        var candidate = onScreen + pending;

        // Слово, которое человек только что восстановил сам, посреди набора трогать нельзя тем
        // более: он смотрит ровно на него.
        if (_undo.IsProtected(candidate) || _undo.ShouldSuppress(candidate))
        {
            return false;
        }

        if (!_antiResonance.Allow(candidate, fix.Word))
        {
            // Резонанс: рвём цикл и забываем контекст, иначе он раскрутится снова — а посреди
            // слова он раскручивается в темпе набора.
            _buffer.Clear();
            _live.Reset();
            return false;
        }

        if (!TextInjector.ReplaceText(fix.DeleteCount, fix.Text))
        {
            // Пакет не ушёл целиком. Слово могло остаться наполовину стёртым, поэтому наша модель
            // экрана больше недостоверна — честнее забыть её, чем печатать по ней дальше.
            Log.Write("правка на лету: система не приняла пакет — контекст сброшен");
            _buffer.Clear();
            _live.Reset();
            return false;
        }

        _buffer.ApplyConversion(fix.Word);
        _live.Applied(fix);

        if (!fix.IsHeal)
        {
            // Кандидат на откат: если человек сейчас вернёт слово обратно, мы это засчитаем.
            _undo.NoteConversion(candidate, fix.Word);
        }

        KeyboardLayoutSwitcher.Switch(fix.ToCyrillic);

        Log.Write($"правка на лету{(fix.IsHeal ? " (лечение)" : string.Empty)}: "
                  + $"{fix.DeleteCount} симв. {candidate.ScriptClass()} → {(fix.ToCyrillic ? "RU" : "EN")}");

        Converted?.Invoke(fix.ToCyrillic);
        return true;
    }

    private void HandleBoundary(uint virtualKey)
    {
        var whitespace = virtualKey switch
        {
            VK_TAB => "\t",
            VK_RETURN => "\n",
            _ => " ",
        };

        Log.Trace("граница слова");
        _buffer.Boundary(whitespace);

        if (!AutoEnabled || !_data.IsLoaded)
        {
            return;
        }

        // Целимся строго в ЗАВЕРШЁННОЕ слово: человек мог уже начать следующее, и без этого
        // завершённое осиротело бы и молча не починилось.
        var target = _buffer.WordForConversion(completedOnly: true);
        if (target is null)
        {
            return;
        }

        var item = target.Value;

        // ⚠️ РЕЖИМ ПРИЛОЖЕНИЯ — ПЕРВЫМ ДЕЛОМ. В терминале и видеоредакторе наш Backspace означает
        // совсем не «стереть символ»: он ломает введённую команду и удаляет клип на таймлинии.
        // Дешевле всего проверить это до любой работы над словом.
        var mode = _foreground.Mode;
        Log.Trace($"программа: {_foreground.Executable}, режим «{mode}»");

        if (mode == AppMode.Off)
        {
            return;
        }

        // ⚠️ СНИППЕТ ПРОВЕРЯЕМ ДО РАСКЛАДКИ. Если сокращение раскрылось, чинить в нём нечего —
        // на экране уже не то слово, которое набирали. Обратный порядок означал бы, что «flh»
        // сперва починится в «адр», а раскроется только со следующего раза.
        //
        // Мягкий режим сниппетам не помеха: он про осторожность в исправлении раскладки, а
        // раскрытие сокращения человек завёл сам и ждёт его везде, кроме полностью выключенных
        // программ (их отсекли выше).
        var expansion = _snippets.Expansion(item.Word);
        if (expansion is not null)
        {
            ExpandSnippet(item, expansion);
            return;
        }

        // Слово, которое человек только что поправил сам или прямо сейчас восстанавливает,
        // автоматика не трогает: спорить с ним посреди правки — худшее, что можно сделать.
        if (_undo.IsProtected(item.Word) || _undo.ShouldSuppress(item.Word))
        {
            return;
        }

        // Мягкий режим (редакторы кода): одиночные буквы и повторы — это переменные и флаги,
        // а не русские предлоги, набранные не в той раскладке.
        if (mode == AppMode.Soft && SoftModeFilter.ShouldSkip(item.Word))
        {
            return;
        }

        // Спасение смешанного слова идёт первым: обычный детектор такие слова всегда оставляет
        // как есть, и без этой ветки они застревали бы наполовину переключёнными.
        var decision = LayoutDetector.MixedRescue(item.Word, _data);
        if (!decision.ShouldConvert)
        {
            var previous = _buffer.ContextWord(forCurrent: false);
            decision = LayoutDetector.Decide(item.Word, _data, _exceptions, previous);
        }

        Log.Trace($"детектор: {(decision.ShouldConvert ? "конвертить" : "оставить")}");

        if (!decision.ShouldConvert)
        {
            return;
        }

        var converted = Keymap.SmartConvert(
            item.Word, decision.ToCyrillic, w => _data.WordsRu.Contains(w));

        if (converted == item.Word)
        {
            return;
        }

        if (!_antiResonance.Allow(item.Word, converted))
        {
            // Резонанс: цикл рвём и начинаем с чистого листа, иначе он раскрутится снова.
            _buffer.Clear();
            return;
        }

        // Кандидат на откат: если человек сейчас вернёт слово обратно, мы это засчитаем.
        _undo.NoteConversion(item.Word, converted);
        Apply(item, converted, decision.ToCyrillic, completedOnly: true, reason: "авто");
    }

    /// <summary>
    /// Переключить всю набранную фразу одним хоткеем.
    ///
    /// Слова разбираются ПООТДЕЛЬНОСТИ: валидные остаются как есть, чинятся только те, что
    /// детектор считает набранными не в той раскладке. Иначе «hello ghbdtn» превратилось бы
    /// в кашу целиком, вместо «hello привет».
    ///
    /// Возвращает true, если группа обработана — в том числе когда чинить оказалось нечего.
    /// Это НЕ ошибка и падать на одно-словную логику нельзя: она force-конвертировала бы
    /// последнее валидное слово в мусор.
    /// </summary>
    private bool ConvertGroup()
    {
        var group = _buffer.GroupForConversion();
        if (group is null)
        {
            return false;
        }

        var g = group.Value;
        var output = new System.Text.StringBuilder();
        var converted = 0;
        var lastToCyrillic = false;
        string? previous = null;

        foreach (var (word, tail) in g.Words)
        {
            var decision = LayoutDetector.Decide(word, _data, _exceptions, previous);

            if (decision.ShouldConvert)
            {
                var fixedWord = Keymap.SmartConvert(
                    word, decision.ToCyrillic, w => _data.WordsRu.Contains(w));

                output.Append(fixedWord).Append(tail);
                lastToCyrillic = decision.ToCyrillic;
                previous = fixedWord;
                converted++;
            }
            else
            {
                output.Append(word).Append(tail);
                previous = word;
            }
        }

        if (converted == 0)
        {
            Log.Write($"группа: все {g.Words.Count} слов(а) валидны — не трогаю");
            return true;
        }

        // Инвариант длины: конверсия посимвольная, значит напечатанное обязано совпасть с
        // удаляемым. Если разошлось — печатать вслепую нельзя, это порча текста.
        if (output.Length != g.DeleteCount)
        {
            Log.Write($"группа: длина {output.Length} ≠ {g.DeleteCount} — отказ");
            return true;
        }

        if (!TextInjector.ReplaceText(g.DeleteCount, output.ToString()))
        {
            Log.Write("группа: система не приняла пакет");
            return true;
        }

        _buffer.Clear();
        _undo.ResetContext();
        KeyboardLayoutSwitcher.Switch(lastToCyrillic);

        Log.Write($"группа: починено {converted} из {g.Words.Count} слов, "
                  + $"{g.DeleteCount} симв. → {(lastToCyrillic ? "RU" : "EN")}");

        Converted?.Invoke(lastToCyrillic);
        return true;
    }

    /// <summary>
    /// Раскрыть сокращение: стереть триггер вместе с хвостом и напечатать раскрытие с тем же
    /// хвостом. Клавишу-разделитель мы не глотаем — она уже дошла до приложения, и мы просто
    /// перепечатываем её сами в том же атомарном пакете.
    /// </summary>
    private void ExpandSnippet(ConversionTarget item, string expansion)
    {
        if (!TextInjector.ReplaceText(item.DeleteCount, expansion + item.Tail))
        {
            Log.Write("сниппет: система не приняла пакет — оставляю как набрано");
            return;
        }

        _buffer.ApplyCompletedConversion(expansion);

        // Длина на экране изменилась не один к одному, поэтому история слов сессии больше не
        // описывает экран — групповые операции по ней печатали бы вслепую.
        _buffer.InvalidateGroupHistory();

        Log.Write($"сниппет: {item.Word.Length} → {expansion.Length} симв.");
    }

    private void Apply(
        ConversionTarget item, string converted, bool toCyrillic, bool completedOnly, string reason)
    {
        // Печатаем замену ВМЕСТЕ с хвостом: удаление считается от каретки, а между словом и
        // кареткой уже лежит пробел (и, возможно, начало следующего слова).
        Log.Trace($"печатаю замену: −{item.DeleteCount} симв.");

        if (!TextInjector.ReplaceText(item.DeleteCount, converted + item.Tail))
        {
            Log.Write("замена: система не приняла пакет — слово оставлено как есть");
            return;
        }

        Log.Trace("замена напечатана");

        if (completedOnly)
        {
            _buffer.ApplyCompletedConversion(converted);
        }
        else
        {
            _buffer.ApplyConversion(converted);
        }

        KeyboardLayoutSwitcher.Switch(toCyrillic);
        Log.Trace("раскладка переключена");

        // В лог только измеримое: длина и направление, без содержимого слова.
        Log.Write($"convert-word({reason}): {item.DeleteCount} симв. "
                  + $"{item.Word.ScriptClass()} → {(toCyrillic ? "RU" : "EN")}");

        Converted?.Invoke(toCyrillic);
    }

    private static bool IsHeld(int virtualKey) =>
        (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;
}
