using Keyboop.Core;
using Keyboop.Core.Layout;
using Keyboop.Core.Snippets;
using Keyboop.Windows.Audio;

namespace Keyboop.Windows.Ui;

/// <summary>
/// Окно настроек. До него хоткей можно было поменять только правкой JSON-файла, что для обычного
/// человека равносильно «нельзя».
///
/// Собрано кодом, без дизайнера: так весь макет виден в одном файле и его можно читать как текст,
/// а не как разметку, сгенерированную средой.
/// </summary>
internal sealed class SettingsForm : Form
{
    private readonly AppSettings _settings;
    private readonly ExceptionStore _exceptions;
    private readonly SnippetStore _snippets;
    private readonly Action<bool> _setRecordingMode;

    private readonly HotkeyBox _dictationHotkey = new();
    private readonly HotkeyBox _layoutHotkey = new();
    private readonly ComboBox _dictationMode = new();
    private readonly ComboBox _language = new();
    private readonly ComboBox _microphone = new();
    private readonly TextBox _modelPath = new();
    private readonly CheckBox _autoFix = new();
    private readonly CheckBox _liveFix = new();
    private readonly CheckBox _capsSwitch = new();
    private readonly CheckBox _sounds = new();
    private readonly CheckBox _dropPeriod = new();
    private readonly CheckBox _dropCapital = new();
    private readonly CheckBox _trailingSpace = new();
    private readonly CheckBox _autoEnter = new();
    private readonly TextBox _ignoredWords = new();
    private readonly TextBox _appModes = new();
    private readonly TextBox _snippetLines = new();

    /// <summary>Настройки применены — вызывающий перевешивает хоткеи и сбрасывает кэши.</summary>
    internal event Action? Applied;

    internal SettingsForm(
        AppSettings settings,
        ExceptionStore exceptions,
        SnippetStore snippets,
        Action<bool> setRecordingMode)
    {
        _settings = settings;
        _exceptions = exceptions;
        _snippets = snippets;
        _setRecordingMode = setRecordingMode;

        Text = L10n.T("settings.title");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(520, 440);

        var tabs = new TabControl { Dock = DockStyle.Top, Height = 380 };
        tabs.TabPages.Add(BuildHotkeysTab());
        tabs.TabPages.Add(BuildLayoutTab());
        tabs.TabPages.Add(BuildVoiceTab());
        tabs.TabPages.Add(BuildSnippetsTab());
        tabs.TabPages.Add(BuildGeneralTab());
        Controls.Add(tabs);

        var save = new Button { Text = L10n.T("settings.save"), Left = 320, Top = 392, Width = 90 };
        save.Click += (_, _) => Apply();

        var close = new Button { Text = L10n.T("settings.close"), Left = 418, Top = 392, Width = 90 };
        close.Click += (_, _) => Close();

        Controls.Add(save);
        Controls.Add(close);
        AcceptButton = save;
        CancelButton = close;

        LoadValues();
    }

    private TabPage BuildHotkeysTab()
    {
        var page = new TabPage(L10n.T("tab.hotkeys"));

        page.Controls.Add(Caption(L10n.T("settings.dictation"), 20));
        _dictationHotkey.SetBounds(200, 17, 280, 24);
        _dictationHotkey.SetRecordingMode = _setRecordingMode;
        page.Controls.Add(_dictationHotkey);

        page.Controls.Add(Caption(L10n.T("settings.mode"), 56));
        _dictationMode.SetBounds(200, 53, 280, 24);
        _dictationMode.DropDownStyle = ComboBoxStyle.DropDownList;
        _dictationMode.Items.AddRange(new object[] { L10n.T("mode.hold"), L10n.T("mode.toggle") });
        page.Controls.Add(_dictationMode);

        page.Controls.Add(Caption(L10n.T("settings.convertWord"), 100));
        _layoutHotkey.SetBounds(200, 97, 280, 24);
        _layoutHotkey.SetRecordingMode = _setRecordingMode;
        page.Controls.Add(_layoutHotkey);

        page.Controls.Add(new Label
        {
            Left = 20,
            Top = 150,
            Width = 460,
            Height = 90,
            ForeColor = SystemColors.GrayText,
            Text = L10n.T("settings.hotkeyHint"),
        });

        return page;
    }

