# Aetherium Forge — Implementation Guide

AI texture forge for Skyrim Special Edition / Anniversary Edition.
It finds every texture the game still loads from the original game archives, upscales it with an ONNX model on the GPU, and packages the result as a self-contained mod that any mod manager can install. It fills gaps only, so it never overrides textures from installed mods.

Status: M1 (core scan), M2 (imaging), M2b (GPU auto profile, model downloads) and M3 (staged pipeline, CLI host) implemented. M3 check: a 30-minute real run (realesrgan-x4plus, GTX 1080) interrupted by a graceful Ctrl+C, a forced double Ctrl+C and a hard kill, then resumed, ended with 143 Done jobs, exactly 143 complete BC7 outputs (correct size and full mip chain), no orphans or partial files, nothing Running or Failed, empty work and staging folders, and no texconv left running.
M1 (core scan) and M2 (imaging) detail: M2 check: the 5 textures the prototype finished, reprocessed with `realesrgan-x4plus` (FP32, DirectML, GTX 1080), match the prototype's output at 42.2–45.5 dB PSNR, with a mean difference of 0.8–1.1 of 255 levels and a mean normal angle difference of 0.7–0.9°. Upscaling a 2048² texture to 4096² takes about 38 s on that GPU.
M1 detail: On the reference install it queues the same 3,433 vanilla textures as the prototype, with identical targets, plus Creation Club and `_ResourcePack.bsa` textures (base tier, §5 item 3).
Proven prototype: `TextureGapUpscale`, an unpublished local tool (Python + texconv + Real-ESRGAN ncnn). Its scan, classification, post-processing and resume logic are the reference for this guide.

---

## 1. Goals and non-goals

**Goals**
- Mod-manager agnostic: work from what the game actually loads (the Skyrim folder), not from a specific mod manager.
- Automatic discovery of the game, the profile files and the best disk for working files.
- Automatic grouping of textures (large surfaces vs small clutter) with editable rules.
- GPU upscaling through **ONNX Runtime + DirectML** with a selectable model per group.
- Long-running, interruptible jobs: Start, Pause, Resume and Stop, with no lost work beyond the item in progress.
- Output as a complete mod: ESL-flagged plugins + BSA archives + FOMOD metadata, zipped.
- Avalonia UI with lists, filters and live (async) progress.

**Non-goals (v1)**
- PBR / TruePBR materials (Community Shaders only).
- Generating parallax or height maps.
- Faces, bodies and skins (`textures\actors\character\...`): skin mods own those.
- Skyrim LE, VR, the Microsoft Store / Game Pass edition.

---

## 2. Tech stack

| Concern | Choice | Notes |
|---|---|---|
| Runtime | .NET 10 (LTS), C# | `win-x64` only (DirectML is Windows-only) |
| UI | Avalonia 11.x | MVVM via `CommunityToolkit.Mvvm`; Fluent theme; `DataGrid` package for lists |
| Inference | `Microsoft.ML.OnnxRuntime.DirectML` | DirectX 12 GPU; works on NVIDIA, AMD and Intel |
| Images (CPU ops) | `SkiaSharp` (already a dependency of Avalonia) | Decode/encode PNG, resize, pixel access |
| DDS read/write | `texconv.exe` (DirectXTex, MIT) shipped in `tools\` | Decode to RGBA PNG, encode BC7 with mipmaps (GPU-accelerated) |
| BSA compression | `K4os.Compression.LZ4.Streams` | SSE archives use the LZ4 frame format |
| Job store | `Microsoft.Data.Sqlite` | One database per project |
| Logging | `Microsoft.Extensions.Logging` + file sink (e.g. Serilog) | Also streamed to the UI log panel |
| DI / hosting | `Microsoft.Extensions.Hosting` | Services, options, background pipeline |

Check every package version on NuGet at implementation time and pin it in `Directory.Packages.props` (central package management).

---

## 3. Solution layout

```
Aetherium Forge/
├─ AetheriumForge.slnx
├─ global.json                    (SDK pin; Microsoft.Testing.Platform test runner)
├─ Directory.Build.props          (net10.0-windows, win-x64, nullable, TreatWarningsAsErrors, MinVer)
├─ Directory.Packages.props       (central package versions)
├─ assets/                        (icon sources: SVG, ICO, PNG)
├─ tools/                         (texconv.exe; not committed, fetched by a script)
├─ models/                        (ONNX models; not committed)
├─ src/
│  ├─ AetheriumForge.Core/        (no UI; all logic, testable)
│  │  ├─ Discovery/   GameLocator, ProfileLocator, DriveAdvisor
│  │  ├─ Game/        PluginList, IniArchiveList, ResourceGraph
│  │  ├─ Archives/    BsaReader, BsaWriter, TesHash
│  │  ├─ Plugins/     EslPluginWriter
│  │  ├─ Textures/    DdsHeader, TextureClassifier, TextureScanner
│  │  ├─ Rules/       ForgeRules (+ JSON persistence), GroupRule
│  │  ├─ Imaging/     IUpscaler, OnnxDmlUpscaler, TiledRunner, ImageOps, Texconv
│  │  ├─ Pipeline/    ForgePipeline, PauseTokenSource, PipelineProgress
│  │  ├─ Jobs/        JobStore (SQLite), JobState, Project
│  │  └─ Packaging/   ArchivePlanner, ModPackager, FomodWriter
│  └─ AetheriumForge.App/         (Avalonia)
│     ├─ Views/       Scan, Rules, Queue, Run, Compare, Package, Settings
│     ├─ ViewModels/
│     └─ Assets/      aetherium-forge.ico, icon PNGs
└─ tests/
   └─ AetheriumForge.Core.Tests/  (xUnit; archive and hash round-trips, classifier, plugin writer)
