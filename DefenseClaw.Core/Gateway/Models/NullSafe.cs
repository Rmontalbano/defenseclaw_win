namespace DefenseClaw.Core.Gateway.Models;

/// <summary>
/// Null hygiene for the gateway payload models.
/// <para>
/// <b>Why the models need it.</b> The sidecar is written in Go, and a Go slice that was never
/// appended to is <c>nil</c>, which <c>encoding/json</c> writes as <c>null</c> — not <c>[]</c>.
/// The live payloads show it (<c>Fixtures\health.json</c> has <c>"discovered":null</c>), and a
/// gateway with no connector registered yet is exactly the state a freshly started or
/// half-configured install is in. System.Text.Json then calls the property setter with
/// <c>null</c>, <i>after</i> the <c>= Array.Empty&lt;T&gt;()</c> initializer, so the initializer
/// alone protects nothing: a <c>foreach</c> over <c>GatewayHealth.Connectors</c> in the poll loop
/// threw on the very payload it exists to parse. Every list-typed property here therefore
/// routes its setter through <see cref="ListOrEmpty{T}"/>.
/// </para>
/// </summary>
internal static class NullSafe
{
    /// <summary>
    /// <paramref name="value"/> itself when it holds no nulls; otherwise an empty list for a
    /// <c>null</c> list, or a copy without the null elements (a Go slice of pointers can carry
    /// them). Never null, and no element of the result is null.
    /// </summary>
    public static IReadOnlyList<T> ListOrEmpty<T>(IReadOnlyList<T>? value)
        where T : class
    {
        if (value is null || value.Count == 0)
        {
            return Array.Empty<T>();
        }

        for (var i = 0; i < value.Count; i++)
        {
            if (value[i] is null)
            {
                return value.Where(static item => item is not null).ToArray();
            }
        }

        return value;
    }
}
