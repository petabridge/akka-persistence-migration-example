using System.Text;
using Akka.Actor;
using Akka.Hosting;
using Akka.Persistence.MongoDb.Hosting;
using Alba.CsConsoleFormat;
using App.Shared;
using App.Shared.Actors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

const int totalActors = 50;
const int messagesPerActor = 220;
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

    Console.WriteLine("Seeding completed");
}

var reporter = sys.ActorOf(Props.Create(() => new ReportingActor(actors)), "reporter");
var report = await reporter.Ask<Report>(GetReport.Instance);

Console.OutputEncoding = Encoding.UTF8;
var headerThickness = new LineThickness(LineWidth.Single, LineWidth.Double);
var rowThickness = new LineThickness(LineWidth.Single, LineWidth.None);

var currentRow = 0;
var doc = new Document(
    new Span("Persistence ID Count: ") { Color = ConsoleColor.Yellow }, report.Rows.Length, "\n",
    new Grid {
        Color = ConsoleColor.Gray,
        Columns = { GridLength.Auto, GridLength.Auto, GridLength.Auto },
        Children = {
            new Cell("Persistence Id") { Stroke = headerThickness, Color = ConsoleColor.White},
            new Cell("Last Sequence Nr") { Stroke = headerThickness, Color = ConsoleColor.White},
            new Cell("Checksum") { Stroke = headerThickness, Color = ConsoleColor.White},
            report.Rows.Select(row =>
            {
                currentRow++;
                var color = currentRow % 2 == 0 ? ConsoleColor.White : ConsoleColor.DarkGray;
                return new[]
                {
                    new Cell(row.Name) { Stroke = rowThickness, Color = color },
                    new Cell(row.LastSequenceNumber) { Stroke = rowThickness, Color = color, Align = Align.Right},
                    new Cell(row.Checksum) { Stroke = rowThickness, Color = color, Align = Align.Right},
                };
            })
        }
    }
);
ConsoleRenderer.RenderDocument(doc);

await host.StopAsync();