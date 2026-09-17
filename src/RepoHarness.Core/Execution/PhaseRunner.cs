using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Output;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Execution;

/// <summary>One phase to run: a program, its arguments, and the evidence it must produce.</summary>
public sealed record PhaseRequest
{
    /// <summary>The leg this phase belongs to, which the ledger and the log path are keyed by.</summary>
    public required string Leg { get; init; }

    /// <summary>The phase's name, which also names its log file.</summary>
    public required string Phase { get; init; }

    /// <summary>
    /// The program: a name looked up on PATH, or a path. Never a shell string, so no shell's process
    /// emulation sits between the harness and the runner — MSYS's was measured losing tests from a
    /// parallel test run with no failure reported.
    /// </summary>
    public required string FileName { get; init; }

    /// <summary>Arguments, one element per argument, never pre-quoted.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Where the log of this phase's output is written; its directory is created if missing.</summary>
    public required string LogFile { get; init; }

    /// <summary>Working directory for the command, or <see langword="null"/> to inherit this one.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Environment overrides; a <see langword="null"/> value removes the variable.</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; init; }
        = new Dictionary<string, string?>(StringComparer.Ordinal);

    /// <summary>
    /// The pattern proving the command ran, or <see langword="null"/> where the phase declares none.
    /// An empty or blank pattern matches anything, so it is refused rather than honoured.
    /// </summary>
    public string? SuccessPattern { get; init; }

    /// <summary>Seconds without output after which the phase is hung; zero disables the bound.</summary>
    public int StallSeconds { get; init; }

    /// <summary>Patterns whose every match is pulled out of the command's output.</summary>
    public IReadOnlyList<string> TimingPatterns { get; init; } = [];

    /// <summary>
    /// Milliseconds wall-clock time may drift from monotonic time across the phase before it is
    /// recorded as having spanned a clock step; zero or less disables the check, since drift below a
    /// millisecond is ordinary and flagging it would mark every phase suspect.
    /// </summary>
    public int ClockStepToleranceMilliseconds { get; init; }

    /// <summary>
    /// Masks a line of the child's output before anything keeps or shows it, or <see langword="null"/>
    /// when the phase carries nothing to mask.
    /// </summary>
    /// <remarks>
    /// Applied here rather than by the caller afterwards, because for the verbose echo there is no
    /// afterwards: a line reaches the terminal as the child writes it, and masking only the finished
    /// log would leave the value in front of whoever asked to watch. The caller that supplied the
    /// secret is the only one that knows what to look for, so it supplies the mask too.
    /// </remarks>
    public Func<string, string>? RedactLine { get; init; }
}

/// <summary>
/// Runs one phase of a leg and reports what it established.
/// </summary>
/// <remarks>
/// The rules here are the ones a green result stops meaning anything without: the exit code is read
/// from the process, a declared pattern must match the command's own output, a phase is bounded by
/// its silence rather than by a guess at how long it should take, and every duration comes from the
/// monotonic clock while the wall clock is watched for the steps one host makes every few seconds.
/// </remarks>
public sealed class PhaseRunner(IProcessRunner processRunner, IFileSystem fileSystem, IHarnessOutput output)
{
    /// <summary>
    /// How long a pattern may spend on one match. A pattern that backtracks past this is refused
    /// rather than left to run: an unbounded match in a phase's own output would hang the leg it was
    /// supposed to be evidence for.
    /// </summary>
    private static readonly TimeSpan MatchBudget = TimeSpan.FromSeconds(5);

    /// <summary>Written without a byte order mark, so a regex reading the log sees the first line.</summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly IProcessRunner _processRunner = processRunner;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IHarnessOutput _output = output;

