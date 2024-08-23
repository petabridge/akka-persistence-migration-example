using Akka.Actor;
using Akka.Event;
using Akka.Persistence.Journal;
using Akka.Persistence.Migration.Configuration;
using Akka.Persistence.Migration.Messages;
using Akka.Persistence.Query;

namespace Akka.Persistence.Migration.Actors;

public class PersistenceWriterActor: ReceivePersistentActor, IWithTimers
{
    private readonly ILoggingAdapter _log;
    private readonly List<Exception> _exceptions = [];
    private readonly int _maxRetries;
    private readonly TimeSpan _retryInterval;
    private PersistenceWriterProtocol.IOperation? _currentOperation;
    
    public PersistenceWriterActor(MigrationOptions options, string persistenceId)
    {
        PersistenceId = persistenceId;

        JournalPluginId = options.ToJournalId;
        SnapshotPluginId = options.ToSnapshotStoreId;
        _maxRetries = options.MaxRetries;
        _retryInterval = options.RetryInterval;
        _log = Context.GetLogger();
        
        Become(Idle);
    }
    
    public override string PersistenceId { get; }
    public ITimerScheduler Timers { get; set; } = null!;
    
    private void Idle()
    {
        Command<EventEnvelope>(env =>
        {
            var msg = env.Tags.Length > 0 ? new Tagged(env.Event, env.Tags) : env.Event;
            HandlePersist(msg, Sender);
        });
        
        Command<SelectedSnapshot>(snapshot =>
        {
            HandleSaveSnapshot(snapshot.Snapshot, Sender);
        });
        CommandAny(HandleUnhandled);
    }
    
    private void Writing()
    {
        Command<PersistenceWriterProtocol.PersistFailed>(fail => HandleFailure(fail.Cause));
        Command<SaveSnapshotSuccess>(_ => Self.Tell(PersistenceWriterProtocol.SnapshotWriteCompleted.Instance));
        Command<SaveSnapshotFailure>(fail => HandleFailure(fail.Cause));
        Command<PersistenceWriterProtocol.Retry>(_ => HandleRetry());
        Command<PersistenceWriterProtocol.IWriteSucceeded>(HandleComplete);
        CommandAny(HandleUnhandled);
    }
    
    private void HandlePersist(object evt, IActorRef sender)
    {
        _currentOperation ??= new PersistenceWriterProtocol.PersistOperation(evt, sender);
        Become(Writing);
        
        _log.Debug($"Migrating event {evt}");
        Persist(evt, _ =>
        {
            _log.Debug($"Event {evt} migrated");
            Self.Tell(PersistenceWriterProtocol.PersistWriteCompleted.Instance);
        });
    }
    
    private void HandleSaveSnapshot(object snapshot, IActorRef sender)
    {
        _currentOperation ??= new PersistenceWriterProtocol.SnapshotOperation(snapshot, sender);
        Become(Writing);
        
        SaveSnapshot(snapshot);
    }
    
    private void HandleComplete(PersistenceWriterProtocol.IWriteSucceeded message)
    {
        _exceptions.Clear();
        _currentOperation!.ReplyTo.Tell(message);
        _currentOperation = null;
        
        Become(Idle);
    }
    
    private void HandleRetry()
    {
        switch (_currentOperation)
        {
            case PersistenceWriterProtocol.PersistOperation p:
                HandlePersist(p.Message, p.ReplyTo);
                break;
            case PersistenceWriterProtocol.SnapshotOperation s:
                HandleSaveSnapshot(s.Message, s.ReplyTo);
                break;
            default:
                throw new AggregateException($"Unknown migration operation: {_currentOperation!.GetType()}", _exceptions);
        }
    }
    
    private void HandleFailure(Exception cause)
    {
        if (_currentOperation is null)
            throw new NullReferenceException("_currentOperation should not be null");
        
        _exceptions.Add(cause);
        if (_exceptions.Count < _maxRetries)
        {
            _log.Info("{0} write operation failed ({1}/{2}), retrying in {3} seconds...", 
                _currentOperation.Name, _exceptions.Count, _maxRetries, _retryInterval.TotalSeconds);
            
            Timers.StartSingleTimer(PersistenceWriterProtocol.Retry.Instance, PersistenceWriterProtocol.Retry.Instance, _retryInterval);
            return;
        }
        
        try
        {
            throw new AggregateException(
                message: string.Format(_currentOperation.ErrorMessage, _maxRetries),
                innerExceptions: _exceptions);
        }
        catch (AggregateException ex)
        {
            _log.Error(ex, _currentOperation.ErrorMessage, _maxRetries);
            _currentOperation.ReplyTo.Tell(_currentOperation.FailedMessage(ex));
        }
    }
    
    private void HandleUnhandled(object msg)
    {
        _log.Error($"Illegal out of band command detected: {msg}");
        Unhandled(msg);
    }
    
    protected override void OnPersistFailure(Exception cause, object @event, long sequenceNr)
    {
        Log.Warning(cause, "Rejected to persist event type [{0}] with sequence number [{1}] for persistenceId [{2}] due to [{3}].",
            @event.GetType(), sequenceNr, PersistenceId, cause.Message);
        _currentOperation?.ReplyTo.Tell(new PersistenceWriterProtocol.PersistFailed(cause));
    }
    
    protected override void OnPersistRejected(Exception cause, object @event, long sequenceNr)
    {
        Log.Warning(cause, "Rejected to persist event type [{0}] with sequence number [{1}] for persistenceId [{2}] due to [{3}].",
            @event.GetType(), sequenceNr, PersistenceId, cause.Message);
        Self.Tell(new PersistenceWriterProtocol.PersistFailed(cause));
    }
}
