namespace Keyboop.Core.Layout;

/// <summary>Решение детектора по одному слову.</summary>
public readonly record struct SwapDecision(bool ShouldConvert, bool ToCyrillic)
{
    /// <summary>Не трогать слово.</summary>
    public static SwapDecision Keep => new(false, false);

    /// <summary>Переключить слово в указанную сторону.</summary>
    public static SwapDecision Convert(bool toCyrillic) => new(true, toCyrillic);
}

/// <summary>
/// Язык предыдущего слова фразы — слабый приор для коротких неоднозначных слов.
///
/// Анализ macOS-версии показал, что ВСЕ промахи детектора — это слова в 2–3 буквы, и почти все
/// они словарные коллизии (yt↔не, in↔шт, th↔ер): обе формы валидны, и слово в вакууме нерешаемо.
/// Контекст фразы — единственный сигнал, который их разрешает.
/// </summary>
public enum ContextHint
{
    None,
    Cyrillic,
    Latin,
}

/// <summary>Пользовательские списки, на которые смотрит детектор.</summary>
public interface IExceptionStore
{
    /// <summary>Слова, которые никогда не переключаем.</summary>
    IReadOnlySet<string> Ignored { get; }

    /// <summary>Слова, выученные на отмене — ведут себя как <see cref="Ignored"/>.</summary>
    IReadOnlySet<string> Learned { get; }

    /// <summary>Слова, которые переключаем всегда.</summary>
    IReadOnlySet<string> ForceSwap { get; }
}

/// <summary>
/// Двусторонний детектор «слово набрано не в той раскладке».
/// Каскад: отсев → форс-списки → словарь → триграммная плаузибельность.
///
/// ⚠️ ГЛАВНЫЙ ПРИНЦИП (strict-gate): слово, валидное в языке, на котором набрано, не переключаем
/// НИКОГДА — даже если его раскладочная пара тоже валидное слово. Переключаем только кашу.
/// Иначе английское «her» превращалось бы в русское «рук», и это недопустимо.
/// </summary>
public static class LayoutDetector
{
    /// <summary>Насколько плаузибельнее должна быть другая раскладка, чтобы переключать.</summary>
    public const double Margin = 2.0;

    /// <summary>
    /// Порог «английская форма короткого кириллического фрагмента — не мусор».
    /// Отсекает nm (−12.6), cz (−11.7), tn (−11.9); пропускает it (−8.6), in (−6.5), the (−7.5).
    /// </summary>
    public const double ShortEnSwapFloor = -10.0;

    /// <summary>Для режима «на лету»: текущий язык должен быть практически невозможен.</summary>
    public const double LiveImpossible = -13.0;

    /// <summary>Для режима «на лету»: другой язык должен быть заметно лучше.</summary>
    public const double LiveMargin = 6.0;

    /// <summary>
    /// Однобуквенные русские предлоги и союзы, набранные в английской раскладке
    /// (f→а, d→в, b→и, r→к, j→о, e→у, z→я, c→с). Их латинский исходник не значимое слово.
    /// </summary>
    public static readonly IReadOnlySet<string> RuSingleLetter =
        new HashSet<string>(StringComparer.Ordinal) { "а", "в", "и", "к", "о", "с", "у", "я" };

    public static ContextHint HintOf(string? previousWord)
    {
        if (string.IsNullOrEmpty(previousWord))
        {
            return ContextHint.None;
        }

        if (previousWord.HasCyrillic())
        {
            return ContextHint.Cyrillic;
        }

        return previousWord.HasLatinLetter() ? ContextHint.Latin : ContextHint.None;
    }

    /// <summary>
    /// Символ — буква, ИЛИ его клавиша в другой раскладке даёт букву (х=[, ж=;, э=', ё=`, ъ=]).
    /// Без этого слова с х/ъ/ж/э/ё вообще не детектировались бы.
    /// </summary>
    public static bool IsLayoutLetter(char c)
    {
        if (char.IsLetter(c))
        {
            return true;
        }

        if (Keymap.EnToRu.TryGetValue(c, out var toRu) && char.IsLetter(toRu))
        {
            return true;
        }

        return Keymap.RuToEn.TryGetValue(c, out var toEn) && char.IsLetter(toEn);
    }

