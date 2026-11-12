# Performance testing

Tools for measuring launch time, memory, the Mods list and the Apply plan, and for proving a change leaves them alone.
Run them on a copy of a data folder, never on one you use: some loading steps tidy records as they read them.

## Startup, headless

```
Arkive.App.exe --measure-startup OUT.json [--data-root DIR] [--forced-gc] [--wait-background] [--save-caches]
```

Runs the startup loading steps with the main window created but never shown, writes OUT.json and exits (0 on success,
1 on an error, 2 on bad options or the 60-minute limit, 3 if a window ever became visible). It skips what writes user
state or only matters on screen: the update offer, the progress window, clean-up jobs, settings and cache saves, the
package-build trim, the game-update notice and mod-file import; these appear in the results as skipped steps. Its
session log goes to `measure-logs` in the data folder. `--data-root` sets `ARKIVE_DATA_ROOT` for the run; without it the
usual data folder is measured. `--forced-gc` collects fully after each step, so the heap column is what Arkive keeps
(and steps take a little longer). `--wait-background` also runs and times the background pass that a normal launch
starts after Checked (step `background`, moment `background`). `--save-caches` saves the hash, outside-change,
working-file index, remembered Mods list and install-state memo caches as a normal launch does (step `cache-save`), so
repeated runs on a copy of a data folder measure what a user's next launch does; use it only with `--data-root` on a
copy. Without it the run writes nothing to the data folder but its log, so every run reads every working file and checks
every package project's install state at Checked, as a first launch after an upgrade does.

Launch has two moments, recorded under `moments` with what the whole process had read by then (`processSeconds` since
the process started, `startupSeconds` since the startup sequence began, `bytesRead`, `readOperations`,
`fileOperations`, `heapBytes`):

- `ready`: the Mods list is on screen, from the remembered list when there is a usable one (step `remembered-list`,
  note `shown`), otherwise the checked list.
- `checked`: the working files of enabled mods (from the working-file index, each checked unchanged or read again),
  the install state (from the install-state memo `cache\package-install-state.json` for each package project whose
  files are all unchanged, checked again for the rest) and the outside-change check are done and the verified list is
  shown (step `mods-list`). When some install states came from the memo, the background pass checks every package
  project again and corrects the list.

The session log has `Startup ready in N s`, `Startup checked in N s`, `Startup mods loaded in N s` and
`Startup loading took N s` (now Checked), `Working files checked for launch: source=index|full read, ...` and
`Mods list install state read: ..., remembered=N, checkedNow=N, notRemembered=N, memoFileChecks=N.`; a state the
background pass found different from what launch showed is logged as `Install state shown at launch corrected by the
background check: ...`.

Two more options look at the window after loading:

- `--dump-mods-list FILE` writes what the Mods tab holds: every row with each text and flag it shows, its assets, the
  overlap banner and the two summary lines. Sorted, with paths relative to the data folder and `<game>`, so a change
  that leaves the Mods list alone gives a byte-identical file. `--dump-at ready|checked|background` says when
  (default `checked`; `background` needs `--wait-background`).
- `--open-view NAME` (repeatable) opens a view the way its entry in the advanced menu does, waits until it has loaded
  (the first one also loads the game database, which launch no longer loads)
  and records a step `open-view:NAME` whose note gives the number of rows in each of the view's grids. Names: `exports`,
  `baselines`, `mesh`, `static-mesh`, `materials`, `packages`, `mod-sets`, `modified-meshes`, `update-audit`,
  `mod-dependencies`.

Normal launches write the same records to the session log as `Startup step: id=..., seconds=..., ...` lines.

## Engine benchmark

```
arkive launch-benchmark DATA-ROOT [--json OUT.json] [--forced-gc]
```

Times the engine loads startup performs (stores, mod library, working-file snapshot, package projects, install state,
Mods list status, game database, index status) in startup order, without the window. Read-only.

## Synthetic data folders

```
arkive launch-fixture NEW-FOLDER [--mesh-edits N] [--textures N] [--effects N] [--materials N] [--packages N]
                      [--builds-per-package N] [--mods N] [--enabled-share 0.3] [--bound-share 0.52] [--seed N]
```

Builds a data folder with dummy game packages under `game\`. Mesh edit records are about 443 KB, like real ones. Of the
working files, `--bound-share` belong to a mod and `--enabled-share` of those to an enabled mod (by default about 15 %
enabled, 37 % disabled, 48 % unbound). The same options give the same records in any folder. The smoke tests use the
same generator (`tests/Arkive.SmokeTests/LaunchFixture.cs`).

## Parity dumps

```
arkive arkive-list DATA-ROOT --json LIST.json
arkive arkive-plan DATA-ROOT --json PLAN.json
```

Sorted dumps of every mod and asset as the Mods list shows them (set-up state, modified, in game, conflict outcome,
binding) and of the Apply plan (every package, action, reason, contributors and set-ups). They hold no times; paths are
relative to the data folder and game paths start with `<game>`, so dumps of the same state are byte-identical and two
mirrors compare equal. Read-only: unlike plain `arkive-list`, the dump does not scan the mods folder for new files.

## Columns

| Column | Meaning |
| --- | --- |
| `seconds` | Wall time of the step. |
| `cpuSeconds` | Process CPU time during the step, all threads (can exceed `seconds`). |
| `bytesRead`, `readOperations`, `bytesWritten` | Process I/O counters during the step, file cache hits included. |
| `fileOperations` | Windows' count of the process's other I/O operations during the step: opening a file counts about two (open and close), looking at a file's size and time one, listing a folder a few. What decides the time with nothing cached, with `bytesRead`. |
| `heapBytes` | Managed heap after the step (with `--forced-gc`, after a full collection). |
| `uiBlockedSeconds`, `uiLongestBlockSeconds` | How long the window's thread could not answer (waits over 0.1 s), in total and the longest. |
| `skipped`, `note`, `error` | Not run in this mode and why; item counts; the step's exception. |
| `storeWalks` | How many times the step walked each store of working files or package projects (listed its folders and read or checked every record). |

Totals add the steps up; `seconds` there is the whole run, and the report's `storeWalks` adds up every step's walks.
Startup waits for `window`, the two caches, `library` (the mods folder scan and the mod library),
`remembered-list` (Ready) and `mods-list` (Checked: the enabled mods' working files from the working-file index, install
state from the package project records, the outside-change check, the Mods list and Edit mods' drafts). The game database
loads when a page that shows in-game names is first shown, and the advanced views read their data when first opened
(`--open-view` times that). The background pass (`--wait-background`) reads every working file once and refreshes the
index and the remembered list; after a launch that showed install states from the memo it also checks every package
project's install state again and refreshes the memo. The benchmark's `install-state` step uses
the same install-state service as the Mods list and the package view. `filesHashed` and `bytesHashed` count files hashed in full
because no saved hash matched.
