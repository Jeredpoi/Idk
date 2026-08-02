namespace Keyboop.Core.Layout;

/// <summary>Латинский токен и русское слово, набираемые одними и теми же клавишами.</summary>
public readonly record struct AmbiguousPair(string En, string Ru);

/// <summary>Кто побеждает в спорной паре.</summary>
public enum PairChoice
{
    /// <summary>Ничего не фиксируем: решает обычная логика — словари и контекст фразы.</summary>
    Auto,

    /// <summary>Всегда английское слово.</summary>
    En,

    /// <summary>Всегда русское.</summary>
    Ru,
}

/// <summary>
/// СПОРНЫЕ ПАРЫ: латинский токен и русское слово, которые набираются ОДНИМИ И ТЕМИ ЖЕ клавишами,
/// и оба существуют в своём языке («vs» ↔ «мы»).
///
/// Именно здесь строгое правило «валидное слово не трогаем» обязано выбрать за человека — а
/// угадать за всех нельзя: кто-то пишет «versus» каждый день, кто-то — «мы» в каждом втором
/// предложении. Поэтому выбор отдаём человеку, а по умолчанию не фиксируем ничего.
///
/// ⚠️ Своего хранилища у списка НЕТ и не нужно: выбор пишется в <c>ExceptionStore.ForceSwap</c>,
/// который детектор проверяет ДО всех встроенных списков. Одна запись даёт сразу оба поведения:
/// «мы» в списке — набранное «vs» превратится в «мы», а набранное «мы» останется как есть.
/// Меньше кода — меньше расхождений.
///
/// Список перенесён из macOS-версии как есть. Там его составляли не из головы: 156 пар посчитали
/// по словарям приложения и прогнали настоящим детектором, оставив только те, где обе стороны
/// реально встречаются. Пары с мусорной стороной не показываются, чтобы список можно было
/// дочитать до конца.
/// </summary>
public static class AmbiguousPairs
{
    /// <summary>
    /// Порядок — по практической пользе, а не по алфавиту: сверху то, где русское слово частотное
    /// и промах заметен каждый день, снизу — редкие случаи. Так нужное находится сразу.
    /// </summary>
    public static readonly IReadOnlyList<AmbiguousPair> All =
    [
        new("vs", "мы"),        // пример, с которого всё началось
        new("here", "руку"),
        new("herb", "руки"),
        new("her", "рук"),
        new("dbl", "вид"),
        new("tt", "ее"),        // «ее» — это «её» без ё
        new("cj", "со"),
        new("rj", "ко"),
        new("ne", "ту"),
        new("dj", "во"),
        new("tim", "ешь"),
        new("leif", "душа"),
        new("lei", "душ"),
        new("verb", "муки"),
        new("inert", "штуке"),
        new("celt", "суде"),
        new("dyer", "внук"),
        new("buh", "игр"),
        new("lye", "дну"),
        new("neh", "тур"),
        new("vlf", "мда"),
        new("abu", "фиг"),

        // Сокращения: живые с обеих сторон.
        new("cv", "см"),
        new("rv", "км"),
        new("ru", "кг"),
        new("rd", "кв"),
        new("vu", "мг"),
        new("uh", "гр"),
        new("in", "шт"),
        new("nsc", "тыс"),
        new("lng", "дтп"),
        new("ids", "швы"),

        // Реже, но спрашивали.
        new("tv", "ем"),
        new("key", "лун"),
        new("keys", "луны"),
        new("ev", "ум"),
    ];

    /// <summary>
    /// Что выбрано сейчас. Читаем ТОЛЬКО явные записи человека: если их нет — это «по контексту»,
    /// и переключатель честно показывает именно это, а не результат наших внутренних списков.
    /// </summary>
    public static PairChoice ChoiceOf(AmbiguousPair pair, IExceptionStore exceptions)
    {
        if (exceptions.ForceSwap.Contains(pair.Ru))
        {
            return PairChoice.Ru;
        }

        return exceptions.ForceSwap.Contains(pair.En) ? PairChoice.En : PairChoice.Auto;
    }

    /// <summary>
    /// Записать выбор. Победившая сторона идёт в «переключать всегда», проигравшая оттуда
    /// убирается — иначе две записи спорили бы между собой на разных ветках каскада.
    /// «По контексту» снимает обе.
    /// </summary>
    public static void Choose(AmbiguousPair pair, PairChoice choice, ExceptionStore exceptions)
    {
        exceptions.RemoveForceSwap(pair.En);
        exceptions.RemoveForceSwap(pair.Ru);

        switch (choice)
        {
            case PairChoice.Ru:
                exceptions.AddForceSwap(pair.Ru);
                break;

            case PairChoice.En:
                exceptions.AddForceSwap(pair.En);
                break;
        }
    }
}
