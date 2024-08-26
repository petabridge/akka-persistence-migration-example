using Akka.Actor;
using Akka.Event;
using Akka.Persistence.Migration.Messages;

namespace Akka.Persistence.Migration.Actors;

public abstract class ReceivePersistenceWithRetryActor<TPersist, TSnapshot>: ReceivePersistentActor, IWithTimers
{
    protected abstract int MaxRetries { get; }
    protected abstract TimeSpan RetryInterval { get; }
    protected List<Exception> Exceptions { get; } = [];

    private TPersist? _currentEvent;
    private TSnapshot? _currentSnapshot;
    private Action<TPersist>? _persistAction;

    public ITimerScheduler Timers { get; set; } = null!;
    
    protected void CommandRetry()
    {
        Command<SaveSnapshotFailure>(fail =>
        {
            Self.Tell(new PersistenceRetryProtocol.SaveSnapshotFailure(fail.Cause));
        });
        
        Command<SaveSnapshotSuccess>(OnSaveSnapshotSuccess);
        
        Command<PersistenceRetryProtocol.IPersistenceFailure>(HandlePersistenceFailure);
        
        Command<PersistenceRetryProtocol.RetrySnapshot>(_ => SaveSnapshot(_currentSnapshot));
        
        Command<PersistenceRetryProtocol.RetryPersist>(_ => PersistWithRetry(_currentEvent!, _persistAction!));
    }
    
    protected void PersistWithRetry(TPersist message, Action<TPersist> onPersist)
    {
        _currentEvent = message;
        _persistAction = onPersist;
        Persist(_currentEvent, _persistAction);
    }

    protected void SaveSnapshotWithRetry(TSnapshot message)
    {
        _currentSnapshot = message;
        SaveSnapshot(message);
    }
    
    private void HandlePersistenceFailure(PersistenceRetryProtocol.IPersistenceFailure fail)
    {
        Exceptions.Add(fail.Cause);
        string operation;
        PersistenceRetryProtocol.IRetry message;
        switch (fail)
        {
            case PersistenceRetryProtocol.SaveSnapshotFailure:
                operation = "SaveSnapshot";
                message = PersistenceRetryProtocol.RetrySnapshot.Instance;
                break;
            case PersistenceRetryProtocol.PersistFailure:
                operation = "Persist";
                message = PersistenceRetryProtocol.RetryPersist.Instance;
                break;
            default:
                throw new IndexOutOfRangeException();
        }
        
        if (Exceptions.Count < MaxRetries)
        {
            Log.Info("{0} failed ({1}/{2}), retrying in {3} seconds...", 
                operation, Exceptions.Count, MaxRetries, RetryInterval.TotalSeconds);
            
            Timers.StartSingleTimer(key: message, msg: message, timeout: RetryInterval);
            return;
        }
        
        try
        {
            throw new AggregateException(
                message: $"{operation} failed. Maximum retries exceeded: {MaxRetries} retries",
                innerExceptions: Exceptions);
        }
        catch (AggregateException ex)
        {
            Log.Error(ex, $"{operation} failed. Maximum retries exceeded: {{0}} retries", MaxRetries);
            switch (fail)
            {
                case PersistenceRetryProtocol.PersistFailure:
                    OnPersistFailure(ex);
                    break;
                case PersistenceRetryProtocol.SaveSnapshotFailure:
                    OnSaveSnapshotFailure(ex);
                    break;
            }
        }
        finally
        {
            Context.Stop(Self);
        }
    }

    protected abstract void OnPersistFailure(Exception cause);
    protected abstract void OnSaveSnapshotSuccess(SaveSnapshotSuccess success);
    protected abstract void OnSaveSnapshotFailure(Exception cause);
    
    protected override void OnPersistFailure(Exception cause, object @event, long sequenceNr)
    {
        base.OnPersistFailure(cause, @event, sequenceNr);
        Self.Tell(new PersistenceRetryProtocol.PersistFailure(cause));
    }
    
    protected override void OnPersistRejected(Exception cause, object @event, long sequenceNr)
    {
        base.OnPersistRejected(cause, @event, sequenceNr);
        Self.Tell(new PersistenceRetryProtocol.PersistFailure(cause));
    }
    
}