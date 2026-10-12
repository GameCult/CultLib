# SPDX-License-Identifier: GPL-3.0-or-later
#
# The Cycles oracle for CultMath's Cycles node semantics (math.Cycles.cs, shaders/CultMath.Cycles.hlsl).
# A Blender Python script, so it is GPL-3.0-or-later; it is a test tool and ships in no package.
#
#   blender --background --factory-startup --python oracle.py -- <fixture directory> [<family> ...]
#
# It refuses to run under any Blender but 5.2.2 LTS, build hash d13f752e3b9c. For each family it bakes
# the node under test with Cycles on the CPU and writes tests/CultMath.Tests/fixtures/cycles-oracle/<family>.json.
# Method: operands go into float, Non-Color input images read with Closest interpolation; one Emit bake per
# output channel (1 sample, margin 0, no denoising) writes the node's result to a float image whose pixels are
# the results, bit for bit. The fixtures are this tool's output and are never edited by hand.

import json
import os
import sys

import bpy
import numpy as np

PINNED_VERSION = "5.2.2 LTS"
PINNED_HASH = "d13f752e3b9c"
SIDE = 32
CASES = SIDE * SIDE


def refuse_other_blender():
    found = (bpy.app.version_string, bpy.app.build_hash.decode() if isinstance(bpy.app.build_hash, bytes) else bpy.app.build_hash)
    if found != (PINNED_VERSION, PINNED_HASH):
        sys.stderr.write(
            "cycles-oracle: needs Blender %s build %s, found %s build %s\n" % (PINNED_VERSION, PINNED_HASH, found[0], found[1]))
        sys.exit(2)


refuse_other_blender()

scene = bpy.context.scene
scene.render.engine = "CYCLES"
scene.cycles.device = "CPU"
scene.cycles.samples = 1
scene.cycles.use_denoising = False
bpy.ops.mesh.primitive_plane_add(size=2)
PLANE = bpy.context.active_object


def by_identifier(sockets, identifier):
    """A node's socket by identifier: several data types share one socket name."""
    for socket in sockets:
        if socket.identifier == identifier:
            return socket
    raise KeyError("no socket %s among %s" % (identifier, [s.identifier for s in sockets]))


def f32(values):
    return np.asarray(values, dtype=np.float32)


def bits(value):
    return "%08x" % int(np.asarray(value, dtype=np.float32).view(np.uint32))


def rng_for(name):
    return np.random.default_rng(int.from_bytes(name.encode(), "little") % (2**32))


def new_image(name, rgba=None):
    image = bpy.data.images.new(name, SIDE, SIDE, alpha=True, float_buffer=True)
    image.colorspace_settings.name = "Non-Color"
    image.alpha_mode = "CHANNEL_PACKED"
    if rgba is not None:
        image.pixels.foreach_set(rgba.reshape(-1))
        image.pack()
    return image


def pack_operands(columns):
    """Operand columns (CASES float32 each) to input images of three operands per image."""
    images = []
    for first in range(0, len(columns), 3):
        rgba = np.zeros((SIDE * SIDE, 4), dtype=np.float32)
        rgba[:, 3] = 1.0
        for k, column in enumerate(columns[first:first + 3]):
            rgba[:, k] = column
        images.append(new_image("operands%d" % first, rgba.reshape(SIDE, SIDE, 4)))
    return images


def operand_sockets(tree, images, count):
    """One float socket per operand, read from the input images with Closest interpolation."""
    sockets = []
    for image in images:
        texture = tree.nodes.new("ShaderNodeTexImage")
        texture.image = image
        texture.interpolation = "Closest"
        separate = tree.nodes.new("ShaderNodeSeparateColor")
        tree.links.new(texture.outputs["Color"], separate.inputs[0])
        sockets.append([separate.outputs[0], separate.outputs[1], separate.outputs[2]])
    flat = [socket for triple in sockets for socket in triple]
    return flat[:count]