    /// <summary>Runs <paramref name="request"/> to completion, or until it stalls.</summary>
    /// <param name="request">The phase.</param>
    /// <param name="cancellationToken">Stops the child and its descendants.</param>
    /// <exception cref="HarnessException">
    /// The success pattern is empty, or a declared pattern is not a regular expression. Refused
    /// rather than treated as "no pattern": a phase whose witness cannot be evaluated would pass on
    /// its exit code alone, which is exactly what the witness exists to stop.
    /// </exception>
    /// <exception cref="OperationCanceledException">The caller cancelled; the child tree was stopped.</exception>
    public async Task<PhaseResult> RunAsync(PhaseRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var success = CompileWitness(request);
        var timings = request.TimingPatterns.Select(pattern => (Pattern: pattern, Regex: Compile(pattern, "a timing pattern"))).ToList();

        var directory = Path.GetDirectoryName(Path.GetFullPath(request.LogFile));
        if (!string.IsNullOrEmpty(directory))
        {
            _fileSystem.CreateDirectory(directory);
        }

        // FileShare.ReadWrite, and flushed per line: a phase that hangs must have a readable log
        // while it is hanging, which is the only evidence of what it was doing.
        await using var log = new StreamWriter(
            new FileStream(request.LogFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite),
            Utf8NoBom)
        {
            AutoFlush = true,
        };

        var gate = new Lock();
        // Silence is the child's, so the clock is told when the child starts. Everything before that
        // — reading the request, opening the log, resolving the program, a machine under load taking
        // its time over any of it — is this tool's own, and counting it as the child being quiet is
        // how a slow launch reads as a hung command.
        var clock = new StallClock();
        var streamed = new StringBuilder();

        WriteHeader(log, gate, request);

        void Line(string raw, bool error)
        {
            clock.Saw();

            // Redacted before the line reaches anything that keeps or shows it, not after. A phase
            // whose environment carries a secret can print it, and the log file, the retained output
            // and the verbose echo all read from here: masking only the finished log would leave the
            // value on the terminal of whoever asked for --verbose, which is the one place a reader
            // is certain to be looking.
            var line = request.RedactLine is { } redact ? redact(raw) : raw;

            lock (gate)
            {
                log.WriteLine(line);

                // Kept as well as written, because a phase stopped for stalling reports no captured
                // output: the runner never returns, and what the child said before it went quiet is
                // the only account of what it was doing.
                streamed.Append(line).Append('\n');
            }

            // Progress is one line per leg transition, not a stream of child output; the child's own
            // output is echoed only when it was asked for. Echoed, it says which leg and phase wrote
            // it: legs run at the same time, so without that the terminal carries several children's
            // output woven together with nothing to tell one from another. The log files keep the
            // line as the child wrote it — this prefix is for the terminal alone, so nothing that
            // reads a log has to know about it.
            if (_output.IsVerbose)
            {
                var tagged = $"{request.Leg}/{request.Phase}: {line}";

                if (error)
                {
                    _output.RawError(tagged);
                }
                else
                {
                    _output.Raw(tagged);
                }
            }
        }

        var processRequest = new ProcessRequest
        {
            FileName = request.FileName,
            Arguments = request.Arguments,
            WorkingDirectory = request.WorkingDirectory,
            Environment = request.Environment,
            OnStarted = clock.Saw,
            OnOutputLine = line => Line(line, error: false),
            OnErrorLine = line => Line(line, error: true),

            // Deliberately no Timeout: a wall-clock budget is a guess about workload size, and an
            // honest run that exceeds it gets killed. The stall bound below is the bound in force.
        };

        var startedUtc = DateTimeOffset.UtcNow;
        var result = await RunBoundedAsync(processRequest, request.StallSeconds, clock, cancellationToken).ConfigureAwait(false);
        var wall = DateTimeOffset.UtcNow - startedUtc;

        // Both readings cover the same window, so what they disagree by is the clock's own movement:
        // a step forward, a step back, or a host that slept in the middle of the phase.
        var drift = Abs(wall - clock.Elapsed);
        var stepped = request.ClockStepToleranceMilliseconds > 0
            && drift > TimeSpan.FromMilliseconds(request.ClockStepToleranceMilliseconds);

        // Matched against what the child wrote and nothing else. The log above also holds the header
        // this run wrote, and a header that echoes the command line contains the pattern whenever
        // the command does, so a phase that never ran would witness itself.
        var childOutput = Combine(result.StandardOutput, result.StandardError);

        if (childOutput.Length == 0)
        {
            lock (gate)
            {
                childOutput = streamed.ToString();
            }
        }

        lock (gate)
        {
            log.WriteLine($"# exit {(result.TimedOut ? "(stopped)" : result.ExitCode.ToString(CultureInfo.InvariantCulture))} after {clock.Elapsed}");
        }

        // Redacted before it leaves this method, not at each place that later reads it. The captured
        // text is the one copy of the child's output that outlives the run — it reaches the ledger's
        // detail, an expected exception's message and the verdict — and a redaction applied by every
        // reader is one a new reader can forget. The witness and the timings are matched against the
        // redacted text too, deliberately: a success pattern that only matches a password is a
        // pattern nobody should be able to write.
        var visible = request.RedactLine is { } redactAll ? redactAll(childOutput) : childOutput;

        return new PhaseResult(
            Leg: request.Leg,
            Phase: request.Phase,
            ExitCode: result.ExitCode,
            Stalled: result.TimedOut,
            StallSeconds: request.StallSeconds,
            Witnessed: success is null ? null : Matches(success, visible, request.SuccessPattern!),
            Duration: result.Duration,
            ClockDrift: drift,
            ClockStepped: stepped,
            Timings: Extract(timings, visible, request.Phase),
            LogFile: request.LogFile,
            Output: visible);
    }

