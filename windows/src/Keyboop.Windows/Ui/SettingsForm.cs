using Keyboop.Core.Layout;
using Keyboop.Core.Snippets;

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
    private readonly TextBox _modelPath = new();
    private readonly CheckBox _autoFix = new();
    private readonly CheckBox _liveFix = new();
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

        Text = "Keyboop — настройки";
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
        Controls.Add(tabs);

        var save = new Button { Text = "Сохранить", Left = 320, Top = 392, Width = 90 };
        save.Click += (_, _) => Apply();

        var close = new Button { Text = "Закрыть", Left = 418, Top = 392, Width = 90 };
        close.Click += (_, _) => Close();

        Controls.Add(save);
        Controls.Add(close);
        AcceptButton = save;
        CancelButton = close;

        LoadValues();
    }

    private TabPage BuildHotkeysTab()
    {
        var page = new TabPage("Хоткеи");

        page.Controls.Add(Caption("Диктовка", 20));
        _dictationHotkey.SetBounds(200, 17, 280, 24);
        _dictationHotkey.SetRecordingMode = _setRecordingMode;
        page.Controls.Add(_dictationHotkey);

        page.Controls.Add(Caption("Как работает", 56));
        _dictationMode.SetBounds(200, 53, 280, 24);
        _dictationMode.DropDownStyle = ComboBoxStyle.DropDownList;
        _dictationMode.Items.AddRange(new object[] { "Удерживать", "Переключать" });
        page.Controls.Add(_dictationMode);

        page.Controls.Add(Caption("Переключить слово", 100));
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
            Text = "Клавишу без модификаторов можно назначить только такую, которая ничего не "
                 + "печатает: Pause, Insert, Scroll Lock или F1–F24.\n\n"
                 + "Иначе символ пропадёт во всех программах — мы глотаем нажатие целиком, чтобы "
                 + "оно не попало в текст.",
        });

        return page;
    }

    private TabPage BuildLayoutTab()
    {
        var page = new TabPage("Раскладка");

        _autoFix.SetBounds(20, 14, 460, 24);
        _autoFix.Text = "Исправлять раскладку автоматически на границе слова";
        page.Controls.Add(_autoFix);

        _liveFix.SetBounds(20, 40, 460, 24);
        _liveFix.Text = "Чинить не дожидаясь пробела";
        page.Controls.Add(_liveFix);

        // Предупреждение здесь не для красоты: галочка выключена по умолчанию именно потому, что
        // цена ошибки посреди слова выше, и человек должен понимать, на что соглашается.
        page.Controls.Add(new Label
        {
            Left = 40,
            Top = 64,
            Width = 440,
            Height = 32,
            ForeColor = SystemColors.GrayText,
            Text = "Слово чинится прямо в процессе набора. Заметно быстрее, но и ошибается заметнее: "
                 + "если что-то пойдёт не так, страдает набираемое слово.",
        });

        page.Controls.Add(new Label { Left = 20, Top = 100, Width = 460, Text = "Не переключать эти слова (по одному в строке):" });
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
            Text = "Программы-исключения, по строке на каждую: имя.exe=off или имя.exe=soft",
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
            Text = "Терминалы, видеоредакторы и удалённые рабочие столы отключены по умолчанию — "
                 + "перечислять их здесь не нужно.",
        });

        return page;
    }

    private TabPage BuildVoiceTab()
    {
        var page = new TabPage("Голос");

        page.Controls.Add(Caption("Модель", 20));
        _modelPath.SetBounds(200, 17, 220, 24);
        _modelPath.ReadOnly = true;
        page.Controls.Add(_modelPath);

        var browse = new Button { Text = "Выбрать…", Left = 426, Top = 16, Width = 60 };
        browse.Click += (_, _) => ChooseModel();
        page.Controls.Add(browse);

        page.Controls.Add(Caption("Язык", 56));
        _language.SetBounds(200, 53, 220, 24);
        _language.DropDownStyle = ComboBoxStyle.DropDownList;
        _language.Items.AddRange(new object[] { "Определять сам", "Русский", "English" });
        page.Controls.Add(_language);

        _dropPeriod.SetBounds(20, 100, 460, 24);
        _dropPeriod.Text = "Убирать точку в конце";
        page.Controls.Add(_dropPeriod);

        _dropCapital.SetBounds(20, 128, 460, 24);
        _dropCapital.Text = "Начинать со строчной буквы";
        page.Controls.Add(_dropCapital);

        _trailingSpace.SetBounds(20, 156, 460, 24);
        _trailingSpace.Text = "Добавлять пробел в конце";
        page.Controls.Add(_trailingSpace);

        _autoEnter.SetBounds(20, 184, 460, 24);
        _autoEnter.Text = "Отправлять Enter сразу после текста";
        page.Controls.Add(_autoEnter);

        page.Controls.Add(new Label
        {
            Left = 20,
            Top = 230,
            Width = 460,
            Height = 70,
            ForeColor = SystemColors.GrayText,
            Text = "Распознавание идёт на вашем компьютере, в сеть не уходит ничего.\n"
                 + "Модель скачивается отдельно — файл ggml-*.bin с Hugging Face.",
        });

        return page;
    }

    private TabPage BuildSnippetsTab()
    {
        var page = new TabPage("Сниппеты");

        page.Controls.Add(new Label
        {
            Left = 20,
            Top = 16,
            Width = 460,
            Height = 34,
            Text = "По строке на сокращение: сокращение = что подставить",
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
            Text = "Раскладка и регистр не важны: сокращение «адр» сработает и если набрать «flh», "
                 + "забыв переключить язык.",
        });

        return page;
    }

    private static Label Caption(string text, int top) =>
        new() { Left = 20, Top = top, Width = 170, Text = text };

    private void ChooseModel()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Файл модели whisper (ggml-*.bin)",
            Filter = "Модели whisper (*.bin)|*.bin|Все файлы (*.*)|*.*",
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _modelPath.Text = dialog.FileName;
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

        _modelPath.Text = _settings.ModelPath;
        _autoFix.Checked = _settings.LayoutAutoFix;
        _liveFix.Checked = _settings.LayoutLiveFix;
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
            Warn("Хоткей диктовки");
            return;
        }

        if (_layoutHotkey.VirtualKey != 0 && !_layoutHotkey.IsSafe())
        {
            Warn("Хоткей переключения слова");
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
        _settings.LayoutAutoFix = _autoFix.Checked;
        _settings.LayoutLiveFix = _liveFix.Checked;
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
            $"{what}: клавишу без модификаторов можно назначить только такую, которая ничего не "
            + "печатает — Pause, Insert, Scroll Lock или F1–F24.\n\n"
            + "Иначе этот символ перестанет набираться во всех программах.",
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
