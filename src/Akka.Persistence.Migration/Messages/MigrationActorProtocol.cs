namespace Akka.Persistence.Migration.Messages;

public sealed class Initialized
{
    public static readonly Initialized Instance = new();
    private Initialized() { }
}
    
public sealed record InitializationFailed(Exception Cause);

public sealed record MigrationCompleted(string PersistenceId);

public sealed record MigrationFailed(Exception Cause, string PersistenceId);

