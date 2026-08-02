using Keyboop.Core;
using Keyboop.Core.Speech;

namespace Keyboop.Windows.Ui;

/// <summary>
/// Окно истории диктовок. Нужно, чтобы вернуть текст, который не удалось вставить.
///
/// Кнопка «Очистить» здесь обязательна и стоит на виду: это расшифровки чужой речи, и человек
/// должен уметь стереть их немедленно, не разыскивая файл на диске.
/// </summary>
internal sealed class HistoryForm : Form
{
    private readonly VoiceHistory _history;
    private readonly ListBox _list = new();

    internal HistoryForm(VoiceHistory history)
    {
        _history = history;

        Text = L10n.T("history.title");
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(560, 420);
        MinimizeBox = false;

        _list.SetBounds(12, 12, 536, 330);
        _list.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        _list.HorizontalScrollbar = true;
        _list.DoubleClick += (_, _) => CopySelected();
        Controls.Add(_list);

        var hint = new Label
        {
            Left = 12,
            Top = 350,
            Width = 380,
            Height = 34,
            ForeColor = SystemColors.GrayText,
            Text = L10n.T("history.hint"),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
        };
        Controls.Add(hint);

        var copy = new Button
        {
            Text = L10n.T("history.copy"),
            Left = 340,
            Top = 386,
            Width = 100,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
        };
        copy.Click += (_, _) => CopySelected();
        Controls.Add(copy);

        var clear = new Button
        {
            Text = L10n.T("history.clear"),
            Left = 448,
            Top = 386,
            Width = 100,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
        };
        clear.Click += (_, _) => ClearAll();
        Controls.Add(clear);

        Reload();
    }

    private void Reload()
    {
        _list.Items.Clear();

        foreach (var entry in _history.Entries)
        {
            // Переносы строк схлопываем: многострочная запись растянула бы список так, что
            // остальные перестали бы помещаться.
            var oneLine = entry.Text.Replace("\r", " ").Replace("\n", " ");
            _list.Items.Add($"{entry.At.ToLocalTime():HH:mm}  {oneLine}");
        }

        if (_list.Items.Count == 0)
        {
            _list.Items.Add(L10n.T("history.empty"));
            _list.Enabled = false;
        }
        else
        {
            _list.Enabled = true;
        }
    }

    private void CopySelected()
    {
        var index = _list.SelectedIndex;
        var entries = _history.Entries;

        if (index < 0 || index >= entries.Count)
        {
            return;
        }

        try
        {
            // ⚠️ Это ЕДИНСТВЕННОЕ место, где Keyboop трогает буфер обмена, и трогает по прямой
            // просьбе человека. Автоматические пути буфер не используют никогда — на этом стоит
            // весь проект.
            Clipboard.SetText(entries[index].Text);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException)
        {
            // Буфер обмена может быть заблокирован другой программой. Это не повод падать.
            MessageBox.Show(this, L10n.T("history.clipboardBusy"),
                "Keyboop", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ClearAll()
    {
        var answer = MessageBox.Show(
            this,
            L10n.T("history.confirmClear"),
            "Keyboop",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (answer == DialogResult.Yes)
        {
            _history.Clear();
            Reload();
        }
    }
}
