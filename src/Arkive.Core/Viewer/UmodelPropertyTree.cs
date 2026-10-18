using System.Globalization;
using System.Text.RegularExpressions;

namespace Arkive.Core.Viewer;

/// <summary>
/// One node of UModel's indented property text (".props.txt" exports and "-dump" output).
/// A node has a scalar <see cref="Value"/>, child nodes, or both empty for "{}".
/// </summary>
public sealed class UmodelPropertyNode
{
    public UmodelPropertyNode(string name, string? value)
    {
        Name = name;
        Value = value;
    }

    public string Name { get; }
    public string? Value { get; internal set; }
    public List<UmodelPropertyNode> Children { get; } = [];

    /// <summary>Name without an array suffix: "Sections[3]" becomes "Sections".</summary>
    public string BaseName => Name.IndexOf('[') is var bracket and > 0 ? Name[..bracket] : Name;

    public UmodelPropertyNode? Child(string name) =>
        Children.FirstOrDefault(child => child.BaseName.Equals(name, StringComparison.OrdinalIgnoreCase));

    public string? ChildValue(string name) => Child(name)?.Value;

    public IEnumerable<UmodelPropertyNode> Elements(string arrayName) =>
        Child(arrayName)?.Children ?? Enumerable.Empty<UmodelPropertyNode>();
}

public static partial class UmodelPropertyTree
{
    public static UmodelPropertyNode Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var root = new UmodelPropertyNode(string.Empty, null);
        var stack = new Stack<UmodelPropertyNode>();
        stack.Push(root);
        UmodelPropertyNode? last = null;
        foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            if (line == "{")
            {
                if (last is not null) stack.Push(last);
                continue;
            }
            if (line == "}")
            {
                if (stack.Count > 1) stack.Pop();
                last = null;
                continue;
            }

            var equals = line.IndexOf(" =", StringComparison.Ordinal);
            if (equals <= 0)
            {
                last = null;
                continue;
            }
            var name = line[..equals].Trim();
            var value = line[(equals + 2)..].Trim();
            var node = new UmodelPropertyNode(name, value.Length == 0 ? null : value);
            if (value.StartsWith('{') && value.EndsWith('}'))
            {
                node.Value = null;
                foreach (var (key, fieldValue) in InlineFields(value[1..^1]))
                    node.Children.Add(new UmodelPropertyNode(key, fieldValue));
            }
            stack.Peek().Children.Add(node);
            last = node;
        }
        return root;
    }

    public static IEnumerable<(string Key, string Value)> InlineFields(string body)
    {
        var depth = 0;
        var start = 0;
        var entry = 0;
        for (var index = 0; index <= body.Length; index++)
        {
            var character = index < body.Length ? body[index] : ',';
            if (character is '{' or '(') depth++;
            else if (character is '}' or ')') depth--;
            else if (character == ',' && depth == 0)
            {
                var field = body[start..index].Trim();
                start = index + 1;
                var equals = field.IndexOf('=');
                if (equals > 0) yield return (field[..equals].Trim(), field[(equals + 1)..].Trim());
                // A short array is written inline as its bare entries: { Texture2D'a', Texture2D'b' }.
                else if (field.Length > 0) yield return ($"[{entry}]", field);
                entry++;
            }
        }
    }

    /// <summary>Object reference text such as <c>Texture2D'tex.name'</c> as (class, path, last name).</summary>
    public static (string ClassName, string Path, string Name)? ParseObjectReference(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var match = ObjectReferenceRegex().Match(value.Trim());
        if (!match.Success) return null;
        var path = match.Groups["path"].Value;
        return (match.Groups["class"].Value, path, path[(path.LastIndexOf('.') + 1)..]);
    }

    public static float[]? ParseColor(UmodelPropertyNode? node)
    {
        if (node is null) return null;
        var components = new[] { "R", "G", "B", "A" };
        var values = new float[4];
        var found = 0;
        for (var index = 0; index < 4; index++)
        {
            var text = node.ChildValue(components[index]);
            if (text is not null && float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                values[index] = value;
                found++;
            }
        }
        return found == 0 ? null : values;
    }

    public static bool TryParseFloat(string? value, out float result) =>
        float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);

    [GeneratedRegex(@"^(?<class>[A-Za-z0-9_]+)'(?<path>[^']+)'$", RegexOptions.CultureInvariant)]
    private static partial Regex ObjectReferenceRegex();
}
