using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace BlueprintsV2.Harness;

/// <summary>
/// Subscribes to Unity's log callback for the whole run and fails if an Error / Exception frame
/// appears. Started as early as possible (from <see cref="HarnessMod.OnLoad"/>).
///
/// <para><b>Two buckets, because one was not enough.</b> This used to keep only frames whose text
/// or stack mentioned the mod, and drop everything else as somebody else's problem. That silently
/// excused the most valuable class of failure there is: the mod corrupts game state, and the
/// <i>game</i> throws about it later, from a stack with no mod frame in it. A run once logged 20
/// <c>NullReferenceException</c>s from
/// <c>SimpleInfoScreen.RefreshMovePanel -&gt; DetailScreenTab.Update</c> - the mod had destroyed a
/// building the info screen still pointed at - and this sweep reported a clean pass, because no
/// needle matched.</para>
///
/// <para>So attributed frames still fail as <c>exception-sweep</c>, and everything else now fails
/// as <c>exception-sweep-unattributed</c>. The second is deliberately a separate case: the run mod
/// list is controlled by <c>test/run-ingame.ps1</c>, so unattributed noise is rare, but when it does
/// appear it wants triaging rather than blaming on the change under test.</para>
/// </summary>
internal static class ExceptionSweep
{
    private static readonly List<string> Attributed = new();
    private static readonly List<string> Unattributed = new();
    private static readonly object Lock = new();
    private static bool started;

    private static readonly string[] Needles = { "BlueprintsV2", "BlueprintsIncluded" };

    /// <summary>
    /// Frames that a clean run produces anyway and that no change of ours can fix. Matched as
    /// substrings against "condition + stack".
    ///
    /// <para><b>Keep this empty unless a run proves otherwise.</b> Every entry is a hole in the
    /// gate, so an addition needs the frame quoted in the comment and a reason it cannot be the
    /// mod's doing.</para>
    /// </summary>
    private static readonly string[] KnownBenign = Array.Empty<string>();

    public static void Start()
    {
        if (started)
            return;
        started = true;
        Application.logMessageReceivedThreaded += OnLog;
    }

    private static void OnLog(string condition, string stackTrace, LogType type)
    {
        if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert)
            return;

        string blob = condition + "\n" + stackTrace;

        if (KnownBenign.Any(b => blob.IndexOf(b, StringComparison.Ordinal) >= 0))
            return;

        bool ours = Needles.Any(n => blob.IndexOf(n, StringComparison.Ordinal) >= 0);

        lock (Lock)
        {
            var bucket = ours ? Attributed : Unattributed;
            if (bucket.Count < 50)
                bucket.Add(Summarise(type, condition, stackTrace, includeStack: !ours));
        }
    }

    /// <summary>
    /// An unattributed frame is useless without a stack - the whole point is that the text does not
    /// say who caused it - so keep the first few stack lines for those. Attributed frames name the
    /// mod in the condition already.
    /// </summary>
    private static string Summarise(LogType type, string condition, string stackTrace, bool includeStack)
    {
        if (!includeStack || string.IsNullOrEmpty(stackTrace))
            return $"[{type}] {condition}";

        var all = stackTrace
            .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        ///The top frames of an NRE are engine plumbing - ThrowHelper, GetComponentFastPath,
        ///GetComponent[T] - and naming those three tells nobody anything. The frame worth reading is
        ///the first game or mod one underneath them, so drop the engine frames whenever doing so
        ///leaves something behind.
        var interesting = all
            .Where(l => l.IndexOf("UnityEngine.", StringComparison.Ordinal) < 0)
            .ToList();
        var frames = (interesting.Count > 0 ? interesting : all).Take(3);

        return $"[{type}] {condition} | " + string.Join(" <- ", frames.ToArray());
    }

    /// <summary>Mod-attributed frames. Kept as <c>exception-sweep</c> so existing runs compare.</summary>
    public static CaseResult Evaluate()
    {
        Application.logMessageReceivedThreaded -= OnLog;
        lock (Lock)
        {
            return Offenders("exception-sweep", Attributed, "mod-related");
        }
    }

    /// <summary>
    /// Frames with no mod name in them. Call after <see cref="Evaluate"/>; it does not unsubscribe
    /// again.
    /// </summary>
    public static CaseResult EvaluateUnattributed()
    {
        lock (Lock)
        {
            return Offenders("exception-sweep-unattributed", Unattributed,
                             "unattributed (no mod name in the frame - most likely game state this " +
                             "mod damaged, so triage before dismissing)");
        }
    }

    private static CaseResult Offenders(string name, List<string> frames, string what)
        => frames.Count == 0
            ? new CaseResult(name, true, null)
            : new CaseResult(name, false,
                $"{frames.Count} {what} error/exception log frame(s):\n" + string.Join("\n", frames.ToArray()));
}
