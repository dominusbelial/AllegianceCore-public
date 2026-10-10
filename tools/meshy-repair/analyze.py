"""Analyze one or more Meshy FBX ship models (P581): welded piece count, each
significant piece's size + gap to hull, crumb count, non-manifold edges,
flipped-normals estimate, and a per-model JSON dump.

Usage (inside Blender headless):
  blender --background --python tools/meshy-repair/analyze.py -- \
      <out.json> <model1.fbx> [model2.fbx ...]
"""
from __future__ import annotations
import bpy, sys, os, json
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from common import analyze_model  # noqa: E402

OUT = sys.argv[sys.argv.index("--") + 1]
FBXS = sys.argv[sys.argv.index("--") + 2:]
assert OUT.endswith(".json"), "first arg must be the output .json path"
assert FBXS, "need at least one fbx"

result = {}
for fp in FBXS:
    name = os.path.splitext(os.path.basename(fp))[0]
    try:
        result[name] = analyze_model(fp)
        print(json.dumps({name: result[name]}, indent=2))
    except Exception as e:
        result[name] = {"error": str(e)}
        print(f"[analyze] {name} FAILED: {e}")

os.makedirs(os.path.dirname(os.path.abspath(OUT)) or ".", exist_ok=True)
with open(OUT, "w") as f:
    json.dump(result, f, indent=2, sort_keys=True)
print("[analyze] wrote", OUT)
