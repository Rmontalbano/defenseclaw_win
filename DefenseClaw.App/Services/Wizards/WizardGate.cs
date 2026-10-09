using System.Collections.Concurrent;

namespace DefenseClaw.App.Services.Wizards;

/// <summary>
/// The small language a page or a field uses to say when it is shown: <see cref="WizardStep.VisibleWhenFieldId"/> /
/// <see cref="WizardField.VisibleWhenFieldId"/> together with their <c>VisibleWhenValues</c>.
/// <para>
/// <b>Three forms, one evaluator.</b> A plain id (<c>provider</c>) with values (<c>bedrock</c>) is shown when that field holds one of the values.
/// <c>a|b</c> as the id is any-of over fields (Splunk's index, source and sourcetype belong to the local and the enterprise pipeline).
/// A <i>compound</i> id - <c>provider=bedrock&amp;bedrock-auth-mode=iam_credentials</c> - is an AND of clauses, each <c>ids=value,value</c>
/// (the values list may hold an empty value, <c>disable=false,</c> means "false or blank"), and its <c>VisibleWhenValues</c> is ignored.
/// The compound form exists because a guardrail judge page is shown only in the global scope <i>and</i> for a judge strategy <i>and</i> while
/// the guardrail is not being turned off: three conditions that a single (field, values) pair cannot say. It is a string rather than new
/// properties so every place that copies a page or a field (the secret routes, the baseline, the Windows policy, the walkthroughs) carries it
/// along without being told.
/// </para>
/// <para>
/// Ids and values come from this app's own tables and from the CLI's choice lists; <see cref="When"/> refuses one that holds a character the
/// notation uses, so a gate cannot be built that reads as another.
/// </para>
/// </summary>
public static class WizardGate
{
    private const char ClauseSeparator = '&';
    private const char FieldSeparator = '|';
    private const char ValueSeparator = ',';
    private const char Assignment = '=';

    private static readonly ConcurrentDictionary<string, Clause[]> Parsed = new(StringComparer.Ordinal);

    /// <summary>One condition: the field (or any of several, <c>a|b</c>) holds one of the values.</summary>
    public readonly record struct Clause(string FieldIds, IReadOnlyList<string> Values);

    /// <summary>A condition on one field: it holds one of <paramref name="values"/> (an empty string among them allows a blank answer).</summary>
    public static Clause When(string fieldId, params string[] values)
    {
        ArgumentException.ThrowIfNullOrEmpty(fieldId);
        ArgumentNullException.ThrowIfNull(values);

        if (values.Length == 0)
        {
            throw new ArgumentException("A gate needs at least one value.", nameof(values));
        }

        Check(fieldId, isId: true);
        foreach (var value in values)
        {
            Check(value, isId: false);
        }

        return new Clause(fieldId, values);
    }

    /// <summary>
    /// The (id, values) pair to put on a page or a field for the conditions given, all of which must hold. One clause on one plain field stays the
    /// plain form, so the data reads as it always did; two or more become one compound id.
    /// </summary>
    public static (string FieldId, IReadOnlyList<string> Values) All(params Clause[] clauses)
    {
        ArgumentNullException.ThrowIfNull(clauses);

        if (clauses.Length == 0)
        {
            throw new ArgumentException("A gate needs at least one condition.", nameof(clauses));
        }

        if (clauses.Length == 1)
        {
            return (clauses[0].FieldIds, clauses[0].Values);
        }

        return (string.Join(ClauseSeparator, clauses.Select(Encode)), Array.Empty<string>());
    }

    /// <summary>
    /// <paramref name="existingFieldId"/> and its values (whatever form they are in) with <paramref name="more"/> required as well; the existing
    /// gate alone when there is nothing to add, and the new conditions alone when there is no existing gate.
    /// </summary>
    public static (string? FieldId, IReadOnlyList<string> Values) And(
        string? existingFieldId,
        IReadOnlyList<string> existingValues,
        params Clause[] more)
    {
        ArgumentNullException.ThrowIfNull(existingValues);
        ArgumentNullException.ThrowIfNull(more);

        var clauses = new List<Clause>();
        if (existingFieldId is { Length: > 0 })
        {
            clauses.AddRange(Read(existingFieldId, existingValues));
        }

        clauses.AddRange(more);
        if (clauses.Count == 0)
        {
            return (existingFieldId, existingValues);
        }

        var (id, values) = All(clauses.ToArray());
        return (id, values);
    }

    /// <summary>True when the gate is satisfied by the current answers; a null or empty id is no gate.</summary>
    public static bool IsVisible(string? gateFieldId, IReadOnlyList<string> gateValues, WizardValues values)
    {
        ArgumentNullException.ThrowIfNull(gateValues);
        ArgumentNullException.ThrowIfNull(values);

        if (gateFieldId is not { Length: > 0 })
        {
            return true;
        }

        if (!IsCompound(gateFieldId))
        {
            return Holds(gateFieldId, gateValues, values);
        }

        foreach (var clause in Parse(gateFieldId))
        {
            if (!Holds(clause.FieldIds, clause.Values, values))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The field ids a gate looks at (every id of every clause), for a caller that wants to know what a page depends on.</summary>
    public static IReadOnlyList<string> FieldsOf(string? gateFieldId)
    {
        if (gateFieldId is not { Length: > 0 })
        {
            return Array.Empty<string>();
        }

        var ids = IsCompound(gateFieldId)
            ? Parse(gateFieldId).SelectMany(c => c.FieldIds.Split(FieldSeparator))
            : gateFieldId.Split(FieldSeparator);
        return ids.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static bool IsCompound(string id) => id.Contains(Assignment, StringComparison.Ordinal);

    private static bool Holds(string fieldIds, IReadOnlyList<string> allowed, WizardValues values)
    {
        // "a|b" is any-of: shown when ANY of those fields holds one of the values.
        foreach (var id in fieldIds.Split(FieldSeparator))
        {
            var actual = values[id];
            for (var i = 0; i < allowed.Count; i++)
            {
                if (string.Equals(allowed[i], actual, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static IEnumerable<Clause> Read(string fieldId, IReadOnlyList<string> values) =>
        IsCompound(fieldId) ? Parse(fieldId) : new[] { new Clause(fieldId, values) };

    private static Clause[] Parse(string compound) =>
        Parsed.GetOrAdd(compound, static text =>
        {
            var clauses = new List<Clause>();
            foreach (var part in text.Split(ClauseSeparator))
            {
                var at = part.IndexOf(Assignment, StringComparison.Ordinal);
                if (at <= 0)
                {
                    // A clause with no field to look at cannot hold: fail closed rather than show a page nobody can reason about.
                    clauses.Add(new Clause("\u0000", Array.Empty<string>()));
                    continue;
                }

                clauses.Add(new Clause(part[..at], part[(at + 1)..].Split(ValueSeparator)));
            }

            return clauses.ToArray();
        });

    private static string Encode(Clause clause) =>
        clause.FieldIds + Assignment + string.Join(ValueSeparator, clause.Values);

    private static void Check(string text, bool isId)
    {
        // An id may hold the any-of bar; neither may hold anything else the notation uses.
        foreach (var c in text)
        {
            if (c is ClauseSeparator or Assignment or ValueSeparator || (!isId && c == FieldSeparator))
            {
                throw new ArgumentException($"\"{text}\" holds {c}, which a wizard gate uses for itself.", nameof(text));
            }
        }
    }
}
