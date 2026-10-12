namespace Arkive.Core.Assets;

public readonly record struct UnrealNameReference(int Index, int Number)
{
    public string Resolve(IReadOnlyList<UnrealNameEntry> names)
    {
        if (Index < 0 || Index >= names.Count)
        {
            return $"<invalid-name:{Index}>";
        }

        var value = names[Index].Value;
        return Number == 0 ? value : $"{value}_{Number - 1}";
    }
}
