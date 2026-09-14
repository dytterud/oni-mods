# Fast Save, measured

What [Fast Save](https://steamcommunity.com/sharedfiles/filedetails/?id=1867707267) actually does to
autosave time on one colony, with its report trimming isolated from its other two features.

Baseline and method: [first-measurements.md](first-measurements.md) ·
[../perf-method.md](../perf-method.md).

## Configuration

| | |
|---|---|
| Colony | The Ramshackle Eclipse, cycle ~587, `cloud_save_files/` |
| Fast Save | **Background Save OFF, Save Optimization Moderate (20 cycles), Use Delegates OFF** |
| Other mods | BlueprintsIncluded, PeterHan.ModUpdateDate, SaveProfiler |
| Attribution | off |

Background Save is off deliberately: with it on, `SaveLoader.Save` returns before the work is
finished, so the root phase closes early and the tree stops describing anything. Delegates are off
because that is a separate question and the crash-prone one. **This measures trimming alone.**

## Result: −9.2% on the total, all of it in serialization

Comparing like position-in-session — 2nd-or-later autosaves only, since the first save of a session
behaves differently (see the baseline doc).

| | baseline (n=3) | Fast Save (n=2) | Δ |
|---|---:|---:|---:|
| **Total** | 3720.6 | **3377.5** | **−343 ms (−9.2%)** |
| serialize | 1967.7 | 1649.0 | −319 ms (−16.2%) |
| compress | 622.6 | 545.0 | −78 ms (−12.5%) |
| `Sim.Save` + unaccounted | ~1080 | ~1138 | unchanged |
| uncompressed | 65.3 MB | 59.9 MB | −8.3% |
| on disk | 5.4 MB | 4.2 MB | −22% |

The two Fast Save runs came in at **3376.7 and 3378.3 ms — 1.6 ms apart**, or ±0.05%. That is far
inside the ±0.73% floor measured for this position, so the difference from baseline is not in
question.

**All of the gain is serialization**, which is what the baseline predicted: trimming removes daily
report data, and serialization is the only phase that touches it. The compression saving is a
second-order consequence of the smaller file, not a separate effect.

Note the object count barely moved (21,529 vs 21,512 baseline). Trimming removes data *within*
objects, not objects, and still takes 16% off the serialization phase.

## The control: it was the deleted data, not the running mod

Turning Fast Save off leaves the colony trimmed — the reports are gone from the file and do not come
back. That makes a clean control available, and the prediction was stated before the run: if the
gain is less data, serialization should stay near 1650 ms with the mod disabled; if Fast Save's live
code path was doing the work, it should return to ~1968 ms.

| config | file | serialize (2nd+) | total (2nd+) |
|---|---|---:|---:|
| no Fast Save, untrimmed | 65.3 MB | 1967.7 (n=3) | 3720.6 (n=3) |
| Fast Save **on**, trimmed | 59.9 MB | 1649.0 (n=2) | 3377.5 (n=2) |
| Fast Save **off**, trimmed | 59.9 MB | 1654.0 (n=1) | 3385.3 (n=1) |

**The last two are indistinguishable** — 5 ms apart on serialization, 8 ms on the total, against a
±0.73% floor for that position.

So the −9.2% is the one-time data deletion, and **Fast Save's runtime behaviour contributes nothing
measurable per save** on this colony in this configuration. Its ongoing value is keeping the file
trimmed as reports re-accumulate, which is real but is a different claim from "the mod makes saving
faster". A player who ran it once and turned it off would keep the entire measured benefit until the
reports built back up.

Caveat: the Fast Save-off trimmed row is **n=1** at this position. It matches a pre-registered
prediction, which is worth more than a bare single run, but it is one run.

## The first save with Fast Save costs +607 ms

| run | position | total | serialize |
|---|---|---:|---:|
| baseline | 1st in session | 3863.4 | 2051.6 |
| **Fast Save A** | 1st ever with trimming | **4470.7** | **2707.6** |
| Fast Save B | steady state | 3376.7 | 1640.5 |
| Fast Save C | steady state | 3378.3 | 1657.4 |

A one-off, confirmed by B and C landing within 1.6 ms of each other afterwards. The likely cause is
the one-time discard of ~565 daily reports, since Moderate retains 20 and the colony had ~585 —
**inferred from the shape of the numbers, not measured.** Establishing it would mean instrumenting
Fast Save's own trimming, which this profiler does not do.

## WITHDRAWN: "the timelapse may swallow the gain"

Recorded here when the Fast Save runs were first written up: `Timelapser.RenderAndPrint` fired in two
of three Fast Save runs at 530–569 ms outside the save, against a ~344 ms saving inside it, raising
the possibility that the felt hitch would not improve — or would get worse. It was explicitly not
attributed to Fast Save at the time, because the outside-the-save bucket only existed for one
baseline run.

Six runs now have that bucket:

| run | Fast Save | `RenderAndPrint` |
|---|---|---:|
| 19:08 | off | — |
| 04:14 | on | — |
| 04:17 | on | 569.0 |
| 04:21 | on | 530.6 |
| 04:30 | off | — |
| 04:33 | **off** | **558.7** |

**Three of six, in both configurations.** The timelapse fires roughly every other cycle regardless of
Fast Save, so it is a constant applying to both sides of the comparison and does not offset the
saving. The concern is withdrawn.

What it leaves is a separate finding, and a more useful one: **the timelapse costs ~553 ms (530.6,
558.7, 569.0) on roughly half of all autosaves, entirely outside the save.** After serialization it
is the largest single item measured anywhere in this investigation — and unlike serialization it is
removable from the game's own settings.

## What this does and does not support

**Supported:** on this colony, Fast Save's trimming takes ~9% off autosave time and 22% off the file
on disk, entirely by serializing less report data, after a one-time penalty on the first save. The
benefit belongs to the deleted data, not to the mod running — with the mod disabled afterwards the
numbers are unchanged.

**Not supported:** anything about Fast Save's headline figure. Background Save and Use Delegates were
both off, and those are the features the larger claims rest on. Background Save in particular
shortens the freeze by moving work off the main thread rather than by doing less of it, and this
profiler cannot measure it without reporting a shorter total for the wrong reason.

**Not supported:** generalising past this colony, this cycle count, this machine or this save
location. A colony with fewer accumulated reports has less for trimming to remove.
