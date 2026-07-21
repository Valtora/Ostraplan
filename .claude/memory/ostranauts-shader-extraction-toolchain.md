---
name: ostranauts-shader-extraction-toolchain
description: "How to extract and disassemble Ostranauts' GPU shaders (UnityPy + d3dcompiler_47) — needed to re-verify Light Viz constants per game patch"
metadata: 
  node_type: memory
  type: reference
  originSessionId: 4b6d354c-30b7-407f-8070-d997cd89c64c
  modified: 2026-07-18T17:44:04.782Z
---

Ostranauts' render constants (Light Viz falloff, blend modes) live in compiled Unity shaders, not the C# DLL. To re-verify them after a game patch (ilspycmd only covers the C# side):

1. `pip install UnityPy`, load `Ostranauts_Data/resources.assets`, filter `obj.type.name == "Shader"`. Key shaders: `Sprites/LoSPass` (the light-mesh fragment = the falloff math), `Sprites/DefaultAdditive` (glow decals), `Sprites/AlbedoPass`, `Hidden/FinalCombinePass`, `Sprites/StencilCombinePass`.
2. Read the typetree: `platforms`/`offsets`/`compressedLengths`/`decompressedLengths`/`compressedBlob` → LZ4-block-decompress → header `int32 count` then `count × (offset, length, segment)` **triplets**. Each subprogram segment contains a `DXBC` container (find the magic; total length at magic+24).
3. Disassemble with the system `d3dcompiler_47.dll` via ctypes (`D3DDisassemble`) — no external tools needed.
4. Blend state is in `m_ParsedForm.m_SubShaders[].m_Passes[].m_State.rtBlend0` (Unity BlendMode enum: 1=One, 4=OneMinusDstColor, 5=SrcAlpha). LoSPass = `OneMinusDstColor One` (screen blend); DefaultAdditive = `SrcAlpha One`.

Verified constants as of 0.34.x (all asserted in Ostraplan's `LightNetworkTests` against `LightComposite`): F=3, Z=0.25, atten `1/(F²(d²+Z²)+0.1)`, per-light 8-bit clamp, normal decode `nx=2r−1, ny(doc)=2g−1` (from `ShaderSetup.NormalPNGtoDXTnm`'s green flip cancelling the doc-space y-flip). The full pipeline analysis is in Ostraplan's `docs/GAME-INTERNALS.md` §6.

Related: [[ostraplan-expose-tuning-as-user-controls]].
