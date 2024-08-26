namespace Akka.Persistence.Migration.Messages;

public static class MigrationActorProtocol
{
    public sealed class Initialized
    {
        public static readonly Initialized Instance = new();
        private Initialized() { }
    }
    
    public sealed record InitializationFailed(Exception Cause);
    
    public sealed record PersistenceIdMigrationCompleted(string PersistenceId);
    
    public sealed record PersistenceIdMigrationFailed(Exception Cause, string PersistenceId);
    
    public sealed class NotifyWhenCompleted
    {
        public static readonly NotifyWhenCompleted Instance = new();
        private NotifyWhenCompleted() { }
    }

    public interface IMigrationResult;
    
    public sealed class MigrationCompleted: IMigrationResult
    {
        public static readonly MigrationCompleted Instance = new();
        private MigrationCompleted() { }
    }

    public sealed record MigrationFailed(Exception Cause, string PersistenceId) : IMigrationResult;
}
