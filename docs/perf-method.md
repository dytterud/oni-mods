# Measuring performance in an ONI mod

Rules learned the hard way while tuning Blueprints Included, generalised because none of them are
specific to that mod. The worked example — every number, every retraction — is
[§7 of the in-game regression testing doc](blueprints-included/in-game-regression-testing.md#7-performance-measurement-separate-mode).
This file is the short version, for the next investigation.

The theme, if there is one: **almost every wrong conclusion in that section came from believing
something plausible instead of measuring it.** Four claims were published and later retracted. None
of them were careless — each was a reasonable reading of the code. That is exactly why the rules
below are about evidence rather than about being careful.

## Before you optimise anything

**Measure first, and measure the thing the player does.** Not the thing that is easy to time. A
per-frame redraw is the mod's only per-frame path; an import is a once-a-session action. They
deserve different amounts of attention, and for a long time only the once-a-session actions had
been profiled at all.

**Check the operation actually does its job.** A sweep once reported plausible timings while every
placement silently failed — the harness was trusting the stopwatch, not the output. Count what came
out (`captured X (expected N)`, `N Constructables created`) and assert it. A fast no-op is the
easiest possible performance win and the least useful.

**Know which condition shows the effect.** Refresh batching measured as −2% dragging across empty
space and 25–31% over a real base, because the expensive branch is only reached when cells are
occupied. A per-frame optimisation measured over empty terrain is measured in the one condition that
cannot show it working. Ask what has to be true for the code under test to do any work, then arrange
that.

## Getting a number you can trust

**The noise floor is ±10% between runs, with a systematic drift.** Two passes over an *identical*
build had pass 2 faster on 85% of ops, median −4.2%. Anything smaller than about ten percent from a
single before/after pair is weather. `test/aa-run.ps1` re-measures this floor; re-run it when the
machine, the game version, or the sweep's iteration counts change.

**To resolve anything smaller, interleave A and B inside one run.** A runtime switch flipped per
iteration, both arms reported separately. That cancels the between-launch drift by construction
instead of trying to subtract it. It costs a temporary toggle in production code — delete it in the
same change that records the numbers, and say in the docs that the figures cannot be reproduced
without rebuilding it.

**A Harmony timing wrapper costs about 0.2 µs.** Any hotspot reading near that is unmeasured, not
free. Instrumenting a method that runs once per visual per frame costs more than the method does and
will flatten exactly the improvement you are chasing — gate those behind an attribution mode, keep
them off for headline numbers, and never compare an instrumented run's medians with an
uninstrumented one's.

**Instrumentation perturbs allocation too.** The same frame read 2,448 KB normally and 1,316 KB
under attribution. Compare like with like.

**Watch for the harness measuring itself.** A `Stopwatch.StartNew()` in a timing prefix allocates,
and a child's prefix runs inside its parent's measured window — so every patched child charged its
wrapper's allocation to its parent. One method read 230 B/call, then 368 B/call once four more of
its children were patched, with no code change in between. That produced a published figure blaming
a string allocation in Klei's code that did not exist.

## Reading the result

**Never infer a mechanism from code shape.** §7 explained a real 25–31% win by pointing at an
expensive call in the refreshed path. Patching that call directly measured **0 invocations** — the
branch is unreachable for tile blueprints entirely. The number was right; the story was invented.
If you cannot point at a measurement, write "mechanism not established" and move on.

**A hotspot reading zero calls looks identical to a code path that never ran.** One pinned method
signature stopped matching when an optional parameter was added, and read zero for two commits
without anyone noticing. Resolve by widest-overload rather than a pinned parameter list where a
method might grow one, and print a loud end-of-run summary naming anything that failed to bind.

**Prefer a control in the same run over a comparison across runs.** The strongest number in the
whole investigation came from four data handlers timed side by side: two resolved components by
string at ~203 µs, two by type at ~1 µs, same loop, same call count, same wrapper. No cross-run
comparison, no noise floor to argue about.

**Check the arithmetic closes.** A fix saved ~547 ms of handler-loop time but ~1030 ms of total
operation time. The gap was a second, uninstrumented lookup elsewhere — worth about as much as the
measured one. When the parts do not add up to the whole, something is unaccounted for, and it is
occasionally the larger half.

## Writing it down

**Record retractions in place; do not edit them out.** A reader who cannot see which conclusions
were wrong has no way to calibrate the ones that remain. Every retraction in §7 stays, with what
was believed, what was measured, and why the two differed.

**Say what was not verified.** "Game-touching logic often has no seam" is an acceptable answer; a
silent gap is not. Both an untested branch and an unproven mechanism belong in the commit message
and the PR, not only in someone's memory.

**Absolute numbers are machine-specific and moment-specific.** They are valid on the machine that
produced them, in the harness configuration that produced them. Changing iteration counts changes
what the median lands on — one op moved from ~500 ms to ~310 ms purely because a first-iteration
outlier stopped dominating a 3-sample median. Figures from before such a change are not comparable
with figures after it, and the doc should say so where it matters.

## What this does not cover

There is **no committed baseline and no automatic regression detection**. Every figure here is one
machine, one run. Now that the obvious wins are gone, the risk has shifted from "is it fast enough"
to "will it silently get slower again" — a committed baseline plus a diff is the highest-value
remaining item, and it is not built.