def triple_socket(tree, images, first):
    """The colour made of operands first, first + 1, first + 2."""
    combine = tree.nodes.new("ShaderNodeCombineColor")
    sockets = operand_sockets(tree, images, first + 3)
    for k in range(3):
        tree.links.new(sockets[first + k], combine.inputs[k])
    return combine.outputs[0]


def bake(label, images, build, channels):
    """Bakes build(tree, images) once per output channel; returns channels float32 arrays of CASES results."""
    results = []
    for channel in range(channels):
        material = bpy.data.materials.new("%s.%d" % (label, channel))
        material.use_nodes = True
        tree = material.node_tree
        tree.nodes.clear()
        output = tree.nodes.new("ShaderNodeOutputMaterial")
        emit = tree.nodes.new("ShaderNodeEmission")
        tree.links.new(emit.outputs[0], output.inputs[0])
        sockets = build(tree, images)
        socket = sockets[channel]
        if socket.type == "RGBA" or socket.type == "VECTOR":
            separate = tree.nodes.new("ShaderNodeSeparateColor")
            tree.links.new(socket, separate.inputs[0])
            # Cycles stores a colour result channel by channel; the channel under test is red.
            tree.links.new(separate.outputs[0], emit.inputs["Color"])
        else:
            tree.links.new(socket, emit.inputs["Color"])
        target = new_image("%s.%d.out" % (label, channel))
        target_node = tree.nodes.new("ShaderNodeTexImage")
        target_node.image = target
        tree.nodes.active = target_node
        PLANE.data.materials.clear()
        PLANE.data.materials.append(material)
        bpy.context.view_layer.objects.active = PLANE
        PLANE.select_set(True)
        bpy.ops.object.bake(type="EMIT", margin=0)
        pixels = np.empty(SIDE * SIDE * 4, dtype=np.float32)
        target.pixels.foreach_get(pixels)
        results.append(pixels.reshape(SIDE * SIDE, 4)[:, 0].copy())
    return results


def vector_bake(label, images, build):
    """For a node whose result is a colour: one bake per RGB channel via Separate Color."""
    results = []
    for channel in range(3):
        def pick(tree, imgs, channel=channel):
            color = build(tree, imgs)
            separate = tree.nodes.new("ShaderNodeSeparateColor")
            tree.links.new(color, separate.inputs[0])
            return [separate.outputs[channel]]
        results.append(bake("%s.c%d" % (label, channel), images, pick, 1)[0])
    return results


# ---- operand sets ----

SPECIAL = [0.0, 1.0, -1.0, 0.5, -0.5, 2.0, -2.0, 3.0, -2.75, 0.1, -0.1, 7.5, -7.5, 1.0e-7, -1.0e-7, 100.0, -100.0, 1.0e4, 0.3, 0.7]
POSITIVE = [0.0, 1.0, 0.5, 2.0, 3.0, 0.1, 10.0, 7.5, 1.0e-3, 100.0, 0.3, 0.7, 2.718281828]
FACTOR = [0.0, 0.5, 1.0, 1.5, -0.5, 0.25, 0.75, 2.0]
COLOR_SPECIAL = [0.0, 0.25, 0.5, 0.75, 1.0, 1.25, -0.25, 0.1, 0.9, 0.3333333, 0.6666667]


def sample_columns(name, count, specials, lows_highs, pair_specials=2):
    """CASES rows of `count` operands: the specials' product for the first operands, then uniform draws."""
    rng = rng_for(name)
    rows = []
    import itertools
    lists = [specials[k] if k < len(specials) else None for k in range(count)]
    fixed = [lst for lst in lists if lst is not None]
    if fixed:
        for combo in itertools.product(*fixed):
            row = list(combo) + [None] * (count - len(combo))
            rows.append(row)
    rows = rows[:CASES]
    while len(rows) < CASES:
        rows.append([None] * count)
    columns = []
    for k in range(count):
        low, high = lows_highs[k]
        column = np.empty(CASES, dtype=np.float32)
        for r, row in enumerate(rows):
            column[r] = row[k] if row[k] is not None else rng.uniform(low, high)
        columns.append(column)
    return columns


