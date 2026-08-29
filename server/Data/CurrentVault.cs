namespace CardVault.Data;

/// <summary>
/// Which collection the work in hand belongs to.
///
/// An <see cref="AsyncLocal{T}"/> rather than something taken from the HTTP request,
/// because not all the work is a request: the daily price capture runs on a timer and
/// has to sweep every vault in turn. Both callers set this the same way, and
/// everything downstream — eighteen services holding one <see cref="Db"/> — carries on
/// knowing nothing about vaults at all.
/// </summary>
public static class CurrentVault
{
    /// <summary>
    /// The vault that was there before there were vaults. Its files stay at the root
    /// of the data directory, exactly where a single-vault install already put them,
    /// so gaining this feature moves nobody's collection.
    /// </summary>
    public const string Default = "default";

    private static readonly AsyncLocal<string?> Value = new();

    public static string Id
    {
        get => Value.Value ?? Default;
        set => Value.Value = string.IsNullOrWhiteSpace(value) ? Default : value;
    }

    /// <summary>
    /// Runs something against one vault and puts the previous one back afterwards.
    /// Used by the background jobs, which visit every vault and must not leave the
    /// last one they touched as the ambient answer for whatever runs next.
    /// </summary>
    public static void With(string vaultId, Action work)
    {
        var previous = Value.Value;
        Value.Value = vaultId;
        try { work(); }
        finally { Value.Value = previous; }
    }

    public static async Task WithAsync(string vaultId, Func<Task> work)
    {
        var previous = Value.Value;
        Value.Value = vaultId;
        try { await work(); }
        finally { Value.Value = previous; }
    }
}