    /// <summary>
    /// Буквенное ядро: срезаем ведущие и концевые скобки, кавычки, тире и пунктуацию —
    /// «(tckb» → «tckb», «„если"» → «если». Цифры не срезаем, чтобы gj1/h2o/b2b остались нетронутыми.
    /// Сама конверсия идёт по полному токену: скобка проходит через таблицу без изменений.
    /// </summary>
    public static string LetterCore(string raw)
    {
        var start = 0;
        var end = raw.Length;

        while (start < end && !IsLayoutLetter(raw[start]) && !char.IsDigit(raw[start]))
        {
            start++;
        }

        while (end > start && !IsLayoutLetter(raw[end - 1]) && !char.IsDigit(raw[end - 1]))
        {
            end--;
        }

        return raw[start..end];
    }

    /// <summary>
    /// Решение для режима «чинить на лету» (посреди слова). Срабатывает только когда сочетание букв
    /// в текущем языке практически невозможно, а в другом нормально. Без словаря-«авось»: слово ещё
    /// не дописано, и цена ошибки здесь выше, чем на границе слова.
    /// </summary>
    public static SwapDecision LiveDecide(string raw, LayoutData data, IExceptionStore exceptions)
    {
        var core = TrimDigits(LetterCore(raw));
        var w = core.ToLowerInvariant();

        if (w.Length < 4 || !w.All(IsLayoutLetter))
        {
            return SwapDecision.Keep;
        }

        var sourceCyrillic = w.HasCyrillic();
        var sourceLatin = w.HasLatinLetter();

        if (sourceCyrillic == sourceLatin)
        {
            return SwapDecision.Keep;
        }

        var toCyrillic = !sourceCyrillic;
        var swapped = Keymap.Convert(core, toCyrillic).ToLowerInvariant();

        if (swapped == w || !swapped.All(char.IsLetter))
        {
            return SwapDecision.Keep;
        }

        // Валидное слово текущего языка не трогаем.
        if (sourceLatin ? data.WordsEn.Contains(w) : data.WordsRu.Contains(w))
        {
            return SwapDecision.Keep;
        }

        if (IsExceptionOrPrefix(w, sourceCyrillic, exceptions))
        {
            return SwapDecision.Keep;
        }

        var original = data.Plausibility(w, sourceCyrillic);
        var swap = data.Plausibility(swapped, toCyrillic);

        return original <= LiveImpossible && swap > original + LiveMargin
            ? SwapDecision.Convert(toCyrillic)
            : SwapDecision.Keep;
    }

    /// <summary>
    /// Слово равно исключению ИЛИ является его префиксом. Только для правки на лету: пока человек
    /// дописывает слово-исключение, его префикс трогать нельзя, иначе выходит щёлканье
    /// «гифк» → латиница, «гифки» → обратно.
    /// </summary>
    public static bool IsExceptionOrPrefix(string w, bool cyrillic, IExceptionStore exceptions)
    {
        if (w.Length < 2)
        {
            return false;
        }

        bool Hit(IReadOnlySet<string> set) =>
            set.Contains(w) || set.Any(e => e.Length > w.Length && e.StartsWith(w, StringComparison.Ordinal));

        if (Hit(exceptions.Learned) || Hit(exceptions.Ignored) || Hit(ExtraWords.DefaultKeep))
        {
            return true;
        }

        return cyrillic
            ? Hit(ExtraWords.Ru) || Hit(ExtraWords.RuAbbr) || Hit(ExtraWords.RuShort)
            : Hit(ExtraWords.En) || Hit(ExtraWords.EnKeepShort);
    }

    /// <summary>
    /// Спасение смешанного слова (кириллица + латиница в одном токене). Такое слово — артефакт
    /// нашего же переключения посреди набора: начало успели починить, а дописанный хвост
    /// декодировался уже в другом алфавите («привtn», «приdет», «ghbdет»).
    ///
    /// Чиним по словарю, а не по сигнатуре артефакта: конвертим весь токен в обе стороны и
    /// принимаем ровно одну, если она даёт валидное слово. Если валидны обе или ни одной — не
    /// угадываем: намеренный билингв («API-ключ», «helloмир») не становится словом ни в одну
    /// сторону, и текст остаётся нетронутым.
    /// </summary>
    public static SwapDecision MixedRescue(string raw, LayoutData data)
    {
        if (!raw.HasCyrillic() || !raw.HasLatinLetter())
        {
            return SwapDecision.Keep;
        }

        var core = LetterCore(raw);
        if (core.Length < 2 || !core.All(IsLayoutLetter))
        {
            return SwapDecision.Keep;
        }

        var toRu = Keymap.Convert(core, toCyrillic: true);
        var toEn = Keymap.Convert(core, toCyrillic: false);

        var ruOk = !toRu.HasLatinLetter() && data.WordsRu.Contains(toRu.ToLowerInvariant());
        var enOk = !toEn.HasCyrillic() && data.WordsEn.Contains(toEn.ToLowerInvariant());

        if (ruOk && !enOk)
        {
            return SwapDecision.Convert(toCyrillic: true);
        }

        return enOk && !ruOk ? SwapDecision.Convert(toCyrillic: false) : SwapDecision.Keep;
    }

