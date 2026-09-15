// Reads or bumps the <Version> element of a .NET project file, and renders the
// version a release channel publishes under.
//
// A file-based app rather than inline shell: the release path already guarantees
// the .NET SDK, so this needs no language the repository does not otherwise use,
// and it can be run locally exactly as CI runs it:
//
//     dotnet run .github/scripts/version.cs read   src/RepoHarness.Cli/RepoHarness.Cli.csproj
//     dotnet run .github/scripts/version.cs bump   src/RepoHarness.Cli/RepoHarness.Cli.csproj [1.2.3]
//     dotnet run .github/scripts/version.cs suffix 1.2.3 beta
//
// Inline shell was the alternative, and the quoting it needs is the kind that only
// fails once it reaches CI.

using System.Text.RegularExpressions;

var versionElement = new Regex("<Version>([^<]+)</Version>", RegexOptions.Compiled);
var threePart = new Regex(@"^\d+\.\d+\.\d+$", RegexOptions.Compiled);

if (args.Length < 2)
{
    return Fail("usage: version <read|bump|suffix> ...");
}

switch (args[0])
{
    case "read":
        {
            var (current, _, _) = ReadVersion(args[1]);
            Console.WriteLine(current);
            return 0;
        }

    case "bump":
        {
            var requested = args.Length > 2 ? args[2] : string.Empty;
            return Bump(args[1], requested);
        }

    case "suffix":
        {
            if (args.Length < 3)
            {
                return Fail("suffix needs <base-version> <channel>");
            }

            return Suffix(args[1], args[2]);
        }

    default:
        return Fail($"unknown command '{args[0]}'");
}

(string Current, Match Match, string Text) ReadVersion(string projectPath)
{
    string text;

    try
    {
        text = File.ReadAllText(projectPath);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        // Reported as an annotation like every other failure here, rather than as an
        // unhandled exception whose stack trace buries the cause in the Actions log.
        Fail($"cannot read {projectPath}: {ex.Message}");
        throw;
    }

    var matches = versionElement.Matches(text);

    if (matches.Count == 0)
    {
        Fail($"no <Version> element in {projectPath}");
    }

    // With more than one, read and bump would act on the first while MSBuild resolves
    // another, and the published version would silently differ from the committed one.
    if (matches.Count > 1)
    {
        Fail($"{projectPath} declares <Version> {matches.Count} times; exactly one is required");
    }

    var match = matches[0];
    return (match.Groups[1].Value, match, text);
}

int Bump(string projectPath, string requested)
{
    var (current, match, text) = ReadVersion(projectPath);
    string next;

    if (!string.IsNullOrWhiteSpace(requested))
    {
        next = requested.Trim();
    }
    else
    {
        // Only the patch component moves automatically. A major or minor change is
        // a deliberate decision, made by passing the version outright.
        var parts = current.Split('-')[0].Split('.');

        if (parts.Length < 3 || !int.TryParse(parts[2], out var patch))
        {
            return Fail($"'{current}' is not a three part version");
        }

        next = $"{parts[0]}.{parts[1]}.{patch + 1}";
    }

    if (!threePart.IsMatch(next))
    {
        return Fail($"'{next}' is not a three part version");
    }

    if (string.Equals(next, current, StringComparison.Ordinal))
    {
        return Fail($"version is already {current}; refusing a release that changes nothing");
    }

    // A lower version passes every other guard and publishes successfully, but NuGet
    // orders it below what is already released, so consumers never resolve it: the
    // release reports green having shipped nothing anyone can install.
    var currentBase = current.Split('-')[0];
    if (Version.TryParse(currentBase, out var currentParsed)
        && Version.TryParse(next, out var nextParsed)
        && nextParsed < currentParsed)
    {
        return Fail($"'{next}' is lower than the current {current}; a release must move forwards");
    }

    File.WriteAllText(
        projectPath,
        string.Concat(text.AsSpan(0, match.Index), $"<Version>{next}</Version>", text.AsSpan(match.Index + match.Length)));

    Console.WriteLine(next);
    return 0;
}

int Suffix(string baseVersion, string channel)
{
    if (!threePart.IsMatch(baseVersion))
    {
        return Fail($"'{baseVersion}' is not a three part version");
    }

    // stable publishes the bare version; beta publishes it as a prerelease. A beta needs
    // no build number to be new: every beta deploy bumps <Version> first, and the package
    // pipeline refuses a version whose tag or release already exists, so the same beta can
    // never be published twice.
    switch (channel)
    {
        case "stable":
            Console.WriteLine(baseVersion);
            return 0;

        case "beta":
            Console.WriteLine($"{baseVersion}-beta");
            return 0;

        default:
            return Fail($"unknown channel '{channel}'; expected beta or stable");
    }
}

int Fail(string message)
{
    Console.Error.WriteLine($"::error::{message}");
    Environment.Exit(1);
    return 1;
}
