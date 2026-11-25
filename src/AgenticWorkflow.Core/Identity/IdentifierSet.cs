using System.Collections;
using System.Collections.Immutable;

namespace AgenticWorkflow.Core.Identity;

/// <summary>
/// Immutable set of <see cref="StableIdentifier"/>s with value equality and a deterministic (ordinal) order,
/// so records that contain one compare and enumerate identically after replay.
/// </summary>
public sealed class IdentifierSet<T> : IReadOnlyCollection<T>, IEquatable<IdentifierSet<T>>
    where T : StableIdentifier
{
    private static readonly IComparer<T> Order = Comparer<T>.Create((a, b) => string.CompareOrdinal(a.Value, b.Value));

    private readonly ImmutableSortedSet<T> _items;

    private IdentifierSet(ImmutableSortedSet<T> items) => _items = items;

    public static IdentifierSet<T> Empty { get; } = new(ImmutableSortedSet.Create(Order));

    public int Count => _items.Count;

    public static IdentifierSet<T> Of(params T[] items) => Of((IEnumerable<T>)items);

    public static IdentifierSet<T> Of(IEnumerable<T> items)
        => new(ImmutableSortedSet.CreateRange(Order, Guard.NotNull(items, nameof(items))));

    public bool Contains(T item) => _items.Contains(item);

    public bool IsSupersetOf(IdentifierSet<T> other) => _items.IsSupersetOf(other._items);

    public bool Overlaps(IdentifierSet<T> other) => _items.Overlaps(other._items);

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool Equals(IdentifierSet<T>? other)
        => other is not null && _items.SequenceEqual(other._items);

    public override bool Equals(object? obj) => Equals(obj as IdentifierSet<T>);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in _items)
            hash.Add(item);

        return hash.ToHashCode();
    }

    public override string ToString() => "{" + string.Join(", ", _items) + "}";
}
