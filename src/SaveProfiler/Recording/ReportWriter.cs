using UtilLibs;

namespace SaveProfiler.Recording;

/// <summary>
/// Turns a finished recording into files on disk.
///
/// This is the half that has to ask the game questions - where the save went, what it is called,
/// which mods are loaded - so it is deliberately thin and every answer it gets is handed straight
/// to <see cref="SaveProfileReport"/>, which is Klei-free and therefore testable. The arithmetic
/// and the wording live there; only the I/O lives here.
///
/// Reports go to <c>Util.RootFolder()/save_profiler</c>, matching where the sibling mod puts user
/// blueprints - that is Klei's own writable root, not the install directory.
/// </summary>
internal static class ReportWriter
{
    private const string FolderName = "save_profiler";

    /// <summary>The active mod list, captured in <c>OnAllModsLoaded</c>. Recorded in every report
    /// because another mod patching the save path changes what was measured - Fast Save replaces
    /// the very serialization this profiler attributes, and a report that did not say so would be
    /// quietly describing a different program.</summary>
    internal static IReadOnlyList<string> ActiveMods { get; set; } = [];

    public static void WriteIfEnabled(string savePath, bool isAutoSave)
    {
        try
        {
            var config = Config.Instance;
            if (isAutoSave ? !config.RecordAutosaves : !config.RecordManualSaves)
                return;

            Write(savePath, isAutoSave, config);
        }
        catch (Exception e)
        {
            // A profiler must never be the reason a save fails. This runs in a Harmony postfix on
            // SaveLoader.Save; anything thrown here would propagate into the game's save path.
            SgtLogger.logError($"SaveProfiler: could not write the save profile: {e}");
        }
    }

    private static void Write(string savePath, bool isAutoSave, Config config)
    {
        SaveProfileReport report = SaveProfileRecorder.BuildReport();

        report.SavePath = savePath;
        report.IsAutoSave = isAutoSave;
        report.ColonyName = ColonyName(savePath);
        report.GameVersion = GameVersion();
        report.CompressedBytes = FileLength(savePath);
        report.UncompressedBytes = report.Phases
            .FirstOrDefault(p => p.Name == "SaveLoader.CompressContents")?.Snap.TotalBytes ?? 0;
        report.ActiveMods.AddRange(ActiveMods);

        string folder = Folder();
        string stem = Path.Combine(folder,
            $"{report.TimestampUtc:yyyyMMdd-HHmmss}-{Sanitize(report.ColonyName)}");

        File.WriteAllText(stem + ".md", report.ToMarkdown());
        if (config.WriteJson)
            File.WriteAllText(stem + ".json", report.ToJson());

        Rotate(folder, config.KeepReports);

        SgtLogger.l($"SaveProfiler: wrote {stem}.md ({report.TotalMs:F0} ms total)");
    }

    private static string Folder()
    {
        string folder = Path.Combine(Util.RootFolder(), FolderName);
        if (!Directory.Exists(folder))
            Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>Deletes the oldest reports past the keep count, newest-first per
    /// <see cref="ReportRotation"/>. The <c>.json</c> half goes with its <c>.md</c>.</summary>
    private static void Rotate(string folder, int keep)
    {
        var newestFirst = new DirectoryInfo(folder)
            .GetFiles("*.md")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => f.FullName)
            .ToList();

        foreach (string doomed in ReportRotation.ToDelete(newestFirst, keep))
        {
            TryDelete(doomed);
            TryDelete(Path.ChangeExtension(doomed, ".json"));
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception e)
        {
            SgtLogger.warning($"SaveProfiler: could not rotate out {path}: {e.Message}");
        }
    }

    private static long FileLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>The colony's own name when the game will give it up, else the save's file name -
    /// which is what the player recognises anyway.</summary>
    private static string ColonyName(string savePath)
    {
        try
        {
            string? baseName = SaveGame.Instance?.BaseName;
            if (!string.IsNullOrWhiteSpace(baseName))
                return baseName!;
        }
        catch (Exception)
        {
            // SaveGame.Instance is a live scene object; not reachable in every teardown order.
        }

        return string.IsNullOrEmpty(savePath) ? "unknown" : Path.GetFileNameWithoutExtension(savePath);
    }

    private static string GameVersion()
    {
        try
        {
            return KleiVersion.ChangeList.ToString();
        }
        catch (Exception)
        {
            return "";
        }
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = name.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray();
        string result = new string(cleaned).Trim();
        return result.Length == 0 ? "unknown" : result;
    }
}
