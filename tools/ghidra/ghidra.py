# -*- coding: utf-8 -*-
# @category IL2CPP
# @menupath Tools.IL2CPP.ApplyNames
# Based on Il2CppDumper ghidra.py (https://github.com/Perfare/Il2CppDumper), MIT License, Copyright (c) 2016 Perfare.
# Adapted for both Ghidra Jython 2.7 and Ghidra 11.3+ PyGhidra (Python 3).
# Consumes the script.json produced by AssetStudioMod '-m il2cpp'.
from __future__ import print_function

import json

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
	# Jython 2: unicode -> utf-8 str. PyGhidra/Python 3: keep unicode str (bytes break createLabel).
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
	createLabel(addr, sanitize_name(name), True, USER_DEFINED)

def make_function(start):
	func = getFunctionAt(start)
	if func is None:
		createFunction(start, None)

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

	print("Script finished!")