def random_color_rows(name, extra=0):
    """CASES colours: primaries, secondaries, greys, the specials' products, then random with tied channels."""
    rng = rng_for(name)
    rows = []
    for r in COLOR_SPECIAL[:5]:
        for g in COLOR_SPECIAL[:5]:
            for b in COLOR_SPECIAL[:5]:
                rows.append((r, g, b))
    for v in COLOR_SPECIAL:
        rows.append((v, v, v))
    while len(rows) < CASES:
        c = list(rng.uniform(-0.25, 1.25, 3))
        tie = rng.integers(0, 6)
        if tie == 0:
            c[1] = c[0]
        elif tie == 1:
            c[2] = c[1]
        elif tie == 2:
            c[2] = c[0]
        rows.append(tuple(c))
    return rows[:CASES]


# ---- families ----

MATH = [
    # (function, operation, operand count, operand specials, ranges)
    ("cycles_divide", "DIVIDE", 2, [SPECIAL, SPECIAL], [(-20, 20)] * 2),
    ("cycles_power", "POWER", 2, [SPECIAL, SPECIAL], [(-4, 4), (-4, 4)]),
    ("cycles_logarithm", "LOGARITHM", 2, [SPECIAL, SPECIAL], [(0.01, 20), (0.01, 20)]),
    ("cycles_sqrt", "SQRT", 1, [SPECIAL], [(-5, 20)]),
    ("cycles_inverse_sqrt", "INVERSE_SQRT", 1, [SPECIAL], [(-5, 20)]),
    ("cycles_modulo", "MODULO", 2, [SPECIAL, SPECIAL], [(-20, 20)] * 2),
    ("cycles_floored_modulo", "FLOORED_MODULO", 2, [SPECIAL, SPECIAL], [(-20, 20)] * 2),
    ("cycles_wrap", "WRAP", 3, [SPECIAL[:10], SPECIAL[:10], SPECIAL[:10]], [(-20, 20), (-10, 10), (-10, 10)]),
    ("cycles_snap", "SNAP", 2, [SPECIAL, SPECIAL], [(-20, 20)] * 2),
    ("cycles_pingpong", "PINGPONG", 2, [SPECIAL, SPECIAL], [(-20, 20)] * 2),
    ("cycles_smooth_min", "SMOOTH_MIN", 3, [SPECIAL[:10], SPECIAL[:10], SPECIAL[:10]], [(-20, 20), (-20, 20), (-3, 3)]),
    ("cycles_smooth_max", "SMOOTH_MAX", 3, [SPECIAL[:10], SPECIAL[:10], SPECIAL[:10]], [(-20, 20), (-20, 20), (-3, 3)]),
    ("cycles_compare", "COMPARE", 3, [SPECIAL[:10], SPECIAL[:10], [0.0, 0.5, 1.0, 1.0e-7]], [(-20, 20), (-20, 20), (0, 2)]),
    ("cycles_round", "ROUND", 1, [SPECIAL], [(-20, 20)]),
    ("cycles_trunc", "TRUNC", 1, [SPECIAL], [(-20, 20)]),
    ("cycles_fract", "FRACT", 1, [SPECIAL], [(-20, 20)]),
    ("cycles_sign", "SIGN", 1, [SPECIAL], [(-20, 20)]),
]

# Operands that must be present as exact pairs whatever the random draws: the quirks the port names.
EXTRA_ROWS = {
    "cycles_power": [(-2.0, 1.0e10), (-2.0, -1.0e10), (-2.0, 3.0), (-2.0, 2.0), (-2.0, -3.0), (-2.0, 2147483648.0), (-2.0, -2147483648.0),
                     (-8.0, 0.3333333), (2.0, 0.0), (0.0, 0.0), (0.0, 2.0), (-0.0, -3.0), (-1.5, 17.0), (-1.5, 16777217.0)],
    "cycles_modulo": [(0.3, 0.1), (-0.3, 0.1), (5.5, -2.0), (-5.5, -2.0), (1.0, 0.0), (1.0e7, 3.0), (7.0, 7.0), (-7.0, 7.0)],
}

