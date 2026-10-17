#!/usr/bin/env python3
"""The strict-form harness pass of the GLSL equality gate: decorates the result of every OpFMul, OpFAdd, OpFSub,
OpVectorTimesScalar and OpDot NoContraction, so the driver may not fuse them into a multiply-add. Binary, instruction
level, reusing spvedit.py's load and mk. It lives in the gate's job-local modules only, never in shipped GLSL.

usage: nocontract.py <in.spv> <out.spv>      prints the count of decorated results"""
import importlib.util, os, struct, sys

here = os.path.dirname(os.path.abspath(__file__))
spec = importlib.util.spec_from_file_location('spvedit', os.path.join(here, '..', 'glsl', 'spvedit.py'))
spvedit = importlib.util.module_from_spec(spec)
spec.loader.exec_module(spvedit)

OP_DOT, OP_FADD, OP_FSUB, OP_FMUL, OP_VECTOR_TIMES_SCALAR = 148, 129, 131, 133, 142
FLOAT_OPS = (OP_FADD, OP_FSUB, OP_FMUL, OP_VECTOR_TIMES_SCALAR, OP_DOT)
DECORATION_NO_CONTRACTION = 42
FIRST_TYPE_OP, LAST_TYPE_OP = 19, 39        # OpTypeVoid .. OpTypeForwardPointer: the annotations end where types begin


def main(src, dst):
    h, ins = spvedit.load(src)
    done = {x[1] for x in ins if spvedit.op(x) == spvedit.OP_DECORATE and x[2] == DECORATION_NO_CONTRACTION}
    ids = [x[2] for x in ins if spvedit.op(x) in FLOAT_OPS and x[2] not in done]
    at = next((k for k, x in enumerate(ins) if FIRST_TYPE_OP <= spvedit.op(x) <= LAST_TYPE_OP), None)
    if at is None:
        sys.exit('refuse: no type section to place the decorations before')
    ins[at:at] = [spvedit.mk(spvedit.OP_DECORATE, i, DECORATION_NO_CONTRACTION) for i in ids]
    words = h + [v for x in ins for v in x]
    open(dst, 'wb').write(struct.pack('<%dI' % len(words), *words))
    print('nocontract: %d results decorated' % len(ids))


if __name__ == '__main__':
    main(*sys.argv[1:3])
