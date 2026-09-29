using System.Security.Cryptography;
using System.Text;

namespace DefenseClaw.Core.Config;

/// <summary>
/// A string value that must never be printed. <see cref="ToString"/> always yields
/// <see cref="Redacted"/>, so a secret cannot leak through interpolation, logging,
/// or exception messages by accident. The raw value is only reachable via
/// <see cref="Reveal"/>, which is deliberately noisy at the call site.
/// </summary>
public sealed class SecretValue : IEquatable<SecretValue>
{
    /// <summary>Placeholder emitted in place of the real value.</summary>
    public const string Redacted = "***REDACTED***";

    private readonly string _value;

    public SecretValue(string value)
    {
        _value = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Length of the underlying value. Safe to log.</summary>
    public int Length => _value.Length;

    public bool IsEmpty => _value.Length == 0;

    /// <summary>
    /// Returns the raw secret. Only call this when handing the value to a sink that
    /// genuinely needs it (an Authorization header, a child process's stdin or environment block).
    /// </summary>
    public string Reveal() => _value;

    /// <summary>Constant-time comparison against a candidate plaintext.</summary>
    public bool Matches(string? candidate)
    {
        if (candidate is null)
        {
            return false;
        }

        var a = Encoding.UTF8.GetBytes(_value);
        var b = Encoding.UTF8.GetBytes(candidate);
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>
    /// Replaces every occurrence of the secret in <paramref name="text"/> with
    /// <see cref="Redacted"/>. Used to scrub subprocess output before it reaches the UI.
    /// </summary>
    public string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text) || _value.Length == 0)
        {
            return text ?? string.Empty;
        }

        return text.Replace(_value, Redacted, StringComparison.Ordinal);
    }

    /// <summary>True when the secret appears anywhere inside <paramref name="text"/>.</summary>
    public bool AppearsIn(string? text) =>
        !string.IsNullOrEmpty(text) && _value.Length > 0 && text.Contains(_value, StringComparison.Ordinal);

    public bool Equals(SecretValue? other) => other is not null && Matches(other._value);

    public override bool Equals(object? obj) => obj is SecretValue other && Equals(other);

    // Deliberately weak: hashing the plaintext would let a hash-dump leak the value.
    public override int GetHashCode() => _value.Length;

    public override string ToString() => Redacted;
}
