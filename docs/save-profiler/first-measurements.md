# First measurements

The first real numbers from Save Profiler, recorded so later work has a starting point and so the
two bugs these runs exposed stay visible. Method rules: [../perf-method.md](../perf-method.md).

## Conditions

| | |
|---|---|
| Colony | The Ramshackle Eclipse, cycle ~585 |
| Save size | 65.3 MB uncompressed → 5.4 MB on disk (8.3%) |
| Location | `cloud_save_files/` — Steam Cloud, **not** a plain local save |
| Game | 744825 |
| Mods | BlueprintsIncluded, PeterHan.ModUpdateDate, SaveProfiler. **Fast Save disabled.** |
| Attribution | off |
| Machine | one machine, one run each. No noise floor established. |

## Autosave — 3,693.8 ms total

Self time, with nesting resolved. Rows sum to the total.

| Phase | Self ms | % | Notes |
|---|---:|---:|---|
| `SaveLoadRoot.SaveWithoutTransform` | 1953.8 | 52.9 | 21,522 calls, 56.7 MB written |
| `SaveLoader.CompressContents` | 601.5 | 16.3 | 65.3 MB → 5.4 MB |
| *unaccounted* | 570.7 | 15.5 | inside the root, outside every measured phase |
| `Sim.Save` | 529.0 | 14.3 | **see below** |
| `SaveManager.Save` | 28.3 | 0.8 | the loop around SaveWithoutTransform |
| `Game.Save` | 10.0 | 0.3 | |
| `SaveLoader.Save (inner)` | 0.4 | 0.0 | |
| `SaveLoader.PrepSaveFile` | 0.2 | 0.0 | |
| `Timelapser.SaveColonyPreview` | 0.0 | 0.0 | |
| **Total** | **3693.8** | **100** | |

Wall-clock by wristwatch: ~4.2 s. The ~500 ms difference is within watch-and-reaction error and is
not treated as a finding.

## What is established

**Per-object serialization is the cost: 52.9%.** 21,522 objects, median 0.072 ms each. This is the
`FieldInfo.GetValue` → `Helper.WriteValue` loop in `KSerialization.SerializationTemplate`. It is the
thing that grows with colony age, and it is the only target worth optimising first.

**Compression is 16.3%**, and the ratio is 8.3% — the data is extremely compressible. A cheaper
compression level would trade into this 601 ms, not into the 53%.

**~571 ms (15.5%) is unaccounted**, and it is *stable*: 571.6 ms on a manual save and 570.7 ms on an
autosave, from a run-to-run spread otherwise in the tens of ms. `PrepSaveFile` (0.2 ms) and
`SaveColonyPreview` (0.0 ms) are measured and ruled out, so by elimination it is the write of the
compressed buffer to disk. Its flatness across two very different invocations suggests a fixed cost
rather than anything proportional. **Untested and worth testing: this colony lives in
`cloud_save_files/`, so Steam Cloud may be in that path.** Profiling a local save of similar size
would settle it.

## The one surprise

**`Sim.Save` costs 529 ms on an autosave and 10.3 ms on a manual save — 50×**, and that difference
accounts for essentially the entire gap between the two (3,693.8 ms vs 3,265.5 ms, a 428 ms
difference against a 519 ms `Sim.Save` delta).

Everything else is within noise between the two runs: `CompressContents` 601.5 vs 610.7, unaccounted
570.7 vs 571.6, `SaveManager.Save` 1982.1 vs 2050.0.

**Mechanism not established.** The plausible story is that an autosave fires at a cycle boundary
while the sim thread is mid-frame, so `Sim.Save` blocks waiting for a safe point, whereas a manual
save happens from a menu with the sim already idle. That is a reading of the situation, not a
measurement, and docs/perf-method.md is explicit about what publishing those is worth. Testing it
means instrumenting the wait, not reasoning about it harder.

## Not measured

**The timelapse never appeared in any run.** `Timelapser.SaveScreenshot` bound successfully (the
gated test confirms the method exists) but recorded zero calls during the save window. `Render()` is
an `IEnumerator` coroutine, so the screenshot is almost certainly taken across later frames, outside
`SaveLoader.Save` entirely. Anything it costs is invisible to this profiler as built.

## Two bugs these runs found

Both were invisible to the unit tests and could only surface against a real colony.

**1. `unaccounted` reported 0.0 ms when the true figure was 1,092 ms.** It subtracted *every*
recorded phase from the root, but phases nest, so the sum (6,991 ms) exceeded the 3,867 ms total and
the `Math.Max(0, …)` guard clamped it. A quarter of the save sat behind a zero that looked like a
clean result. Fixed by measuring nesting and subtracting only direct children.

**2. `SaveLoadRoot.SaveWithoutTransform` recorded itself as its own parent and vanished.** It
re-enters itself; the innermost call finished first and claimed the frame below it — a copy of
itself. A self-parented row is unreachable from the root, so 1,954 ms across 21,522 calls — the
single largest item in the report — silently disappeared, and `SaveManager.Save` absorbed the time
as though it had no children. Fixed by parenting to the nearest ancestor that is not the phase
itself.

The second is the more instructive one: the report still looked entirely reasonable with its biggest
row missing.

## What these numbers do not license

- Differencing runs. No noise floor was established; the sibling mod measured ±10% with systematic
  drift, and nothing here is more careful than that.
- Generalising past this colony, this machine, this mod list, or this save location.
- Any claim about *why* `Sim.Save` behaves differently on an autosave.
