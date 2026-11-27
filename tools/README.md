# tools

Third-party binaries are **not** committed. Scripts here fetch or build them.

| File | What it does |
|---|---|
| `fetch-tools.ps1` | Installs `texconv.exe` (DirectXTex, MIT) from the pinned `may2026` release and verifies its SHA-256. `-From <path>` copies an existing copy instead; the hash must still match. |
| `convert_model.py` | Converts `RealESRGAN_x4plus.pth` (BSD-3-Clause) to ONNX for the upscaler. Needs Python with `torch` and `onnx`. |

```powershell
.\tools\fetch-tools.ps1
```

Models go in `models\` (development) or `%LOCALAPPDATA%\AetheriumForge\models` (default at runtime), each `*.onnx` with an optional `<name>.json` sidecar.
