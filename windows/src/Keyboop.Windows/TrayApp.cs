using System.Diagnostics;
using Keyboop.Core;
using Keyboop.Core.Layout;
using Keyboop.Core.Snippets;
using Keyboop.Core.Speech;
using Keyboop.Windows.Audio;
using Keyboop.Windows.Diagnostics;
using Keyboop.Windows.Interop;
using Keyboop.Windows.Speech;
using Keyboop.Windows.Ui;

namespace Keyboop.Windows;

/// <summary>
/// Приложение живёт в трее, без окна. Здесь собирается всё вместе: перехватчик хоткея,
/// оркестратор диктовки и значок состояния.
///
/// ⚠️ Хук ставится на ЭТОТ поток (он же поток интерфейса) — только у него есть цикл сообщений,
/// без которого Windows не доставит колбэк. Поэтому цикл нельзя занимать надолго: пока поток занят,
/// хук молчит, а через 300 мс система его снимет.
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly AppSettings _settings;
    private readonly WhisperSpeechEngine _engine = new();
    private readonly VoiceController _voice;
    private readonly KeyboardHook _hook = new();
    private readonly NotifyIcon _tray;
    private readonly SynchronizationContext _ui;
    private readonly LayoutEngine _layout;
    private readonly ExceptionStore _exceptions;
    private readonly ForegroundApp _foreground;
    private readonly SnippetStore _snippets;
    private readonly UndoLearner _undo;
    private readonly VoiceHistory _history;
    private readonly TrayIcons _icons = new();
    private readonly System.Windows.Forms.Timer _layoutTimer = new();
    private VoiceState _state = VoiceState.Idle;
    private string _shownLayout = string.Empty;

    internal TrayApp()
    {
        _settings = AppSettings.Load();

        // ⚠️ Язык выбираем ПЕРВЫМ ДЕЛОМ, до всего остального. Ниже собирается меню, а оно берёт
        // строки в момент сборки: сделай это позже — и первое меню осталось бы на языке по
        // умолчанию до перезапуска.
        ApplyUiLanguage();

        // История шифруется средствами Windows: в файле лежат расшифровки чужой речи, и открытым
        // текстом рядом с настройками им не место.
        _history = new VoiceHistory(AppSettings.VoiceHistoryPath, cipher: new DpapiHistoryCipher());
        _voice = new VoiceController(_settings, _engine, _history);

        // Языковые данные лежат в папке data рядом с приложением и весят около пяти мегабайт,
        // поэтому читаются один раз лениво — первым обращением к LayoutData.Shared.
        _exceptions = new ExceptionStore(AppSettings.ExceptionsPath);
        _foreground = new ForegroundApp(_exceptions);
        _snippets = new SnippetStore(AppSettings.SnippetsPath);
        _undo = new UndoLearner(AppSettings.UndoLearnPath, _exceptions);
        _layout = new LayoutEngine(LayoutData.Shared, _exceptions, _foreground, _snippets, _undo)
        {
            AutoEnabled = _settings.LayoutAutoFix,
            LiveFixEnabled = _settings.LayoutLiveFix,
        };
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        _tray = new NotifyIcon
        {
            Icon = _icons.Layout("EN"),
            Visible = true,
            Text = "Keyboop",
            ContextMenuStrip = BuildMenu(),
        };

        // Опрос раскладки раз в секунду. Дёшево (два вызова user32), зато значок отвечает на
        // вопрос «работает ли оно вообще» без заглядывания в лог. Событие о смене раскладки
        // приходит только окну переднего плана, а мы фоновые — подписаться на него нельзя.
        _layoutTimer.Interval = 1000;
        _layoutTimer.Tick += (_, _) => RefreshLayoutIcon();
        _layoutTimer.Start();

        // О выученном слове говорим: решение обратимо, и человек должен знать, что оно принято.
        _undo.Learned += word => _ui.Post(
            _ => ShowBalloon(L10n.T("notice.learned", word)), null);

        _voice.StateChanged += OnStateChanged;
        _voice.Notice += OnNotice;

        Cues.Enabled = _settings.Sounds;
        _layout.Converted += Cues.Converted;

        _hook.Dictation = new HotkeyBinding
        {
            VirtualKey = _settings.HotkeyVirtualKey,
            Modifiers = _settings.HotkeyModifiers,
        };
        _hook.LayoutConvert = new HotkeyBinding
        {
            VirtualKey = _settings.LayoutHotkeyVirtualKey,
            Modifiers = _settings.LayoutHotkeyModifiers,
        };
        _hook.Mode = _settings.Mode;
        _hook.CapsSwitchesLayout = _settings.CapsSwitchesLayout;
        _hook.CapsSwitchRequested += SwitchLayoutByCaps;
        _hook.IsRecording = () => _voice.IsRecording;
        _hook.DictationStarted += () => _voice.Begin();
        _hook.DictationStopped += () => _voice.End();
        _hook.DictationCancelled += () => _voice.Cancel();
        _hook.LayoutConvertRequested += () => _layout.ConvertManually();
        _hook.KeyObserved = _layout.OnKeyDown;

        LoadModelIfConfigured();

        try
        {
            _hook.Install();
        }
        catch (InvalidOperationException ex)
        {
            // Без перехватчика приложение бесполезно, но падать молча нельзя: человек должен
            // понять, что произошло. Чаще всего мешает другой перехватчик или политика домена.
            Log.Write($"хук: не установлен — {ex.Message}");
            MessageBox.Show(
                ex.Message + L10n.T("notice.hookFailed"),
                "Keyboop", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// Загрузить модель, если она выбрана. Ничего не ждём: чтение полутора гигабайт занимает
    /// секунды, а мы на потоке с перехватчиком клавиатуры — заняв его, мы бы этот перехватчик
    /// и потеряли.
    /// </summary>
    private void LoadModelIfConfigured()
    {
        if (string.IsNullOrWhiteSpace(_settings.ModelPath))
        {
            Log.Write("модель: путь не задан — диктовка выключена до выбора файла");
            return;
        }

        LoadModelInBackground(_settings.ModelPath, announce: false);
    }

    private async void LoadModelInBackground(string path, bool announce)
    {
        try
        {
            await _engine.LoadModelAsync(path);

            if (announce)
            {
                ShowBalloon(L10n.T("notice.modelLoaded"));
            }
        }
        catch (Exception ex)
        {
            // async void: исключение отсюда некому поймать, поэтому ловим ВСЁ. Непойманное здесь
            // означает не «сообщение в лог», а падение всего процесса.
            Log.Write($"модель: не загружена — {ex.GetType().Name}: {ex.Message}");
            ShowBalloon(L10n.T("notice.modelFailed"));
        }
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();

        menu.Items.Add(L10n.T("tray.settings"), null, (_, _) => OpenSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(L10n.T("models.open"), null, (_, _) => OpenModels());
        menu.Items.Add(L10n.T("tray.chooseModel"), null, (_, _) => ChooseModel());

        var language = new ToolStripMenuItem(L10n.T("tray.voiceLanguage"));
        foreach (var (code, title) in new[] { ("auto", L10n.T("lang.auto")), ("ru", "Русский"), ("en", "English") })
        {
            var item = new ToolStripMenuItem(title) { Checked = _settings.Language == code };
            item.Click += (_, _) =>
            {
                _settings.Language = code;
                _settings.Save();
                foreach (ToolStripMenuItem sibling in language.DropDownItems)
                {
                    sibling.Checked = false;
                }

                item.Checked = true;
            };
            language.DropDownItems.Add(item);
        }

        menu.Items.Add(language);
        menu.Items.Add(BuildUiLanguageMenu());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Toggle(L10n.T("opt.dropPeriod"), _settings.DropFinalPeriod,
            v => _settings.DropFinalPeriod = v));
        menu.Items.Add(Toggle(L10n.T("opt.dropCapital"), _settings.DropLeadingCapital,
            v => _settings.DropLeadingCapital = v));
        menu.Items.Add(Toggle(L10n.T("opt.trailingSpace"), _settings.TrailingSpace,
            v => _settings.TrailingSpace = v));
        menu.Items.Add(Toggle(L10n.T("opt.autoEnter"), _settings.AutoEnter,
            v => _settings.AutoEnter = v));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Toggle(L10n.T("opt.autoFix"), _settings.LayoutAutoFix,
            v =>
            {
                _settings.LayoutAutoFix = v;
                _layout.AutoEnabled = v;
                _foreground.Invalidate();
            }));
        menu.Items.Add(Toggle(L10n.T("opt.liveFix"), _settings.LayoutLiveFix,
            v =>
            {
                _settings.LayoutLiveFix = v;
                _layout.LiveFixEnabled = v;

                // Правка на лету — частный случай автоматического исправления и без него не
                // делает ничего. Включив её при выключенной автоматике, человек получил бы
                // галочку без последствий; включаем автоматику за него, а не молчим.
                if (v && !_settings.LayoutAutoFix)
                {
                    _settings.LayoutAutoFix = true;
                    _layout.AutoEnabled = true;
                    _tray.ContextMenuStrip = BuildMenu();
                }
            }));
        menu.Items.Add(Toggle(L10n.T("opt.capsSwitch"), _settings.CapsSwitchesLayout,
            v =>
            {
                _settings.CapsSwitchesLayout = v;
                _hook.CapsSwitchesLayout = v;
            }));
        menu.Items.Add(Toggle(L10n.T("opt.sounds"), _settings.Sounds,
            v =>
            {
                _settings.Sounds = v;
                Cues.Enabled = v;
            }));
        menu.Items.Add(new ToolStripSeparator());

        // Мгновенное выключение. Нужно ровно на случай «что-то пошло не так прямо сейчас»:
        // человек должен уметь остановить программу, не разбираясь и не убивая процесс
        // через диспетчер задач.
        _pauseItem = new ToolStripMenuItem(L10n.T("tray.pause")) { CheckOnClick = true };
        _pauseItem.CheckedChanged += (_, _) => SetPaused(_pauseItem.Checked);
        menu.Items.Add(_pauseItem);

        menu.Items.Add(Toggle(L10n.T("tray.autostart"), Autostart.IsEnabled, Autostart.Set));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(L10n.T("tray.history"), null, (_, _) => OpenHistory());
        menu.Items.Add(L10n.T("tray.log"), null, (_, _) => OpenLog());
        menu.Items.Add(L10n.T("tray.crashes"), null, (_, _) => OpenCrashReports());
        menu.Items.Add(L10n.T("tray.quit"), null, (_, _) => ExitThread());

        return menu;
    }

    private ToolStripMenuItem? _pauseItem;

    /// <summary>
    /// Язык интерфейса. Отдельный пункт, а не общий с языком распознавания: диктовать по-английски
    /// и читать меню по-русски — совершенно нормальное сочетание, и объединять их было бы
    /// самоуверенностью.
    /// </summary>
    private ToolStripMenuItem BuildUiLanguageMenu()
    {
        var root = new ToolStripMenuItem(L10n.T("tray.uiLanguage"));

        foreach (var (code, title) in new[] { ("auto", L10n.T("lang.auto")), ("ru", "Русский"), ("en", "English") })
        {
            var item = new ToolStripMenuItem(title) { Checked = _settings.UiLanguage == code };
            item.Click += (_, _) =>
            {
                _settings.UiLanguage = code;
                _settings.Save();
                ApplyUiLanguage();

                // Меню собрано из уже переведённых строк, поэтому его надо собрать заново — иначе
                // язык сменится только после перезапуска, а человек решит, что пункт не работает.
                _tray.ContextMenuStrip = BuildMenu();
                _shownLayout = string.Empty;
                RefreshLayoutIcon();
            };
            root.DropDownItems.Add(item);
        }

        return root;
    }

    /// <summary>Применить выбранный язык интерфейса; «auto» означает язык системы.</summary>
    private void ApplyUiLanguage() =>
        L10n.Select(
            _settings.UiLanguage,
            System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);

    /// <summary>Пауза снимает перехватчик целиком: пока он снят, мы не видим ввод вообще.</summary>
    private void SetPaused(bool paused)
    {
        if (paused)
        {
            _hook.Uninstall();
            _tray.Icon = _icons.Paused;
            _tray.Text = L10n.T("state.paused");
            return;
        }

        try
        {
            _hook.Install();
            _shownLayout = string.Empty;
            RefreshLayoutIcon();
        }
        catch (InvalidOperationException ex)
        {
            Log.Write($"хук: возобновить не удалось — {ex.Message}");
            ShowBalloon(L10n.T("notice.resumeFailed"));
            if (_pauseItem is not null)
            {
                _pauseItem.Checked = true;   // состояние меню обязано отражать правду
            }
        }
    }

    private ToolStripMenuItem Toggle(string title, bool initial, Action<bool> apply)
    {
        var item = new ToolStripMenuItem(title) { Checked = initial, CheckOnClick = true };
        item.CheckedChanged += (_, _) =>
        {
            apply(item.Checked);
            _settings.Save();
        };
        return item;
    }

    private void ChooseModel()
    {
        using var dialog = new OpenFileDialog
        {
            Title = L10n.T("dialog.modelTitle"),
            Filter = L10n.T("dialog.modelFilter"),
        };

        if (dialog.ShowDialog() != DialogResult.OK)
        {
            return;
        }

        UseModel(dialog.FileName);
    }

    /// <summary>Выбрать файл модели: запомнить путь и начать загрузку.</summary>
    private void UseModel(string path)
    {
        _settings.ModelPath = path;
        _settings.Save();
        LoadModelInBackground(path, announce: true);
    }

    private void OpenModels()
    {
        using var form = new ModelsForm();
        form.ModelChosen += UseModel;
        form.ShowDialog();
    }

    private void OpenSettings()
    {
        using var form = new SettingsForm(
            _settings, _exceptions, _snippets, paused => _hook.RecordingMode = paused);
        form.Applied += ApplySettings;
        form.ShowDialog();
    }

    /// <summary>
    /// Применить настройки без перезапуска. Хоткеи перевешиваем целиком, кэш активной программы
    /// сбрасываем: человек мог поменять списки исключений, не переключая окон.
    /// </summary>
    private void ApplySettings()
    {
        _hook.Dictation = new HotkeyBinding
        {
            VirtualKey = _settings.HotkeyVirtualKey,
            Modifiers = _settings.HotkeyModifiers,
        };
        _hook.LayoutConvert = new HotkeyBinding
        {
            VirtualKey = _settings.LayoutHotkeyVirtualKey,
            Modifiers = _settings.LayoutHotkeyModifiers,
        };
        _hook.Mode = _settings.Mode;
        _layout.AutoEnabled = _settings.LayoutAutoFix;
        _layout.LiveFixEnabled = _settings.LayoutLiveFix;
        _hook.CapsSwitchesLayout = _settings.CapsSwitchesLayout;
        Cues.Enabled = _settings.Sounds;
        _foreground.Invalidate();

        if (!string.IsNullOrWhiteSpace(_settings.ModelPath))
        {
            LoadModelIfConfigured();
        }

        Log.Write("настройки: применены без перезапуска");
    }

    /// <summary>Значок показывает текущий язык — но только когда мы не заняты чем-то важнее.</summary>
    private void RefreshLayoutIcon()
    {
        if (_state != VoiceState.Idle || !_hook.IsInstalled)
        {
            return;
        }

        var code = KeyboardLayoutSwitcher.ForegroundIsCyrillic() ? "RU" : "EN";
        if (code == _shownLayout)
        {
            return;
        }

        _shownLayout = code;
        _tray.Icon = _icons.Layout(code);
        _tray.Text = $"Keyboop — {code}";
    }

    /// <summary>
    /// Caps Lock в режиме мгновенного переключения. Зовётся ИЗ КОЛБЭКА ХУКА, поэтому здесь только
    /// отправка сообщения окну и сброс буфера — ничего дорогого.
    /// </summary>
    private void SwitchLayoutByCaps()
    {
        var direction = KeyboardLayoutSwitcher.Toggle();
        if (direction is not bool toCyrillic)
        {
            return;
        }

        // Человек сменил язык сам, посреди набора. Наша модель того, что на экране, после этого
        // недостоверна: дальше он печатает уже другими буквами.
        _layout.ResetContext();
        Cues.Converted(toCyrillic);
    }

    private void OpenHistory()
    {
        using var form = new HistoryForm(_history);
        form.ShowDialog();
    }

    /// <summary>Папка с отчётами об авариях. Создаём на всякий случай: она может ещё не существовать.</summary>
    private static void OpenCrashReports()
    {
        try
        {
            Directory.CreateDirectory(CrashReport.Directory);
            Process.Start(new ProcessStartInfo(CrashReport.Directory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Write($"отчёты: папка не открылась — {ex.GetType().Name}");
        }
    }

    private LogWindow? _log;

    /// <summary>
    /// Живой лог. Окно НЕ модальное: смотреть его надо, продолжая печатать в другой программе.
    /// Второй раз то же окно не открываем — просто поднимаем существующее.
    /// </summary>
    private void OpenLog()
    {
        if (_log is { IsDisposed: false })
        {
            _log.Activate();
            return;
        }

        _log = new LogWindow();
        _log.FormClosed += (_, _) => _log = null;
        _log.Show();
    }

    /// <summary>
    /// Состояние приходит с фонового потока распознавания, а трогать NotifyIcon можно только
    /// с потока интерфейса — поэтому переносим вызов явно.
    /// </summary>
    private void OnStateChanged(VoiceState state)
    {
        _ui.Post(_ =>
        {
            _state = state;

            switch (state)
            {
                case VoiceState.Recording:
                    _tray.Icon = _icons.Listening;
                    _tray.Text = L10n.T("state.recording");
                    Cues.RecordingStarted();
                    break;

                case VoiceState.Processing:
                    _tray.Icon = _icons.Processing;
                    _tray.Text = L10n.T("state.processing");
                    Cues.RecordingStopped();
                    break;

                default:
                    _shownLayout = string.Empty;   // заставить перерисовать значок языка
                    RefreshLayoutIcon();
                    break;
            }
        }, null);
    }

    private void OnNotice(string message) => _ui.Post(_ => ShowBalloon(message), null);

    private void ShowBalloon(string message)
    {
        _tray.BalloonTipTitle = "Keyboop";
        _tray.BalloonTipText = message;
        _tray.ShowBalloonTip(4000);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _layoutTimer.Stop();
            _layoutTimer.Dispose();
            _hook.Dispose();
            _voice.Dispose();
            _icons.Dispose();
            _engine.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
        }

        base.Dispose(disposing);
    }
}
