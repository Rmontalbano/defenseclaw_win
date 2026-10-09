using System.Text.RegularExpressions;
using DefenseClaw.Core.Config;

namespace DefenseClaw.Core.Cli;

/// <summary>
/// A value to type when a prompt shows: the prompt, as a pattern for the text in front of the cursor, and the <see cref="SecretValue"/>
/// that answers it. Used once.
/// </summary>
internal sealed class PtyAnswer
{
    public PtyAnswer(Regex prompt, SecretValue value)
    {
        Prompt = prompt ?? throw new ArgumentNullException(nameof(prompt));
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Matched against the row the cursor is on, up to the cursor, without trailing spaces. Anchor it: <c>  NAME:</c> is a prompt, a sentence that merely ends in <c>NAME:</c> is not.</summary>
    public Regex Prompt { get; }

    public SecretValue Value { get; }

    /// <summary>True once the value has been typed. An answer is never typed twice, however often the prompt is drawn.</summary>
    public bool Typed { get; internal set; }
}

/// <summary>
/// What is said to a command running in a pseudo-console: reads what it draws (<see cref="VtScreen"/>), notices the prompt it is waiting at,
/// hands back the keystrokes that answer it, and returns the transcript with every secret taken out of it.
/// <para>
/// <b>Typing is the only way a value leaves this class</b>, once, at a prompt that matched an answer's pattern and only after the output has
/// been quiet for a moment (<c>quiet</c> in <see cref="TakeInput"/>): a prompt that is the end of a line the child is still writing is not
/// a prompt. No other prompt is answered; a command that asks something this was not told about waits until the runner gives up on it.
/// </para>
/// <para>
/// <b>Scrubbing.</b> Every line goes through <see cref="SecretValue.Scrub"/> for each value, and for each value's <i>preview</i>: the
/// DefenseClaw CLI confirms a stored key with <c>Saved NAME = abcd…wxyz to ~/.defenseclaw/.env</c>, four characters from each end, and eight
/// characters of a key are still material. The preview is built the way the CLI builds it (more than eight characters: the first four, an
/// ellipsis, the last four), so the confirmation line reads <c>Saved NAME = ***REDACTED*** to ...</c>. This happens on whole lines, after
/// the screen has put them together, so a value the console split over two writes is still found. The value is never in the transcript
/// because it is never in anything the transcript is built from: it goes to the input pipe and nowhere else.
/// </para>
/// </summary>
internal sealed class PtyConversation
{
    /// <summary>The CLI's own masked preview of a value, as <c>mask()</c> in <c>defenseclaw/credentials.py</c> (0.8.10) builds it; empty when it would reveal nothing (it prints <c>****</c> for a short value).</summary>
    internal static string PreviewOf(string value) =>
        value.Length > 8 ? value[..4] + "…" + value[^4..] : string.Empty;

    private readonly VtScreen _screen;
    private readonly IReadOnlyList<PtyAnswer> _answers;
    private readonly SecretValue[] _scrubbed;
    private PtyAnswer? _armed;
    private int _heldBlank;
    private bool _emittedText;

    public PtyConversation(IReadOnlyList<PtyAnswer> answers, int columns = PseudoConsole.DefaultColumns, int rows = PseudoConsole.DefaultRows)
    {
        _answers = answers ?? throw new ArgumentNullException(nameof(answers));
        _screen = new VtScreen(columns, rows);

        var scrubbed = new List<SecretValue>();
        foreach (var answer in answers)
        {
            if (answer.Value.IsEmpty)
            {
                continue;
            }

            scrubbed.Add(answer.Value);
            if (PreviewOf(answer.Value.Reveal()) is { Length: > 0 } preview)
            {
                scrubbed.Add(new SecretValue(preview));
            }
        }

        _scrubbed = scrubbed.ToArray();
    }

    /// <summary>How many answers there are.</summary>
    public int Total => _answers.Count;

    /// <summary>How many values have been typed.</summary>
    public int Typed { get; private set; }

    /// <summary>True when every value has been typed.</summary>
    public bool AllTyped => Typed >= _answers.Count;

    /// <summary>True while the cursor sits behind a prompt that has an answer waiting to be typed.</summary>
    public bool PromptShowing => _armed is not null;

    /// <summary>The queries the child's terminal was asked and nobody answered (see <see cref="VtScreen.Queries"/>), for a failure's explanation.</summary>
    public IReadOnlyList<string> Queries => _screen.Queries;

    /// <summary>The text on the cursor's row in front of the cursor.</summary>
    public string CursorLine => _screen.CursorLine;

    /// <summary>Draws a piece of the child's output; returns the lines that are complete now, scrubbed.</summary>
    public IReadOnlyList<string> Feed(string chunk)
    {
        _screen.Write(chunk);

        _armed = null;
        var prompt = _screen.CursorLine.TrimEnd();
        if (prompt.Length > 0)
        {
            foreach (var answer in _answers)
            {
                if (!answer.Typed && answer.Prompt.IsMatch(prompt))
                {
                    _armed = answer;
                    break;
                }
            }
        }

        return Clean(_screen.TakeLines());
    }

    /// <summary>
    /// The keystrokes that answer the prompt that is showing (the value, then Enter), or null when none is showing or the output has not
    /// gone quiet yet. The caller writes them to the child's input; each answer comes out once.
    /// </summary>
    /// <param name="quiet">True when nothing has been drawn for a moment: the child has stopped writing and is waiting.</param>
    public string? TakeInput(bool quiet)
    {
        if (!quiet || _armed is not { } answer)
        {
            return null;
        }

        _armed = null;
        answer.Typed = true;
        Typed++;
        return answer.Value.Reveal() + "\r";
    }

    /// <summary>The rest of the transcript: what the screen still holds at the end of the run, scrubbed. The blank lines at the very end are dropped.</summary>
    public IReadOnlyList<string> Finish()
    {
        var rest = Clean(_screen.Flush());
        _heldBlank = 0;
        return rest;
    }

    /// <summary>
    /// Takes the secrets out of the lines. Blank lines are kept between text and held back at the edges: a transcript neither starts with
    /// them (the console's first row) nor ends with them (a CLI's closing <c>echo()</c>), but a blank line between two lines of text is the CLI's own.
    /// </summary>
    private IReadOnlyList<string> Clean(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
        {
            return lines;
        }

        var cleaned = new List<string>(lines.Count);
        foreach (var raw in lines)
        {
            var line = raw;
            foreach (var secret in _scrubbed)
            {
                line = secret.Scrub(line);
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                if (_emittedText)
                {
                    _heldBlank++;
                }

                continue;
            }

            for (; _heldBlank > 0; _heldBlank--)
            {
                cleaned.Add(string.Empty);
            }

            cleaned.Add(line);
            _emittedText = true;
        }

        return cleaned;
    }
}
