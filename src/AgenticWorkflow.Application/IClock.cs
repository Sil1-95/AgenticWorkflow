namespace AgenticWorkflow.Application;

/// <summary>The only place application logic reads the time, so runs are reproducible in tests.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
