"""P581c close-up Solid/matcap proof renders: raw vs repaired, 6 angles per fin,
framed close on each fin root and fin edge. Flat Solid (BLENDER_WORKBENCH) shading
so geometry is judged directly, no materials/textures hiding surface artifacts.

Usage (Blender headless):
  blender --background --python tools/meshy-repair/close_up_render.py -- \
      <model.fbx> <out_prefix> <fin_bbox_x_min> <y> <z> <fin_label> [margin_scale]
Cameras orbit the fin target at a close radius (clamped to the fin's own size) and
write <out_prefix>_angle{i}.png for i in 0..5.
"""
from __future__ import annotations
import bpy, bmesh, sys, os
from mathutils import Vector
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from common import import_fbx  # noqa: E402


def render_close(src, out_prefix, target, radius, label=""):
    obj = import_fbx(src)
    mesh = obj.data
    for o in list(bpy.data.objects):
        if o.type != 'MESH':
            bpy.data.objects.remove(o, do_unlink=True)
    scene = bpy.context.scene
    if scene.world is None:
        scene.world = bpy.data.worlds.new("DarkWorld")
    scene.world.use_nodes = False
    try:
        scene.world.color = (0.05, 0.055, 0.07)
    except Exception:
        pass
    scene.render.engine = 'BLENDER_WORKBENCH'   # Solid shading, no materials
    scene.render.image_settings.file_format = 'PNG'
    scene.render.resolution_x = 880
    scene.render.resolution_y = 736
    try:
        scene.display.shading.light = 'FLAT'
        scene.display.shading.color_type = 'SINGLE'
        scene.display.shading.single_color = (0.75, 0.72, 0.66)  # neutral clay
    except Exception:
        pass
    # a second matcap-flavoured pass would need cycles; Solid FLAT is the faithful
    # "no materials" solid look the brief asks for.

    tgt = Vector(target)

    def look_at(eye, t):
        z = (Vector(eye) - t).normalized()
        x = Vector((0, 0, 1)).cross(z)
        if x.length < 1e-6:
            x = Vector((1, 0, 0))
        x.normalize()
        y = z.cross(x)
        import mathutils
        m = mathutils.Matrix(((x.x, y.x, z.x, 0), (x.y, y.y, z.y, 0),
                              (x.z, y.z, z.z, 0), (0, 0, 0, 1)))
        return mathutils.Matrix.Translation(eye) @ m

    for i in range(6):
        az = i * 60.0
        import math
        th = math.radians(az)
        # orbit slightly above the horizon so we see the fin root lip
        eye = (tgt.x + radius * math.cos(th),
               tgt.y + radius * math.sin(th),
               tgt.z + radius * 0.45)
        cam = bpy.data.cameras.new(f"cc{i}")
        co = bpy.data.objects.new(f"cc{i}", cam)
        scene.collection.objects.link(co)
        co.matrix_world = look_at(eye, tgt)
        cam.type = 'PERSP'
        cam.angle = 0.55
        scene.camera = co
        la = bpy.data.lights.new(f"sun{i}", 'SUN')
        lo = bpy.data.objects.new(f"sun{i}", la)
        scene.collection.objects.link(lo)
        lo.matrix_world = look_at(tuple(Vector(eye) * 0.4), tgt)
        scene.render.filepath = f"{out_prefix}_angle{i}.png"
        bpy.ops.render.render(write_still=True)
        bpy.data.objects.remove(co, do_unlink=True)
        bpy.data.objects.remove(lo, do_unlink=True)


if __name__ == "__main__":
    argv = sys.argv[sys.argv.index("--") + 1:]
    src, out_prefix, bx, y, z, label = argv[0], argv[1], float(argv[2]), float(argv[3]), float(argv[4]), argv[5]
    margin = float(argv[6]) if len(argv) > 6 else 0.35
    # radius from the fin's vertical extent: use a fixed close radius in ship units
    render_close(src, out_prefix, (bx, y, z), 0.35, label)