    private TabPage BuildLayoutTab()
    {
        var page = new TabPage(L10n.T("tab.layout"));

        _autoFix.SetBounds(20, 14, 460, 24);
        _autoFix.Text = L10n.T("opt.autoFix");
        page.Controls.Add(_autoFix);

        _liveFix.SetBounds(20, 40, 460, 24);
        _liveFix.Text = L10n.T("opt.liveFix");
        page.Controls.Add(_liveFix);

        // ⚠️ ЗАВИСИМОСТЬ ДОЛЖНА БЫТЬ ВИДНА. Правка на лету — частный случай автоматического
        // исправления и при выключенной автоматике не делает ничего. Две независимые с виду
        // галочки, из которых вторая молча не работает без первой, — это ровно тот класс
        // «настройка есть, а власти у неё нет», из-за которого человек идёт писать, что
        // программа сломана.
        _autoFix.CheckedChanged += (_, _) => SyncLiveFixAvailability();

        // Предупреждение здесь не для красоты: галочка выключена по умолчанию именно потому, что
        // цена ошибки посреди слова выше, и человек должен понимать, на что соглашается.
        page.Controls.Add(new Label
        {
            Left = 40,
            Top = 64,
            Width = 440,
            Height = 32,
            ForeColor = SystemColors.GrayText,
            Text = L10n.T("settings.liveFixHint"),
        });

        page.Controls.Add(new Label { Left = 20, Top = 100, Width = 300, Text = L10n.T("settings.ignoredWords") });

        // Спорные пары живут в отдельном окне: их три десятка, и в поле «по одному в строке» они
        // не помещаются — да и выбирать там надо не «есть/нет», а кто из двух побеждает.
        var pairs = new Button { Text = L10n.T("amb.open"), Left = 340, Top = 96, Width = 140 };
        pairs.Click += (_, _) =>
        {
            using var form = new AmbiguousPairsForm(_exceptions);
            form.ShowDialog(this);

            // Выбор победителя убирает слово из «не переключать». Не перечитав поле, мы бы вернули
            // его обратно при сохранении — и две записи заспорили бы между собой.
            _ignoredWords.Lines = _exceptions.Ignored.OrderBy(w => w, StringComparer.Ordinal).ToArray();
        };
        page.Controls.Add(pairs);

        _ignoredWords.SetBounds(20, 120, 460, 72);
        _ignoredWords.Multiline = true;
        _ignoredWords.ScrollBars = ScrollBars.Vertical;
        page.Controls.Add(_ignoredWords);

        page.Controls.Add(new Label
        {
            Left = 20,
            Top = 198,
            Width = 460,
            Height = 32,
            Text = L10n.T("settings.appModes"),
        });
        _appModes.SetBounds(20, 232, 460, 72);
        _appModes.Multiline = true;
        _appModes.ScrollBars = ScrollBars.Vertical;
        page.Controls.Add(_appModes);

        page.Controls.Add(new Label
        {
            Left = 20,
            Top = 308,
            Width = 460,
            Height = 34,
            ForeColor = SystemColors.GrayText,
            Text = L10n.T("settings.appModesHint"),
        });

        return page;
    }

    /// <summary>
    /// Правка на лету доступна только вместе с автоматическим исправлением: она его частный
    /// случай. Снятая автоматика гасит и её — и видно это сразу, а не по отсутствию эффекта.
    /// </summary>
    private void SyncLiveFixAvailability()
    {
        _liveFix.Enabled = _autoFix.Checked;

        if (!_autoFix.Checked)
        {
            _liveFix.Checked = false;
        }
    }

    private TabPage BuildGeneralTab()
    {
        var page = new TabPage(L10n.T("tab.general"));

        _capsSwitch.SetBounds(20, 18, 460, 24);
        _capsSwitch.Text = L10n.T("opt.capsSwitch");
        page.Controls.Add(_capsSwitch);

        page.Controls.Add(new Label
        {
            Left = 40,
            Top = 44,
            Width = 440,
            Height = 34,
            ForeColor = SystemColors.GrayText,
            Text = L10n.T("settings.capsHint"),
        });

        _sounds.SetBounds(20, 88, 460, 24);
        _sounds.Text = L10n.T("opt.sounds");
        page.Controls.Add(_sounds);

        page.Controls.Add(new Label
        {
            Left = 40,
            Top = 114,
            Width = 440,
            Height = 34,
            ForeColor = SystemColors.GrayText,
            Text = L10n.T("settings.soundsHint"),
        });

        return page;
    }