```

---

## 4. Discovery

### 4.1 Game folder (best effort, in this order; the user can always override)

1. **Steam**
   - Steam path: `HKCU\Software\Valve\Steam\SteamPath` (fallback `HKLM\SOFTWARE\WOW6432Node\Valve\Steam\InstallPath`).
   - Parse `<Steam>\steamapps\libraryfolders.vdf` (KeyValues text). Every library has a `path` and an `apps` list.
   - Find app id **489830** (Skyrim Special Edition). Read `<library>\steamapps\appmanifest_489830.acf` → `installdir`.
   - Game folder = `<library>\steamapps\common\<installdir>`.
2. **Bethesda key**: `HKLM\SOFTWARE\WOW6432Node\Bethesda Softworks\Skyrim Special Edition\installed path`.
3. **GOG**: `HKLM\SOFTWARE\WOW6432Node\GOG.com\Games\<gameId>\path`. Verify the GOG id for Skyrim AE before relying on it; you can also scan all subkeys for `gameName` containing "Skyrim".
4. **Epic**: `C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests\*.item` (JSON). Match `DisplayName` containing "Skyrim", then use `InstallLocation`.

Validate each candidate: it must contain `SkyrimSE.exe` and a `Data` folder. Read the executable's file version (for example 1.6.1170) and show it.

### 4.2 Profile files

| What | Where |
|---|---|
| INI files (`Skyrim.ini`, `SkyrimPrefs.ini`, `SkyrimCustom.ini`) | `Documents\My Games\Skyrim Special Edition\` (GOG: `...Skyrim Special Edition GOG`, Epic: `...Skyrim Special Edition EPIC`) |
| `plugins.txt` (active plugins and order) | `%LOCALAPPDATA%\Skyrim Special Edition\plugins.txt` (same GOG/EPIC suffix rule) |
| `Skyrim_Default.ini` | Game folder |

Resolve **Documents** with the Known Folder API (`SHGetKnownFolderPath(FOLDERID_Documents)`), not by building `%USERPROFILE%\Documents`. `Environment.GetFolderPath(SpecialFolder.MyDocuments)` calls that API on Windows, so use it and keep P/Invoke out of Core. Future native interop (DXGI, NVML) goes in a separate `AetheriumForge.Native` project that uses `LibraryImport` with `AllowUnsafeBlocks`. Users often move Documents, often to another drive.

### 4.3 Working disk

- List fixed drives (`DriveInfo.GetDrives()`, `DriveType.Fixed`, `IsReady`) with free space.
- Estimate the space needed from the scan: output ≈ Σ(target² × 1.33) bytes for BC7 with mips, plus temporary space of about `concurrency × largest image × 4 bytes × 3 stages`.
- Default the project folder to the drive with the most free space that fits the estimate. Warn if none fits. Never use the system drive if another one fits.

### 4.4 Mod Organizer 2 (best effort, optional)

MO2 keeps mods outside `Data` and exposes them only while the game runs through its virtual file system, so scanning `Data` sees only the base game. Support MO2 by reading an instance:
- Instance folder → `ModOrganizer.ini` (`mod_directory`, profile).
- `profiles\<profile>\modlist.txt` (lines `+Mod` enabled, `-Mod` disabled; the **top** of the file is the **highest** priority).
- `profiles\<profile>\plugins.txt` (use instead of the AppData one).
- Build the loose-file view by walking enabled mods in priority order, then `overwrite\`.

Detect MO2 if `Data` holds only base-game files while the plugin list references many mods, and offer "Point me to your MO2 instance".

---

## 5. What the game loads (resource graph)

The game resolves each file path in this order. The highest wins.

1. **Loose files** in `Data\` (or the MO2 view). These always win over any archive.
2. **Plugin archives**, in plugin load order (later plugins win): for each active plugin `X.esp/.esm/.esl`, the game loads `X.bsa` and `X - Textures.bsa` if they exist.
3. **INI archives**: `sResourceArchiveList` + `sResourceArchiveList2` (these hold the base game, including `Skyrim - Textures0..8.bsa`). Read `Skyrim_Default.ini`, then `Skyrim.ini`, then `SkyrimCustom.ini` (later overrides earlier). On AE, also add the archives of the Creation Club plugins listed in `Skyrim.ccc` if their plugins exist.

### Active plugins

- `plugins.txt`: lines starting with `#` are comments; lines starting with `*` are active (AE format). Line order is the load order.
- **Implicit plugins**, always loaded first even if not listed: `Skyrim.esm`, `Update.esm`, `Dawnguard.esm`, `HearthFires.esm`, `Dragonborn.esm`, then the CC plugins from `Skyrim.ccc` that exist in Data.
- Fallback when `plugins.txt` is missing: the newest CrashLogger log's `PLUGINS:` section. That's the method the prototype used.

