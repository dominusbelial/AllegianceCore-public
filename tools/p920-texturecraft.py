#!/usr/bin/env python3
"""Private, one-atlas TextureCraft 0.2.0 evaluation. See P920_REPORT.md.

Requires Pillow, numpy, ispc_texcomp==1.0.1, texturecraft-cli, and a built
mdl-to-glb. Never writes artwork or the reference pack. No network calls.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import struct
import subprocess
import sys
import time

from PIL import Image
import numpy as np

REPO = Path(__file__).resolve().parent.parent
WEAR = [
    ("Subtle edge wear", "#999891", .48, .4, "edgeWear", .12, .32),
    ("Settled dust", "#746b59", .82, .04, "dirt", .10, .35),
    ("Recess grime", "#292a24", .88, .02, "dirt", .14, .25),
]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--artwork", required=True, type=Path)
    parser.add_argument("--reference-pack", required=True, type=Path)
    parser.add_argument("--hull", default="wc_bifig")
    parser.add_argument("--out", type=Path, default=Path(".scratch/p920/run"))
    args = parser.parse_args()
    root = args.out.resolve()
    if root.exists():
        parser.error("output must be new; choose a fresh directory")
    if not args.hull or any(c not in "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_-" for c in args.hull):
        parser.error("hull must be an asset stem")
    root.mkdir(parents=True)
    started = time.monotonic()
    dotnet = os.environ.get("DOTNET", "dotnet")
    subprocess.run([dotnet, str(REPO / "tools/mdl-to-glb/bin/Debug/net10.0/mdl-to-glb.dll"),
                    "export", str(args.artwork.resolve()), args.hull, "--out", str(root / "native")], check=True)
    # Extract exactly the GLB's PNG. TextureCraft's mesh importer does not add
    # embedded base colour to the paint stack. Leave every geometry byte intact.
    data = (root / f"native/{args.hull}.glb").read_bytes()
    length = struct.unpack_from("<I", data, 12)[0]
    gltf = json.loads(data[20:20 + length])
    if len(gltf.get("images", [])) != 1 or len(gltf.get("materials", [])) != 1:
        raise ValueError("pilot requires exactly one textured material")
    atlas = gltf["materials"][0]["name"]
    if not atlas or any(c not in "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_-" for c in atlas):
        raise ValueError("unsupported atlas name")
    view = gltf["bufferViews"][gltf["images"][0]["bufferView"]]
    offset = 28 + length + view.get("byteOffset", 0)
    (root / "native/basecolor.png").write_bytes(data[offset:offset + view["byteLength"]])
    reference = json.loads((args.reference_pack / "manifest.json").read_text())
    entry = reference["textures"][atlas]
    size = entry["maps"]["basecolor"]["width"]
    if reference["schema"] != 3 or entry["maps"]["basecolor"]["height"] != size:
        raise ValueError("pilot requires square schema-3 reference maps")
    for name, meta in entry["maps"].items():
        if meta["width"] != (size if name == "basecolor" else size // 2) or meta["height"] != meta["width"]:
            raise ValueError("reference does not use the pilot's full/half size policy")
    native = (args.artwork / f"{atlas}.mdl").read_bytes()
    if struct.unpack_from("<I", native)[0] != 0xDEBADF00:
        raise ValueError("pilot supports binary native atlases only")
    if hashlib.sha256(native).hexdigest() != entry["native"]["TextureSha256"]:
        raise ValueError("reference native atlas hash mismatch")
    log = []
    with (root / "texturecraft-stderr.log").open("w") as err:
        process = subprocess.Popen(["texturecraft-cli", "serve", "--read-root", str(root), "--write-root", str(root)],
                                   stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=err, text=True)
        def call(method, **params):
            t = time.monotonic()
            process.stdin.write(json.dumps(dict(id=len(log) + 1, method=method, params=params)) + "\n")
            process.stdin.flush()
            line = process.stdout.readline()
            if not line:
                raise RuntimeError("TextureCraft exited; see stderr log")
            reply = json.loads(line)
            log.append(dict(method=method, params=params, seconds=round(time.monotonic() - t, 4), reply=reply))
            (root / "commands.json").write_text(json.dumps(log, indent=2) + "\n")
            if not reply["ok"]:
                raise RuntimeError(reply)
            return reply.get("result")
        def cmd(command, **params):
            return call("command.run", id=command, params=params)
        try:
            call("mesh.import", path=f"native/{args.hull}.glb", resolution=size)
            cmd("layer.remove")
            resource = call("resource.import", path="native/basecolor.png")
            cmd("layer.add.image", name=resource["name"])
            cmd("layer.rename", name="Native colour (file rows)")
            layer = cmd("layer.add.fill", name="Paint material", roughness=.62, metallic=.18)["layer"]
            for channel in ("baseColor", "normal", "height"):
                cmd("layer.blend", layer=layer, channel=channel, enabled=False)
            cmd("bake.run", ts="all", resolution=512, maps=["normal_base", "ambient_occlusion", "curvature", "thickness"],
                rays=16, antialiasing=1, dilation=8, cpu=True)
            call("textures.export", dir="baked", preset="Mesh maps", size=512, format="png", bitDepth=8)
            options = dict(preset="Document channels + Normal + AO", size=size, format="png", bitDepth=8, padding="none", normalFormat="openGl")
            call("textures.export", dir="baseline", **options)
            for name, color, roughness, metallic, generator, opacity, level in WEAR:
                layer = cmd("layer.add.fill", name=name, color=color, roughness=roughness, metallic=metallic)["layer"]
                for channel in ("normal", "height"):
                    cmd("layer.blend", layer=layer, channel=channel, enabled=False)
                cmd("layer.blend", layer=layer, channel="all", opacity=opacity)
                cmd("mask.add.black", layer=layer)
                cmd("effect.add.generator", layer=layer, generator=generator, level=level, softness=.25, noise=.12, seed=920)
            call("session.inspect")
            call("project.save", path="pilot.tcraft")
            call("textures.export", dir="export", **options)
            call("textures.export", dir="directx", **dict(options, normalFormat="directX"))
            call("mesh.export", path="processed.glb", format="glb")
        finally:
            process.terminate()
            try:
                process.wait(timeout=3)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait()
    source = root / "pack-input" / atlas
    source.mkdir(parents=True)
    coverage = np.clip(np.asarray(Image.open(root / f"baseline/mesh_{atlas}_roughness.png"), dtype=np.float32) / round(.62 * 255), 0, 1)
    for name, channel in [("basecolor", "baseColor"), ("normal", "normal"), ("metallic", "metallic"), ("roughness", "roughness")]:
        image = Image.open(root / f"export/mesh_{atlas}_{channel}.png").convert("RGB")
        if name == "basecolor":
            # TextureCraft masks exports to rasterized UV coverage. Preserve
            # authored texels sampled by degenerate UVs too: apply only its
            # measured wear delta over the complete native colour image.
            baseline = Image.open(root / f"baseline/mesh_{atlas}_baseColor.png").convert("RGB")
            original = Image.open(root / "native/basecolor.png").convert("RGB").resize((size, size), Image.Resampling.BILINEAR)
            image = Image.fromarray(np.uint8(np.clip(np.asarray(original, dtype=np.int16)
                + np.asarray(image, dtype=np.int16) - np.asarray(baseline, dtype=np.int16), 0, 255)))
        elif name == "normal":
            # No height layer/high-poly transfer in this pilot: the exported
            # mesh-map normal is the complete final normal. Unlike the composite
            # exporter, Mesh maps retains neutral values outside UV coverage.
            image = Image.open(root / f"baked/mesh_{atlas}_normal_base.png").convert("RGB").resize((size, size), Image.Resampling.BILINEAR)
        elif name in ("metallic", "roughness"):
            # Composite export premultiplies fractional UV-edge coverage even
            # in RGB/scalar files. Remove it before compressing data maps.
            values = np.asarray(image, dtype=np.float32)
            values = np.divide(values, coverage[:, :, None], out=np.full_like(values, (.18 if name == "metallic" else .62) * 255), where=coverage[:, :, None] > 0)
            image = Image.fromarray(np.uint8(np.clip(np.rint(values), 0, 255)))
        # Schema 3 binds binary native maps with V reversed. TextureCraft's
        # OpenGL tangent points against canonical dV, matching that binding:
        # reverse image rows, preserve green. Do not also invert green.
        image.transpose(Image.Transpose.FLIP_TOP_BOTTOM).save(source / f"{name}.png")
    Image.new("RGB", (size, size)).save(source / "emission.png")
    sys.path.insert(0, str(REPO / "tools/hd-pack"))
    import build_pack
    catalog = {"models": [{"kind": "ship", "model": args.hull, "stems": [atlas], "capital": False}]}
    build_pack.build(root / "pack-input", args.artwork, root / "pack", catalog, format="bc", policy={"small": size}, workers=1)
    manifest_path = root / "pack/manifest.json"
    manifest = json.loads(manifest_path.read_text())
    if "materialTuning" in reference:
        manifest["materialTuning"] = reference["materialTuning"]
    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n")
    # Keep the builder's archives consistent with the copied reference tuning.
    build_pack.release_zip(root / "pack", root / "pack.zip")
    build_pack.release_zip(root / "pack", root / "pack.bc3.zip", manifest, bc3=True)
    summary = dict(hull=args.hull, atlas=atlas, seconds=round(time.monotonic() - started, 3),
                   texturecraft=subprocess.check_output(["texturecraft-cli", "--version"], text=True).strip(),
                   nativeSha256=hashlib.sha256(native).hexdigest(),
                   mapSha256={p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted((root / "pack/textures" / atlas).glob("*.dds"))})
    (root / "summary.json").write_text(json.dumps(summary, indent=2) + "\n")
    print(json.dumps(summary, indent=2))


if __name__ == "__main__":
    main()
