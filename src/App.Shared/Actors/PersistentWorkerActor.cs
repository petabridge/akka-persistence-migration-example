using Akka.Actor;
using Akka.Event;
using Akka.Persistence;

namespace App.Shared.Actors;

public class PersistentWorkerActor: ReceivePersistentActor
{
    private readonly int _actorIndex;
    private readonly List<int> _state = [];
    private int _lastData = -1;
    private IActorRef? _sender;
    
    public PersistentWorkerActor(int index, int totalActors, int snapshotCount)
    {
        _actorIndex = index;

        var log = Context.GetLogger();
        
        Recover<SnapshotOffer>(offer =>
        {
            _state.AddRange((IEnumerable<int>)offer.Snapshot);
            if (_state.Any(i => i % totalActors != _actorIndex))
                throw new Exception("Illegal state detected during recovery");
            _lastData = _state.Last();
        });
        
        Recover<int>(i =>
        {
            if(i < _lastData)
                throw new Exception("Illegal event detected during recovery");
            _lastData = i;
            _state.Add(i);
        });
        
        Recover<RecoveryCompleted>(_ =>
        {
            log.Info($"{PersistenceId}: Recovery completed. State: {string.Join(", ", _state)}");
        });
        
        Command<int>(msg =>
        {
            _sender = Sender;
            Persist(msg, i =>
            {
                log.Info($"Data persisted: {i}");
                _lastData = i;
                _state.Add(i);
                if(_state.Count % snapshotCount == 0)
                    SaveSnapshot(_state);
                else
                    _sender.Tell(i);
            });
        });
        
        Command<SaveSnapshotSuccess>(_ =>
        {
            log.Info($"Last persisted data snapshot: {_lastData}");
            _sender.Tell(_lastData);
        });
        
        Command<SaveSnapshotFailure>(_ => throw new Exception("Snapshot failed!"));
        
        Command<string>(msg =>
        {
            if(msg is "terminate")
                Context.Stop(Self);
        });
    }
    
    public override string PersistenceId => $"persistent-worker-{_actorIndex}";
    
}