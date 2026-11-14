using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace CppLanguageEditor.Controls;

public sealed class CppEditor : UserControl
{
    private const int HighlightMarginLines = 60;

    private static readonly Color DefaultForeground = Color.FromArgb(35, 38, 45);
    private static readonly Color DefaultBackground = Color.FromArgb(250, 250, 252);

    private readonly RichTextBox _textBox;
    private bool _highlighting;
    private bool _isRestoring;
    private bool _undoGroupOpen;
    private bool _highlightDirty = true;
    private int _lastVisibleStartChar = -1;
    private int _lastVisibleEndChar = -1;
    private string _lastKnownText = string.Empty;
    private int _lastSelectionStart;
    private int _lastSelectionLength;
    private readonly Stack<EditorSnapshot> _undoStack = new();
    private readonly Stack<EditorSnapshot> _redoStack = new();
    private readonly System.Windows.Forms.Timer _highlightTimer;
    private readonly System.Windows.Forms.Timer _undoGroupingTimer;
    private sealed record EditorSnapshot(string Text, int SelectionStart, int SelectionLength);

    private const int EM_GETSCROLLPOS = 0x04DD;
    private const int EM_SETSCROLLPOS = 0x04DE;
    private const int WM_SETREDRAW = 0x000B;
    private const uint RDW_INVALIDATE = 0x0001;
    private const uint RDW_ERASE = 0x0004;
    private const uint RDW_UPDATENOW = 0x0100;
    private const uint RDW_ALLCHILDREN = 0x0080;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(
        IntPtr hWnd, int msg, IntPtr wParam, ref NativePoint lParam);

