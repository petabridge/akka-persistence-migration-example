namespace Akka.Persistence.Migration.Messages;

public static class PersistenceRetryProtocol
{
    public interface IPersistenceResult;

    public interface IPersistenceFailure : IPersistenceResult
    {
        Exception Cause { get; }
    }
    
    public sealed record SaveSnapshotFailure(Exception Cause): IPersistenceFailure;
    
    public sealed record PersistFailure(Exception Cause): IPersistenceFailure;
    
    public interface IRetry;
    
    public class RetrySnapshot: IRetry
    {
        public static readonly RetrySnapshot Instance = new();
        private RetrySnapshot() { }
    }
    
    public class RetryPersist: IRetry
    {
        public static readonly RetryPersist Instance = new();
        private RetryPersist() { }
    }
}