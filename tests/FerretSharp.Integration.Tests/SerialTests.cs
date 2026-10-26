namespace FerretSharp.Integration.Tests;

/// <summary>Tests that change what the shared container does for every new session: they run after all others, alone.</summary>
[CollectionDefinition(nameof(SerialTests), DisableParallelization = true)]
public sealed class SerialTests;