MAP_RANGE = [
    ("cycles_map_range_linear", "LINEAR"),
    ("cycles_map_range_stepped", "STEPPED"),
    ("cycles_map_range_smoothstep", "SMOOTHSTEP"),
    ("cycles_map_range_smootherstep", "SMOOTHERSTEP"),
]

MIX_MODES = [
    ("cycles_mix_blend", "MIX"), ("cycles_mix_add", "ADD"), ("cycles_mix_mul", "MULTIPLY"), ("cycles_mix_screen", "SCREEN"),
    ("cycles_mix_overlay", "OVERLAY"), ("cycles_mix_sub", "SUBTRACT"), ("cycles_mix_div", "DIVIDE"), ("cycles_mix_diff", "DIFFERENCE"),
    ("cycles_mix_exclusion", "EXCLUSION"), ("cycles_mix_dark", "DARKEN"), ("cycles_mix_light", "LIGHTEN"), ("cycles_mix_dodge", "DODGE"),
    ("cycles_mix_burn", "BURN"), ("cycles_mix_hue", "HUE"), ("cycles_mix_sat", "SATURATION"), ("cycles_mix_val", "VALUE"),
    ("cycles_mix_color", "COLOR"), ("cycles_mix_soft", "SOFT_LIGHT"), ("cycles_mix_linear", "LINEAR_LIGHT"),
]


def with_extra(function, columns, count):
    extra = EXTRA_ROWS.get(function, [])
    for r, row in enumerate(extra):
        for k in range(count):
            columns[k][CASES - 1 - r] = row[k] if k < len(row) else 0.0
    return columns


def encode_cases(columns, results):
    cases = []
    for r in range(CASES):
        cases.append("%s -> %s" % (" ".join(bits(c[r]) for c in columns), " ".join(bits(x[r]) for x in results)))
    return cases


def family_math():
    functions = []
    for function, operation, count, specials, ranges in MATH:
        columns = with_extra(function, sample_columns(function, count, specials, ranges), count)
        images = pack_operands(columns)

        def build(tree, imgs, operation=operation, count=count):
            node = tree.nodes.new("ShaderNodeMath")
            node.operation = operation
            sockets = operand_sockets(tree, imgs, count)
            for k in range(count):
                tree.links.new(sockets[k], node.inputs[k])
            return [node.outputs[0]]

        functions.append({"function": function, "node": "ShaderNodeMath " + operation, "cases": encode_cases(columns, bake(function, images, build, 1))})
    return functions


def family_map_range():
    functions = []
    for function, kind in MAP_RANGE:
        count = 6
        specials = [SPECIAL[:10], SPECIAL[:5], SPECIAL[:5]]
        columns = sample_columns(function, count, specials, [(-20, 20), (-10, 10), (-10, 10), (-20, 20), (-20, 20), (-1, 8)])
        # Rows with From Min equal to From Max, and with From Min above From Max, whatever the draws.
        for r in range(8):
            columns[2][CASES - 1 - r] = columns[1][CASES - 1 - r]
        for r in range(8, 16):
            columns[1][CASES - 1 - r], columns[2][CASES - 1 - r] = max(columns[1][CASES - 1 - r], columns[2][CASES - 1 - r]), min(columns[1][CASES - 1 - r], columns[2][CASES - 1 - r])
        for r in range(16, 24):
            columns[5][CASES - 1 - r] = float(r - 18)
        images = pack_operands(columns)

        def build(tree, imgs, kind=kind):
            node = tree.nodes.new("ShaderNodeMapRange")
            node.data_type = "FLOAT"
            node.interpolation_type = kind
            node.clamp = False
            sockets = operand_sockets(tree, imgs, 6)
            for k, identifier in enumerate(["Value", "From_Min_FLOAT", "From_Max_FLOAT", "To_Min_FLOAT", "To_Max_FLOAT", "Steps_FLOAT"]):
                socket = by_identifier(node.inputs, identifier)
                tree.links.new(sockets[k], socket)
            return [node.outputs["Result"]]

        functions.append({"function": function, "node": "ShaderNodeMapRange " + kind, "cases": encode_cases(columns, bake(function, images, build, 1))})
        # The Clamp option, on the same operands: a Clamp node of type Range on the result.
        def build_clamped(tree, imgs, kind=kind):
            node = tree.nodes.new("ShaderNodeMapRange")
            node.data_type = "FLOAT"
            node.interpolation_type = kind
            node.clamp = True
            sockets = operand_sockets(tree, imgs, 6)
            for k, identifier in enumerate(["Value", "From_Min_FLOAT", "From_Max_FLOAT", "To_Min_FLOAT", "To_Max_FLOAT", "Steps_FLOAT"]):
                socket = by_identifier(node.inputs, identifier)
                tree.links.new(sockets[k], socket)
            return [node.outputs["Result"]]

        functions.append({"function": function, "node": "ShaderNodeMapRange " + kind + " with Clamp",
                          "then": "cycles_clamp_range(result, to_min, to_max)",
                          "cases": encode_cases(columns, bake(function + ".clamped", images, build_clamped, 1))})
    return functions


