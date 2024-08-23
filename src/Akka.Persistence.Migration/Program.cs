using Akka.Actor;
using Akka.Hosting;
using Akka.Persistence.Migration;
using Akka.Persistence.Migration.Actors;
using Akka.Persistence.Migration.Configuration;
using Akka.Persistence.Migration.Messages;
using Akka.Persistence.MongoDb.Hosting;
using Akka.Persistence.Sql.Hosting;
using LinqToDB;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

const string postgresConnectionString = "Server=127.0.0.1;Port=5432;Database=akka;User Id=akka;Password=akka;Include Error Detail=true;";
const string mongoDbConnectionString = "mongodb://localhost:27017/akka";

var hostBuilder = Host.CreateDefaultBuilder()
    .ConfigureLogging(builder =>
    {
        builder.AddConsole();
    })
    .ConfigureServices((ctx, services) =>
    {
        services
            .AddOptions<MigrationOptions>()
            .Configure(opt =>
            {
                var config = ctx.Configuration.GetSection("MigrationOptions");
                config.Bind(opt);
                opt.MigrationSqlOptions.ConnectionString = postgresConnectionString;
                opt.MigrationSqlOptions.PluginId = "migration";
                opt.MigrationSqlOptions.ProviderName = ProviderName.PostgreSQL15;
            });
        
        services
            // Setup Akka
            .AddAkka("migration-system", (builder, provider) =>
            {
                var options = provider.GetRequiredService<IOptions<MigrationOptions>>().Value;
                
                builder
                    // Boilerplate logging setup
                    .ConfigureLoggers(configurator: logging =>
                    {
                        logging.ClearLoggers();
                        logging.AddLoggerFactory();
                    })
                    
                    // Setup source persistence plugin, replace this with the appropriate plugin hosting extension method
                    .WithMongoDbPersistence(
                        connectionString: mongoDbConnectionString,
                        isDefaultPlugin: false)
                    // Make sure that query "write-plugin" setting points to the correct write plugin
                    .AddHocon(
                        hocon: $"{options.FromReadJournalId}.write-plugin = {options.FromJournalId.ToHocon()}", 
                        addMode: HoconAddMode.Prepend)
                    
                    // Setup target persistence plugin
                    .WithSqlPersistence(
                        connectionString: postgresConnectionString, 
                        providerName: ProviderName.PostgreSQL15,
                        autoInitialize: true,
                        isDefaultPlugin: false)
                    
                    // Setup required migration related persistence plugins
                    .WithSqlMigrationPersistence(provider)
                    .WithActors((system, registry) =>
                    {
                        var migratorActor = system.ActorOf(Props.Create(() => new MigrationTrackerActor(options)), "migrationTracker");
                        registry.Register<MigrationTrackerActor>(migratorActor);
                    });
            });
    });

var host = hostBuilder.Build();
await host.StartAsync();

var options = host.Services.GetRequiredService<IOptions<MigrationOptions>>().Value;
var registry = host.Services.GetRequiredService<IActorRegistry>();
var migrationTracker = await registry.GetAsync<MigrationTrackerActor>();

var result = await migrationTracker
    .Ask<MigrationActorProtocol.IMigrationResult>(MigrationActorProtocol.NotifyWhenCompleted.Instance);
switch (result)
{
    case MigrationActorProtocol.MigrationCompleted:
        Console.WriteLine("Migration completed");
        break;
    case MigrationActorProtocol.MigrationFailed fail:
        Console.WriteLine($"Migration failed while processing persistence ID {fail.PersistenceId}");
        Console.WriteLine(fail.Cause);
        break;
}

Console.WriteLine("Waiting for all persist operation to complete");
await Task.Delay(TimeSpan.FromSeconds(5));

await migrationTracker.GracefulStop(options.AskTimeout);
await migrationTracker.WatchAsync();

await host.StopAsync();