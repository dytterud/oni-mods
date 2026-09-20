# Manual smoke-test checklist

The mod has no automated end-to-end coverage — it needs a running game, and the
meaningful pipeline tests are `[RequiresGameInstall]` (see [test/README.md](../../test/README.md)).
Run this pass in-game after any change that touches blueprint data, the tools, the
visualizers, or the UI (e.g. every commit of the nullable / language-modernization work).

Automating this pass is possible but not yet built — see
[in-game-regression-testing.md](in-game-regression-testing.md).

Build a Debug mod (`-c Debug`, real install configured via `Directory.Build.props.user` —
see [README.md](../../README.md)), launch ONI, load or start a colony.

## 1. Create

- [ ] **Snapshot tool** — select an area with several buildings of different layers
  (foundation, wiring, plumbing, gas), a few gas/liquid tiles, and some natural ground.
  A blueprint is created and the create dialog opens.
- [ ] **Create-blueprint (drag) tool** — same, via the drag tool. Blueprint created.
- [ ] Filters, toggled in the tool parameter menu, are respected:
  - [ ] **Preserve air tiles** on → empty/gas cells inside the selection become dig commands.
  - [ ] **Collect notes** on → existing blueprint notes in the area are captured.
  - [ ] **Collect natural elements** on → the secondary element-state menu appears; natural
    tiles are captured with their element/mass/temperature.
  - [ ] **Planning Tool shapes** (only if the Planning Tool mod is loaded) → drawn shapes
    are captured; toggle is absent when that mod is not loaded.
  - [ ] Per-layer toggles (buildings / backwall / wires / gas / liquid / solid conduit /
    logic / dig) each include or exclude their layer.

## 2. Save & reload

- [ ] Name the blueprint, save. A `.blueprint` file appears under the blueprints folder
  (subfolder if a folder was chosen).
- [ ] Close and reopen the selection screen — the blueprint is listed with the correct
  name, dimensions, building count, dig count, note count.
- [ ] Restart the game, reopen — still loads, same counts, no errors in the log.

## 3. Place

- [ ] Place the blueprint on open ground — build orders appear for every building with the
  right positions and orientations; dig commands appear for the dig cells.
- [ ] **Place with settings** — disabled buildings stay unbuilt; per-building element
  selection, orientation, and conduit connections (wire/pipe/rail directions) match the
  source.
- [ ] Place over existing/!valid terrain — replacement visualizers show which buildings
  are blocked; no crash.
- [ ] Rotate the placement (all four orientations) — preview and resulting orders rotate.
- [ ] **Back-wall buildings** (needs the DLC content that has them, e.g. a shelf) - a blueprint
  holding a back wall plus something mounted on it previews green and places. The same building
  previews green over an existing back wall, including a buried one, and red where there is none.
- [ ] **Snap to Grid** — tick it in the blueprint state panel, then click and drag, with both the
  blueprint tool and the snapshot tool. Copies land edge to edge at the step shown, including on
  a fast drag. Rotate a quarter turn and drag again: copies still sit edge to edge. The row's
  label and both step fields read correctly.

## 4. Notes

- [ ] **Note tool** → create a **text note**: title, body, symbol, colour. Save the
  blueprint, reload, place — the note entity is restored with all fields.
- [ ] Create an **element note** (element, mass, temperature). Save, reload, place — fields
  restored.
- [ ] Edit a text note's side screen (text + symbol), confirm the change persists after
  save/reload.

## 5. Folders & metadata

- [ ] Create a folder; move a blueprint into it and back out.
- [ ] Rename a blueprint (renaming screen) — file is renamed, name in list updates.
- [ ] Rename a folder — contained blueprints still load.
- [ ] Delete a blueprint, delete a folder — files removed, no dangling list entries.
- [ ] Set a custom **icon** and **tint** (sprite selector) — persists after save/reload and
  shows in the list.

## 6. Multiplayer (only if you use ONI Together)

- [ ] Host places a blueprint → client sees the same build/dig orders.
- [ ] Host creates/updates/cancels a note → client stays in sync.
- [ ] Blueprint visualization (hover preview) syncs start/update/stop to the client.

## 7. Regression sweep

- [ ] Play at normal + 3× speed for a minute with a blueprint placed — no per-frame
  exceptions in the log.
- [ ] `Player.log` has no new `BlueprintsIncluded` warnings/errors versus a clean run.
