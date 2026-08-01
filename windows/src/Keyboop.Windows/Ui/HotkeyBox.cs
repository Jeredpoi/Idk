namespace Keyboop.Windows.Ui;

/// <summary>
/// Поле назначения хоткея: человек нажимает сочетание, и оно записывается.
///
/// Именно нажимает, а не вводит код клавиши. Раньше поменять хоткей можно было только правкой
/// JSON-файла, что для обычного человека равносильно «нельзя».
/// </summary>
internal sealed class HotkeyBox : TextBox
{
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
    private const int VK_LWIN = 0x5B;

    internal HotkeyBox()
    {
        ReadOnly = true;
        Cursor = Cursors.Hand;
        TextAlign = HorizontalAlignment.Center;
        Text = "нажмите сочетание";
    }

    /// <summary>Виртуальный код выбранной клавиши. Ноль — не назначено.</summary>
    internal int VirtualKey { get; private set; }

    /// <summary>Модификаторы, которые надо удерживать.</summary>
    internal List<int> Modifiers { get; private set; } = [];

    /// <summary>
    /// Перехватчик обязан молчать, пока идёт назначение, иначе он проглотит то самое сочетание,
    /// которое человек пытается назначить. Форма подставляет сюда живой хук.
    /// </summary>
    internal Action<bool>? SetRecordingMode { get; set; }

    internal void Assign(int virtualKey, IEnumerable<int> modifiers)
    {
        VirtualKey = virtualKey;
        Modifiers = modifiers.ToList();
        Text = Describe();
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        SetRecordingMode?.Invoke(true);
        Text = "жду нажатия…";
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        SetRecordingMode?.Invoke(false);
        Text = Describe();
    }

    /// <summary>
    /// Ловим клавиши до того, как WinForms превратит их в навигацию: Tab, стрелки и Escape
    /// иначе увели бы фокус вместо того, чтобы записаться в хоткей.
    /// </summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (!Focused)
        {
            return base.ProcessCmdKey(ref msg, keyData);
        }

        var key = keyData & Keys.KeyCode;

        // Escape — отказаться от назначения, оставив прежнее.
        if (key == Keys.Escape)
        {
            Parent?.Focus();
            return true;
        }

        // Голый модификатор — это ещё не сочетание, ждём основную клавишу.
        if (key is Keys.ShiftKey or Keys.ControlKey or Keys.Menu or Keys.LWin or Keys.RWin)
        {
            return true;
        }

        var modifiers = new List<int>();
        if ((keyData & Keys.Control) != 0)
        {
            modifiers.Add(VK_CONTROL);
        }

        if ((keyData & Keys.Shift) != 0)
        {
            modifiers.Add(VK_SHIFT);
        }

        if ((keyData & Keys.Alt) != 0)
        {
            modifiers.Add(VK_MENU);
        }

        Assign((int)key, modifiers);
        Parent?.Focus();
        return true;
    }

    private string Describe()
    {
        if (VirtualKey == 0)
        {
            return "не назначено";
        }

        var parts = new List<string>();
        foreach (var modifier in Modifiers)
        {
            parts.Add(modifier switch
            {
                VK_CONTROL => "Ctrl",
                VK_SHIFT => "Shift",
                VK_MENU => "Alt",
                VK_LWIN => "Win",
                _ => "?",
            });
        }

        parts.Add(((Keys)VirtualKey).ToString());
        return string.Join(" + ", parts);
    }

    /// <summary>
    /// Голая клавиша, которая ЧТО-ТО ПЕЧАТАЕТ, в качестве хоткея недопустима: мы глотаем нажатие
    /// целиком, и человек останется без этого символа во всех программах, пока не выйдет из
    /// Keyboop. В macOS-версии это стоило трёх отчётов «перестал работать пробел».
    /// Разрешаем без модификаторов только те клавиши, что и так ничего не печатают.
    /// </summary>
    internal bool IsSafe()
    {
        if (VirtualKey == 0)
        {
            return false;
        }

        if (Modifiers.Count > 0)
        {
            return true;
        }

        var key = (Keys)VirtualKey;
        return key is Keys.Pause or Keys.Scroll or Keys.Insert
            || (key >= Keys.F1 && key <= Keys.F24);
    }
}
