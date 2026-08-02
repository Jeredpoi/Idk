namespace Keyboop.Core;

public enum Lang
{
    Ru,
    En,
}

/// <summary>
/// Язык интерфейса. Русский и английский.
///
/// ⚠️ Русский здесь ПЕРВИЧЕН, а английский — не подстрочник к нему. Программа написана
/// по-русски и звучит по-русски: коротко, без канцелярита и без «Пожалуйста, обратите внимание».
/// Английский текст пишется заново с той же интонацией, а не переводится дословно, иначе
/// англоязычный интерфейс читается как машинный перевод — что он, собственно, и есть.
///
/// Таблица лежит в ядре, а не в оконном коде, ровно по той же причине, что и детектор: так её
/// полноту можно проверить тестом на любой машине, не открывая ни одного окна. Пропущенный перевод
/// — не «мелочь оформления»: в интерфейсе он выглядит как английская фраза посреди русского меню.
/// </summary>
public static class L10n
{
    /// <summary>Текущий язык. Меняется в настройках; интерфейс пересобирается на месте.</summary>
    public static Lang Current { get; set; } = Lang.Ru;

    /// <summary>
    /// Выбрать язык по настройке. <paramref name="preference"/> — «ru», «en» или «auto»;
    /// при «auto» решает язык системы.
    /// </summary>
    public static void Select(string? preference, string? systemLanguage)
    {
        Current = preference?.ToLowerInvariant() switch
        {
            "ru" => Lang.Ru,
            "en" => Lang.En,
            _ => systemLanguage is not null
                 && systemLanguage.StartsWith("ru", StringComparison.OrdinalIgnoreCase)
                ? Lang.Ru
                : Lang.En,
        };
    }

    /// <summary>
    /// Строка по ключу. Неизвестный ключ возвращается как есть — так пропуск виден в интерфейсе
    /// сразу и не превращается в пустую кнопку.
    /// </summary>
    public static string T(string key) =>
        Table.TryGetValue(key, out var pair) ? (Current == Lang.Ru ? pair.Ru : pair.En) : key;

    public static string T(string key, params object?[] args) =>
        string.Format(T(key), args);

    /// <summary>Вся таблица целиком — для теста полноты.</summary>
    public static IReadOnlyDictionary<string, (string Ru, string En)> All => Table;

