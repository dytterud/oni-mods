namespace SaveProfiler.Recording;

/// <summary>
/// Which of the reports already on disk to delete once a new one lands.
///
/// Split out and given plain strings so it can be tested without a filesystem - the same reason
/// <c>BlueprintPaths</c> exists in the sibling mod. The caller does the directory listing and the
/// deleting; this decides only what goes.
/// </summary>
public static class ReportRotation
{
    /// <summary>
    /// The entries to delete so that at most <paramref name="keep"/> remain, newest first.
    ///
    /// <paramref name="existing"/> is expected in newest-first order (the caller sorts by write
    /// time). A <paramref name="keep"/> of zero or less keeps everything: a config typo should
    /// not silently erase the reports the user was collecting.
    /// </summary>
    public static IReadOnlyList<string> ToDelete(IReadOnlyList<string> existing, int keep)
    {
        if (keep <= 0 || existing.Count <= keep)
            return [];

        return existing.Skip(keep).ToList();
    }
}
