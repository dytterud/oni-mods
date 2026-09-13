using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BlueprintsV2.BlueprintData;
using UnityEngine;

namespace BlueprintsV2.Harness.Perf;

/// <summary>
/// docs/blueprints-included/in-game-regression-testing.md §7: the blueprint selection screen's open cost - the dialog
/// the Use Blueprint tool puts up, reported as slow to open.
///
/// Opening runs two independent pieces of work, so this measures them on their own axes rather
/// than as one blended number:
///
/// <list type="bullet">
/// <item><b>the file list</b> - <c>UpdateBlueprintButtons</c> walks the current folder and gives
/// every blueprint a <c>FileHierarchyEntry</c> GameObject, instantiated on first sight and cached
/// in <c>BlueprintEntries</c> thereafter. Swept over library size L with the preview suppressed,
/// both <i>cold</i> (entry cache emptied first - a session's first open) and <i>warm</i> (entries
/// already built - every open after that).</item>
/// <item><b>the preview</b> - <c>BlueprintPreviewScreen.LoadBlueprintPreview</c> destroys and
/// rebuilds one GameObject per building in the selected blueprint. Swept over building count N
/// with a fixed tiny library, for both visualizer branches (see
/// <see cref="SyntheticBlueprint.Build(int, string, string)"/>).</item>
/// </list>
///
/// Note which opens actually draw a preview in the real UI: <c>ShowingInfoPreview</c> starts false,
/// so a session's very first open shows no preview, but both close paths (<c>OnCloseClicked</c>,
/// <c>OnPlaceBlueprint</c>) set it back to true - so every subsequent open does. The sweep sets the
/// flag explicitly per case rather than depending on that ordering.
/// </summary>
internal static class SelectionScreenPerf
{
    /// <summary>Blueprints in the folder being listed. 500 is past what a tidy library holds - it
    /// is there to show the slope, not to claim it is typical.</summary>
    private static readonly int[] LibrarySizes = { 10, 50, 200, 500 };

    /// <summary>Buildings in the previewed blueprint. Capped at 2000 to stay at or under the
    /// <c>AutoPreviewCuttoff</c> default, above which the screen puts up a confirm prompt instead
    /// of drawing (so a bigger N would time the prompt, not the preview).</summary>
    private static readonly int[] PreviewSizes = { 100, 500, 1000, 2000 };

    private const int LibraryForPreviewSweep = 10;

    /// <summary>Sample counts. Deliberately higher than the placement sweep's: the selection-screen
    /// deltas worth chasing from here are single-digit percentages (entry pooling, hierarchy
    /// activation), and at the original 3-5 samples those were indistinguishable from run-to-run
    /// drift - a run that changed nothing on the cold path still moved it 3.3% (docs §7). Cold has
    /// no warmup by definition: every iteration empties the entry cache, which is the thing being
    /// measured.</summary>
    private const int Warmup = 1, Iterations = 10;
    private const int ColdIterations = 10;

    /// <summary>Frames to keep the clock running past the call - see <see cref="PerfRunner.TimeOp"/>.
    /// Three covers the Unity <c>Start</c> that fires each new preview object's <c>OnSpawn</c>
    /// plus the layout/canvas rebuild the new hierarchy triggers.</summary>
    private const int SettleFrames = 3;

    private const int IdleBaselineFrames = 30;

    // The screen and ModAssets are internal mod types; their members are public but the types are
    // not, so reach everything by reflection (same pattern as PerfRunner's import hotspots).
    private static readonly Assembly ModAsm = typeof(Blueprint).Assembly;
    private static readonly Type ScreenType = ModAsm.GetType("BlueprintsV2.UnityUI.BlueprintSelectionScreen")!;
    private static readonly Type ModAssetsType = ModAsm.GetType("BlueprintsV2.ModAssets")!;
    private static readonly Type FileHandlingType = ModAssetsType.GetNestedType("BlueprintFileHandling", BindingFlags.Public | BindingFlags.NonPublic)!;

    private static readonly MethodInfo ShowWindowMethod = ScreenType.GetMethod("ShowWindow", BindingFlags.Public | BindingFlags.Static)!;
    private static readonly FieldInfo InstanceField = ScreenType.GetField("Instance", BindingFlags.Public | BindingFlags.Static)!;
    private static readonly FieldInfo ShowingInfoPreviewField = ScreenType.GetField("ShowingInfoPreview", BindingFlags.Public | BindingFlags.Instance)!;
    private static readonly FieldInfo TargetBlueprintField = ScreenType.GetField("TargetBlueprint", BindingFlags.Public | BindingFlags.Instance)!;
    private static readonly FieldInfo BlueprintEntriesField = ScreenType.GetField("BlueprintEntries", BindingFlags.Public | BindingFlags.Instance)!;
    private static readonly FieldInfo SelectedBlueprintField = ModAssetsType.GetField("SelectedBlueprint", BindingFlags.Public | BindingFlags.Static)!;
    private static readonly FieldInfo SelectedFolderField = ModAssetsType.GetField("SelectedFolder", BindingFlags.Public | BindingFlags.Static)!;
    private static readonly FieldInfo RootFolderField = FileHandlingType.GetField("RootFolder", BindingFlags.Public | BindingFlags.Static)!;

