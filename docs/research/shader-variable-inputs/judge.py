#!/usr/bin/env python3
"""Compare a device output with C#'s reference bits: cases over the lavapipe bound 2.2e-6 max(|v|, 1), and the largest.
usage: judge.py <ref.bin> <out.bin> <label>"""
import math, struct, sys
ref, out = open(sys.argv[1], 'rb').read(), open(sys.argv[2], 'rb').read()
n = len(ref) // 16
r, o = struct.unpack('<%df' % (4 * n), ref), struct.unpack('<%df' % (4 * n), out)
over, exact, worst = 0, 0, 0.0
for i in range(n):
    s = max((abs(o[4*i+j] - r[4*i+j]) / max(abs(r[4*i+j]), 1.0) if math.isfinite(o[4*i+j]) else math.inf) for j in range(4))
    exact += ref[16*i:16*i+16] == out[16*i:16*i+16]
    over += s > 2.2e-6
    worst = max(worst, s)
print('%s: cases %d, bit-exact %d, over 2.2e-6 %d, worst %.3g' % (sys.argv[3], n, exact, over, worst))