def family_clamp():
    functions = []
    for function, clamp_type in (("cycles_clamp_minmax", "MINMAX"), ("cycles_clamp_range", "RANGE")):
        columns = sample_columns(function, 3, [SPECIAL[:10], SPECIAL[:10], SPECIAL[:10]], [(-20, 20)] * 3)
        images = pack_operands(columns)

        def build(tree, imgs, clamp_type=clamp_type):
            node = tree.nodes.new("ShaderNodeClamp")
            node.clamp_type = clamp_type
            sockets = operand_sockets(tree, imgs, 3)
            for k in range(3):
                tree.links.new(sockets[k], node.inputs[k])
            return [node.outputs[0]]

        functions.append({"function": function, "node": "ShaderNodeClamp " + clamp_type, "cases": encode_cases(columns, bake(function, images, build, 1))})
    return functions


def mix_columns(name, blend):
    rng = rng_for(name)
    colors = random_color_rows(name)
    colors2 = random_color_rows(name + "2")
    rng.shuffle(colors2)
    factors = [FACTOR[r % len(FACTOR)] if r < 8 * 64 else float(rng.uniform(-0.5, 1.5)) for r in range(CASES)]
    columns = [f32(factors)]
    columns += [f32([c[k] for c in colors]) for k in range(3)]
    columns += [f32([c[k] for c in colors2]) for k in range(3)]
    return columns


