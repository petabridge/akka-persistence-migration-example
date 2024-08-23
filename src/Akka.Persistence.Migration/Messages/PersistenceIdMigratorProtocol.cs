namespace Akka.Persistence.Migration.Messages;

public static class PersistenceIdMigratorProtocol
{
    public sealed class Initialize
    {
        public static readonly Initialize Instance = new();
        private Initialize() { }
    }
    
    public sealed record EventPersisted
    {
        public static readonly EventPersisted Instance = new();
        private EventPersisted() { }
    }
    
    public sealed record EventPlaybackCompleted
    {
        public static readonly EventPlaybackCompleted Instance = new();
        private EventPlaybackCompleted() { }
    }
    
    public sealed record EventPlaybackFailed(Exception Cause);

    public sealed record CommandFailure(Exception Cause);
}