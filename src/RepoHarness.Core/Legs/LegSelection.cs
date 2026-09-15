using RepoHarness.Core.Configuration;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Legs;

/// <summary>A leg chosen for a command.</summary>
/// <param name="Name">The leg's name, as the configuration declares it.</param>
/// <param name="Leg">The leg.</param>
public sealed record SelectedLeg(string Name, LegConfig Leg);

/// <summary>Which legs a command acts on.</summary>
/// <param name="Legs">The legs, in the order they were named, or in the configuration's order when none were.</param>
/// <param name="Named">Whether they were named with <c>--legs</c>, rather than being every declared leg.</param>
public sealed record LegSelection(IReadOnlyList<SelectedLeg> Legs, bool Named)
{
    /// <summary>
    /// Resolves what <c>--legs</c> was given. A value may hold several names separated by commas, so
    /// <c>--legs a,b</c>, <c>--legs a, b</c> and <c>--legs a b</c> select the same two legs, and a leg
    /// set's name selects its legs. Giving no names selects every declared leg.
    /// </summary>
    /// <exception cref="HarnessException">
    /// A name is neither a leg nor a leg set. Raised before any host is measured, so a typo never
    /// costs a connection attempt, let alone a run.
    /// </exception>
    public static LegSelection Resolve(HarnessConfig config, IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(values);

        var names = values
            .SelectMany(value => value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .ToList();

        if (names.Count == 0)
        {
            return new LegSelection([.. config.Legs.Select(pair => new SelectedLeg(pair.Key, pair.Value))], Named: false);
        }

        var unknown = names
            .Where(name => !config.Legs.ContainsKey(name) && !config.LegSets.ContainsKey(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (unknown.Count > 0)
        {
            var declared = config.Legs.Count == 0 ? "no legs are declared" : $"declared legs: {string.Join(", ", config.Legs.Keys)}";

            throw new HarnessException(
                HarnessExit.UsageError,
                $"--legs names {string.Join(", ", unknown.Select(name => $"'{name}'"))}, which is neither a leg nor a leg set; {declared}");
        }

        var selected = new List<SelectedLeg>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in names)
        {
            IEnumerable<string> legNames = config.LegSets.TryGetValue(name, out var set) ? set : [name];

            foreach (var legName in legNames)
            {
                // Reported under the name the configuration declares, whatever case it was typed in.
                var declaredName = config.Legs.Keys.First(key => string.Equals(key, legName, StringComparison.OrdinalIgnoreCase));

                if (seen.Add(declaredName))
                {
                    selected.Add(new SelectedLeg(declaredName, config.Legs[declaredName]));
                }
            }
        }

        return new LegSelection(selected, Named: true);
    }
}
