<p align="center">
  <img src="assets/icon/aetherium-forge-256.png" alt="Aetherium Forge" width="128" height="128">
</p>

<h1 align="center">Aetherium Forge</h1>

<p align="center">AI texture forge for <em>The Elder Scrolls V: Skyrim</em> Special Edition / Anniversary Edition.</p>

---

> **Status:** early development. Milestone M1 (discovery, archive reading, gap-set scan, rules, job store) is in place; there is no UI or upscaling yet. See [IMPLEMENTATION_GUIDE.md](IMPLEMENTATION_GUIDE.md).

Aetherium Forge finds every texture the game still loads from the original game archives, upscales it on the GPU with an ONNX model, and packages the result as a self-contained mod that any mod manager can install. It **fills gaps only**: it never overrides textures that your installed mods already provide.

## Features (planned)

- **Mod-manager agnostic.** Works from what the game actually loads, with optional Mod Organizer 2 instance support.
- **Automatic discovery** of the game (Steam, GOG, Epic), profile files, and the best disk for working files.
- **Editable grouping rules** (large surfaces vs. small clutter) with a per-group model, size cap and normal-map strength.
- **GPU upscaling** through ONNX Runtime + DirectML (NVIDIA, AMD, Intel).
- **Interruptible jobs.** Start, Pause, Resume and Stop with no lost work beyond the item in progress.
- **Complete mod output.** ESL-flagged plugins, SSE BSA archives, FOMOD metadata, zipped.
- **Avalonia desktop UI** with live progress, before/after compare, and a queue you can filter.

## Requirements

- Windows 10/11 x64 with a DirectX 12 GPU
- .NET 10 SDK (to build)
- Skyrim Special Edition / Anniversary Edition (Steam, GOG or Epic)

## Repository layout

```
assets/   icon sources (SVG, ICO, PNG)
src/      AetheriumForge.Core (logic) and AetheriumForge.App (Avalonia UI)
tests/    xUnit tests
tools/    helper scripts; third-party binaries (texconv) are fetched, not committed
models/   ONNX models; not committed
```

## Branching

| Branch | Purpose |
|---|---|
| `main` | Released, stable code. Changes arrive only by merge from `develop` (or a `hotfix/*` branch). |
| `develop` | Integration branch. Feature branches (`feature/*`, `fix/*`) start here and merge back here. |

Every merge is a true merge commit (`--no-ff`). Branches are never squashed or rebased. See [CONTRIBUTING.md](CONTRIBUTING.md).

## Legal

Aetherium Forge source code is released under the [MIT License](LICENSE).

The **output** it generates is derived from Bethesda's game textures (and any textures in your load order). It is for **personal use only**. Do not redistribute generated textures or packaged mods.

This project is not affiliated with or endorsed by Bethesda Softworks, ZeniMax Media or Microsoft. *The Elder Scrolls*, *Skyrim* and related names are trademarks of their respective owners. Third-party components and models carry their own licences; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
