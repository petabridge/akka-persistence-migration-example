// -----------------------------------------------------------------------
//  <copyright file="ITestContainer.cs" company="Akka.NET Project">
//      Copyright (C) 2013-2023 .NET Foundation <https://github.com/akkadotnet/akka.net>
//  </copyright>
// -----------------------------------------------------------------------

namespace Akka.Persistence.Migration.Tests.Infrastructure
{
    public interface ITestContainer : IDisposable, IAsyncDisposable
    {
        public string ConnectionString { get; }

        public string? DatabaseName { get; }

        public string ContainerName { get; }

        public bool Initialized { get; }

        public string ProviderName { get; }
    }
}
