using System.Globalization;
using RepoHarness.Core.Build;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Legs;

/// <summary>
/// Whether a leg's host has the room its build still needs: what is asked of each host before placing, and
/// the legs placed where there is not enough.
/// </summary>
/// <remarks>
/// A consumer's two variants, the first builds of a new worktree's copy, filled a host's disk half way
/// through and died writing an object, while <c>legs</c> said the host could run them. A leg's need is what
/// its build directory comes to once built - declared as <see cref="LegConfig.BuildSpaceGiB"/>, or what a
/// build of its variant recorded there: in the tree's own copy, or else in the main checkout's - less what
/// that directory already holds. Nothing is walked: each build records what its directory came to as it
/// finishes. Legs sharing a filesystem on one host are counted together, in the order they were selected,
/// because every build directory stays once built; a leg that no longer fits beside the ones before it is
/// turned away, and they are kept. A leg whose need nothing says is placed as it always was; one whose need
/// is known where the room could not be measured is placed and said to be unchecked.
/// </remarks>
public static class LegRoom
{
    private const long Gibibyte = 1L << 30;

    /// <summary>
    /// What to ask each candidate host about the room on it: where its copies are kept, and each selected
    /// leg's build directory there with the main checkout's copy of the same variant.
    /// </summary>
    /// <param name="context">The repository and its configuration.</param>
    /// <param name="candidates">Each selected leg, with the hosts it could be placed on.</param>
    /// <param name="here">The host this machine is to the machine that sent the legs here, or <see langword="null"/>.</param>
    /// <param name="comparison">How this machine compares paths.</param>
    /// <param name="builds">Whether the command builds, so that its legs' build directories are worth asking about.</param>
    public static IReadOnlyDictionary<HostId, RoomQuestions> Questions(
        HarnessContext context,
        IEnumerable<(SelectedLeg Leg, IReadOnlyList<HostId> Hosts)> candidates,
        HostId? here,
        StringComparison comparison,
        bool builds)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(candidates);

        var asked = new Dictionary<HostId, (string? SpaceAt, List<string> Builds)>();

        foreach (var (leg, hosts) in candidates)
        {
            foreach (var host in hosts)
            {
                // A host that declares nowhere to keep a copy is refused where the leg is placed on it; asked
                // nothing here.
                if (Paths(context, leg.Leg, host, here, comparison) is not { } paths)
                {
                    continue;
                }

                if (!asked.TryGetValue(host, out var questions))
                {
                    questions = (paths.Main, []);
                    asked[host] = questions;
                }

                if (builds)
                {
                    questions.Builds.AddRange(paths.Own == paths.MainBuild ? [paths.Own] : [paths.Own, paths.MainBuild]);
                }
            }
        }

