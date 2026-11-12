# Performance testing

Tools for measuring launch time, memory, the Mods list and the Apply plan, and for proving a change leaves them alone.
Run them on a copy of a data folder, never on one you use: some loading steps tidy records as they read them.

## Startup, headless

```
Arkive.App.exe --measure-startup OUT.json [--data-root DIR] [--forced-gc] [--wait-background]
```

Runs the startup loading steps with the main window created but never shown, writes OUT.json and exits (0 on success,
1 on an error, 2 on bad options or the 60-minute limit, 3 if a window ever became visible). It skips what writes user
state or only matters on screen: the update offer, the progress window, clean-up jobs, settings and cache saves, the
package-build trim, the game-update notice and mod-file import; these appear in the results as skipped steps. Its
session log goes to `measure-logs` in the data folder. `--data-root` sets `ARKIVE_DATA_ROOT` for the run; without it the
usual data folder is measured. `--forced-gc` collects fully after each step, so the heap column is what Arkive keeps
(and steps take a little longer). `--wait-background` also times the restored Library search that startup leaves
running; without it that search is still let finish, untimed, before the process exits.

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
| `heapBytes` | Managed heap after the step (with `--forced-gc`, after a full collection). |
| `uiBlockedSeconds`, `uiLongestBlockSeconds` | How long the window's thread could not answer (waits over 0.1 s), in total and the longest. |
| `skipped`, `note`, `error` | Not run in this mode and why; item counts; the step's exception. |

Totals add the steps up; `seconds` there is the whole run. `filesHashed` and `bytesHashed` count files hashed in full
because no saved hash matched.
