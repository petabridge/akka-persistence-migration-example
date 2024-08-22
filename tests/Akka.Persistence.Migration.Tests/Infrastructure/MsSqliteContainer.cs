// -----------------------------------------------------------------------
//  <copyright file="MsSqliteContainer.cs" company="Akka.NET Project">
//      Copyright (C) 2013-2023 .NET Foundation <https://github.com/akkadotnet/akka.net>
//  </copyright>
// -----------------------------------------------------------------------

using Microsoft.Data.Sqlite;

namespace Akka.Persistence.Migration.Tests.Infrastructure
{
    /// <summary>
    ///     Fixture used to run Sqlite
    /// </summary>
    public sealed class MsSqliteContainer : ITestContainer
    {
        private SqliteConnection? _heldConnection;
        public string ConnectionString => $"Filename=file:memdb-{DatabaseName}.db;Mode=Memory;Cache=Shared";
        public string? DatabaseName { get; private set; }

        public string ContainerName => string.Empty;
        public bool Initialized => _heldConnection is not null;

        public string ProviderName => LinqToDB.ProviderName.SQLiteMS;

        public async Task InitializeAsync()
        {
            if (Initialized)
                return;

            GenerateDatabaseName();

            var conn = new SqliteConnection(ConnectionString);
            await conn.OpenAsync();

            _heldConnection = conn;
        }

        public async ValueTask DisposeAsync()
        {
            if (_heldConnection is not null)
            {
                _heldConnection.Close();
                await _heldConnection.DisposeAsync();
            }

            _heldConnection = null;
        }

        public void Dispose()
        {
            _heldConnection?.Close();
            _heldConnection?.Dispose();
            _heldConnection = null;
        }

        private void GenerateDatabaseName()
        {
            DatabaseName = $"sql_tests_{Guid.NewGuid():N}";
        }
    }
}