def family_mix():
    functions = []
    rng = rng_for("mix-float")
    # Mix, Float: a and b in the usual ranges, the factor as in the blend modes.
    columns = [f32([FACTOR[r % len(FACTOR)] if r < 512 else rng.uniform(-0.5, 1.5) for r in range(CASES)]),
               f32(rng.uniform(-20, 20, CASES)), f32(rng.uniform(-20, 20, CASES))]
    images = pack_operands(columns)

    def build_float(tree, imgs):
        node = tree.nodes.new("ShaderNodeMix")
        node.data_type = "FLOAT"
        node.clamp_factor = False
        node.clamp_result = False
        sockets = operand_sockets(tree, imgs, 3)
        for k, identifier in enumerate(["Factor_Float", "A_Float", "B_Float"]):
            tree.links.new(sockets[k], by_identifier(node.inputs, identifier))
        return [by_identifier(node.outputs, "Result_Float")]

    functions.append({"function": "cycles_mix_float", "node": "ShaderNodeMix FLOAT", "cases": encode_cases(columns, bake("mix_float", images, build_float, 1))})

    columns = mix_columns("mix-vector", "VECTOR")
    images = pack_operands(columns)

    def build_vector(tree, imgs):
        node = tree.nodes.new("ShaderNodeMix")
        node.data_type = "VECTOR"
        node.factor_mode = "UNIFORM"
        node.clamp_factor = False
        sockets = operand_sockets(tree, imgs, 1)
        tree.links.new(sockets[0], by_identifier(node.inputs, "Factor_Float"))
        tree.links.new(triple_socket(tree, imgs, 1), by_identifier(node.inputs, "A_Vector"))
        tree.links.new(triple_socket(tree, imgs, 4), by_identifier(node.inputs, "B_Vector"))
        return by_identifier(node.outputs, "Result_Vector")

    functions.append({"function": "cycles_mix_vector", "node": "ShaderNodeMix VECTOR UNIFORM", "cases": encode_cases(columns, vector_bake("mix_vector", images, build_vector))})

    for function, blend in MIX_MODES:
        columns = mix_columns(function, blend)
        images = pack_operands(columns)

        def build(tree, imgs, blend=blend):
            node = tree.nodes.new("ShaderNodeMix")
            node.data_type = "RGBA"
            node.blend_type = blend
            node.clamp_factor = False
            node.clamp_result = False
            sockets = operand_sockets(tree, imgs, 1)
            tree.links.new(sockets[0], by_identifier(node.inputs, "Factor_Float"))
            tree.links.new(triple_socket(tree, imgs, 1), by_identifier(node.inputs, "A_Color"))
            tree.links.new(triple_socket(tree, imgs, 4), by_identifier(node.inputs, "B_Color"))
            return by_identifier(node.outputs, "Result_Color")

        functions.append({"function": function, "node": "ShaderNodeMix RGBA " + blend, "cases": encode_cases(columns, vector_bake(function, images, build))})
    return functions


def family_rgb_to_bw():
    rows = random_color_rows("rgb_to_bw")
    columns = [f32([c[k] for c in rows]) for k in range(3)]
    # One case per primary pins the scene-linear coefficients.
    for k in range(3):
        for j in range(3):
            columns[j][CASES - 1 - k] = 1.0 if j == k else 0.0
    images = pack_operands(columns)

    def build(tree, imgs):
        node = tree.nodes.new("ShaderNodeRGBToBW")
        tree.links.new(triple_socket(tree, imgs, 0), node.inputs["Color"])
        return [node.outputs["Val"]]

    return [{"function": "cycles_rgb_to_bw", "node": "ShaderNodeRGBToBW", "cases": encode_cases(columns, bake("rgb_to_bw", images, build, 1))}]


RAMP_SIZE = 256
RAMP_STOPS = [(0.0, 0.0), (0.3, 1.0), (1.0, 0.25)]


def family_ramp():
    rng = rng_for("ramp")
    # The table: one case per entry, at a t whose float32 product with 255 is the entry's index, so the
    # lookup returns the entry itself.
    table_t = []
    for k in range(RAMP_SIZE):
        base = np.float32(k) / np.float32(RAMP_SIZE - 1)
        found = None
        candidate = base
        for _ in range(8):
            if np.float32(candidate * np.float32(RAMP_SIZE - 1)) == np.float32(k):
                found = candidate
                break
            candidate = np.nextafter(candidate, np.float32(2.0))
        if found is None:
            candidate = base
            for _ in range(8):
                candidate = np.nextafter(candidate, np.float32(-1.0))
                if np.float32(candidate * np.float32(RAMP_SIZE - 1)) == np.float32(k):
                    found = candidate
                    break
        if found is None:
            raise RuntimeError("no float32 t reads ramp entry %d exactly" % k)
        table_t.append(found)
    specials = [-0.5, -0.25, -1.0e-7, 0.0, 1.0e-7, 0.001, 0.002, 0.5, 0.25, 0.999, 1.0, 1.0000001, 1.25, 1.5, 0.3, 0.29999998, 0.30000004]
    values = list(table_t) + specials
    while len(values) < CASES:
        values.append(float(rng.uniform(-0.25, 1.25)))
    column = f32(values[:CASES])
    images = pack_operands([column])

    def build(tree, imgs):
        node = tree.nodes.new("ShaderNodeValToRGB")
        node.color_ramp.interpolation = "LINEAR"
        elements = node.color_ramp.elements
        while len(elements) < len(RAMP_STOPS):
            elements.new(0.5)
        for element, (position, red) in zip(elements, RAMP_STOPS):
            element.position = position
            element.color = (red, 0.0, 0.0, 1.0)
        tree.links.new(operand_sockets(tree, imgs, 1)[0], node.inputs["Fac"])
        return [node.outputs["Color"]]

    return [{"function": "cycles_ramp_index", "node": "ShaderNodeValToRGB LINEAR size %d stops %s" % (RAMP_SIZE, json.dumps(RAMP_STOPS)),
             "table_entries": RAMP_SIZE, "cases": encode_cases([column], bake("ramp", images, build, 1))}]