    /// <summary>The close callback ShowWindow takes. Real callers re-select the blueprint / tear
    /// the tool down in here; a no-op keeps the measurement to the screen itself.</summary>
    private static readonly System.Action<Blueprint> NoOpClose = _ => { };

    public static IEnumerator Run(PerfReport report, HarnessLog log)
    {
        log.Line("selection-screen sweep");

        var rootFolder = (BlueprintFolder)RootFolderField.GetValue(null);
        if (rootFolder == null)
        {
            log.Line("  SKIP: BlueprintFileHandling.RootFolder is null (blueprints never loaded)");
            yield break;
        }

        // Everything below rewrites the shared library and the selection; put the real state back
        // even if a case throws, so any later harness work sees the world it expects.
        var savedLibrary = new List<Blueprint>(rootFolder.Blueprints);
        object? savedSelectedFolder = SelectedFolderField.GetValue(null);
        object? savedSelectedBlueprint = SelectedBlueprintField.GetValue(null);
        SelectedFolderField.SetValue(null, null);   // list the root folder, not whatever was open

        yield return MeasureIdleFrame(report, log);

        try
        {
            yield return RunLibrarySweep(report, log, rootFolder);
            yield return RunPreviewSweep(report, log, rootFolder);
        }
        finally
        {
            CloseScreen();
            ReplaceLibrary(rootFolder, savedLibrary);
            ClearEntryCache();
            SelectedFolderField.SetValue(null, savedSelectedFolder);
            SelectedBlueprintField.SetValue(null, savedSelectedBlueprint);
        }
    }

