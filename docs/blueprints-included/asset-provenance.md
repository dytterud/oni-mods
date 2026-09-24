# Provenance — what came from upstream, and what may

This fork was taken from upstream **Blueprints Expanded** under MIT. What it may take from
upstream since then is a licensing question, not a housekeeping one. This page records where the
licence line is, the rule that follows from it, and the evidence for the one binary that did come
from upstream: the `blueprints_ui` UI bundles.

## The relicense cut

| | |
|---|---|
| Upstream MIT from | [`4a8c9a8`](https://github.com/Sgt-Imalas/Sgt_Imalas-Oni-Mods/commit/4a8c9a8) — 2024-07-08 |
| **Upstream relicensed to All Rights Reserved** | [`771622f`](https://github.com/Sgt-Imalas/Sgt_Imalas-Oni-Mods/commit/771622f) — **2026-09-07 21:57:24 UTC** |
| Upstream relicensed again, to the "Sgt_Imalas ModRepository License v1.0" (source-available, no compiled builds) | [`e57f92b`](https://github.com/Sgt-Imalas/Sgt_Imalas-Oni-Mods/commit/e57f92b) — 2026-09-24 09:24:01 UTC |
| This fork's initial commit | `c84e698` — 2026-09-07 17:08:22 UTC |

The fork predates the relicense by 4h49m. An MIT grant attaches to the version it was published
under and is not retractable, so everything upstream published **before 21:57:24Z on 2026-09-07**
may be used under MIT, including material pulled later. Everything published after it may not.
The second change does not move that line: its licence forbids compiled builds, which is what this
fork ships.

That is the only line that matters, and it is a wall-clock one — not "before the fork" and not
"before upstream's LICENSE commit appears in a `git log --since`". Two upstream commits land
inside the same afternoon on opposite sides of it.

## Where the bundles came from

Bundles are binaries, so provenance is decidable: a git blob SHA is a content hash, and two
repositories that hold the same bytes report the same SHA.

| Our commit | Date (UTC) | Identical to upstream | Upstream date (UTC) | |
|---|---|---|---|---|
| `b1e3175` | 09-07 17:25 | [`e856903`](https://github.com/Sgt-Imalas/Sgt_Imalas-Oni-Mods/commit/e856903) | 09-04 21:33 | MIT — shipped until the rebuild below |
| `895fa78` | 09-10 16:57 | [`063fb40`](https://github.com/Sgt-Imalas/Sgt_Imalas-Oni-Mods/commit/063fb40) | 09-07 22:47 | 50 minutes after the cut |
| `f3888d6` (#95) | 09-18 15:26 | [`cc28b8b`](https://github.com/Sgt-Imalas/Sgt_Imalas-Oni-Mods/commit/cc28b8b) | 09-16 16:07 | after the cut |

The two later pulls were reverted in #113. `b1e3175`'s blobs are `4345a5e` (linux), `5434116` (mac)
and `2335a6f` (windows), and they are what the rebuild's spec was extracted from. The bundles in the
tree are no longer those; see [Rebuilding them ourselves](#rebuilding-them-ourselves).

**`895fa78`'s commit message is wrong.** It says "Rebuild the UI bundles so Chinese text
renders"; nothing was rebuilt, and there is no Unity project in this repo that could have rebuilt
them. The bytes are upstream's.

## What the two pulls actually contained

Decompressed and diffed at the object level, not by file size:

- `b1e3175` → `895fa78` is **one property on one text element**. The folder dropdown's entry
  label went `"Overflow": 3` (Truncate) → `"Overflow": 0`. The `.resS` stream — every texture,
  every font atlas — is byte-identical, so there was never a font change here and reverting
  cannot regress glyph rendering. Truncate drops a *line* that does not fit its rect vertically,
  which is why CJK names came out empty where Latin ones did not; `Ellipsis` would have the same
  bug. `ModAssets.LoadAssets` now sets `Overflow` on the loaded prefab instead.
- `895fa78` → `f3888d6` adds the **GridSnap row** under `InfoItemsContainer` — the row, its
  `Checkbox`/`Checkmark`, a `Label`, and `WidthInput`/`HeightInput` with their `TextArea`,
  `Text` and `Placeholder` — plus five `…INFOITEMSCONTAINER.GRIDSNAP.*` text blocks.
  `CurrentBlueprintStateScreen.BuildGridSnapRow` now assembles that row from parts the pinned
  bundle already has.
- `f3888d6` also brought **`assets/uis/noteoptions.prefab`**, which nothing in this repo has ever
  referenced: a `UnityEngine.UI.Slider` and a checkbox for upstream's own note-opacity toggle,
  plus three sprites and 12 KiB of `.resS`. This fork's note opacity is a PLib config float. It
  is gone and nothing needs it back.

## The bundles contain no mod code

Every `MonoScript` the three bundles reference resolves to one of two assemblies:

    UnityEngine.UI      AspectRatioFitter, Button, ContentSizeFitter, GridLayoutGroup,
                        HorizontalLayoutGroup, Image, LayoutElement, Mask, Outline,
                        RectMask2D, ScrollRect, Scrollbar, Text, VerticalLayoutGroup
    Unity.TextMeshPro   TMP_FontAsset, TMP_InputField

No `BlueprintsV2`, no `UtilLibs`, no `Assembly-CSharp`, no Klei type. The prefabs are stock uGUI.
Mod behaviour is attached at runtime by name — `transform.Find("InfoItemsContainer/GridSnap")`
and friends — and labels are plain `UnityEngine.UI.Text` whose *text content is a JSON
descriptor* that `TMPConverter.ReplaceAllText` parses and swaps for a Klei `LocText`.

Two things follow. The bundles could be rebuilt in a stock Unity project with no ONI assemblies
at all, which is [#114](https://github.com/dytterud/oni-mods/issues/114). And the four `UtilLibs`
classes retained in [pruned `UtilLibs` files](../utillibs-pruned.md) for "prefab safety" are not
needed for that — there is no prefab script to break.

## Reproducing the analysis

Nothing here has to be taken on trust. The bundles are `UnityFS` archives holding **one
LZMA-compressed block**; the block table itself is LZ4. Decompress with Python — `lz4` and the
stdlib `lzma`, the latter fed a `FORMAT_ALONE` header with an unknown size (`b"\xff" * 8`),
because Unity stores the 5 props bytes without a length. That yields a serialized file plus a
`.resS`. The `MonoScript` entries are `(className, namespace, assemblyName)` string triplets; the
text elements are the JSON blobs above, keyed by their `Content` field.

## The rule

- **Behaviour and ideas, never expression.** From anything upstream published after 2026-09-07
  21:57:24 UTC, what may be taken is what a feature does or which bug a fix addresses. It may be
  learned from upstream's public history, its issues, its change notes, or by running the released
  mod. Code, strings, translations and art may not be taken, whether pasted, lightly edited or
  transliterated.
- **No binary crosses from upstream.** Ever, regardless of licence. A binary cannot be reviewed in a
  diff, its provenance is invisible in the tree, and "it matched upstream's blob exactly" is the
  thing to avoid rather than the thing to check for. The UI bundles are built here (below).
- **No decompiling.** Upstream's released DLLs may be run, never decompiled, disassembled or opened
  in a decompiler. In the EEA, where this fork is developed, the Software Directive allows
  decompiling only for interoperability (art. 6), while studying a program by running it is
  protected (art. 5(3)).
- **Clean-room.** An upstream change worth having becomes an `upstream-sync` issue that describes
  the behaviour in prose, with no upstream code and without upstream's internal structure. It is
  implemented from the issue alone, by someone who has not read upstream's code for that change;
  the PR template asks for this. The only shortcut is a fix too small to carry any expression,
  such as a single token.
- **Never replace a `UtilLibs` file wholesale from upstream.** A pruned file comes back from this
  repository's history ([pruned `UtilLibs` files](../utillibs-pruned.md)). `UtilMethods.cs`
  differs on purpose: upstream's copy carries string constants aimed at AI coding assistants,
  which this fork removed.

## Rebuilding them ourselves

Tracked as [#114](https://github.com/dytterud/oni-mods/issues/114). The Unity project is a
separate repository, [`dytterud/oni-blueprints-ui`](https://github.com/dytterud/oni-blueprints-ui);
its README and NOTICE carry the detail. In short:

- **The prefabs are a spec, not prefab files.** `spec/*.json` holds the serialized state of the
  five prefabs `ModAssets.LoadAssets` loads, extracted from `b1e3175`'s bundle, the MIT one above.
  A generic editor script rebuilds them field by field, and `tools/compare.py` diffs a built bundle
  against the spec. `blueprintInfoScreen`, which nothing loads, is left out.
- **The bundles carry 34 sprites**, not the handful #114 first estimated.
  - **12 are the game's own art.** The rebuilt bundle ships a same-sized white placeholder under
    each name, and `ModAssets.UseGameSprites` swaps the game's sprite in after
    `Assets.OnPrefabInit`, so no Klei art ships. The harness case
    `game-sprites-replace-the-bundle-art` asserts the swap. Against today's bundles it replaces the
    embedded copies with the originals. Three copies are pixel-identical to the game's; the rest
    differ only on anti-aliased edges (alpha at most 37/255), which is recompression, not different
    art.
  - **The other 22 are not in the game.** These are icons, rounded corners and scrollbar art. They
    are carried over under upstream's MIT grant; where they came from before BlueprintsV2 is not
    recorded.
- **The NotoSans font and TMP font asset are dropped.** `TMPConverter` already gives every label
  the game's fonts.

**The bundles in the tree are now built by that project**, with Unity 6000.3.5f2, the game's own
version:

| Platform | Blob |
|---|---|
| windows | `e5a2ec8` |
| mac | `e4965f3` |
| linux | `eb7b143` |

Check them with `git hash-object`. Its `tools/compare.py` reported all three identical to the
spec, and the in-game harness passed with them in place.

Each texture keeps the GPU format the source shipped: DXT5 for the large icons, uncompressed for
the small ones. `paste_0` is the one exception; it is uncompressed because its 359x447 size cannot
be DXT-compressed on its own. Texture memory is 3.8 MB, against the source's 4.5 MB. The files are
about 97 KB, down from 420-440 KB, because the NotoSans font and its atlas are gone. Neither
compresses well.

To ship a new build:
1. Copy `out/<platform>/blueprints_ui` over `ModAssets/assets/<platform>/blueprints_ui`.
2. Confirm all three blobs changed.
3. Run the harness.
4. Update the table above.

Doing it would let `BuildGridSnapRow` and the overflow fixup go back into the prefab, and would
take the last third-party art out of this repository.
