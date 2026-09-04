# AssetStudioMod — Export Guide

A plain-language guide to what the export functions produce and when to use them.
For full CLI flags see the [CLI ReadMe](../AssetStudioCLI/ReadMe.md); this doc is the
"what does this give me, and why" overview.

There are two things to pick: a **mode** (what kind of job) and, for exports, the
**asset types** you want. In the GUI these map to the Export / Model menus; in the CLI
they are `-m <mode>` and `-t <types>`.

---

## Modes (`-m`) — the kind of job

| Mode | What it does | When to use it |
|------|--------------|----------------|
| `info` | Loads the files and just **counts** assets by type. Exports nothing. | First thing to run — see what a game contains before extracting. |
| `export` *(default)* | **Converts** each asset to a normal, usable file (see the table below). | The everyday "give me the textures/audio/models" job. |
| `exportRaw` | Writes each asset's **raw serialized bytes** (`.dat`), no conversion. | You want the untouched original data, or a type that doesn't convert. |
| `dump` | Writes each asset's **fields as text/JSON** — the structure and values. | Datamining: read an asset's actual data (stats, config, layout). Best paired with the type tree DB and, for scripts, an assembly folder. |
| `extract` | **Decompresses asset bundles** (`.bundle`, `.unity3d`) into plain assets files. | Unpack packed bundles first, then load the result. |
| `live2d` | Exports **Live2D Cubism** models (model, textures, motions, physics). | 2D "VTuber"-style animated characters. |
| `splitObjects` | Exports **each 3D model object separately** (FBX/glTF/GLB). | Pull individual props/characters out of a scene. |
| `animator` | Exports **Animator assets as rigged models** with their AnimationClips. | Characters/objects **with their animations**. |

---

## Asset types (`-t`) and what `export` produces

| Type (`-t`) | Output file | What it is / why you'd want it |
|-------------|-------------|--------------------------------|
| `tex2d` (Texture2D, Texture2DArray) | image: **png** (default), or jpg/bmp/tga/webp — or raw `.tex` if conversion is off | Game art: textures, UI, icons, character skins. |
| `sprite` | image (png/…), **cropped** out of its atlas (alpha-mask aware) | Individual 2D sprites already cut from sprite sheets. |
| `audio` | **wav** (from FMOD/FSB), or the original (mp3/ogg/m4a) / `.fsb` | Music, sound effects, voice lines. |
| `video` (VideoClip) | the original video container (e.g. **mp4/webm**) | Cutscenes / in-game video. |
| `movieTexture` | **.ogv** | Legacy (old-Unity) movie textures. |
| `font` | **.ttf** / **.otf** | The game's fonts. |
| `text` (TextAsset) | original extension, else **.txt** / `.bytes` | Config, dialogue, JSON/CSV, arbitrary data blobs. |
| `shader` | **.shader** text (Unity < 2021 only) | Inspect shader source. |
| `monoBehaviour` | **.json** | **The data goldmine** — item/enemy/level/config data defined by the game's scripts. Needs a type tree (embedded, or via assemblies for custom fields). |
| `mesh` | **.obj** (geometry only) | A single 3D mesh. For rigged/animated models use the model modes instead. |
| *(anything else)* | raw **.dat** | Fallback: the untouched serialized asset. |

Multiple types at once: `-t tex2d,sprite,audio`. Default is "all supported".

Image format: `--image-format png|jpg|bmp|tga|webp`. Audio: `--audio-format wav|none`.

---

## 3D models: FBX vs glTF

The model-producing modes (`animator`, `splitObjects`, and model exports) build a full
model — **mesh + skeleton + skinning + materials/textures + animations**. Pick the
format with `--model-format`:

| `--model-format` | Output | Use |
|------------------|--------|-----|
| `fbx` *(default)* | `.fbx` (via the native FBX library) | Maya/Blender/Unity re-import; the traditional choice. |
| `gltf` | `.gltf` (textures embedded) | Modern, open format; web/Blender friendly. |
| `glb` | `.glb` (single self-contained file) | Same as glTF but one tidy file — easiest to hand around. |

> A plain `mesh` export is just geometry (`.obj`). Use `animator`/`splitObjects` (or the
> GUI Model menu) when you want the rig and animations too.

---

## Filtering (works with export/dump/info)

- `--filter-by-name <text>` — asset name contains text
- `--filter-by-container <text>` — container/path contains text
- `--filter-by-text <text>` — name **or** container
- `--filter-by-pathid <ids>` — exact PathIDs
- `--filter-with-regex` — treat the text as a regular expression

---

## Reading "stripped" games (data that hides its structure)

Some games ship without embedded type trees (recipes). Then `dump` of engine assets and
generic inspection would normally fail. AssetStudioMod now ships a **type tree database**
(`classdata.tpk`, loaded automatically) so those assets read correctly. For a game's
**custom** MonoBehaviour fields you also need the game's code: pass
`--assembly-folder <path-to-Managed>`. See `docs/PROJECT_NOTES.md` for details.

---

## Quick recipes

```bash
# What's in here?
AssetStudioModCLI <folder> -m info

# All textures + sprites as PNG
AssetStudioModCLI <folder> -t tex2d,sprite --image-format png -o out

# Just the audio, as WAV
AssetStudioModCLI <folder> -t audio -o out

# Datamine: dump every MonoBehaviour to JSON (with the game's scripts)
AssetStudioModCLI <folder> -m dump -t monoBehaviour --assembly-folder <Managed> -o out

# Characters with animations, as GLB
AssetStudioModCLI <folder> -m animator --model-format glb -o out
```