    /// <summary>
    /// The floor every <c>-settled</c> number sits on: <see cref="SettleFrames"/> frames of this
    /// paused colony with no harness work in them. Without it a settled figure is unreadable - ONI
    /// keeps rendering, so part of that span is the game, not the dialog.
    /// </summary>
    private static IEnumerator MeasureIdleFrame(PerfReport report, HarnessLog log)
    {
        var spans = new List<double>(IdleBaselineFrames);
        var alloc = new List<AllocSample>(IdleBaselineFrames);
        for (int i = 0; i < IdleBaselineFrames; i++)
        {
            // Doubles as the allocation noise floor: whatever the game allocates in these frames
            // by itself is the amount any other op's alloc delta has to beat to mean anything.
            var probe = AllocProbe.Begin();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int f = 0; f < SettleFrames; f++)
                yield return null;
            sw.Stop();
            spans.Add(sw.Elapsed.TotalMilliseconds);
            alloc.Add(probe.End());
        }
        PerfRunner.Record(report, log, "idle-frame", SettleFrames, IdleBaselineFrames, spans, AllocStats.From(alloc));
    }

    /// <summary>Library-size axis, preview suppressed. Cold runs its own iteration loop rather than
    /// going through <see cref="PerfRunner.TimeOp"/>: emptying the entry cache means destroying the
    /// entry GameObjects, and Unity only actions a <c>Destroy</c> at end of frame - done from a
    /// TimeOp <c>setup</c> (which runs immediately before the body) the corpses would still be in
    /// the hierarchy during the very open being timed.</summary>
    private static IEnumerator RunLibrarySweep(PerfReport report, HarnessLog log, BlueprintFolder rootFolder)
    {
        foreach (int l in LibrarySizes)
        {
            var library = BuildLibrary(l);
            ReplaceLibrary(rootFolder, library);
            var target = library[0];
            log.Line($"selection-screen library L={l}");

            var coldTimes = new List<double>(ColdIterations);
            var coldSettled = new List<double>(ColdIterations);
            var coldAlloc = new List<AllocSample>(ColdIterations);
            for (int i = 0; i < ColdIterations; i++)
            {
                CloseScreen();
                ClearEntryCache();
                yield return null;  // let the Destroys take effect before we time an open

                var probe = AllocProbe.Begin();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                OpenScreen(target, withPreview: false);
                coldTimes.Add(sw.Elapsed.TotalMilliseconds);
                // Closed with the synchronous timer, like TimeOp's - the settle frames below are
                // the game's own work, not this open's.
                coldAlloc.Add(probe.End());
                for (int f = 0; f < SettleFrames; f++)
                    yield return null;
                sw.Stop();
                coldSettled.Add(sw.Elapsed.TotalMilliseconds);
            }
            PerfRunner.Record(report, log, "open-list-cold", l, ColdIterations, coldTimes, AllocStats.From(coldAlloc));
            PerfRunner.Record(report, log, "open-list-cold-settled", l, ColdIterations, coldSettled);

            yield return PerfRunner.TimeOp(report, log, "open-list-warm", l, Warmup, Iterations,
                body: () => OpenScreen(target, withPreview: false),
                setup: CloseScreen,
                settleFrames: SettleFrames);
            CloseScreen();

            // The same open with the list cache defeated: re-adding the identical library rebuilds
            // it in the identical order but bumps BlueprintFolder.ContentRevision, which is what
            // the cache keys on. Keeps a number for the rebuild itself, and proves the cached op
            // above is fast because it skipped work rather than because listing broke.
            yield return PerfRunner.TimeOp(report, log, "open-list-warm-fresh", l, Warmup, Iterations,
                body: () => OpenScreen(target, withPreview: false),
                setup: () => { CloseScreen(); ReplaceLibrary(rootFolder, library); },
                settleFrames: SettleFrames);
            CloseScreen();
        }
    }

    /// <summary>Building-count axis, library fixed small. Both visualizer branches, because the
    /// screen picks between them per building and they are not remotely the same cost.</summary>
    private static IEnumerator RunPreviewSweep(PerfReport report, HarnessLog log, BlueprintFolder rootFolder)
    {
        foreach (string buildingId in new[] { "Tile", "Ladder" })
        {
            if (Assets.GetBuildingDef(buildingId) == null)
            {
                log.Line($"  SKIP preview sweep for {buildingId}: no such BuildingDef");
                continue;
            }
            string opName = "open-preview-" + buildingId.ToLowerInvariant();

            foreach (int n in PreviewSizes)
            {
                var library = BuildLibrary(LibraryForPreviewSweep);
                var target = SyntheticBlueprint.Build(n, buildingId, $"PerfPreview{buildingId}{n}");
                library.Add(target);
                ReplaceLibrary(rootFolder, library);
                log.Line($"selection-screen preview {buildingId} N={n}");

                // Reopening the same, unchanged blueprint - what a player does constantly, and what
                // BlueprintPreviewScreen's load cache is there to make free.
                yield return PerfRunner.TimeOp(report, log, opName, n, Warmup, Iterations,
                    body: () => OpenScreen(target, withPreview: true),
                    setup: CloseScreen,
                    settleFrames: SettleFrames);
                CloseScreen();
                yield return null;

                // And the drawing itself, with the cache deliberately defeated: CacheCost bumps
                // ContentRevision, which is exactly the signal the cache keys on. Keeps a number
                // for the unavoidable first draw of a blueprint, and proves the cached op above is
                // fast because it skipped work rather than because generation broke.
                yield return PerfRunner.TimeOp(report, log, opName + "-fresh", n, Warmup, Iterations,
                    body: () => OpenScreen(target, withPreview: true),
                    setup: () => { CloseScreen(); target.CacheCost(); },
                    settleFrames: SettleFrames);
                CloseScreen();
                yield return null;
            }
        }
    }

    private static List<Blueprint> BuildLibrary(int count)
    {
        var library = new List<Blueprint>(count);
        for (int i = 0; i < count; i++)
            library.Add(SyntheticBlueprint.Build(1, "Tile", $"PerfLib{i:D4}"));
        return library;
    }

    /// <summary>Swaps the root folder's contents wholesale. <c>deleteIfEmpty: false</c> because
    /// emptying a folder normally means the user deleted its last blueprint, and BlueprintFolder
    /// answers that by trying to remove the backing directory - not something a benchmark should do
    /// to the install's real blueprints folder.</summary>
    private static void ReplaceLibrary(BlueprintFolder rootFolder, List<Blueprint> library)
    {
        foreach (var existing in new List<Blueprint>(rootFolder.Blueprints))
            rootFolder.RemoveBlueprint(existing, deleteIfEmpty: false);
        foreach (var bp in library)
            rootFolder.AddBlueprint(bp);
    }

    private static void OpenScreen(Blueprint target, bool withPreview)
    {
        SelectedBlueprintField.SetValue(null, target);
        SetShowingInfoPreview(withPreview);
        ShowWindowMethod.Invoke(null, new object[] { NoOpClose, target, true });
    }

    private static void CloseScreen()
    {
        if (InstanceField.GetValue(null) is KScreen screen)
            screen.Show(false);
    }

    /// <summary>ShowWindow reads <c>ShowingInfoPreview</c> (via ClearUIState -> SetMaterialState)
    /// to decide whether this open draws a preview, so it has to be set on the instance before the
    /// call - which means there is nothing to set on the very first open, when the screen has yet
    /// to be instantiated. That open is preview-less either way, matching the real first open.</summary>
    private static void SetShowingInfoPreview(bool value)
    {
        object? instance = InstanceField.GetValue(null);
        if (instance != null)
            ShowingInfoPreviewField.SetValue(instance, value);
    }

    /// <summary>Destroys every cached <c>FileHierarchyEntry</c> and empties the cache, so the next
    /// open has to instantiate them again - the cold case. Also drops TargetBlueprint, which would
    /// otherwise keep hold of a blueprint about to leave the library.</summary>
    private static void ClearEntryCache()
    {
        object? instance = InstanceField.GetValue(null);
        if (instance == null)
            return;

        if (BlueprintEntriesField.GetValue(instance) is System.Collections.IDictionary entries)
        {
            foreach (object? entry in entries.Values)
                if (entry is Component c && c != null)
                    UnityEngine.Object.Destroy(c.gameObject);
            entries.Clear();
        }
        TargetBlueprintField.SetValue(instance, null);
    }
}
