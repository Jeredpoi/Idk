using System.Diagnostics;
using Keyboop.Core.Layout;
using Keyboop.Windows.Diagnostics;
using Keyboop.Windows.Interop;
using Keyboop.Windows.Speech;

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
    private readonly PushToTalkHook _hook = new();
    private readonly NotifyIcon _tray;
    private readonly SynchronizationContext _ui;
    private readonly LayoutEngine _layout;
    private readonly ExceptionStore _exceptions;

    internal TrayApp()
    {
        _settings = AppSettings.Load();
        _voice = new VoiceController(_settings, _engine);

        // Языковые данные лежат в папке data рядом с приложением и весят около пяти мегабайт,
        // поэтому читаются один раз лениво — первым обращением к LayoutData.Shared.
        _exceptions = new ExceptionStore(AppSettings.ExceptionsPath);
        _layout = new LayoutEngine(LayoutData.Shared, _exceptions)
        {
            AutoEnabled = _settings.LayoutAutoFix,
        };
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = true,
            Text = "Keyboop — диктовка",
            ContextMenuStrip = BuildMenu(),
        };

        _voice.StateChanged += OnStateChanged;
        _voice.Notice += OnNotice;

        _hook.VirtualKey = _settings.HotkeyVirtualKey;
        _hook.Modifiers = _settings.HotkeyModifiers;
        _hook.Mode = _settings.Mode;
        _hook.IsRecording = () => _voice.IsRecording;
        _hook.DictationStarted += () => _voice.Begin();
        _hook.DictationStopped += () => _voice.End();
        _hook.DictationCancelled += () => _voice.Cancel();
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
            }));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Показать лог", null, (_, _) => OpenLog());
        menu.Items.Add("Выход", null, (_, _) => ExitThread());

        return menu;
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
            _tray.Icon = state switch
            {
                VoiceState.Recording => SystemIcons.Exclamation,
                VoiceState.Processing => SystemIcons.Information,
                _ => SystemIcons.Application,
            };

            _tray.Text = state switch
            {
                VoiceState.Recording => "Keyboop — идёт запись",
                VoiceState.Processing => "Keyboop — распознаю",
                _ => "Keyboop — диктовка",
            };
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
            _hook.Dispose();
            _voice.Dispose();
            _engine.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
        }

        base.Dispose(disposing);
    }
}
