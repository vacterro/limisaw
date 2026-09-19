"""Thin delegate to the canonical SAIPEN conformance gate.

The no-publish release body invokes `<python> tools/validate.py --gate core`
against THIS project; the engine itself is owned by the SAIPEN installation
(STATE.md `saipen_home`), so the gate runs from there and this file only
forwards the arguments. Keeping the shim one line of forwarding prevents the
two failure modes of a vendored copy: a stale gate validating a newer tree,
and an engine whose install-root probe resolves to the project and refuses.
"""
import os
import runpy
import sys
from pathlib import Path

home = None
state = Path(__file__).resolve().parent.parent / ".saipen" / "STATE.md"
for line in state.read_text(encoding="utf-8-sig").splitlines():
    if line.startswith("saipen_home:"):
        body = line.split(":", 1)[1].strip().strip('"')
        home = body.replace("\\\\", "\\")
        break
if not home:
    raise SystemExit("tools/validate.py: STATE.md has no saipen_home; cannot find the engine")
sys.path.insert(0, os.path.join(home, "tools"))
sys.argv[0] = os.path.join(home, "tools", "validate.py")
runpy.run_path(sys.argv[0], run_name="__main__")
