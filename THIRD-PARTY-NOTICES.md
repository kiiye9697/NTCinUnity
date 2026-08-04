Third-Party Notices
===================

This file records third-party material this project depends on or derives from,
and the obligations attached to each. Read it before redistributing anything from
this repository.


1. NVIDIA RTXNTC SDK / LibNTC
-----------------------------

**Upstream:** https://github.com/NVIDIA-RTX/RTXNTC
**License:** NVIDIA RTX SDKs License Agreement
**SPDX marker used by NVIDIA:** `LicenseRef-NvidiaProprietary`
**Copyright:** Copyright (c) 2024, NVIDIA CORPORATION & AFFILIATES. All rights reserved.

### Required notice

NVIDIA's license requires the following statement to appear in modifications and
derivative works of its source code, and it is reproduced here and in the headers
of the affected files:

> This software contains source code provided by NVIDIA Corporation.

### Files in this repository derived from the SDK

| File | Derived from | Nature of the derivation |
|------|--------------|--------------------------|
| `unity-plugin/Shaders/NtcInference.hlsl` | `Inference.hlsli` | Port of the MLP inference math (weight decode, layer evaluation, HGELUClamp activation, latent sampling) to plain HLSL for Unity |
| `unity-plugin/Resources/NtcDecompress.compute` | `DecompressINT8.hlsl`, `Inference.hlsli` | Port of the same math into a Unity compute kernel, with Unity-specific resource binding, a groupshared weight cache, and a software int8 dot-product fallback |

These two files are governed by **NVIDIA's license, not by this repository's
LICENSE**. Each carries an attribution header saying so.

### Why this repository is not open source

The NVIDIA RTX SDKs License Agreement states, in section 4(e), that the licensee
may not use the SDK in any manner that would cause it to become subject to an
open source software license — specifically a license requiring that the software
be disclosed or distributed in source form, licensed for making derivative works,
or redistributable at no charge.

Because the two shader files above are derivative works of SDK source code,
placing this repository under MIT, Apache-2.0, or a similar license would breach
that clause. That is the reason for the research-and-evaluation terms in LICENSE.

### What this repository does *not* contain

No NVIDIA source, header, binary, or SDK archive is redistributed here. In
particular `ntc-cli.exe` and `libntc` are **not** included. To bake your own
`.ntc` files, clone the SDK from the upstream URL above and build it yourself,
accepting NVIDIA's license at that point.

### Interoperability note

`unity-plugin/Runtime/NtcContainerReader.cs` parses the NTEX container layout
(header, JSON chunk, data chunk). It was written from observed file structure and
the SDK's public documentation for interoperability purposes and contains no
NVIDIA source code.


2. Unity "Classic Sponza" sample scene
--------------------------------------

The scene used during development (`Sponza_Showcase`) and its textures come from
Unity Technologies' Classic Sponza sample, which itself derives from Crytek's
Sponza model. No license grant accompanied the copy used here.

Consequences, and how this repository handles them:

* The scene and its art are **not** included in this repository.
* Compressed `.ntc` files baked from those textures are derivative works of that
  art, so they are **not** included either. `.gitignore` excludes them.
* Quality figures quoted in the documentation (PSNR per material, VRAM
  comparisons) are measurements only and contain no asset data.

If you want to reproduce the measurements, obtain Classic Sponza from Unity under
Unity's terms and bake it yourself with the included bake window.


3. NoEmotion HDRs (development only)
------------------------------------

The development project's sky uses HDR panoramas from NoEmotion HDRs, covered by
`NoEmotionHDRs-License.txt` in that project. Those files live only in the local
development project and are not part of this repository.


4. Unity Engine and Universal Render Pipeline
---------------------------------------------

This plugin is written against Unity 2021.3 and URP, used under the Unity
Software Terms accepted when installing the editor. No Unity engine code is
redistributed here.


Summary
-------

| Component | Redistributed here? | Governing terms |
|-----------|--------------------|-----------------|
| Our plugin code (container reader, asset builder, controller, tile streamer, bake tools, editor UI) | Yes | LICENSE in this repository |
| NVIDIA-derived shader math (2 files) | Yes, as derivative source | NVIDIA RTX SDKs License Agreement |
| NVIDIA SDK / `ntc-cli` binaries | No | Obtain from NVIDIA |
| Classic Sponza art and `.ntc` baked from it | No | Obtain from Unity |
| NoEmotion HDR skies | No | NoEmotion HDRs license |
