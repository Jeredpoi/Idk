namespace Keyboop.Core.Layout;

/// <summary>Что напечатать, чтобы починить слово посреди набора.</summary>
/// <param name="DeleteCount">Сколько символов стереть — считая ОТ КАРЕТКИ и только то, что уже на экране.</param>
/// <param name="Text">Что напечатать взамен. Уже включает символ проглоченной клавиши.</param>
/// <param name="Word">Каким слово станет на экране — этим значением обновляется буфер и якорь.</param>
/// <param name="ToCyrillic">В какую сторону переключать раскладку после замены.</param>
/// <param name="IsHeal">Правка нашего же артефакта (кириллический префикс плюс сырой латинский хвост).</param>
public readonly record struct LiveFixPlan(
    int DeleteCount, string Text, string Word, bool ToCyrillic, bool IsHeal);

/// <summary>
/// Правка раскладки ПОСРЕДИ СЛОВА, не дожидаясь пробела.
///
/// ⚠️ САМАЯ ОПАСНАЯ ФУНКЦИЯ ПРОЕКТА. В macOS-версии она породила больше отчётов об ошибках, чем
/// всё остальное вместе взятое: её значение по умолчанию меняли четыре раза за полтора месяца.
/// Причина одна — на границе слова ошибка выглядит как «программа поправила зря», а посреди слова
/// как «программа съела текст», потому что стирание и печать шли разными событиями и реальное
/// нажатие успевало лечь между ними.
///
/// На Windows этого класса ошибок нет по построению: <c>SendInput</c> доставляет весь пакет
/// (Backspace'ы плюс замена) атомарно относительно другого ввода. Поэтому здесь НЕТ ни таймера
/// паузы, ни событий-пустышек, ни «здоровья перехватчика» — всего того аппарата, который в
/// оригинале существует ровно затем, чтобы получить атомарность, которую Windows даёт даром.
/// Правка выполняется прямо в колбэке хука, на нажатии очередной буквы, а сама буква проглатывается
/// и печатается нами в том же пакете.
///
/// Класс сознательно ничего не знает про Windows и решает только ЧТО печатать. Гейты, которые
/// зависят от окружения (режим программы, зажатые модификаторы, обучение на отмене,
/// анти-резонанс), проверяет вызывающий — их нельзя выразить без системы.
/// </summary>
public sealed class LiveFixer
{
    /// <summary>
    /// Короче четырёх букв не судим: на такой длине детектор ошибается слишком часто, а цена
    /// ошибки посреди слова выше, чем на границе.
    /// </summary>
    public const int MinLength = 4;

    /// <summary>
    /// Верхний предел. Он не про качество решения, а про время: колбэк хука обязан вернуться
    /// быстро, а длинные слова прекрасно чинятся на границе слова, как и раньше.
    /// </summary>
    public const int MaxLength = 16;

    private string _anchor = string.Empty;

    /// <summary>
    /// Последнее, что мы сами напечатали посреди этого слова.
    ///
    /// Живёт РОВНО в пределах текущего слова и служит двум целям: не конвертировать один и тот же
    /// результат повторно и опознавать собственный артефакт при лечении смешанного слова. Как
    /// только слово завершилось, каретка уехала или человек начал править слово руками — якорь
    /// недействителен, и его обязан сбросить вызывающий.
    /// </summary>
    public string Anchor => _anchor;

    public void Reset() => _anchor = string.Empty;

    /// <summary>Запомнить, что план напечатан.</summary>
    public void Applied(LiveFixPlan plan) => _anchor = plan.Word;

    /// <summary>
    /// Решение для слова, которое сейчас набирается.
    /// </summary>
    /// <param name="onScreen">Слово, как оно уже лежит на экране.</param>
    /// <param name="pending">Символ нажатой клавиши — на экране его ЕЩЁ НЕТ, мы его проглатываем.</param>
    public LiveFixPlan? Plan(
        string onScreen, string pending, LayoutData data, IExceptionStore exceptions)
    {
        if (!data.IsLoaded)
        {
            return null;
        }

        var candidate = onScreen + pending;

        if (candidate.Length == 0 || candidate.Length > MaxLength)
        {
            return null;
        }

        // Составные графемы: Backspace стирает их не по одной кодовой единице, значит считать
        // удаление по длине строки нельзя. Такое слово просто не трогаем.
        if (KeystrokeBuffer.VisualLength(onScreen) != onScreen.Length)
        {
            return null;
        }

        return candidate.HasCyrillic() && candidate.HasLatinLetter()
            ? HealPlan(candidate, pending)
            : ConvertPlan(candidate, onScreen, data, exceptions);
    }

    /// <summary>
    /// Обычная конверсия: слово целиком набрано не в той раскладке.
    /// </summary>
    private LiveFixPlan? ConvertPlan(
        string candidate, string onScreen, LayoutData data, IExceptionStore exceptions)
    {
        if (candidate.Length < MinLength || candidate == _anchor)
        {
            return null;
        }

        var decision = LayoutDetector.LiveDecide(candidate, data, exceptions);
        if (!decision.ShouldConvert)
        {
            return null;
        }

        var converted = Keymap.SmartConvert(
            candidate, decision.ToCyrillic, w => data.WordsRu.Contains(w));

        // Инвариант длины. Конверсия посимвольная, поэтому расхождение означает, что мы чего-то
        // не понимаем про это слово, — а печатать вслепую посреди набора значит портить текст.
        if (converted == candidate || converted.Length != candidate.Length)
        {
            return null;
        }

        return new LiveFixPlan(onScreen.Length, converted, converted, decision.ToCyrillic, false);
    }

    /// <summary>
    /// Лечение собственного артефакта.
    ///
    /// Откуда он берётся: мы починили начало слова и переключили раскладку, но переключение доходит
    /// до системы не мгновенно, и следующие нажатия успевают декодироваться ещё латиницей. Выходит
    /// «приdtn» — кириллический префикс плюс сырой латинский хвост. Обычный детектор такие слова
    /// всегда оставляет как есть (в них есть оба алфавита, значит источник неочевиден), и без
    /// лечения слово так и застревало бы наполовину переключённым.
    ///
    /// ⚠️ Лечим ТОЛЬКО если префикс — ровно то, что мы сами только что напечатали. Иначе это
    /// намеренно смешанный текст («API-ключ», «Wi-Fiроутер»), и трогать его нельзя.
    /// </summary>
    private LiveFixPlan? HealPlan(string candidate, string pending)
    {
        var tailLength = TrailingLatinRun(candidate);
        if (tailLength == 0 || tailLength < pending.Length)
        {
            return null;
        }

        var tail = candidate[^tailLength..];
        var prefix = candidate[..^tailLength];

        if (prefix.Length == 0 || prefix.HasLatinLetter() || prefix != _anchor)
        {
            return null;
        }

        var converted = Keymap.Convert(tail, toCyrillic: true);

        if (converted == tail
            || converted.Length != tail.Length
            || converted.HasLatinLetter())
        {
            return null;
        }

        // Стираем только то, что реально на экране: символ нажатой клавиши туда ещё не дошёл.
        return new LiveFixPlan(tailLength - pending.Length, converted, prefix + converted, true, true);
    }

    /// <summary>Длина непрерывной латинской «хвостовой» части слова.</summary>
    private static int TrailingLatinRun(string word)
    {
        var count = 0;

        for (var i = word.Length - 1; i >= 0; i--)
        {
            if (word[i] is >= 'a' and <= 'z' or >= 'A' and <= 'Z')
            {
                count++;
                continue;
            }

            break;
        }

        return count;
    }
}
