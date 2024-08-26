using Akka.Actor;

namespace App.Shared.Actors;

public class ReportingActor: ReceiveActor
{
    private readonly IActorRef[] _workers;
    private readonly SortedSet<ReportRow> _reportRows = new(ReportRow.Comparer);
    private int _reportCount;
    private bool _completed;
    private IActorRef? _watcher;

    public ReportingActor(IActorRef[] workers)
    {
        _workers = workers;

        Receive<ReportRow>(row =>
        {
            _reportRows.Add(row);
            _reportCount++;
            
            if (_reportCount != _workers.Length) 
                return;
            
            _completed = true;
            if (_watcher is null) 
                return;
                
            _watcher.Tell(new Report(_reportRows.ToArray()));
            _watcher = null;
        });

        Receive<GetReport>(_ =>
        {
            if(_completed)
                Sender.Tell(new Report(_reportRows.ToArray()));
            else
                _watcher = Sender;
        });
    }

    protected override void PreStart()
    {
        base.PreStart();
        foreach (var actor in _workers)
        {
            actor.Tell(ReportState.Instance);
        }
    }
}