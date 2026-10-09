using System.Text;

namespace DefenseClaw.Core.Cli;

/// <summary>
/// A very small terminal: just enough of a VT100/xterm screen to turn what a pseudo-console prints into the lines a person would have
/// read on it, and to say what is on the row the cursor is on (that is where a prompt is).
/// <para>
/// <b>Why a screen and not a regex that strips escape sequences.</b> A pseudo-console does not forward the child's bytes; it re-draws its own
/// screen as a VT stream. The line <c>Value: </c> came out as <c>V</c>, a window-title sequence, a cursor-visibility toggle, then
/// <c>alue: </c> - and after the value was typed the renderer sent the cursor home and drew the next line from there. Stripping the sequences
/// and joining what is left reads this right by luck; the moment a repaint overwrites a row that was already drawn (a progress line, a
/// redraw after typing) the stripped text has the row twice. A screen that honours the cursor moves, <c>CR</c>/<c>LF</c> and the erase
/// sequences always holds the text that was on screen.
/// </para>
/// <para>
/// <b>What it models:</b> printable text (with the deferred wrap at the right edge), <c>CR</c>, <c>LF</c>, <c>BS</c>, <c>TAB</c>, cursor
/// movement (<c>CUU CUD CUF CUB CNL CPL CHA VPA CUP HVP</c>, save and restore), erase in line and in display, insert, delete and erase of
/// characters. <b>What it ignores:</b> colours and attributes, modes (including the alternate screen), scroll regions, window titles and every
/// other string sequence. It never answers a query (a reply typed into the child's input would be read by a prompt as characters of the
/// value); the queries it saw are listed in <see cref="Queries"/> for a diagnostic.
/// </para>
/// <para>
/// Lines are handed out once, in order, as soon as the cursor has left them downwards (<see cref="TakeLines"/>), and the rest at the end
/// (<see cref="Flush"/>). A row drawn again after it was handed out is not handed out again.
/// </para>
/// </summary>
internal sealed class VtScreen
{
    private const int MaxSequenceLength = 64;
    private const int HistoryToKeep = 2_000;

    private readonly int _columns;
    private readonly int _rows;
    private readonly List<StringBuilder> _lines = new() { new StringBuilder() };

    /// <summary>Per row: true when the row is the continuation of the one above it because the text wrapped at the right edge. A wrapped line is one line of the transcript.</summary>
    private readonly List<bool> _continues = new() { false };
    private readonly StringBuilder _parameters = new();
    private readonly List<string> _queries = new();

    private State _state;
    private int _top;
    private int _cursorRow;
    private int _cursorColumn;
    private int _taken;
    private int _savedRow;
    private int _savedColumn;
    private bool _intermediate;
    private bool _overlong;

