namespace Akka.Persistence.Migration.Messages;

public static class SnapshotSyncProtocol
{
    internal sealed class Retry
    {
        public static readonly Retry Instance = new();
        private Retry() { }
    }

    public interface ISnapshotSyncCommand;
    public interface ISnapshotSyncResponse;
    
    public sealed class GetCount: ISnapshotSyncCommand
    {
        public static readonly GetCount Instance = new();
        private GetCount() { }
    }
    
    public sealed record SnapshotSyncCount(long Count): ISnapshotSyncResponse;
    
    public sealed class GetCurrent: ISnapshotSyncCommand
    {
        public static readonly GetCurrent Instance = new();
        private GetCurrent() { }
    }
    
    public sealed class MoveNext: ISnapshotSyncCommand
    {
        public static readonly MoveNext Instance = new();
        private MoveNext() { }
    }
    
    public sealed record CurrentSnapshotSync(SelectedSnapshot? Snapshot): ISnapshotSyncResponse;
    public sealed record AllSnapshotsLoaded(int Count) : ISnapshotSyncResponse;
}