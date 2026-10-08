#!/usr/bin/env python3
"""P576 (A0) — decode Allegiance native sky art.

Two jobs:
  1. Decode the environment/planet texture `.mdl` files (RGB565 BinarySurfaceInfo)
     into individual PNGs and labelled contact sheets (all 10 environments and
     all 43 planets).  These are Domingo's reference sheets.
  2. Decode a globe dome mesh `globeN.mdl` and print its geometry stats
     (bounds, vertex/triangle counts, texcoord extents, winding) so the dome
     geometry is documented for the port.

Faithful port of:
  - texture half:  MdlTextureDecoder.cs / reference/decode_texture.py
  - mesh half:     MdlParser.cs (object-graph token machine -> MeshGeo)
Native references for the sky draw rules live in the P576 report.

Usage:
  decode_sky.py textures <ArtworkDir> <OutDir>
  decode_sky.py dome     <path/to/globe1.mdl>
"""
import os
import struct
import sys
import zlib

import numpy as np
from PIL import Image, ImageDraw

MAGIC = 0x0DEBADF00


def read_cstr(data, pos):
    end = data.index(b"\x00", pos)
    s = data[pos:end].decode()
    length = (end - pos) + 1
    padded = (length + 3) & ~3
    return s, pos + padded


# ---------------------------------------------------------------------------
# Texture (.bmp .mdl) decode -> RGBA uint8 array (top-row-first)
# ---------------------------------------------------------------------------
def decode_texture(data):
    magic, _ver = struct.unpack_from("<2I", data, 0)
    assert magic == MAGIC, f"bad magic {magic:#x}"
    n_ns, n_imp, n_exp, _n_def = struct.unpack_from("<4I", data, 8)
    pos = 24
    for _ in range(n_ns):
        _, pos = read_cstr(data, pos)
    for _ in range(n_imp):
        pos += 4
        _, pos = read_cstr(data, pos)
    exports = []
    for _ in range(n_exp):
        s, pos = read_cstr(data, pos)
        exports.append(s)
    pos += 4  # def index
    pos += 4  # ObjectImport token
    pos += 4  # import index
    pos += 4  # ObjectBinary token
    size_x, size_y, pitch, bitcount, redmask, greenmask, bluemask, alphamask = \
        struct.unpack_from("<8i", data, pos)
    pos += 36  # BinarySurfaceInfo (32 + 1 colorkey + 3 pad)
    assert bitcount == 16, f"bitcount {bitcount}"
    raw = np.frombuffer(data, dtype="<u2", count=size_x * size_y, offset=pos) \
        .reshape(size_y, size_x)
    r = (((raw & redmask) >> 11).astype(np.float32) / 31.0 * 255.0).round().astype(np.uint8)
    g = (((raw & greenmask) >> 5).astype(np.float32) / 63.0 * 255.0).round().astype(np.uint8)
    b = ((raw & bluemask).astype(np.float32) / 31.0 * 255.0).round().astype(np.uint8)
    a = np.full_like(r, 255)
    rgba = np.dstack([r, g, b, a])
    rgba = np.flipud(rgba)  # reference flips vertically (row 0 = top)
    return size_x, size_y, rgba


def label_contact(items, cols, out_path, cell, label=True, thumb_w=None):
    """Montage a list of (title, rgba_array) into a grid contact sheet."""
    n = len(items)
    rows = (n + cols - 1) // cols
    cw, ch = cell
    tw = thumb_w if thumb_w else cw
    th = int(ch * (tw / cw))
    title_h = 26 if label else 0
    W, H = cols * cw, rows * (th + title_h)
    canvas = Image.new("RGB", (W, H), (10, 10, 12))
    dr = ImageDraw.Draw(canvas)
    for idx, (title, rgba) in enumerate(items):
        r, c = divmod(idx, cols)
        img = Image.fromarray(rgba, "RGBA").convert("RGB")
        img = img.resize((tw, th), Image.LANCZOS)
        x, y = c * cw, r * (th + title_h)
        if label:
            dr.text((x + 4, y + 4), title, fill=(220, 220, 220))
            y += title_h
        canvas.paste(img, (x, y))
    canvas.save(out_path)
    return out_path


# ---------------------------------------------------------------------------
# Globe dome mesh decode (object-graph token machine, MdlParser.cs port)
# ---------------------------------------------------------------------------
OBJ_END, OBJ_FLOAT, OBJ_STRING, OBJ_TRUE, OBJ_FALSE, OBJ_LIST = 0, 1, 2, 3, 4, 5
OBJ_APPLY, OBJ_BINARY, OBJ_REFERENCE, OBJ_IMPORT, OBJ_PAIR = 6, 7, 8, 9, 10


