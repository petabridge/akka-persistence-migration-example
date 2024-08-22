using Akka.Actor;

namespace Akka.Persistence.Migration.Messages;

public static class PersistenceWriterProtocol
{
    public interface IWriteResult;

    public interface IWriteSucceeded : IWriteResult;
    
    public sealed class WriteCompleted: IWriteSucceeded
    {
        public static readonly WriteCompleted Instance = new();
        private WriteCompleted() { }
    }

    public interface IWriteFailed : IWriteResult
    {
        public Exception Cause { get; init; }
    }
    
    public sealed record PersistFailed(Exception Cause): IWriteFailed;
    
    public sealed record SnapshotFailed(Exception Cause): IWriteFailed;    

    public interface IOperation
    {
        public IActorRef ReplyTo { get; init; }
        public string Name { get; }
        public string ErrorMessage { get; }
        public IWriteFailed FailedMessage(Exception cause);
    }

    public sealed record PersistOperation(object Message, IActorRef ReplyTo) : IOperation
    {
        public string Name => "Event";
        public string ErrorMessage => "Event migration failed. Maximum retries exceeded: {0} retries";
        public IWriteFailed FailedMessage(Exception cause) => new PersistFailed(cause);
    }
    
    public sealed record PersistAllOperation(object[] Messages, IActorRef ReplyTo): IOperation
    {
        public string Name => "Batched events";
        public string ErrorMessage => "Batched events migration failed. Maximum retries exceeded: {0} retries";
        public IWriteFailed FailedMessage(Exception cause) => new PersistFailed(cause);
    }
    
    public sealed record SnapshotOperation(object Message, IActorRef ReplyTo): IOperation
    {
        public string Name => "Snapshot";
        public string ErrorMessage => "Snapshot migration failed. Maximum retries exceeded: {0} retries";
        public IWriteFailed FailedMessage(Exception cause) => new SnapshotFailed(cause);
    }
    
    public sealed class Retry
    {
        public static readonly Retry Instance = new();
        private Retry() { }
    }
}
