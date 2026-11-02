#!/usr/bin/env python3
"""P921 reproducible Blender probe. Usage: python3 editor_probe.py BLENDER SCRATCH EXPORTED_FIG30_GLB
Run mdl-to-glb export first; this script only writes under SCRATCH.
"""
import json
from pathlib import Path
import struct
import subprocess
import sys

blender, scratch, fig30 = sys.argv[1:]
scratch = Path(scratch).resolve()
scratch.mkdir(parents=True, exist_ok=True)
fig30 = Path(fig30).resolve()
vertices = struct.pack('<9f3H', 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 1, 2)
probe = {
    'asset': {'version': '2.0'}, 'scene': 0,
    'scenes': [{'name': 'Default', 'nodes': [0]}, {'name': 'Levels', 'nodes': [3]}],
    'nodes': [
        {'name': 'base', 'mesh': 0, 'extensions': {'MSFT_lod': {'ids': [2]}}},
        {'name': 'LOD_0_px_64', 'children': [2],
         'extras': {'allegianceLodIndex': 0, 'allegiancePixelRadius': 64}},
        {'name': 'coarse', 'mesh': 0},
        {'name': 'outside_default', 'children': [1], 'extras': {'probe': True}}],
    'meshes': [{'name': 'triangle', 'primitives': [{'attributes': {'POSITION': 0}, 'indices': 1}]}],
    'accessors': [
        {'bufferView': 0, 'componentType': 5126, 'count': 3, 'type': 'VEC3', 'min': [0, 0, 0], 'max': [1, 1, 0]},
        {'bufferView': 1, 'componentType': 5123, 'count': 3, 'type': 'SCALAR'}],
    'bufferViews': [{'buffer': 0, 'byteLength': 36}, {'buffer': 0, 'byteOffset': 36, 'byteLength': 6}],
    'buffers': [{'byteLength': len(vertices)}], 'extensionsUsed': ['MSFT_lod']}
j = json.dumps(probe).encode()
j += b' ' * (-len(j) % 4)
vertices += b'\0' * (-len(vertices) % 4)
(scratch / 'probe.glb').write_bytes(struct.pack('<III', 0x46546c67, 2, 28+len(j)+len(vertices))
    + struct.pack('<II', len(j), 0x4e4f534a) + j + struct.pack('<II', len(vertices), 0x004e4942) + vertices)
script = scratch / 'blender_roundtrip.py'
script.write_text('''import bpy, sys
from pathlib import Path
scratch, fig30 = map(Path, sys.argv[sys.argv.index('--')+1:])
print('P921_BLENDER_VERSION', bpy.app.version_string)
for source, dest in [(scratch/'probe.glb', scratch/'probe-out.glb'), (fig30, scratch/'fig30-edited.glb')]:
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.gltf(filepath=str(source))
    print('P921_PARENTS', [(o.name, dict(o.items()), len(o.children)) for o in bpy.data.objects if o.name.startswith('LOD_')])
    bpy.ops.export_scene.gltf(filepath=str(dest), export_format='GLB', export_extras=True, export_normals=True, export_texcoords=True)
''')
subprocess.run([blender, '--background', '--factory-startup', '--python', str(script), '--', str(scratch), str(fig30)], check=True)
for name in ['probe-out.glb', 'fig30-edited.glb']:
    data = (scratch / name).read_bytes()
    doc = json.loads(data[20:20+struct.unpack_from('<I', data, 12)[0]])
    print(json.dumps({'file': name, 'scenes': doc.get('scenes'),
        'parents': [n for n in doc['nodes'] if n.get('name', '').startswith('LOD_')],
        'extensionsUsed': doc.get('extensionsUsed', [])}, indent=2))
