using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace BlueprintsV2.Harness;

/// <summary>
/// Subscribes to Unity's log callback for the whole run and fails if any Error / Exception frame
/// mentions the mod. Started as early as possible (from <see cref="HarnessMod.OnLoad"/>).
/// </summary>
internal static class ExceptionSweep
{
    private static readonly List<string> Offenders = new();
    private static readonly object Lock = new();
    private static bool started;

    private static readonly string[] Needles = { "BlueprintsV2", "BlueprintsIncluded" };

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
        if (!Needles.Any(n => blob.IndexOf(n, StringComparison.Ordinal) >= 0))
            return;

        lock (Lock)
        {
            if (Offenders.Count < 50)
                Offenders.Add($"[{type}] {condition}");
        }
    }

    public static CaseResult Evaluate()
    {
        Application.logMessageReceivedThreaded -= OnLog;
        lock (Lock)
        {
            return Offenders.Count == 0
                ? new CaseResult("exception-sweep", true, null)
                : new CaseResult("exception-sweep", false,
                    $"{Offenders.Count} mod-related error/exception log frame(s):\n" + string.Join("\n", Offenders));
        }
    }
}
