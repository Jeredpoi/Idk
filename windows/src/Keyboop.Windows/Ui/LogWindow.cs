using System.Diagnostics;
using Keyboop.Core;
using Keyboop.Windows.Diagnostics;

namespace Keyboop.Windows.Ui;

/// <summary>
/// Живой лог: строки появляются по мере того, как программа работает.
///
/// Зачем отдельное окно, когда есть файл: «Блокнот» показывает срез на момент открытия и сам не
/// обновляется, а при аварии человек ещё и не успевает его открыть. Здесь видно происходящее
/// прямо сейчас — в том числе последнюю строку перед обрывом.
///
/// ⚠️ ОКНО НЕ МОДАЛЬНОЕ И ЭТО ПРИНЦИПИАЛЬНО. Смотреть лог надо, продолжая печатать в другой
/// программе; модальное окно перехватило бы фокус и наблюдать стало бы не за чем.
/// </summary>
internal sealed class LogWindow : Form
{
    /// <summary>
    /// Как часто забираем накопленное. Четверть секунды: глазу этого достаточно, чтобы поток
    /// выглядел живым, а перерисовка не идёт на каждую букву при быстром наборе.
    /// </summary>
    private const int RefreshMs = 250;

    /// <summary>Сколько строк держим в окне. Дальше отрезаем сверху — важен хвост.</summary>
    private const int MaxLines = 2000;

    private readonly TextBox _text = new();
    private readonly CheckBox _verbose = new();
    private readonly CheckBox _follow = new();
    private readonly System.Windows.Forms.Timer _timer = new();

    internal LogWindow()
    {
        Text = L10n.T("log.title");
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(760, 480);
        MinimumSize = new Size(520, 320);

        _text.Multiline = true;
        _text.ReadOnly = true;
        _text.ScrollBars = ScrollBars.Both;
        _text.WordWrap = false;
        _text.Font = new Font(FontFamily.GenericMonospace, 9);
        _text.BackColor = SystemColors.Window;
        _text.SetBounds(12, 12, 736, 400);
        _text.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        Controls.Add(_text);

        _verbose.SetBounds(12, 422, 220, 24);
        _verbose.Text = L10n.T("log.verbose");
        _verbose.Checked = Log.Verbose;
        _verbose.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        _verbose.CheckedChanged += (_, _) =>
        {
            Log.Verbose = _verbose.Checked;
            Log.Write($"подробный режим: {(Log.Verbose ? "включён" : "выключен")}");
        };
        Controls.Add(_verbose);

        _follow.SetBounds(240, 422, 180, 24);
        _follow.Text = L10n.T("log.follow");
        _follow.Checked = true;
        _follow.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        Controls.Add(_follow);

        var copy = new Button
        {
            Text = L10n.T("log.copy"),
            Left = 430,
            Top = 420,
            Width = 100,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
        };
        copy.Click += (_, _) => CopyAll();
        Controls.Add(copy);

        var open = new Button
        {
            Text = L10n.T("log.openFile"),
            Left = 538,
            Top = 420,
            Width = 110,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
        };
        open.Click += (_, _) => OpenFile();
        Controls.Add(open);

        var clear = new Button
        {
            Text = L10n.T("log.clear"),
            Left = 656,
            Top = 420,
            Width = 92,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
        };
        clear.Click += (_, _) => _text.Clear();
        Controls.Add(clear);

        Controls.Add(new Label
        {
            Left = 12,
            Top = 452,
            Width = 736,
            Height = 20,
            ForeColor = SystemColors.GrayText,
            Text = L10n.T("log.hint"),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
        });

        // Стартуем с уже накопленного: человек открывает лог как раз тогда, когда что-то уже
        // произошло, и пустое окно было бы издевательством.
        _text.Text = Log.Snapshot();
        Log.DrainLive();
        ScrollToEnd();

        _timer.Interval = RefreshMs;
        _timer.Tick += (_, _) => Pump();
        _timer.Start();
    }

    /// <summary>
    /// Забрать накопленное и дописать в окно.
    ///
    /// ⚠️ Ничего не делаем, когда очередь пуста. Присвоение текста заново сбрасывает выделение
    /// и позицию прокрутки, поэтому «обновлять на всякий случай» здесь означает мешать читать.
    /// </summary>
    private void Pump()
    {
        var lines = Log.DrainLive();
        if (lines.Count == 0)
        {
            return;
        }

        Trim();
        _text.AppendText(string.Join(Environment.NewLine, lines) + Environment.NewLine);

        if (_follow.Checked)
        {
            ScrollToEnd();
        }
    }

    /// <summary>Обрезать сверху, если строк стало слишком много.</summary>
    private void Trim()
    {
        if (_text.Lines.Length <= MaxLines)
        {
            return;
        }

        _text.Lines = _text.Lines[^(MaxLines / 2)..];
    }

    private void ScrollToEnd()
    {
        _text.SelectionStart = _text.TextLength;
        _text.SelectionLength = 0;
        _text.ScrollToCaret();
    }

    private void CopyAll()
    {
        try
        {
            // Единственное обращение к буферу обмена вне истории диктовок, и тоже по прямой
            // просьбе: лог нужно уметь переслать одним движением.
            Clipboard.SetText(_text.Text.Length > 0 ? _text.Text : " ");
        }
        catch (Exception)
        {
            MessageBox.Show(this, L10n.T("history.clipboardBusy"),
                "Keyboop", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OpenFile()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Log.FilePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Write($"лог: файл не открылся — {ex.GetType().Name}");
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        _timer.Dispose();
        base.OnFormClosed(e);
    }
}