FAMILIES = {
    "math": family_math,
    "map-range": family_map_range,
    "clamp": family_clamp,
    "mix": family_mix,
    "rgb-to-bw": family_rgb_to_bw,
    "ramp": family_ramp,
}


def identity_check():
    """The bake must return an operand image bit for bit, or no fixture means anything."""
    rng = np.random.default_rng(1)
    column = f32(rng.uniform(-200, 200, CASES))
    images = pack_operands([column])

    def build(tree, imgs):
        return [operand_sockets(tree, imgs, 1)[0]]

    got = bake("identity", images, build, 1)[0]
    if not np.array_equal(got.view(np.uint32), column.view(np.uint32)):
        sys.stderr.write("cycles-oracle: the bake does not return its operands bit for bit\n")
        sys.exit(3)


def kernel_check():
    """The CPU kernel must not fuse multiply-adds.

    Cycles' AVX2 CPU kernel is compiled with FMA, so a - floor(a / b) b is one fused operation there and
    10000 mod 0.1 is -1.5e-4; the SSE4.2 kernel has none, and gives the unfused result 0 that CultMath's C#
    computes. The fixtures pin the SSE4.2 kernel: run with CYCLES_CPU_NO_AVX2=1.
    """
    a = f32([10000.0] * CASES)
    b = f32([0.1] * CASES)
    unfused = a - np.floor(a / b) * b
    images = pack_operands([a, b])

    def build(tree, imgs):
        node = tree.nodes.new("ShaderNodeMath")
        node.operation = "FLOORED_MODULO"
        sockets = operand_sockets(tree, imgs, 2)
        tree.links.new(sockets[0], node.inputs[0])
        tree.links.new(sockets[1], node.inputs[1])
        return [node.outputs[0]]

    got = bake("kernel", images, build, 1)[0]
    if not np.array_equal(got.view(np.uint32), unfused.astype(np.float32).view(np.uint32)):
        sys.stderr.write("cycles-oracle: this Cycles CPU kernel fuses multiply-adds; run with CYCLES_CPU_NO_AVX2=1\n")
        sys.exit(4)


def main():
    argv = sys.argv[sys.argv.index("--") + 1:]
    out = argv[0]
    wanted = argv[1:] or list(FAMILIES)
    os.makedirs(out, exist_ok=True)
    identity_check()
    kernel_check()
    header = {
        "oracle": "tools/cycles-oracle/oracle.py (Blender %s, build %s, Cycles CPU, 1 sample, Emit bake, float Non-Color images)" % (PINNED_VERSION, PINNED_HASH),
        "blender": PINNED_VERSION,
        "build_hash": PINNED_HASH,
        "platform": bpy.app.build_platform.decode() if isinstance(bpy.app.build_platform, bytes) else str(bpy.app.build_platform),
        "encoding": "each case is 'operands -> results', every scalar a float32 bit pattern in hex, colours flattened as r g b",
    }
    for family in wanted:
        document = dict(header)
        document["family"] = family
        document["functions"] = FAMILIES[family]()
        text = json.dumps(document, indent=1) + "\n"
        with open(os.path.join(out, family + ".json"), "w", newline="\n") as handle:
            handle.write(text)
        print("cycles-oracle: wrote", family, len(document["functions"]), "functions")


main()
