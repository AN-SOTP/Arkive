# Arkive

**A Windows mod workbench for the Western/global Steam version of Lost Ark.**

Arkive finds game assets, exports editable working files, validates changes, rebuilds protected Unreal Engine 3 packages, and applies or restores mods with hash-verified backups. Mods are shared as portable `.arkive` files. The project includes a WPF desktop application, a command-line interface and a reusable core library.

Arkive is an **AGPL-3.0-only public preview**; the current version is on the [Releases](https://github.com/AN-SOTP/Arkive/releases/latest) page. This repository contains current source, tests, build infrastructure, dependency notices, GUI screenshots and application artwork/icon. No game packages, exported game assets, databases, mods, personal settings, credentials or historical development checkouts are distributed.

**Using Arkive always carries inherent risk. Creating, sharing or using mods may result in account suspension or a permanent ban.** There is no guarantee against crashes, client repairs, incompatibility, data loss or account sanctions. Use a client you are authorized to access and keep independent backups.

Mods contain or are derived from Lost Ark game data owned by its rights holders (Smilegate and Amazon Games), which Arkive does not license to anyone. Selling access to that data is commercial use of someone else's property, and anyone who does so carries that risk alone. Paywalled mods are not welcome in this project's issues, discussions or releases, and I request that no modifications made via Arkive are paywalled in any way, and are made publically
available.

Arkive operates on files **while the game, launcher and anti-cheat are closed**. It does not inject code, attach to the game process, disable anti-cheat or provide runtime hot-swapping. Closing those processes and passing Arkive's validation do not eliminate the risks above.

## Screenshots

The actual desktop GUI. The library and mod screenshots use fictional demonstration data; the model viewer screenshots show game models rendered from a locally installed client. None of them are installable mods, and the repository contains no game files or personal library data.

### Asset library

Search technical filenames and in-game names, then inspect an asset or its relationships.

![Arkive asset library with demonstration search results](docs/screenshots/asset-library.png)

### Edit mods

Keep a named draft, edit its working assets and add individual assets or a mesh's complete dependency kit.

![Arkive mod editor with demonstration working assets and asset search](docs/screenshots/edit-mods.png)

### Mod library

Enable mods, set priorities and review the effective asset selection before applying changes.

![Arkive mod library with demonstration priorities and asset details](docs/screenshots/mod-library.png)

### Model viewer

Every LOD from Arkive's own decoder, coloured by the game's own compiled shaders, including glow and effect materials.

![Arkive model viewer showing Thaemine, Conqueror of Stars, with the game's shaders and blue glow](docs/screenshots/model-viewer-thaemine.png)

**Preview with edits** shows a mod's working files on top of the original asset and refreshes when they are saved (below shows a recoloured wing texture for Kazeros and changed hair colour for Serca).

![Arkive model viewer showing Death Incarnate Kazeros with glowing effect wings](docs/screenshots/model-viewer-kazeros.png)

![Arkive model viewer showing a mesh with a mod's edited material values and texture](docs/screenshots/model-viewer-edits.png)

### Live in game example

Mods made with Arkive, running in Lost Ark.

**Kazeros 2-3 Cutscene**

https://github.com/user-attachments/assets/1f6c9379-cdef-4ee6-ab5f-9dbd0af5e39a

**Character creator**

https://github.com/user-attachments/assets/d1f532b4-41b6-44d6-9351-42392d6c7d74

**Echidna Raid Cutscenes**

https://github.com/user-attachments/assets/81b639c8-ce7a-41b1-869f-009deb6645c7

## Contents

- [Install and first run](#install-and-first-run)
- [GUI workflow](#gui-workflow)
- [Game names and metadata](#game-names-and-metadata)
- [CLI workflow](#cli-workflow)
- [How it works](#how-it-works)
- [Game updates and recovery](#game-updates-and-recovery)
- [Application updates](#application-updates)
- [Build and release](#build-and-release)
- [Limitations and known issues](#limitations-and-known-issues)
- [Privacy, security and licensing](#privacy-security-and-licensing)

## Install and first run

### Requirements

- Windows 11 is recommended, 64-bit x64-compatible. The installer allows Windows 10 1809+, but .NET/OS support depends on edition and lifecycle; see [Microsoft's supported OS list](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md). Older Windows and Windows on ARM are not verified Arkive targets.
- A Western/global Steam Lost Ark installation; write access to its Packages folder when applying/restoring mods.
- Space for exports, decoded packages, staged builds and original backups. A large kit can use several times its source package size.

### Installer

1. Download the latest `Arkive-Setup-X.Y.Z-win-x64.exe` from [Releases](https://github.com/AN-SOTP/Arkive/releases/latest). Optionally compare `Get-FileHash -Algorithm SHA256 PATH` with the release's `.sha256` file.
2. Close running Arkive instances, run the installer and choose destination/shortcut options.
3. In **Asset library → Library setup**, select your client's `EFGame\ReleasePC\Packages` directory and choose **Build / update index**. Source packages are read, never renamed.
4. Choose **Build game DB**, select `EFGame\data2.lpk`, and wait for the mapping to load. Technical and in-game-name searches are now available.

The per-user installer normally needs no administrator access. Its default destination is `%LOCALAPPDATA%\Programs\Arkive`. It includes the .NET runtime, native Crunch decoder bridge, the exact tested Lost Ark UModel build, SDL libraries, pinned Crunch encoder, and the database builder's managed cryptography/SQLite libraries and native SQLite runtime. Database building, editing and deployment need no separate .NET, Visual Studio, Python or QuickBMS install.

Dependencies are installed privately alongside Arkive, not as machine-wide prerequisites. The installer does not need to detect or download existing global runtimes: the tested versions are always provided, including on an offline machine. Upgrades replace the application's own dependency files and preserve its separate data folder. Building from source still requires the developer tools listed under **Build and release**.

The preview installer is **not Authenticode-signed**. Windows may show an unknown-publisher/SmartScreen warning. Verify trusted source and hashes; hashes are not publisher signatures.

### Data and existing libraries

Installed Arkive keeps `mods`, `staging`, `cache`, `backups`, `logs` and downloaded `updates` under `%LOCALAPPDATA%\Arkive`. Application upgrades/uninstall preserve this folder. Uninstall does not restore the game: restore active mods first.

Source/portable builds retain project-root behavior. Set `ARKIVE_DATA_ROOT` to use an existing library or another drive. This does not automatically move another checkout's data:

```powershell
$env:ARKIVE_DATA_ROOT = 'E:\ArkiveData'
& "$env:LOCALAPPDATA\Programs\Arkive\Arkive.App.exe"
```

Use a trusted local folder, not the game's Packages directory. Avoid simultaneous instances against one data root. Back up the whole data folder for migration: shared `.arkive` files are portable, but drafts, source evidence and restore receipts are separate.

## GUI workflow

The four primary tabs separate discovery, creation, editing and deployment. **Advanced** retains native diagnostics and specialist/legacy workflows.

### Asset library

Search **All names and filenames** by physical/decoded package filename, object, class or loaded in-game name. The full index includes assets even when their class is not moddable. Inspect a selected object in UModel, browse its package, open it in Arkive, export it, copy its identity or explore evidence-labelled relationships. UModel and Arkive may support different display types. Direct database links, verified dependencies and candidate mappings are deliberately distinguished.

**Preview** (or double-clicking a mesh) opens Arkive's model viewer. It shows every LOD of skeletal and static meshes decoded by Arkive's own parser, with UModel's materials, textures and object properties alongside: LOD and UV-set switching, wireframe, normals, tangents, skeleton, bone names, sockets, UV layout, material IDs, vertex colours, section isolation, texture channels, screenshots and PgUp/PgDn browsing. Objects UModel cannot read still show their geometry. By default each material runs **the game's own compiled shader**: the exact permutation the material uses, translated from the client's shader cache, so packed textures (for example two-tone hair masks) appear in their game colours, glow shows, and effect materials are drawn as the light they emit. Per-family colour recipes and the stored textures, as UModel shows them, remain available for comparison. **Export colours** saves each material's colours, as shown, as textures laid out on UV set 0. The first preview indexes the game's shaders once (about 30 seconds, about 8 MB in the data folder); prepared scenes are cached up to 1 GB. The viewer follows the files it reads: when the object's package or any package its materials and textures come from changes (for example when a mod is applied or restored), the open view refreshes and keeps the camera. In mod authoring, **Preview with edits** shows a mesh from its clean package with the mod's edited GLB, texture images and material values on top, and refreshes whenever one of those working files is saved; **Your edits** (E) switches back to the original. The viewer needs the Microsoft Edge WebView2 Runtime, which ships with Windows 10 and 11.

The local index is resumable. Worker count and optional fast scratch storage help indexing. The setup panel collapses after configuration; refresh the index when the client changes.

### Create

1. In **Create mod**, enter a name and optional version/author/description. **Create mod** saves a draft and opens **Edit mods**; no game files change.
2. Expand **Add assets**, search by game/object name and select results. **Add asset only** prepares one editable GLB, PNG/DDS or material project. **Add mesh + textures/materials** prepares a skeletal mesh and its exact resolved dependencies as a structured kit. References start excluded; tick **Include** only for assets your mod should replace.
3. Use **Preview original** to inspect sources in UModel. Extraction shows stage, elapsed time and dependency counts, with cancellation at safe checkpoints. Original previews remain available during extraction; editing/building waits until completion.
4. Select a working asset and use **Open edited file**, **Working folder**, **Replace file**, **Compare with original** or **Edit material**. Edit the working file displayed in the table. Export meshes back as GLB while retaining the target contract.
5. **Save mod** validates included edits and writes a `.arkive` file. New mods start disabled; saving is not installation.
6. **Preview changes** shows the hypothetical enabled result without changing files. **Save and apply** saves/enables, reviews the actual plan and applies after confirmation. You can also enable later in **Mods**.
7. **Share** saves and validates before copying the portable file to your chosen destination.

### Edit

Choose **Mods → Edit mod**, or use the **Edit mods** selector. Missing working copies are prepared against a verified clean client, using verified backups if Arkive has an active package replacement. Existing working edits are retained. Edit, save, then preview/apply the new composition with the game closed.

Ctrl-click toggles rows, Shift-click selects a range and Ctrl+A selects every row in the focused table. **Remove selected** affects all selected assets; single-file edit/compare/preview needs one row. Clearing a draft preserves working files and the previous saved archive. An empty draft cannot become a valid shared mod.

Drag dividers to resize working/search tables and mod/detail panes. Collapse Add assets, metadata or library setup when unnecessary. Pane sizes, density, theme and random/fixed banner preferences persist.

### Import, priorities and deployment

Use **Mods → Import**, or open a `.arkive` file with Arkive. Import never installs automatically. Review, enable, set priority, preview the plan, then confirm **Apply changes**.

- Higher numeric priority wins for the **same native asset**.
- Edits to different assets within a UPK are composed into one package.
- Equal-priority competing edits block Apply. Change priority or use **Make this mod win**.
- Resolved ownership is not proof of animation/material compatibility; dependency and source checks still run.
- Enable/priority edits are desired state. Only confirmed Apply changes the game. Disable mods and apply to restore packages no longer contributed by enabled mods.

Assets, Conflicts, Details and Apply plan panes explain the effective result. **Advanced → Package projects** retains per-package rebuild, inspection, bulk install/restore, source evidence and cleanup. Prefer the named-mod workflow for normal use.

## Game names and metadata

No game database is distributed. Both the installed GUI and CLI build the in-game-name mapping directly from your client's `EFGame\data2.lpk`. In **Asset library → Library setup**, choose the language and **Build game DB**. Progress shows archive decoding and mapping stages; **Cancel DB build** stops the operation. The resulting mapping loads automatically and is remembered for the next launch.

```powershell
$cli = "$env:LOCALAPPDATA\Programs\Arkive\Arkive.Cli.exe"
& $cli database-build 'E:\Games\Lost Ark\EFGame\data2.lpk' --language English
# Optional explicit destination and JSON result; progress goes to stderr:
& $cli database-build 'E:\Games\Lost Ark\EFGame\data2.lpk' --out 'E:\ArkiveData\staging\lpk\arkive-asset-metadata.json' --language English --json
```

The default output is `staging\lpk\data2-fixed\arkive-asset-metadata.json` under Arkive's data root. **Load mapping** accepts an existing JSON. Rebuild after client updates when mappings change; then refresh verified asset links as needed. Database building does not itself scan every UPK or generate those dependency links.

The builder reads only the supported main-client tables, handles LPK Blowfish word order and filename-derived AES with per-0x400-byte CBC resets, and reads SQLite in read-only mode. Decrypted tables are temporary and removed on success, failure or cancellation; only the mapping is retained. A failed/cancelled build leaves the previous mapping untouched. Current Western/global schema and a language present in the client are required; unsupported protection/schema changes are refused.

Legacy Python/BMS scripts remain in source for format research, with upstream credits, but are not required by the installed workflow. All outputs stay local; do not redistribute game databases or generated game-data mappings.

## CLI workflow

The installer includes `Arkive.Cli.exe`. For source builds replace `& $cli` with `dotnet run --project src/Arkive.Cli -c Release --`. `--help` lists all native commands; `--version` prints the version. Most commands accept `--json`. Paths/object names below are examples, not real required asset identities.

```powershell
$cli = "$env:LOCALAPPDATA\Programs\Arkive\Arkive.Cli.exe"
$data = "$env:LOCALAPPDATA\Arkive"
$packages = 'E:\Games\Lost Ark\EFGame\ReleasePC\Packages'
& $cli --help
& $cli inspect "$packages\PACKAGE.upk" --json
& $cli catalog "$packages\PACKAGE.upk" --filter hero
& $cli index-build $packages --cache "$data\cache\library-index" --workers 4
& $cli index-search $packages --cache "$data\cache\library-index" --metadata "$data\staging\lpk\arkive-asset-metadata.json" --query 'Brelshaza' --scope ObjectAndInGameName --limit 50
```

Use the exact physical package/object identity from your own index. Do not rename client packages. Initialize the data root/game configuration in the GUI before library deployment commands.

### Shared mods

```powershell
& $cli arkive-inspect 'E:\Downloads\Example.arkive' --json
& $cli arkive-import 'E:\Downloads\Example.arkive' --root $data
& $cli arkive-list $data
& $cli arkive-set $data --mod 'Example' --enable --priority 5
& $cli arkive-conflicts $data
& $cli arkive-plan $data --json
# Review first; game/launcher/anti-cheat must be closed:
& $cli arkive-apply $data --confirm

# Restore packages no longer contributed by this mod:
& $cli arkive-set $data --mod 'Example' --disable
& $cli arkive-plan $data
& $cli arkive-apply $data --confirm
```

Use IDs from `arkive-list` when names collide. Import supports `--enable`, `--priority N` and explicit `--replace`; game installation still needs Apply. `arkive-update` concerns **game-client compatibility**, not application updates:

```powershell
& $cli client-update-check-all $data --json
& $cli arkive-update $data --mod 'Example' --json
& $cli arkive-plan $data
```

### Lower-level authoring

The CLI exposes native project stages rather than the GUI's interactive named draft. Each stage reports a manifest consumed by the next; use actual reported paths.

```powershell
# Texture: export, edit PNG/DDS externally, register a project:
& $cli texture-export-native "$packages\PACKAGE.upk" --object OBJECT --alias ALIAS --out "$data\staging\manual\texture"
& $cli texture-project-create 'PATH\arkive-native-texture.json' --edited 'PATH\edit.png' --workspace "$data\staging\texture-mods" --name 'Example texture' --alpha preserve-original

# Skeletal mesh: recon, editable export, external GLB edit, validation/registration:
& $cli skeletal-mesh-recon "$packages\PACKAGE.upk" --object OBJECT --alias ALIAS --out "$data\staging\manual\mesh"
& $cli skeletal-mesh-export-editable 'PATH\arkive-skeletalmesh-recon.json' --out 'PATH\editable'
& $cli skeletal-mesh-preflight 'PATH\arkive-editable-skeletalmesh.json' --edited 'PATH\edit.glb' --json
& $cli skeletal-mesh-register-edit 'PATH\arkive-editable-skeletalmesh.json' --edited 'PATH\edit.glb' --workspace "$data\staging\skeletal-mesh-edits" --name 'Example mesh'
```

Static stages are `static-mesh-recon`, `static-mesh-export-editable`, `static-mesh-preflight`, `static-mesh-register-edit`, `static-mesh-compile`. Material stages begin with `material-recon` and `material-create-edit`; use `--help` for their exact options. Modify supported parameter manifests, not guessed raw offsets.

The advanced CLI exposes each texture reconstruction stage, skeletal topology compilation, staged-UPK construction, `package-compose`, bundle readiness and explicit `mod-install`/`mod-restore`. These use native evidence manifests, not a `.arkive` directly. `arkive-migrate $data` converts existing selected package-project assets to portable mods without changing installed game files. Then inspect/enable/plan/apply normally. New interactive cross-package authoring is simplest in the GUI; there is no single CLI equivalent to its named draft form.

Normal exit codes: `0` success, `1` failed/refused or a blocking audit finding, `2` invalid invocation. Read reports/diagnostics even when a command succeeds. Do not casually mix legacy deployment and named-mod deployment; reconcile their exact receipts and conflicts.

## How it works

### Packages and protection

The verified profile uses UE3-derived UPK `FileVersion 868`, `LicenseeVersion 16`, Lost Ark flags `0x44`, extended tables, AES-256-ECB-protected chunk prefixes and LZ4. Arkive reads a logical image, preserves unrelated serialized data, rewrites supported exports/offsets and reconstructs physical chunks/protection. No-op/decode-back and unrelated-export proofs bind outputs to clean source bytes.

The built-in Western/global protection profile is `western-global-steam-v1`. It does not imply support for other regions or future protection layouts. LPK/database archives use a separate protection profile.

### Textures and materials

Verified paths cover BC1/DXT1, BC3/DXT5, BC5/ATI2, supported Unity-Crunch and UE3 bulk mips. Native manifests retain format/dimension/mip contracts and alpha policy. Arkive rebuilds required mip payloads and validates decoded results. Preserve normal-map semantics; colour changes do not permit arbitrary compression/layout changes.

Supported `MaterialInstanceConstant` edits cover proven scalar/vector parameters, compatible texture references and supported parent/reference fields. Arkive is not a shader compiler or arbitrary material graph importer. An emissive recolour may also need a material-tint edit. Particle effects and animation events are separate.

### Meshes, skeletons, cloth and morphs

GLB exports retain supported position/normal/tangent/UV/colour buffers, material sections, joints/weights, reference skeleton and stable vertex identifiers. Topology-preserving edits retain native structure. Topology-capable replacements compile changed LOD0 within bone/section limits and duplicate it into source LOD slots; independently authored LODs are not supplied by this route.

Skeleton comparison checks names, hierarchy/bind transforms. Retarget workspaces help map donors but do not guarantee live animation compatibility. Animation-aware preflight checks weights, bounds and stress poses, not every animation/attachment/physics interaction. Verified cloth/morph links are preserved/remapped; unsupported or incompatible cases are refused. This is not a universal cloth solver, new physics importer or unlimited morph authoring system.

Supported StaticMesh render buffers can be replaced while retaining original BodySetup/collision and non-editable data. Collision is not regenerated. Extensive geometry changes still need sensible target weights, supported UV/colour contracts, material correspondence and attachment expectations. Entirely new skeletons/classes are not automatically accepted.

### Model viewer and game shaders

The viewer is a three.js page hosted in WebView2; Arkive prepares each scene natively. Geometry comes from Arkive's skeletal and static mesh decoders (UModel's glTF only when the native layout is unproven); materials, textures and properties come from UModel's export and dump of the same object, and every package they were read from is recorded so a later change rebuilds the scene.

Lost Ark's shader caches (the `ShaderCache` exports in `ReleasePC`) hold LZ4-compressed DXBC shaders and, for every compiled material permutation, its static parameter set, uniform expressions and base-pass pixel shader. Arkive indexes them once, matches each material instance (or its nearest parent with a static permutation) to the permutation compiled for exactly its switch and mask values, evaluates the uniform expressions with the instance's parameters into the registers the shader record names, and translates the disassembled shader to GLSL with D3D bit-exact register semantics. Engine inputs the viewer does not have (sky and ambient light, fog, exact world vectors) are neutral or approximated, so lighting is the viewer's own; the material's colour, masks and glow are the game's.

### .arkive files, composition and deployment

An `.arkive` is a versioned ZIP-based container with manifest, edited payloads and target/source contract metadata, not a prebuilt game UPK. Local priority/enable state, absolute working paths, install receipts and backups stay in the user's library. Import validates paths, duplicates, counts/sizes, schema, kind/class correspondence, required entries and SHA-256. Unknown/newer schemas, unsafe paths, tampering and extra payloads are refused. New archive writes are re-read/verified before replacing their destination.

Enabled winners targeting one UPK are composed from a verified clean package, validated and backed up before controlled replacement. Receipts bind exact paths/hashes; unknown current bytes, altered approval evidence and unresolved identity block unsafe operations. A tied mod is never silently chosen. Portable mods contain data, not install scripts, but malformed inputs can still target parser bugs; only import files you trust.

## Game updates and recovery

1. **Before updating/reinstalling Lost Ark**, restore all active mods while backups and receipts match.
2. Update the client, close its processes and run **Advanced → Game update safety → Check all after game update**.
3. Review exact, safely rebaseable and review-required findings. Rebase only proven unchanged contracts. Rebuild against new clean source when supported; changed skeleton/layout/material contracts need investigation or reauthoring.
4. Refresh index/metadata as needed, preview and apply the enabled selection again.

Steam reinstall changes game bytes but does not clear Arkive's library. If a mod remains marked installed/outdated, inspect current hashes and receipts; do not delete recovery evidence to silence a warning. Unknown bytes require verified restore or client repair. Keep backups until clean state is confirmed. Steam repair restores stock files, not edit contracts.

**Restore all active** in advanced Package projects restores verified active deployments. Normal named-mod restore is disabling mods and applying the plan. Deleting a library archive is not restoring the game.

## Application updates

When enabled in Settings, startup checks GitHub's latest stable release and shows an inline notice for a newer `major.minor.patch`. Download/install requires **Install update and restart**, explicit confirmation and no active mod work. Merely publishing a release does not install it.

The download is bound to the exact versioned installer, API size and SHA-256. Arkive saves its draft/settings, closes, then a separately staged helper verifies again, runs the per-user installer and reopens the same data root. Source/portable builds are not automatically overwritten. Mods/backups are outside application replacement.

If a check, download or installation fails, Arkive says what failed, why and what to do next: for example an offline connection, GitHub's request limit, a download that failed verification (deleted, never run), another Arkive window or locked file blocking setup, or a required restart. Installer exit codes are translated and `install.log` stays beside the download. A failed download or install leaves the current version, mods, drafts and backups in place. Startup checks that fail are recorded in the session log instead of interrupting work.

Releases are checked and downloaded **without a GitHub token**. If you saved a token in an earlier version, **Settings → Forget token** removes it. A token is only needed by builds that update from a private fork: use an account's own fine-grained token with **Contents: read-only** access; it stays in Windows Credential Manager, not settings/logs/installer/source. Tokens do not grant access the account lacks.

Tokens go only to GitHub API requests, not approved asset redirects. Untrusted/HTTP hosts, missing/wrong hashes, oversized files and downgrades are refused. A compromised authorized repository can still publish a malicious correctly hashed installer; code-signing/release review remain recommended.

## Build and release

### Developer requirements

Git, PowerShell, .NET **10 SDK**, Visual Studio/Build Tools with **Desktop development with C++**, x64 MSVC/Windows SDK, and [Inno Setup 6.7+ or 7](https://jrsoftware.org/isdl.php) for installer builds. Native toolset defaults to `v145`; VS2022 can use `v143`. NuGet restores pinned BouncyCastle.Cryptography, Microsoft.Data.Sqlite/SQLitePCLRaw and self-contained Microsoft runtime packs. Their notices are included in installer builds.

```powershell
git clone https://github.com/AN-SOTP/Arkive.git
cd Arkive
winget install --id Microsoft.DotNet.SDK.10 -e
# Install the C++ workload through Visual Studio Installer / Build Tools.
winget install --id JRSoftware.InnoSetup.7 -e -s winget -i

# Download Arkive-Tools-win-x64.zip from the v0.1.0 release:
.\eng\Get-ExternalTools.ps1 -Archive 'E:\Downloads\Arkive-Tools-win-x64.zip'
.\tools\Build-CrunchNative.ps1
# VS2022: .\tools\Build-CrunchNative.ps1 -PlatformToolset v143
.\eng\Verify-Arkive.ps1 -RequireExternalCrunch
dotnet run --project src/Arkive.App -c Release
```

`eng/ExternalTools.json` pins tested UModel/Crunch/SDL SHA-256 values and upstream sources. Do not replace them with random latest builds. The native decoder bridge is built from vendored source, not committed as a DLL.

```powershell
.\eng\Build-Installer.ps1
# The version comes from Directory.Build.props; -Version X.Y.Z only confirms it and refuses a mismatch.
# Optional: -IsccPath 'C:\Tools\Inno\ISCC.exe'
```

This verifies dependencies, runs both test suites, publishes self-contained win-x64 GUI/CLI/updater and compiles into `dist\releases`. `-SkipVerification` is for local iteration, not release acceptance. `eng/Publish-Arkive.ps1` remains a framework-dependent developer publisher, not the full installer pipeline.

On a clean Windows test account with no Arkive installed, `eng/Test-Installer.ps1 -Installer PATH` exercises installation, CLI/tool discovery, GUI launch, the staged updater/restart and uninstall against an isolated workspace. It refuses to overwrite an existing installation and retains test logs. It creates/removes the per-user test installation registration; do not run it while doing other Arkive work.

The verification baseline contains **109 domain smoke cases** and an isolated WPF UI suite: archive/asset pipelines, import validation, composition/priority, restore/source contracts, discovery, selection/layout/theme and update validation. Set `ARKIVE_TEST_PACKAGE_DIR` for optional real-client tests. Synthetic tests cannot certify all live assets. Builds use nullable checks, warnings-as-errors, deterministic settings, neutral source path mapping and no managed Release PDBs; these do not prevent decompilation.

### Maintainers

`Directory.Build.props` is the only place the release version is set; the installer build, the release workflow and the application's update check all read it. To release, update `<Version>` there, review/test, then push `main` and a matching tag:

```powershell
git tag vX.Y.Z
git push origin main
git push origin vX.Y.Z
```

The tag-triggered Windows workflow refuses a tag that disagrees with `Directory.Build.props`, retrieves the pinned tool pack from `v0.1.0`, runs both test suites, builds the installer and its `.sha256` file and creates a **draft** release. Replace the draft notes, review the assets, then publish: publishing offers the update to every installed copy, while drafts/prereleases are ignored by the updater. The `v0.1.0` baseline was seeded manually; its tool pack is the bootstrap dependency for later release tags.

Run release jobs on reviewed repository-owned tags, not untrusted PR code with write credentials. Published installers and source are available from the Releases page.

`eng/Export-Source.ps1 -Destination NEW_FOLDER` creates an allowlisted fresh snapshot without history, generated binaries, user data or extra Markdown documents. It is an initial-publication aid, not the normal push process. Audit new dependency/artwork additions.

## Limitations and known issues

- Support is limited to verified Western/global Steam layouts; client updates can alter protection/serialization/contracts.
- Arbitrary new exports/imports/classes, renamed native asset identities, unlimited new skeletons and generic UE3/UE4/UE5 import are not implemented.
- UModel is an original-asset viewer/semantic oracle, not the game's renderer. Some shaders, streamed textures or animations differ; fork upgrades need a new verified tool baseline.
- Arkive's model viewer runs the game's material shaders but not its renderer: there is no in-game lighting, bloom, fog, animation or particle simulation, glow is scaled into a displayable range, and panning or flickering effects show a single moment. Materials without a matching compiled shader fall back to colour recipes or stored textures. It needs the WebView2 Runtime.
- Cross-character replacements may deform during live animations. Stress poses cannot prove every animation, cloth/morph/attachment or physics case.
- Some cloth/morph/animation layouts and references remain unsupported. Full kits can retain unresolved dependencies and first-time preparation can be expensive; progress is not a completion-time guarantee.
- StaticMesh collision is preserved, not rebuilt. Large visible geometry changes can disagree with it.
- Shader compilation, particle/VFX editing, animation events and runtime injection/hot-reload are outside the verified workflow.
- Old projects can lack current oracle/approval evidence, or retain stale installed state after reinstall. Rebuild/reconcile against verified clean bytes; unknown identity can intentionally block deletion/restore.
- Priority resolves target ownership, not semantic compatibility of all dependencies.
- CLI authoring requires explicit manifest chaining; the GUI is the recommended named-mod editor.
- The installer/updater is an unsigned preview; uninstall preserves data and does not restore the game.
- Not every bug is known. Report it on [GitHub Issues](https://github.com/AN-SOTP/Arkive/issues) with version, operation, asset identity, failure code and a sanitized diagnostic excerpt. Review paths/names before sharing logs; never submit credentials or whole game packages casually.

## Privacy, security and licensing

No telemetry or credential upload is implemented. Asset operations run locally. Optional update checks contact GitHub with normal request metadata such as IP/user-agent. Local settings/logs contain local paths and asset identities; sanitize them before sharing.

Publication starts from a clean snapshot instead of prior commits. Mods, database/asset exports, caches, backups, builds, personal settings and AI configs are excluded. Application banners/icon and the interoperability key are intentionally retained. `THIRD-PARTY-NOTICES.txt` contains dependency licenses and artwork credits.

Bounded parsing, safe paths, exact source/backup hashes and explicit deployment confirmation are safeguards, not a formal security audit. Managed code remains decompilable; no secret client-side key can be made unrecoverable. A trusted installer still executes code. Game terms, anti-cheat policies and redistribution rights are separate from Arkive's licence; no bypass or stealth guarantee is offered.

Arkive's original code, tests, build scripts and documentation are licensed **AGPL-3.0-only**. See `LICENSE.txt` for the complete unmodified GNU licence and `COPYRIGHT.txt` for its scope. Distributed derivatives must provide corresponding source under the licence; modified versions offered for remote network interaction must offer source to their users. Recipients can redistribute it. Private modifications do not automatically require publication, and copyright licensing does not control independent implementations merely inspired by Arkive's ideas. See the [GNU licence FAQ](https://www.gnu.org/licenses/gpl-faq.html#GPLRequireSourcePostedPublic) and [AGPL section 13](https://www.gnu.org/licenses/agpl-3.0.html#section13).

Anyone receiving an installer must also have access to its exact corresponding source and build instructions. Third-party components retain their licences; Lost Ark artwork/trademarks and user/game assets are not relicensed as Arkive code. This project is not affiliated with Smilegate, Amazon Games or UE Viewer.

Primary references: [Lost Ark UModel fork](https://www.gildor.org/smf/index.php?topic=3055.0), [UE Viewer source/license](https://github.com/gildor2/UEViewer), [pinned Crunch source](https://github.com/Unity-Technologies/crunch/tree/8708900eca8ec609d279270e72936258f81ddfb7), [GitHub release API](https://docs.github.com/en/rest/releases/releases), [Inno Setup](https://jrsoftware.org/isdl.php).
