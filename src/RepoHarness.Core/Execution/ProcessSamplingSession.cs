using RepoHarness.Core.Platform;
using System.Diagnostics;
using System.Globalization;

namespace RepoHarness.Core.Execution;

/// <summary>
/// Sampling in progress for one leg: the reading taken as it started, the readings taken while it
/// runs, and the reading taken as it ends.
/// </summary>
/// <remarks>
/// Every sample is kept. With only a first and a last reading, a contender that started and
/// finished in between leaves no trace at all, and the leg it corrupted reports a clean run.
/// </remarks>
public sealed class ProcessSamplingSession : IAsyncDisposable
{
    private readonly ProcessSampler _sampler;
    private readonly ContentionRequest _request;
    private readonly StringComparison _pathComparison;
    private readonly List<ProcessSample> _samples = [];
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly Task _loop;
    private ContentionReport? _report;

    internal ProcessSamplingSession(
        ProcessSampler sampler,
        ContentionRequest request,
        ProcessSample first,
        StringComparison pathComparison)
    {
        _sampler = sampler;
        _request = request;
        _pathComparison = pathComparison;
        _samples.Add(first);

        _loop = request.SampleSeconds > 0
            ? SampleEveryAsync(TimeSpan.FromSeconds(request.SampleSeconds))
            : Task.CompletedTask;
    }

    /// <summary>
    /// Takes the closing sample and reports what the whole set found. Asked again, it returns the
    /// same report rather than sampling a machine the leg has already left.
    /// </summary>
    /// <param name="cancellationToken">
    /// Stops the closing sample. An already-cancelled run still gets a report, and that report says
    /// the closing sample was never taken rather than showing a clean one.
    /// </param>
    public async Task<ContentionReport> StopAsync(CancellationToken cancellationToken = default)
    {
        if (_report is { } existing)
        {
            return existing;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        await _loop.ConfigureAwait(false);

        var index = Next();
        var elapsed = _elapsed.Elapsed;

        var last = cancellationToken.IsCancellationRequested
            ? new ProcessSample(index, elapsed, [], "the run was interrupted before the closing sample")
            : await _sampler.SampleAsync(index, elapsed, cancellationToken).ConfigureAwait(false);

        Add(last);

        lock (_gate)
        {
            _report ??= Classify([.. _samples], _request, _pathComparison, Environment.ProcessId);
            return _report;
        }
    }

    /// <summary>Stops sampling. A report that was never asked for is simply not produced.</summary>
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _loop.ConfigureAwait(false);
        _stop.Dispose();
    }

