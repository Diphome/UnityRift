<p align="center"><img src="docs/unityrift.png" width="140" alt="UnityRift"></p>

# UnityRift

**UnityRift** is a toolkit for **reverse-engineering and porting Unity games** — with a full **Unity → Godot 4 pipeline** at its center: export whole scenes (meshes, materials, particles, lights, cameras) and MonoBehaviour script stubs straight into a ready-to-open Godot project, recover shaders and IL2CPP/Mono code, and drive it all from the GUI, the CLI, or an MCP server for AI agents.

> **Origins.** UnityRift began as a fork of aelurum's [AssetStudioMod](https://github.com/aelurum/AssetStudio) (itself a fork of Perfare's [AssetStudio](https://github.com/Perfare/AssetStudio)) — huge thanks to both. It has since grown into a **distinct project** with its own direction (the Godot pipeline, IL2CPP/Ghidra tooling, a .NET class explorer, an MCP server, and many core fixes) that no longer resembles a simple fork. The original asset-extraction features are still here and credited below.

**Neither the repository, nor the tool, nor its authors are affiliated with, sponsored, or authorized by Unity Technologies or its affiliates.** UnityRift extracts and inspects assets for interoperability, research, and preservation; respect the rights and terms of any content you process.

## What UnityRift adds

- **IL2CPP support** via [Cpp2IL](https://github.com/SamboyCoding/Cpp2IL): `GameAssembly.dll` / `libil2cpp.so` + `global-metadata.dat` are detected automatically, dummy assemblies are generated and cached, and they feed the .NET explorer and MonoBehaviour field parsing.
- **.NET class explorer** — browse the game's managed assemblies as C#-like stubs (with optional IL). GUI tab **".NET Classes"**, CLI `-m dotnet`, MCP `dotnet_list` / `dotnet_type`.
- **Ghidra / Il2CppDumper package** (`-m il2cpp`) — generates `script.json`, `il2cpp.h`, `il2cpp_ghidra.h` and bundled `ghidra.py` / `ghidra_with_struct.py` scripts (patched for Ghidra Jython 2.7 **and** 11.3+ PyGhidra) so functions get named the same way [Il2CppDumper](https://github.com/Perfare/Il2CppDumper) does. Plus `--il2cpp-lookup`, `--il2cpp-strings`, and `--il2cpp-dummy-dll` (export the dummy .NET assemblies to `<out>/DummyDll` for dnSpy / ILSpy / dotPeek).
- **Type-tree database (TPK)** — decode type-tree-stripped builds via a bundled `classdata.tpk` (`--typetree-db`, auto-loaded when present).
- **glTF 2.0 export** (`.glb` / `.gltf`) as an FBX-free alternative (meshes, skinning, materials + embedded textures, node animations).
- **Godot 4 export** (`-m godot`) — converts a game's **materials** and **particle FX** into Godot 4 scaffolds:
   - Materials → `.gdshader` (spatial) with `render_mode` (blend/cull/depth), uniforms and defaults mapped from the material's properties and render states, a best-effort `fragment()` body, and the original shader as a reference block to hand-port; plus a `.tres` ShaderMaterial and the referenced textures (PNG).
   - ParticleSystems → `.tscn` (GPUParticles3D + ParticleProcessMaterial): emission shape, lifetime, velocity/size/rotation/gravity, start color and amount are mapped (curve/gradient fields approximated by their scalar).
   - Also on the MCP as `godot_export`.
- **Godot 4 scene export** (`-m godotscene`) — turns a Unity scene/prefab into a ready-to-open Godot 4 project: each mesh root is exported as glTF (correct orientation, materials, skinning, animations) and a `scene.tscn` instances them under a `Node3D`, plus native Godot nodes for **ParticleSystems** (GPUParticles3D), **Lights** (Directional/Omni/Spot Light3D) and **Cameras** (Camera3D) placed at their world transform, and **MonoBehaviour script stubs**: objects inside a mesh are listed in `scripts_manifest.json` with a shipped `attach_scripts.gd` EditorScript that binds each stub onto the real imported glTF node (run it in Godot with the scene open); objects with no mesh get `<Object>_Scripts` holder nodes (objects parented to bones are handled via `BoneAttachment3D`). Add `--godot-attach-plugin` to instead ship an editor plugin that attaches the stubs automatically when the scene is opened. Generated with a `project.godot`. Point it at a `levelN` file, a prefab bundle, or the game's `*_Data` folder; open the output in Godot 4 and run `scene.tscn`. Also on the MCP as `godot_scene_export`. (Validated against Godot 4.7.)
- **Godot 4 script stubs** (`-m godotscripts`) — one GDScript stub per MonoBehaviour class, with its serialized fields as `@export` vars (defaults captured from an instance), object references noted for manual wiring, and `_ready()`/`_process()` TODOs. Works for **Mono** (managed assemblies auto-detected) and **IL2CPP** (`--il2cpp`, Cpp2IL dummy assemblies). The Unity logic is not translated — this is scaffolding to port by hand. Also on the MCP as `godot_scripts_export`. (Stubs validated against Godot 4.7.)
- **MCP server** (`tools/mcp/assetstudio-mcp.mjs`) — exposes the CLI as tools so an agent can drive info/export/dump, the .NET explorer, and the IL2CPP/Ghidra workflow.
- **Animated model preview** in the GUI — select an Animator, pick a clip, play it with textured per-submesh rendering.
- **Faster project loading** — parallel asset reads, direct type-tree→JSON streaming, and garbage-count guards.

See [`docs/PROJECT_NOTES.md`](docs/PROJECT_NOTES.md) for the full engineering log and the licensing inventory.

## AssetStudio Features

- Support Unity version:
  - 1.7 - 6000.2
- Support asset types:
  - **Texture2D**, **Texture2DArray** : convert to png, tga, jpeg, bmp, webp
  - **Sprite** : crop Texture2D to png, tga, jpeg, bmp, webp
  - **AudioClip** : mp3, ogg, wav, m4a, fsb. Support converting FSB file to WAV(PCM)
  - **Font** : ttf, otf
  - **Mesh** : obj
  - **TextAsset**
  - **Shader** (for Unity < 2021)
  - **MovieTexture**
  - **VideoClip**
  - **MonoBehaviour** : json
  - **Animator** : export to FBX file with bound AnimationClip
 
## Inherited AssetStudioMod features

- CLI version (for Windows, Linux, Mac)
- Support of sprites with alpha mask
- Support of image export in WebP format
- Support of Live2D Cubism model export
   - Ported from aelurum's fork of Perfare's [UnityLive2DExtractor](https://github.com/aelurum/UnityLive2DExtractor)
   - Using the Live2D export in AssetStudio allows you to specify a Unity version and assembly folder if needed
- Support of swizzled Switch textures
    - Ported from nesrak1's [AssetStudio fork](https://github.com/nesrak1/AssetStudio/tree/switch-tex-deswizzle)
- Detecting bundles with UnityCN encryption
   - Detection only. If you want to open them, please use Razmoth's [Studio](https://github.com/RazTools/Studio) or Escartem's [AnimeStudio](https://github.com/Escartem/AnimeStudio)
- Some UI optimizations and bug fixes (See [CHANGELOG](https://github.com/aelurum/AssetStudio/blob/AssetStudioMod/CHANGELOG.md) for details)

## Requirements

- UnityRift.net472
   - GUI/CLI - [.NET Framework 4.7.2](https://dotnet.microsoft.com/download/dotnet-framework/net472)
- UnityRift.net8
   - GUI/CLI (Windows) - [.NET Desktop Runtime 8.0](https://dotnet.microsoft.com/download/dotnet/8.0)
   - CLI (Linux/Mac) - [.NET Runtime 8.0](https://dotnet.microsoft.com/download/dotnet/8.0)
- UnityRift.net9
   - GUI/CLI (Windows) - [.NET Desktop Runtime 9.0](https://dotnet.microsoft.com/download/dotnet/9.0)
   - CLI (Linux/Mac) - [.NET Runtime 9.0](https://dotnet.microsoft.com/download/dotnet/9.0)

## CLI Usage

You can read the CLI readme [here](AssetStudioCLI/ReadMe.md).

### Run

- Command-line: `UnityRiftCLI <asset folder path>`
- Command-line for Portable versions (.NET 6+): `dotnet UnityRiftCLI.dll <asset folder path>`

### Basic Samples

- Show a list with a number of assets of each type available for export
```
UnityRiftCLI <asset folder path> -m info
```
- Export assets of all supported for export types
```
UnityRiftCLI <asset folder path>
```
- Export assets of specific types
```
UnityRiftCLI <asset folder path> -t tex2d
```
```
UnityRiftCLI <asset folder path> -t tex2d,sprite,audio
```
- Export assets grouped by type
```
UnityRiftCLI <asset folder path> -g type
```
- Export assets to a specified output folder
```
UnityRiftCLI <asset folder path> -o <output folder path>
```
- Dump assets to a specified output folder
```
UnityRiftCLI <asset folder path> -m dump -o <output folder path>
```
- Export Live2D Cubism models
```
UnityRiftCLI <asset folder path> -m live2d
```
> When running in live2d mode, the only filter option supported is `--filter-by-name`.
- Export all FBX objects (similar to "Export all objects (split)" option in the GUI)
```
UnityRiftCLI <asset folder path> -m splitObjects
```
> When running in splitObjects mode, the only filter option supported is `--filter-by-name`.
- Export Animator assets
```
UnityRiftCLI <asset folder path> -m animator
```
- Generate Il2CppDumper-compatible Ghidra helpers from an IL2CPP game (script.json, il2cpp.h, ghidra.py)
```
UnityRiftCLI <game folder> -m il2cpp -o <output folder>
```
Look up a method/address while decompiling:
```
UnityRiftCLI <game folder> -m il2cpp --il2cpp-lookup PlayerController$$Update
UnityRiftCLI <game folder> -m il2cpp --il2cpp-lookup 0x1A2B3C
```

### Advanced Samples
- Export image assets converted to webp format to a specified output folder
```
UnityRiftCLI <asset folder path> -o <output folder path> -t sprite,tex2d --image-format webp
```
- Show the number of audio assets that have "voice" in their names
```
UnityRiftCLI <asset folder path> -m info -t audio --filter-by-name voice
```
- Export audio assets that have "voice" in their names
```
UnityRiftCLI <asset folder path> -t audio --filter-by-name voice
```
- Export audio assets that have "music" or "voice" in their names
```
UnityRiftCLI <asset folder path> -t audio --filter-by-name music,voice
```
```
UnityRiftCLI <asset folder path> -t audio --filter-by-name music --filter-by-name voice
```
- Export audio assets that have "char" in their names **or** containers
```
UnityRiftCLI <asset folder path> -t audio --filter-by-text char
```
- Export audio assets that have "voice" in their names **and** "char" in their containers
```
UnityRiftCLI <asset folder path> -t audio --filter-by-name voice --filter-by-container char
```
- Export FBX objects that have "model" or "scene" in their names and set the scale factor to 10
```
UnityRiftCLI <asset folder path> -m splitObjects --filter-by-name model,scene --fbx-scale-factor 10
```
- Export MonoBehaviour assets that require an assembly folder to read and create a log file
```
UnityRiftCLI <asset folder path> -t monobehaviour --assembly-folder <assembly folder path> --log-output both
```
- Export assets that require to specify a Unity version
```
UnityRiftCLI <asset folder path> --unity-version 2017.4.39f1
```
- Load assets of all types and show them (similar to "Display all assets" option in the GUI)
```
UnityRiftCLI <asset folder path> -m info --load-all
```
- Load assets of all types and dump Material assets
```
UnityRiftCLI <asset folder path> -m dump -t material --load-all
```

## GUI Usage

### Load Assets/AssetBundles

Use **File->Load file** or **File->Load folder**.

When AssetStudio loads AssetBundles, it decompresses and reads it directly in memory, which may cause a large amount of memory to be used. You can use **File->Extract file** or **File->Extract folder** to extract AssetBundles to another folder, and then read.

### Extract/Decompress AssetBundles

Use **File->Extract file** or **File->Extract folder**.

### Export Assets, Live2D models

Use **Export** menu.

### Export Model

Export model from "Scene Hierarchy" using the **Model** menu.

Export Animator from "Asset List" using the **Export** menu.

#### With AnimationClip

Select model from "Scene Hierarchy" then select the AnimationClip from "Asset List", using **Model->Export selected objects with AnimationClip** to export.

Export Animator will export bound AnimationClip or use **Ctrl** to select Animator and AnimationClip from "Asset List", using **Export->Export Animator with selected AnimationClip** to export.

### Export MonoBehaviour

When you select an asset of the MonoBehaviour type for the first time, AssetStudio will ask you the directory where the assembly is located, please select the directory where the assembly is located, such as the `Managed` folder.

#### For Il2Cpp

AssetStudioMod generates dummy assemblies itself: **File → Load IL2CPP binary**, or just load the game folder (GameAssembly.dll / libil2cpp.so + `global-metadata.dat` are detected automatically). The first run uses [Cpp2IL](https://github.com/SamboyCoding/Cpp2IL) and is cached.

To name functions in Ghidra the same way [Il2CppDumper](https://github.com/Perfare/Il2CppDumper) does:

1. CLI: `UnityRiftCLI <game folder> -m il2cpp -o <out>` (or GUI **.NET Classes → Export → Export Ghidra / Il2CppDumper package**).
2. Import `GameAssembly.dll` / `libil2cpp.so` into Ghidra and let auto-analysis finish.
3. **File → Parse C Source...** and add `<out>/il2cpp/il2cpp_ghidra.h`.
4. **Window → Script Manager** → add `<out>/il2cpp/ghidra` as a script directory, run `ghidra.py` (names) or `ghidra_with_struct.py` (names + types), and pick `script.json`.

Scripts work in Ghidra's Jython 2.7 and in Ghidra 11.3+ PyGhidra (Python 3). Addresses in `script.json` are RVAs; the scripts add `currentProgram.getImageBase()`.

## Build

* Visual Studio 2022 or newer
* AssetStudioMod is **64-bit only**. 32-bit (x86) builds are no longer supported.
* **AssetStudioFBXNative** uses the [FBX SDK 2020.3.x](https://aps.autodesk.com/developer/overview/fbx-sdk) (x64). Install it before building; the project looks for it in the default location (`C:\Program Files\Autodesk\FBX\FBX SDK\2020.3.10`). To use a different version or path, set the `FBXSDK_ROOT` environment variable, or pass `/p:FbxSdkDir="<path>\"` to MSBuild — no need to edit the project file.

## Open source libraries used

### Texture2DDecoder
* [Ishotihadus/mikunyan](https://github.com/Ishotihadus/mikunyan)
* [BinomialLLC/crunch](https://github.com/BinomialLLC/crunch)
* [Unity-Technologies/crunch](https://github.com/Unity-Technologies/crunch/tree/unity)

### LZMA compression
* [7-zip/sdk](https://www.7-zip.org/sdk.html)

### Oodle compression
* [zao/ooz](https://github.com/zao/ooz)
