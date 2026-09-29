using System.Runtime.InteropServices;
using System.Security;
using DefenseClaw.Core.Config;

namespace DefenseClaw.App.Services.Wizards;

/// <summary>
/// The one place a wizard's typed secret is read out of the <see cref="SecureString"/> the password box
/// holds.
/// <para>
/// <b>Why the value is kept as a <see cref="SecureString"/> and not a string property.</b> A
/// <c>PasswordBox</c> keeps what is typed in a <see cref="SecureString"/> and only builds a managed string when
/// <c>Password</c> is read. The wizard view-model therefore holds the box's own <c>SecurePassword</c> copy — never a
/// <c>string</c> — and this class turns it into a <see cref="SecretValue"/> only at the moment a run needs it.
/// Checking what was typed (does it contain a newline the CLI would refuse, is it only spaces) is done on the
/// unmanaged buffer without making a string at all, so a keystroke costs no managed copy of the secret.
/// </para>
/// <para>
/// A managed string cannot be zeroed, so the <see cref="SecretValue"/> built for a run lives until the garbage
/// collector reclaims it. What this design bounds is <i>how long the wizard keeps the value reachable</i>: from
/// the keystroke to the end of the run, as encrypted memory, then not at all.
/// </para>
/// </summary>
internal static class SecretEntry
{
    /// <summary>What a typed value looks like, worked out without materialising it.</summary>
    /// <param name="TrimmedLength">Characters left after leading and trailing whitespace is dropped.</param>
    /// <param name="HasControlCharacter">
    /// A newline, tab, NUL or any other control character <i>inside</i> the value. The CLI refuses these outright
    /// (<c>sanitize_dotenv_value</c>: a raw newline would inject a second <c>KEY=VALUE</c> line into
    /// <c>.env</c>), and a value with one is almost always a bad paste.
    /// </param>
    internal readonly record struct Shape(int TrimmedLength, bool HasControlCharacter)
    {
        public bool IsEmpty => TrimmedLength == 0;
    }

    /// <summary>Inspects <paramref name="value"/> in place. <c>null</c> and empty are an empty shape.</summary>
    public static Shape Inspect(SecureString? value)
    {
        if (value is null || value.Length == 0)
        {
            return default;
        }

        var buffer = SecureStringMarshal.SecureStringToGlobalAllocUnicode(value);
        try
        {
            var (first, last) = TrimmedRange(buffer, value.Length);
            if (first > last)
            {
                return default;
            }

            var hasControl = false;
            for (var i = first; i <= last && !hasControl; i++)
            {
                hasControl = char.IsControl((char)Marshal.ReadInt16(buffer, i * sizeof(char)));
            }

            return new Shape(last - first + 1, hasControl);
        }
        finally
        {
            Marshal.ZeroFreeGlobalAllocUnicode(buffer);
        }
    }

    /// <summary>
    /// The value as a <see cref="SecretValue"/>, with leading and trailing whitespace removed (a token never
    /// starts or ends with any, and a pasted one often does). <c>null</c> when nothing is left, or when the value
    /// contains a control character (see <see cref="Shape"/>) — a value the CLI would refuse is never sent.
    /// </summary>
    public static SecretValue? ToSecret(SecureString? value)
    {
        if (value is null || value.Length == 0)
        {
            return null;
        }

        var buffer = SecureStringMarshal.SecureStringToGlobalAllocUnicode(value);
        try
        {
            var (first, last) = TrimmedRange(buffer, value.Length);
            if (first > last)
            {
                return null;
            }

            for (var i = first; i <= last; i++)
            {
                if (char.IsControl((char)Marshal.ReadInt16(buffer, i * sizeof(char))))
                {
                    return null;
                }
            }

            // One managed string, and it is already trimmed: no untrimmed copy is left behind.
            return new SecretValue(Marshal.PtrToStringUni(buffer + (first * sizeof(char)), last - first + 1));
        }
        finally
        {
            Marshal.ZeroFreeGlobalAllocUnicode(buffer);
        }
    }

    /// <summary>Indexes of the first and last non-whitespace character; <c>first &gt; last</c> when there is none.</summary>
    private static (int First, int Last) TrimmedRange(IntPtr buffer, int length)
    {
        var first = 0;
        while (first < length && char.IsWhiteSpace((char)Marshal.ReadInt16(buffer, first * sizeof(char))))
        {
            first++;
        }

        var last = length - 1;
        while (last >= first && char.IsWhiteSpace((char)Marshal.ReadInt16(buffer, last * sizeof(char))))
        {
            last--;
        }

        return (first, last);
    }
}
