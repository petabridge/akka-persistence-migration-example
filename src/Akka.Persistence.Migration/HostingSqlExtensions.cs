using Akka.Hosting;
using Akka.Persistence.Migration.Configuration;
using Akka.Persistence.Sql.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Akka.Persistence.Migration;

/// <summary>
/// Migrator Akka.Persistence.Sql persistence setup helper
/// </summary>
public static class HostingSqlExtensions
{
    public static AkkaConfigurationBuilder WithSqlMigrationPersistence(this AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        var migrationOptions = provider.GetRequiredService<IOptions<MigrationOptions>>().Value;
        var opt = migrationOptions.MigrationSqlOptions;
        
        var journalDbOpts = JournalDatabaseOptions.Default;
        journalDbOpts.JournalTable!.TableName = $"{journalDbOpts.JournalTable!.TableName}_{opt.PluginId}";
        journalDbOpts.MetadataTable!.TableName = $"{journalDbOpts.MetadataTable!.TableName}_{opt.PluginId}";
        journalDbOpts.TagTable!.TableName = $"{journalDbOpts.MetadataTable!.TableName}_{opt.PluginId}";

        var snapshotDbOpts = SnapshotDatabaseOptions.Default;
        snapshotDbOpts.SnapshotTable!.TableName = $"{snapshotDbOpts.SnapshotTable!.TableName}_{opt.PluginId}";

        return builder.WithSqlPersistence(
            new SqlJournalOptions(false, opt.PluginId)
            {
                ConnectionString = opt.ConnectionString,
                ProviderName = opt.ProviderName,
                AutoInitialize = true,
                DatabaseOptions = journalDbOpts
            },
            new SqlSnapshotOptions(false, opt.PluginId)
            {
                ConnectionString = opt.ConnectionString,
                ProviderName = opt.ProviderName,
                AutoInitialize = true,
                DatabaseOptions = snapshotDbOpts
            });
    }
}