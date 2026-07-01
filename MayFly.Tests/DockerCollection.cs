using Xunit;

/// <summary>
/// xUnit collection that serialises all real-Docker tests so they do not race for port 20000.
/// </summary>
[CollectionDefinition("docker-sequential", DisableParallelization = true)]
public class DockerSequentialCollection { }