    /// <summary>
    /// Runs the child, stopping it when it goes <paramref name="stallSeconds"/> without a line on
    /// either stream. The bound is enforced through a source of this method's own, so a hung phase
    /// and a run the operator interrupted stay distinguishable: the first comes back as a stopped
    /// phase to be reported, the second is raised and ends the leg.
    /// </summary>
    private async Task<ProcessResult> RunBoundedAsync(
        ProcessRequest request,
        int stallSeconds,
        StallClock clock,
        CancellationToken cancellationToken)
    {
        if (stallSeconds <= 0)
        {
            return await _processRunner.RunAsync(request, cancellationToken).ConfigureAwait(false);
        }

        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var finished = new CancellationTokenSource();
        var bound = TimeSpan.FromSeconds(stallSeconds);
        var watching = WatchAsync(clock, bound, stall, finished.Token);

        try
        {
            return await _processRunner.RunAsync(request, stall.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The bound fired, and the runner stopped the child and its descendants. The exit code
            // is meaningless, which is what a stopped phase reports; the lines the child wrote
            // before it went quiet were kept as they arrived.
            return new ProcessResult(-1, string.Empty, string.Empty, clock.Elapsed, TimedOut: true);
        }
        finally
        {
            await finished.CancelAsync().ConfigureAwait(false);
            await watching.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// How much longer than the silence bound a child may take to start. Starting is this tool's own
    /// work and is not counted as the child being quiet, but it is not unbounded either: resolving a
    /// program walks every entry of PATH, and one naming an unreachable share blocks for that
    /// platform's own timeout per entry, as does a working directory on a mount that has gone away.
    /// <para>
    /// A multiple of the silence bound rather than a number of its own, because the two measure
    /// different things and only one of them repeats. A child may legitimately be silent for its
    /// whole bound over and over; it starts once. Four times over is past any honest launch and
    /// still finite, and it scales with how patient the run was asked to be, which is the knob
    /// somebody actually sets.
    /// </para>
    /// </summary>
    private const int StartBoundMultiple = 4;

    /// <summary>
    /// Watches the silence and cancels <paramref name="stall"/> when it passes <paramref name="bound"/>,
    /// or when the child has not started within <see cref="StartBoundMultiple"/> times that. The poll
    /// is a quarter of the bound so the phase is stopped near the moment it is due, and never more
    /// often than every 50 milliseconds, which would cost more than it measures.
    /// </summary>
    private static async Task WatchAsync(StallClock clock, TimeSpan bound, CancellationTokenSource stall, CancellationToken finished)
    {
        var poll = TimeSpan.FromMilliseconds(Math.Clamp(bound.TotalMilliseconds / 4, 50, 1000));

        try
        {
            while (!finished.IsCancellationRequested)
            {
                await Task.Delay(poll, finished).ConfigureAwait(false);

                if (clock.Overdue(bound, bound * StartBoundMultiple))
                {
                    await stall.CancelAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The phase finished, so there is nothing left to bound.
        }
    }

    private static void WriteHeader(StreamWriter log, Lock gate, PhaseRequest request)
    {
        // Written for whoever reads the log, and deliberately never matched against: this is the
        // text a success pattern would otherwise witness itself in.
        lock (gate)
        {
            log.WriteLine($"# leg {request.Leg}, phase {request.Phase}");
            log.WriteLine($"# command {request.FileName} {string.Join(' ', request.Arguments)}");
            log.WriteLine($"# started {DateTimeOffset.UtcNow:u}");
        }
    }

    private static Regex? CompileWitness(PhaseRequest request)
    {
        if (request.SuccessPattern is not { } pattern)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(pattern))
        {
            throw new HarnessException(
                HarnessExit.ConfigInvalid,
                $"Phase '{request.Phase}' of leg '{request.Leg}' declares an empty success pattern, which matches anything; "
                + "declare the line the command prints when it succeeds, or declare no pattern.");
        }

        return Compile(pattern, "a success pattern");
    }

    private static Regex Compile(string pattern, string what)
    {
        try
        {
            // Multiline, so a pattern anchored with ^ or $ means the start or end of a line of
            // output rather than of the whole capture, which is how such a pattern is written.
            return new Regex(pattern, RegexOptions.Multiline | RegexOptions.CultureInvariant, MatchBudget);
        }
        catch (ArgumentException ex)
        {
            throw new HarnessException(HarnessExit.ConfigInvalid, $"'{pattern}' is not {what}: {ex.Message}", ex);
        }
    }

    private static bool Matches(Regex regex, string text, string pattern)
    {
        try
        {
            return regex.IsMatch(text);
        }
        catch (RegexMatchTimeoutException ex)
        {
            // Never reported as "did not match": a pattern that could not be evaluated is not
            // evidence either way, and a leg whose witness cannot be read must say so.
            throw new HarnessException(
                HarnessExit.ConfigInvalid,
                $"The pattern '{pattern}' took longer than {MatchBudget.TotalSeconds:0}s against this phase's output, "
                + "so whether it matched is unknown; it is written in a form that backtracks.",
                ex);
        }
    }

    private IReadOnlyList<PhaseTiming> Extract(IReadOnlyList<(string Pattern, Regex Regex)> patterns, string text, string phase)
    {
        var found = new List<PhaseTiming>();

        foreach (var (pattern, regex) in patterns)
        {
            try
            {
                foreach (var match in regex.Matches(text).Cast<Match>())
                {
                    var value = match.Groups.Count > 1 && match.Groups[1].Success ? match.Groups[1].Value : match.Value;
                    found.Add(new PhaseTiming(pattern, match.Value, value));
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // A timing mark never changes a verdict, so a pattern that cannot be read leaves
                // this phase unmeasured rather than failing it. Said out loud, because a timing
                // silently missing from a report reads as a phase that reported no timing.
                _output.Warn(phase, $"the timing pattern '{pattern}' could not be matched against this phase's output in time; no timing was taken from it");
            }
        }

        return found;
    }

    /// <summary>Both streams as the child wrote them, with a line break between when both carry text.</summary>
    private static string Combine(string standardOutput, string standardError)
        => standardOutput.Length == 0
            ? standardError
            : standardError.Length == 0
                ? standardOutput
                : standardOutput + "\n" + standardError;

    private static TimeSpan Abs(TimeSpan value) => value < TimeSpan.Zero ? -value : value;

    /// <summary>
    /// How long the phase has run and how long it has been silent, both from the monotonic clock.
    /// A wall clock cannot answer either question on a host whose clock steps forward by 25 seconds
    /// every few seconds: the phase would look hung the moment the clock moved.
    /// </summary>
    private sealed class StallClock
    {
        /// <summary>What <see cref="_lastOutputMs"/> holds before the child has started.</summary>
        private const long NotYet = -1;

        private readonly Stopwatch _elapsed = Stopwatch.StartNew();
        private long _lastOutputMs = NotYet;

        /// <summary>How long the phase has run.</summary>
        public TimeSpan Elapsed => _elapsed.Elapsed;

        /// <summary>
        /// How long since the last line on either stream, and nothing at all until the child is
        /// running. A process that has not started yet has not been quiet: the time spent starting
        /// one belongs to this tool, and counting it against the child makes a machine under load
        /// look like a hung command. That window has a bound of its own, in
        /// <see cref="Overdue(TimeSpan, TimeSpan)"/>.
        /// </summary>
        public TimeSpan Quiet
        {
            get
            {
                var last = Interlocked.Read(ref _lastOutputMs);

                return last == NotYet
                    ? TimeSpan.Zero
                    : TimeSpan.FromMilliseconds(_elapsed.ElapsedMilliseconds - last);
            }
        }

        /// <summary>
        /// Whether the phase is past whichever bound is in force: how long the child may take to
        /// start before it has started, how long it may stay silent after.
        /// </summary>
        /// <param name="quiet">How long the child may say nothing.</param>
        /// <param name="starting">How long the child may take to start.</param>
        /// <remarks>
        /// Two bounds rather than one because they measure different things. Counting the launch as
        /// silence makes a slow machine read as a hung command, which is the defect this clock was
        /// written for; leaving the launch unmeasured makes an unreachable PATH entry a phase that
        /// never ends and never reports.
        /// </remarks>
        public bool Overdue(TimeSpan quiet, TimeSpan starting)
            => Interlocked.Read(ref _lastOutputMs) == NotYet
                ? _elapsed.Elapsed >= starting
                : Quiet >= quiet;

        /// <summary>
        /// Records that the child started, or said something. Called from the thread that starts the
        /// child and from the reader threads of both streams, so it is interlocked.
        /// </summary>
        public void Saw() => Interlocked.Exchange(ref _lastOutputMs, _elapsed.ElapsedMilliseconds);
    }
}
