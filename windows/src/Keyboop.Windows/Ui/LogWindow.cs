using System.Diagnostics;
using System.Text;
using Keyboop.Core;
using Keyboop.Windows.Diagnostics;

namespace Keyboop.Windows.Ui;

/// <summary>
/// Живой лог: строки появляются по мере того, как программа работает.
///
/// ⚠️ ЗАПУСКАЕТСЯ ОТДЕЛЬНЫМ ПРОЦЕССОМ (тот же файл программы с ключом --log), и это главное
/// свойство окна. Окно внутри основного процесса умирает вместе с ним — то есть исчезает ровно
/// в тот момент, когда на него смотрят. Отдельный процесс не держит ни перехватчика, ни звука,
/// ни распознавания: падать ему нечем, и последние строки перед смертью основного остаются на
/// экране.
///
/// В отдельном процессе окно читает ФАЙЛ и следит за его концом. Очередь в памяти для этого не
/// годится по той же причине, по которой не годилась в отчёте об аварии: у чужого процесса своя
/// память, и она пуста.
/// </summary>
internal sealed class LogWindow : Form
{
    /// <summary>Четверть секунды: глазу достаточно, а перерисовка не идёт на каждую букву.</summary>
    private const int RefreshMs = 250;

    /// <summary>Сколько строк держим в окне. Дальше отрезаем сверху — важен хвост.</summary>
    private const int MaxLines = 3000;

    private readonly TextBox _text = new();
    private readonly CheckBox _follow = new();
    private readonly System.Windows.Forms.Timer _timer = new();

    /// <summary>Окно живёт отдельным процессом и читает файл, а не память.</summary>
    private readonly bool _standalone;

    /// <summary>Докуда файл уже прочитан. Только для отдельного процесса.</summary>
    private long _position;

    internal LogWindow(bool standalone = false)
    {
        _standalone = standalone;

        Text = L10n.T(standalone ? "log.titleStandalone" : "log.title");
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(820, 520);
        MinimumSize = new Size(520, 320);
        ShowInTaskbar = true;

        _text.Multiline = true;
        _text.ReadOnly = true;
        _text.ScrollBars = ScrollBars.Both;
        _text.WordWrap = false;
        _text.Font = new Font(FontFamily.GenericMonospace, 9);
        _text.BackColor = SystemColors.Window;
        _text.SetBounds(12, 12, 796, 440);
        _text.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        Controls.Add(_text);

        _follow.SetBounds(12, 462, 200, 24);
        _follow.Text = L10n.T("log.follow");
        _follow.Checked = true;
        _follow.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        Controls.Add(_follow);

        AddButton(L10n.T("log.copy"), 470, CopyAll);
        AddButton(L10n.T("log.openFile"), 578, OpenFile);
        AddButton(L10n.T("log.clear"), 686, () => _text.Clear());

        Controls.Add(new Label
        {
            Left = 12,
            Top = 492,
            Width = 796,
            Height = 20,
            ForeColor = SystemColors.GrayText,
            Text = L10n.T("log.hint"),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
        });

        Prime();

        _timer.Interval = RefreshMs;
        _timer.Tick += (_, _) => Pump();
        _timer.Start();
    }

    private void AddButton(string text, int left, Action action)
    {
        var button = new Button
        {
            Text = text,
            Left = left,
            Top = 460,
            Width = 100,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
        };

        button.Click += (_, _) => action();
        Controls.Add(button);
    }

    /// <summary>Показать то, что уже накопилось: окно открывают, когда что-то уже произошло.</summary>
    private void Prime()
    {
        if (_standalone)
        {
            _text.Text = Log.FileTail(MaxLines / 2);
            _position = CurrentLength();
        }
        else
        {
            _text.Text = Log.Snapshot();
            Log.DrainLive();
        }

        ScrollToEnd();
    }

    private void Pump()
    {
        var lines = _standalone ? ReadNewFromFile() : Log.DrainLive();
        if (lines.Count == 0)
        {
            // ⚠️ Пустой ответ — не повод трогать текст. Присвоение сбрасывает выделение и
            // прокрутку, то есть «обновление на всякий случай» мешало бы читать.
            return;
        }

        Trim();
        _text.AppendText(string.Join(Environment.NewLine, lines) + Environment.NewLine);

        if (_follow.Checked)
        {
            ScrollToEnd();
        }
    }

    /// <summary>Дочитать файл с того места, где остановились в прошлый раз.</summary>
    private List<string> ReadNewFromFile()
    {
        var lines = new List<string>();

        try
        {
            var length = CurrentLength();

            // Файл стал короче — значит его повернули или стёрли. Читаем с начала.
            if (length < _position)
            {
                _position = 0;
                lines.Add("--- лог начат заново ---");
            }

            if (length == _position)
            {
                return lines;
            }

            // FileShare.ReadWrite обязателен: основной процесс держит файл на запись, и без
            // этого мы не смогли бы прочесть ни строчки.
            using var stream = new FileStream(
                Log.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            stream.Seek(_position, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            while (reader.ReadLine() is { } line)
            {
                lines.Add(line);
            }

            _position = stream.Position;
        }
        catch (Exception)
        {
            // Файл мог быть занят на миг. Следующий тик прочтёт.
        }

        return lines;
    }

    private static long CurrentLength()
    {
        try
        {
            var file = new FileInfo(Log.FilePath);
            return file.Exists ? file.Length : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

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