    private async Task SampleEveryAsync(TimeSpan every)
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await Task.Delay(every, _stop.Token).ConfigureAwait(false);
                Add(await _sampler.SampleAsync(Next(), _elapsed.Elapsed, _stop.Token).ConfigureAwait(false));
            }
        }
        catch (OperationCanceledException)
        {
            // The leg finished; StopAsync takes the closing sample.
        }
    }

    private int Next()
    {
        lock (_gate)
        {
            return _samples.Count;
        }
    }

    private void Add(ProcessSample sample)
    {
        lock (_gate)
        {
            _samples.Add(sample);
        }
    }

    /// <summary>
    /// What the whole set of samples found: which processes outside the harness's own tree used
    /// this leg's build directory, and when each was seen.
    /// </summary>
    /// <param name="samples">Every reading, the first and the last among them.</param>
    /// <param name="request">The leg's build directory and the tools that matter.</param>
    /// <param name="pathComparison">How paths compare on the host that was sampled.</param>
    /// <param name="harnessId">The harness's own process id, whose descendants are not contenders.</param>
    internal static ContentionReport Classify(
        IReadOnlyList<ProcessSample> samples,
        ContentionRequest request,
        StringComparison pathComparison,
        int harnessId)
    {
        var firstIndex = samples[0].Index;
        var lastIndex = samples[^1].Index;
        var tracked = new Dictionary<string, Tracked>(StringComparer.Ordinal);

        foreach (var sample in samples)
        {
            var byId = new Dictionary<int, SampledProcess>();

            foreach (var process in sample.Processes)
            {
                byId[process.Id] = process;
            }

            foreach (var process in sample.Processes)
            {
                var key = Identity(process);

                if (!tracked.TryGetValue(key, out var entry))
                {
                    entry = new Tracked(process, ProcessSampler.InHarnessTree(process, byId, harnessId));
                    tracked[key] = entry;
                }

                entry.Samples.Add(sample.Index);
            }
        }

        var contenders = new List<ContendingProcess>();
        var shared = new List<ContendingProcess>();

        foreach (var entry in tracked.Values.Where(entry => !entry.Inside))
        {
            var seen = Seen(entry.Samples, firstIndex, lastIndex);

            if (Tool(entry.Process, request.BuildTools) is { } buildTool
                && NamesBuildDirectory(entry.Process.CommandLine, request.BuildDirectory, pathComparison))
            {
                contenders.Add(new ContendingProcess(entry.Process, seen, buildTool));
            }
            else if (Tool(entry.Process, request.SharedResourceTools) is { } sharedTool)
            {
                // No build directory is asked of these: they share a cache rather than a build
                // directory, which is why they are a warning and not a verdict.
                shared.Add(new ContendingProcess(entry.Process, seen, sharedTool));
            }
        }

        var unreadable = samples
            .Where(sample => sample.Unreadable is not null)
            .Select(sample => $"sample {sample.Index} at {sample.Elapsed:g}: {sample.Unreadable}")
            .ToList();

        var limits = new List<string>(ProcessSampler.KnownBlindSpots);

        if (samples.Any(sample => sample.Processes.Count > 0)
            && samples.All(sample => sample.Processes.All(process => process.CommandLine is null)))
        {
            limits.Add("no command line could be read on this machine, so nothing was matched against the build directory");
        }

        return new ContentionReport(
            samples,
            [.. contenders.OrderBy(found => found.Process.Id)],
            [.. shared.OrderBy(found => found.Process.Id)],
            unreadable,
            limits);
    }

    /// <summary>
    /// How one process is told apart from another across samples: its id together with its start
    /// time.
    /// <para>
    /// Where a start time could not be read at all, the id alone has to serve. Keeping the samples
    /// apart instead — which is what naming the sample did — turns one process into one entry per
    /// sample, and a contender that never left is then reported as having come at the start and
    /// again at the end. On a host where nothing can publish a start time that is every process in
    /// every report, which is a worse and far more likely wrong answer than an id coming back around
    /// to a different process inside one leg.
    /// </para>
    /// </summary>
    private static string Identity(SampledProcess process)
        => process.StartedUtc is { } started
            ? string.Create(CultureInfo.InvariantCulture, $"{process.Id}@{started.UtcTicks}")
            : string.Create(CultureInfo.InvariantCulture, $"{process.Id}@?");

    private static ProcessSeen Seen(IReadOnlySet<int> samples, int first, int last)
    {
        var atStart = samples.Contains(first);
        var atEnd = samples.Contains(last);

        return (atStart, atEnd) switch
        {
            (true, true) => ProcessSeen.Throughout,
            (true, false) => ProcessSeen.AtTheStart,
            (false, true) => ProcessSeen.AtTheEnd,
            _ => ProcessSeen.DuringTheRun,
        };
    }

    /// <summary>
    /// The configured tool <paramref name="process"/> is, matched on the program's name rather than
    /// anywhere in the command line: a path that merely mentions <c>cmake</c> is not cmake running.
    /// </summary>
    private static string? Tool(SampledProcess process, IReadOnlyList<string> tools)
    {
        foreach (var tool in tools)
        {
            var name = Path.GetFileNameWithoutExtension(tool.Trim());

            if (name.Length > 0 && string.Equals(process.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return tool;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a command line names this leg's build directory. Both separators are tried: a tool
    /// started under one shell writes the path the other way round, and it is the same directory.
    /// </summary>
    private static bool NamesBuildDirectory(string? commandLine, string buildDirectory, StringComparison pathComparison)
    {
        if (commandLine is null || buildDirectory.Length == 0)
        {
            return false;
        }

        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(buildDirectory));

        return commandLine.Contains(directory, pathComparison)
            || commandLine.Contains(directory.Replace('\\', '/'), pathComparison)
            || commandLine.Contains(directory.Replace('/', '\\'), pathComparison);
    }

    private sealed class Tracked(SampledProcess process, bool inside)
    {
        public SampledProcess Process { get; } = process;

        public bool Inside { get; } = inside;

        public HashSet<int> Samples { get; } = [];
    }
}
