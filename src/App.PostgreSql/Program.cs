using Akka.Actor;
using Akka.Hosting;
using Akka.Persistence.Sql.Hosting;
using App.Shared.Actors;
using LinqToDB;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

const int totalActors = 50;
const int messagesPerActor = 200;
const int snapshotEvery = 50;

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
                .WithSqlPersistence(
                    "Server=127.0.0.1;Port=5432;Database=akka;User Id=akka;Password=akka;", 
                    ProviderName.PostgreSQL15);
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

Console.ReadKey();
await host.StopAsync();