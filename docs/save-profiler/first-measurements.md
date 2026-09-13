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
| `Sim.Save` | 529.0 | 14.3 | **outlier — see the retraction below** |
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

**~571–1108 ms is unaccounted**, and it is *not* stable on its own — an early draft of this
document claimed it was, on two runs that agreed by coincidence; a third measured 1108.5 ms. What is
stable is `Sim.Save` + unaccounted taken together (see below). `PrepSaveFile` (0.2 ms) and
`SaveColonyPreview` (0.0 ms) are measured and ruled out, so by elimination it is the write of the
compressed buffer to disk. Its flatness across two very different invocations suggests a fixed cost
rather than anything proportional. **Untested and worth testing: this colony lives in
`cloud_save_files/`, so Steam Cloud may be in that path.** Profiling a local save of similar size
would settle it.

## Four runs, and the noise floor

All on the same colony, same mod list, Fast Save off. `unaccounted` is recomputed uniformly here as
root minus its direct children, so the first run (taken before that bug was fixed) is comparable.

| run | kind | total | serialize | compress | `Sim.Save` | unaccounted | Sim+unacc |
|---|---|---:|---:|---:|---:|---:|---:|
| 18:24 | auto | 3866.5 | 2076.2 | 627.4 | 10.3 | 1091.7 | **1102.0** |
| 18:36 | manual | 3265.5 | 2021.1 | 610.7 | 10.3 | 571.6 | **581.9** |
| 18:39 | auto | 3693.8 | 1953.8 | **529.0** | 529.0 | 570.7 | **1099.8** |
| 18:51 | auto | 3882.7 | 2073.0 | 628.0 | 12.1 | 1108.5 | **1120.5** |

**Noise floor, measured rather than assumed:** across three autosaves the total spans 3693.8–3882.7,
a range of 189 ms on a mean of 3814 — about **±2.5%**. Serialize is ±3%, compress ±2%. That is
tighter than the ±10% the sibling mod measured for its own sweeps, and it means a Fast Save
comparison needs to beat roughly 190 ms on the total to mean anything.

## RETRACTED, then partly reinstated: the `Sim.Save` finding

Worth reading as a sequence, because both mistakes are instructive.

**First claim (2 runs):** `Sim.Save` costs 529 ms on an autosave against 10.3 ms on a manual save —
a 50× difference, attributed to autosave-versus-manual.

**First retraction (3 runs):** a repeat autosave measured 12.1 ms, and the *first* autosave had
already measured 10.3 ms. Two of three autosaves were cheap, so 529 ms was written off as an
outlier and the whole finding was retracted.

**What a fifth run showed:** 528.1 ms. Reproducible to within 1 ms of the original 529.0, in a
different session.

| run | session | save # in session | kind | `Sim.Save` |
|---|---|---|---|---:|
| 18:24 | 1 | 1st | auto | 10.3 |
| 18:36 | 1 | 2nd | manual | 10.3 |
| 18:39 | 1 | 3rd | auto | **529.0** |
| 18:51 | 2 | 1st | auto | 12.1 |
| 18:54 | 2 | 2nd | auto | **528.1** |

**So the retraction was also wrong.** The numbers were never noise — they are bimodal, and the
distinguishing variable was one nobody had varied on purpose: how many saves had already happened
since the colony was loaded. Three cheap readings and two expensive ones looked like 3-versus-1
noise only because the runs were not labelled by it.

The lesson is narrower than "measure more". Both errors came from the same place: a variable that was
moving was not in the table. The first pass compared autosave-versus-manual because that was the
column that existed; the retraction called 529 an outlier because position-in-session was still not
a column. Counting runs does not help if the axis is missing.

**What can be claimed now:** the first save after loading a colony pays ~10 ms in `Sim.Save`; a later
autosave pays ~528 ms. The single manual save was also the 2nd save of its session, so "manual saves
never pay it" and "the first save after load never pays it" both fit all five runs and cannot be
separated without a manual save taken later in a session.

**What is unchanged by any of it:** `Sim.Save` + unaccounted, together, is ~1,080–1,120 ms on every
autosave (1102.0, 1099.8, 1120.5, 1078.3). The ~520 ms is always paid. Loading a colony only moves
where it lands — into `Sim.Save` on later saves, into the unmeasured remainder on the first.

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
