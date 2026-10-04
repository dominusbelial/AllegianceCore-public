#!/usr/bin/env python3
"""P337 — restore real Meshy textures onto the ship/missile content models.

Each P287 FBX model was deliberately exported with its materials STRIPPED
(see /tmp/p287stage/glb_to_fbx.py:36 `o.data.materials.clear()`), so the ships
currently render flat matte gray even though real faction-livery textures
still exist in the source .glb files. This script re-extracts each model's
baseColor texture directly from its source .glb (a GLB is a 12-byte header +
JSON chunk + BIN chunk; the material's `baseColorTexture` -> image ->
`bufferView` is a JPEG blob) and writes it to
`Content/Textures/ships/<asset>.png`, keyed by the same asset names
`HullModelResolver.ShipAssets` enumerates. No Blender dependency.

Verified assumption (see the P337 task scope item 1): every ship asset has
exactly one material with exactly one baseColor texture; the three missiles
(mis05/mis400/artifact) likewise have exactly one baseColor texture each
(their extra images are the metallicRoughness + normal PBR maps, irrelevant to
BasicEffect's diffuse-only sampling). So one texture per asset is correct — no
per-meshpart mapping is needed.

Usage:
  python3 extract_meshy_textures.py [MESHY_DIR] [OUT_DIR] [UPScaled_GLB] [MAX_SIZE]

Defaults: MESHY_DIR = ~/Development/allegiance-tools/reference/meshy-results/models
          OUT_DIR   = src/Allegiance.ClientCore.MonoGame/Content/Textures/ships
          UPScaled_GLB = the pre-P287 fallback's source .glb (bios_scout_upscaled)
          MAX_SIZE  = 1024 (downscaled from the 2048² source — plenty for low-poly
                      ships that render mostly minified; keeps repo + GPU sane)
"""
import io
import json
import os
import struct
import sys
from PIL import Image

REPO = os.path.dirname(os.path.abspath(__file__))
DEFAULT_MESHY = os.path.expanduser(
    "~/Development/allegiance-tools/reference/meshy-results/models")
DEFAULT_OUT = os.path.join(
    REPO, "src/Allegiance.ClientCore.MonoGame/Content/Textures/ships")
DEFAULT_FALLBACK_GLB = (
    "/tmp/claude-1000/-home-domingo/3dd3c01a-33dd-4353-8ac0-a088b27a4ab6/"
    "scratchpad/bios_scout_upscaled.glb")

# Mirrors HullModelResolver.ShipAssets (72) + MissileAssets (3) + FallbackAsset.
SHIP_ASSETS = [
    "belters-bomber","belters-fig","belters-scout",
    "bios-bomber","bios-carrier","bios-constructor","bios-corvette","bios-cruiser",
    "bios-destroyer","bios-devastator","bios-ejectpod","bios-fighter","bios-interceptor",
    "bios-miner","bios-stealthfighter","bios-trooptransport",
    "dreg-bbr2","dreg-carrier","dreg-corvette","dreg-fig2","dreg-frigate","dreg-scout2","dreg-stealthfighter","dreg-trooptransport",
    "giga-bomber","giga-fighter","giga-interceptor","giga-scout",
    "gt-ass","gt-bomber","gt-carrier","gt-con","gt-cruiser","gt-dev","gt-fighter","gt-hod",
    "gt-int","gt-miner","gt-mustang","gt-pod","gt-rd","gt-scout","gt-sf","gt-tt",
    "ic-bomber","ic-carrier","ic-constructor","ic-corvette","ic-cruiser","ic-dev",
    "ic-devastator","ic-ejectpod","ic-fighter","ic-frigate","ic-int","ic-interceptor",
    "ic-miner","ic-scout","ic-stealthfighter","ic-trooptransport",
    "oh-bomber","oh-fighter","oh-scout",
    "rix-ass","rix-bomber","rix-carrier","rix-con","rix-cruiser","rix-fighter","rix-int",
    "rix-miner","rix-pod","rix-scout","rix-sf","rix-tt",
    "tf-bomber","tf-fig","tf-scout",
]
MISSILE_ASSETS = ["mis05", "mis400", "artifact"]
FALLBACK_ASSET = "bios_scout_upscaled"


def parse_glb(path):
    d = open(path, "rb").read()
    assert d[:4] == b"glTF", f"{path}: not a GLB"
    off = 12
    chunks = []
    while off + 8 <= len(d):
        clen = struct.unpack("<I", d[off:off + 4])[0]
        ctype = d[off + 4:off + 8]
        chunks.append((ctype, d[off + 8:off + 8 + clen]))
        off += 8 + clen + ((4 - (clen % 4)) % 4)
    gl = json.loads(chunks[0][1].decode("utf-8"))
    bin_chunk = next((data for ctype, data in chunks[1:] if ctype == b"BIN\x00"), None)
    return gl, bin_chunk


def base_color_bytes(glb_path):
    """Return the raw encoded bytes of the material's baseColor texture."""
    gl, bin_chunk = parse_glb(glb_path)
    mats = gl.get("materials", [])
    if not mats:
        raise RuntimeError("no materials in GLB")
    if len(mats) != 1:
        raise RuntimeError(f"{len(mats)} materials (expected 1)")
    bc = mats[0].get("pbrMetallicRoughness", {}).get("baseColorTexture")
    if bc is None:
        raise RuntimeError("material has no baseColorTexture")
    tex = gl["textures"][bc["index"]]
    img = gl["images"][tex["source"]]
    bv = gl["bufferViews"][img["bufferView"]]
    soff = bv.get("byteOffset", 0)
    raw = bin_chunk[soff:soff + bv["byteLength"]]
    return raw


def extract(source_glb, out_png, max_size):
    raw = base_color_bytes(source_glb)
    # Always transcode to PNG (MonoGame TextureImporter reads .png; the GLB
    # payloads are JPEG). Downscale oversized sources so a low-poly ship's
    # texture stays lean in the repo and on the GPU.
    img = Image.open(io.BytesIO(raw))
    img = img.convert("RGBA")
    if max(img.size) > max_size:
        img.thumbnail((max_size, max_size), Image.Resampling.LANCZOS)
    img.save(out_png, "PNG")
    return img.size


def main():
    meshy = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_MESHY
    out_dir = sys.argv[2] if len(sys.argv) > 2 else DEFAULT_OUT
    fallback_glb = sys.argv[3] if len(sys.argv) > 3 else DEFAULT_FALLBACK_GLB
    max_size = int(sys.argv[4]) if len(sys.argv) > 4 else 1024
    os.makedirs(out_dir, exist_ok=True)

    jobs = [(a, os.path.join(meshy, a + ".glb")) for a in SHIP_ASSETS + MISSILE_ASSETS]
    jobs.append((FALLBACK_ASSET, fallback_glb))

    ok, fail = [], []
    for asset, src in jobs:
        dst = os.path.join(out_dir, asset + ".png")
        if not os.path.exists(src):
            fail.append((asset, f"missing source {src}"))
            print(f"MISS  {asset}: source not found ({src})")
            continue
        try:
            dims = extract(src, dst, max_size)
            size = os.path.getsize(dst)
            ok.append(asset)
            print(f"OK    {asset:<26} {dims[0]}x{dims[1]}  {size} B  <- {os.path.basename(src)}")
        except Exception as e:
            fail.append((asset, str(e)))
            print(f"FAIL  {asset}: {e}")

    print(f"\nWrote {len(ok)} textures to {out_dir}; {len(fail)} failed.")
    if fail:
        for a, why in fail:
            print(f"  {a}: {why}")
        sys.exit(1)


if __name__ == "__main__":
    main()
