using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace BlueprintsV2.Harness;

/// <summary>
/// Drives one harness run: load fixture -> wait for sim -> place fixture buildings -> run the
/// assertion cases or (in perf mode) the benchmarks -> write results -> quit. Lives on a
/// <see cref="UnityEngine.Object.DontDestroyOnLoad"/> GameObject so it survives the MainMenu -> game
/// scene transition.
/// </summary>
internal sealed class HarnessRunner : MonoBehaviour
{
    private const int SettleFrames = 30;
    private const float LoadTimeoutSeconds = 240f;

    private void Start() => StartCoroutine(Run());

    private IEnumerator Run()
    {
        var log = new HarnessLog(HarnessGate.LogPath);
        HarnessCases.Log = log;
        Screenshot.Reset();
        var results = new ResultSet();

        string fixturePath = HarnessGate.FixturePath;
        log.Line($"fixture save: {fixturePath}");

        if (!File.Exists(fixturePath))
        {
            results.Add(new CaseResult("bootstrap", false, $"fixture save not found at {fixturePath} - the launcher should have copied it there"));
            Finish(results, log);
            yield break;
        }

        // Let the MainMenu finish initializing before driving a load.
        float t = 0f;
        while (MainMenu.Instance == null && t < 30f)
        {
            t += Time.unscaledDeltaTime;
            yield return null;
        }
        yield return new WaitForSecondsRealtime(1.5f);
        log.Line($"MainMenu ready ({MainMenu.Instance != null}); calling LoadScreen.DoLoad");

        bool threw = false;
        try
        {
            LoadScreen.DoLoad(fixturePath);
        }
        catch (Exception e)
        {
            log.Line("LoadScreen.DoLoad threw: " + e);
            threw = true;
        }
        if (threw)
        {
            results.Add(new CaseResult("bootstrap", false, "LoadScreen.DoLoad threw (see harness.log)"));
            Finish(results, log);
            yield break;
        }

        // Wait for the colony to be live, with a hard deadline and progress logging.
        float waited = 0f, nextLog = 5f;
        while (Game.Instance == null || Game.Instance.IsLoading() || !Grid.IsInitialized())
        {
            waited += Time.unscaledDeltaTime;
            if (waited >= nextLog)
            {
                log.Line($"  waiting for colony {waited:F0}s (game={Game.Instance != null} " +
                         $"loading={(Game.Instance != null && Game.Instance.IsLoading())} grid={Grid.IsInitialized()})");
                nextLog += 5f;
            }
            if (waited > LoadTimeoutSeconds)
            {
                results.Add(new CaseResult("bootstrap", false,
                    $"colony not ready after {LoadTimeoutSeconds:F0}s (game={Game.Instance != null} grid={Grid.IsInitialized()})"));
                Finish(results, log);
                yield break;
            }
            yield return null;
        }

        log.Line($"colony live after {waited:F0}s; settling {SettleFrames} frames");
        for (int i = 0; i < SettleFrames; i++)
            yield return null;

        // --- place the fixture buildings ---
        try
        {
            var telepad = GameUtil.GetActiveTelepad();
            if (telepad == null)
                throw new HarnessAssertException("no active Printing Pod in the loaded colony");
            HarnessCases.AnchorCell = Grid.PosToCell(telepad);
            log.Line($"printing pod cell {HarnessCases.AnchorCell} {Grid.CellToXY(HarnessCases.AnchorCell)}");
            HarnessCases.PlacedPrefabIds = FixtureBuilder.PlaceAll(HarnessCases.AnchorCell, log);
            log.Line($"placed {HarnessCases.PlacedPrefabIds.Count} fixture buildings: {string.Join(", ", HarnessCases.PlacedPrefabIds)}");
        }
        catch (Exception e)
        {
            results.Add(new CaseResult("fixture-setup", false, e.ToString()));
            Finish(results, log);
            yield break;
        }

        for (int i = 0; i < SettleFrames; i++)
            yield return null;

        if (HarnessGate.Mode == HarnessMode.Perf)
        {
            log.Line("mode=perf; running benchmarks instead of assertion cases");
            yield return Perf.PerfRunner.Run(log);
            log.Flush();
            Application.Quit();
            yield break;
        }

        foreach (var testCase in HarnessCases.All)
        {
            log.Line($"running case: {testCase.Name}");
            Exception? err = null;
            IEnumerator? body = null;
            try { body = testCase.Body(); }
            catch (Exception e) { err = e; }

            ///Drive nested enumerators here rather than handing them to Unity. `yield return
            ///SomeHelper()` would make Unity own that helper's execution, and an exception thrown
            ///inside it then escapes this try/catch and kills this coroutine outright: no results
            ///file, and run-ingame.ps1 waits out its timeout with nothing to show for it. Keeping
            ///the whole call tree on one stack means a throw anywhere in a case fails *that case*
            ///and lets the rest of the suite finish.
            var stack = new Stack<IEnumerator>();
            if (body != null)
                stack.Push(body);

            while (err == null && stack.Count > 0)
            {
                var frame = stack.Peek();
                bool moved;
                try { moved = frame.MoveNext(); }
                catch (Exception e) { err = e; break; }

                if (!moved)
                {
                    stack.Pop();
                    continue;
                }

                ///a yielded IEnumerator is a nested helper - run it on this stack. Anything else
                ///(null, a YieldInstruction) is Unity's to interpret, so pass it up.
                if (frame.Current is IEnumerator nested)
                    stack.Push(nested);
                else
                    yield return frame.Current;
            }

            var result = err == null
                ? new CaseResult(testCase.Name, true, null)
                : new CaseResult(testCase.Name, false, err.ToString());
            log.Line($"  {(result.Passed ? "PASS" : "FAIL")}{(result.Message == null ? "" : " - " + result.Message)}");
            results.Add(result);
        }

        Finish(results, log);
    }

    private static void Finish(ResultSet results, HarnessLog log)
    {
        results.Add(ExceptionSweep.Evaluate());
        results.Add(ExceptionSweep.EvaluateUnattributed());
        try
        {
            JUnitWriter.Write(HarnessGate.ResultsPath, results);
            log.Line($"wrote {HarnessGate.ResultsPath} - {results.PassCount}/{results.Count} passed");
        }
        catch (Exception e)
        {
            log.Line("failed to write results: " + e);
        }
        log.Flush();
        Application.Quit();
    }
}

internal sealed class HarnessLog
{
    private readonly StreamWriter writer;

    public HarnessLog(string path)
    {
        writer = new StreamWriter(path, append: false) { AutoFlush = true };
        Line("=== BPI harness run " + SysDateTime.Now.ToString("O") + " ===");
    }

    public void Line(string text)
    {
        Debug.Log("[BPI-Harness] " + text);
        writer.WriteLine(text);
    }

    public void Flush() => writer.Flush();
}
