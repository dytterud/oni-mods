using System;
using System.Collections.Generic;
using System.Linq;

namespace BlueprintsV2.Harness;

/// <summary>Minimal assertion helpers - throw <see cref="HarnessAssertException"/> on failure.</summary>
internal static class Assert
{
    public static void True(bool condition, string what)
    {
        if (!condition)
            throw new HarnessAssertException($"expected true: {what}");
    }

    public static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new HarnessAssertException($"{what}: expected [{expected}], got [{actual}]");
    }
}

internal static class CollectionAssert
{
    /// <summary>Order-independent multiset equality.</summary>
    public static void SameItems<T>(IEnumerable<T> expected, IEnumerable<T> actual, string what)
    {
        var e = expected.OrderBy(x => x).ToList();
        var a = actual.OrderBy(x => x).ToList();
        if (e.Count != a.Count || e.Zip(a, (x, y) => EqualityComparer<T>.Default.Equals(x, y)).Any(ok => !ok))
            throw new HarnessAssertException($"{what}: expected [{string.Join(", ", e)}], got [{string.Join(", ", a)}]");
    }
}

internal sealed class HarnessAssertException : Exception
{
    public HarnessAssertException(string message) : base(message) { }
}
