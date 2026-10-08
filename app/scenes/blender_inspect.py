"""Trusted, embedded SheepCode worker. Inspection only: no save, render, addons or user scripts."""
import json
import math
import os
import sys

import bpy


def finite_vector(values):
    return [round(float(v), 6) if math.isfinite(float(v)) else 0.0 for v in values]


def inspect(request):
    path = request["path"]
    extension = os.path.splitext(path)[1].lower()
    bpy.context.preferences.filepaths.use_scripts_auto_execute = False
    if extension == ".blend":
        bpy.ops.wm.open_mainfile(filepath=path, load_ui=False, use_scripts=False)
    elif extension == ".fbx":
        bpy.ops.object.select_all(action="SELECT")
        bpy.ops.object.delete(use_global=False)
        bpy.ops.import_scene.fbx(filepath=path, use_image_search=False, use_anim=False)
    else:
        raise ValueError("Only .blend and .fbx are accepted")

    # Inspect base mesh data. Never evaluate modifiers, drivers, scripts, materials or render hooks.
    objects = []
    vertices = []
    triangles = []
    edges = []
    total_vertices = 0
    total_faces = 0
    preview_partial = False
    for obj in sorted(bpy.context.scene.objects, key=lambda item: item.name)[:2000]:
        is_mesh = obj.type == "MESH" and obj.data is not None
        count_v = len(obj.data.vertices) if is_mesh else 0
        count_f = len(obj.data.polygons) if is_mesh else 0
        total_vertices += count_v
        total_faces += count_f
        parent = obj.parent.name if obj.parent else ""
        objects.append({"id": obj.name, "name": obj.name, "type": obj.type, "parent": parent,
                        "vertices": count_v, "faces": count_f, "position": finite_vector(obj.location),
                        "scale": finite_vector(obj.scale), "materials": [slot.material.name for slot in obj.material_slots if slot.material],
                        "components": [modifier.type for modifier in obj.modifiers],
                        "references": [obj.library.filepath] if obj.library else []})
        if not is_mesh or obj.library is not None:
            continue
        if count_v > 20000 or len(vertices) + count_v > 30000 or len(triangles) > 30000:
            preview_partial = True
            continue
        offset = len(vertices)
        vertices.extend(finite_vector(obj.matrix_world @ vertex.co) for vertex in obj.data.vertices)
        for polygon in obj.data.polygons:
            if len(triangles) >= 40000:
                preview_partial = True
                break
            indices = list(polygon.vertices)
            for index in range(1, min(len(indices) - 1, 128)):
                if len(triangles) >= 40000:
                    break
                triangles.append([offset + indices[0], offset + indices[index], offset + indices[index + 1]])
        for edge in obj.data.edges:
            if len(edges) >= 50000:
                preview_partial = True
                break
            edges.append([offset + edge.vertices[0], offset + edge.vertices[1]])
    return {"status": "inspected", "format": extension[1:], "engine": "Blender " + bpy.app.version_string,
            "objectCount": len(bpy.context.scene.objects), "meshCount": sum(obj.type == "MESH" for obj in bpy.context.scene.objects),
            "vertexCount": total_vertices, "faceCount": total_faces, "materials": [item.name for item in bpy.data.materials][:200],
            "objects": objects, "geometry": {"vertices": vertices, "triangles": triangles, "edges": edges},
            "partial": preview_partial or len(bpy.context.scene.objects) > 2000,
            "notice": "Base mesh data; modifiers/animation/drivers are not evaluated. Linked meshes and external textures are not shown in the preview. No render or save is performed."}


try:
    request = json.load(sys.stdin)
    reply = inspect(request)
except Exception as error:
    reply = {"status": "failed", "error": str(error)}
print("SHEEPCODE_SCENE_JSON=" + json.dumps(reply, ensure_ascii=True, allow_nan=False), flush=True)
