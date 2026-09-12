# -*- coding: utf-8 -*-
# @category IL2CPP
# @menupath Tools.IL2CPP.FixAnalysis
# Repairs the classic IL2CPP auto-analysis trap: Ghidra flags a runtime init helper
# (il2cpp_codegen_initialize_method / il2cpp_runtime_class_init / object_new / GC_*) as
# "no-return", which truncates every function that calls it near the top, so the real
# body (and its xrefs) never gets disassembled.
#
# This clears the bogus no-return flag on those helpers and re-disassembles the
# call-site fall-throughs it had cut off. Safe to run repeatedly. Dual-runtime
# (Ghidra Jython 2.7 and 11.3+ PyGhidra); uses only the flat API + Java classes.
#
# Run it once right after import/auto-analysis (before or after ghidra.py). If you have
# already applied names with ghidra.py, more helpers are matched by name.
from __future__ import print_function

# Substrings (case-insensitive) of runtime helpers that should NOT be no-return.
# libil2cpp.so / GameAssembly.dll export several of these, so matching works even
# before ghidra.py renames anything.
NAME_HINTS = [
    "il2cpp_codegen_initialize",
    "il2cpp_runtime_class_init",
    "il2cpp_vm_metadatacache",
    "il2cpp_object_new",
    "il2cpp_codegen_object_new",
    "il2cpp_vm_object_new",
    "initializeruntimemetadata",
    "il2cpp_gc_",
    "gc_",
]

fm = currentProgram.getFunctionManager()
listing = currentProgram.getListing()


def matches(fn):
    n = fn.getName().lower()
    for h in NAME_HINTS:
        if h in n:
            return True
    return False


def main():
    fixed = []
    for fn in fm.getFunctions(True):
        if fn.hasNoReturn() and matches(fn):
            fn.setNoReturn(False)
            fixed.append(fn)
    print("[fix] cleared no-return on %d init helper(s)" % len(fixed))
    if not fixed:
        print("[fix] nothing to do (helpers not marked no-return, or not yet named).")
        return

    from ghidra.app.cmd.disassemble import DisassembleCommand
    repaired = 0
    for fn in fixed:
        for ref in getReferencesTo(fn.getEntryPoint()):
            if not ref.getReferenceType().isCall():
                continue
            instr = listing.getInstructionAt(ref.getFromAddress())
            if instr is None:
                continue
            fall = instr.getFallThrough()
            if fall is None:
                continue
            if listing.getInstructionAt(fall) is None and listing.getDataAt(fall) is None:
                DisassembleCommand(fall, None, True).applyTo(currentProgram)
                repaired += 1
    print("[fix] re-disassembled %d truncated call-site(s)." % repaired)
    print("[fix] done. Re-run Ghidra auto-analysis if functions still look incomplete.")


if __name__ == "__main__":
    main()
