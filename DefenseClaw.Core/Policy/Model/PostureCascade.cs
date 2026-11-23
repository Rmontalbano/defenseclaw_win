namespace DefenseClaw.Core.Policy.Model;

/// <summary>
/// What the Posture detail needs to know about the guardrail cascade of runtime 1.0.10 (<see cref="GuardrailCascade"/>): whether config.yaml sets
/// it, and the CRITICAL floor to show in the severity matrix. Null on a runtime without the cascade, which leaves the matrix and its text exactly as
/// they were.
/// </summary>
/// <param name="Set">config.yaml sets <c>guardrail.cascade</c> or a scoped cascade, which can change what the pack levels predict.</param>
/// <param name="CriticalFloor"><c>admin.critical_floor</c> (<c>log</c>, <c>alert</c>, <c>confirm</c>, <c>block</c>): a proven CRITICAL ends at least there.</param>
public sealed record PostureCascade(bool Set, string CriticalFloor)
{
    /// <summary>The line under the matrix while the cascade is set.</summary>
    public const string MatrixCaveat = "guardrail.cascade is set: it can change the outcome these pack levels predict for this scope; see Cascade.";

    /// <summary>The context of a config.yaml read.</summary>
    public static PostureCascade Of(GuardrailCascade cascade)
    {
        ArgumentNullException.ThrowIfNull(cascade);
        return new PostureCascade(cascade.IsSet, cascade.CriticalFloor);
    }
}