    /// <summary>
    /// Решение для авто-режима. Ручной хоткей сюда не заходит — там человек решил сам.
    /// <paramref name="previous"/> — предыдущее слово фразы, как оно выглядит на экране.
    /// </summary>
    public static SwapDecision Decide(
        string raw, LayoutData data, IExceptionStore exceptions, string? previous = null)
    {
        var context = HintOf(previous);
        var core = LetterCore(raw);

        // Слово с цифрами: берём буквенную часть и чиним только длинные однородные слова.
        // Короткие цифро-токены (gj1, h2o, b2b, i18n) обязаны остаться нетронутыми.
        var hadDigits = core.Any(char.IsDigit);
        if (hadDigits)
        {
            core = TrimDigits(core);
        }

        var w = core.ToLowerInvariant();

        // Дефисные термины конвертим целиком и строго по списку. Посегментный разбор упёрся бы
        // в краеугольный принцип: «у» в «у-штл» — валидный предлог, и авто-флипать его нельзя.
        if (w.Contains('-'))
        {
            var segments = w.Split('-');
            if (segments.Length >= 2
                && segments.All(s => s.Length > 0 && s.All(IsLayoutLetter))
                && w.HasCyrillic() != w.HasLatinLetter())
            {
                var toCyr = !w.HasCyrillic();
                var hyphenSwapped = Keymap.Convert(core, toCyr).ToLowerInvariant();
                if (ExtraWords.HyphenTerms.Contains(hyphenSwapped))
                {
                    return SwapDecision.Convert(toCyr);
                }
            }

            return SwapDecision.Keep;
        }

        if (w.Length < (hadDigits ? 4 : 1) || !w.All(IsLayoutLetter))
        {
            return SwapDecision.Keep;
        }

        if (exceptions.Ignored.Contains(w) || exceptions.Learned.Contains(w))
        {
            return SwapDecision.Keep;
        }

        // Бренды и сервисы — раньше словаря и контекста, жёстче любого статистического сигнала.
        if (ExtraWords.DefaultKeep.Contains(w))
        {
            return SwapDecision.Keep;
        }

        var sourceCyrillic = w.HasCyrillic();
        var sourceLatin = w.HasLatinLetter();

        if (sourceCyrillic == sourceLatin)
        {
            return SwapDecision.Keep;
        }

        var toCyrillic = !sourceCyrillic;
        var swapped = Keymap.Convert(core, toCyrillic).ToLowerInvariant();

        // Внутренний апостроф разрешён: английские контракции (i'm, don't) на русской раскладке
        // дают «э» внутри слова, и без послабления они отсекались бы здесь.
        if (swapped == w || !swapped.All(c => char.IsLetter(c) || c == '\''))
        {
            return SwapDecision.Keep;
        }

        var sourceIsRealWord = data.WordsRu.Contains(w) || data.WordsEn.Contains(w);

        // 1. Аббревиатуры. Встроенный список форсим ТОЛЬКО если исходник не настоящее слово:
        // иначе он съедал валидные русские слова, чья латинская форма совпала с аббревиатурой
        // («еды» → tls, «св» → cd, «шву» → ide). Пользовательский список — явное намерение.
        if (ExtraWords.AbbreviationForceSwap.Contains(swapped) && !sourceIsRealWord)
        {
            return SwapDecision.Convert(toCyrillic);
        }

        if (exceptions.ForceSwap.Contains(swapped))
        {
            return SwapDecision.Convert(toCyrillic);
        }

        if (ExtraWords.AbbreviationForceSwap.Contains(w) || exceptions.ForceSwap.Contains(w))
        {
            return SwapDecision.Keep;   // ввели «sql» осознанно — оставляем
        }

        // ★ STRICT-GATE. Стоит выше контекстной логики, чтобы «here» не ломалось и в русской фразе.
        // ForceRuAmb пропускаем сквозь гейт: это намеренный форс каши-токенов.
        if (w.Length >= 2 && sourceIsRealWord && !(sourceLatin && ExtraWords.ForceRuAmb.Contains(w)))
        {
            return SwapDecision.Keep;
        }

        // 2. Одиночные буквы.
        if (w.Length == 1)
        {
            return DecideSingleLetter(w, swapped, sourceLatin, sourceCyrillic, context, previous);
        }

        // Короткий кириллический фрагмент → латиница: словарное совпадение ничего не доказывает,
        // потому что EN-словарь полон двухбуквенного мусора, а русское окончание («ть», «ся»)
        // попадает на него случайно. Порог отсекает мусор и пропускает реальные слова.
        bool EnSwapNotJunk() =>
            w.Length >= 4 || data.Plausibility(swapped, cyrillic: false) > ShortEnSwapFloor;

        // 3. Словарь. Коллизии (обе формы валидны) разрешает контекст фразы.
        if (sourceLatin)
        {
            if (data.WordsEn.Contains(w) && !ExtraWords.ForceRuAmb.Contains(w))
            {
                if (context == ContextHint.Cyrillic
                    && data.WordsRu.Contains(swapped)
                    && !ExtraWords.EnKeepShort.Contains(w))
                {
                    return SwapDecision.Convert(toCyrillic: true);   // «привет yt» → «привет не»
                }

                return SwapDecision.Keep;
            }

            if (data.WordsRu.Contains(swapped))
            {
                // Частый английский токен вне словаря (vs, lol) при латинском контексте —
                // намеренный английский, а не каша.
                if (context == ContextHint.Latin && ExtraWords.EnKeepShort.Contains(w))
                {
                    return SwapDecision.Keep;
                }

                return SwapDecision.Convert(toCyrillic: true);
            }
        }
        else
        {
            // Зеркало: частый английский сленг, которого нет в EN-словаре, а кириллическая
            // форма — гиббериш.
            if (ExtraWords.ForceEnAmb.Contains(swapped) && !data.WordsRu.Contains(w))
            {
                return SwapDecision.Convert(toCyrillic: false);
            }

            if (data.WordsRu.Contains(w))
            {
                if (context == ContextHint.Latin && data.WordsEn.Contains(swapped))
                {
                    return SwapDecision.Convert(toCyrillic: false);   // «hello шт» → «hello in»
                }

                return SwapDecision.Keep;
            }

            if (data.WordsEn.Contains(swapped) && EnSwapNotJunk())
            {
                return SwapDecision.Convert(toCyrillic: false);
            }
        }

        // 4. Триграммы — только от четырёх букв. На коротких статистика ненадёжна и даёт
        // ложняки вида «тк» → «nr»: короткие слова переключаем строго по шагам 1–3.
        if (w.Length < 4)
        {
            return SwapDecision.Keep;
        }

        var originalScore = data.Plausibility(w, sourceCyrillic);
        var swapScore = data.Plausibility(swapped, toCyrillic);

        if (swapScore > originalScore + Margin)
        {
            return SwapDecision.Convert(toCyrillic);
        }

        // Оригинал — сплошной мусор, а перевод заметно лучше.
        if (originalScore <= -19.0 && swapScore > originalScore + 1.0)
        {
            return SwapDecision.Convert(toCyrillic);
        }

        return SwapDecision.Keep;
    }

