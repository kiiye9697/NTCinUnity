# Third-Party Notices

This document identifies third-party material referenced by or used to develop NTCinUnity. The repository-level `LICENSE` applies only to original NTCinUnity material.

## NVIDIA RTXNTC

- Upstream: <https://github.com/NVIDIA-RTX/RTXNTC>
- Local path: `nvidia-backend/`
- Integration: Git submodule pointing to NVIDIA's official repository
- Governing terms: the license files in the RTXNTC repository

The parent repository stores only the submodule reference. A recursive clone fetches RTXNTC directly from NVIDIA. Local RTXNTC build output, downloaded SDKs, binaries, and uncommitted modifications are not part of the parent repository.

### NVIDIA-derived shader code

The following NTCinUnity files contain Unity ports of RTXNTC inference/decompression logic:

| NTCinUnity file | NVIDIA source used as reference |
|---|---|
| `unity-plugin/Shaders/NtcInference.hlsl` | `Inference.hlsli` |
| `unity-plugin/Resources/NtcDecompress.compute` | `DecompressINT8.hlsl`, `Inference.hlsli` |

NVIDIA marks the relevant upstream shader source with `SPDX-License-Identifier: LicenseRef-NvidiaProprietary`. The NVIDIA-derived portions of the two files above remain governed by NVIDIA's terms, not by the repository-level `LICENSE`.

The attribution required by the NVIDIA license is reproduced in those files and here:

> This software contains source code provided by NVIDIA Corporation.

The rest of the Unity-specific code includes resource binding, NTEX parsing, GPU asset construction, URP material integration, Editor tooling, and tile scheduling written for this project.

## Unity and Universal Render Pipeline

The plugin targets Unity 2021.3 and the Universal Render Pipeline. Unity is used under the Unity Software Terms accepted when installing and using the editor. No Unity engine or URP source is copied into this repository.

## Classic Sponza development scene

The local development showcase uses Unity's Classic Sponza sample, derived from the Crytek Sponza model. That scene is used only for local validation.

The public repository does not include:

- the showcase Unity project;
- Classic Sponza meshes or textures;
- HDR environment assets used by the local project;
- `.ntc` files baked from those textures;
- decompressed preview images derived from those textures;
- the compiled showcase Player.

Measurements in `docs/验证记录.md` are numerical observations only and do not include asset data.

## Summary

| Component | Included | Terms |
|---|---|---|
| Original Unity plugin C# and Editor code | Yes | Repository `LICENSE` |
| NVIDIA-derived portions of two shader files | Yes | NVIDIA RTXNTC terms and file attribution |
| RTXNTC source | Referenced as official submodule | RTXNTC repository license |
| Local RTXNTC build output and binaries | No | Not distributed |
| Local Classic Sponza showcase and baked data | No | Not distributed |