    private TabPage BuildVoiceTab()
    {
        var page = new TabPage(L10n.T("tab.voice"));

        page.Controls.Add(Caption(L10n.T("settings.model"), 20));
        _modelPath.SetBounds(200, 17, 220, 24);
        _modelPath.ReadOnly = true;
        page.Controls.Add(_modelPath);

        var browse = new Button { Text = L10n.T("settings.browse"), Left = 426, Top = 16, Width = 60 };
        browse.Click += (_, _) => ChooseModel();
        page.Controls.Add(browse);

        // Каталог с загрузкой — здесь же, а не только в меню трея: первый запуск начинается
        // именно с настроек, и отсутствие модели упирается ровно в этот шаг.
        var catalog = new Button { Text = L10n.T("models.open"), Left = 200, Top = 318, Width = 180 };
        catalog.Click += (_, _) =>
        {
            using var form = new ModelsForm();
            form.ModelChosen += path => _modelPath.Text = path;
            form.ShowDialog(this);
        };
        page.Controls.Add(catalog);

        page.Controls.Add(Caption(L10n.T("settings.language"), 56));
        _language.SetBounds(200, 53, 220, 24);
        _language.DropDownStyle = ComboBoxStyle.DropDownList;
        // Названия языков распознавания пишутся на самих языках — так их узнают в любом интерфейсе.
        _language.Items.AddRange(new object[] { L10n.T("lang.auto"), "Русский", "English" });
        page.Controls.Add(_language);

        page.Controls.Add(Caption(L10n.T("settings.microphone"), 92));
        _microphone.SetBounds(200, 89, 286, 24);
        _microphone.DropDownStyle = ComboBoxStyle.DropDownList;
        page.Controls.Add(_microphone);

        _dropPeriod.SetBounds(20, 136, 460, 24);
        _dropPeriod.Text = L10n.T("opt.dropPeriod");
        page.Controls.Add(_dropPeriod);

        _dropCapital.SetBounds(20, 164, 460, 24);
        _dropCapital.Text = L10n.T("opt.dropCapital");
        page.Controls.Add(_dropCapital);

        _trailingSpace.SetBounds(20, 192, 460, 24);
        _trailingSpace.Text = L10n.T("opt.trailingSpace");
        page.Controls.Add(_trailingSpace);

        _autoEnter.SetBounds(20, 220, 460, 24);
        _autoEnter.Text = L10n.T("opt.autoEnter");
        page.Controls.Add(_autoEnter);

        page.Controls.Add(new Label
        {
            Left = 20,
            Top = 256,
            Width = 460,
            Height = 60,
            ForeColor = SystemColors.GrayText,
            Text = L10n.T("settings.privacyHint"),
        });

        return page;
    }

    private TabPage BuildSnippetsTab()
    {
        var page = new TabPage(L10n.T("tab.snippets"));

        page.Controls.Add(new Label
        {
            Left = 20,
            Top = 16,
            Width = 460,
            Height = 34,
            Text = L10n.T("settings.snippets"),
        });

        _snippetLines.SetBounds(20, 52, 460, 220);
        _snippetLines.Multiline = true;
        _snippetLines.ScrollBars = ScrollBars.Vertical;
        _snippetLines.Font = new Font(FontFamily.GenericMonospace, 9);
        page.Controls.Add(_snippetLines);

        page.Controls.Add(new Label
        {
            Left = 20,
            Top = 280,
            Width = 460,
            Height = 60,
            ForeColor = SystemColors.GrayText,
            Text = L10n.T("settings.snippetsHint"),
        });

        return page;
    }

    private static Label Caption(string text, int top) =>
        new() { Left = 20, Top = top, Width = 170, Text = text };

    private void ChooseModel()
    {
        using var dialog = new OpenFileDialog
        {
            Title = L10n.T("dialog.modelTitle"),
            Filter = L10n.T("dialog.modelFilter"),
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _modelPath.Text = dialog.FileName;
        }
    }

    /// <summary>
    /// Заполнить список микрофонов. «Как решит Windows» — всегда первый пункт и значение по
    /// умолчанию: большинству людей выбирать нечего, и заставлять их — лишний шаг.
    ///
    /// Выбранный микрофон, которого уже нет в системе (вынули из USB), молча возвращает список
    /// к умолчанию. Показывать «устройство не найдено» тут не за что: провод вынимают каждый день.
    /// </summary>
    private void FillMicrophones()
    {
        // DisplayMember выставляем ДО наполнения: заданный после, он не перерисовывает уже
        // добавленные строки, и список показывает имя типа вместо названия микрофона.
        _microphone.DisplayMember = nameof(AudioInput.Name);
        _microphone.Items.Clear();
        _microphone.Items.Add(new AudioInput(AudioDevices.SystemDefault, L10n.T("mic.default")));

        foreach (var input in AudioDevices.Inputs())
        {
            _microphone.Items.Add(input);
        }

        _microphone.SelectedIndex = 0;

        for (var i = 1; i < _microphone.Items.Count; i++)
        {
            if (_microphone.Items[i] is AudioInput input && input.Id == _settings.MicrophoneId)
            {
                _microphone.SelectedIndex = i;
                break;
            }
        }
    }