### Gap set = what Aetherium Forge processes

```
gapSet = { p in INI-archive textures (*.dds)
           where no loose file p exists
             and no active plugin archive contains p
             and not excluded by rules }
```

Path comparison is case-insensitive with backslash separators. Normalise every path to lowercase with `\`.

---

## 6. BSA format (SSE, version 105)

### Header (36 bytes)

| Offset | Type | Field |
|---|---|---|
| 0 | char[4] | `BSA\0` |
| 4 | u32 | version = **105** |
| 8 | u32 | folder records offset = 36 |
| 12 | u32 | archive flags |
| 16 | u32 | folder count |
| 20 | u32 | file count |
| 24 | u32 | total folder-name length: Σ(name length + 1). Counts each name's null, **not** its length byte (verified against vanilla archives) |
| 28 | u32 | total file-name length (including nulls) |
| 32 | u16 | content flags (textures = `0x2`) |
| 34 | u16 | padding |

**Archive flags:** `0x1` folder names included, `0x2` file names included, `0x4` compressed by default, `0x100` file names embedded in the data blocks. For textures archives use `0x1 | 0x2 | 0x4` (= 7). Vanilla texture archives use `0x107`: each file's full path is embedded as a bstring before its data, so the reader must skip it.

### Folder records (24 bytes each, sorted by folder hash)

`u64 hash · u32 fileCount · u32 padding · u64 offset`. The offset points to this folder's name/file-record block, **plus `totalFileNameLength`** (a historical quirk; follow it exactly).

### Per-folder block

`u8 nameLength (including null) · char[] name + '\0'`, then `fileCount × { u64 hash · u32 size · u32 offset }`, with files sorted by hash.
- `size` bit `0x40000000` **toggles** compression relative to archive flag `0x4`.
- `offset` is absolute from the start of the file.

**File-name block:** all file names, null-terminated, in the same order as the file records.

**Data block (compressed):** `u32 originalSize` + an **LZ4 frame** (SSE). Uncompressed: raw bytes.

### Hash (TES4 / SSE)

Lowercase the path and use backslashes. Hash folders as the full folder path with no extension. Hash files as the name only.

```
root = name without extension, ext = extension including the dot
h1 = (len(root) > 2 ? root[^2] << 8 : 0) | root[^1] | (len(root) << 16) | (root[0] << 24)
ext ".kf" → h1 |= 0x80;  ".nif" → h1 |= 0x8000;  ".dds" → h1 |= 0x8080;  ".wav" → h1 |= 0x80000000
h2 = 0; for c in root[1 .. len-3]: h2 = h2 * 0x1003F + c   (uint32 wraparound)
h3 = 0; for c in ext: h3 = h3 * 0x1003F + c
hash = ((ulong)(h2 + h3) << 32) | h1
```

**Required test:** read every vanilla `Skyrim - Textures*.bsa`, recompute the hash of every folder and file name, and assert they equal the stored hashes. Don't ship the writer until this round-trip passes 100%.

### Writer rules

- Keep each archive **under about 2 GB**. That's the community-safe limit; offsets are u32.
- Group files by folder and sort folders and files by hash.
- Compress every texture (LZ4 frame, default level). Store an uncompressed block only if compression doesn't shrink it, by flipping the `0x40000000` bit for that file.
- **Validation:** reopen the written archive with `BsaReader`, extract a sample, and byte-compare it with the source DDS.

---

## 7. Plugin writer (ESL-flagged ESP, no records)

One plugin per archive pair. File name: `AetheriumForge - Textures NN.esp`.

TES4 record:

| Field | Value |
|---|---|
| Type | `TES4` |
| Data size | sum of the subrecords below |
| Flags | `0x200` (Light / ESL). Don't set master. |
| FormID | 0 |
| Version control | 0 |
| Internal version | 44 |
| Unknown | 0 |

Subrecords (`char[4] type · u16 size · data`):
- `HEDR` (12 bytes): `float version = 1.70` (compatible with all SE/AE builds; 1.71 needs 1.6.1130+) · `int32 numRecords = 0` · `u32 nextObjectId = 0x800`.
- `CNAM`: author, zero-terminated (`Aetherium Forge`).
- `SNAM`: description, zero-terminated (model, date, texture count, "fills gaps only").

No masters are needed, because the plugin has no records. The game loads `X.bsa` and `X - Textures.bsa` for it, so each plugin can carry two archives of about 2 GB.

**Test:** the file starts with `TES4`, the data size matches, and xEdit opens it without errors (manual check once).

---

## 8. Texture classification and rules

### DDS header

Read the first 148 bytes (decompress only the first block of an archive entry; the prototype reads about 200 bytes through a partial LZ4 decode).
- Height @12, width @16, mip count @28, FourCC @84, caps2 @112 (cubemap = `0x200`).
- If FourCC = `DX10`: DXGI format @128.

### Texture kind (by file-name suffix, before `.dds`)

| Suffix | Kind | Default |
|---|---|---|
| `_n` | Normal (tangent space; alpha = specular) | Upscale + renormalise |
| `_msn` | Model-space normal | Skip |
| `_s` | Specular | Upscale |
| `_g` | Glow | Upscale |
| `_p` | Height / parallax | Upscale (no AI; Lanczos) |
| `_e`, `_em`, `_m`, `_envmask` | Environment / env mask | Skip (`_envmask`: upscale is fine) |
| `_sk` | Skin tint | Skip |
| `_b`, `_bl` | Backlight | Upscale |
| (none) or `_d` | Diffuse | Upscale (alpha kept) |

Also skip cubemaps (caps2 `0x200`).

### Default excluded folders

`textures\actors\character\`, `textures\interface\`, `textures\lod\`, `textures\terrain\` (LOD), `textures\effects\`, `textures\cubemaps\`, `textures\dyndolod\`, `textures\test\`, `textures\shadertests\`.

### Groups (editable rules, evaluated top-down, first match wins)

```json
{
  "groups": [
    { "name": "Clutter",  "match": ["*\\clutter\\*"], "cap": 2048, "maxFactor": 4,
      "model": "realesrgan-x4plus", "normalStrength": 1.4 },
    { "name": "Large",    "match": ["*"],             "cap": 4096, "maxFactor": 4,
      "model": "realesrgan-x4plus", "normalStrength": 1.0 }
  ],
  "minSource": 128,
  "exclude": [ "...see above..." ]
}
```

**Target size:** `target = min(cap, sourceMax × maxFactor)`. Skip if `sourceMax ≥ cap` or `sourceMax < minSource`. Keep the aspect ratio.

**Automatic small/large separation:** besides path patterns, a rule can match on source size (`sourceMax <= 512`) or kind. The Rules screen shows live counts per group before any processing.

### Look (artistic direction per group)

Each group can carry an optional `look` block that steers how the output looks. Every value defaults to neutral, which gives the plain model output (current behaviour).

```json
"look": {
  "aiStrength": 1.0,
  "sharpen": { "amount": 0.0, "radius": 1.0 },
  "denoise": null,
  "brightness": 0.0, "contrast": 1.0, "gamma": 1.0, "saturation": 1.0, "temperature": 0.0,
  "normalStrength": 1.0,
  "specularScale": 1.0
}
```

| Control | Applies to | Effect |
|---|---|---|
| `aiStrength` (0–1) | all kinds that use the model | Blends the AI output with a plain Lanczos upscale of the source. Below 1, it tames over-sharpened, "painted" AI detail. |
| `sharpen.amount`, `sharpen.radius` | diffuse, glow, backlight | Unsharp mask after resizing. 0 = off. |
| `denoise` (0–1 or null) | models that support it | Denoise strength for models published with a denoise variant (for example `realesr-general-wdn-x4v3`, blended by weight interpolation at load time). Null = the model's default. |
| `brightness` (−1–1), `contrast` (0.5–2), `gamma` (0.5–2), `saturation` (0–2), `temperature` (−1–1, cool to warm) | diffuse, glow, backlight only | Colour grading in linear light after resizing. Never applied to normal, specular, mask or parallax maps, which encode data, not colour. |
| `normalStrength` | normal maps | "How 3D": scales the XY of tangent-space normals before renormalising (§10 step 6). Replaces the group-level `normalStrength`, which stays as an alias for compatibility. |
| `specularScale` (0–2) | specular maps and the specular alpha of normal maps | "How shiny": scales specular intensity, clamped to 0–1. |

Order in §10: upscale → AI-strength blend → resize → sharpen → colour grading (colour kinds) → normal strength (normals) → specular scale → merge alpha → encode.

The look block is part of the rules JSON, so it is saved with the project and exported with the rules. The job store records a hash of the effective look per job. Changing a group's look re-queues its finished jobs only when the user confirms ("Apply to finished textures?").

---

## 9. Upscaling with ONNX Runtime + DirectML

### Session setup

```csharp
var so = new SessionOptions();
so.AppendExecutionProvider_DML(deviceId);              // DXGI adapter index
so.EnableMemoryPattern = false;                        // required by DirectML
so.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;       // required by DirectML
so.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
using var session = new InferenceSession(modelPath, so);
```

- **One session per model**, reused for the whole run. Load lazily.
- Read the scale from the model metadata or by a probe: run a 16×16 input and compare the output size.
- **Precision:** FP32 by default. FP16 models are faster on RTX-class GPUs but slower on Pascal (GTX 10xx), so offer them as an option per model.

### Tiled inference (bounded memory and real pause)

- Tile size 256–512 px (default 384), overlap 16–32 px (default 24).
- Input tensor: NCHW float32, RGB, range 0–1.
- For each tile: extract with overlap → run → crop the overlap from the output (scale × overlap) → write into the output buffer. Optionally feather the seams with a linear blend.
- **Check the pause/cancel token between tiles.** This makes Pause take effect within about one tile (under a second) and Stop finish without corrupt output.
- **Out of video memory:** catch the ORT exception, halve the tile size and retry the image.

### Models

- Default: Real-ESRGAN x4plus (BSD-3). Convert `RealESRGAN_x4plus.pth` (GitHub release) to ONNX with PyTorch, using `torch.onnx.export`, opset 17, and dynamic H/W axes. A one-off script goes in `tools/convert_model.py`.
- More models: the community model database (OpenModelDB) lists texture-oriented models. **Check each model's licence**; some are non-commercial only.
- Models folder: `models\*.onnx` plus an optional `models\<name>.json` sidecar (`displayName`, `scale`, `precision`, `licence`, `recommendedFor: ["diffuse","normal"]`).
- **Model download is part of the UI** (Settings → Models, and a first-run prompt):
  - A model manifest (`models.json`, shipped with the app) lists each downloadable model: name, display name, URL, SHA-256, size, scale, precision, licence, and recommended GPU tier.
  - Converted ONNX models (FP32 and FP16) are published as assets of a `models-vN` GitHub release of this repository, each with its licence. Only models whose licence allows redistribution are hosted (Real-ESRGAN: BSD-3-Clause).
  - The UI shows installed and available models, downloads with progress, Pause/Cancel and resume (HTTP range), and verifies the SHA-256 before moving the file into `%LOCALAPPDATA%\AetheriumForge\models`. Users can delete models and add their own `.onnx` with a sidecar.
  - The download needs the user's confirmation and shows the size and licence first.
- **Discovery contract** (published: [`models-v1`](https://github.com/panagiotispsichopedas/aetherium-forge/releases/tag/models-v1)):
  1. `GET https://api.github.com/repos/panagiotispsichopedas/aetherium-forge/releases?per_page=100` (header `Accept: application/vnd.github+json`, a generic `User-Agent`, no token).
  2. Keep non-draft releases whose tag matches `^models-v(\d+)$` and take the highest number. Never use `/releases/latest`: it can point at a models release or at an app release.
  3. Download that release's `models.json` asset. Fallback when the API is rate-limited (60 requests/hour unauthenticated): `https://github.com/panagiotispsichopedas/aetherium-forge/releases/download/models-v1/models.json`. Cache the manifest with its ETag.
  4. `models.json` has `schemaVersion` (1), `release` and `models[]`. Each model has `name`, `family`, `displayName`, `description`, `architecture`, `parametersMillions`, `fileName`, `url`, `size`, `sha256` (lowercase hex), `sidecarFileName`, `scale`, `precision` (`fp32`/`fp16`), `tier` (`standard`/`low`), `licence`, `licenceFile`, `licenceUrl`, `recommendedFor`, `source`, `input`, `output`, `opset`, `maxAbsDiffVsPyTorch`. The app ignores fields it does not know and refuses a `schemaVersion` it does not support.
  5. Download to `<name>.onnx.partial` (resume with HTTP `Range`), check the size and SHA-256, then move it into the models folder and write the sidecar (also published as an asset).
  - **Trust boundary:** the SHA-256 values come from `models.json` in the same release as the models, so verification proves a download matches the release, not who published it. Integrity rests on write access to this repository's releases; there is no separate signature. The app accepts only `https://github.com/{owner}/{repo}/releases/download/…` URLs and redirects to GitHub's release-asset hosts.
  - New or updated models go in a new `models-vN` release; published assets are never replaced, so a SHA-256 always identifies one file. Rebuild models with `tools/convert_model.py --validate`.

