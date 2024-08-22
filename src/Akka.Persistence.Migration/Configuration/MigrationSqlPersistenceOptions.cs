namespace Akka.Persistence.Migration.Configuration;

public class MigrationSqlPersistenceOptions
{
    public string PluginId { get; set; } = "migration";
    public string ConnectionString { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    
    public string JournalId => $"akka.persistence.journal.{PluginId}";
    public string ReadJournalId => $"akka.persistence.query.journal.{PluginId}";
    public string SnapshotStoreId => $"akka.persistence.snapshot-store.{PluginId}";
}