    [DllImport("user32.dll", EntryPoint = "SendMessage", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessageNative(
        IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RedrawWindow(
        IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);

    private static readonly Regex TokenRegex = new(
        @"(?<comment>//[^\r\n]*|/\*[\s\S]*?\*/)|" +
        @"(?<string>""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*')|" +
        @"(?<preprocessor>^\s*#\s*[A-Za-z_]\w*)|" +
        @"(?<number>\b(?:0[xX][0-9A-Fa-f]+|\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)[uUlLfF]*\b)|" +
        @"(?<keyword>\b(?:alignas|alignof|asm|auto|bool|break|case|catch|char|char8_t|char16_t|char32_t|class|const|constexpr|consteval|constinit|continue|decltype|default|delete|do|double|else|enum|explicit|export|extern|false|float|for|friend|goto|if|inline|int|long|mutable|namespace|new|noexcept|nullptr|operator|private|protected|public|register|reinterpret_cast|return|short|signed|sizeof|static|static_assert|static_cast|struct|switch|template|this|thread_local|throw|true|try|typedef|typeid|typename|union|unsigned|using|virtual|void|volatile|wchar_t|while|override|final)\b)|" +
        @"(?<type>\b(?:size_t|string|vector|map|set|unordered_map|unordered_set|list|deque|queue|stack|pair|unique_ptr|shared_ptr|optional|variant|istream|ostream|ifstream|ofstream|iostream|cin|cout|cerr|endl)\b)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    public CppEditor()
    {
        Dock = DockStyle.Fill;
        BackColor = Color.White;

        _textBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            AcceptsTab = true,
            WordWrap = false,
            DetectUrls = false,
            Font = new Font("Consolas", 11F),
            BackColor = DefaultBackground,
            ForeColor = DefaultForeground,
            HideSelection = false,
            ScrollBars = RichTextBoxScrollBars.Both,
            EnableAutoDragDrop = true
        };
        Controls.Add(_textBox);

        _lastKnownText = _textBox.Text;

        _textBox.TextChanged += (_, _) =>
        {
            if (!_isRestoring) RegisterTextChange();
            _highlightDirty = true;
            ScheduleHighlight();
            TextChanged?.Invoke(this, EventArgs.Empty);
        };
        _textBox.SelectionChanged += (_, _) =>
        {
            if (!_isRestoring && !_highlighting)
            {
                _lastSelectionStart = _textBox.SelectionStart;
                _lastSelectionLength = _textBox.SelectionLength;
            }
        };
        _textBox.VScroll += (_, _) => ScheduleHighlight();
        _textBox.HScroll += (_, _) => ScheduleHighlight();
        _textBox.KeyDown += HandleEditorKeyDown;

        _highlightTimer = new System.Windows.Forms.Timer { Interval = 200 };
        _highlightTimer.Tick += (_, _) =>
        {
            _highlightTimer.Stop();
            ApplySyntaxHighlighting();
        };

        _undoGroupingTimer = new System.Windows.Forms.Timer { Interval = 600 };
        _undoGroupingTimer.Tick += (_, _) =>
        {
            _undoGroupingTimer.Stop();
            _undoGroupOpen = false;
            RememberCurrentState();
        };
    }

    public new event EventHandler? TextChanged;

    public string EditorText
    {
        get => _textBox.Text;
        set
        {
            _isRestoring = true;
            try
            {
                _textBox.Text = value ?? string.Empty;
                _textBox.ClearUndo();
                _undoStack.Clear();
                _redoStack.Clear();
                _undoGroupingTimer.Stop();
                _undoGroupOpen = false;
                _lastKnownText = _textBox.Text;
                _lastSelectionStart = _textBox.SelectionStart;
                _lastSelectionLength = _textBox.SelectionLength;
                _highlightDirty = true;
                _lastVisibleStartChar = -1;
                _lastVisibleEndChar = -1;
            }
            finally { _isRestoring = false; }
            ScheduleHighlight();
            TextChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public int SelectionStart { get => _textBox.SelectionStart; set => _textBox.SelectionStart = value; }
    public int SelectionLength { get => _textBox.SelectionLength; set => _textBox.SelectionLength = value; }
    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    public void Undo() => RestoreFrom(_undoStack, _redoStack);
    public void Redo() => RestoreFrom(_redoStack, _undoStack);

    public void GetCursorLineColumn(out int line, out int column)
    {
        int pos = Math.Min(_textBox.SelectionStart, _textBox.TextLength);
        line = _textBox.GetLineFromCharIndex(pos) + 1;
        int lineStart = _textBox.GetFirstCharIndexFromLine(line - 1);
        if (lineStart < 0) lineStart = 0;
        column = pos - lineStart + 1;
    }

    private void RegisterTextChange()
    {
        if (!_undoGroupOpen)
        {
            _undoStack.Push(new EditorSnapshot(
                _lastKnownText, _lastSelectionStart, _lastSelectionLength));
            _undoGroupOpen = true;
        }
        _redoStack.Clear();
        _lastSelectionStart = _textBox.SelectionStart;
        _lastSelectionLength = _textBox.SelectionLength;
        _undoGroupingTimer.Stop();
        _undoGroupingTimer.Start();
    }

    private void RememberCurrentState()
    {
        _lastKnownText = _textBox.Text;
        _lastSelectionStart = _textBox.SelectionStart;
        _lastSelectionLength = _textBox.SelectionLength;
    }

    private void RestoreFrom(Stack<EditorSnapshot> source, Stack<EditorSnapshot> destination)
    {
        _undoGroupingTimer.Stop();
        _undoGroupOpen = false;
        if (source.Count == 0) return;

        destination.Push(new EditorSnapshot(
            _textBox.Text, _textBox.SelectionStart, _textBox.SelectionLength));

        EditorSnapshot snapshot = source.Pop();
        _isRestoring = true;
        try
        {
            _textBox.Text = snapshot.Text;
            int start = Math.Clamp(snapshot.SelectionStart, 0, _textBox.TextLength);
            int length = Math.Clamp(snapshot.SelectionLength, 0, _textBox.TextLength - start);
            _textBox.Select(start, length);
            _textBox.ClearUndo();
            RememberCurrentState();
            _highlightDirty = true;
            _lastVisibleStartChar = -1;
            _lastVisibleEndChar = -1;
        }
        finally { _isRestoring = false; }

        ScheduleHighlight();
        TextChanged?.Invoke(this, EventArgs.Empty);
    }

    private void HandleEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.Z) { Undo(); e.Handled = true; e.SuppressKeyPress = true; }
        else if (e.Control && e.KeyCode == Keys.Y) { Redo(); e.Handled = true; e.SuppressKeyPress = true; }
    }

    public void Cut() => _textBox.Cut();
    public void Copy() => _textBox.Copy();
    public void Paste() => _textBox.Paste();
    public void DeleteSelection() => _textBox.SelectedText = string.Empty;
    public void SelectAllText() => _textBox.SelectAll();

    public void SetFontSize(float size)
    {
        _textBox.Font = new Font("Consolas", size);
        _highlightDirty = true;
        _lastVisibleStartChar = -1;
        _lastVisibleEndChar = -1;
        ScheduleHighlight();
    }

    public void GoToPosition(int line, int column = 1, int length = 1)
    {
        if (line < 1) line = 1;
        var lines = _textBox.Lines;
        if (lines.Length == 0) return;
        line = Math.Min(line, lines.Length);
        int start = _textBox.GetFirstCharIndexFromLine(line - 1);
        if (start < 0) start = 0;
        int index = Math.Min(start + Math.Max(0, column - 1), _textBox.TextLength);
        _textBox.Focus();
        _textBox.Select(index, Math.Clamp(length, 0, _textBox.TextLength - index));
        _textBox.ScrollToCaret();
    }

    public void HighlightError(int line, int column, int length = 1)
    {
        int start = GetIndex(line, column);
        if (start < 0 || start >= _textBox.TextLength) return;
        int count = Math.Clamp(length, 1, _textBox.TextLength - start);
        _textBox.Select(start, count);
        _textBox.SelectionBackColor = Color.MistyRose;
        _textBox.SelectionColor = Color.DarkRed;
        _textBox.Select(start, 0);
    }

    public void ClearErrorHighlights()
    {
        _highlightDirty = true;
        _lastVisibleStartChar = -1;
        _lastVisibleEndChar = -1;
        ApplySyntaxHighlighting();
    }

    private int GetIndex(int line, int column)
    {
        if (line < 1 || line > _textBox.Lines.Length) return -1;
        int start = _textBox.GetFirstCharIndexFromLine(line - 1);
        return start < 0 ? -1 : Math.Min(start + Math.Max(0, column - 1), _textBox.TextLength);
    }

    private void ScheduleHighlight()
    {
        if (_highlighting) return;
        _highlightTimer.Stop();
        _highlightTimer.Start();
    }

    private void ApplySyntaxHighlighting()
    {
        if (_highlighting || IsDisposed || !_textBox.IsHandleCreated) return;
        if (_textBox.ClientSize.Width <= 0 || _textBox.ClientSize.Height <= 0) return;

        int textLength = _textBox.TextLength;
        if (textLength == 0)
        {
            _lastVisibleStartChar = 0;
            _lastVisibleEndChar = 0;
            _highlightDirty = false;
            return;
        }

        int firstVisibleChar = _textBox.GetCharIndexFromPosition(new Point(0, 0));
        if (firstVisibleChar < 0) firstVisibleChar = 0;
        int lastVisibleChar = _textBox.GetCharIndexFromPosition(
            new Point(_textBox.ClientSize.Width - 1, _textBox.ClientSize.Height - 1));
        if (lastVisibleChar < 0 || lastVisibleChar >= textLength)
            lastVisibleChar = textLength - 1;
        if (firstVisibleChar > lastVisibleChar) firstVisibleChar = lastVisibleChar;

        int totalLines = _textBox.GetLineFromCharIndex(textLength) + 1;
        int firstLine = Math.Max(0,
            _textBox.GetLineFromCharIndex(firstVisibleChar) - HighlightMarginLines);
        int lastLine = Math.Min(totalLines - 1,
            _textBox.GetLineFromCharIndex(lastVisibleChar) + HighlightMarginLines);

        int regionStart = _textBox.GetFirstCharIndexFromLine(firstLine);
        if (regionStart < 0) regionStart = 0;
        int regionEnd = (lastLine + 1 < totalLines)
            ? _textBox.GetFirstCharIndexFromLine(lastLine + 1)
            : textLength;
        if (regionEnd < 0) regionEnd = textLength;

        if (!_highlightDirty
            && regionStart == _lastVisibleStartChar
            && regionEnd == _lastVisibleEndChar)
        {
            return;
        }

        _highlightDirty = false;
        _lastVisibleStartChar = regionStart;
        _lastVisibleEndChar = regionEnd;

        _highlighting = true;
        IntPtr hWnd = _textBox.Handle;

        NativePoint scrollPosition = new();
        SendMessage(hWnd, EM_GETSCROLLPOS, IntPtr.Zero, ref scrollPosition);
        int selectionStart = _textBox.SelectionStart;
        int selectionLength = _textBox.SelectionLength;

        SendMessageNative(hWnd, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
        try
        {
            _textBox.SuspendLayout();

            _textBox.Select(regionStart, regionEnd - regionStart);
            _textBox.SelectionColor = DefaultForeground;
            _textBox.SelectionBackColor = DefaultBackground;

            string source = _textBox.Text;
            foreach (Match match in TokenRegex.Matches(source))
            {
                if (match.Index + match.Length <= regionStart) continue;
                if (match.Index >= regionEnd) break;

                Color? color = match.Groups["comment"].Success ? Color.ForestGreen :
                    match.Groups["string"].Success ? Color.Brown :
                    match.Groups["preprocessor"].Success ? Color.MediumPurple :
                    match.Groups["number"].Success ? Color.DarkCyan :
                    match.Groups["keyword"].Success ? Color.Blue :
                    match.Groups["type"].Success ? Color.Teal : null;
                if (!color.HasValue) continue;

                int start = Math.Max(match.Index, regionStart);
                int end = Math.Min(match.Index + match.Length, regionEnd);
                _textBox.Select(start, end - start);
                _textBox.SelectionColor = color.Value;
            }

            int safeStart = Math.Min(selectionStart, textLength);
            int safeLength = Math.Min(selectionLength, Math.Max(0, textLength - safeStart));
            _textBox.Select(safeStart, safeLength);
            SendMessage(hWnd, EM_SETSCROLLPOS, IntPtr.Zero, ref scrollPosition);
        }
        finally
        {
            _textBox.ResumeLayout();
            SendMessageNative(hWnd, WM_SETREDRAW, new IntPtr(1), IntPtr.Zero);
            RedrawWindow(hWnd, IntPtr.Zero, IntPtr.Zero,
                RDW_INVALIDATE | RDW_ERASE | RDW_UPDATENOW | RDW_ALLCHILDREN);
            _highlighting = false;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _highlightTimer.Dispose();
            _undoGroupingTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}