    private static readonly Dictionary<string, (string Ru, string En)> Table = new()
    {
        // ——— Меню в трее ———
        ["tray.settings"] = ("Настройки…", "Settings…"),
        ["tray.chooseModel"] = ("Выбрать модель…", "Choose a model…"),
        ["tray.voiceLanguage"] = ("Язык распознавания", "Speech language"),
        ["tray.uiLanguage"] = ("Язык интерфейса", "Interface language"),
        ["tray.pause"] = ("Приостановить", "Pause"),
        ["tray.autostart"] = ("Запускать при входе в систему", "Start at login"),
        ["tray.history"] = ("История диктовок…", "Dictation history…"),
        ["tray.log"] = ("Показать лог", "Show the log"),
        ["tray.quit"] = ("Выход", "Quit"),

        ["lang.auto"] = ("Определять сам", "Detect automatically"),

        // ——— Подписи значка ———
        ["state.paused"] = ("Keyboop — приостановлен", "Keyboop — paused"),
        ["state.recording"] = ("Keyboop — идёт запись", "Keyboop — recording"),
        ["state.processing"] = ("Keyboop — распознаю", "Keyboop — transcribing"),

        // ——— Переключатели, общие для меню и окна настроек ———
        ["opt.dropPeriod"] = ("Убирать точку в конце", "Drop the final period"),
        ["opt.dropCapital"] = ("Начинать со строчной буквы", "Start with a lowercase letter"),
        ["opt.trailingSpace"] = ("Добавлять пробел в конце", "Add a trailing space"),
        ["opt.autoEnter"] = ("Отправлять Enter сразу после текста", "Press Enter right after the text"),
        ["opt.autoFix"] = ("Исправлять раскладку автоматически на границе слова",
                           "Fix the layout automatically at the word boundary"),
        ["opt.liveFix"] = ("Чинить не дожидаясь пробела", "Fix without waiting for a space"),
        ["opt.sounds"] = ("Звуковые метки", "Sound cues"),
        ["opt.capsSwitch"] = ("Caps Lock переключает язык", "Caps Lock switches the language"),
        ["settings.capsHint"] = (
            "Caps Lock перестаёт включать верхний регистр и мгновенно меняет язык. "
            + "Замок и индикатор при этом не срабатывают вовсе.",
            "Caps Lock stops toggling upper case and switches the language instead. "
            + "The lock and its indicator never engage at all."),

        // ——— Сообщения ———
        ["notice.learned"] = ("Больше не переключаю «{0}». Убрать можно в настройках.",
                              "I won't switch “{0}” any more. You can undo this in settings."),
        ["notice.hookFailed"] = ("\n\nБез перехватчика клавиатуры хоткей диктовки работать не будет.",
                                 "\n\nWithout the keyboard hook the dictation hotkey will not work."),
        ["notice.modelFailed"] = ("Модель не загрузилась. Выберите файл заново в меню трея.",
                                  "The model didn't load. Pick the file again from the tray menu."),
        ["notice.modelLoaded"] = ("Модель загружена. Можно диктовать.",
                                  "Model loaded. You can dictate now."),
        ["notice.resumeFailed"] = ("Не удалось возобновить работу. Подробности в логе.",
                                   "Couldn't resume. The log has the details."),
        ["error.modelLoad"] = ("Не удалось загрузить модель:\n", "Couldn't load the model:\n"),

        ["voice.noModel"] = ("Модель распознавания не выбрана. Откройте настройки и укажите файл модели.",
                             "No speech model selected. Open settings and point Keyboop at a model file."),
        ["voice.modelLoading"] = ("Модель ещё загружается. Секунду.",
                                  "The model is still loading. One moment."),
        ["voice.recordFailed"] = ("Не удалось начать запись. Проверьте микрофон и разрешение на доступ к нему.",
                                  "Couldn't start recording. Check the microphone and its permission."),
        ["voice.silence"] = ("Микрофон молчал. Возможно, он занят другим приложением.",
                             "The microphone stayed silent. Another app may be holding it."),
        ["voice.empty"] = ("Речь не распознана.", "Nothing was recognised."),
        ["voice.insertFailed"] = ("Не удалось вставить текст. Он сохранён в истории диктовок.",
                                  "Couldn't insert the text. It's saved in the dictation history."),
        ["voice.failed"] = ("Распознавание не удалось. Подробности в логе.",
                            "Transcription failed. The log has the details."),

        // ——— Диалог выбора модели ———
        ["dialog.modelTitle"] = ("Файл модели whisper (ggml-*.bin)", "Whisper model file (ggml-*.bin)"),
        ["dialog.modelFilter"] = ("Модели whisper (*.bin)|*.bin|Все файлы (*.*)|*.*",
                                  "Whisper models (*.bin)|*.bin|All files (*.*)|*.*"),

        // ——— Окно настроек ———
        ["settings.title"] = ("Keyboop — настройки", "Keyboop — settings"),
        ["settings.save"] = ("Сохранить", "Save"),
        ["settings.close"] = ("Закрыть", "Close"),

        ["tab.hotkeys"] = ("Хоткеи", "Hotkeys"),
        ["tab.layout"] = ("Раскладка", "Layout"),
        ["tab.voice"] = ("Голос", "Voice"),
        ["tab.snippets"] = ("Сниппеты", "Snippets"),
        ["tab.general"] = ("Общие", "General"),
        ["settings.soundsHint"] = (
            "Короткие тоны: исправленная раскладка, начало и конец диктовки. Этот выключатель "
            + "гасит весь звук, который издаёт программа.",
            "Short tones: a fixed layout, the start and the end of a dictation. This switch "
            + "silences every sound the program makes."),

        ["settings.dictation"] = ("Диктовка", "Dictation"),
        ["settings.mode"] = ("Как работает", "How it works"),
        ["settings.convertWord"] = ("Переключить слово", "Switch a word"),
        ["mode.hold"] = ("Удерживать", "Hold"),
        ["mode.toggle"] = ("Переключать", "Toggle"),
        ["settings.hotkeyHint"] = (
            "Клавишу без модификаторов можно назначить только такую, которая ничего не "
            + "печатает: Pause, Insert, Scroll Lock или F1–F24.\n\n"
            + "Иначе символ пропадёт во всех программах — мы глотаем нажатие целиком, чтобы "
            + "оно не попало в текст.",
            "A hotkey without modifiers may only use a key that types nothing: "
            + "Pause, Insert, Scroll Lock or F1–F24.\n\n"
            + "Otherwise that character disappears everywhere — we swallow the whole keypress "
            + "so it never reaches the text."),

        ["settings.liveFixHint"] = (
            "Слово чинится прямо в процессе набора. Заметно быстрее, но и ошибается заметнее: "
            + "если что-то пойдёт не так, страдает набираемое слово.",
            "The word is fixed as you type it. Noticeably faster — and noticeably worse when it "
            + "misfires, because the word you're typing is what suffers."),
        ["settings.ignoredWords"] = ("Не переключать эти слова (по одному в строке):",
                                     "Never switch these words (one per line):"),
        ["settings.appModes"] = ("Программы-исключения, по строке на каждую: имя.exe=off или имя.exe=soft",
                                 "Excepted apps, one per line: name.exe=off or name.exe=soft"),
        ["settings.appModesHint"] = (
            "Терминалы, видеоредакторы и удалённые рабочие столы отключены по умолчанию — "
            + "перечислять их здесь не нужно.",
            "Terminals, video editors and remote desktops are off by default — "
            + "no need to list them here."),

        ["settings.model"] = ("Модель", "Model"),
        ["settings.microphone"] = ("Микрофон", "Microphone"),
        ["mic.default"] = ("Как решит Windows", "Whatever Windows picks"),
        ["settings.browse"] = ("Выбрать…", "Browse…"),
        ["settings.language"] = ("Язык", "Language"),
        ["settings.privacyHint"] = (
            "Распознавание идёт на вашем компьютере, в сеть не уходит ничего.\n"
            + "Модель скачивается отдельно — файл ggml-*.bin с Hugging Face.",
            "Speech is recognised on your own computer; nothing leaves it.\n"
            + "The model is downloaded separately — a ggml-*.bin file from Hugging Face."),

        ["settings.snippets"] = ("По строке на сокращение: сокращение = что подставить",
                                 "One shortcut per line: shortcut = what it expands to"),
        ["settings.snippetsHint"] = (
            "Раскладка и регистр не важны: сокращение «адр» сработает и если набрать «flh», "
            + "забыв переключить язык.",
            "Layout and case don't matter: the shortcut “адр” fires even when typed as “flh” "
            + "with the wrong layout on."),

        // ——— Модели распознавания ———
        ["models.open"] = ("Скачать модель…", "Download a model…"),
        ["models.title"] = ("Keyboop — модели распознавания", "Keyboop — speech models"),
        ["models.sub"] = (
            "Это единственное место в программе, которое ходит в сеть, и только по нажатию кнопки. "
            + "Файл скачивается с закреплённой ревизии и проверяется по контрольной сумме: "
            + "не совпала — не установится.",
            "This is the only place in the program that uses the network, and only when you press "
            + "the button. The file comes from a pinned revision and is checked against its "
            + "checksum: no match, no install."),
        ["models.download"] = ("Скачать", "Download"),
        ["models.delete"] = ("Удалить", "Delete"),
        ["models.use"] = ("Использовать", "Use"),
        ["models.installed"] = ("скачана", "installed"),
        ["models.cancel"] = ("Отмена", "Cancel"),
        ["models.colModel"] = ("Модель", "Model"),
        ["models.colSize"] = ("Размер", "Size"),
        ["models.colNote"] = ("Особенности", "Notes"),
        ["models.colState"] = ("Состояние", "State"),
        ["models.busy"] = ("Качаю «{0}»…", "Downloading “{0}”…"),
        ["models.ok"] = ("Модель скачана и проверена.", "Model downloaded and verified."),
        ["models.errNetwork"] = ("Не удалось скачать: сеть недоступна или файл не отдан.",
                                 "Download failed: the network is unavailable or the file wasn't served."),
        ["models.errChecksum"] = ("Контрольная сумма не совпала — файл удалён и не установлен.",
                                  "The checksum didn't match — the file was deleted, not installed."),
        ["models.errDisk"] = ("Не удалось записать файл на диск.", "Couldn't write the file to disk."),
        ["models.confirmDelete"] = ("Удалить скачанную модель «{0}»?", "Delete the downloaded model “{0}”?"),

        ["model.base.note"] = ("быстрая, базовая точность", "fast, basic accuracy"),
        ["model.small.note"] = ("баланс качества и скорости", "balance of quality and speed"),
        ["model.medium.note"] = ("выше точность, медленнее", "higher accuracy, slower"),
        ["model.large.note"] = ("аккуратнее с пунктуацией и связностью, на 1–4 с медленнее",
                                "cleaner punctuation and phrasing, 1–4 s slower"),

        // ——— Спорные пары ———
        ["amb.open"] = ("Спорные пары…", "Ambiguous pairs…"),
        ["amb.title"] = ("Keyboop — кто побеждает", "Keyboop — who wins"),
        ["amb.sub"] = (
            "Эти слова набираются одними и теми же клавишами и существуют в обоих языках. "
            + "Мы не можем угадать за вас: кто-то пишет «versus» каждый день, кто-то — «мы» "
            + "в каждом втором предложении. Выберите, что должно получаться.",
            "These words are typed with the very same keys and exist in both languages. "
            + "We can't guess for you: some people write “versus” daily, others write “мы” "
            + "in every other sentence. Pick what should come out."),
        ["amb.auto"] = ("по контексту", "by context"),
        ["amb.colTyped"] = ("Набрано этими клавишами", "Typed with these keys"),
        ["amb.colResult"] = ("Что получится", "What comes out"),
        ["amb.hint"] = (
            "Выбор сильнее всех встроенных правил — он попадает в исключения. Слово, которого "
            + "здесь нет, всегда можно добавить вручную на вкладке «Раскладка».",
            "Your choice outranks every built-in rule — it goes into the exceptions. A pair that "
            + "isn't listed can always be added by hand on the Layout tab."),

        ["warn.dictationHotkey"] = ("Хоткей диктовки", "The dictation hotkey"),
        ["warn.layoutHotkey"] = ("Хоткей переключения слова", "The word-switching hotkey"),
        ["warn.unsafeHotkey"] = (
            "{0}: клавишу без модификаторов можно назначить только такую, которая ничего не "
            + "печатает — Pause, Insert, Scroll Lock или F1–F24.\n\n"
            + "Иначе этот символ перестанет набираться во всех программах.",
            "{0} may only use a modifier-free key that types nothing — "
            + "Pause, Insert, Scroll Lock or F1–F24.\n\n"
            + "Otherwise that character stops typing in every program."),

        // ——— История диктовок ———
        ["history.title"] = ("Keyboop — история диктовок", "Keyboop — dictation history"),
        ["history.hint"] = ("Двойной щелчок — скопировать. Записи хранятся час и стираются сами.",
                            "Double-click to copy. Entries are kept for an hour, then erased."),
        ["history.copy"] = ("Копировать", "Copy"),
        ["history.clear"] = ("Очистить", "Clear"),
        ["history.empty"] = ("(пусто)", "(empty)"),
        ["history.clipboardBusy"] = ("Буфер обмена сейчас занят другой программой. Попробуйте ещё раз.",
                                     "The clipboard is busy in another program right now. Try again."),
        ["history.confirmClear"] = ("Стереть всю историю диктовок?", "Erase the whole dictation history?"),

        // ——— Поле назначения хоткея ———
        ["hotkey.press"] = ("нажмите сочетание", "press a combination"),
        ["hotkey.waiting"] = ("жду нажатия…", "waiting…"),
        ["hotkey.unassigned"] = ("не назначено", "not assigned"),
    };
}
