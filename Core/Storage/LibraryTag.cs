namespace Animanager.Core.Storage;

public sealed record LibraryTag(string Name)
{
    internal static readonly IEqualityComparer<LibraryTag> Comparer = EqualityComparer<LibraryTag>.Create(
        (left, right) => StringComparer.OrdinalIgnoreCase.Equals(left?.Name, right?.Name),
        tag => StringComparer.OrdinalIgnoreCase.GetHashCode(tag.Name));

    internal bool IsValid => !string.IsNullOrWhiteSpace(Name) && Name == Name.Trim() && !Name.Any(char.IsControl);
}
