# Reading a save profile

What each row of a Save Profiler report actually covers, and what it does not license you to
conclude. Companion to [../perf-method.md](../perf-method.md), whose rules this mod is built on.

## The pipeline being measured

Verified by disassembling the shipped `Assembly-CSharp.dll` and `Assembly-CSharp-firstpass.dll`,
not inferred from names:

```
SaveLoader.Save(filename, isAutoSave, updateSavePointer) -> path      ROOT
├── Sim.Save(writer, x, y)                    sim/grid state: temperature, mass, element per cell
├── SaveManager.Save(writer)                  every saved GameObject, grouped by prefab
│   └── SaveLoadRoot.SaveWithoutTransform(writer)          once per object
│       └── KSerialization.Serializer.SerializeTypeless()  once per component  [opt-in]
├── Game.Save(writer)                         game-level state
├── SaveLoader.Save(writer)                   the inner overload
└── SaveLoader.CompressContents(writer, uncompressed, length)   zlib, via Ionic
```

`SaveManager.Save` is where the reflection cost lives. Underneath it,
`KSerialization.SerializationTemplate.SerializeData` is a flat loop of `FieldInfo.GetValue` →
`Helper.WriteValue` per field, then the same again for properties — one reflective read per field,
per object, per save. That is the thing that grows with colony age.

## Why the phases do not add up

**They are a tree, not a partition.** Everything above runs inside the root, and the inner
`SaveLoader.Save(BinaryWriter)` overload runs inside the outer one — so its time is counted in
both. Each phase records the phase it actually ran inside (measured from a thread-local stack, not
declared from a table), which makes two columns meaningful:

- **Self** — a phase's total with its *direct* children subtracted. This is the column to read.
- **unaccounted** — the root's total minus its *direct* children only: the part of the save that
  no phase accounts for.

The first real run got this wrong and it is worth knowing why. `UnaccountedMs` subtracted *every*
phase from the root rather than the direct children; because the tree double-counts downward, the
sum came to 6,991 ms against a 3,867 ms total, and the `Math.Max(0, …)` guard reported **0.0 ms**.
The real figure was 1,092 ms — 28% of the save, more than compression cost — sitting behind a
zero that looked like a clean result.

A large unaccounted share is a finding. §7 of the in-game regression testing doc records a fix that
saved ~547 ms of handler-loop time but ~1030 ms of total operation time; the gap was a second,
uninstrumented lookup worth about as much as the measured one. When the parts do not add up to the
whole, something is unaccounted for, and it is occasionally the larger half.

## Where the byte counts come from

Not a second pass. `SaveLoadRoot.SaveWithoutTransform` writes every component as

```
<Klei string: type name><int32: byte length><data…>
```

backpatching the length by seeking back over it once the data is written. So the stream-position
delta across one serialization *is* that component's on-disk size, free.

Two consequences. First, byte attribution costs nothing beyond the timing wrapper. Second — worth
knowing if you ever go further than measuring — the format frames each component independently, so
a cached blob could in principle be spliced back in without fixing up any global offsets.

## The "outside the save" section

The save is not the whole hitch. A new cycle also builds the daily report
(`ReportManager.OnNightTime`) and takes the timelapse screenshot (`Timelapser.OnNewDay` →
`RenderAndPrint`), and none of that runs inside `SaveLoader.Save` — so none of it is in any phase
total, while the player feels all of it as one pause.

Those are measured separately and reported under their own heading, never added to the save's
totals. **Read the window carefully: that section covers everything measured since the previous
report, not this save.** Some of it runs before the save and some after — the timelapse is a
coroutine, so its frames land once the save has already returned and the report has been written.
With an autosave every cycle, the window is the previous cycle boundary.

**A consequence worth knowing: a setting change shows up one report late.** Turning the timelapse
off mid-cycle still leaves its cost in the *next* report, because that report covers the window the
setting was changed during. A single non-zero reading after switching something off is not evidence
the switch failed — the report after it is.

`Timelapser.Render()` is deliberately not patched: it returns an `IEnumerator`, so a prefix/postfix
pair would time the construction of the coroutine — microseconds — and present that as the cost of
the screenshot. A wrapper that measures the wrong thing is worse than none, because it produces a
number. `RenderAndPrint` is patched instead.

## The attribution mode

Off by default. On, it wraps `SerializeTypeless`, which fires once per component: a mature colony
means hundreds of thousands of wrapped calls, and a Harmony wrapper costs roughly 0.2 µs.

That cost lands **inside the phase totals in the same report**. An attributed run's
`SaveManager.Save` figure is therefore larger than the same colony's unattributed one, for reasons
that have nothing to do with the game. The report states this in place. Never difference an
attributed report against an unattributed one — this is the same rule the sibling mod's harness
enforces with its `perf-attribution` sentinel, and it exists because the mistake is easy and
invisible.

Nested serializations land in a `«nested serializations»` row rather than being folded into their
parent type, since a nested call runs inside its parent's measured window and counting both would
double-count the inner time.

## What a report does not license

- **Differencing two runs.** One run, one machine, one mod list, no noise floor. The sibling mod
  measured its floor at ±10% between runs *with a systematic drift* — two passes over an identical
  build had pass 2 faster on 85% of operations. Anything smaller than about ten percent from a
  single before/after pair is weather.
- **A mechanism.** The report says where time went, not why. §7 explains a real 25–31% win by
  pointing at an expensive call that direct measurement then showed was invoked **zero** times —
  the number was right and the story was invented. If you cannot point at a measurement, write
  "mechanism not established".
- **Comparing against someone else's numbers.** Absolute figures are machine- and
  moment-specific.

## When a row reads zero

Check the "patch targets did not resolve" section at the top first. A target that failed to bind
records zero calls, which is indistinguishable from a code path that never ran. That exact
confusion cost two commits in the sibling mod when a pinned method signature stopped matching after
an optional parameter was added.

If the section is absent, every target bound and a zero is real.

## Measured elsewhere

- [first-measurements.md](first-measurements.md) — the baseline, the noise floor, and the wrong
  turns taken getting there.
- [fast-save-comparison.md](fast-save-comparison.md) — Fast Save's report trimming and delegates,
  isolated and measured.
- [where-the-time-goes.md](where-the-time-goes.md) — the per-component-type breakdown: no hot type,
  419,032 calls, and why the cost is per-call rather than per-byte.

## If another mod is on the save path

The report lists the active mods for this reason. **Fast Save** in particular replaces the
serialization being attributed here — its delegate mode swaps out the reflection entirely, and its
background-save mode moves work off the main thread. A profile taken with it installed describes
Fast Save, not the base game.
