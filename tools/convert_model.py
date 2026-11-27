#!/usr/bin/env python3
"""Convert Real-ESRGAN models (PyTorch .pth) to ONNX for Aetherium Forge (IMPLEMENTATION_GUIDE section 9).

Supported: ``RealESRGAN_x4plus.pth`` (RRDBNet, release v0.1.0) and ``realesr-general-x4v3.pth`` (SRVGGNetCompact,
release v0.2.5.0). The architectures are defined inline, so the script needs only ``torch`` and ``onnx`` (no ``basicsr``);
``--validate`` also needs ``onnxruntime``. It does not download anything: fetch the .pth from the official Real-ESRGAN
GitHub releases (https://github.com/xinntao/Real-ESRGAN/releases) yourself and pass its path.

Output:
    <out>.onnx   opset 17, input ``input`` / output ``output`` (NCHW, RGB, 0-1), dynamic H and W,
                 custom metadata ``scale`` = 4.
    <out>.json   sidecar read by ModelCatalog: displayName, scale, precision, licence, recommendedFor.

Usage:
    python tools/convert_model.py RealESRGAN_x4plus.pth
    python tools/convert_model.py RealESRGAN_x4plus.pth --output models/realesrgan-x4plus.onnx
    python tools/convert_model.py RealESRGAN_x4plus.pth --fp16          # writes realesrgan-x4plus-fp16.onnx
    python tools/convert_model.py realesr-general-x4v3.pth --validate   # compact model, checked against PyTorch

Notes:
    * FP16 halves the file size and is faster on RTX-class GPUs, but slower on Pascal (GTX 10xx); FP32 is the default.
    * Copy the .onnx and .json files into %LOCALAPPDATA%\\AetheriumForge\\models.
    * Real-ESRGAN is BSD-3-Clause licensed.
"""

from __future__ import annotations

import argparse
import inspect
import json
import sys
from pathlib import Path

import onnx
import torch
from torch import nn
from torch.nn import functional as F

SCALE = 4
OPSET = 17
DEFAULT_NAME = "realesrgan-x4plus"


class ResidualDenseBlock(nn.Module):
    """Five 3x3 convolutions with dense connections and a 0.2-scaled residual (ESRGAN)."""

    def __init__(self, num_feat: int = 64, num_grow_ch: int = 32) -> None:
        super().__init__()
        self.conv1 = nn.Conv2d(num_feat, num_grow_ch, 3, 1, 1)
        self.conv2 = nn.Conv2d(num_feat + num_grow_ch, num_grow_ch, 3, 1, 1)
        self.conv3 = nn.Conv2d(num_feat + 2 * num_grow_ch, num_grow_ch, 3, 1, 1)
        self.conv4 = nn.Conv2d(num_feat + 3 * num_grow_ch, num_grow_ch, 3, 1, 1)
        self.conv5 = nn.Conv2d(num_feat + 4 * num_grow_ch, num_feat, 3, 1, 1)
        self.lrelu = nn.LeakyReLU(negative_slope=0.2, inplace=False)

    def forward(self, x: torch.Tensor) -> torch.Tensor:
        x1 = self.lrelu(self.conv1(x))
        x2 = self.lrelu(self.conv2(torch.cat((x, x1), 1)))
        x3 = self.lrelu(self.conv3(torch.cat((x, x1, x2), 1)))
        x4 = self.lrelu(self.conv4(torch.cat((x, x1, x2, x3), 1)))
        x5 = self.conv5(torch.cat((x, x1, x2, x3, x4), 1))
        return x5 * 0.2 + x


class RRDB(nn.Module):
    """Residual-in-residual dense block: three dense blocks and a 0.2-scaled residual."""

    def __init__(self, num_feat: int, num_grow_ch: int = 32) -> None:
        super().__init__()
        self.rdb1 = ResidualDenseBlock(num_feat, num_grow_ch)
        self.rdb2 = ResidualDenseBlock(num_feat, num_grow_ch)
        self.rdb3 = ResidualDenseBlock(num_feat, num_grow_ch)

    def forward(self, x: torch.Tensor) -> torch.Tensor:
        out = self.rdb1(x)
        out = self.rdb2(out)
        out = self.rdb3(out)
        return out * 0.2 + x


