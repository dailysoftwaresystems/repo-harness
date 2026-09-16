namespace RepoHarness.Core.Results;

/// <summary>
/// Joins several strings into one key that cannot be confused with a different set of parts.
/// </summary>
/// <remarks>
/// A key built by concatenation alone is ambiguous: a host named <c>build</c> with the tree
/// <c>/x</c> and a host named <c>buil</c> with the tree <c>d/x</c> produce one string, and a
/// dictionary that groups by it silently treats two legs as one. The separator is a byte no host
/// name, path, exception type or message can hold, which is what makes the join reversible in
/// principle and unambiguous in practice.
/// Kept here, and written as an escape, because the separator was once typed into three source
/// files as the raw byte: git reads a file holding a NUL as binary, so those files showed up in
/// review as "Binary files differ", could not be diffed, greped or merged, and passed unread.
/// </remarks>
public static class CompositeKey
{
    /// <summary>The separator the parts are joined with.</summary>
    public const string Separator = "\0";

    /// <summary>Joins <paramref name="parts"/> into one key.</summary>
    /// <param name="parts">The parts, in an order the caller keeps stable.</param>
    public static string Of(params ReadOnlySpan<string> parts) => string.Join(Separator, parts!);
}
