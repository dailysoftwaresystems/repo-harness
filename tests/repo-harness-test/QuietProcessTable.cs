using RepoHarness.Core.Platform;

namespace RepoHarness.Tests;

/// <summary>
/// A process table that reads, and finds a machine running nothing but this test. It reports no
/// degradation, which is what keeps a test using it about its own subject: a table that could not be
/// read is a verdict of its own, and the machine's real one - read through a program on Windows - holds
/// whatever else runs there, and is read as slowly as the machine is busy: a minute a reading, measured
/// beside another session's parallel builds.
/// </summary>
internal sealed class QuietProcessTable : IProcessTable
{
    public Task<ProcessTableReading> ReadAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new ProcessTableReading(
            [new SampledProcess(Environment.ProcessId, null, "repo-harness-test", DateTimeOffset.UnixEpoch, "repo-harness-test")],
            null));
}
