# Where the save-time investigation ended

Twenty-odd profiled autosaves on one late-game colony, starting from "autosave gets slow and nobody
measures it". This is the summary; the detail and the wrong turns are in the sibling documents.

[first-measurements.md](first-measurements.md) · [where-the-time-goes.md](where-the-time-goes.md) ·
[fast-save-comparison.md](fast-save-comparison.md) · [reading-a-save-profile.md](reading-a-save-profile.md)

## The answer

A late-game autosave (cycle ~590, 21,500 objects, 59.9 MB uncompressed → 4.2 MB on disk) costs
roughly **3,400 ms**, in four comparable parts rather than one dominant one:

| | ms | share |
|---|---:|---|
| serialization | ~1,500–1,650 | ~45% |
| `Sim.Save` + disk write (bimodal, ~600 or ~1,100) | ~600–1,180 | ~25% |
| compression | ~535 | ~16% |
| timelapse, outside the save, on ~half of cycles | 249–777 | on top |

**The save is not slow because the file is big. It is slow because it visits 419,000 things.** One
object holding 4 MB costs 47 ms; twenty-one thousand objects holding 0.49 MB between them cost
77 ms. The 41 MB sim grid writes in about twelve milliseconds. Per-byte throughput spans two orders
of magnitude, and everything expensive is on the many-small-things side.

That single fact explains most of what follows.

## What is worth doing

1. **Turn the timelapse off.** 249–777 ms on roughly half of autosaves, outside the save, removable
   from the game's own settings. No code. The largest available win, and it was found by accident
   while chasing something else.
2. **Run Fast Save once, then it is optional.** Its report trimming took ~344 ms and 22% off the
   file, but that is the one-time deletion of accumulated data — with the mod disabled afterwards
   the numbers are unchanged. Keep it on only to stop reports re-accumulating.
3. **Leave Use Delegates off.** ~155 ms (4.4%) against a documented failure mode of an unloadable
   save, closed won't-fix upstream.

Nothing on this list is a mod anyone still needs to write.

## What was ruled out, and by what

Five optimisation ideas were proposed during the investigation. **Three died on measurement**, which
is the main thing this profiler bought:

| idea | killed by |
|---|---|
| Steam Cloud in the write path | three local runs matched cloud's distribution |
| Hand-written serializers for top-N types | no hot type — top one is 9.8%, top ten 53% of 413 |
| Type-name caching | `WriteKleiString` is 792k calls for ~165 ms; a cache reaches under 50 ms |
| Dirty-tracking / skipping unchanged objects | no mutation signal — Harmony cannot hook field stores |
| Compression level | **still open**, ~535 ms, untouched |

Each of the first three looked sound in advance. Two of them were recommended in writing before
being measured.

## The limit of this instrument

The remaining ~705 ms of per-object overhead — 33 µs per object, outside any component — is real and
**not further decomposable with this tool**. Its candidate mechanisms are a `GetComponents<T>()`
allocation per object and roughly 1.7 million `Stream.Position` calls for the length backpatching.
One is a Unity generic, the other a BCL property; neither is cleanly patchable.

Harmony wraps method boundaries. What is left is *inside* a method. Going further needs a sampling
profiler attached to Mono — a different instrument, not a bigger version of this one.

Saying so is the honest end point. The alternative was to estimate those two mechanisms from call
counts and publish the estimate, which is the failure this whole exercise kept catching.

## Corrections, for calibration

Seven published claims needed correcting. They are recorded in place in the sibling documents rather
than edited out, because a reader who cannot see which conclusions failed has no way to weigh the
ones that remain.

| claim | correction |
|---|---|
| `unaccounted` = 0.0 ms | clamped a real 1,092 ms to zero |
| serialization tree looked complete | its largest row, 1,954 ms, was self-parented and invisible |
| `Sim.Save` 50× slower on autosaves | retracted as an outlier, then *reinstated* — it was bimodal on position-in-session |
| cycle-boundary work ruled out at 1.4 ms | that was the cost of the timelapse *not firing* |
| timelapse ~553 ms | range is 249–777 |
| delegates the "highest-ceiling optimization" | measured ceiling 4.4% |
| `Sim`+unaccounted "tightly stable" | bimodal; nearly produced a false positive on the storage test |

Two patterns account for nearly all of them:

**A variable that was moving was not in the table.** Position-in-session, and whether the timelapse
actually fired, each caused two wrong conclusions. More runs do not help when the axis is missing —
both times the axis came from the user watching the game, not from the data.

**Extrapolating a measurement past what it measured.** The delegate ceiling came from a real
benchmark of field-access speed, applied to a phase where field access turned out to be a thin
slice. Having a benchmark in hand made the over-reach easier to believe, not harder.

One methodology error is worth recording separately: the final run changed two variables at once — a
fix to the profiler's own timing and a new instrumentation point — which made the fix's effect
unmeasurable. Three earlier findings in this investigation were undone by exactly that class of
mistake before it was repeated.

## What the profiler is for now

It works, it is tested, and its targets are pinned against the shipped assemblies so a game update
fails a test rather than silently producing zeroes. Anyone asking "where does save time go" on a
different colony, a different mod list or a different machine can answer it in one autosave instead
of inferring it from the code.

That was the point. The optimisations it found were mostly settings, and the ones it killed were
mostly mine.
