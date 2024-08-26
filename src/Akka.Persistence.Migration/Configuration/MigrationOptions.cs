namespace Akka.Persistence.Migration.Configuration;

public sealed record MigrationOptions
{
    public MigrationSqlPersistenceOptions MigrationSqlOptions { get; } = new();
    public string FromJournalId { get; set; } = string.Empty;
    public string FromReadJournalId { get; set; } = string.Empty;
    public string FromSnapshotStoreId { get; set; } = string.Empty;
    public string ToJournalId { get; set; } = string.Empty;
    public string ToSnapshotStoreId { get; set; } = string.Empty;
    public bool MigrateAllSnapshots { get; set; }
    public int MaxRetries { get; set; } = 5;
    public TimeSpan RetryInterval { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan AskTimeout { get; set; } = TimeSpan.FromMinutes(10);
    public int SnapshotEvery { get; set; } = 50;
    public int MaxParallelMigrations { get; set; } = 5;
}