### GPU auto profile

Runs on first launch, when the GPU or driver changes, or on request (Settings → GPU → Recalibrate). Results are stored per adapter (LUID + driver version) and model.

1. **Enumerate adapters** with DXGI (`IDXGIFactory6::EnumAdapterByGpuPreference`, high performance first). Read the name, LUID, dedicated video memory, and whether it is integrated or software. Pick the discrete adapter with the most dedicated memory by default.
2. **Memory budget** from `IDXGIAdapter3::QueryVideoMemoryInfo` (local segment `Budget − CurrentUsage`).
3. **Precision:** if the FP16 model exists, run a short benchmark of FP16 against FP32 at the same tile and keep the faster. FP16 normally wins on RTX-class GPUs; Pascal (GTX 10xx) normally keeps FP32.
4. **Tile size:** starting from a size estimated from the budget, run one tile, measure peak memory use, and grow or halve until it stays below about 80 % of the budget. The upscaler still halves the tile when the GPU runs out of memory at run time.
5. **Throughput:** record seconds per megapixel. This seeds the ETA (§11).
6. **Default model:** low tier (integrated GPU or under 4 GB of dedicated memory) recommends a compact model (for example `realesr-general-x4v3`); otherwise `realesrgan-x4plus`. This only sets the first-run defaults for the groups; the Rules screen keeps the per-group choice.

