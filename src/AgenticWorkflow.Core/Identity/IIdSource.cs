namespace AgenticWorkflow.Core.Identity;

/// <summary>
/// The only source of generated identities inside the kernel. Tests supply a sequential implementation so
/// that the exact same history can be produced twice; production uses <see cref="GuidIdSource"/>.
/// </summary>
public interface IIdSource
{
    Guid Next();
}

/// <summary>Time-ordered random GUIDs.</summary>
public sealed class GuidIdSource : IIdSource
{
    public Guid Next() => Guid.CreateVersion7();
}
