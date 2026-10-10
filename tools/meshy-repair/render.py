"""Colored per-piece review render of a Meshy FBX (P581): Solid/Workbench-style
shading with per-welded-component object colours (hull grey, significant
detached pieces highlighted, crumbs dim) across 4 views (top/side/front/
perspective). Used for before/after audit visuals. Does not alter the file.

Usage (Blender headless):
  blender --background --python tools/meshy-repair/render.py -- \
      <src.fbx> <out.png> [--label TEXT]
"""
from __future__ import annotations
import bpy, bmesh, sys, os
from mathutils import Vector
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from common import import_fbx, welded_components, WELD_DIST  # noqa: E402


def key(c):
    return (round(c[0], 5), round(c[1], 5), round(c[2], 5))


COLORS = [(0.55, 0.55, 0.55, 1.0),   # hull grey
          (0.90, 0.15, 0.15, 1.0),   # piece red
          (0.95, 0.55, 0.05, 1.0),   # orange
          (0.15, 0.40, 0.95, 1.0),   # blue
          (0.10, 0.80, 0.30, 1.0),   # green
          (0.75, 0.10, 0.75, 1.0)]   # magenta


def render(src: str, out: str, label: str = ""):
    obj = import_fbx(src)
    mesh = obj.data
    bm = bmesh.new()
    bm.from_mesh(mesh)
    bmesh.ops.remove_doubles(bm, verts=list(bm.verts), dist=WELD_DIST)
    bm.verts.ensure_lookup_table(); bm.edges.ensure_lookup_table(); bm.faces.ensure_lookup_table()

    # component id per welded vert
    parent = list(range(len(bm.verts)))
    def find(a):
        while parent[a] != a:
            parent[a] = parent[parent[a]]; a = parent[a]
        return a
    def union(a, b):
        ra, rb = find(a), find(b)
        if ra != rb: parent[rb] = ra
    for e in bm.edges:
        union(e.verts[0].index, e.verts[1].index)
    cid = [find(v.index) for v in bm.verts]
    roots = sorted(set(cid), key=lambda r: -cid.count(r))
    root_color = {r: COLORS[min(i, len(COLORS) - 1)] for i, r in enumerate(roots)}

    # assign each ORIGINAL face to its component via its welded vert ids
    vert_to_root = {}
    for vi, v in enumerate(bm.verts):
        vert_to_root.setdefault(key(v.co), cid[vi])
    face_colors = []
    for f in mesh.polygons:
        k = key(mesh.vertices[f.vertices[0]].co)
        face_colors.append(root_color.get(vert_to_root.get(k, roots[0]), COLORS[0]))

    # per-component vertex color layers (Color Attribute) then split not needed:
    # bake color per face into vertex colors and shade flat with an emission mat.
    vcols = mesh.vertex_colors.new(name="PieceColor")
    for fi, f in enumerate(mesh.polygons):
        col = face_colors[fi]
        for li in range(f.loop_total):
            vcols.data[f.loop_start + li].color = col

    # materials: not needed — BLENDER_WORKBENCH renders vertex colors directly
    # via display.shading.color_type='VERTEX' (Solid/Workbench per-piece colours).

    # --- camera + lights -------------------------------------------------------
    # remove only the factory default cube/light/camera — KEEP our imported mesh
    for o in list(bpy.data.objects):
        if o.type != 'MESH':
            bpy.data.objects.remove(o, do_unlink=True)

    ctx = bpy.context
    scene = ctx.scene
    # dark backdrop for contrast (workbench uses world colour as background)
    if scene.world is None:
        scene.world = bpy.data.worlds.new("DarkWorld")
    scene.world.use_nodes = False
    try:
        scene.world.color = (0.045, 0.050, 0.062)
    except Exception:
        pass
    scene.render.engine = 'BLENDER_WORKBENCH'   # Solid/Workbench shading (brief)
    scene.render.image_settings.file_format = 'PNG'
    scene.render.resolution_x = 980
    scene.render.resolution_y = 650
    try:
        scene.display.shading.light = 'FLAT'
        scene.display.shading.color_type = 'VERTEX'
    except Exception:
        pass

    # bounds
    xs = [v.co[0] for v in mesh.vertices]; ys = [v.co[1] for v in mesh.vertices]; zs = [v.co[2] for v in mesh.vertices]
    cx, cy, cz = (max(xs)+min(xs))/2, (max(ys)+min(ys))/2, (max(zs)+min(zs))/2
    rad = max(max(xs)-min(xs), max(ys)-min(ys), max(zs)-min(zs))

    # keep our mesh object in the scene (leave transforms as imported)
    mesh_obj = None
    for o in bpy.data.objects:
        if o.type == 'MESH':
            mesh_obj = o

    def look_at_matrix(eye, target, up=(0, 0, 1)):
        import mathutils
        eye = Vector(eye); target = Vector(target)
        z = (eye - target).normalized()
        x = up if isinstance(up, Vector) else Vector(up)
        x = x.cross(z).normalized()
        y = z.cross(x)
        m = mathutils.Matrix(((x.x, y.x, z.x, 0), (x.y, y.y, z.y, 0),
                              (x.z, y.z, z.z, 0), (0, 0, 0, 1)))
        t = mathutils.Matrix.Translation(eye)
        return t @ m

    def add_view(name, eye, ortho):
        cam = bpy.data.cameras.new(name+"_cam")
        co = bpy.data.objects.new(name+"_cam", cam)
        scene.collection.objects.link(co)
        co.matrix_world = look_at_matrix(eye, (cx, cy, cz))
        cam.type = 'ORTHO' if ortho else 'PERSP'
        cam.ortho_scale = rad * 2.6
        scene.camera = co
        la = bpy.data.lights.new(name+"_sun", 'SUN')
        lo = bpy.data.objects.new(name+"_sun", la)
        scene.collection.objects.link(lo)
        lo.matrix_world = look_at_matrix(tuple(Vector(eye)*0.3), (cx, cy, cz))

    views = [
        ("top",    (cx, cy, cz + rad*2.6), True),
        ("front",  (cx, cy + rad*2.6, cz), True),
        ("side",   (cx + rad*2.6, cy, cz), True),
        ("persp",  (cx + rad*1.9, cy + rad*1.5, cz + rad*1.7), False),
    ]
    base, ext = os.path.splitext(out)
    cam_objs = []
    for name, eye, ortho in views:
        co = add_view(name, eye, ortho)
        cam_objs.append(co)
        scene.render.filepath = f"{base}_{name}{ext}"
        bpy.ops.render.render(write_still=True)
    # delete all created cameras + lights at the very end (guarded)
    for co in cam_objs:
        try:
            bpy.data.objects.remove(co, do_unlink=True)
        except Exception:
            pass
    for lo in list(bpy.data.objects):
        if lo.type == 'LIGHT':
            try:
                bpy.data.objects.remove(lo, do_unlink=True)
            except Exception:
                pass


if __name__ == "__main__":
    argv = sys.argv[sys.argv.index("--") + 1:]
    src, out = argv[0], argv[1]
    label = argv[argv.index("--label") + 1] if "--label" in argv else ""
    render(src, out, label)