DXGI interop lives in a separate `AetheriumForge.Native` project that uses `LibraryImport` with `AllowUnsafeBlocks`, so Core stays free of unsafe code (§4.2).

---

## 10. Per-texture processing (reference: prototype `run.py`)

1. **Extract** the DDS from its archive → `work\src\...`.
2. **Decode:** `texconv -nologo -y -ft png -f R8G8B8A8_UNORM -o <dir> <file.dds>` (accepts many files per call).
3. **Split:** RGB → model input. Keep alpha only if `min(alpha) < 255`.
4. **Upscale** RGB with the group's model (tiled, DirectML).
5. **Resize** to the target size (Lanczos/Mitchell) and keep the aspect ratio.
6. **Normal maps:** convert RGB to a vector `v = rgb/127.5 − 1`, multiply XY by the group's normal strength, clamp Z to at least 0.05, normalise, and convert back. Never feed alpha (specular) through the AI; upscale it with bicubic.
   - **Look** (§8): apply the group's look controls in the order given there (AI-strength blend, sharpen, colour grading, specular scale). With the neutral defaults this step changes nothing.
7. **Merge alpha** (resized with bicubic) if it was kept.
8. **Encode:** `texconv -nologo -y -f BC7_UNORM -m 0 -o <tmpOut> <file.png>` (BC7 is GPU-accelerated by default; `-gpu N` selects the adapter, `-nogpu` is the fallback).
9. **Commit atomically:** move the finished DDS from `tmpOut` to `output\textures\...`, then mark the job **Done** in the database. **Only this step makes an item count as finished.**

