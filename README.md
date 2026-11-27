<p align="center">
  <img src="assets/icon/aetherium-forge-256.png" alt="Aetherium Forge" width="128" height="128">
</p>

<h1 align="center">Aetherium Forge</h1>

<p align="center">AI texture forge for <em>The Elder Scrolls V: Skyrim</em> Special Edition / Anniversary Edition.</p>

<p align="center">
  <a href="https://github.com/panagiotispsichopedas/aetherium-forge/actions/workflows/ci.yml"><img src="https://github.com/panagiotispsichopedas/aetherium-forge/actions/workflows/ci.yml/badge.svg?branch=develop" alt="CI"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/licence-MIT-blue.svg" alt="MIT licence"></a>
  <a href="https://github.com/panagiotispsichopedas/aetherium-forge/wiki"><img src="https://img.shields.io/badge/docs-wiki-informational.svg" alt="Wiki"></a>
</p>

---

> **Status: early development.** There is no UI and no upscaling yet. See [Status](#status).

Aetherium Forge finds every texture that Skyrim still loads from its **original game archives**, upscales it on your GPU with an ONNX model, and packages the result as a self-contained mod that any mod manager can install.

It **fills gaps only**. If a mod you installed already replaces a texture (as a loose file or inside its own archive), Aetherium Forge leaves that texture alone. You keep your hand-made texture mods, and the forge upscales only what is left.

## Contents

- [How it works](#how-it-works)
- [Status](#status)
- [Features](#features)
- [Requirements](#requirements)
- [Building from source](#building-from-source)
- [Models](#models)
- [Repository layout](#repository-layout)
- [Branching, CI and releases](#branching-ci-and-releases)
- [Documentation](#documentation)
- [Legal](#legal)

## How it works

```
 Skyrim folder + profile files
            │
            ▼
 ┌─────────────────────┐   Which textures does the game load, and from where?
 │ 1. Scan             │   INI archives → plugin archives → loose files.
 │    (resource graph) │   Gap set = textures served only by base-game archives.
 └─────────┬───────────┘
           ▼
 ┌─────────────────────┐   Classify each texture (diffuse, normal, specular, …),
 │ 2. Rules            │   skip excluded folders and kinds, pick a group,
 │                     │   a model and a target size.
 └─────────┬───────────┘
           ▼
 ┌─────────────────────┐   Decode with texconv → tiled ONNX inference on the GPU
 │ 3. Upscale          │   (DirectML) → resize, renormalise normal maps,
 │                     │   merge alpha → encode BC7 with mipmaps.
 └─────────┬───────────┘
           ▼
 ┌─────────────────────┐   ESL-flagged plugins + SSE BSA archives
 │ 4. Package          │   + FOMOD metadata, zipped.
 └─────────────────────┘
```

Progress is kept in a SQLite job store, so a run can be paused, stopped or interrupted and resumed without redoing finished textures. After you install new mods, a rescan marks textures that a mod now provides as skipped.

Details: [Architecture](https://github.com/panagiotispsichopedas/aetherium-forge/wiki/Architecture) and [Skyrim resource loading](https://github.com/panagiotispsichopedas/aetherium-forge/wiki/Skyrim-Resource-Loading) in the wiki.

## Status

| Milestone | Scope | State |
|---|---|---|
| **M1** Core | Game, profile and drive discovery; `plugins.txt`, INI and `Skyrim.ccc` parsing; SSE BSA reader and TES4 hash; gap-set scan; texture classifier; editable rules; SQLite job store with rescan | **Merged** into `develop` |
| **M2** Imaging | texconv wrapper, image operations, tiled ONNX Runtime + DirectML upscaler, post-processing, model conversion script; converted models published as `models-v1` | **In progress** |
| **M2b** GPU auto profile | DXGI adapter and video-memory budget, FP16/FP32 benchmark, tile calibration, default model tier; in-app model downloads | Next |
| M3 – M7 | Pipeline (Pause/Resume/Stop), Avalonia UI, rules editor and compare view, packaging, prototype import and polish | Planned |

The full plan is in [IMPLEMENTATION_GUIDE.md](IMPLEMENTATION_GUIDE.md).

## Features

Planned for the first release. Items marked ✅ exist in `AetheriumForge.Core` today.

- ✅ **Mod-manager agnostic.** Works from what the game actually loads, not from one mod manager's data. Optional Mod Organizer 2 instance support is planned.
- ✅ **Automatic discovery** of the game (Steam, Bethesda launcher, GOG, Epic), the profile files, and the best disk for working files.
- ✅ **Editable grouping rules** (for example small clutter vs. large surfaces) with a per-group model, size cap, scale factor and normal-map strength.
- ✅ **Resumable job store** with crash recovery and rescan.
- **GPU upscaling** through ONNX Runtime + DirectML on NVIDIA, AMD and Intel GPUs, with automatic tile sizing and precision selection.
- **Interruptible jobs.** Start, Pause, Resume and Stop, with no lost work beyond the item in progress.
- **Complete mod output.** ESL-flagged plugins, SSE BSA archives, FOMOD metadata, zipped.
- **Avalonia desktop UI** with live progress, a before/after compare view, and a filterable queue.

## Requirements

**To run** (once there is a release):

- Windows 10 or 11, x64
- A DirectX 12 GPU (NVIDIA, AMD or Intel)
- *Skyrim Special Edition* or *Anniversary Edition* from Steam, GOG or Epic (Skyrim LE, VR and the Microsoft Store / Game Pass edition are not supported)
- Free disk space for the output (it can reach tens of GB for a full run; the app estimates it before starting)

No Python and no separate runtime: the app is self-contained.

**To build:**

- The .NET 10 SDK (the exact feature band is pinned in [global.json](global.json))
- PowerShell 7 or Windows PowerShell 5.1 for the helper scripts

## Building from source

```powershell
git clone https://github.com/panagiotispsichopedas/aetherium-forge.git
cd aetherium-forge
git switch develop
```

Fetch the third-party tools (`texconv.exe` from DirectXTex, pinned and SHA-256 verified):

```powershell
.\tools\fetch-tools.ps1
```

Build and test:

```powershell
dotnet build AetheriumForge.slnx
```

```powershell
dotnet test --solution AetheriumForge.slnx
```

Tests run on Microsoft.Testing.Platform (xUnit v3). Integration tests that need the game, texconv or a model are skipped unless you point environment variables at them; see [Building and testing](https://github.com/panagiotispsichopedas/aetherium-forge/wiki/Building-and-Testing) in the wiki.

> `tools\fetch-tools.ps1` arrives with the M2 imaging work. On a branch without it, the build and the M1 tests do not need texconv.

## Models

Aetherium Forge never bundles AI models in the repository or the app download.

- Converted ONNX models (FP32 and FP16) are published as assets of a **`models-vN`** GitHub release of this repository, together with a `models.json` manifest that lists each model's size, SHA-256, scale, precision, licence and recommended GPU tier. The current one is **[`models-v1`](https://github.com/panagiotispsichopedas/aetherium-forge/releases/tag/models-v1)**. Published assets are never replaced; new models go in a new release.
- The app finds the highest-numbered `models-vN` release through the GitHub Releases API and offers the downloads in **Settings → Models** and on first run. Every download shows its size and licence first, needs your confirmation, and is hash-checked.
- Only models whose licence allows redistribution are hosted. The defaults are **Real-ESRGAN x4plus** and **realesr-general-x4v3** (BSD-3-Clause).
- You can add your own `.onnx` model with a small JSON sidecar.

Contributors convert models from PyTorch with `tools/convert_model.py`; that is the only place Python is used. See [Models](https://github.com/panagiotispsichopedas/aetherium-forge/wiki/Models) and [Python model conversion](https://github.com/panagiotispsichopedas/aetherium-forge/wiki/Python-Model-Conversion).

## Repository layout

```
assets/                      icon sources (SVG, ICO, PNG)
src/AetheriumForge.Core/     all logic, no UI: Discovery, Game, Archives, Textures, Rules, Jobs (+ Imaging, Pipeline in M2)
src/AetheriumForge.Native/   (planned, M2b) DXGI interop through LibraryImport
src/AetheriumForge.App/      (planned, M4) Avalonia desktop UI
tests/                       xUnit v3 tests
tools/                       helper scripts; third-party binaries (texconv) are fetched, not committed
models/                      local ONNX models for development; not committed
IMPLEMENTATION_GUIDE.md      the design: formats, algorithms, milestones
```

## Branching, CI and releases

| Branch | Purpose |
|---|---|
| `main` | Released, stable code. Changes arrive only by merge from `develop` (or a `hotfix/*` branch). |
| `develop` | Integration branch (protected, pull requests only). Feature branches (`feature/*`, `fix/*`, `docs/*`) start here and merge back here. |

- Every merge is a **true merge commit**. Branches are never squashed or rebased.
- **CI** builds and tests every push and pull request on `main` and `develop`.
- **Releases** are built only from a `vX.Y.Z` tag on `main`. The version comes from the tag through MinVer.
- Commit messages follow [Conventional Commits](https://www.conventionalcommits.org/).

See [CONTRIBUTING.md](CONTRIBUTING.md) for the full workflow.

## Documentation

- **[Wiki](https://github.com/panagiotispsichopedas/aetherium-forge/wiki)**: architecture, tech stack, how Skyrim loads resources, the BSA format, rules, models, the GPU auto profile, building and testing, contributing, and an FAQ.
- [IMPLEMENTATION_GUIDE.md](IMPLEMENTATION_GUIDE.md): the authoritative design.
- [CONTRIBUTING.md](CONTRIBUTING.md): branches, merges, CI, releases and code standards.

## Legal

Aetherium Forge source code is released under the [MIT License](LICENSE).

The **output** it generates is derived from Bethesda's game textures (and any textures in your load order). It is for **personal use only**. Do not redistribute generated textures or packaged mods.

This project is not affiliated with or endorsed by Bethesda Softworks, ZeniMax Media or Microsoft. *The Elder Scrolls*, *Skyrim* and related names are trademarks of their respective owners. Third-party components and models carry their own licences; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
