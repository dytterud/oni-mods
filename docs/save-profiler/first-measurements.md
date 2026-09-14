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
| Machine | one machine. Seven runs; noise floor measured at ±2.5% (below). |

## Autosave — 3,693.8 ms total

Self time, with nesting resolved. Rows sum to the total.

| Phase | Self ms | % | Notes |
|---|---:|---:|---|
| `SaveLoadRoot.SaveWithoutTransform` | 1953.8 | 52.9 | 21,522 calls, 56.7 MB written |
| `SaveLoader.CompressContents` | 601.5 | 16.3 | 65.3 MB → 5.4 MB |
| *unaccounted* | 570.7 | 15.5 | inside the root, outside every measured phase |
| `Sim.Save` | 529.0 | 14.3 | **position-dependent — see below** |
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

Later broken down further in [where-the-time-goes.md](where-the-time-goes.md): roughly half of it is
419,032 component serializations at ~2 µs each with no dominant type, and half is per-object
overhead outside any component.

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

## Seven runs, and the noise floor

All on the same colony, same mod list, Fast Save off. `unaccounted` is recomputed uniformly as root
minus its direct children, so the early runs (taken before that bug was fixed) are comparable.
"Pos" is the save's position within its session — the axis two earlier passes failed to record.

| run | sess | pos | kind | total | serialize | compress | `Sim.Save` | unacc | Sim+unacc | outside |
|---|---:|---:|---|---:|---:|---:|---:|---:|---:|---:|
| 18:24 | 1 | 1 | auto | 3866.5 | 2076.2 | 627.4 | 10.3 | 1091.7 | **1102.0** | — |
| 18:36 | 1 | 2 | manual | 3265.5 | 2021.1 | 610.7 | 10.3 | 571.6 | **581.9** | — |
| 18:39 | 1 | 3 | auto | 3693.8 | 1953.8 | 601.5 | **529.0** | 570.7 | **1099.8** | — |
| 18:51 | 2 | 1 | auto | 3882.7 | 2073.0 | 628.0 | 12.1 | 1108.5 | **1120.5** | — |
| 18:54 | 2 | 2 | auto | 3719.6 | 1968.7 | 627.2 | **528.1** | 550.2 | **1078.3** | — |
| 18:58 | 2 | 3 | auto | 3748.4 | 1980.7 | 639.0 | **522.4** | 560.5 | **1082.9** | — |
| 19:08 | 3 | 1 | auto | 3863.4 | 2051.6 | 623.0 | 12.5 | 1116.1 | **1128.6** | 1.4 |

**Noise floor, measured rather than assumed:** across six autosaves the total spans 3693.8–3882.7,
a range of 189 ms on a mean of 3796 — about **±2.5%**. Serialize is ±3%, compress ±2%. That is
tighter than the ±10% the sibling mod measured for its own sweeps, and it means a Fast Save
comparison needs to beat roughly 190 ms on the total to mean anything.

## RETRACTED, then partly reinstated: the `Sim.Save` finding

Worth reading as a sequence, because both mistakes are instructive.

**First claim (2 runs):** `Sim.Save` costs 529 ms on an autosave against 10.3 ms on a manual save —
a 50× difference, attributed to autosave-versus-manual.

**First retraction (3 runs):** a repeat autosave measured 12.1 ms, and the *first* autosave had
already measured 10.3 ms. Two of three autosaves were cheap, so 529 ms was written off as an
outlier and the whole finding was retracted.

**What further runs showed:** 528.1 ms, then 522.4 ms. Reproducible to within 7 ms of the original
529.0, across three separate sessions.

Sorted by position within the session, the readings stop looking noisy:

| position in session | kind | `Sim.Save` |
|---|---|---:|
| 1st | auto | 10.3, 12.1, 12.5 |
| 2nd | manual | 10.3 |
| 2nd and later | auto | 529.0, 528.1, 522.4 |

**So the retraction was also wrong.** The numbers were never noise — they are bimodal, and the
distinguishing variable was one nobody had varied on purpose: how many saves had already happened
since the colony was loaded. Three cheap readings against one expensive one looked like 3-versus-1
noise only because the runs were not labelled by it.

The lesson is narrower than "measure more". Both errors came from the same place: a variable that was
moving was not in the table. The first pass compared autosave-versus-manual because that was the
column that existed; the retraction called 529 an outlier because position-in-session was still not
a column. Counting runs does not help if the axis is missing.

**What can be claimed now**, and it fits all seven runs: the first autosave after loading a colony
pays ~10–12.5 ms in `Sim.Save`; every later autosave pays ~522–529 ms. The one manual save was cheap,
but it was also its session's second save, so "manual saves never pay it" is supported by a single
sample and should not be leaned on.

**What is unchanged by any of it:** `Sim.Save` + unaccounted, together, is 1,078–1,129 ms on every
autosave. **The ~520 ms is always paid — loading only moves where it lands**, into `Sim.Save` on
later saves and into the unmeasured remainder on the first. That reframes the question: this is one
wait whose position shifts with sim-thread state, not a cost that appears and disappears. Mechanism
still not established.

## PARTLY RULED OUT, then corrected: cycle-boundary work outside the save

The measured save (3.7–3.9 s) ran consistently below a wristwatch reading of ~4.2 s, and the
standing hypothesis — raised from watching the game, not from the code — was that a new cycle does
extra work outside `SaveLoader.Save` that the player feels as part of the same pause. Daily report
generation and the timelapse were the named candidates.

Measured, on run 19:08:

| Outside the save | Calls | ms |
|---|---:|---:|
| `ReportManager.OnNightTime` | 1 | 1.1 |
| `Timelapser.OnNewDay` | 2 | 0.3 |
| `Timelapser.SaveScreenshot` | **0** | — |
| `Timelapser.RenderAndPrint` | **0** | — |
| **total** | | **1.4** |

**1.4 ms**, and this was written up as the hypothesis being ruled out. `unresolvedTargets` was
empty, so the zeros were real calls-never-made rather than targets that failed to bind.

### Correction: the timelapse was not absent, it had not fired yet

Three later runs measured `Timelapser.RenderAndPrint` at **569.0 ms, 530.7 ms**, and zero — one
call, outside the save, in two runs out of three.

So the 1.4 ms reading was the cost of the timelapse **not firing**, reported as the cost of the
timelapse. Measuring something once while it happens to be idle and concluding it is cheap is the
same error as the two `Sim.Save` passes, in a different costume: the run was not labelled by whether
the thing under test actually ran.

What survives from the original measurement is narrower and still useful: **daily report generation
costs ~1.1 ms at cycle 585**, measured with `OnNightTime` genuinely executing. Whatever Fast Save's
report trimming buys, it is not that — and the
[Fast Save comparison](fast-save-comparison.md) bears that out, finding the gain entirely in
serialization rather than in report generation.

And the frequency, from six runs with the bucket active: **it fires on three of six cycle
boundaries, at 530.6 / 558.7 / 569.0 ms, with and without Fast Save alike.** Roughly every other
cycle, costing ~553 ms outside the save. That makes it the largest single item measured anywhere in
this investigation after serialization itself, and the only large one the game already has a setting
to switch off.

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

- Differencing runs that are closer than ~190 ms on the total, which is this setup's measured ±2.5%
  noise floor.
- Comparing any two runs without labelling each by its position within its session. Two separate
  passes drew wrong conclusions from exactly that omission.
- Generalising past this colony, this machine, this mod list, or this save location.
- Any claim about *why* `Sim.Save` behaves differently on an autosave.
