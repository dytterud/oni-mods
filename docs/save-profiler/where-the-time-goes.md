# Where the time goes

The per-component-type breakdown of an autosave, and what it rules in and out. Run with
attribution on, which perturbs the phase totals — see the caveat at the end.

Baseline and method: [first-measurements.md](first-measurements.md) ·
[fast-save-comparison.md](fast-save-comparison.md) · [../perf-method.md](../perf-method.md).

## Conditions

The Ramshackle Eclipse, cycle ~590, 59.9 MB uncompressed (already trimmed by Fast Save), Fast Save
on in Moderate with delegates, attribution **on**.

## The shape of it

```
419,032 component serializations    19.5 per object    mean 1.88 µs    =  787 ms
21,485 objects, per-object overhead                                    ≈  705 ms
                                                        serialization  ≈ 1492 ms
```

413 distinct component types. Per-object overhead is derived: serialization measured 1,492 ms in
runs without attribution, and components account for 787 ms of it.

## Finding 1: there is no hot type

| type | calls | ms | % of component time | µs/call |
|---|---:|---:|---:|---:|
| `PrimaryElement` | 21,386 | 77.4 | 9.8 | 3.6 |
| `BuildingHP` | 13,825 | 47.9 | 6.1 | 3.5 |
| `ReportManager` | **1** | 47.2 | 6.0 | **47,238** |
| `Deconstructable` | 13,825 | 45.9 | 5.8 | 3.3 |
| `Repairable` | 13,006 | 40.4 | 5.1 | 3.1 |
| `Instance` | 21,299 | 40.1 | 5.1 | 1.9 |
| `Prioritizable` | 21,379 | 39.5 | 5.0 | 1.8 |
| `KPrefabID` | 21,485 | 33.4 | 4.2 | 1.6 |
| `Operational` | 7,267 | 24.8 | 3.2 | 3.4 |
| `AutoDisinfectable` | 13,821 | 20.6 | 2.6 | 1.5 |

Top type 9.8%, top ten 53%, across 413 types. **The cost is 419,032 calls at ~2 µs each, not any one
expensive thing.**

## Finding 2: the cost is per-call, not per-byte

| | bytes | time | throughput |
|---|---:|---:|---|
| `ReportManager` — 1 object | 4.02 MB | 47 ms | 85 MB/s |
| `PrimaryElement` — 21,386 objects | 0.49 MB | 77 ms | 6 MB/s |
| sim grid via `Sim.Save` | ~41 MB (estimated) | ~12 ms | ~3 GB/s |

Component data is **8.0 MB of the 59.9 MB file — 13.3%**. The rest is overwhelmingly sim grid,
written at memory-copy speed in about twelve milliseconds.

**The save is not slow because the file is big. It is slow because it visits 419,000 things.** A
single object holding 4 MB costs 47 ms; twenty-one thousand objects holding 0.49 MB between them
cost 77 ms. Two orders of magnitude apart per byte.

This is also why Fast Save's delegate mode only bought 9.5% of the phase
([the measurement](fast-save-comparison.md#use-delegates-155-ms-and-a-failed-prediction)): faster
field access does nothing about call count.

## Finding 3: framing may cost more than the data

Estimated, not measured. Each component is written as `<type name string><int32 length><data>`. At
roughly 23 bytes of name-plus-length per component, 419,032 components come to about **9.6 MB of
framing against 8.0 MB of actual component data.**

The profiler measures component bytes as a stream-position delta across `SerializeTypeless`, which
starts *after* the name and length are written — so the 8.0 MB figure excludes framing and the
comparison is sound in direction, if not in precision. Confirming it means measuring the write
position before the name rather than after, which is one small profiler change.

If it holds, it says something structural about ONI save sizes: the same ~413 type-name strings are
UTF-8 encoded and written 419,032 times per save.

## What this rules out

**Hand-written serializers for the top-N component types — withdrawn.** This was recommended twice
in this investigation, most recently as idea 4 in the standing list. With no type above 9.8% and the
top ten at 53%, ten hand-maintained serializers would attack 417 ms and realistically recover a
fraction of it, in exchange for a permanent maintenance burden and a correctness risk on every game
update. The bounded-set reasoning was sound; the distribution it assumed does not exist.

Measuring the distribution before writing the code cost one autosave.

## What this opens

The **~705 ms of per-object overhead** — 33 µs per object, outside any component — is the largest
item nobody has examined, Fast Save included. From `SaveLoadRoot.SaveWithoutTransform`'s IL, each
object pays one `GetComponents<T>()` array allocation, and each of its 19.5 components pays a
`WriteKleiString` of the type name plus four `Stream.Position` operations for the length backpatch.

Per save that is **419,032 UTF-8 encodings and roughly 1.7 million stream-position calls.** Three
mechanisms, none of which change the file format:

- cache the encoded bytes for each of the 413 distinct type names
- use Klei's non-allocating `GetComponents(List<T>)` overload
- track the write position arithmetically instead of round-tripping through `Stream.Position`

None is individually large — each is plausibly 40–90 ms — but they are additive, low-risk, and
unexplored. Their real size is unknown until measured, which is the point.

## Caveat: attribution perturbs this run

Per-component wrappers cost time inside the phase totals they sit in. This run's serialization read
1,970.8 ms against ~1,492 ms in comparable runs without attribution — roughly 478 ms of wrapper
across 419,032 calls.

The per-component figures themselves are measured inside the wrapper bracket and are not inflated by
it; the phase totals are. **Do not compare this run's totals against an unattributed run**, which is
what the report says in place and why the mode is off by default.
