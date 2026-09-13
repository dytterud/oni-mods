# Changelog

Notable changes to Save Profiler. Format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Added

- **First version.** Times the save pipeline and writes a report to
  `Documents/Klei/OxygenNotIncluded/save_profiler/`.

  Phases measured: `SaveLoader.Save` (the root), and nested inside it `Sim.Save`,
  `SaveManager.Save`, `Game.Save`, the inner `SaveLoader.Save(BinaryWriter)` overload, and
  `SaveLoader.CompressContents`. The compression phase also reports the uncompressed byte count,
  read straight off the argument the game hands the compressor, which gives the compression ratio
  without re-measuring anything.

  Per-object cost comes from `SaveLoadRoot.SaveWithoutTransform`. Byte counts are stream-position
  deltas rather than a second pass over the data: the format writes each component as
  `<type name><int32 length><data>`, backpatching the length by seeking, so the delta across one
  serialization *is* that component's on-disk size.

- **Opt-in per-component-type attribution**, off by default. It patches
  `KSerialization.Serializer.SerializeTypeless`, which fires once per component — hundreds of
  thousands of times in a mature colony — so it measurably slows the save and inflates the phase
  totals printed in the same report. The report records which mode produced it and states in place
  that the two modes are not comparable.

- **A loud unresolved-target section.** Every patch target is resolved by name and by widest
  overload rather than by a pinned parameter list, and anything that fails to bind is named in the
  log and at the top of the report. A row reading zero calls is otherwise indistinguishable from a
  code path that never ran, which `docs/perf-method.md` records as having gone unnoticed for two
  commits in the sibling mod.

  `PatchTargetResolutionTests` pins the shape of all eight targets against a real install, so a
  game update that moves one fails a test run rather than silently producing a report full of
  zeroes.

### Fixed

- **`unaccounted` reported 0.0 ms when it should have reported 1,092 ms.** The figure subtracted
  every recorded phase from the root, but the phases nest, so the sum exceeded the total and the
  `Math.Max(0, …)` guard clamped a real 28%-of-the-save gap to zero. Found by the first run against
  a real colony, which is the only place it could have been found — the unit tests asserted the
  clamp as intended behaviour, and for negatives it is.

  Phases now record the phase they ran inside, measured from a thread-local stack rather than
  declared from a table, so the report renders an actual tree with a **Self** column (total minus
  direct children) and computes `unaccounted` from direct children only. Measuring the nesting
  rather than declaring it also means a mod that reorders or re-parents the save path is described
  correctly instead of confidently mis-attributed.

### Known limits

- The phases are nested, so they do not partition the total. The remainder is published as
  `unaccounted` rather than distributed or hidden.
- A report is one run on one machine with one mod list, and carries no noise floor. Nothing here
  supports differencing two reports against each other.
- Nothing is optimised. This mod only measures.
