using Akka;
using Akka.Actor;
using Akka.Hosting;
using Akka.Persistence;
using Akka.Persistence.Migration.Actors;
using Akka.Persistence.MongoDb.Hosting;
using Akka.Persistence.MongoDb.Query;
using Akka.Persistence.Query;
using Akka.Persistence.Sql.Hosting;
using Akka.Streams;
using Akka.Streams.Dsl;
using LinqToDB;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
                .WithMongoDbPersistence(
                    connectionString: "mongodb://localhost:27017/akka",
                    isDefaultPlugin: false)
                .WithSqlPersistence(
                    connectionString: "Server=127.0.0.1;Port=5432;Database=akka;User Id=postgres;Password=mysecretpassword;", 
                    providerName: ProviderName.PostgreSQL15, 
                    isDefaultPlugin: true)
                .AddHocon("""
                          akka.persistence.query.mongodb.write-plugin = "akka.persistence.journal.mongodb"
                          """, HoconAddMode.Prepend);
        });
    });

var host = hostBuilder.Build();
await host.StartAsync();

var sys = host.Services.GetRequiredService<ActorSystem>();
var migrationTracker = sys.ActorOf(Props.Create(() => new MigrationTrackerActor()), name: "migrationTracker");
await migrationTracker.WatchAsync();

await host.StopAsync();