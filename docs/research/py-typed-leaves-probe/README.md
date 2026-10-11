# Python typed leaves: probe evidence (2026-10-11)

Serves `cultlib-gaps:ruling:py-slot-typed-leaves` and the specs
`cultlib-gaps:cut_spec:cut-leaf-vectors-cs.r1` and `cut-py-typed-leaves.r1`.
Run by the Imagination pass `imagination-cl-py-typed-leaves` on Yggdrasil at CultLib main 4ec4bfb7.

- `write_probe_stores.py` + `PyLeavesProbe.cs`: a Python store whose catalog uses the leaf type names
  (from `CultSchemaTypeNames.FromType`) is opened by the C# reference and its payload rewritten byte-identical,
  float32 included (except inside an `object` slot, where C# itself rewrites a fixint as uint8). Negative control:
  one slot named Int64 where C# has Int32 is refused ("slot 3 changed type").
- `proto_leaves.py` + `run_vectors.py`: prototype leaves reproduce the C# numeric-leaves vectors (160 reads, 46 writes,
  the mixed record). `truth.py`: what C# accepts per slot type (byte[] accepts str; bool only false/true; record
  accepts []; nil in an int slot refused).
- `perf.py`: float32 via a marker with packb-first fallback; a 1.5k-node record with float32 about 0.8 ms vs 0.1 ms.
- `job1.log`: the verify run.

Scripts are probes, not a suite; they are kept so the next pass can rerun them.