    /// <summary>
    /// Одиночная буква. Тонкость с контекстом: после обычного слова («room d новой») предлог чиним,
    /// после слова-классификатора («vitamin d», «витамин d») буква — маркер, и трогать её нельзя.
    /// </summary>
    private static SwapDecision DecideSingleLetter(
        string w, string swapped, bool sourceLatin, bool sourceCyrillic,
        ContextHint context, string? previous)
    {
        if (sourceLatin && RuSingleLetter.Contains(swapped))
        {
            var prev = previous?.ToLowerInvariant();
            if (prev is not null)
            {
                if (context == ContextHint.Latin && ExtraWords.LabelClassifiers.Contains(prev))
                {
                    return SwapDecision.Keep;
                }

                if (context == ContextHint.Cyrillic && ExtraWords.RuLabelClassifiers.Contains(prev))
                {
                    return SwapDecision.Keep;
                }
            }

            return SwapDecision.Convert(toCyrillic: true);
        }

        // Одиночные английские i/u/a, набранные на русской раскладке (ш/г/ф — не русские слова).
        // В явно русской фразе не трогаем: там это скорее шум или опечатка.
        if (sourceCyrillic && context != ContextHint.Cyrillic
            && swapped is "i" or "u" or "a")
        {
            return SwapDecision.Convert(toCyrillic: false);
        }

        return SwapDecision.Keep;
    }

    private static string TrimDigits(string s)
    {
        var start = 0;
        var end = s.Length;

        while (start < end && char.IsDigit(s[start]))
        {
            start++;
        }

        while (end > start && char.IsDigit(s[end - 1]))
        {
            end--;
        }

        return s[start..end];
    }
}
