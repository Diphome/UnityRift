# -*- coding: utf-8 -*-
# @runtime Jython
# Based on Il2CppDumper il2cpp_header_to_ghidra.py (https://github.com/Perfare/Il2CppDumper), MIT License, Copyright (c) 2016 Perfare.
# Standalone helper: UnityRift already writes il2cpp_ghidra.h next to il2cpp.h, so this is only needed
# if you want to re-run the transform yourself.
from __future__ import print_function
import os
import sys
import re

header = "typedef unsigned __int8 uint8_t;\n" \
         "typedef unsigned __int16 uint16_t;\n" \
         "typedef unsigned __int32 uint32_t;\n" \
         "typedef unsigned __int64 uint64_t;\n" \
         "typedef __int8 int8_t;\n" \
         "typedef __int16 int16_t;\n" \
         "typedef __int32 int32_t;\n" \
         "typedef __int64 int64_t;\n" \
         "typedef __int64 intptr_t;\n" \
         "typedef __int64 uintptr_t;\n" \
         "typedef unsigned __int64 size_t;\n" \
         "typedef _Bool bool;\n"


def main():
    src = "il2cpp.h"
    dst = "il2cpp_ghidra.h"
    if len(sys.argv) >= 2:
        src = sys.argv[1]
    if len(sys.argv) >= 3:
        dst = sys.argv[2]
    if not os.path.isfile(src):
        print("il2cpp.h not found: " + src)
        print("Usage: il2cpp_header_to_ghidra.py [il2cpp.h] [il2cpp_ghidra.h]")
        return
    print("il2cpp.h opened...")
    with open(src, "r") as f:
        original_header_data = f.read()
    print("il2cpp.h read...")
    fixed_header_data = re.sub(r": (\w+) {", r"{\n\t\1 super;", original_header_data)
    print("il2cpp.h data fixed...")
    with open(dst, "w") as f:
        f.write(header)
        f.write(fixed_header_data)
    print("il2cpp_ghidra.h written: " + dst)


if __name__ == "__main__":
    print("Script started...")
    main()
