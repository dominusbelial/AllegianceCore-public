# P812 — headless Blender render check for exported .glb hulls.
#
#   blender -b --python render-check.py -- <in.glb> <out.png> [size]
#
# Workbench engine, Solid shading, MATCAP studio light (the P812 brief bans
# material shading — native .mdl exports carry no material data). The PNG is
# evidence only; geometry verification is numeric (mdl-to-glb roundtrip).

import math
import sys

import bpy
import mathutils

argv = sys.argv[sys.argv.index("--") + 1:]
glb_path, out_path = argv[0], argv[1]
size = int(argv[2]) if len(argv) > 2 else 1280

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=glb_path)

# World bounds over every imported mesh.
mins = mathutils.Vector((1e9, 1e9, 1e9))
maxs = mathutils.Vector((-1e9, -1e9, -1e9))
mesh_objects = [o for o in bpy.context.scene.objects if o.type == "MESH"]
assert mesh_objects, f"no meshes imported from {glb_path}"
for o in mesh_objects:
    for corner in o.bound_box:
        world = o.matrix_world @ mathutils.Vector(corner)
        mins = mathutils.Vector(map(min, mins, world))
        maxs = mathutils.Vector(map(max, maxs, world))
center = (mins + maxs) / 2
radius = max((maxs - mins).length / 2, 1e-3)

# Camera: orthographic, off-axis so the hull reads as 3-D.
cam_data = bpy.data.cameras.new("cam")
cam_data.type = "ORTHO"
cam_data.ortho_scale = radius * 2.6
cam_obj = bpy.data.objects.new("cam", cam_data)
bpy.context.scene.collection.objects.link(cam_obj)
direction = mathutils.Vector((0.55, 0.45, 0.7)).normalized()
cam_obj.location = center + direction * radius * 4
look = (center - cam_obj.location).to_track_quat("-Z", "Y")
cam_obj.rotation_euler = look.to_euler()
bpy.context.scene.camera = cam_obj

scene = bpy.context.scene
scene.render.engine = "BLENDER_WORKBENCH"
scene.display.shading.type = "SOLID"
scene.display.shading.light = "MATCAP"
scene.display.shading.color_type = "MATERIAL"
matcaps = [sl.name for sl in bpy.types.StudioLight.__subclasses__()]  # noqa: F841 (API probe)
try:
    prefs = bpy.context.preferences.addons["blender"].preferences
except Exception:
    prefs = None
if prefs is not None and hasattr(prefs, "studio_lights"):
    names = [sl.name for sl in prefs.studio_lights if sl.type == "MATCAP" and sl.name != "Default"]
    if names:
        scene.display.shading.studio_light = sorted(names)[0]
scene.display.shading.show_cavity = True

scene.render.resolution_x = size
scene.render.resolution_y = size * 3 // 4
scene.render.filepath = out_path
scene.render.image_settings.file_format = "PNG"

bpy.ops.render.render(write_still=True)
print(f"render-check: wrote {out_path} (meshes={len(mesh_objects)}, radius={radius:.3f})")
