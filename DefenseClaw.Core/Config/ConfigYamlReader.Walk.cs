namespace DefenseClaw.Core.Config;

public sealed partial class ConfigYamlReader
{
    /// <summary>The parsed top-level mapping; null for an empty document. For the key walk in <see cref="ConfigKeyPattern"/>.</summary>
    internal object? RootNode => _root;

    /// <summary>The node as a mapping, or null when it is not one.</summary>
    internal static IDictionary<object, object?>? AsMap(object? node) => Map(node);
}