class RRDBNet(nn.Module):
    """RRDBNet as in basicsr ``rrdbnet_arch`` for scale 4 (no pixel-unshuffle; that is only used for x1 and x2)."""

    def __init__(self, num_in_ch: int = 3, num_out_ch: int = 3, num_feat: int = 64, num_block: int = 23, num_grow_ch: int = 32) -> None:
        super().__init__()
        self.conv_first = nn.Conv2d(num_in_ch, num_feat, 3, 1, 1)
        self.body = nn.Sequential(*[RRDB(num_feat, num_grow_ch) for _ in range(num_block)])
        self.conv_body = nn.Conv2d(num_feat, num_feat, 3, 1, 1)
        self.conv_up1 = nn.Conv2d(num_feat, num_feat, 3, 1, 1)
        self.conv_up2 = nn.Conv2d(num_feat, num_feat, 3, 1, 1)
        self.conv_hr = nn.Conv2d(num_feat, num_feat, 3, 1, 1)
        self.conv_last = nn.Conv2d(num_feat, num_out_ch, 3, 1, 1)
        self.lrelu = nn.LeakyReLU(negative_slope=0.2, inplace=False)

    def forward(self, x: torch.Tensor) -> torch.Tensor:
        feat = self.conv_first(x)
        feat = feat + self.conv_body(self.body(feat))
        feat = self.lrelu(self.conv_up1(F.interpolate(feat, scale_factor=2, mode="nearest")))
        feat = self.lrelu(self.conv_up2(F.interpolate(feat, scale_factor=2, mode="nearest")))
        return self.conv_last(self.lrelu(self.conv_hr(feat)))


class SRVGGNetCompact(nn.Module):
    """Compact VGG-style network with PReLU and a pixel-shuffle head (basicsr ``srvgg_arch``), used by realesr-general-x4v3."""

    def __init__(self, num_in_ch: int = 3, num_out_ch: int = 3, num_feat: int = 64, num_conv: int = 32, upscale: int = 4) -> None:
        super().__init__()
        self.upscale = upscale
        body: list[nn.Module] = [nn.Conv2d(num_in_ch, num_feat, 3, 1, 1), nn.PReLU(num_parameters=num_feat)]
        for _ in range(num_conv):
            body += [nn.Conv2d(num_feat, num_feat, 3, 1, 1), nn.PReLU(num_parameters=num_feat)]
        body.append(nn.Conv2d(num_feat, num_out_ch * upscale * upscale, 3, 1, 1))
        self.body = nn.ModuleList(body)
        self.upsampler = nn.PixelShuffle(upscale)

    def forward(self, x: torch.Tensor) -> torch.Tensor:
        out = x
        for layer in self.body:
            out = layer(out)
        out = self.upsampler(out)
        return out + F.interpolate(x, scale_factor=self.upscale, mode="nearest")


# Known architectures: default output name, display name and network factory.
ARCHS = {
    "rrdb": (DEFAULT_NAME, "Real-ESRGAN x4plus", lambda: RRDBNet(num_in_ch=3, num_out_ch=3, num_feat=64, num_block=23, num_grow_ch=32)),
    "compact": ("realesr-general-x4v3", "Real-ESRGAN general x4v3 (compact)", lambda: SRVGGNetCompact()),
}


def load_state(weights: Path) -> dict:
    """Loads the official checkpoint (EMA weights when present).

    Always loads with ``weights_only=True``: a full unpickle can run arbitrary code from the checkpoint, so there is no
    fallback to it. torch older than 1.13 has no ``weights_only`` and is rejected.
    """
    if "weights_only" not in inspect.signature(torch.load).parameters:
        raise SystemExit(
            f"torch {torch.__version__} is too old: torch.load has no weights_only parameter, and loading a checkpoint "
            "without it can execute arbitrary code. Install torch 1.13 or later."
        )

    state = torch.load(weights, map_location="cpu", weights_only=True)

    for key in ("params_ema", "params"):
        if isinstance(state, dict) and key in state:
            return state[key]
    return state


def detect_arch(state: dict) -> str:
    """RRDBNet checkpoints have ``conv_first``; SRVGGNetCompact checkpoints have ``body.0`` and no ``conv_first``."""
    return "rrdb" if "conv_first.weight" in state else "compact"


def load_model(weights: Path, arch: str | None = None) -> tuple[nn.Module, str]:
    """Builds the network for ``arch`` (auto-detected when None) and loads the checkpoint strictly."""
    state = load_state(weights)
    arch = arch or detect_arch(state)
    model = ARCHS[arch][2]()
    model.load_state_dict(state, strict=True)
    model.eval()
    return model, arch