---

## 11. Pipeline and concurrency

```
[Scan] → JobStore (SQLite)
          │
          ▼
 Producer ─► Channel<Job>(bounded 8) ─► Decode (CPU, N workers)
                                         ─► Channel(bounded 4) ─► Upscale (GPU, 1 worker, tiled)
                                         ─► Channel(bounded 4) ─► PostProcess (CPU, N workers)
                                         ─► Channel(bounded 4) ─► Encode (texconv, 2 workers) ─► Commit
```

- `System.Threading.Channels` with **bounded** capacity, so memory stays flat and the GPU never waits on the CPU.
- Use **one GPU worker per adapter**. DirectML sessions aren't designed for parallel runs on one adapter.
- **Pause:** a `PauseTokenSource` (an async manual-reset gate) checked by the producer, by every stage before each item, and by the upscaler between tiles. Items in progress keep their state in memory and continue after Resume.
- **Stop:** cancel the `CancellationToken`. Each stage finishes or abandons its current item. Abandoned items go back to `Queued`. Temporary files are cleaned on the next start.
- **Crash or power loss:** on start, interrupted jobs (`Running`) return to `Queued`, and the `work\` folder is wiped. `Done`, `Failed` and `Skipped` keep their state; failed jobs are retried only on request (Queue screen → Retry).
- **Progress:** each stage reports `PipelineProgress` (item, stage, percent, tiles done/total, timings) through an `IProgress<T>`/`Channel` that the UI consumes on the dispatcher (throttled to about 10 Hz).
- **ETA:** an exponential moving average of the seconds per megapixel (source) per model, multiplied by the remaining megapixels. This is far more accurate than a per-item average, because sizes vary from 128² to 2048².
- **GPU telemetry** (optional): DXGI `QueryVideoMemoryInfo` for video memory; NVML (NVIDIA) for load if present.

---

## 12. Job store (SQLite)

```sql
CREATE TABLE project (key TEXT PRIMARY KEY, value TEXT);          -- game path, rules JSON, scan date...
CREATE TABLE job (
  id INTEGER PRIMARY KEY,
  path TEXT UNIQUE NOT NULL,         -- textures\...\x.dds (lowercase)
  archive TEXT NOT NULL,             -- source BSA
  src_w INT, src_h INT, format TEXT, kind TEXT,
  grp TEXT, target INT, model TEXT,
  state TEXT NOT NULL,               -- Queued|Running|Done|Failed|Skipped
  attempts INT DEFAULT 0, error TEXT,
  seconds REAL, finished_at TEXT
);
CREATE INDEX job_state ON job(state);
CREATE TABLE run (id INTEGER PRIMARY KEY, started TEXT, ended TEXT, done INT, failed INT);
```

- **Import** from the prototype: every `<prototype folder>\mod\textures\...` DDS that exists → `Done` (copy or reference it), and `failed.txt` → `Failed`.
- **Rescan** (after the user installs mods): recompute the gap set. Jobs no longer in it become `Skipped (now provided by a mod)`; new gaps are `Queued`.

---

## 13. Packaging (any mod manager)

1. **Plan the archives:** sort the finished textures by path and fill archives up to about 1.9 GB each. Two archives per plugin: `AetheriumForge - Textures NN.bsa` and `AetheriumForge - Textures NN - Textures.bsa`.
2. **Write the plugins** (section 7) and the archives (section 6). Validate each archive by reopening it.
3. **FOMOD metadata:** `fomod\info.xml` with `Name`, `Author`, `Version`, `Description`, `Website`. Both Vortex and MO2 read this.
4. **Readme** (`AetheriumForge - README.txt`) with the model(s), rules, texture count, date, and load-order guidance:
   - "Load these plugins **early**: right after the base game and Creation Club plugins. Loose textures from any mod always win, and so do later mods' archives."
   - "Re-run Aetherium Forge (Rescan → Forge → Package) after installing new texture mods."
5. **Zip** everything (`System.IO.Compression.ZipFile`, store mode for the archives since they're already compressed). The zip goes to the project folder.
6. **Afterwards:** remind the user to re-run PGPatcher/ParallaxGen (new textures) and TexGen/DynDOLOD (LOD uses textures).

**Licensing note shown in the UI:** output contains derivatives of Bethesda and mod textures and is for personal use only. Don't redistribute it.

---

## 14. UI (Avalonia)

### Structure (approved redesign, applied after the M4/M5 pages merge)

The app follows the workflow, not the build order. The left rail is a **stepper** with each step's status, a project card on top (name, folder, queued and done counts) and a run mini-progress at the bottom that is visible from every page. A status bar shows project, GPU memory and run state.

| Step | Replaces | Contents |
|---|---|---|
| **1 · Project** | Scan | Game detection, project folder, scan and rescan, a trimmed summary (four tiles), "Next: Style". |
| **2 · Style** | Rules + Compare | Groups on the left (drag to reorder; first match wins), a large before/after preview in the centre with the sample strip, and a right panel with **Matching** (patterns, size range, kinds), **Output** (model, largest size, scale) and **Look** (§8 sliders and presets) tabs. Every group setting is edited here and only here, including the model and relief (normal strength). One action bar: Apply, Revert, unsaved-changes indicator, and confirmation before finished textures change. |
| **3 · Forge** | Run + Queue | **Pre-flight checklist** (project scanned, models installed, GPU calibrated, texconv found, disk space), each failed check with a fix button. Start is disabled until the required checks pass. Below it: overall progress and ETA, stage lanes, and tabs In progress / Queued / Failed / Done / Log. Filters and bulk actions (Retry, Skip, Re-queue) live in the tabs and are locked with a visible reason while forging. |
| **4 · Package** | Package | M6 (below). |
| Models & GPU (tool) | Models + Settings | Downloads, calibration, adapter, advanced settings. Also reached from pre-flight fixes. |

**Rules for every page**
- The primary action sits bottom-right in an `ActionBar`. Destructive choices are never the accent or the default.
- Errors, warnings and info use one `StatusBanner` pattern. Blocked actions always say why and offer the fix.
- While forging, Scan, Rescan, Open project and Apply are locked, with a banner. Leaving a page with unsaved changes asks first.
- **Theme:** follows the OS by default. The user can override it in Models & GPU → Settings (or the rail menu): **System / Light / Dark**, applied instantly through `RequestedThemeVariant`.
  - The choice is stored in the app-level settings database `%LOCALAPPDATA%\AetheriumForge\app.db` (SQLite, table `setting(key TEXT PRIMARY KEY, value TEXT)`), not in a project.
  - App preferences that were in `app-settings.json` (recent projects, last project, first-run dismissal) move to the same database, with a one-time import of the old file.
- **Design system:** one `Theme/AetheriumTheme.axaml` resource dictionary (Light and Dark `ThemeDictionaries`, tuned dark-first).
  - Semantic brushes Success, Warning, Danger and Info, kept separate from the ember accent.
  - Type ramp 28/20/15/13/11.
  - Spacing 4/8/12/16/24/32.
  - One card style.
  - Shared controls: `PageHeader`, `ActionBar`, `StatusBanner`, `EmptyState`, `StatTile`, `StageLane`, `ChecklistItem`, `ConfirmFlyout`.
  - No hex colours in views.
- **Accessibility:** `AutomationProperties.Name` on every icon button; state is never shown by colour alone.
- **Keys:** `Ctrl+1…4` steps, `Ctrl+Enter` start, `Space` pause, `Ctrl+S` apply. In the preview, arrows pan, `+`/`-` zoom and `[`/`]` move the split.
- **Wording:**
  - "Textures to forge" (not "Gaps").
  - "Largest size" / "Up to ×N" (not "Cap" / "Max factor").
  - "Relief" for normal strength, "Shine" for specular scale.
  - "Fast preview" (not "draft").

### Screen contents (as built in M4/M5; the structure above regroups them)

| Screen | Contents | Key interactions |
|---|---|---|
| **Scan** | Detected game (path, version, store), profile files, plugin count, drives with free space and the chosen project folder, plus a "Scan" button | Override paths; the scan runs async with a progress bar; summary cards by group, kind and resolution |
| **Rules** | Group list (pattern, size range, kind, cap, max factor, model, normal strength), exclusions, minimum source size | Live counts and estimated output size and time per group; reset to defaults; import/export JSON |
| **Queue** | Virtualised `DataGrid` of jobs: path, kind, group, source → target, model, state, time, error | Filter (state, group, kind, text), sort, multi-select → Retry / Skip / Re-queue / change model |
| **Run** | Start / Pause / Resume / Stop; overall bar + ETA; per-stage lanes (decode, upscale, post, encode) with the current item and tile progress; throughput; GPU load and video memory; live log tail | Buttons enabled by state machine (Idle → Running ⇄ Paused → Stopping → Idle) |
| **Compare** | Before/after slider for any finished job; zoom 100–400%; channel view (RGB, alpha, normal) | Pick the model per group by testing on samples (a "Preview 10 random from group" button). **Look panel:** sliders for every §8 look control, with a live preview on the sample textures (rendered from a cached upscale, so slider changes don't re-run the AI) and presets (Neutral, Crisp, Soft, Vivid, Deep relief). "Apply to group" writes the look into the rules. |
| **Package** | Archive plan (archives, sizes, plugins), output path, build button, result | Shows the load-order note and the PGPatcher/DynDOLOD reminder |
| **Settings** | GPU adapter and its auto profile (precision, tile, throughput), tile overlap, worker counts, texconv path, models folder, log level | Recalibrate GPU; manual overrides for the profile |
| **Models** (in Settings, and a first-run prompt) | Installed models (size, precision, licence, recommended tier) and models available from the manifest | Download with progress, Pause/Cancel and resume; hash check; delete; add a custom `.onnx` |

**MVVM notes**
- `ObservableCollection` + `DataGrid` virtualisation for about 5,000 rows; update rows in batches (`DispatcherTimer`, about 10 Hz), not per event.
- The pipeline lives in a singleton service. Closing the window asks to Pause or Stop. A tray icon is optional.

---

## 15. Testing strategy

| Test | Type |
|---|---|
| Hash round-trip over the vanilla archives (section 6) | Integration (needs the game, skipped in CI) |
| BSA writer → reader byte-equality on generated archives | Unit |
| ESL writer header bytes | Unit |
| `plugins.txt` / INI parsing (comments, `*`, CC list, overrides) | Unit |
| Gap-set computation on a synthetic resource graph | Unit |
| Classifier (suffix → kind, rule matching, target sizes) | Unit |
| Normal renormalisation (all vectors unit length; strength 1.0 = identity) | Unit |
| Tiled inference = whole-image inference (on a small test model) within tolerance | Integration |
| Pause/Stop/resume (no corrupt output, no duplicates) | Integration with a fake upscaler |

---

## 16. Milestones

| # | Deliverable | Done when |
|---|---|---|
| M1 | Core: discovery, plugin/INI parsing, BSA reader + hash, scanner, classifier, SQLite store | The scan of the reference PC reproduces the prototype manifest (3,433 jobs) |
| M2 | Imaging: texconv wrapper, ONNX DirectML tiled upscaler, post-processing | 5 textures match the prototype's output quality |
| M2b | GPU auto profile (§9): DXGI adapters and memory budget (`AetheriumForge.Native`), precision benchmark, tile calibration, throughput, default model tier; model manifest and download service (hash-verified, resumable) | On the reference GTX 1080 the profile picks an adapter, precision and tile without manual settings; a model downloads, verifies and loads |
| M3 | Pipeline: channels, Pause/Resume/Stop, ETA, crash recovery | A 30-minute run interrupted 3 ways resumes with no corruption |
| M4 | UI: Scan, Queue, Run | Full run controllable from the UI |
| M5 | Rules editor + Compare, including per-group **look** controls (§8) with live preview | Per-group model choice and look (AI strength, sharpen, colour grading, normal strength, specular scale) work end to end, and neutral looks reproduce the M2 output exactly |
| M5b | UI redesign (§14 Structure), in three phases after the M4/M5 merge: **A** design system, rail stepper, run and unsaved-changes guards; **B** Style workspace (Rules + Compare) and Forge workspace (Run + Queue + pre-flight); **C** accessibility, keyboard, wording, first-run walkthrough | A first-time user goes from launch to a running forge without leaving the stepper or hitting a dead end. No hex colours in views. Every icon button has an accessible name. |
| M6 | Packaging: BSA/ESL/FOMOD/zip | The mod installs in Vortex, the game loads it, and xEdit opens the plugins cleanly |
| M7 | Import from prototype, rescan, settings, polish | The prototype's finished textures aren't redone |

---

## 17. Risks and mitigations

| Risk | Mitigation |
|---|---|
| AI distorts normal maps | Renormalise; allow a "Lanczos only" model per group; Compare screen |
| Video memory exhaustion (8 GB cards) | Tiling with automatic tile-size reduction |
| BSA hash or format mistake → game ignores files | Mandatory round-trip test (section 6); validation on write |
| Users installing texture mods later | Rescan + "now provided by a mod" state; README guidance |
| MO2 virtual file system | Explicit MO2 instance support (section 4.4) |
| Model licences | Licence field in the model sidecar, shown in the UI |
| Huge output (about 45 GB for 3,400 textures) | Disk advisor; per-group caps; size estimate before running |
