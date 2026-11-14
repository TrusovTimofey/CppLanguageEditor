using CppLanguageEditor.Controls;
using CppLanguageEditor.Models;

namespace CppLanguageEditor;

public sealed class MainForm : Form
{
    private readonly CppEditor _editor = new();
    private readonly DataGridView _errorsGrid = new();
    private readonly ToolStripStatusLabel _status = new("Готово");
    private readonly ToolStripStatusLabel _cursorStatus = new("Строка: 1, столбец: 1");
    private readonly ToolStrip _toolStrip = new();
    private readonly string[] _textTopics =
    {
        "Постановка задачи", "Грамматика", "Классификация грамматики",
        "Метод анализа", "Тестовый пример", "Список литературы", "Исходный код программы"
    };
    private string? _currentFile;
    private bool _isDirty;
    private bool _loading;

    public MainForm()
    {
        Text = "C++ Language Editor";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(850, 550);
        Size = new Size(1200, 780);
        KeyPreview = true;
        AllowDrop = true;

        var menu = BuildMenu();
        var split = BuildWorkspace();
        var statusStrip = new StatusStrip();
        statusStrip.Items.Add(_status);
        statusStrip.Items.Add(new ToolStripStatusLabel { Spring = true });
        statusStrip.Items.Add(_cursorStatus);

        BuildToolbar();
        Controls.Add(split);
        Controls.Add(_toolStrip);
        Controls.Add(menu);
        Controls.Add(statusStrip);
        MainMenuStrip = menu;

        _editor.TextChanged += (_, _) =>
        {
            if (!_loading) _isDirty = true;
            UpdateTitle();
            UpdateCursorStatus();
        };
        _editor.KeyUp += (_, _) => UpdateCursorStatus();
        _editor.MouseUp += (_, _) => UpdateCursorStatus();
        _errorsGrid.CellDoubleClick += (_, e) => NavigateToError(e.RowIndex);
        FormClosing += OnFormClosing;
        DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true
            ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
                OpenFile(paths[0]);
        };
        KeyDown += HandleShortcuts;
        UpdateTitle();
    }

    private MenuStrip BuildMenu()
    {
        var menu = new MenuStrip();
        var file = new ToolStripMenuItem("Файл");
        file.DropDownItems.Add(Item("Создать", NewFile, Keys.Control | Keys.N));
        file.DropDownItems.Add(Item("Открыть", OpenDialog, Keys.Control | Keys.O));
        file.DropDownItems.Add(Item("Сохранить", SaveFile, Keys.Control | Keys.S));
        file.DropDownItems.Add(Item("Сохранить как", SaveAs));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Item("Выход", Close));

        var edit = new ToolStripMenuItem("Правка");
        edit.DropDownItems.Add(Item("Отменить", () => _editor.Undo(), Keys.Control | Keys.Z));
        edit.DropDownItems.Add(Item("Повторить", () => _editor.Redo(), Keys.Control | Keys.Y));
        edit.DropDownItems.Add(new ToolStripSeparator());
        edit.DropDownItems.Add(Item("Вырезать", () => _editor.Cut(), Keys.Control | Keys.X));
        edit.DropDownItems.Add(Item("Копировать", () => _editor.Copy(), Keys.Control | Keys.C));
        edit.DropDownItems.Add(Item("Вставить", () => _editor.Paste(), Keys.Control | Keys.V));
        edit.DropDownItems.Add(Item("Удалить", () => _editor.DeleteSelection(), Keys.Delete));
        edit.DropDownItems.Add(Item("Выделить все", () => _editor.SelectAllText(), Keys.Control | Keys.A));

        var text = new ToolStripMenuItem("Текст");
        foreach (string topic in _textTopics)
            text.DropDownItems.Add(Item(topic, () => ShowTopic(topic)));

        var run = new ToolStripMenuItem("Пуск");
        run.ShortcutKeys = Keys.F5;
        run.Click += (_, _) => RunAnalyzerPlaceholder();

        var help = new ToolStripMenuItem("Справка");
        help.DropDownItems.Add(Item("Вызов справки", ShowHelp, Keys.F1));
        help.DropDownItems.Add(Item("О программе", ShowAbout));

        menu.Items.AddRange(new ToolStripItem[] { file, edit, text, run, help });
        return menu;
    }

    private static ToolStripMenuItem Item(string title, Action action, Keys shortcut = Keys.None)
    {
        var item = new ToolStripMenuItem(title);
        if (shortcut != Keys.None) item.ShortcutKeys = shortcut;
        item.Click += (_, _) => action();
        return item;
    }

    private void BuildToolbar()
    {
        _toolStrip.Dock = DockStyle.Top;
        _toolStrip.GripStyle = ToolStripGripStyle.Hidden;
        _toolStrip.Items.Add(ToolButton("Создать", "Новый документ", NewFile));
        _toolStrip.Items.Add(ToolButton("Открыть", "Открыть файл", OpenDialog));
        _toolStrip.Items.Add(ToolButton("Сохранить", "Сохранить", SaveFile));
        _toolStrip.Items.Add(new ToolStripSeparator());
        _toolStrip.Items.Add(ToolButton("Отменить", "Отменить", () => _editor.Undo()));
        _toolStrip.Items.Add(ToolButton("Повторить", "Повторить", () => _editor.Redo()));
        _toolStrip.Items.Add(new ToolStripSeparator());
        _toolStrip.Items.Add(ToolButton("Копировать", "Копировать", () => _editor.Copy()));
        _toolStrip.Items.Add(ToolButton("Вырезать", "Вырезать", () => _editor.Cut()));
        _toolStrip.Items.Add(ToolButton("Вставить", "Вставить", () => _editor.Paste()));
        _toolStrip.Items.Add(new ToolStripSeparator());
        _toolStrip.Items.Add(ToolButton("Пуск", "Запустить анализатор (пока не реализован)", RunAnalyzerPlaceholder));
        _toolStrip.Items.Add(ToolButton("Справка", "Справка", ShowHelp));
        _toolStrip.Items.Add(ToolButton("О программе", "О программе", ShowAbout));
    }

    private static ToolStripButton ToolButton(string text, string tip, Action action)
    {
        var button = new ToolStripButton(text) { ToolTipText = tip, DisplayStyle = ToolStripItemDisplayStyle.Text };
        button.Click += (_, _) => action();
        return button;
    }

    private Control BuildWorkspace()
    {
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterWidth = 7,
            SplitterDistance = 490,
            Panel1MinSize = 150,
            Panel2MinSize = 120
        };

        var editorPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
        editorPanel.Controls.Add(_editor);
        var editorHeader = new Label
        {
            Text = "Редактор исходного кода C++",
            Dock = DockStyle.Top,
            Height = 25,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            ForeColor = Color.FromArgb(65, 72, 85)
        };
        editorPanel.Controls.Add(editorHeader);
        _editor.Dock = DockStyle.Fill;
        split.Panel1.Controls.Add(editorPanel);

        var resultPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
        var resultHeader = new Label
        {
            Text = "Результаты работы языкового процессора",
            Dock = DockStyle.Top,
            Height = 25,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            ForeColor = Color.FromArgb(65, 72, 85)
        };
        ConfigureErrorGrid();
        resultPanel.Controls.Add(_errorsGrid);
        resultPanel.Controls.Add(resultHeader);
        split.Panel2.Controls.Add(resultPanel);
        return split;
    }

    private void ConfigureErrorGrid()
    {
        _errorsGrid.Dock = DockStyle.Fill;
        _errorsGrid.ReadOnly = true;
        _errorsGrid.AllowUserToAddRows = false;
        _errorsGrid.AllowUserToDeleteRows = false;
        _errorsGrid.MultiSelect = false;
        _errorsGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _errorsGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _errorsGrid.RowHeadersVisible = false;
        _errorsGrid.BackgroundColor = Color.White;
        _errorsGrid.Columns.Add("Severity", "Тип");
        _errorsGrid.Columns.Add("Line", "Строка");
        _errorsGrid.Columns.Add("Column", "Столбец");
        _errorsGrid.Columns.Add("Message", "Сообщение");
        _errorsGrid.Columns["Severity"]!.FillWeight = 15;
        _errorsGrid.Columns["Line"]!.FillWeight = 12;
        _errorsGrid.Columns["Column"]!.FillWeight = 12;
        _errorsGrid.Columns["Message"]!.FillWeight = 61;
    }

    public void AddError(EditorError error)
    {
        int row = _errorsGrid.Rows.Add(error.Severity, error.Line, error.Column, error.Message);
        _errorsGrid.Rows[row].Tag = error;
    }

    public void ClearErrors()
    {
        _errorsGrid.Rows.Clear();
        _editor.ClearErrorHighlights();
    }

    public void HighlightError(int line, int column, int length = 1) =>
        _editor.HighlightError(line, column, length);

    private void NavigateToError(int rowIndex)
    {
        if (rowIndex < 0 || rowIndex >= _errorsGrid.Rows.Count) return;
        if (_errorsGrid.Rows[rowIndex].Tag is not EditorError error) return;
        _editor.GoToPosition(error.Line, error.Column, error.Length);
        _editor.HighlightError(error.Line, error.Column, error.Length);
        _status.Text = $"Переход к ошибке: строка {error.Line}, столбец {error.Column}";
    }

    private void RunAnalyzerPlaceholder()
    {
        _status.Text = "Анализатор пока не подключён. Здесь будет вызов будущего парсера.";
        MessageBox.Show(this,
            "Место запуска синтаксического анализатора подготовлено.\nПарсер будет подключён позднее.",
            "Пуск", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void NewFile()
    {
        if (!ConfirmDiscardChanges()) return;
        _loading = true;
        _editor.EditorText = string.Empty;
        _loading = false;
        _currentFile = null;
        _isDirty = false;
        ClearErrors();
        _status.Text = "Создан новый документ";
        UpdateTitle();
    }

    private void OpenDialog()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "Исходные файлы C++ (*.cpp;*.h;*.hpp;*.cc)|*.cpp;*.h;*.hpp;*.cc|Текстовые файлы (*.txt)|*.txt|Все файлы (*.*)|*.*",
            Title = "Открыть документ"
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) OpenFile(dialog.FileName);
    }

    private void OpenFile(string path)
    {
        if (!ConfirmDiscardChanges()) return;
        try
        {
            _loading = true;
            _editor.EditorText = File.ReadAllText(path);
            _loading = false;
            _currentFile = path;
            _isDirty = false;
            ClearErrors();
            _status.Text = $"Открыт файл: {Path.GetFileName(path)}";
            UpdateTitle();
        }
        catch (Exception ex)
        {
            _loading = false;
            MessageBox.Show(this, $"Не удалось открыть файл:\n{ex.Message}", "Ошибка",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SaveFile()
    {
        if (string.IsNullOrWhiteSpace(_currentFile)) { SaveAs(); return; }
        SaveTo(_currentFile);
    }

    private void SaveAs()
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "Исходный файл C++ (*.cpp)|*.cpp|Заголовочный файл (*.h)|*.h|Текстовый файл (*.txt)|*.txt|Все файлы (*.*)|*.*",
            DefaultExt = "cpp",
            AddExtension = true,
            Title = "Сохранить документ как"
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _currentFile = dialog.FileName;
            SaveTo(_currentFile);
        }
    }

    private void SaveTo(string path)
    {
        try
        {
            File.WriteAllText(path, _editor.EditorText);
            _isDirty = false;
            _status.Text = $"Сохранено: {Path.GetFileName(path)}";
            UpdateTitle();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Не удалось сохранить файл:\n{ex.Message}", "Ошибка",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private bool ConfirmDiscardChanges()
    {
        if (!_isDirty) return true;
        var result = MessageBox.Show(this, "Сохранить изменения в текущем документе?",
            "Несохранённые изменения", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (result == DialogResult.Cancel) return false;
        if (result == DialogResult.Yes)
        {
            SaveFile();
            return !_isDirty;
        }
        return true;
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!ConfirmDiscardChanges()) e.Cancel = true;
    }

    private void UpdateTitle()
    {
        string name = string.IsNullOrWhiteSpace(_currentFile) ? "Безымянный" : Path.GetFileName(_currentFile);
        Text = $"{name}{(_isDirty ? " *" : "")} — C++ Language Editor";
    }

    private void UpdateCursorStatus()
    {
        int pos = _editor.SelectionStart;
        string source = _editor.EditorText;
        int line = 1, column = 1;
        for (int i = 0; i < Math.Min(pos, source.Length); i++)
        {
            if (source[i] == '\n') { line++; column = 1; }
            else if (source[i] != '\r') column++;
        }
        _cursorStatus.Text = $"Строка: {line}, столбец: {column}";
    }

    private void HandleShortcuts(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F5) { RunAnalyzerPlaceholder(); e.Handled = true; }
        else if (e.KeyCode == Keys.F1) { ShowHelp(); e.Handled = true; }
    }

    private void ShowTopic(string topic)
    {
        MessageBox.Show(this,
            $"Раздел «{topic}» будет заполнен на следующем этапе разработки языкового процессора.",
            topic, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ShowHelp()
    {
        string help = """
        C++ Language Editor — краткая справка

        Файл:
        • Создать (Ctrl+N) — новый документ.
        • Открыть (Ctrl+O) — открыть текстовый файл.
        • Сохранить (Ctrl+S) — сохранить документ.
        • Сохранить как — сохранить под новым именем.
        • Выход — закрыть приложение с проверкой несохранённых изменений.

        Правка:
        • Отменить (Ctrl+Z), Повторить (Ctrl+Y).
        • Вырезать (Ctrl+X), Копировать (Ctrl+C), Вставить (Ctrl+V).
        • Удалить — удалить выделенный фрагмент.
        • Выделить все (Ctrl+A).

        Текст:
        Пункты открывают информационные окна-заготовки.

        Пуск (F5):
        Точка подключения будущего синтаксического анализатора.

        Редактор:
        Подсветка базовых конструкций C++ и нумерация строк.
        Двойной щелчок по ошибке в таблице переводит курсор к её позиции.
        """;
        MessageBox.Show(this, help, "Вызов справки", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ShowAbout()
    {
        MessageBox.Show(this,
            "C++ Language Editor\nВерсия 1.0\n\nТекстовый редактор — основа будущего языкового процессора.",
            "О программе", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
