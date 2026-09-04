# -*- coding: utf-8 -*-
# @category IL2CPP
# @menupath Tools.IL2CPP.ApplyNamesAndTypes
# Based on Il2CppDumper ghidra_with_struct.py (https://github.com/Perfare/Il2CppDumper), MIT License, Copyright (c) 2016 Perfare.
# Adapted for both Ghidra Jython 2.7 and Ghidra 11.3+ PyGhidra (Python 3).
# Consumes script.json + il2cpp_ghidra.h produced by AssetStudioMod '-m il2cpp'.
from __future__ import print_function

import json

from ghidra.app.util.cparser.C import CParserUtils
from ghidra.app.cmd.function import ApplyFunctionSignatureCmd

processFields = [
	"ScriptMethod",
	"ScriptString",
	"ScriptMetadata",
	"ScriptMetadataMethod",
	"Addresses",
]

functionManager = currentProgram.getFunctionManager()
baseAddress = currentProgram.getImageBase()
USER_DEFINED = ghidra.program.model.symbol.SourceType.USER_DEFINED

def ghidra_str(s):
	# Jython 2: unicode -> utf-8 str. PyGhidra/Python 3: keep unicode str (bytes break createLabel / CParser).
	if s is None:
		return ""
	try:
		unicode  # noqa: F821  (Python 2)
	except NameError:
		if isinstance(s, bytes):
			return s.decode("utf-8", "replace")
		return s
	if isinstance(s, unicode):  # noqa: F821
		try:
			return s.encode("utf-8")
		except Exception:
			return str(s)
	return s

def sanitize_name(name):
	name = ghidra_str(name).replace(" ", "-")
	for ch in "<>:`":
		name = name.replace(ch, "_")
	return name

def file_path(f):
	if f is None:
		return None
	p = getattr(f, "absolutePath", None)
	if p:
		return p
	getter = getattr(f, "getAbsolutePath", None)
	return getter() if getter else str(f)

def get_addr(addr):
	return baseAddress.add(int(addr))

def set_name(addr, name):
	try:
		createLabel(addr, sanitize_name(name), True, USER_DEFINED)
	except Exception:
		print("set_name() Failed.")

def set_type(addr, type_name):
	# Requires types (il2cpp_ghidra.h) to be imported first via File > Parse C Source...
	newType = ghidra_str(type_name).replace("*", " *").replace("  ", " ").strip()
	dataTypes = getDataTypes(newType)
	addrType = None
	if len(dataTypes) == 0:
		if newType.endswith(" *"):
			baseType = newType[:-2]
			dataTypes = getDataTypes(baseType)
			if len(dataTypes) == 1:
				dtm = currentProgram.getDataTypeManager()
				pointerType = dtm.getPointer(dataTypes[0])
				addrType = dtm.addDataType(pointerType, None)
	elif len(dataTypes) > 1:
		print("Conflicting data types found for type " + newType)
		return
	else:
		addrType = dataTypes[0]
	if addrType is None:
		print("Could not identify type " + newType)
	else:
		try:
			createData(addr, addrType)
		except ghidra.program.model.util.CodeUnitInsertionException:
			print("Warning: unable to set type (CodeUnitInsertionException)")

def make_function(start):
	func = getFunctionAt(start)
	if func is None:
		try:
			createFunction(start, None)
		except Exception:
			print("Warning: Unable to create function")

def set_sig(addr, name, sig):
	sig = ghidra_str(sig)
	name = sanitize_name(name)
	try:
		typeSig = CParserUtils.parseSignature(None, currentProgram, sig, False)
	except ghidra.app.util.cparser.C.ParseException:
		print("Warning: Unable to parse")
		print(sig)
		print("Attempting to modify...")
		try:
			newSig = sig.replace(", ", "ext, ").replace(")", "ext)")
			typeSig = CParserUtils.parseSignature(None, currentProgram, newSig, False)
		except Exception:
			print("Warning: also unable to parse")
			print(newSig)
			print("Skipping.")
			return
	if typeSig is not None:
		try:
			typeSig.setName(name)
			ApplyFunctionSignatureCmd(addr, typeSig, USER_DEFINED, False, True).applyTo(currentProgram)
		except Exception:
			print("Warning: unable to set Signature. ApplyFunctionSignatureCmd() Failed.")

f = askFile("script.json from Il2CppDumper / AssetStudioMod", "Open")
if f is None:
	print("Cancelled.")
else:
	data = json.loads(open(file_path(f), "rb").read().decode("utf-8"))

	if "ScriptMethod" in data and "ScriptMethod" in processFields:
		scriptMethods = data["ScriptMethod"]
		monitor.initialize(len(scriptMethods))
		monitor.setMessage("Methods")
		for scriptMethod in scriptMethods:
			if monitor.isCancelled():
				break
			addr = get_addr(scriptMethod["Address"])
			make_function(addr)
			set_name(addr, scriptMethod["Name"])
			monitor.incrementProgress(1)

	if "ScriptString" in data and "ScriptString" in processFields:
		index = 1
		scriptStrings = data["ScriptString"]
		monitor.initialize(len(scriptStrings))
		monitor.setMessage("Strings")
		for scriptString in scriptStrings:
			if monitor.isCancelled():
				break
			addr = get_addr(scriptString["Address"])
			value = ghidra_str(scriptString["Value"])
			createLabel(addr, "StringLiteral_" + str(index), True, USER_DEFINED)
			setEOLComment(addr, value)
			index += 1
			monitor.incrementProgress(1)

	if "ScriptMetadata" in data and "ScriptMetadata" in processFields:
		scriptMetadatas = data["ScriptMetadata"]
		monitor.initialize(len(scriptMetadatas))
		monitor.setMessage("Metadata")
		for scriptMetadata in scriptMetadatas:
			if monitor.isCancelled():
				break
			addr = get_addr(scriptMetadata["Address"])
			name = scriptMetadata["Name"]
			set_name(addr, name)
			setEOLComment(addr, ghidra_str(name))
			monitor.incrementProgress(1)
			sig = scriptMetadata.get("Signature")
			if sig:
				set_type(addr, sig)

	if "ScriptMetadataMethod" in data and "ScriptMetadataMethod" in processFields:
		scriptMetadataMethods = data["ScriptMetadataMethod"]
		monitor.initialize(len(scriptMetadataMethods))
		monitor.setMessage("Metadata Methods")
		for scriptMetadataMethod in scriptMetadataMethods:
			if monitor.isCancelled():
				break
			addr = get_addr(scriptMetadataMethod["Address"])
			name = scriptMetadataMethod["Name"]
			set_name(addr, name)
			setEOLComment(addr, ghidra_str(name))
			monitor.incrementProgress(1)

	if "Addresses" in data and "Addresses" in processFields:
		addresses = data["Addresses"]
		monitor.initialize(len(addresses))
		monitor.setMessage("Addresses")
		for index in range(len(addresses) - 1):
			if monitor.isCancelled():
				break
			start = get_addr(addresses[index])
			make_function(start)
			monitor.incrementProgress(1)

	if "ScriptMethod" in data and "ScriptMethod" in processFields:
		scriptMethods = data["ScriptMethod"]
		monitor.initialize(len(scriptMethods))
		monitor.setMessage("Signatures")
		for scriptMethod in scriptMethods:
			if monitor.isCancelled():
				break
			addr = get_addr(scriptMethod["Address"])
			raw = scriptMethod.get("Signature") or ""
			sig = ghidra_str(raw)
			if sig.endswith(";"):
				sig = sig[:-1]
			set_sig(addr, scriptMethod["Name"], sig)
			monitor.incrementProgress(1)

	print("Script finished!")
