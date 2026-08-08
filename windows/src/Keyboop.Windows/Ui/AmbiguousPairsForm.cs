using Keyboop.Core;
using Keyboop.Core.Layout;

namespace Keyboop.Windows.Ui;

/// <summary>
/// «Кто побеждает»: список слов, которые набираются одними и теми же клавишами и существуют
/// в обоих языках («vs» и «мы»).
///
/// Смысл окна — не настройка ради настройки. Главное правило детектора («валидное слово не
/// трогаем») именно здесь обязано выбрать за человека, а угадать за всех нельзя. По умолчанию
/// не фиксируем ничего: пусть решает контекст.
///
/// Своего хранилища у окна нет — выбор пишется в общий список исключений, который детектор
/// проверяет раньше всех встроенных правил.
/// </summary>
internal sealed class AmbiguousPairsForm : Form
{
    private const int ChoiceColumn = 1;

    private readonly ExceptionStore _exceptions;
    private readonly DataGridView _grid = new();

    /// <summary>Пока идёт заполнение, обработчик изменений молчит — иначе он писал бы наши же значения.</summary>
    private bool _loading;

    internal AmbiguousPairsForm(ExceptionStore exceptions)
    {
        _exceptions = exceptions;

        Text = L10n.T("amb.title");
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(520, 460);
        MinimizeBox = false;
        MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;

        Controls.Add(new Label
        {
            Left = 16,
            Top = 12,
            Width = 488,
            Height = 60,
            Text = L10n.T("amb.sub"),
        });

        _grid.SetBounds(16, 78, 488, 296);
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
        _grid.EditMode = DataGridViewEditMode.EditOnEnter;
        _grid.MultiSelect = false;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = L10n.T("amb.colTyped"),
            ReadOnly = true,
            FillWeight = 55,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });

        _grid.Columns.Add(new DataGridViewComboBoxColumn
        {
            HeaderText = L10n.T("amb.colResult"),
            FillWeight = 45,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton,
        });

        // Выбор в выпадающем списке фиксируем СРАЗУ, не дожидаясь ухода из ячейки: иначе человек
        // выберет вариант, закроет окно — и ничего не сохранится.
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty)
            {
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        };
        _grid.CellValueChanged += OnChoiceChanged;

        // ⚠️ БЕЗ ЭТОГО ОБРАБОТЧИКА ТАБЛИЦА УМЕЕТ БРОСАТЬ ИСКЛЮЧЕНИЕ ПРЯМО В ЛИЦО. Ячейка со
        // списком считает ошибкой любое значение, которого нет среди её вариантов, и по умолчанию
        // показывает модальное окно с текстом для разработчика. Здесь такого случиться не должно
        // (варианты и значение мы ставим сами), но цена страховки — три строки, а цена промаха —
        // непонятное окно поверх настроек.
        _grid.DataError += (_, e) => e.ThrowException = false;

        Controls.Add(_grid);

        Controls.Add(new Label
        {
            Left = 16,
            Top = 382,
            Width = 488,
            Height = 40,
            ForeColor = SystemColors.GrayText,
            Text = L10n.T("amb.hint"),
        });

        var close = new Button
        {
            Text = L10n.T("settings.close"),
            Left = 404,
            Top = 424,
            Width = 100,
        };
        close.Click += (_, _) => Close();
        Controls.Add(close);
        CancelButton = close;

        Fill();
    }

    private void Fill()
    {
        _loading = true;

        foreach (var pair in AmbiguousPairs.All)
        {
            var index = _grid.Rows.Add($"{pair.En}  ·  {pair.Ru}", null);
            var cell = (DataGridViewComboBoxCell)_grid.Rows[index].Cells[ChoiceColumn];

            // Варианты у каждой строки свои — это сами слова пары, а не абстрактные «англ./рус.».
            // Человек выбирает то, что увидит на экране, а не название языка.
            cell.Items.Add(L10n.T("amb.auto"));
            cell.Items.Add(pair.En);
            cell.Items.Add(pair.Ru);

            cell.Value = AmbiguousPairs.ChoiceOf(pair, _exceptions) switch
            {
                PairChoice.En => pair.En,
                PairChoice.Ru => pair.Ru,
                _ => L10n.T("amb.auto"),
            };
        }

        _loading = false;
    }

    private void OnChoiceChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (_loading || e.ColumnIndex != ChoiceColumn || e.RowIndex < 0
            || e.RowIndex >= AmbiguousPairs.All.Count)
        {
            return;
        }

        var pair = AmbiguousPairs.All[e.RowIndex];
        var value = _grid.Rows[e.RowIndex].Cells[ChoiceColumn].Value as string;

        var choice = value == pair.En ? PairChoice.En
            : value == pair.Ru ? PairChoice.Ru
            : PairChoice.Auto;

        AmbiguousPairs.Choose(pair, choice, _exceptions);
    }
}