    private void LoadValues()
    {
        _dictationHotkey.Assign(_settings.HotkeyVirtualKey, _settings.HotkeyModifiers);
        _layoutHotkey.Assign(_settings.LayoutHotkeyVirtualKey, _settings.LayoutHotkeyModifiers);
        _dictationMode.SelectedIndex = _settings.Mode == Interop.HotkeyMode.Toggle ? 1 : 0;

        _language.SelectedIndex = _settings.Language switch
        {
            "ru" => 1,
            "en" => 2,
            _ => 0,
        };

        FillMicrophones();

        _modelPath.Text = _settings.ModelPath;
        _autoFix.Checked = _settings.LayoutAutoFix;
        _liveFix.Checked = _settings.LayoutLiveFix;
        SyncLiveFixAvailability();
        _capsSwitch.Checked = _settings.CapsSwitchesLayout;
        _sounds.Checked = _settings.Sounds;
        _dropPeriod.Checked = _settings.DropFinalPeriod;
        _dropCapital.Checked = _settings.DropLeadingCapital;
        _trailingSpace.Checked = _settings.TrailingSpace;
        _autoEnter.Checked = _settings.AutoEnter;

        _ignoredWords.Lines = _exceptions.Ignored.OrderBy(w => w, StringComparer.Ordinal).ToArray();
        _snippetLines.Lines = _snippets.All
            .Select(p => $"{p.Key} = {p.Value}")
            .OrderBy(l => l, StringComparer.Ordinal)
            .ToArray();

        _appModes.Lines = _exceptions.AppModePairs()
            .Select(p => $"{p.Key}={p.Value}")
            .OrderBy(l => l, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void Apply()
    {
        // ⚠️ Небезопасный хоткей не сохраняем и честно объясняем почему. Молча проигнорировать
        // выбор человека нельзя — он решит, что программа сломана; а согласиться нельзя тем более.
        if (_dictationHotkey.VirtualKey != 0 && !_dictationHotkey.IsSafe())
        {
            Warn(L10n.T("warn.dictationHotkey"));
            return;
        }

        if (_layoutHotkey.VirtualKey != 0 && !_layoutHotkey.IsSafe())
        {
            Warn(L10n.T("warn.layoutHotkey"));
            return;
        }

        _settings.HotkeyVirtualKey = _dictationHotkey.VirtualKey;
        _settings.HotkeyModifiers = _dictationHotkey.Modifiers;
        _settings.LayoutHotkeyVirtualKey = _layoutHotkey.VirtualKey;
        _settings.LayoutHotkeyModifiers = _layoutHotkey.Modifiers;
        _settings.HotkeyModeName = _dictationMode.SelectedIndex == 1 ? "toggle" : "hold";

        _settings.Language = _language.SelectedIndex switch
        {
            1 => "ru",
            2 => "en",
            _ => "auto",
        };

        _settings.ModelPath = _modelPath.Text;
        _settings.MicrophoneId = _microphone.SelectedItem is AudioInput chosen ? chosen.Id : string.Empty;
        _settings.LayoutAutoFix = _autoFix.Checked;
        _settings.LayoutLiveFix = _liveFix.Checked;
        _settings.CapsSwitchesLayout = _capsSwitch.Checked;
        _settings.Sounds = _sounds.Checked;
        _settings.DropFinalPeriod = _dropPeriod.Checked;
        _settings.DropLeadingCapital = _dropCapital.Checked;
        _settings.TrailingSpace = _trailingSpace.Checked;
        _settings.AutoEnter = _autoEnter.Checked;
        _settings.Save();

        _exceptions.ReplaceIgnored(_ignoredWords.Lines);
        _exceptions.ReplaceAppModes(_appModes.Lines);
        _snippets.Replace(ParseSnippets(_snippetLines.Lines));

        Applied?.Invoke();
        Close();
    }

    /// <summary>
    /// Разбор строк «сокращение = раскрытие». Делим по ПЕРВОМУ знаку равенства: в раскрытии
    /// он встречается сплошь и рядом — в адресах, формулах, подписях.
    /// </summary>
    private static IEnumerable<KeyValuePair<string, string>> ParseSnippets(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var parts = line.Split('=', 2);
            if (parts.Length != 2)
            {
                continue;
            }

            var trigger = parts[0].Trim();
            var expansion = parts[1].Trim();

            if (trigger.Length > 0 && expansion.Length > 0)
            {
                yield return new KeyValuePair<string, string>(trigger, expansion);
            }
        }
    }

    private void Warn(string what) =>
        MessageBox.Show(
            this,
            L10n.T("warn.unsafeHotkey", what),
            "Keyboop",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        // Страховка: если окно закрыли, пока поле хоткея было в фокусе, перехват обязан вернуться.
        _setRecordingMode(false);
        base.OnFormClosed(e);
    }
}