class Node:  # opaque marker base
    pass


def parse_mesh(data):
    pos = 0
    def ru():
        nonlocal pos
        v = struct.unpack_from("<I", data, pos)[0]
        pos += 4
        return v
    def rs():
        nonlocal pos
        s, pos = read_cstr(data, pos)
        return s

    magic = ru()
    assert magic == MAGIC, f"bad magic {magic:#x}"
    ru()  # version
    n_ns, n_imp, n_exp, n_def = ru(), ru(), ru(), ru()
    ns = [rs() for _ in range(n_ns)]
    imps = []
    for _ in range(n_imp):
        ru()
        imps.append(rs())
    exps = [rs() for _ in range(n_exp)]

    objects = {}

    def read_obj():
        nonlocal pos
        stack = []
        while True:
            tok = ru()
            if tok == OBJ_END:
                return stack.pop()
            elif tok == OBJ_FLOAT:
                stack.append(struct.unpack_from("<f", data, pos)[0]); pos += 4
            elif tok == OBJ_STRING:
                stack.append(rs())
            elif tok == OBJ_TRUE:
                stack.append(True)
            elif tok == OBJ_FALSE:
                stack.append(False)
            elif tok == OBJ_LIST:
                n = ru()
                stack.append([read_obj() for _ in range(n)])
            elif tok == OBJ_APPLY:
                fn = stack.pop()
                stack.append(apply(fn, stack))
            elif tok == OBJ_BINARY:
                fn = stack.pop()
                stack.append(read_binary(fn))
            elif tok == OBJ_REFERENCE:
                stack.append(objects[ru()])
            elif tok == OBJ_IMPORT:
                stack.append(imps[ru()])
            elif tok == OBJ_PAIR:
                b = stack.pop(); a = stack.pop()
                stack.append((a, b))
            else:
                raise RuntimeError(f"bad token {tok}")

    def fnname(fn):
        return fn if isinstance(fn, str) else None

    def apply(fn, stack):
        name = fnname(fn)
        if name == "GroupGeo":
            return ("GroupGeo", [stack.pop()])
        if name == "TextureGeo":
            geo = stack.pop(); image = stack.pop()
            return ("TextureGeo", image, geo)
        if name == "LODGeo":
            geo = stack.pop(); stack.pop()
            return geo
        if name == "ModifiableNumber":
            return ("ModNum", stack.pop())
        if name == "LightsGeo":
            stack.pop(); return ("LightsGeo",)
        # unknown apply -> pop one arg, passthrough
        if stack:
            stack.pop()
        return ("Apply<" + str(name) + ">",)

    def read_binary(fn):
        nonlocal pos
        name = fnname(fn)
        if name == "MeshGeo":
            cv = ru(); ci = ru()
            verts = struct.unpack_from("<%df" % (8 * cv), data, pos)
            pos += 8 * cv * 4
            idxs = struct.unpack_from("<%dH" % ci, data, pos)
            pos += ci * 2
            return ("MeshGeo", np.array(verts, dtype=np.float32).reshape(-1, 8),
                    np.array(idxs, dtype=np.uint16))
        if name == "FrameData":
            cnt = ru()
            frames = []
            for _ in range(cnt):
                nm = rs()
                p = struct.unpack_from("<3f", data, pos); pos += 12
                f = struct.unpack_from("<3f", data, pos); pos += 12
                u = struct.unpack_from("<3f", data, pos); pos += 12
                frames.append((nm, p, f, u))
            return ("FrameData", frames)
        if name == "LightsGeo":
            cnt = ru(); pos += 48 * cnt
            return ("LightsGeo",)
        raise RuntimeError(f"no Read for {name}")

    for _ in range(n_exp + n_def):
        idx = ru()
        objects[idx] = read_obj()

    def collect(node, out):
        if node is None:
            return
        if isinstance(node, tuple):
            if node[0] == "MeshGeo":
                out.append(node)
            elif node[0] == "GroupGeo":
                for c in node[1]:
                    collect(c, out)
            elif node[0] == "TextureGeo":
                collect(node[2], out)
            elif node[0] == "LODGeo":
                collect(node[1], out)
            elif isinstance(node[0], list):  # degenerate
                for c in node[0]:
                    collect(c, out)
        elif isinstance(node, list):
            for c in node:
                collect(c, out)

    meshes = []
    for i, name in enumerate(exps):
        if name == "object" and i in objects:
            collect(objects[i], meshes)
    return ns, imps, exps, meshes


