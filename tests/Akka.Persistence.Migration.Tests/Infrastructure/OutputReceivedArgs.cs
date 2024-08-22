// -----------------------------------------------------------------------
//  <copyright file="OutputReceivedArgs.cs" company="Akka.NET Project">
//      Copyright (C) 2013-2023 .NET Foundation <https://github.com/akkadotnet/akka.net>
//  </copyright>
// -----------------------------------------------------------------------

namespace Akka.Persistence.Migration.Tests.Infrastructure
{
    public class OutputReceivedArgs : EventArgs
    {
        public OutputReceivedArgs(string output)
            => Output = output;

        public string Output { get; }
    }
}
