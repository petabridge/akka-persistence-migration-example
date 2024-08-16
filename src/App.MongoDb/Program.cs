using Akka.Actor;
using Akka.Hosting;
using Akka.Persistence.MongoDb.Hosting;
using App.Shared.Actors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

const int totalActors = 50;
const int messagesPerActor = 200;
const int snapshotEvery = 50;

var isSeeding = args is ["seed"];

var hostBuilder = Host.CreateDefaultBuilder()
    .ConfigureLogging(builder =>
    {
        builder.AddConsole();
    })
    .ConfigureServices((_, services) =>
    {
        services.AddAkka("System", (builder, _) =>
        {
            builder
                .ConfigureLoggers(logging =>
                {
                    logging.ClearLoggers();
                    logging.AddLoggerFactory();
                })
                .WithMongoDbPersistence("mongodb://localhost:27017/akka");
        });
    });

var host = hostBuilder.Build();
await host.StartAsync();

var sys = host.Services.GetRequiredService<ActorSystem>();
var actors = new IActorRef[totalActors];

foreach (var actorIndex in Enumerable.Range(0, totalActors))
{
    actors[actorIndex] = sys.ActorOf(
        Props.Create(() => new PersistentWorkerActor(actorIndex, totalActors, snapshotEvery)),
        $"worker-{actorIndex}");
}

if (isSeeding)
{
    Console.WriteLine("Seeding started");
    var tasks = new Task<int>[totalActors];
    for (var msgNum = 0; msgNum < messagesPerActor; msgNum++)
    {
        for (var actorIndex = 0; actorIndex < totalActors; actorIndex++)
        {
            var message = msgNum * totalActors + actorIndex;
            tasks[actorIndex] = actors[actorIndex].Ask<int>(message);
        }
        await Task.WhenAll(tasks);
    }

    var watchTasks = new Task<bool>[totalActors];
    foreach (var actorIndex in Enumerable.Range(0, totalActors))
    {
        watchTasks[actorIndex] = actors[actorIndex].WatchAsync();
        actors[actorIndex].Tell("terminate");
    }

    await Task.WhenAll(watchTasks);
    Console.WriteLine("Seeding completed");
}

await host.StopAsync();