def validate(model: nn.Module, output: Path, fp16: bool) -> float:
    """Runs the exported model with onnxruntime (CPU) and returns the max absolute difference against PyTorch FP32."""
    import numpy as np
    import onnxruntime as ort

    reference_model = model.to(device="cpu", dtype=torch.float32)
    x = torch.rand(1, 3, 48, 80)
    with torch.no_grad():
        reference = reference_model(x).numpy()
    session = ort.InferenceSession(str(output), providers=["CPUExecutionProvider"])
    result = session.run(None, {"input": x.numpy().astype(np.float16 if fp16 else np.float32)})[0].astype(np.float32)
    if result.shape != reference.shape:
        raise RuntimeError(f"shape mismatch: onnx {result.shape} vs torch {reference.shape}")
    return float(np.abs(result - reference).max())


def export(model: nn.Module, output: Path, fp16: bool) -> None:
    """Exports with opset 17 and dynamic H/W, then embeds the scale metadata."""
    dtype = torch.float16 if fp16 else torch.float32
    # Tracing runs the model once; older torch builds lack FP16 convolutions on the CPU, so trace FP16 on CUDA when available.
    device = "cuda" if fp16 and torch.cuda.is_available() else "cpu"
    model = model.to(device=device, dtype=dtype)
    dummy = torch.rand(1, 3, 64, 64, dtype=dtype, device=device)

    kwargs = {
        "input_names": ["input"],
        "output_names": ["output"],
        "opset_version": OPSET,
        "do_constant_folding": True,
        "dynamic_axes": {"input": {2: "height", 3: "width"}, "output": {2: "out_height", 3: "out_width"}},
    }
    # torch >= 2.5 has a dynamo exporter (the default from 2.9); dynamic_axes targets the TorchScript exporter.
    if "dynamo" in inspect.signature(torch.onnx.export).parameters:
        kwargs["dynamo"] = False

    output.parent.mkdir(parents=True, exist_ok=True)
    with torch.no_grad():
        torch.onnx.export(model, dummy, str(output), **kwargs)

    onnx_model = onnx.load(str(output))
    for prop in list(onnx_model.metadata_props):
        if prop.key == "scale":
            onnx_model.metadata_props.remove(prop)
    entry = onnx_model.metadata_props.add()
    entry.key = "scale"
    entry.value = str(SCALE)
    onnx.checker.check_model(onnx_model)
    onnx.save(onnx_model, str(output))


def write_sidecar(output: Path, fp16: bool, display_name: str) -> Path:
    """Writes the <name>.json sidecar next to the model."""
    sidecar = output.with_suffix(".json")
    content = {
        "displayName": display_name + (" (FP16)" if fp16 else ""),
        "scale": SCALE,
        "precision": "fp16" if fp16 else "fp32",
        "licence": "BSD-3-Clause",
        "recommendedFor": ["diffuse", "normal"],
    }
    sidecar.write_text(json.dumps(content, indent=2) + "\n", encoding="utf-8")
    return sidecar


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Convert a Real-ESRGAN .pth (x4plus or general-x4v3) to ONNX (opset 17, dynamic H/W).")
    parser.add_argument("weights", type=Path, help="path to RealESRGAN_x4plus.pth or realesr-general-x4v3.pth")
    parser.add_argument("--arch", choices=sorted(ARCHS), help="network architecture (default: detected from the checkpoint)")
    parser.add_argument("--output", type=Path, help="output .onnx path (default: <model-name>[-fp16].onnx next to the weights)")
    parser.add_argument("--fp16", action="store_true", help="export FP16 weights and tensors (faster on RTX, slower on GTX 10xx)")
    parser.add_argument("--validate", action="store_true", help="compare the ONNX output with PyTorch using onnxruntime (needs onnxruntime and numpy)")
    args = parser.parse_args(argv)

    if not args.weights.is_file():
        parser.error(f"weights not found: {args.weights}")

    model, arch = load_model(args.weights, args.arch)
    name, display_name, _ = ARCHS[arch]
    output = args.output or args.weights.with_name(name + ("-fp16" if args.fp16 else "") + ".onnx")
    export(model, output, args.fp16)
    sidecar = write_sidecar(output, args.fp16, display_name)
    print(f"Wrote {output} and {sidecar} (arch {arch})")
    if args.validate:
        fresh, _ = load_model(args.weights, arch)
        print(f"Max abs difference vs PyTorch FP32: {validate(fresh, output, args.fp16):.6f}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
