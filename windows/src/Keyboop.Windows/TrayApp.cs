using System.Diagnostics;
using Keyboop.Core.Layout;
using Keyboop.Core.Snippets;
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
    private readonly TrayIcons _icons = new();
    private readonly System.Windows.Forms.Timer _layoutTimer = new();
    private VoiceState _state = VoiceState.Idle;
    private string _shownLayout = string.Empty;

    internal TrayApp()
    {
        _settings = AppSettings.Load();
        _voice = new VoiceController(_settings, _engine);

        // Языковые данные лежат в папке data рядом с приложением и весят около пяти мегабайт,
        // поэтому читаются один раз лениво — первым обращением к LayoutData.Shared.
        _exceptions = new ExceptionStore(AppSettings.ExceptionsPath);
        _foreground = new ForegroundApp(_exceptions);
        _snippets = new SnippetStore(AppSettings.SnippetsPath);
        _layout = new LayoutEngine(LayoutData.Shared, _exceptions, _foreground, _snippets)
        {
            AutoEnabled = _settings.LayoutAutoFix,
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

        _voice.StateChanged += OnStateChanged;
        _voice.Notice += OnNotice;

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
                ex.Message + "\n\nБез перехватчика клавиатуры хоткей диктовки работать не будет.",
                "Keyboop", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void LoadModelIfConfigured()
    {
        if (string.IsNullOrWhiteSpace(_settings.ModelPath))
        {
            Log.Write("модель: путь не задан — диктовка выключена до выбора файла");
            return;
        }

        try
        {
            _engine.LoadModel(_settings.ModelPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or ApplicationException)
        {
            Log.Write($"модель: не загружена — {ex.GetType().Name}: {ex.Message}");
            ShowBalloon("Модель не загрузилась. Выберите файл заново в меню трея.");
        }
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();

        menu.Items.Add("Настройки…", null, (_, _) => OpenSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выбрать модель…", null, (_, _) => ChooseModel());

        var language = new ToolStripMenuItem("Язык");
        foreach (var (code, title) in new[] { ("auto", "Определять сам"), ("ru", "Русский"), ("en", "English") })
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
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Toggle("Убирать точку в конце", _settings.DropFinalPeriod,
            v => _settings.DropFinalPeriod = v));
        menu.Items.Add(Toggle("Начинать со строчной буквы", _settings.DropLeadingCapital,
            v => _settings.DropLeadingCapital = v));
        menu.Items.Add(Toggle("Пробел в конце", _settings.TrailingSpace,
            v => _settings.TrailingSpace = v));
        menu.Items.Add(Toggle("Отправлять Enter после текста", _settings.AutoEnter,
            v => _settings.AutoEnter = v));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Toggle("Исправлять раскладку автоматически", _settings.LayoutAutoFix,
            v =>
            {
                _settings.LayoutAutoFix = v;
                _layout.AutoEnabled = v;
                _foreground.Invalidate();
            }));
        menu.Items.Add(new ToolStripSeparator());

        // Мгновенное выключение. Нужно ровно на случай «что-то пошло не так прямо сейчас»:
        // человек должен уметь остановить программу, не разбираясь и не убивая процесс
        // через диспетчер задач.
        _pauseItem = new ToolStripMenuItem("Приостановить") { CheckOnClick = true };
        _pauseItem.CheckedChanged += (_, _) => SetPaused(_pauseItem.Checked);
        menu.Items.Add(_pauseItem);

        menu.Items.Add(Toggle("Запускать при входе в систему", Autostart.IsEnabled, Autostart.Set));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Показать лог", null, (_, _) => OpenLog());
        menu.Items.Add("Выход", null, (_, _) => ExitThread());

        return menu;
    }

    private ToolStripMenuItem? _pauseItem;

    /// <summary>Пауза снимает перехватчик целиком: пока он снят, мы не видим ввод вообще.</summary>
    private void SetPaused(bool paused)
    {
        if (paused)
        {
            _hook.Uninstall();
            _tray.Icon = _icons.Paused;
            _tray.Text = "Keyboop — приостановлен";
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
            ShowBalloon("Не удалось возобновить работу. Подробности в логе.");
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
            Title = "Файл модели whisper (ggml-*.bin)",
            Filter = "Модели whisper (*.bin)|*.bin|Все файлы (*.*)|*.*",
        };

        if (dialog.ShowDialog() != DialogResult.OK)
        {
            return;
        }

        try
        {
            _engine.LoadModel(dialog.FileName);
            _settings.ModelPath = dialog.FileName;
            _settings.Save();
            ShowBalloon("Модель загружена. Можно диктовать.");
        }
        catch (Exception ex)
        {
            Log.Write($"модель: выбор не удался — {ex.GetType().Name}: {ex.Message}");
            MessageBox.Show("Не удалось загрузить модель:\n" + ex.Message,
                "Keyboop", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
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

    private static void OpenLog()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Log.FilePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Write($"лог: не открылся — {ex.GetType().Name}");
        }
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
                    _tray.Text = "Keyboop — идёт запись";
                    break;

                case VoiceState.Processing:
                    _tray.Icon = _icons.Processing;
                    _tray.Text = "Keyboop — распознаю";
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