    public VtScreen(int columns = PseudoConsole.DefaultColumns, int rows = PseudoConsole.DefaultRows)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 2);
        _columns = columns;
        _rows = rows;
    }

    private enum State
    {
        Ground,
        Escape,
        EscapeIntermediate,
        Csi,
        Text,
        TextEscape,
    }

    /// <summary>The text on the cursor's row, from its left edge up to the cursor: what is showing in front of the cursor, a prompt when there is one.</summary>
    public string CursorLine
    {
        get
        {
            var row = EnsureRow(_top + _cursorRow);
            return row.ToString(0, Math.Min(_cursorColumn, row.Length));
        }
    }

    /// <summary>The cursor's row counted from the first row ever drawn, so two readings of <see cref="CursorLine"/> can tell whether they are the same row.</summary>
    public int CursorRow => _top + _cursorRow;

    /// <summary>The sequences that asked the terminal something (cursor position, device attributes), as text. Never answered.</summary>
    public IReadOnlyList<string> Queries => _queries;

    /// <summary>Draws <paramref name="text"/>. A sequence may end in a later call than the one it began in.</summary>
    public void Write(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (var c in text)
        {
            Process(c);
        }
    }

    /// <summary>The lines the cursor has moved down past since the last call (a line that wrapped is one), as text without trailing spaces, oldest first.</summary>
    public IReadOnlyList<string> TakeLines()
    {
        _ = EnsureRow(_top + _cursorRow);

        // The cursor's row may be the tail of a line that wrapped: that line is not finished until the cursor leaves it.
        var through = _top + _cursorRow;
        while (through > _taken && _continues[through])
        {
            through--;
        }

        if (through <= _taken)
        {
            return Array.Empty<string>();
        }

        var lines = Join(_taken, through);
        _taken = through;
        TrimHistory();
        return lines;
    }

    /// <summary>Everything not handed out yet, the cursor's row included, without the blank rows at the end. For the end of the run.</summary>
    public IReadOnlyList<string> Flush()
    {
        var end = _lines.Count;
        while (end > _taken && IsBlank(_lines[end - 1]))
        {
            end--;
        }

        var lines = Join(_taken, Math.Max(_taken, end));
        _taken = Math.Max(_taken, _lines.Count);
        return lines;
    }

    /// <summary>Rows <paramref name="from"/> up to <paramref name="to"/> as lines: a row that wrapped joins the one before it.</summary>
    private List<string> Join(int from, int to)
    {
        var lines = new List<string>(Math.Max(0, to - from));
        var current = new StringBuilder();
        for (var i = from; i < to; i++)
        {
            if (!_continues[i] && i > from)
            {
                lines.Add(current.ToString().TrimEnd());
                current.Clear();
            }

            current.Append(_lines[i]);
        }

        if (to > from)
        {
            lines.Add(current.ToString().TrimEnd());
        }

        return lines;
    }

    private static bool IsBlank(StringBuilder row)
    {
        for (var i = 0; i < row.Length; i++)
        {
            if (!char.IsWhiteSpace(row[i]))
            {
                return false;
            }
        }

        return true;
    }

    private void Process(char c)
    {
        switch (_state)
        {
            case State.Ground:
                Ground(c);
                break;

            case State.Escape:
                Escape(c);
                break;

            case State.EscapeIntermediate:
                // ESC ( B and friends: a character set designation. The byte after the intermediate ends it.
                if (c == '\x1b')
                {
                    _state = State.Escape;
                }
                else if (c is >= (char)0x30 and <= (char)0x7E)
                {
                    _state = State.Ground;
                }

                break;

            case State.Csi:
                Csi(c);
                break;

            case State.Text:
                // An OSC, DCS, APC, PM or SOS string: ended by BEL or by ESC \ . Nothing in it is kept.
                if (c == '\a')
                {
                    _state = State.Ground;
                }
                else if (c == '\x1b')
                {
                    _state = State.TextEscape;
                }

                break;

            case State.TextEscape:
                if (c == '\\')
                {
                    _state = State.Ground;
                }
                else
                {
                    // Not a terminator: the escape starts a sequence of its own.
                    _state = State.Escape;
                    Escape(c);
                }

                break;
        }
    }

    private void Ground(char c)
    {
        switch (c)
        {
            case '\x1b':
                _state = State.Escape;
                break;
            case '\r':
                _cursorColumn = 0;
                break;
            case '\n':
            case '\v':
            case '\f':
                LineFeed();
                break;
            case '\b':
                _cursorColumn = Math.Max(0, Math.Min(_cursorColumn, _columns - 1) - 1);
                break;
            case '\t':
                _cursorColumn = Math.Min(_columns - 1, ((Math.Min(_cursorColumn, _columns - 1) / 8) + 1) * 8);
                break;
            default:
                if (c >= ' ' && c != '\x7f' && !(c >= '\x80' && c <= '\x9f'))
                {
                    Print(c);
                }

                break;
        }
    }

    private void Escape(char c)
    {
        _state = State.Ground;
        switch (c)
        {
            case '[':
                _parameters.Clear();
                _intermediate = false;
                _overlong = false;
                _state = State.Csi;
                break;
            case ']':
            case 'P':
            case 'X':
            case '^':
            case '_':
                _state = State.Text;
                break;
            case '7':
                _savedRow = _cursorRow;
                _savedColumn = _cursorColumn;
                break;
            case '8':
                _cursorRow = _savedRow;
                _cursorColumn = _savedColumn;
                break;
            case 'D':
                LineFeed();
                break;
            case 'E':
                _cursorColumn = 0;
                LineFeed();
                break;
            case 'M':
                _cursorRow = Math.Max(0, _cursorRow - 1);
                break;
            case 'c':
                // Full reset: what was drawn stays in the transcript, the screen starts over below it.
                ScrollContentAway();
                _cursorRow = 0;
                _cursorColumn = 0;
                break;
            case '\x1b':
                _state = State.Escape;
                break;
            default:
                if (c is >= ' ' and <= '/')
                {
                    _state = State.EscapeIntermediate;
                }

                break;
        }
    }

    private void Csi(char c)
    {
        if (c == '\x1b')
        {
            // An escape inside a sequence abandons it and starts another.
            _state = State.Escape;
            return;
        }

        if (c >= '@' && c <= '~')
        {
            _state = State.Ground;
            if (!_overlong)
            {
                Dispatch(c);
            }

            return;
        }

        if (c >= ' ' && c <= '?')
        {
            if (c <= '/')
            {
                _intermediate = true;
            }

            if (_parameters.Length < MaxSequenceLength)
            {
                _parameters.Append(c);
            }
            else
            {
                _overlong = true;
            }

            return;
        }

        // A control character in the middle of a sequence: acted on, the sequence goes on.
        if (c < ' ')
        {
            Ground(c);
        }
    }

    private void Dispatch(char final)
    {
        var text = _parameters.ToString();
        var marker = text.Length > 0 && text[0] is '?' or '>' or '<' or '=';
        var numbers = Numbers(marker ? text[1..] : text);

        int Count(int index) => index < numbers.Length && numbers[index] is { } v && v > 0 ? v : 1;
        int Mode() => numbers.Length > 0 && numbers[0] is { } v ? v : 0;

        // A sequence with an intermediate byte (cursor shape, soft reset) is about things this screen does not model.
        if (_intermediate)
        {
            return;
        }

        switch (final)
        {
            case 'A':
                _cursorRow = Math.Max(0, _cursorRow - Count(0));
                break;
            case 'B':
            case 'e':
                _cursorRow = Math.Min(_rows - 1, _cursorRow + Count(0));
                EnsureRow(_top + _cursorRow);
                break;
            case 'C':
            case 'a':
                _cursorColumn = Math.Min(_columns - 1, Math.Min(_cursorColumn, _columns - 1) + Count(0));
                break;
            case 'D':
                _cursorColumn = Math.Max(0, Math.Min(_cursorColumn, _columns - 1) - Count(0));
                break;
            case 'E':
                _cursorRow = Math.Min(_rows - 1, _cursorRow + Count(0));
                _cursorColumn = 0;
                EnsureRow(_top + _cursorRow);
                break;
            case 'F':
                _cursorRow = Math.Max(0, _cursorRow - Count(0));
                _cursorColumn = 0;
                break;
            case 'G':
            case '`':
                _cursorColumn = Math.Min(_columns - 1, Count(0) - 1);
                break;
            case 'd':
                _cursorRow = Math.Min(_rows - 1, Count(0) - 1);
                EnsureRow(_top + _cursorRow);
                break;
            case 'H':
            case 'f':
                _cursorRow = Math.Min(_rows - 1, Count(0) - 1);
                _cursorColumn = Math.Min(_columns - 1, Count(1) - 1);
                EnsureRow(_top + _cursorRow);
                break;
            case 'J':
                EraseDisplay(Mode());
                break;
            case 'K':
                EraseLine(Mode());
                break;
            case '@':
                InsertCharacters(Count(0));
                break;
            case 'P':
                DeleteCharacters(Count(0));
                break;
            case 'X':
                EraseCharacters(Count(0));
                break;
            case 's' when !marker && text.Length == 0:
                _savedRow = _cursorRow;
                _savedColumn = _cursorColumn;
                break;
            case 'u' when !marker && text.Length == 0:
                _cursorRow = _savedRow;
                _cursorColumn = _savedColumn;
                break;
            case 'n' when !marker && Mode() == 6:
                _queries.Add("cursor position report (CSI 6 n)");
                break;
            case 'c':
                _queries.Add("device attributes (CSI c)");
                break;
        }
    }

    private static int?[] Numbers(string parameters)
    {
        if (parameters.Length == 0)
        {
            return Array.Empty<int?>();
        }

        var parts = parameters.Split(';');
        var numbers = new int?[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            // A sub-parameter ("38:5:196") only refines the first number.
            var head = parts[i].Split(':')[0];
            numbers[i] = int.TryParse(head, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : null;
        }

        return numbers;
    }

    private void Print(char c)
    {
        if (_cursorColumn >= _columns)
        {
            // Past the last column: the text wraps onto the next row (the deferred wrap of a real terminal), and it is still the same line.
            _cursorColumn = 0;
            LineFeed();
            _continues[_top + _cursorRow] = true;
        }

        var row = EnsureRow(_top + _cursorRow);
        if (_cursorColumn < row.Length)
        {
            row[_cursorColumn] = c;
        }
        else
        {
            row.Append(' ', _cursorColumn - row.Length).Append(c);
        }

        _cursorColumn++;
    }

    private void LineFeed()
    {
        if (_cursorRow >= _rows - 1)
        {
            // At the bottom: the screen scrolls, and the row that left the top is history.
            _top++;
        }
        else
        {
            _cursorRow++;
        }

        _ = EnsureRow(_top + _cursorRow);
        if (_cursorColumn >= _columns)
        {
            _cursorColumn = _columns - 1;
        }
    }

    private StringBuilder EnsureRow(int absolute)
    {
        while (_lines.Count <= absolute)
        {
            _lines.Add(new StringBuilder());
            _continues.Add(false);
        }

        return _lines[absolute];
    }

    private void EraseLine(int mode)
    {
        var row = EnsureRow(_top + _cursorRow);
        var column = Math.Min(_cursorColumn, _columns - 1);
        switch (mode)
        {
            case 0:
                if (row.Length > column)
                {
                    row.Length = column;
                }

                break;
            case 1:
                for (var i = 0; i <= column && i < row.Length; i++)
                {
                    row[i] = ' ';
                }

                break;
            default:
                row.Clear();
                break;
        }
    }

    private void EraseDisplay(int mode)
    {
        switch (mode)
        {
            case 0:
                EraseLine(0);
                for (var i = _top + _cursorRow + 1; i < _lines.Count && i < _top + _rows; i++)
                {
                    _lines[i].Clear();
                }

                break;
            case 1:
                for (var i = _top; i < _top + _cursorRow && i < _lines.Count; i++)
                {
                    _lines[i].Clear();
                }

                EraseLine(1);
                break;
            default:
                // The whole screen: what is on it stays in the transcript, and the screen is blank below it.
                ScrollContentAway();
                break;
        }
    }

    /// <summary>Clears the screen the way scrolling it out of sight does: the rows with content stay as history above a fresh, empty screen.</summary>
    private void ScrollContentAway()
    {
        var last = -1;
        for (var i = Math.Min(_lines.Count, _top + _rows) - 1; i >= _top; i--)
        {
            if (!IsBlank(_lines[i]))
            {
                last = i;
                break;
            }
        }

        if (last < 0)
        {
            return;
        }

        _top = last + 1;
        _ = EnsureRow(_top + _cursorRow);
    }

    private void InsertCharacters(int count)
    {
        var row = EnsureRow(_top + _cursorRow);
        var column = Math.Min(_cursorColumn, _columns - 1);
        if (column > row.Length)
        {
            row.Append(' ', column - row.Length);
        }

        row.Insert(column, new string(' ', count));
        if (row.Length > _columns)
        {
            row.Length = _columns;
        }
    }

    private void DeleteCharacters(int count)
    {
        var row = EnsureRow(_top + _cursorRow);
        var column = Math.Min(_cursorColumn, _columns - 1);
        if (column < row.Length)
        {
            row.Remove(column, Math.Min(count, row.Length - column));
        }
    }

    private void EraseCharacters(int count)
    {
        var row = EnsureRow(_top + _cursorRow);
        var column = Math.Min(_cursorColumn, _columns - 1);
        for (var i = column; i < column + count && i < row.Length; i++)
        {
            row[i] = ' ';
        }
    }

    private void TrimHistory()
    {
        // Rows above the screen that have been handed out can no longer be addressed or changed.
        var removable = Math.Min(_taken, _top) - HistoryToKeep;
        if (removable > HistoryToKeep)
        {
            _lines.RemoveRange(0, removable);
            _continues.RemoveRange(0, removable);
            _top -= removable;
            _taken -= removable;
            _savedRow = Math.Min(_savedRow, _rows - 1);
        }
    }
}
