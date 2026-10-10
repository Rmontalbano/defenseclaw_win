using System.Text;
using DefenseClaw.Core.IO;

namespace DefenseClaw.Core.Cli;

/// <summary>
/// Reads a child's output line by line the way <see cref="System.Diagnostics.Process.BeginOutputReadLine"/> does (a line ends at <c>\n</c>, <c>\r\n</c>
/// or a lone <c>\r</c>), but keeps at most <c>maxChars</c> of any one line (CUST-250). Process's own reader buffers a line whole, so a child that
/// writes hundreds of MB without a newline is held in memory until the run's timeout; here the rest of such a line is read and dropped, and the
/// line that is returned ends with <see cref="ReadLimits.CliLineTruncatedMarker"/> so nobody mistakes it for the whole.
/// <para>
/// Secret scrubbing runs on each returned line, after the cut, so a secret that straddles the cut would leave its first part behind (CUST-341).
/// A cut therefore (1) passes the kept text through <c>sanitizeCut</c> (the caller's scrubber, which also drops a trailing partial secret) and
/// (2) drops a trailing run of token-like characters (at most <see cref="MaxTokenTailChars"/>), which covers a secret the caller never registered.
/// </para>
/// </summary>
internal sealed class BoundedLineReader(TextReader reader, int maxChars = ReadLimits.CliLineChars, Func<string, string>? sanitizeCut = null)
{
    private const int ChunkChars = 8192;

    /// <summary>The longest run of token-like characters dropped at a cut (a real API key or token is far shorter).</summary>
    internal const int MaxTokenTailChars = 1024;

    private readonly char[] _chunk = new char[ChunkChars];
    private int _position;
    private int _length;
    private bool _skipLineFeed;

    /// <summary>The next line, or null at the end of the stream.</summary>
    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken = default)
    {
        StringBuilder? line = null;
        var truncated = false;
        var any = false;

        while (true)
        {
            if (_position >= _length)
            {
                _length = await reader.ReadAsync(_chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
                _position = 0;
                if (_length == 0)
                {
                    return any ? Finish(line, truncated, sanitizeCut) : null;
                }
            }

            if (_skipLineFeed)
            {
                _skipLineFeed = false;
                if (_chunk[_position] == '\n')
                {
                    _position++;
                    continue;
                }
            }

            var start = _position;
            while (_position < _length && _chunk[_position] is not ('\n' or '\r'))
            {
                _position++;
            }

            var count = _position - start;
            if (count > 0)
            {
                any = true;
                line ??= new StringBuilder();
                var room = maxChars - line.Length;
                if (count > room)
                {
                    truncated = true;
                    count = Math.Max(0, room);
                }

                _ = line.Append(_chunk, start, count);
            }

            if (_position < _length)
            {
                var terminator = _chunk[_position++];
                _skipLineFeed = terminator == '\r';
                return Finish(line, truncated, sanitizeCut);
            }

            // The chunk ended mid-line: whatever of it counts is in the builder, the next read continues the line.
        }
    }

    private static string Finish(StringBuilder? line, bool truncated, Func<string, string>? sanitizeCut)
    {
        if (line is null)
        {
            return string.Empty;
        }

        if (!truncated)
        {
            return line.ToString();
        }

        var kept = line.ToString();
        if (sanitizeCut is not null)
        {
            kept = sanitizeCut(kept);
        }

        // What the scrubber cannot know about: a token-like run ending at the cut may be the front of a longer secret.
        var end = kept.Length;
        var floor = Math.Max(0, end - MaxTokenTailChars);
        while (end > floor && IsTokenChar(kept[end - 1]))
        {
            end--;
        }

        // A run longer than the bound is not a token: keep it rather than empty the line.
        if (end == floor && floor > 0)
        {
            end = kept.Length;
        }

        return string.Concat(kept.AsSpan(0, end), ReadLimits.CliLineTruncatedMarker);
    }

    /// <summary>The characters API keys, bearer tokens, JWTs and base64 are made of.</summary>
    private static bool IsTokenChar(char c) =>
        char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '~' or '+' or '/' or '=';
}
