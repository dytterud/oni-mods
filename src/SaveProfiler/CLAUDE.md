# CLAUDE.md — Save Profiler

A measuring instrument for ONI's save pipeline. It optimises nothing and must never try to —
its only job is to produce numbers someone else can act on. Read the repo-root
[CLAUDE.md](../../CLAUDE.md) too.

## The one rule

**Everything that computes or phrases a number lives in `Recording/` and touches no Klei or Unity
type.** The `Patches/` shims read a timestamp and a stream position and hand both over; they
contain no arithmetic.

This is not tidiness. The committed `lib/` assemblies are reference-only, so a type that touches
Klei cannot execute in a unit test — the same constraint that put
`BlueprintsIncluded/BlueprintData/BlueprintPaths.cs` where it is. Move a calculation into a patch
file and it becomes permanently untestable.

## Layout

| Path | What |
|------|------|
| `Recording/SampleAccumulator.cs` | counts, totals, min/max/median. Pure BCL. |
| `Recording/SaveProfileReport.cs` | aggregation and the Markdown/JSON wording. Pure BCL + Newtonsoft. |
| `Recording/SaveProfileRecorder.cs` | live state during one save. Pure BCL. |
| `Recording/ReportRotation.cs` | which old reports to delete. Pure BCL, plain strings. |
| `Recording/ReportWriter.cs` | **the only Klei-touching file in `Recording/`** — paths, colony name, mod list, disk I/O. |
| `Patches/PatchInstaller.cs` | target resolution + the unresolved-target tally. |
| `Patches/SavePhasePatches.cs` | the phase timings. |
| `Patches/ComponentSerializationPatches.cs` | per-object and per-component attribution. |

## Patch targets, and why they are resolved by name

Verified against the shipped assemblies (see `PatchTargetResolutionTests`):

```
SaveLoader.Save(string filename, bool isAutoSave, bool updateSavePointer) -> string   // root
  Sim.Save(BinaryWriter, int x, int y)
  SaveManager.Save(BinaryWriter)
  Game.Save(BinaryWriter)
  SaveLoader.Save(BinaryWriter)
  SaveLoader.CompressContents(BinaryWriter, byte[] uncompressed, int length)
SaveLoadRoot.SaveWithoutTransform(BinaryWriter)
KSerialization.Serializer.SerializeTypeless(object obj, BinaryWriter writer)
```

Patching is **manual**, not `[HarmonyPatch]`, and resolution is by **widest overload**, never a
pinned parameter list. An attribute has to pin a signature, and a pinned signature stops matching
the moment the game adds an optional parameter — at which point the row reads zero calls, which
looks exactly like a code path that never ran. `docs/perf-method.md` records that costing two
commits before anyone noticed. Anything that fails to bind is named in the log and in the report.

Arguments are injected **positionally** (`__0`, `__1`, …) because a parameter name is a private
detail a game update may rename without changing behaviour. A shape change makes Harmony throw at
patch time, which `TryPatch` converts into a named unresolved target — loud, not silent.

`PatchTargetResolutionTests` pins every one of these shapes. If it fails, the game moved; go look.

## Measurement discipline

Inherited from `docs/perf-method.md`, and the reasons are in there:

- **`Stopwatch.GetTimestamp()`, never `Stopwatch.StartNew()`.** A `Stopwatch` is a class, and a
  child's prefix runs inside its parent's measured window, so starting one charges the child's
  allocation to the parent.
- **`__state` is a readonly struct passed by `out`.** The wrapper must allocate nothing, and this
  matters most in `ComponentSerializationPatches`, which runs once per component.
- **Per-component attribution is opt-in and defaults off.** It adds a wrapper to hundreds of
  thousands of calls and inflates the phase totals in the same report. The report says which mode
  produced it and states that the two modes are not comparable. Do not quietly flip the default.
- **Phases are nested, not a partition.** They are not expected to sum to the total, and the
  remainder is published as `unaccounted` rather than normalised away.

## Gotchas

- **`DateTime` is not `System.DateTime` here.** `Assembly-CSharp` declares its own `DateTime` in
  the global namespace, which beats the implicit `using System` and has neither `UtcNow` nor a
  format-provider `ToString`. Spell out `System.DateTime`.
- **`SgtLogger.logError` takes one argument**, unlike `l` and `warning` which take an optional
  assembly override.
- **Nothing thrown from a postfix may escape.** These shims sit on the player's save path;
  `ReportWriter.WriteIfEnabled` swallows everything and logs instead.
- **`BinaryWriter.BaseStream.Position` can throw** if the stream is closed or non-seekable by the
  time a postfix runs. A missing byte count is worth losing; a thrown exception is not.
- **The byte counts come from the format**, not from a second pass: each component is written as
  `<type name><int32 length><data>` with the length backpatched by seeking, so a stream-position
  delta *is* the component's on-disk size.
- **`PatchInstaller` is `public` for the tests.** This project cannot use `InternalsVisibleTo` —
  PolySharp's internal polyfills collide with the real BCL types in the net8.0 test assembly.
