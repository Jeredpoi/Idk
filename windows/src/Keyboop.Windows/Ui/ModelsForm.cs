using Keyboop.Core;
using Keyboop.Core.Speech;
using Keyboop.Windows.Speech;

namespace Keyboop.Windows.Ui;

/// <summary>
/// Каталог моделей распознавания: скачать, удалить, выбрать.
///
/// До этого окна модель приходилось искать на Hugging Face самому и указывать файл вручную —
/// то есть первый запуск упирался в задачу, которую большинство людей просто не станет решать.
///
/// ⚠️ Это единственное окно программы, за которым стоит сетевой запрос, и он делается ровно по
/// нажатию кнопки «Скачать». Ни каталог, ни описания, ни размеры не тянутся из сети: они зашиты
/// в <see cref="ModelCatalog"/> вместе с контрольными суммами.
/// </summary>
internal sealed class ModelsForm : Form
{
    private readonly ListView _list = new();
    private readonly ProgressBar _progress = new();
    private readonly Label _status = new();
    private readonly Button _download = new();
    private readonly Button _delete = new();
    private readonly Button _use = new();

    private CancellationTokenSource? _cancellation;

    /// <summary>Человек выбрал модель для работы — вызывающий её загружает и сохраняет путь.</summary>
    internal event Action<string>? ModelChosen;

    internal ModelsForm()
    {
        Text = L10n.T("models.title");
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(620, 400);

        Controls.Add(new Label
        {
            Left = 16,
            Top = 12,
            Width = 588,
            Height = 48,
            Text = L10n.T("models.sub"),
        });

        _list.SetBounds(16, 66, 588, 180);
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.MultiSelect = false;
        _list.HideSelection = false;
        _list.Columns.Add(L10n.T("models.colModel"), 150);
        _list.Columns.Add(L10n.T("models.colSize"), 80);
        _list.Columns.Add(L10n.T("models.colNote"), 250);
        _list.Columns.Add(L10n.T("models.colState"), 100);
        _list.SelectedIndexChanged += (_, _) => RefreshButtons();
        Controls.Add(_list);

        _progress.SetBounds(16, 258, 588, 18);
        _progress.Minimum = 0;
        _progress.Maximum = 1000;
        _progress.Visible = false;
        Controls.Add(_progress);

        _status.SetBounds(16, 284, 588, 34);
        _status.ForeColor = SystemColors.GrayText;
        Controls.Add(_status);

        _download.SetBounds(16, 330, 120, 28);
        _download.Text = L10n.T("models.download");
        _download.Click += async (_, _) => await DownloadSelectedAsync();
        Controls.Add(_download);

        _delete.SetBounds(146, 330, 120, 28);
        _delete.Text = L10n.T("models.delete");
        _delete.Click += (_, _) => DeleteSelected();
        Controls.Add(_delete);

        _use.SetBounds(276, 330, 140, 28);
        _use.Text = L10n.T("models.use");
        _use.Click += (_, _) => UseSelected();
        Controls.Add(_use);

        var close = new Button
        {
            Text = L10n.T("settings.close"),
            Left = 504,
            Top = 330,
            Width = 100,
        };
        close.Click += (_, _) => Close();
        Controls.Add(close);
        CancelButton = close;

        Fill();
    }

    private void Fill()
    {
        _list.Items.Clear();

        foreach (var model in ModelCatalog.All)
        {
            var item = new ListViewItem(model.Name);
            item.SubItems.Add(ModelCatalog.LocalizedSize(model.Size));
            item.SubItems.Add(L10n.T(model.NoteKey));
            item.SubItems.Add(ModelDownloader.IsInstalled(model.Name) ? L10n.T("models.installed") : string.Empty);
            item.Tag = model.Name;
            _list.Items.Add(item);
        }

        if (_list.Items.Count > 0)
        {
            _list.Items[0].Selected = true;
        }

        RefreshButtons();
    }

    private string? Selected =>
        _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as string : null;

    private void RefreshButtons()
    {
        var busy = _cancellation is not null;
        var name = Selected;
        var installed = name is not null && ModelDownloader.IsInstalled(name);

        _download.Text = busy ? L10n.T("models.cancel") : L10n.T("models.download");
        _download.Enabled = busy || (name is not null && !installed);
        _delete.Enabled = !busy && installed;
        _use.Enabled = !busy && installed;
        _list.Enabled = !busy;
    }

    private async Task DownloadSelectedAsync()
    {
        // Кнопка двойного назначения: пока идёт загрузка, она отменяет её. Отдельная кнопка
        // «Отмена» простаивала бы всё остальное время.
        if (_cancellation is not null)
        {
            _cancellation.Cancel();
            return;
        }

        var name = Selected;
        if (name is null)
        {
            return;
        }

        _cancellation = new CancellationTokenSource();
        _progress.Visible = true;
        _progress.Style = ProgressBarStyle.Marquee;
        _status.Text = L10n.T("models.busy", name);
        RefreshButtons();

        var progress = new Progress<double>(fraction =>
        {
            if (fraction < 0)
            {
                // Сервер не сказал размер — честнее крутить бесконечную полосу, чем врать про «0 %».
                _progress.Style = ProgressBarStyle.Marquee;
                return;
            }

            _progress.Style = ProgressBarStyle.Continuous;
            _progress.Value = Math.Clamp((int)(fraction * 1000), 0, 1000);
        });

        DownloadResult result;

        try
        {
            result = await ModelDownloader.DownloadAsync(name, progress, _cancellation.Token);
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            _progress.Visible = false;
            _progress.Style = ProgressBarStyle.Continuous;
            _progress.Value = 0;
        }

        _status.Text = result switch
        {
            DownloadResult.Ok => L10n.T("models.ok"),
            DownloadResult.Cancelled => string.Empty,
            DownloadResult.ChecksumMismatch => L10n.T("models.errChecksum"),
            DownloadResult.DiskError => L10n.T("models.errDisk"),
            _ => L10n.T("models.errNetwork"),
        };

        RefreshState(name);
        RefreshButtons();

        // Скачал — почти наверняка чтобы этим и пользоваться. Лишний шаг здесь не нужен.
        if (result == DownloadResult.Ok)
        {
            ModelChosen?.Invoke(ModelDownloader.PathOf(name));
        }
    }

    private void DeleteSelected()
    {
        var name = Selected;
        if (name is null)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            L10n.T("models.confirmDelete", name),
            "Keyboop",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (answer != DialogResult.Yes)
        {
            return;
        }

        ModelDownloader.Delete(name);
        RefreshState(name);
        RefreshButtons();
    }

    private void UseSelected()
    {
        var name = Selected;
        if (name is not null)
        {
            ModelChosen?.Invoke(ModelDownloader.PathOf(name));
            Close();
        }
    }

    private void RefreshState(string name)
    {
        foreach (ListViewItem item in _list.Items)
        {
            if ((string?)item.Tag == name)
            {
                item.SubItems[3].Text =
                    ModelDownloader.IsInstalled(name) ? L10n.T("models.installed") : string.Empty;
                return;
            }
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Закрыть окно посреди загрузки — законное действие, но бросать её на произвол нельзя:
        // недокачанный временный файл иначе останется на диске навсегда.
        _cancellation?.Cancel();
        base.OnFormClosing(e);
    }
}