def dump_dome(path):
    data = open(path, "rb").read()
    ns, imps, exps, meshes = parse_mesh(data)
    print(f"file: {os.path.basename(path)}")
    print(f"  namespaces: {ns}")
    print(f"  imports:    {imps}")
    print(f"  exports:    {exps}")
    print(f"  meshes under 'object': {len(meshes)}")
    for mi, m in enumerate(meshes):
        verts, idxs = m[1], m[2]
        pos = verts[:, 0:3]
        uv = verts[:, 3:5]
        nrm = verts[:, 5:8]
        mn = pos.min(axis=0)
        mx = pos.max(axis=0)
        rad = np.linalg.norm(pos, axis=1).max()
        print(f"  mesh[{mi}]: verts={len(verts)} tris={len(idxs)//3}")
        print(f"    pos   min={mn.round(4).tolist()} max={mx.round(4).tolist()} "
              f"|pos|max={rad:.4f}")
        print(f"    uv    min={uv.min(axis=0).round(4).tolist()} "
              f"max={uv.max(axis=0).round(4).tolist()}")
        print(f"    norm  min={nrm.min(axis=0).round(4).tolist()} "
              f"max={nrm.max(axis=0).round(4).tolist()}")
        # winding sample
        if len(idxs) >= 3:
            v0 = pos[idxs[0]]; v1 = pos[idxs[1]]; v2 = pos[idxs[2]]
            nrm_calc = np.cross(v1 - v0, v2 - v0)
            outward = (v0 + v1 + v2) / 3.0
            facing = np.dot(nrm_calc, outward)
            print(f"    first-tri winding: cross·outward={facing:.4f} "
                  f"({'CW(outward-facing)' if facing > 0 else 'CCW(inward-facing?)'})")
        print(f"    first 8 indices: {idxs[:8].tolist()}")


def cmd_textures(artwork, outdir):
    os.makedirs(outdir, exist_ok=True)
    envs = sorted([f for f in os.listdir(artwork)
                   if f.startswith("environment") and f.endswith(".mdl")],
                  key=lambda s: int(''.join(ch for ch in s if ch.isdigit()) or 0))
    planets = sorted([f for f in os.listdir(artwork)
                      if f.startswith("nebplnt") and f.endswith(".mdl")],
                     key=lambda s: int(''.join(ch for ch in s if ch.isdigit()) or 0))
    print(f"environments: {len(envs)}  planets: {len(planets)}")

    env_items = []
    for f in envs:
        w, h, rgba = decode_texture(open(os.path.join(artwork, f), "rb").read())
        stem = os.path.splitext(f)[0]
        img = Image.fromarray(rgba, "RGBA")
        img.save(os.path.join(outdir, stem + ".png"))
        env_items.append((stem, rgba))
        print(f"  {f}: {w}x{h} (RGB565)")
    label_contact(env_items, 5, os.path.join(outdir, "contact_environments.png"),
                  cell=(512, 512), label=True, thumb_w=480)
    label_contact(env_items, 5, os.path.join(outdir, "contact_environments_full.png"),
                  cell=(1024, 1024), label=False, thumb_w=1000)

    pl_items = []
    for f in planets:
        raw = open(os.path.join(artwork, f), "rb").read()
        stem = os.path.splitext(f)[0]
        if raw[:4] == b"use ":          # ASCII stub -> real texture is the PNG
            png = os.path.join(artwork, stem.replace("bmp", "") + ".png")
            img = Image.open(png).convert("RGBA")
            w, h = img.size
            rgba = np.array(img)
            print(f"  {f}: {w}x{h} (PNG stub)")
        else:
            w, h, rgba = decode_texture(raw)
            img = Image.fromarray(rgba, "RGBA")
            img.save(os.path.join(outdir, stem + ".png"))
            print(f"  {f}: {w}x{h}")
        pl_items.append((stem, rgba))
    label_contact(pl_items, 8, os.path.join(outdir, "contact_planets.png"),
                  cell=(256, 256), label=True, thumb_w=240)
    label_contact(pl_items, 8, os.path.join(outdir, "contact_planets_full.png"),
                  cell=(512, 512), label=False, thumb_w=500)
    print("contact sheets ->", outdir)


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(1)
    if sys.argv[1] == "textures":
        cmd_textures(sys.argv[2], sys.argv[3])
    elif sys.argv[1] == "dome":
        dump_dome(sys.argv[2])
    else:
        print(__doc__)
        sys.exit(1)