        return asked.ToDictionary(pair => pair.Key, pair => new RoomQuestions(pair.Value.SpaceAt, [.. pair.Value.Builds.Distinct(StringComparer.Ordinal)]));
    }

    /// <summary>
    /// <paramref name="placements"/>, with each placed leg whose host lacks the room its build still needs
    /// turned away - skipped-unavailable, saying what is free and what it needs - and each leg placed with a
    /// known need where the room could not be measured, said as that.
    /// </summary>
    /// <param name="context">The repository and its configuration.</param>
    /// <param name="placements">Where each selected leg was placed, in the order the legs were selected.</param>
    /// <param name="here">The host this machine is to the machine that sent the legs here, or <see langword="null"/>.</param>
    /// <param name="comparison">How this machine compares paths.</param>
    public static (IReadOnlyList<LegPlacement> Placements, IReadOnlyList<string> Unchecked) Apply(
        HarnessContext context,
        IReadOnlyList<LegPlacement> placements,
        HostId? here,
        StringComparison comparison)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(placements);

        var placed = placements.ToList();
        var taken = new Dictionary<(HostId Host, string Filesystem), (long Bytes, List<string> Legs)>();
        var directories = new HashSet<(HostId Host, string Directory)>();
        var unmeasured = new List<string>();

        for (var index = 0; index < placed.Count; index++)
        {
            if (placed[index] is not { Host: { } host } placement
                || Need(context, placement.Leg, host, here, comparison) is not { } need)
            {
                continue;
            }

            if (need.Disk is not { } disk)
            {
                unmeasured.Add(
                    $"leg '{placement.Leg.Name}' was placed on {host.Host} without its room checked, which could not be "
                    + $"measured there: {need.Unmeasured}");
                continue;
            }

            // Counted once however many legs name it: two legs building one directory are refused as that when
            // the run is planned, and would otherwise be refused here for room the second never takes.
            if (!directories.Add((host.Host, need.Directory)))
            {
                continue;
            }

            var key = (host.Host, disk.Filesystem);
            var before = taken.TryGetValue(key, out var counted) ? counted : (Bytes: 0L, Legs: new List<string>());

            if (before.Bytes + need.Bytes > disk.FreeBytes)
            {
                var beside = before.Legs.Count == 0
                    ? string.Empty
                    : $", beside the ~{DiskSpace.Size(before.Bytes)} {string.Join(" and ", before.Legs.Select(name => $"'{name}'"))} need there";

                placed[index] = new LegPlacement(
                    placement.Leg,
                    null,
                    (here is null ? $"{host.Host}: " : string.Empty)
                    + $"{DiskSpace.Size(disk.FreeBytes)} free on '{disk.Filesystem}', and this leg needs ~{DiskSpace.Size(need.Bytes)}, "
                    + $"{need.Source}{beside}")
                {
                    Verdict = LegVerdict.SkippedUnavailable,
                };

                continue;
            }

            taken[key] = (before.Bytes + need.Bytes, [.. before.Legs, placement.Leg.Name]);
        }

        return (placed, unmeasured);
    }

    /// <summary>What a leg's build still needs on its host, and what is known of the room there.</summary>
    /// <param name="Bytes">What it still needs.</param>
    /// <param name="Disk">The room where its build directory is, or <see langword="null"/> where it could not be measured.</param>
    /// <param name="Source">What said how much it needs.</param>
    /// <param name="Directory">Its build directory there.</param>
    /// <param name="Unmeasured">Why the room could not be measured, where it could not.</param>
    private sealed record Needed(long Bytes, DiskSpace? Disk, string Source, string Directory, string? Unmeasured);

    /// <summary>
    /// What <paramref name="leg"/>'s build still needs on <paramref name="host"/>, where something says, with
    /// the room where its build directory is and what said so; <see langword="null"/> where nothing does.
    /// </summary>
    private static Needed? Need(
        HarnessContext context,
        SelectedLeg leg,
        HostReport host,
        HostId? here,
        StringComparison comparison)
    {
        if (Paths(context, leg.Leg, host.Host, here, comparison) is not { } paths
            || Answered(host, paths.Own) is not { } own)
        {
            return null;
        }

        var main = paths.MainBuild == paths.Own ? null : Answered(host, paths.MainBuild);

        var (expected, source) = leg.Leg.BuildSpaceGiB is { } declared
            ? ((long?)(long)Math.Ceiling(declared * Gibibyte), string.Create(CultureInfo.InvariantCulture, $"as its buildSpaceGiB, {declared:0.###}, declares"))
            : own.RecordedBytes is { } recorded
                ? (recorded, "what its last build there came to")
                : (main?.RecordedBytes, "what the main checkout's copy of the same variant came to there");

        // What the directory holds counts against its need only where its build recorded it: one there that no
        // build of this version recorded holds an amount nothing measured, and is left to build as it always did.
        long? present = !own.Exists ? 0 : own.RecordedBytes;

        return expected is { } bytes && present is { } held
            ? new Needed(Math.Max(0, bytes - held), own.Disk, source, paths.Own, own.Unmeasured ?? "no reason was given")
            : null;
    }

    /// <summary>What <paramref name="host"/> answered about the build directory at <paramref name="path"/>, as it was asked.</summary>
    private static BuildDirectoryRoom? Answered(HostReport host, string path)
        => host.Builds.FirstOrDefault(build => string.Equals(build.Path, path, StringComparison.Ordinal));

    /// <summary>
    /// The paths <paramref name="leg"/> has on <paramref name="host"/>: where the host keeps the main checkout's
    /// copy, the leg's own build directory there and the main checkout's copy of the same variant; or
    /// <see langword="null"/> where the host declares nowhere to keep a copy.
    /// </summary>
    /// <remarks>
    /// The variant for the leg's own operating system: a host of another is never given the leg, so the variant
    /// it would have there is never built.
    /// </remarks>
    private static (string Main, string Own, string MainBuild)? Paths(
        HarnessContext context,
        LegConfig leg,
        HostId host,
        HostId? here,
        StringComparison comparison)
    {
        try
        {
            var variant = VariantKey.For(context.Config, leg, leg.Os);
            var main = LegTrees.On(context, host, context.Layout.MainCheckoutRoot, comparison);
            var own = LegTrees.On(context, host, LegTrees.Here(context, leg, here), comparison);

            return (main, variant.DirectoryOn(host, own), variant.DirectoryOn(host, main));
        }
        catch (HarnessException)
        {
            return null;
        }
    }
}
