# Save Profiler

Measures where Oxygen Not Included's autosave time actually goes, and writes a report you can
read.

Late-game autosaves get slow. The game ships profilers for loading and for world generation
(`LoadProfiler`, `WorldGenProfiler`) but none for saving — `SaveLoader.Save` carries no
instrumentation at all. This mod adds it.

It changes nothing about how the game saves. It only measures.

## What you get

A Markdown file (and optionally a JSON twin) in

```
Documents/Klei/OxygenNotIncluded/save_profiler/
```

containing:

- **Total save time**, and the split across `Sim.Save`, `SaveManager.Save`, `Game.Save` and
  `SaveLoader.CompressContents` — the serialize-versus-compress question, which as far as we can
  tell nobody has measured before.
- **Uncompressed and compressed size**, and the ratio.
- **Per-object serialization cost**, from `SaveLoadRoot.SaveWithoutTransform`.
- **Per-component-type time and bytes**, when you turn attribution on — which types dominate, and
  how many bytes each writes.
- **The active mod list**, because another mod on the save path changes what was measured.

## Options

| Option | Default | Notes |
|---|---|---|
| Profile autosaves | on | |
| Profile manual saves | off | |
| Break down by component type | **off** | See below. |
| Also write JSON | on | |
| Reports to keep | 20 | 0 keeps everything. |

### About the component breakdown

It is off by default and that is deliberate. Turning it on wraps every component serialization —
hundreds of thousands of calls in a mature colony — which slows the save down and inflates the
phase totals **in the same report**.

Use it when you want to know which component types cost the most. Do not compare a report recorded
with it on against one recorded with it off. The report says which mode produced it and repeats
this warning in place.

## Reading the numbers

The phases are **nested inside** the total, not a partition of it, so they will not sum to 100%.
Whatever is left over appears as an `unaccounted` row — that row is information, not a rounding
error.

A single report is one run, on one machine, with one mod list, and has no noise floor to compare
against. Treat a figure as an order of magnitude, not something you can difference against another
run. If a row reads zero, check the "patch targets did not resolve" section first: a target that
failed to bind reads exactly like a code path that never ran.

## Related

- [`docs/save-profiler/reading-a-save-profile.md`](../../docs/save-profiler/reading-a-save-profile.md)
  — what each phase actually covers.
- [`docs/perf-method.md`](../../docs/perf-method.md) — the measurement rules this mod is built on.
