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

## The catch: the timelapse may swallow the gain

| run | save | outside the save | full cycle-boundary |
|---|---:|---:|---:|
| baseline 19:08 | 3863.4 | 1.4 | 3864.8 |
| Fast Save 04:17 | 3376.7 | **569.2** | 3945.9 |
| Fast Save 04:21 | 3378.3 | **530.7** | 3909.0 |

`Timelapser.RenderAndPrint` fired in two of three Fast Save runs, costing 530–569 ms outside
`SaveLoader.Save`. Fast Save saves ~344 ms inside the save; the timelapse costs ~550 ms outside it.
If it fires at the same rate either way, **the hitch the player feels may not improve at all.**

**This is not attributed to Fast Save, and should not be.** The outside-the-save bucket only exists
from run 19:08 onward, so the comparison is n=1 baseline against n=3 Fast Save. The timelapse
plausibly fires every N cycles and was simply due. Fast Save does ship a background-timelapser
feature, which makes it worth checking rather than assuming in either direction.

Deciding it needs baseline runs with the ambient bucket active: Fast Save off, several cycles, and a
count of how often `RenderAndPrint` fires and at what cost.

## What this does and does not support

**Supported:** on this colony, Fast Save's trimming alone takes ~9% off autosave time and 22% off
the file on disk, entirely by serializing less report data, after a one-time penalty on the first
save.

**Not supported:** anything about Fast Save's headline figure. Background Save and Use Delegates were
both off, and those are the features the larger claims rest on. Background Save in particular
shortens the freeze by moving work off the main thread rather than by doing less of it, and this
profiler cannot measure it without reporting a shorter total for the wrong reason.

**Not supported:** that Fast Save improves the felt hitch on this colony, given the timelapse
overlap above.

**Not supported:** generalising past this colony, this cycle count, this machine or this save
location. A colony with fewer accumulated reports has less for trimming to remove.
