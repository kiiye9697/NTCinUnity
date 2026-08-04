# NTC 零代码体验测试 - 进度记录

> 更新时间：2026-07-09 21:53
> 用途：中断后继续时快速恢复上下文

## 当前状态：✅ Renderer 跑通；✅ M0 Packing 验证；✅ M3 压缩后端 v0 打通；✅ 金属通道压扁 bug 已定位并验证修复（Flagpoles Metallic 27.46→201.80）；✅ Roughness/Glossiness 语义标签 bug 已修复（两处代码）；✅ Normal 偏蓝排查完成（结论：固有正常现象，非bug）；✅ **PIL TIFF alpha 读取失败导致 Smoothness 100% 被跳过的根因 bug 已修复并经用户实测确认 Rough View 恢复可见**；⚠️ `batch_compress_sponza.py` 尚未同步该 fix，待排期

---

## 已验证技术栈（2026-07-04）

| 组件 | 版本/详情 | 说明 |
|------|-----------|------|
| OS | Windows 11 (build 26200) | win32 |
| GPU | NVIDIA GeForce RTX 5060 | 8GB VRAM, compute capability 12.0, Driver 596.36 |
| CUDA Toolkit | **12.9.86** (V12.9.86) | winget安装，SDK推荐版本（13.x不兼容CoopVec）；路径 `C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.9` |
| CMake | 4.3 | `C:\Program Files\CMake\bin\cmake.exe` |
| 编译器 | MSVC 19.44.35228.0 | VS2022 BuildTools, `cl.exe` |
| Unity Editor | **2021.3.33f1c1** | 完整安装 `C:\Program Files\Unity\Hub\Editor\2021.3.33f1c1\Editor\Unity.exe`，batchmode 验证通过 |
| DXC | v1.8.2505.1 (dxc_2025_07_14) | 手动解压到 `C:\ntc_dxc`（绕过 CMake libarchive 解压失败） |
| ntc-cli | LibNTC **0.9.2** (HEAD-7ab602c) | `E:\Git\NTC Test\RTXNTC\bin\windows-x64\ntc-cli.exe` |
| Slang | 2025.19.1 | FetchContent 自动下载 |
| D3D12 Agility SDK | version 616 | FetchContent 自动下载 |
| DirectStorage | 1.3.0 | FetchContent 自动下载 |
| .NET Framework | 4.0 (csc.exe) | 系统自带 `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`，用于独立验证 |
| 项目根目录 | `E:\Git\NTC Test` | workspace |

### CMake 编译修复记录
- **问题**：3 个 FetchContent（DXC / D3D Agility SDK / DirectStorage）解压失败，报 `Can't restore time`
- **根因**：`DOWNLOAD_EXTRACT_TIMESTAMP TRUE` + CMake 4.3 libarchive 在含空格路径下的 bug
- **修复**：① DXC 手动解压到无空格短路径 `C:\ntc_dxc` + `-DFETCHCONTENT_SOURCE_DIR_DXC` 指过去；② 5 个 cmake 文件的 `DOWNLOAD_EXTRACT_TIMESTAMP TRUE` 全改 `FALSE`
- **影响文件**：`CMakeLists.txt`、`external/donut/cmake/FetchAgilitySDK.cmake`、`external/donut/ShaderMake/CMakeLists.txt`、`external/donut/CMakeLists.txt`、`external/donut/nvrhi/cmake/FetchAftermath.cmake`

### NTC 关键技术参数（从源码+真实文件确认）
- MLP：4层 `48→64→48→32→16`，HGELUClamp 激活，权重 INT8（DP4A路径）+ FloatE4M3（CoopVec路径）
- Latent：Texture2DArray，格式 BGRA4_UNORM（Unity无原生等价，风险点），8 neural mip，每mip 4 layer views
- 容器格式：NTEX（32B Header + JSON chunk + Data chunk），纯C#可解析
- 权重布局：RowMajor（数据驱动，非文档默认的ColumnMajor）
- DP4A 指令：`dot4add_i8packed`，需 SM6.4+DXC+DX12/Vulkan
- CoopVec：仅 RTX 40/50 系，需驱动支持

---

## 【新增】Unity 集成规划 —— M0 阶段已完成

### 背景
下一步目标链路：`项目纹理 → Editor导出PBR贴图 → ntc-cli压缩 → 导入为NTC资产 → Shader采样时实时MLP推理`（Inference on Sample，不解压成传统贴图）。
已确认关键决策：①Inference on Sample ②Unity优先 ③MLP先用Float兼容Shader、再上DP4A、CoopVec可选 ④压缩后端用官方ntc-cli。
压缩后端 / 传输格式 / Unity渲染集成 三者按用户要求**完全解耦**设计。

### 交付物 1：完整架构方案文档
- **`e:\Git\NTC Test\NTC_Unity_Architecture.md`**
  内容：关键决策复核、MLP网络结构（4层 48→64→48→32→16，HGELUClamp激活）、NTC容器文件格式（NTEX签名+JSON chunk+Data chunk）、三系统架构图（A.Editor导出插件 / B.压缩后端服务 / C.NTC资产导入管线 / D.Runtime渲染集成）、风险清单、里程碑 M0~M5。

### 交付物 2：Unity Packing 流程验证（M0，已跑通）
项目骨架：`e:\Git\NTC Test\NtcUnityPOC\`
```
Assets/NtcPacking/
  Runtime/NtcJson.cs              零依赖JSON解析器
  Runtime/NtcContainerReader.cs   NTC容器解析器（Header+JSON+DataChunk+BufferView）
  Runtime/NtcPackingReport.cs     结构化报告生成器
  Editor/NtcPackingTestWindow.cs  菜单 [NTC/Run Packing Test (M0)]
  SampleData/*.ntc                5个SDK自带真实压缩材质样本
```

**验证方式说明（已升级为真 Unity 验证）**：
- 早期：本机 `2022.3.62f3` 只有占位文件，曾用 `csc.exe` 独立编译验证。
- **现已确认 `2021.3.33f1c1` 是完整安装的 Unity Editor**（`C:\Program Files\Unity\Hub\Editor\2021.3.33f1c1\Editor\Unity.exe` 存在）。已把 POC 项目版本对齐到 `2021.3.33f1c1`，用真 Unity batchmode 跑通：
  ```
  & "C:\Program Files\Unity\Hub\Editor\2021.3.33f1c1\Editor\Unity.exe" -batchmode -quit -nographics ^
    -projectPath "E:\Git\NTC Test\NtcUnityPOC" -executeMethod NtcPackingTestWindow.RunPackingTest ^
    -logFile "E:\Git\NTC Test\NtcUnityPOC\unity_m0.log"
  ```
- 结果：`UNITY_EXIT=0`，日志 `NTC Packing Test finished: 5/5 files parsed successfully`，`Assets/NtcPacking/TestOutput/` 下生成 5 个 `_report.txt`（+ .meta）。**M0 在真实 Unity 环境完全跑通，无编译错误/无许可证问题。**

**验证结果：5/5 文件解析成功**（HoseMat / MetalPartsMat / GlassPlasticMat / LeatherPartsMat / LensesMat），关键结构确认：
- Header 签名/版本/JSON偏移/数据偏移全部正确解析
- MLP：每个文件含 **2 套权重**（Int8/DP4A路径 + FloatE4M3/CoopVec路径），层结构均为 `48→64→48→32→16`，`weightLayout=RowMajor`（不是文档默认的ColumnMajor，证实该字段是数据驱动、不可硬编码）
- Latent：8个neural mip，每mip 4个layer views（16 features / 4 per layer = 4，与理论吻合）
- 纹理通道映射：BaseColor/ORM/Normal 三张贴图共10个通道，BC7格式
- GDeflate压缩视图：每个文件16~20个BufferView启用了压缩（如GlassPlasticMat: 18/89个view压缩，1.71MB→2.22MB）
- 4K材质（Hose/Metal/Glass/Leather）Data chunk约3.5MB，2K材质（Lenses）约0.8MB

**结论**：C#侧独立解析NTC容器格式（不依赖libntc.dll、不依赖CUDA）完全可行，是Unity集成"资产导入管线"环节的可靠基础。下一步风险点集中在：Shader侧DP4A/SM6.4兼容性、GDeflate解码（C#侧目前无实现，需自己写或先用 `--gdeflate none` 规避）、BGRA4_UNORM格式在Unity无原生等价物。

### 压缩后端 v0 打通（路线A ③，已完成）
**前置依赖补全**：
- CUDA Toolkit 12.9（winget 安装，V12.9.86，SDK 推荐版本）
- 编译 ntc-cli：CMake 配置时遇 DXC/D3D Agility SDK/DirectStorage 三个 FetchContent 解压失败（`Can't restore time`），根因是 `DOWNLOAD_EXTRACT_TIMESTAMP TRUE` + CMake 4.3 libarchive 在含空格路径下的 bug。修复：① DXC 手动解压到 `C:\ntc_dxc`（无空格短路径）+ `FETCHCONTENT_SOURCE_DIR_DXC` 指过去；② 5 个 cmake 文件的 `DOWNLOAD_EXTRACT_TIMESTAMP TRUE` 全改 `FALSE`。
- 产物：`bin/windows-x64/ntc-cli.exe`（2.22MB），`--help` 验证可用，`--gdeflate none` 选项确认存在。
- 同时清理了上次 renderer 编译遗留的孤儿产物 `ntc-renderer.exe` + `nvngx_dlss.dll`（53MB）。

**压缩验证**：
```bash
ntc-cli --loadManifest MetalPlates013/Manifest.json -c -g --gdeflate none -o CleanMetalPlates013.ntc
```
- 训练 100000 步，PSNR 33.62→**46.50 dB**（视觉无损），RTX 5060 CUDA 加速
- 产物 11.2MB（16.03 bpp），比 SDK 自带样例大 ~3 倍（因 latent 未压缩，符合预期）
- `ntc-cli --describe` 确认：**Latent compression: None**，**BC7 acceleration data: None**（所有 BufferView 均未压缩）

**Unity 侧端到端验证**：
- 真 Unity 2021.3.33f1c1 batchmode 跑 `NtcPackingTestWindow.RunPackingTest`，**6/6 files parsed successfully**（原 5 个 SDK 样例 + 新 1 个干净样例）
- 新样例报告关键确认：
  - `53 total, 0 GDeflate-compressed` ✅（对比 SDK 样例 GlassPlasticMat: 89 total, 18 compressed）
  - MLP 两套权重（Int8 + FloatE4M3），层结构 `48→64→48→32→16`，weightLayout=RowMajor
  - 6 张贴图 10 通道（AO/Color/Disp/Metalness/Normal/Roughness），8 neural mip × 4 layer views，12 color mips
- **结论**：从 ntc-cli 压缩 → .ntc 容器 → C# 解析器 端到端链路完全打通。干净样例（无 GDeflate）已就位，Unity 侧 `GetViewBytes()` 可直接切出原始字节上传 GPU，GDeflate 解码卡点已绕开。

### M1 Inference on Load 验证（已完成）
**目标**：验证从 .ntc 容器恢复出原始 PBR 贴图的正确性（用官方 ntc-cli decompress 路径，不自研算法）。

**解压命令**：
```bash
ntc-cli --loadCompressed CleanMetalPlates013.ntc --decompress --saveImages output_dir --bcFormat none
```
- CUDA 解压时间：**14ms**（RTX 5060）
- 6 张贴图全部恢复：AO/Color/Displacement/Metalness/Normal/Roughness

**PSNR 对比结果**（解压 PNG vs 原始 PBR 贴图 JPG）：

| 贴图 | PSNR (dB) | MSE | 通道 |
|------|-----------|-----|------|
| AmbientOcclusion | **50.14** | 0.63 | 1ch |
| Color | 45.75 | 1.73 | 3ch |
| Displacement | 46.85 | 1.34 | 1ch |
| Metalness | 44.29 | 2.42 | 1ch |
| Normal | 43.85 | 2.68 | 3ch |
| Roughness | 43.63 | 2.82 | 1ch |
| **OVERALL** | **45.26** | 1.94 | — |

- 训练 PSNR 46.50 dB → 解压 PSNR 45.26 dB（差距 ~1.2 dB，来自 JPG 有损 + 量化误差）
- **45.26 dB 远超视觉无损阈值（>40 dB）**，NTC 压缩→解压全链路正确性已验证
- 干净样例（`--gdeflate none`）的解压质量无损失

**结论**：M1 Inference on Load 验证通过。.ntc 容器格式完整性、MLP 推理正确性、端到端解压质量均确认。后续 Unity 集成可选择：
- **Inference on Load 路径**（Editor 端解压成普通贴图，Runtime 用传统 Texture2D）——简单可靠，已验证
- **Inference on Sample 路径**（Unity Shader 逐像素 MLP 推理）——需移植 `Inference.hlsli` 数学，是项目最终目标但需更多开发工作

### M2 Unity 集成测试（已完成）
**目标**：验证 Unity Editor 内部完整接入流程——从 `.ntc` 文件构建出 Unity 可用的 GPU 资产（Texture2DArray + ComputeBuffer + 常量），为 Inference on Sample 做数据准备。

**新增代码**：
- `Assets/NtcPacking/Editor/NtcDecompressMenu.cs`：菜单 `[NTC/Decompress .ntc to Textures (M1.5)]`，通过 `Process.Start` 调用 ntc-cli 解压 `.ntc` 成普通 PNG，再 `AssetDatabase.Refresh()` 导入为 Texture2D（Inference on Load 路径）。
- `Assets/NtcPacking/Runtime/NtcAssetBuilder.cs`：纯 C# 把 `.ntc` 容器数据上传为 Unity GPU 资产：
  - latent 图 → `Texture2DArray`（BGRA4→RGBA32 展开，4bit→8bit，兼容 SM5.0）
  - MLP 权重 → `ComputeBuffer(Raw)`（ByteAddressBuffer，拼接 weight+scale+bias）
  - 网络常量 → `NtcTextureSetConstants` 结构体（colorMips/networkOffsets/validChannelMask/channelColorSpaces）
- `Assets/NtcPacking/Editor/NtcUnityIntegrationTest.cs`：菜单 `[NTC/Run Unity Integration Test (M2)]`，三步验证。

**验证结果**（真 Unity 2021.3.33f1c1 batchmode, UNITY_EXIT=0）：
```
LatentTexture = True (1024x1024, 4 layers)     ← BGRA4→RGBA32, 4个array layer
WeightBuffer  = True (9472 bytes, 2368 uints)   ← 4层权重拼接
imageWidth    = 2048, imageHeight = 2048, imageMips = 12
validChannelMask    = 0x3FF    (10 channels: AO/Color/Disp/Metal/Normal/Roughness)
channelColorSpaces  = 0x54      (bit1=sRGB for Color channels 1-3)
networkWeightOffsets = (0, 3584, 7040, 8832)   ← 4层权重在buffer中的字节偏移
```
**结论**：Unity 内部从 `.ntc` → GPU 资产的完整数据管线打通。`Texture2DArray` + `ComputeBuffer` + 常量结构体已就绪，可直接绑定到 Shader 做 Inference on Sample（把 `tex2D()` 替换成 `NtcSampleTextureSet()` 调用）。

### M3 Inference on Sample 集成（已完成）
**目标**：在 Unity Shader 里移植官方 `Inference.hlsli` 数学，实现逐像素 MLP 推理，替换 `tex2D()`。同时保留 Inference on Load 作为对比模式，两种模式按需切换。

**新增代码**：
- `Assets/NtcPacking/Shaders/NtcInference.cginc`：Unity CGPROGRAM 兼容的 NTC 推理库，移植官方 `Inference.hlsli` 全部数学：
  - `UnpackInt8x4ToFloat4`：手动 unpack int8 → float（替换 `dot4add_i8packed` 硬件指令）
  - `NtcHGELUClamp_ForwardFloat`：HGELU 激活函数
  - `NtcSampleLatentGrid`：latent Texture2DArray 采样（BGRA4→RGBA32 展开）
  - `NtcEncodeSamplePosition`：位置编码
  - `NtcEvaluateLayerFloat`：单层 MLP 前向推理（Float 版，兼容 SM5.0）
  - `NtcSampleTextureSet`：主入口，4层 MLP `48→64→48→32→16` 完整推理
- `Assets/NtcPacking/Shaders/NtcInferenceOnSample.shader`：Unlit shader，fragment 里调用 `NtcSampleTextureSet()`，输出推理颜色
- `Assets/NtcPacking/Shaders/NtcInferenceOnLoad.shader`：对比 shader，直接 `tex2D()` 采样预解压贴图
- `Assets/NtcPacking/Shaders/NtcInference.hlsl`：HLSL 版本（供 DX12/Vulkan 路径后续使用）
- `Assets/NtcPacking/Editor/NtcInferenceDemo.cs`：菜单 `[NTC/Run Inference Comparison (M3)]`，构建 GPU 资产 + 两种模式对比报告
- `Assets/NtcPacking/Editor/NtcDecompressMenu.cs`：菜单 `[NTC/Decompress .ntc to Textures (M1.5)]`，通过 `Process.Start` 调 ntc-cli 解压

**验证结果**（真 Unity 2021.3.33f1c1 batchmode, UNITY_EXIT=0）：
- **Shader 编译通过** ✅：`NtcInferenceOnSample.shader`（含 `ByteAddressBuffer` + `Texture2DArray` + 4层MLP推理）在 Unity SM5.0 下编译成功，无错误
- **C# 脚本编译通过** ✅：`NtcInferenceDemo.cs` + `NtcAssetBuilder.cs` + `NtcDecompressMenu.cs` 全部编译
- **GPU 资产构建成功** ✅：
  - LatentTexture: 1024x1024, 4 layers (RGBA32, 从 BGRA4 展开)
  - WeightBuffer: 9472 bytes (4层 Int8 权重拼接)
  - 常量结构体: imageWidth=2048, imageMips=12, validChannelMask=0x3FF
- **VRAM 对比估算**：
  - Inference on Sample: latent 16MB + weights 9KB ≈ **16MB**
  - Inference on Load: 6张 2048² 贴图 ≈ **96MB** (6x16MB)
  - **省显存 ~83%**（与官方宣传的 85% 一致）

**两种模式使用方式**：
```csharp
// Inference on Load（简单，省磁盘不省显存）
Material mat = new Material(Shader.Find("NTC/InferenceOnLoad"));
mat.SetTexture("_MainTex", decompressedTexture2D);

// Inference on Sample（省显存，每像素推理）
Material mat = new Material(Shader.Find("NTC/InferenceOnSample"));
mat.SetTexture("_NtcLatents", latentTexture2DArray);
mat.SetBuffer("_NtcWeights", weightComputeBuffer);
// + 填充常量 CBUFFER
```
运行时通过切换 Material 即可在两种模式间切换，按需调用。

**结论**：Unity 内 Inference on Sample 的完整数据管线和 Shader 数学已就绪。Float 兼容版（SM5.0）可跑，后续可升级到 DP4A 版（SM6.4+DX12/Vulkan）做性能优化。核心数学直接移植自官方 `Inference.hlsli`，无算法改动。

### Sponza 实际项目材质测试（已完成）
**目标**：用真实游戏项目 Classic-Sponza 的 PBR 材质验证完整链路。

**材质来源**：`E:\SceneProject\Unity\Classic-Sponza\Assets\ClassicSponza\Art\Sponza\Textures\Bricks`
- 原始格式：.tif/.tiff（Unity 项目常用），ntc-cli 不支持，先用 Python+PIL 转 PNG
- 5张贴图：Color(Diffuse, sRGB) / Normal / AO / Metalness / Smoothness
- 创建 manifest.json 标注 PBR 语义

**压缩结果**：
```bash
ntc-cli --loadManifest Manifest.json -c -g --gdeflate none -o SponzaBricks.ntc
```
- 训练 100000 步，PSNR 33.62→**51.37 dB**（视觉无损，远超 MetalPlates013 的 46.50 dB）
- 产物 11.2MB（16.03 bpp），Latent compression: None
- 14 通道（Color 3 + Normal 3 + AO 1 + Metalness 3 + Smoothness 3 + padding 1）
- 8 neural mip，每mip 4 layer views，53 BufferView 全部无 GDeflate

**Unity 解析验证**：
- 真 Unity 2021.3.33f1c1 batchmode：**7/7 files parsed successfully**（原 6 个 + SponzaBricks）
- SponzaBricks_report.txt 生成，结构完全正确

**结论**：从真实游戏项目（Classic-Sponza）的 PBR 材质 → tif 转 PNG → ntc-cli 压缩 → .ntc 容器 → Unity C# 解析，完整链路验证通过。ntc-cli 对真实游戏美术资产的压缩质量极佳（51.37 dB），不需要任何算法改进。

### 2026-07-06：on-Load / on-Sample Streaming 通道映射注意
- `on-Load compute` 与 `on-Sample streaming` 现在共用同一套绑定函数：`BindPbrTargetsToMaterial()`；两条路径的贴图路由必须保持一致。
- 流送路径的数据流为：`.ntc metadata` → `BuildPbrLayout()` 推导通道索引 → `NtcTileStreamer.Register()` 保存 `NtcPbrChannelLayout` → `NtcDecompressor.DecompressRegion()` 写入 RT → `BindPbrTargetsToMaterial()` 绑定到原材质 clone。
- 当前约定的 RT 映射：
  - `BaseColor.rgb`：来自 `Color` / `Albedo` / `Diffuse`，sRGB 通道在 compute 内转 linear；`a` 当前固定为 1。
  - `Normal.rgb`：来自 `Normal`，绑定到 `_BumpMap`。
  - `Mask.r`：Metallic；`Mask.a`：Smoothness。若源贴图是 Roughness，compute 内转 `1 - roughness`。
  - `AO.g`：Occlusion，绑定到 `_OcclusionMap`。
- 重要烘焙要求：标量贴图必须在 manifest 写 `channelSwizzle`，否则 `ntc-cli` 可能按 RGB 推断为 3 通道，导致通道错位/浪费 BPP/布料高频失真。已修：
  - `NtcBakeWindow.cs`：`AO/Metalness/Smoothness` 写 `channelSwizzle: "R"`；URP `_MetallicGlossMap` 拆成 `Metalness=R`、`Smoothness=A`。
  - `batch_compress_sponza.py`：同样写入 `channelSwizzle`，并保留 `--gdeflate off`。
- 旧 `.ntc` 风险：之前烘出来的 `Metalness` 可能是 3 通道（例如 `Color 3 + Normal 3 + AO 1 + Metalness 3`），这类文件不能靠 Runtime 修补，必须重烤。重烤后期望布局约为 `Color3 + Normal3 + AO1 + Metalness1 + Smoothness1`。
- 植被/alpha 仍是独立问题：当前 NTC BaseColor RT 的 alpha 固定为 1，cutout/透明材质应保留原 `_BaseMap.a` 或后续把 alpha mask 单独作为 NTC 通道处理。
- 缺失通道处理：如果材质没有对应贴图/通道，不要伪造 NTC 通道；烘焙端跳过该通道并继续烘焙，Runtime 绑定时保留原材质标量（例如 `_Metallic` / `_Smoothness`）。URP `_MetallicGlossMap` 只有源图真的有 alpha 时才把 `Smoothness=A` 写入 manifest；如果实际图片缺 A（PIL 读到 RGB），Bake 只打印 WARNING 并跳过 Smoothness，不让整个材质失败；Roughness View 显示材质标量常量。
- 测试辅助：`NtcSceneController` 面板新增 `View` 按钮组（`Lit / Base / Normal / Metal / Rough`），使用 `NTC/PBRChannelDebug_URP` 非光照 shader 查看当前模式下对应材质的 PBR 通道。该 View Mode 只用于验证 on-Load/on-Sample 解码与流送通道映射，不代表最终材质 shader；调试 shader 走 `SRPDefaultUnlit` 且 `Cull Off`，避免布料/植被双面材质在通道查看中消失。
- 质量测试建议：高频布料/植被不要用 16 bpp 判断最终质量；`NtcBakeWindow` 的 BPP 滑条已扩到 64，建议先用 32 或 48 bpp 重烤单个问题材质对比。若官方 `ntc-cli --loadCompressed ... -D` 输出 PNG 也错位/串位，是烘焙/源图/manifest 问题；若官方 PNG 正常但 Unity RT 错位，才继续查 Unity 解码常量/采样逻辑。

### 2026-07-06（续）：★ 重大 bug 修复 —— latent 采样多余的 `.bgra` swizzle 导致 unpack 系统性失真
**根因**：官方 `Inference.hlsli` 对 latent 采样结果做 `sampledValue.bgra` swizzle，是因为它把 latent 数据绑定成**真实硬件 `BGRA4_UNORM`** 格式采样：该格式的物理位布局是 `bit0-3=B, bit4-7=G, bit8-11=R, bit12-15=A`，GPU 按格式定义自动把 (r,g,b,a) 解包成 `(nibble2, nibble1, nibble0, nibble3)`，所以需要 `.bgra` 把它还原成物理 nibble 顺序 `(nibble0,1,2,3)`。
但 Unity 端 `NtcAssetBuilder.BuildLatentTexture()` 没有用（也无法用）原生 `BGRA4_UNORM`，而是手动拆出 4 个 nibble **按物理顺序直接写入 `RGBA32`** 纹理（`r=nibble0, g=nibble1, b=nibble2, a=nibble3`）。Unity 对普通 `RGBA32` 采样不做任何格式相关重排，采样结果**已经是** `(nibble0,1,2,3)`，即已经等价于官方代码 `.bgra` 之后的结果。
`NtcDecompress.compute` 和 `NtcInference.hlsl`（on-Load 与 on-Sample 共用同一个 `assets.LatentTexture`）之前照抄了官方的 `.bgra`，在我们的数据布局下相当于把已经排好的 `(nibble0,1,2,3)` 又变成 `(nibble2,1,0,3)`——即**每 4 个 latent 输入特征中 nibble0 与 nibble2 被互换**，污染了送入 MLP 的全部 32 个 latent 输入通道，导致解码结果系统性失真/错位（不是 bpp 量化噪声，是真正的数据错位 bug）。
**修复**：移除 `NtcDecompress.compute`（第164行附近）和 `NtcInference.hlsl`（`NtcSampleLatentGrid` 内）两处的 `.bgra` swizzle，直接使用采样结果 `s` / `sampledValue`。
**影响范围**：同时修复 on-Load（compute shader 全图解码）和 on-Sample streaming（tile 推理）两条路径，因为二者共用同一份 `BuildLatentTexture()` 产出的 latent 纹理。
**待验证**：需要在 Unity 里重新触发一次 unpack（无需重新烘焙 .ntc，纯 shader 端修复），对比修复前后同一材质的画面差异，预期应显著消除之前反馈的"贴图跑串/失真/摩尔纹"现象。

### 2026-07-06（续2）：★ 第二个 bug 修复 —— 缺少与 Unity 纹理 V 轴约定匹配的垂直翻转，导致解码贴图"下移错位"
**背景**：`.bgra` swizzle 修复后用户反馈"没有区别，仍然出现texture的明显下移动错位"，说明还有第二个独立 bug。已排查并排除：latent mip 尺寸公式（`FeatureGridMath::GetGridDimension`，与 Unity mip 减半公式一致）、`_SrcRect`/`_DstOffset` 偏移错误（`NtcDecompressor`/`NtcTileStreamer` 确认 `_DstOffset` 恒为 `(0,0)`）、latent 数组顺序错位。

**根因**：Unity 对**导入的磁盘图片资源**（PNG 经 `TextureImporter` / `Texture2D.LoadImage`）会在导入时做一次垂直翻转，使内部第 0 行对应视觉上的"底部"（V=0=底部，与 `GetPixel/SetPixel` 文档"(0,0) 为左下角"一致）。但：
- `NtcDecompress.compute` 直写的 `RWTexture2D`（RenderTexture UAV）**不经过**这个导入翻转，`SV_DispatchThreadID` 直接对应内存行，是"顶部朝下"的原始 raster 约定（与 ntc-cli 训练时的像素索引一致）。
- `NtcInferenceOnSample_URP.shader` 里从 mesh UV（Unity 标准 V=0=底部约定）反推的 `texel` 坐标，直接喂给了用训练时原始 raster 约定寻址的 latent 网络，方向对不上。
- latent `Texture2DArray`（`NtcAssetBuilder.BuildLatentTexture()`，走 `SetPixelData`）同样不经过导入翻转，保持原始文件顶部朝下顺序——这与 MLP 内部数学（positional encoding、latent UV 寻址）是自洽的，**不应改动**。

这两处（compute 输出写入位置 + on-Sample fragment 的 texel 计算）与 "Original" 模式（走标准导入翻转的贴图）及 legacy on-Load PNG 路径（`Texture2D.LoadImage` 同样会翻转）方向不一致，导致解码结果在视觉上整体垂直错位，即用户描述的"明显下移动错位"。

**修复**：
1. `NtcDecompress.compute`（`NtcDecompressMain` 末尾）：在写入 4 个输出 UAV 前新增
   ```hlsl
   int2 outPosition = int2(dstPosition.x, mipSize.y - 1 - dstPosition.y);
   ```
   `_OutBaseColor`/`_OutNormal`/`_OutMask`/`_OutAO` 全部改写到 `outPosition`。`pixelPosition`/`uv`（latent 采样、positional encoding 坐标）保持不变，只翻转最终输出写入位置。
2. `NtcInferenceOnSample_URP.shader`（`frag()`）：算出 `texel` 后立即
   ```hlsl
   texel.y = imageSize.y - 1 - texel.y;
   ```
   使其重新对齐训练时的原始 raster 行，再传给 `NtcSampleTextureSet()`。

**影响范围**：同时覆盖 on-Load（compute 全图解码）、on-Sample streaming（`NtcTileStreamer` 走同一 compute kernel）、on-Sample legacy 逐像素 fragment 路径。`NtcAssetBuilder.BuildLatentTexture()` 未改动。
**已知安全前提**：翻转基准使用 `mipSize.y`（即 `_MipImageSize.y`），仅在目标纹理完整高度等于该 mip 尺寸时成立——已确认当前所有调用点 `_DstOffset` 恒为 `(0,0)`，该前提成立。
**验证状态**：`read_lints` 检查两处改动文件均为 0 错误；**尚待用户在 Unity 中实测确认**（无需重新烘焙 `.ntc`）。

### 完整里程碑状态
| 里程碑 | 状态 | 说明 |
|--------|------|------|
| M0 资产管线验证 | ✅ | 7/7 .ntc 文件解析成功（含 Sponza 真实材质） |
| M1 Inference on Load | ✅ | ntc-cli 解压 PSNR 45.26 dB（MetalPlates013） |
| M2 Unity 集成 | ✅ | .ntc → Texture2DArray + ComputeBuffer |
| M3 Inference on Sample | ✅ | Shader 编译通过，MLP 数学移植完成 |
| M3.5 Sponza 真实材质 | ✅ | PSNR 51.37 dB，7/7 解析成功 |
| M4 DP4A 优化 | ⏭️ | 需 SM6.4+DX12，性能优化 |
| M5 批量化/SRP Batcher | ⏭️ | 多材质合批 |

---

## 2026-07-09：排查"贴图偏暗/缺高光"反馈 —— 三个独立结论 + 一个真 bug 修复并验证

**背景**：用户反馈截图里柱子/金属材质表面偏暗、缺少镜面高光，怀疑是 NTC 解码通道错误。这次排查过程本身很有代表性——三个看起来像同一个 bug 的现象，最后拆成了三个完全独立的根因，值得记录排查方法论。

### 排查方法论：先问清楚"对比的是哪两个模式"，再动代码
一开始只有"偏暗、缺高光"的模糊反馈，容易顺着"通道解码错了"的方向一头扎进 shader/compute 代码。**先用几个问题把现象锁定到具体模式/具体区域**，比直接看代码高效得多：
- 追问"哪张截图对应哪个模式"（Original / on-Load / on-Sample），一下排除了一半的可能性。
- 追问"具体哪个区域缺高光"（柱子/金属表面，而不是整体场景），把范围从"全局渲染管线"收窄到"具体材质的具体通道"。

### 结论 1（设计限制，非 bug）：`NtcInferenceOnSample_URP.shader` 是纯 Unlit 展示 shader
`frag()` 只做逐像素 MLP 解码出 BaseColor 三通道就 `return half4(rgb, 1)`，完全没有调用 URP 光照/BRDF、没有 Metallic/Smoothness 输出。`README_NTC.md` 也写明 on-Sample 模式"不参与完整光照"。**只要截图时用的是 on-Sample，缺高光是必然的**，跟通道解码对错无关。如果需要 on-Sample 也能出高光，需要另外扩展 shader 加入 URP Lit BRDF 计算——这是功能缺失，需要单独排期，不是本次要修的 bug。

### 结论 2（材质设计本身，非 bug）：石柱类材质本来就没有强高光
用 Python 读取源 `.tif` 与 NTC 解压 `.png` 的像素统计对比（`PIL + numpy`，`arr.mean()/min()/max()`），发现 `Column_A`（石柱）等材质：
- `Metallic` 源文件本身就是全 0（石材/雕塑非金属），压缩前后完全一致。
- `Smoothness` 源文件均值 54.59，压缩后 54.57，几乎无损。

也就是：石柱在 Original 模式下本来就不会有强反射高光，NTC 也没解码错，是材质设计如此。用户看到的"整体偏暗"更多是画面本身处于阴影区域。

**经验**：怀疑压缩质量问题时，**不要只看解压后的图像"看起来暗不暗"，一定要拉源文件做像素级 min/max/mean 对比**——很多"看起来不对"的贴图其实源头就是这个数值，压缩完全没有损失。

### 结论 3（真 bug，已定位+修复+验证）：金属材质的 Metalness 通道被压缩共享 bpp 预算"压扁"
用同样的方法批量扫了全部材质（`Ceiling/Column_B/C/Floor/Ivy/Lion_Head/Roof/Vase_*` 等 + 真金属材质 `Flagpoles`），发现：
- 非金属材质（石材/雕塑/布料等）：`Metallic` 源本来就是 0，压缩后也是 0，**没有问题**。
- **`Flagpoles`（旗杆，真金属）**：源文件 `Metallic` 均值 **201.84**（≈79%金属度），NTC 解压后骤降到 **27.46**（≈11%金属度）——**真实的数据损失**，导致金属反射几乎完全消失。

**根因**：NTC 把一个材质的所有通道（Color/Normal/AO/Metalness/Smoothness）压进同一个共享 bpp 预算的神经网络里训练。当某个通道是接近二值/高对比度的 mask（比如"金属 vs 非金属"这种非渐变、非平滑的通道），且其他通道（尤其 Color/Normal）信息量更大、主导了训练损失时，这个 mask 通道会被"压扁"到接近某个常数值，与 bpp 高低本身关系不大——是**通道间信息量不均衡导致的训练损失分配问题**，不是通道 swizzle/格式错位（这两点在 07-06 已经修过，且已确认那两个修复覆盖的是 latent 采样和贴图垂直翻转，跟这次的 Metallic 数值压扁是完全独立的问题）。

之前已经有人在 `Manifest.json` 里给 `Metalness` 加了 `lossFunctionScale: 8.0` 尝试修复，但**8倍权重仍不够**，压缩后金属度还是从201.84掉到27.46（几乎没起作用）。

**本次验证过的修复方案（用 `ntc-cli` 直接跑压缩+解压，对比像素均值）**：

| lossFunctionScale | bpp | 解压后 Metallic 均值 | 结论 |
|---|---|---|---|
| 8（原配置） | 16 | 27.46 | 损坏 |
| 24 | 16（**文件大小不变**） | 201.80 | **恢复**（几乎完美匹配源 201.84） |
| 8（不变） | 20（ntc-cli 支持的 bpp 上限） | 201.66 | 恢复 |
| 24 | 20 | 201.83 | 恢复（无额外收益） |

**关键发现**：**单独把 `lossFunctionScale` 从 8 提到 24（不需要动 bpp，不增加文件大小）就足以让金属度几乎完美恢复**，是成本最低的修复方式；提高 bpp 也能达到类似效果，但会增加文件体积。两者叠加没有额外收益。

**顺带发现的另一个小坑**：`ntc-cli` 的 `KnownLatentShapes` 预设表（`RTXNTC/libraries/RTXNTC-Library/src/PublicFunctions.cpp`）**bpp 上限只到 20**（`{20.0f, {2, 16}}`），超过 20 会报错 `Cannot select a latent shape for X bpp.`。但 `NtcBakeWindow.cs` 里的 UI 滑条此前允许拉到 **64**，这是一个误导性的 UI bug——用户拉高滑条到 30+ 直接会看到烘焙失败报错，且报错信息本身没有解释这个硬性上限。已同步修正滑条上限为 20。

**已实施的代码修复**：
1. `Assets/NtcPlugin/Editor/NtcBakeWindow.cs`：
   - `_metalLossScale` 默认值 `8f → 24f`（`kPrefMetalLossScale` 的 fallback）。
   - `Bits per pixel` 滑条上限 `64f → 20f`，并在 HelpBox 里说明 ntc-cli 的硬性 bpp 上限来源。
   - `Metalness loss scale` 的 HelpBox 补充本次验证数据（Flagpoles 201.84→27.46→201.80 的实测对比），并建议"只坏了 mask 通道时优先调这个权重，而不是加 bpp"。
2. `RTXNTC/batch_compress_sponza.py`（这是本次排查中发现的**另一个未生效脚本**：`TEXTURE_MAP` 元组缺 `loss_scale` 字段、`BPP` 变量从未定义，脚本本身跑不起来，之前 Flagpoles 实际是用一次性内联脚本压的，日志里能看到 `loss_scale={'Metalness': 8.0}` 但源码里根本没有这个逻辑）：
   - `TEXTURE_MAP` 加回 `loss_scale` 字段，`Metallic` 项设为 `24.0`。
   - 补上缺失的 `BPP = "16"` 常量定义（+ 注释说明 20 bpp 上限）。
   - `process_material()` 里把 `loss_scale` 写入生成的 `Manifest.json`（`lossFunctionScale` 字段）。

**尚未做（按需再做）**：还没有重新批量烘焙全部材质验证 Unity 里的实际视觉效果——上面的验证都是用 `ntc-cli` 命令行直接跑的压缩/解压/像素对比，没有经过 Unity 的 on-Load/on-Sample 完整链路。如果要让 Flagpoles 在游戏里真正呈现好的金属高光，需要：① 用 Unity `NtcBakeWindow` 面板重新 Rebake `Flagpoles`（现在默认值已经是 scale=24），② 在 Original/on-Load 模式间切换对比，确认视觉上金属反射恢复。

### 本次排查中复核过、确认"没有问题"的历史修复（避免重复排查）
- **latent `.bgra` swizzle 修复**（07-06）：覆盖 `NtcDecompress.compute` + `NtcInference.hlsl` 的 `NtcSampleLatentGrid`，影响的是全部 32 个 latent 输入特征通道（Color/Normal/AO/Metalness/Smoothness 全部共享同一份 latent，不区分具体 PBR 通道）。这次的 Metallic 数值压扁问题发生在 latent→MLP 解码之后的训练损失分配层面，跟 swizzle 是两个完全不同的阶段，**互不影响，本次不需要改动**。
- **纹理垂直翻转修复**（07-06 续2）：覆盖 compute 输出写入位置 + on-Sample fragment 的 texel 坐标，解决的是"图案整体错位/下移"，跟通道数值大小无关，**本次不需要改动**。

### 给后续排查的经验清单
1. **反馈"偏暗/缺高光"时，先确认具体是哪个 View/Mode**（Original / on-Load / on-Sample，或 `NtcSceneController` 的 `Lit/Base/Normal/Metal/Rough` View 按钮），避免在错误的抽象层排查。
2. **怀疑压缩质量问题，永远用像素统计做实证**，不要凭视觉印象：`PIL.Image.open(path).convert('L')` 转灰度后 `numpy` 求 `min/max/mean`，源文件 vs 解压文件一比就知道是"材质本来就这样"还是"真的被压坏了"。
3. **NTC 的共享 bpp 预算机制**：多个通道共享同一个训练预算，高对比度/二值化的 mask 类通道（金属度、cutout alpha 等）在与 Color/Normal 等信息量大的通道共训时容易被压扁。`lossFunctionScale` 是最省成本的解法（不增加文件大小），必要时才叠加提高 bpp。
4. **ntc-cli 的 bpp 硬上限是 20**（`KnownLatentShapes` 预设表决定），任何 UI/脚本里出现比 20 更大的默认值/滑条上限都是潜在的误导性 bug。
5. **写批量脚本前先跑一次确认脚本本身没有裸名引用等基础 bug**——`batch_compress_sponza.py` 这次被发现有一个 `BPP` 未定义直接会导致 `NameError`，说明它自 07-06 写完后从未被完整跑过一次（真正跑 Flagpoles 用的是当时的另一段内联脚本）。

---

## 2026-07-09（续3）：论文核对 + Roughness/Glossiness 语义标签 bug + Normal 偏蓝排查

### A. 核对官方论文《Random-Access Neural Compression of Material Textures》是否有 lossFunctionScale 阈值说明

用户提供了论文 PDF，要求核实上面 `lossFunctionScale: 8 → 24`（Flagpoles 金属通道修复）这个实测数据是否已经在论文里有对应说明。用 `pypdf` 提取全部 15 页正文逐页核对：

- **§4.5 Optimization Procedure and Loss Function**：对比了 SSIM / VGG / L1 / L2 / 组合损失函数的取舍（Figure 6：纯 L2 保留颜色保真但丢高频细节，L2+SSIM 反之），最终论文选用纯 L2 作为标准损失，理由是"训练最鲁棒、最简单、算得最快"，**没有讨论按通道设置不同权重**。
- **§7.2 Limitations（Uniform Resolution 段落）**：唯一相关的一句话——"Our method can assign different levels of importance to textures by weighting its contribution to the loss"，只是提到"可以按通道加权 loss"这个概念，**完全没有给出任何具体权重数值、阈值范围或推荐值**。

**结论：论文中没有与本项目实测的 `lossFunctionScale 8→24` 对应的具体说明或数据**。这部分是本项目独有的实测经验，记录如下（与 07-09 首次记录的结论 3 合并看）：

| lossFunctionScale | bpp | 解压后 Metallic 均值（源 201.84） | 结论 |
|---|---|---|---|
| 8（原配置，论文默认权重的 8 倍） | 16 | 27.46 | 损坏 |
| 24 | 16（文件大小不变） | 201.80 | 恢复 |
| 8（不变） | 20（bpp 上限） | 201.66 | 恢复（靠堆 bpp） |
| 24 | 20 | 201.83 | 恢复（无额外收益） |

**经验补充**：论文给的是"可以加权"这个机制存在性的保证，但具体权重需要每个项目自己按材质的通道对比度实测调——8 倍不够、24 倍才够，这个倍数本身跟具体资产（Flagpoles 金属遮罩的二值化程度、跟同材质里 Color/Normal 的相对信息量）强相关，没有放之四海而皆准的公式，遇到类似"某个 mask 通道被压扁"问题时应该按同样的方法（换 loss scale + 对比源/解压均值）实测搜索，而不是照抄某个固定倍数。

### B. Roughness/Glossiness 语义标签错标 bug（已修复）

**现象**：用户注意到烘焙产物的 manifest 语义标签里，Smoothness 通道被标成了 `"Roughness"`。

**根因排查**：
- 项目源数据文件是 `*_Smoothness.tif`，语义上是 **smoothness/glossiness**（光滑度，数值越大越光滑），而不是 **roughness**（粗糙度，二者是 `1 - x` 的反相关系）。
- `RTXNTC/docs/Manifest.md` 和 `ntc-utils/include/ntc-utils/Manifest.h` 确认 `Roughness` 和 `Glossiness` 是两个独立且都合法的 `SemanticLabel`，不能混用。
- 但两处代码都把 Smoothness 纹理的 manifest `semantics` 字段错误写成了 `"Roughness"`：
  - `Classic-Sponza-NTC/Assets/NtcPlugin/Editor/NtcBakeWindow.cs`（`kTextureSpecs` 数组）
  - `RTXNTC/batch_compress_sponza.py`（`TEXTURE_MAP` 列表）

**影响范围评估**：查看 `NtcAssetBuilder.cs` 的 `BuildPbrLayout()`，Unity 运行时判断某个通道是 Roughness 还是 Smoothness 是按纹理的 **`Name` 字段字符串匹配**（`Contains("rough")` / `Contains("smooth")`），**不读** manifest 的 `semantics` 字段。所以：
- **不影响 Unity 自身的 on-Load / on-Sample 渲染效果**——这是个纯元数据 bug。
- 但是**错误的标签会误导任何读取 manifest 语义信息的外部工具**（例如 NVIDIA 官方 `ntc-explorer`），把光滑度数值当粗糙度显示，视觉上刚好是"反相"的效果（本该亮的地方显示暗，反之）。

**修复**：
- `NtcBakeWindow.cs`：`Smoothness` 纹理的 `TextureSpec` semantics 从 `"Roughness"` 改为 `"Glossiness"`。
- `batch_compress_sponza.py`：`TEXTURE_MAP` 里 Smoothness 条目的 semantics 从 `{"Roughness": "R"}` 改为 `{"Glossiness": "R"}`。
- 两处都补充了代码注释说明源数据本质、bug 由来、影响范围，避免以后再被误改回去。

### C. Normal 贴图"压缩后偏蓝、有 bias"排查（排查结论：整体偏蓝是固有正常现象，非 bug；发现另一个独立的轻微细节衰减现象）

**现象**：用户观察到压缩后的 Normal 贴图看起来偏蓝，怀疑压缩引入了颜色偏差（bias）。

**排查过程**：
1. 先查代码：`NtcDecompress.compute`（Normal 通道直接写 `float4(nx, ny, nz, 1.0)`，无额外色彩空间转换）、`NtcSceneController.cs`（`_BumpScale` 统一设为 1，未做额外处理），确认解码链路没有引入人为的偏色逻辑。
2. 用 Python（PIL + numpy）对 Flagpoles / Ivy 两个材质的源 `Normal.tiff` 与 NTC 解压 `Normal.png` 做 RGB 三通道均值对比：两者几乎完全一致（例如 Flagpoles B 通道：源 245.52 vs 解压 245.57），**没有发现额外的整体偏蓝色偏**。
3. 批量扫全部 17 个材质的 B 通道（法线 Z 分量，切线空间约定下几乎所有像素都接近 255）均值/标准差比值，`stdRatio` 全部落在 0.93~1.0 区间，说明压缩整体保真度良好。

**结论 1（固有现象，非 bug）**：切线空间法线贴图的编码约定是 `R=X, G=Y, B=Z`，而绝大多数表面法线接近垂直于表面（Z 分量接近满值），所以法线贴图**视觉上天然整体偏蓝**——这是格式约定决定的固有现象，跟是否压缩、压缩质量高低都无关，不是 bug。

**结论 2（真实但独立的轻微现象，与"偏蓝"是两件事）**：进一步对比 R/G 通道（法线 X/Y 分量，代表法线扰动细节）相对中值 127.5 的平均绝对偏差比例，发现部分材质——尤其是那些解压分辨率被放大了 2 倍的（`Column_A/B/C`、`Lion_Head`、`Lion_Shield`、`Roof`、`Vase_Octagonal`、`Vase_Round`）——存在约 **4%~7% 的细节强度轻微衰减**（ratio 落在 0.92~0.98）。这是压缩本身正常的轻微有损表现（细节高频信息在有损压缩中最先受影响），**不是**用户描述的"偏蓝 bias"问题，两者是完全不同的两个现象，容易被混为一谈，特此在日志里明确区分。

**待办（如需进一步验证）**：以上排查全部基于命令行 `ntc-cli` 的压缩/解压像素对比，尚未在 Unity 场景里做 on-Load / on-Sample 的实际视觉对比截图；如果用户希望肉眼确认，需要在 `NtcSceneController` 面板切到 `Normal` View 逐材质对比 Original vs on-Load/on-Sample。

---

## 环境预检结果（全部通过，除 CUDA Toolkit）

| 项目 | 状态 | 详情 |
|------|------|------|
| 显卡 | ✅ | RTX 5060, 8GB VRAM, Driver 596.36 |
| Git / CMake / Python | ✅ | 均正常 |
| VS 2022 | ⚠️ | 只装了 BuildTools（够用，已验证可编译） |
| CUDA Toolkit | ❌ | 未安装（无 nvcc）——只影响 ntc-cli / ntc-explorer，不影响 ntc-renderer |

## 关键发现

1. **exFAT 问题已解决**：`E:` 盘是 exFAT，CMake 解压依赖包会失败。方案：源码留在 `E:\Git\NTC Test\RTXNTC`，构建输出目录放到 NTFS 盘 `C:\Dev\RTXNTC_build`。
2. **CUDA Toolkit 依赖分析**（与原指南"不需要CUDA"的说法不同，此仓库版本实际要求）：
   - `tools/cli`（ntc-cli，压缩工具）→ **硬性要求** CUDA Toolkit
   - `tools/explorer`（ntc-explorer，质量查看器）→ **硬性要求** CUDA Toolkit
   - `support/tests/bctest` → 也要求 CUDA Toolkit
   - `samples/renderer`（ntc-renderer，渲染对比） → **不需要** CUDA Toolkit ✅
   - `libraries/RTXNTC-Library`（libntc 核心库）→ CUDA 是可选的（`NTC_WITH_CUDA` 选项，已设 OFF）
3. 为此**修改了 `RTXNTC/CMakeLists.txt`**，新增两个选项使 cli/explorer 可选跳过：
   - `NTC_BUILD_CLI`（默认 ON）
   - `NTC_BUILD_EXPLORER`（默认 ON）
   - 并给对应的 `set_target_properties` 加了 `if(TARGET ...)` 保护

## ✅ 已完成并验证通过

1. SDK 已克隆到 `e:\Git\NTC Test\RTXNTC`
2. CMake 配置成功：`C:\Dev\build_cmake.bat`
   ```
   cmake -S "E:\Git\NTC Test\RTXNTC" -B "C:\Dev\RTXNTC_build" -DCMAKE_BUILD_TYPE=Release ^
     -DNTC_WITH_CUDA=OFF -DNTC_BUILD_CLI=OFF -DNTC_BUILD_EXPLORER=OFF -DNTC_WITH_TESTS=OFF ^
     -DDONUT_WITH_DX12=OFF -DNTC_WITH_RENDERER=ON
   ```
3. 编译成功：`C:\Dev\build_compile.bat`（调用 vcvarsall.bat x64 + `cmake --build ... --config Release -j8`）
4. 产物位置：`E:\Git\NTC Test\RTXNTC\bin\windows-x64\ntc-renderer.exe`（+ `libntc.dll`）
5. **SDK 自带预压缩好的 FlightHelmet 模型**（无需 ntc-cli 现场压缩）：
   - `assets/models/FlightHelmet/FlightHelmet.ntc.gltf`
   - 对应 `.ntc` 材质文件（HoseMat / MetalPartsMat / GlassPlasticMat / LeatherPartsMat / LensesMat）
6. **已成功启动 ntc-renderer.exe**（不带参数，自动加载内置 FlightHelmet 场景）：
   - 窗口标题：`NTC Scene Renderer (Vulkan, NVIDIA GeForce RTX 5060)`
   - 进程正常运行，Vulkan 后端工作正常

## 下一步（回家/继续时可直接做）

### A. 立即可做（不需要任何安装）
- 在 Renderer 窗口里按 **Tab** 键切换 Reference / BC7 / NTC-on-Load / NTC-on-Sample 模式，肉眼对比 + 看右上角 VRAM/FPS 数字
- 用 **WASD + 鼠标** 环绕观察模型
- 用 **Save Screenshot** 按钮截图保存对比结果
- 参考 `ntc_zero_code_test.md` 里第 4 节的验收清单逐项打勾

### B. 如果想体验完整压缩流程（ntc-cli / ntc-explorer / 自定义材质压缩）
需要安装 **NVIDIA CUDA Toolkit**（约 3-4GB，管理员权限）：
- 下载地址：https://developer.nvidia.com/cuda-downloads
- 装好后，重新用以下命令跑 CMake（去掉 `-DNTC_BUILD_CLI=OFF -DNTC_BUILD_EXPLORER=OFF`）：
  ```
  cmake -S "E:\Git\NTC Test\RTXNTC" -B "C:\Dev\RTXNTC_build" -DCMAKE_BUILD_TYPE=Release ^
    -DNTC_WITH_CUDA=OFF -DDONUT_WITH_DX12=OFF -DNTC_WITH_RENDERER=ON
  ```
  （注：libntc 库本身的 NTC_WITH_CUDA 仍可保持 OFF，压缩用的是 ntc-cli 自己链接的 CUDA runtime，与库的训练加速是两件事——如果发现 compress 报错缺 CUDA kernel，再把 NTC_WITH_CUDA 也设 ON 重新配置编译）
- 然后重新执行 `cmake --build C:\Dev\RTXNTC_build --config Release -j8`
- 之后可以按 `ntc_zero_code_test.md` 第 2-3 节，用 `ntc-cli.exe compress` 压缩自己的材质，再用 `ntc-explorer.exe` 查看 PSNR

### C. 用 Python 脚本压缩其他 GLTF 模型（同样不需要 ntc-cli，但需要 CUDA！）
`support/tools/convert_gltf_materials.py` 脚本内部实际调用的还是 CUDA 加速的压缩逻辑，所以这条路一样需要 CUDA Toolkit，不能绕过。

## 关键文件/路径备忘

- 源码：`e:\Git\NTC Test\RTXNTC`（已修改顶层 `CMakeLists.txt`，加了 NTC_BUILD_CLI / NTC_BUILD_EXPLORER 选项）
- 构建输出目录：`C:\Dev\RTXNTC_build`（NTFS）
- CMake 配置脚本：`C:\Dev\build_cmake.bat`
- 编译脚本：`C:\Dev\build_compile.bat`
- 编译产物：`E:\Git\NTC Test\RTXNTC\bin\windows-x64\ntc-renderer.exe`
- 原始测试指南：`e:\Git\NTC Test\ntc_zero_code_test.md`
- Renderer 使用文档：`e:\Git\NTC Test\RTXNTC\docs\Renderer.md`

## 继续时可以直接说

"继续 NTC 测试，帮我看看 Renderer 里 Tab 切换后的对比结果" 或 "帮我装 CUDA Toolkit 继续测 ntc-cli" 等。

"在 Unity 里用 NtcBakeWindow 重新 Rebake Flagpoles，确认金属高光是否恢复" —— 对应 2026-07-09 记录的 Metalness 通道压扁 bug 修复，代码侧默认值已改好（`metalLossScale` 默认 24），需要在真实场景里 Rebake + 切换 Original/on-Load 对比验证。

## 2026-07-10：Roughness/Smoothness 通道保真度不足排查 + 修复

### 背景
用户判断"主要差异问题集中在 roughness 通道，怀疑签名不匹配"，要求确认 roughness 确实参与了计算，并适配 Unity Lit Shader。

### 排查结论：数据链路本身没有问题
逐层复核了 `NtcAssetBuilder.BuildPbrLayout()` → `NtcDecompressor.DecompressRegion()`（`_MaskChannels` uniform 传参）→ `NtcDecompress.compute`（`ChannelValue()` 从 `outputs[]` 读取真实 4 层 MLP 前向传播结果，`_MaskChannels.z` 控制 Roughness→Smoothness 转换 `smooth = 1.0 - smooth`）→ `NtcSceneController.BindPbrTargetsToMaterial()`（`EnableKeyword("_METALLICSPECGLOSSMAP")` 正确设置），确认 **Roughness/Smoothness 数值确实来自完整 MLP 真实推理输出，没有被跳过或替换为常量**。

同时排除了以下"签名不匹配"候选假设：
- `_METALLICSPECGLOSSMAP` / `_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A` 两个 URP keyword 的驱动逻辑（`LitGUI.cs`）——管理正确。
- 全部 25 个材质的 `_SmoothnessTextureChannel` 属性——全部为 `0`（SpecularMetallicAlpha，正确值），没有材质误配置为读 BaseMap alpha。
- on-Load（PNG 路径 + compute 路径）、on-Sample streamed 路径——都正确绑定到标准 URP Lit Shader 材质。
- **例外**：legacy per-pixel on-Sample shader（`NtcInferenceOnSample_URP.shader`）是纯 Unlit，只输出 BaseColor，不支持 Metallic/Smoothness/完整光照——这是已知设计限制，非 bug（README_NTC.md 已注明）。

### 真正的根因：Smoothness 通道从未获得 loss 权重加成，保真度低于其他通道
用 Python（`PIL+numpy`，bicubic 放大源 `.tif` 到解压分辨率后比较像素标准差比值，避免分辨率不匹配导致的采样误差）对全部 17 个已烘焙材质做像素级实证对比：

| 通道 | 平均保真度(std ratio) | 典型范围 |
|---|---|---|
| Color | ~0.999 | 几乎无损 |
| AO | ~0.987 | 1~4% 损失 |
| Normal | ~0.980 | 2~5% 损失 |
| **Smoothness** | **~0.94** | **2~18% 损失（最差 Ceiling 0.824）** |

根因与 2026-07-09 的 Metalness 压扁 bug 是**同一机制的另一半**：NTC 共享 bpp 预算训练下，`NtcBakeWindow.cs` 和 `batch_compress_sponza.py` 里 **只给 Metalness 配置了 `lossFunctionScale=24`，Smoothness 从未配置过（默认权重=1）**，导致其在与 Color/Normal 等信息量大的通道共训时长期处于劣势，细节被压扁。

### 已实施的修复
1. `Classic-Sponza-NTC/Assets/NtcPlugin/Editor/NtcBakeWindow.cs`：新增 `_smoothLossScale` 字段（EditorPrefs 持久化，默认 16，UI 滑条范围 1~32），三层调用链（`StartBake`→`BakeQueueWorker`→`BakeSingleWorker`）透传，`WriteManifestFile()` 新增 `isSmoothOrRough` 语义判断（匹配 Glossiness/Roughness/Smooth），写入 manifest 的 `lossFunctionScale` 字段。
2. `RTXNTC/batch_compress_sponza.py`：`TEXTURE_MAP` 里 Smoothness 条目的 `loss_scale` 从 `None` 改为 `16.0`，与 Editor 窗口默认值保持一致。

### 待验证（下次可直接做）
"用新的 Smoothness loss scale 重新 Rebake Ceiling，确认高光细节是否改善" —— 代码侧默认值已改好（`smoothLossScale` 默认 16），需要在 Unity NtcBakeWindow 里重新 Rebake（尤其是 Ceiling 这个最差案例 std ratio 0.824），再重跑像素对比脚本验证 std ratio 是否提升，最后切换 Original/on-Load/on-Sample 做视觉对比确认高光/粗糙度表现改善。如果 16 不够，可参考当年 Metalness 从默认调到 24 的过程逐步上调（滑条上限 32）。

### 后续排查：位深 / BRDF 简化位置 / 参数可解释性
- 源 `*_Smoothness.tif` 本身就是 8bit（`BitsPerSample=(8,8,8)`），DCC 导出层没有更高精度可留；MLP 内部数学是 fp16，无损；唯一被人为砍到 8bit 的是 Unity 侧的 Mask 输出 RenderTexture（一直用 `R8G8B8A8_UNorm`）。已修复：`NtcDecompressor.cs` 的 `AllocateTargets`/`CreateTarget` 给 Mask 通道单独升级到 `R16G16B16A16_UNorm`（`SystemInfo.IsFormatSupported` 检测，不支持则回退 8bit）。定量上 8bit 量化误差（~0.4%）远小于实测的 Smoothness 保真度损失（6~18%），所以这只是加固，不是根因修复（根因仍是 loss weight）。
- "roughness 应该放 G 通道" 的经验来自 BC1/565 打包（G 通道 6bit）和 UE/glTF 的 ORM 贴图惯例，我们的 Roughness/Smoothness 是独立单通道 BC4 纹理，R/G/B/A 只是命名，物理精度完全相同，换字母没有收益。
- Roughness 参与的 BRDF 简化位置：`LitInput.hlsl` `smoothness` → `BRDF.hlsl` `InitializeBRDFDataDirect()`（`perceptualRoughness = 1-smoothness` → `roughness = perceptualRoughness²` → `roughness2 = roughness²`）→ `DirectBRDFSpecular()`。这是 URP 的 **Minimalist Cook-Torrance BRDF**：标准 GGX NDF + Kelemen-Szirmay-Kalos 近似 Visibility 项 + `1/LoH` 近似 Fresnel，三项揉合成一个表达式（出自 Siggraph 2015《Optimizing PBR for Mobile》），是移动端性能优先简化，非完整桌面级 multi-scatter GGX。
- 参数可解释性：`NtcBakeWindow.cs` 的 Metalness/Smoothness loss scale 滑条标签改为显示 "Nx vs others"（如 "24x vs others"），HelpBox 文案说明其含义是"相对于其他通道隐含的 1x 权重，这个通道被赋予多少倍训练损失权重"，不再是不知道单位的裸数字。

## 2026-07-10（续）：Debug 视图 "金属过补偿" bug 修复

### 现象
用户切换 Original ↔ on-Load 时，Metal debug 视图（View: Metallic）显示金属度像是"过补偿"了，怀疑 roughness/metallic 参与计算有差异或签名不匹配。

### 排查结论：不是 MLP/渲染管线的问题，是 debug 视图 shader 自己的逻辑 bug
逐行对比了 `PBRChannelDebug_URP.shader`（debug 视图）和 URP 真实渲染路径 `LitInput.hlsl` 的 `SampleMetallicSpecGloss()`：

- **真实渲染**：当 `_METALLICSPECGLOSSMAP` keyword 开启时，`metallic = specGloss.r`（纹理值），**完全不乘 `_Metallic` 标量**；标量只在没有贴图时才作为纯标量使用。
- **Debug shader（bug 所在）**：`mode==3` 分支写的是 `float m = SAMPLE_TEXTURE2D(_MetallicGlossMap, ...).r * _Metallic;`——无条件多乘了一次 `_Metallic` 标量，且完全没有"没有贴图走纯标量"的分支。

Sponza 场景里绝大多数材质的 `_Metallic` 标量 = 0（Ceiling/Bricks/Floor/Column_A/Column_B/Details/Ivy/Lion_Head/Lion_Shield/Vase_*/Roof/Vase_Octagonal 等 25 个材质里有大约一半），而 `BindPbrTargetsToMaterial()`（on-Load/on-Sample 路径）在纹理里检测到 metal 通道时会把 `_Metallic` 标量强制设为 1（真实渲染里这个标量在有贴图时本就不起作用，所以这个强制设置是安全、正确的）。但 debug shader 会把这个标量错误地乘进去：
- Original 模式下 debug 视图显示 `纹理值 × 0 = 全黑`（看起来完全没有金属）
- on-Load 模式下 debug 视图显示 `纹理值 × 1 = 纹理真实值`（突然出现所有金属细节）

这个人为放大的对比差异就是用户观察到的"金属过补偿"，但它只存在于 debug 视图里，从未影响过真实光照渲染（真实渲染两种模式下 `_Metallic` 标量的取值差异根本不参与计算）。

### 已实施的修复
1. `PBRChannelDebug_URP.shader`：新增 `_NtcHasGlossMap` 属性；`mode==3`（Metallic）和 `mode==4`（Roughness）分支改为与 `SampleMetallicSpecGloss()` 完全一致的双路分支——有贴图时纯采样（metallic 不再乘标量），无贴图时纯标量（不采样贴图）。
2. `NtcSceneController.cs` 的 `BindDebugViewMaterial()`：新增 `hasGlossMap = src.HasProperty(kMetallicGlossMap) && src.IsKeywordEnabled("_METALLICSPECGLOSSMAP")` 判断（按 keyword 而非"槎位是否有纹理"判断，与真实shader的编译期分支完全对齐），写入 `_NtcHasGlossMap`。

### 验证状态
代码修改已完成，lint 检查无错误。**尚未在 Unity 里实际运行验证**——下次可以直接说"进 Play 模式切 Original/on-Load，看 Metal debug 视图现在是否还有跳变"来确认。

---

## 2026-07-10（续2）：★ 根因 —— PIL 读 TIFF alpha 失败导致 Smoothness 100% 被跳过（已修复+已验证 Roughness 恢复可见）

### 现象
用户反馈：即使重新 Rebake 过，Play 模式切到 `Rough` View 依然完全看不到粗糙度（空白/常量），跟 07-10 修的 loss scale / debug shader 过补偿都不是同一个问题。

### 完整数据链路回溯（本次逐段验证过，全部正常，问题出在最上游）
```
*_MetallicSmoothness.tif（PS导出，物理4通道）
   └─ NtcBakeWindow.ConvertToPng()（内嵌Python+PIL，从A通道切出Smoothness）── ★ 根因在这里，被静默跳过
   └─ WriteManifestFile() 写 Manifest.json（channelSwizzle/semantics/lossFunctionScale）
   └─ ntc-cli --loadManifest -c 压缩成 .ntc（MLP训练）
   └─ NtcAssetBuilder.BuildPbrLayout() 按纹理Name字符串匹配解析通道布局
   └─ NtcDecompressor.DecompressRegion() 传 _MaskChannels uniform
   └─ NtcDecompress.compute 用4层MLP真实推理，_MaskChannels.z 控制 Roughness→Smoothness 的 1-x 转换
   └─ NtcSceneController.BindPbrTargetsToMaterial() 绑定RT到材质，设 keyword
   └─ PBRChannelDebug_URP.shader mode==4（Rough）采样显示
```
本次实测（用 `ntc-cli --describe` 检查当天 16:37-17:00 刚重新 Bake 出的全部 24 个 `.ntc`）：**清一色只有 8 通道**（Color3+Normal3+AO1+Metalness1），**一个 Smoothness 都没写进去**——链路下游（manifest/压缩/解压/debug shader）全部正常也没用，因为 Smoothness 从最上游就没被采集。

### 根因
`ConvertToPng()` 判断"源图有没有 alpha"用的是 `'A' not in img.getbands()`。但项目里全部 `*_MetallicSmoothness.tif`（Photoshop 导出）虽然物理上是 4 sample/pixel（`SamplesPerPixel=4`，第4通道是真实 smoothness 数据，实测 Ceiling alpha 均值 36.3、Flagpoles 均值 48.7，数据完好），Photoshop 把第4通道的 TIFF `ExtraSamples` 标记写成了 `UNSPECIFIED`（既非 `ASSOCALPHA` 也非 `UNASSALPHA`）。PIL 对这种非标准标记保守处理，只暴露 `mode='RGB'`，`getbands()` 永远不含 `'A'`。于是判断永远为真 → **Smoothness 对全部 24 个材质、每次 Bake 都被当作"缺失通道"静默跳过**（只留一条容易被日志滚动缓冲区冲掉的 WARNING）。

### 修复
`Classic-Sponza-NTC/Assets/NtcPlugin/Editor/NtcBakeWindow.cs` 的 `ConvertToPng()`：在原有 `needs_alpha and 'A' not in img.getbands()` 判断前，新增一段容错——检测到 `img.mode=='RGB'` 且底层解码器 tile 其实是 `RGBX`/`RGBXX` 原始编码时，手动 patch `img._mode='RGBA'` + 重写 `img.tile` 的 rawmode 为 `'RGBA'` 再 `img.load()`，强制把第4个 sample 读出来当作真正的 alpha，然后再走原有逻辑（真的没有第4通道才会走跳过分支）。已用真实 `Ceiling_MetallicSmoothness.tif` 跑通 Python 逻辑验证（alpha mean=36.27，与之前 `tifffile` 直读的结果完全一致）。

### 验证结果（✅ 已确认修复生效）
用户在 Unity 里用 `NtcBakeWindow` 重新 Rebake 后，Play 模式切到 `Rough` View **确认粗糙度已经可见**，问题解决。

### 影响范围与风险提示
- **只影响 Editor Bake 阶段**（`NtcBakeWindow.ConvertToPng()`），不影响运行时解码/渲染逻辑本身——之前几次排查的 loss scale、debug shader 过补偿、latent swizzle、垂直翻转等 bug 修复依然全部有效，互不冲突。
- **独立脚本 `RTXNTC/batch_compress_sponza.py` 尚未同步这个 fix**：它是另一套走命令行批量压缩的路径，`ConvertToPng`/PIL 逻辑是各自独立实现的，如果之后用这个脚本重新烤，需要检查它是否有一样的 alpha 检测方式，否则会复现同样的问题。
- **该 patch 依赖 PIL 内部私有实现细节**（`img._mode`、`img.tile` 的 rawmode 元组结构），是绕过 PIL 保守判断的 hack，不是标准公开 API。如果以后升级 Pillow 版本，`TiffImagePlugin` 内部对 `tile`/`_mode` 的组织方式可能变化，这段 patch 可能失效（需重新适配）或者不再必要（如果新版本修了这个保守判断）。
- **仅针对 `RGBX`/`RGBXX` rawmode 做了特判**：如果以后新增的贴图源文件用了其他 TIFF 压缩/编码方式（比如 LZW 压缩的 4通道 TIFF，rawmode 可能不是 `RGBX`），这个条件判断会不满足，退回到原来的静默跳过行为——即，这个修复只覆盖了当前 Sponza 项目实际遇到的这一种 TIFF 变体，不是通用的"所有4通道TIFF alpha检测"修复。
- **潜在的根本预防措施**：更彻底的解决方法是在美术导出环节要求 Photoshop（或转出流程）把 TIFF 的 `ExtraSamples` 明确标记为 `UNASSALPHA`，这样 PIL/绝大多数图像库都能直接正确识别为 RGBA，不需要在代码里做这种 hack 式的兼容处理。如果后续接入新的美术资产/新的 DCC 工具导出的 TIFF，仍建议先用 Python 脚本抽查 `getbands()`/`ExtraSamples` 确认，而不是默认信任 Bake 日志里没有 WARNING。
- **日志可见性问题也建议后续跟进**：当前 `NtcBakeWindow` 的日志缓冲区只保留最后10行，这次问题本可以更早从 WARNING 里发现，但被后续输出冲掉了。如果以后还要排查类似"某通道静默缺失"的问题，建议先加大日志缓冲区或把 WARNING 单独持久化到文件，而不是仅凭 UI 滚动日志。

### 2026-07-13：★ 修复 alpha-test/cutout 植被材质（插片草）暴露插片边缘问题

**问题**：Unity 中带 alpha cutout 的植被材质（`Ivy`、`Vase_Round_Plants`、`Vase_Hanging_Chain`，均为 `_ALPHATEST_ON` + `RenderType: TransparentCutout`）在 on-Load/on-Sample 模式下渲染时，本应被裁剪掉的透明区域没有裁剪，插片草的矩形/十字插片轮廓完全暴露。这是 2026-07-06 就记录在案但当时未修复的已知问题（见上文"植被/alpha 仍是独立问题"条目）。

**根因（系统性功能缺失，非孤立 bug）**：从烘焙到运行时的整条链路都从未处理过 alpha 通道：
1. `NtcBakeWindow.CollectTextures()` / `batch_compress_sponza.py` `TEXTURE_MAP` 只采集 Diffuse/Normal/AO/Metallic/Smoothness，从未采集 `_BaseMap` 自身的 alpha 通道。
2. `NtcAssetBuilder.NtcPbrChannelLayout` 结构体压根没有 Alpha 字段，容器解析层直接丢弃了这个概念。
3. `NtcDecompress.compute` 的 `_OutBaseColor[outPosition] = float4(r, g, b, 1.0);` 硬编码 alpha=1.0，无论源材质是否 cutout。
4. legacy PNG 路径（`ApplyOnLoad`）里 `Color.png` 直接绑定到 `_BaseMap`，同样没有 alpha 数据来源。

实测确认 `Ivy_Diffuse.tif`/`Vase_Round_Plants_Diffuse.tif`/`Vase_Hanging_Chain_Diffuse.tif` 的第4通道（PIL 因 Photoshop `ExtraSamples=UNSPECIFIED` 误判为 RGB，与 2026-07-10 的 Smoothness bug 同一根因）确实含有真实的双峰分布 cutout mask 数据——问题是数据从未被读取/传输，不是源图缺数据。

**修复**（跨 5 个文件的协同改动，复用 RTXNTC 官方 `AlphaMask` 语义机制，见 `docs/SettingsAndQuality.md`）：
- `NtcBakeWindow.cs`：新增 `TextureSpec("Alpha", "Alpha", false, "BC4", "AlphaMask", "A")`；新增 `AddAlphaCutoutFromBaseMap()`，仅当材质 `_AlphaClip=1` 或 `_ALPHATEST_ON` 且源图确实有 alpha 时，才把 `_BaseMap`/`_MainTex` 的 alpha 通道单独采集为 `Alpha` 标量纹理（`channelSwizzle="A"`）参与 manifest/烘焙。
- `NtcAssetBuilder.cs`：`NtcPbrChannelLayout` 新增 `int Alpha`/`bool HasAlpha`；`BuildPbrLayout()` 新增按纹理名 `alpha`/`cutout`/`opacity` 识别的分支。
- `NtcDecompress.compute`：新增 `int _AlphaChannel` uniform；`_OutBaseColor.a` 从硬编码 `1.0` 改为 `_AlphaChannel >= 0 ? ChannelValue(outputs, _AlphaChannel) : 1.0`（无 Alpha 通道的材质保持向后兼容的 alpha=1）。
- `NtcDecompressor.cs`：`DecompressRegion()` 透传 `_cs.SetInt("_AlphaChannel", layout.HasAlpha ? layout.Alpha : -1)`。
- `NtcSceneController.cs`：新增 `ntcAlpha` 字段 + `LoadDecompressedTextures()` 加载 `Alpha.png`（legacy PNG 路径）；新增 `MergeAlphaIntoColorMap()`，把独立的 `Alpha.png`/`ntcAlpha` 合并进 `Color.png` 的 alpha 通道后再绑定到 `_BaseMap`（ntc-cli 把每个 NTC 纹理解压成独立 PNG 文件，`Color.png` 本身没有可用 alpha）。compute 路径（on-Load mode2 / on-Sample streaming mode3）无需额外绑定改动，因为两者共用的 `BindPbrTargetsToMaterial()` 已经只是直接设置 `_BaseMap = t.BaseColor`，而 `t.BaseColor` 这个 RT 的 `.a` 已经在 compute shader 里被正确写入。
- `batch_compress_sponza.py`：新增 `ALPHA_CUTOUT_MATERIALS = {"Ivy", "Vase_Round_Plants", "Vase_Hanging_Chain"}` 白名单 + `extract_alpha_cutout()`（复用同一段 PIL RGBX/RGBXX hack 从 Diffuse 源图提取 alpha 存为 `Alpha.png`），与 `NtcBakeWindow.cs` 保持同步。

**尚待验证**（需要在 Unity Editor 里实测，本次改动未包含）：
- 需要对 `Ivy`/`Vase_Round_Plants`/`Vase_Hanging_Chain` 三个材质执行 Rebake（旧 `.ntc` 没有 Alpha 通道，无法靠 Runtime 补丁修复，必须重烤）。
- Rebake 后需在 Original/on-Load/on-Sample 三种模式下对比插片边缘裁剪是否正确，并检查 alpha cutout 是否引入新的噪点/边缘闪烁（`docs/SettingsAndQuality.md` 提到 NTC 对强非线性 alpha 阈值可能引入类似静态噪点的边缘效应，尽管文档说"regular cutouts like tree leaves work fine"）。
- `--discardMaskedOutPixels` 选项（进一步提升被裁剪像素之外区域的图像质量）尚未接入 `NtcBakeWindow`/`batch_compress_sponza.py` 的命令行参数，可作为后续优化项。

### 2026-07-13：工作区重组 + NtcPlugin 通用化（去场景特化）

**背景**：之前 NtcPlugin 与 Classic-Sponza-NTC 示例工程耦合在一起，且插件代码里有 4 处 Sponza 硬编码，无法作为独立 Unity 包分发。重组为 4 个独立分区，并把插件通用化。

**通用化（去 Sponza 硬编码）**——`NtcBakeWindow.cs` / `NtcSceneController.cs` / `NtcSceneControllerEditor.cs`：
1. 纹理源目录：`TryFindTexturesByConvention` 原硬编码 `"ClassicSponza/Art/Sponza/Textures"` → 新增可配置字段 `_textureSourcePath`（EditorPrefs 持久化），空则跳过约定查找。
2. `.ntc` 文件名前缀：烘焙输出（`StartBake`）与状态检查（`CheckBakeStatus`）原硬编码 `"Sponza"` 前缀 → 新增 `_ntcFilePrefix`（烘焙端）/ `ntcFilePrefix`（运行时 `FindNtcFile`），空=无前缀。烘焙输出的 decompressed 目录名同步改为用 `ntcOutput` 文件名，与运行时 `LoadDecompressedTextures` 的查找逻辑一致。
3. ntc-cli 路径探测：`AutoDetectNtcCli` 原硬编码 `"../../RTXNTC/bin/windows-x64/ntc-cli.exe"` → 改为探测工程根与 `Packages/` 下候选，找不到回退 PATH 的 `ntc-cli.exe`。
4. HelpBox 文案：`NtcSceneController` 的 "is this the Sponza scene?" 提示 → 改为通用排查指引。

残留的 "Sponza" 字样仅存在于注释/tooltip 文档说明中（不影响逻辑）。`batch_compress_sponza.py` 的 `ALPHA_CUTOUT_MATERIALS` 白名单是 Sponza 专用脚本的一部分，留在示例侧，不属于插件。

**工作区重组**（`e:\Git\NTC Test\` 下 4 个子目录）：
- `nvidia-backend/` ← 原 `RTXNTC/`（ntc-cli、libntc、docs，含独立 .git 仓库整体移动，历史保留）。散落的 `ntc_zero_code_test.md`（SDK 入门指南）归入此目录。
- `unity-plugin/` ← 原 `Classic-Sponza-NTC/Assets/NtcPlugin/` 的完整副本（Runtime/Editor/Shaders/Resources/NtcData + README）。作为干净的可分发 Unity 插件源。`NTC_Unity_Architecture.md` 归入此目录。
- `unity-showcase/` ← 原 `Classic-Sponza-NTC/`（Sponza 场景 + 美术资产 + NtcPlugin 副本，示例能独立运行）。**注意：此重命名因 Unity Editor 正在运行锁定工程文件而未完成，目录仍名为 `Classic-Sponza-NTC`，需关闭 Unity 后执行 `Move-Item Classic-Sponza-NTC unity-showcase`。**
- `ue-showcase/` ← 原 `UE-NtcPlugin/`（UE 版本插件，后续 plugins showcase 继续）。

`PROGRESS.md` 留在根目录作为跨项目日志。`ntc_layout_report.txt` / `GlassPlasticMat.json.txt`（Sponza 材质的 NTC 容器调试 dump）暂留根目录，待 unity-showcase 改名后归入。

**编译验证**：重组前已用 cmake + MSVC 2022 + CUDA 12.9 成功重新编译 `nvidia-backend/bin/windows-x64/ntc-cli.exe`（Release），`--help` 输出正常，`--discardMaskedOutPixels` 选项确认存在。

**重组完成**：`Classic-Sponza-NTC → unity-showcase` 重命名在关闭 Unity Editor 后成功执行；`ntc_layout_report.txt` / `GlassPlasticMat.json.txt` 已归入 `unity-showcase/`。根目录现仅剩 `PROGRESS.md`。

### 2026-07-13：UE 插件（ue-showcase）alpha cutout 同步 + 通用化

**背景**：UE 插件（`ue-showcase/`，原 `UE-NtcPlugin/`）镜像 Unity NtcPlugin，但 alpha cutout 修复（同日给 Unity 做的）尚未同步。重组后 ntc-cli 路径也需更新（RTXNTC→nvidia-backend）。

**同步改动**（跨 6 个文件，与 Unity 修复一一对应）：
- `NtcAssetBuilder.h`：`FNtcPbrChannelLayout` 新增 `int32 Alpha`/`HasAlpha()`。
- `NtcAssetBuilder.cpp`：`BuildPbrLayout()` 新增 `alpha`/`cutout`/`opacity` 命名识别分支。
- `NtcDecompress.usf`：新增 `int AlphaChannel` uniform；`OutBaseColor.a` 从硬编码 `1.0` 改为 `AlphaChannel >= 0 ? NtcChannelValue(Outputs, AlphaChannel) : 1.0`。
- `NtcDecompressor.h`：`FNtcDecompressCS::FParameters` 新增 `SHADER_PARAMETER(int32, AlphaChannel)`。
- `NtcDecompressor.cpp`：`DecompressRegion()` 透传 `Parameters->AlphaChannel = Layout.HasAlpha() ? Layout.Alpha : -1`。
- `NtcBakeWindow.cpp`：`MakeDefaultSlots()` 新增 `Alpha (cutout mask)` slot（semantic `AlphaMask`，swizzle `R`）；`AutoDetectNtcCli()` 从硬编码 `../RTXNTC/...` 改为探测 `nvidia-backend`/`RTXNTC` 多级路径 + 回退 PATH；`WriteManifest()` 的 `bcFormat` 从全 BC7 改为按 semantic 选择（标量通道 BC4，RGB 通道 BC7），对齐 Unity `kTextureSpecs`。

`NtcSceneComponent` 无需改动：它只负责 LoadAndDecompress 并把结果 RT（含已正确写入 alpha 的 BaseColor）存到 `Targets`，用户通过 `GetTargets()` 自取绑定材质，alpha 自然传递。

**已知限制**（`NtcSceneComponent.h` scope note 已记录）：UE 版本当前只实现 Original + InferenceOnLoad 两种模式，per-tile InferenceOnSample streaming（Unity 的 `NtcTileStreamer.cs`）未移植。Mode 枚举已预留，后续可加。

**测试方式**：将 `ue-showcase/` 整个目录放入 UE 工程的 `Plugins/NtcPlugin/`，编译工程，给 Actor 添加 `NtcSceneComponent`，设置 `NtcFilePath` 指向 .ntc 文件，调用 `LoadAndDecompress()`。可用 BakeWindow（NTC 菜单）先烘焙含 Alpha 通道的 .ntc 验证 cutout。

### 2026-07-13：UE 插件测试工程集成 + shader 编译修复

**测试工程**：`E:\SceneProject\Unreal-SpaceDesert\`（UE 5.7，PBRSet），插件已 robocopy 到 `Plugins/NtcPlugin/`。

**修复 1 — shader 虚拟路径**（运行时崩溃 `Couldn't find source file of virtual shader path '/Plugin/NtcPlugin/NtcDecompress.usf'`）：`NtcPluginModule` 把 `/Plugin/NtcPlugin` 映射到 `Source/NtcPlugin/Shaders`，所以必须带子目录。
- `NtcDecompressor.cpp`：`IMPLEMENT_GLOBAL_SHADER` 路径 → `/Plugin/NtcPlugin/Private/NtcDecompress.usf`。
- `NtcDecompress.usf`：`#include "NtcInference.ush"` → `#include "/Plugin/NtcPlugin/Public/NtcInference.ush"`。

**修复 2 — global shader 编译错误**（`NtcInference.ush:52` `condition for short-circuiting ternary operator must be scalar`）：UE5.7 的 DXC 不允许三元运算符用向量条件。`Ntc_UnpackS8` 里 `v - (v > 127 ? 256 : 0)`（`v` 为 int4）→ 改为 `v - (int4(v > 127) * 256)`（bool4→int4 分量级，无需 `select` 内建，各 shader model 通用）。已扫描两个 shader 文件其余三元表达式，条件均为标量，无需改动。

**C++ 编译**：`Build.bat PBRSetEditor Win64 Development` 成功（NtcPlugin + NtcPluginEditor 两模块，31.5s）。shader 为纯 `.ush` 改动，无需重编 C++，清 `Intermediate/Shaders` 后由 Editor 启动时重新编译。

### 2026-07-13：UE 场景级 NtcSceneController + NtcTileStreamer 实现（对齐 Unity）

**背景**：UE 插件此前只有单 Actor 级 `NtcSceneComponent`（仅 InferenceOnLoad，解压到 RT，不接材质、不扫场景）。用户要求按 Unity 同逻辑实现完整的场景级控制器：自动扫描材质 → 匹配 .ntc → 批量替换 → on-Sample tile 流送。

**新增文件**（6 个）：
- `NtcPlugin/Public/NtcTileStreamer.h` + `Private/NtcTileStreamer.cpp` — on-Sample tile 流送调度器，端口自 `NtcTileStreamer.cs`。含 `FNtcRenderTargetSet`（RHI 纹理 + UTexture2D 双重包装）、`FNtcStreamEntry`（Entry）、`FNtcTileStreamer`（Register/Tick/驱逐/预算）。Tick 在 game thread 收集可见性+距离+预算 tile，enqueued render command 跑 DecompressRegion。
- `NtcPlugin/Public/NtcSceneController.h` + `Private/NtcSceneController.cpp` — 场景级控制器，端口自 `NtcSceneController.cs`（1411行→精简为~450行 UE 版）。含 `FNtcMaterialGroup`（USTRUCT）、`ENtcSceneMode`/`ENtcViewMode` 枚举、`ScanSceneMaterials()`、`SetMode()`（渐进式，一 group/Tick）、`ApplyOriginal/OnLoad/OnSample`、`TickStreaming()`、`BindTargetsToMaterial()`（按材质参数名启发式匹配）。
- `NtcPluginEditor/Public/NtcSceneManagerWindow.h` + `Private/NtcSceneManagerWindow.cpp` — Slate 可停靠窗口，含 Scan 按钮、三模式切换按钮、五通道视图按钮、实时状态/统计文本。注册为 `NTC Scene Manager` Tab。

**修改文件**（2 个）：
- `NtcPluginEditor/Public/NtcPluginEditorModule.h` — 新增 `NtcSceneManagerTabName` + `SpawnSceneManagerTab()`。
- `NtcPluginEditor/Private/NtcPluginEditorModule.cpp` — 注册 Scene Manager Tab。

**关键设计决策**（UE 与 Unity 的差异点）：
1. **RHI 纹理 → UTexture2D 包装**：UE 材质参数需要 `UTexture*`，而 `FNtcDecompressor::CreateTarget` 产出的是 `FTextureRHIRef`（`PF_R8G8B8A8`，UAV 兼容）。`UTextureRenderTarget2D` 的 `RTF_RGBA8` 映射到 `PF_B8G8R8A8`（非 UAV 兼容，compute 无法写），所以改用 `UTexture2D::CreateTransient` 创建包装，然后在 render thread 替换其 `FTextureResource::TextureRHI` 指向解压器 RHI。
2. **材质参数匹配**：UE 没有 Unity 的固定 `_BaseMap`/`_BumpMap` 属性名。`BindTargetsToMaterial` 遍历源材质的 `UMaterialExpressionTextureSampleParameter2D` 获取参数名，按名称含 `basecolor`/`normal`/`metallic`/`rough`/`occl` 启发式匹配，先查 Normal/Metallic/Roughness/AO 再查 Color 避免 `BaseColorNormal` 误匹配。
3. **GC 根**：`FNtcRenderTargetSet` 里的 `UTexture2D*` 是裸指针，由 Controller 的 `UPROPERTY TArray<UTexture2D*> TrackedTextures` 统一 GC 根化。
4. **同步构建**：`EnsureGpuData` 用 `FlushRenderingCommands()` 同步等待渲染线程完成 Build（`FNtcAssetBuilder::Build` 需 `FRHICommandListImmediate`）。一 group/Tick 渐进式处理避免一次性卡死。
5. **可见性**：Tile 流送用 `UPrimitiveComponent::GetLastRenderTime()` 判断"最近渲染过"（<1s 视为可见），距离用 `Bounds.Origin` 到相机。

**M0-M3 里程碑对照**：
| 里程碑 | Unity | UE | 状态 |
|--------|-------|-----|------|
| M0 材质显示 | `BindPbrTargetsToMaterial` | `BindTargetsToMaterial` | ✅ |
| M1 场景扫描+批量替换 | `AutoScanScene` + `MaterialGroup` | `AutoScanScene` + `FNtcMaterialGroup` | ✅ |
| M2 on-Sample 流送 | `NtcTileStreamer.cs` | `NtcTileStreamer.h/.cpp` | ✅ |
| M3 工具/UI | `OnGUI()` IMGUI | `SNtcSceneManagerWindow` Slate | ✅ |

**使用方式**：
1. 打开 UE 工程，菜单 `Window > NTC Scene Manager`（或 Tools 分类下）。
2. 点 `Add Controller` 在场景中创建带 `NtcSceneController` 组件的 Actor。
3. 在 Controller 的 Details 面板设 `NtcDataPath` 指向 .ntc 文件目录。
4. 点 `Scan Scene Materials` 扫描场景材质并匹配 .ntc。
5. 点 `on-Load` 或 `on-Sample` 切换模式。状态/统计文本实时更新。

**已知限制**（后续可迭代）：
- on-Load 使用 `FlushRenderingCommands` 同步等待（每 group 一帧），大量材质时会有逐帧微卡。可改为完全异步（FThreadSafeBool 轮询）。
- View mode 切换（BaseColor/Normal/Metallic/Roughness 调试视图）目前只是重新 Apply 当前模式，尚未实现通道隔离显示材质（Unity 用 `PBRChannelDebug_URP` shader）。需要新建 UE 调试材质。
- 材质参数名匹配为启发式，不支持自定义映射表。

### 2026-07-14：修复 alpha 通道启发式匹配 + 补齐 Mipmap 生成

**问题1（用户指出）**：`BindTargetsToMaterial` 的启发式关键词（`basecolor`/`normal`/`metallic`/`rough`/`occl`/`color`）遗漏了 alpha/cutout/opacity 通道。NTC 的 compute shader 把 cutout mask 写进 `BaseColor.a`（`Layout.HasAlpha()` 为 false 时 `.a=1.0`，向后兼容），但材质里常有独立的 `OpacityMask`/`AlphaMap`/`Cutout` 纹理参数，之前完全没被匹配到，会导致 alpha 测试材质渲染出错误的不透明度。

**修复**：`NtcSceneController.cpp` 的 `BindTargetsToMaterial` 新增一条匹配分支——参数名含 `alpha`/`opacity`/`cutout` 时绑定 `Targets.BaseColor`（alpha 数据本就在其 `.a` 通道），且该分支排在 `basecolor`/`color` 之前，避免 `BaseColorAlpha` 之类的名字被 catch-all 分支先截胡。特意不匹配裸的 `mask` 关键字——UE Megascans 工作流里 `MaskMap` 通常指 ORM 贴图（R=AO,G=Rough,B=Metal），与本插件的 Mask RT 布局（R=Metallic,A=Smoothness）不同，混淆会绑错纹理。

**问题2（已知限制项）**：on-Load 和 on-Sample 路径都没有生成 mipmap，远处表面会走样（Unity 版本两条路径都有 mip 生成）。

**修复**：
- `NtcSceneController.cpp` 的 `ApplyOnLoad`：`Allocate` 改为 `bMips=true`，decompress 后在同一个 render command 里用 `FGenerateMips::Execute`（`GenerateMips.h`，box-filter downsample）对四张 RT 各生成一次 mip chain，与 decompress 分成两个独立 `FRDGBuilder`（避免同一 external RHI 在一个 graph 里注册两次）。
- `NtcTileStreamer.cpp`：`FNtcRenderTargetSet::Allocate` 调用处改为 `bMips=true`；`Tick()` 的 tile work item 新增 `bGenMipsAfter` 标记，当某个 tile 使 `ResidentCount` 达到 `TileResident.Num()`（缓存全部填满）时置位；render command 里在该 tile 的 `DecompressRegion` 之后追加一次 `FGenerateMips::Execute`，对齐 `NtcTileStreamer.cs` 里 `NtcDecompressor.GenerateMips(e.targets)` 的调用时机。

**受影响文件**：`NtcSceneController.cpp`（alpha 匹配 + on-Load mips）、`NtcTileStreamer.h/.cpp`（on-Sample mips）。均已通过 lint 检查，零编译错误。

### 2026-07-14：UE NtcBakeWindow 补齐 Scene Batch Bake（对齐 Unity）

**背景**：用户指出 UE 侧 `NtcBakeWindow` 只有手动单材质烘焙（手选 5 张源文件），缺少 Unity 侧 `NtcBakeWindow.cs` 的"扫描场景材质 → 列表 → 批量烘焙"工作流。原头文件注释明确写了"intentionally the smallest useful slice... not a full scene-batch-bake UI"，现在补齐。

**改动**（`NtcBakeWindow.h/.cpp`）：

1. **新增 `FNtcSceneBakeEntry` 结构**：`MaterialName` + `TWeakObjectPtr<UMaterialInterface>` + `TextureSlots`（自动收集的纹理槽位） + `bSelected`/`bHasNtc`/`NtcPath`/`NtcSize`/`Status`/`Psnr`，对齐 Unity `MaterialEntry`。

2. **`OnScanScene()`**：遍历当前 World 所有 `AActor` 的 `UPrimitiveComponent`，按材质插槽收集 `UMaterialInterface*`，对 `MaterialInstanceDynamic` 回溯到父材质名（避免 `_INST_xxx`），按名去重后对每个材质调用 `CollectTexturesFromMaterial`。

3. **`CollectTexturesFromMaterial()`**：两条路径收集纹理参数：
   - `UMaterialInstance`：直接读 `TextureParameterValues`（`FTextureParameterValue`）
   - `UMaterial`（或 MI 的 Parent）：遍历 `GetExpressionCollection().Expressions` 找 `UMaterialExpressionTextureSampleParameter2D`
   
   然后 `AddTextureToEntry` 按参数名启发式匹配 semantic（`alpha`/`normal`/`metallic`/`rough`/`smooth`/`occl`/`basecolor`/`color`），和 `NtcSceneController::BindTargetsToMaterial` 使用相同关键词集，保证"烘焙时收集的纹理"与"运行时绑定的材质参数"一致。

4. **`GetTextureSourcePath()`**：从 `UTexture2D::AssetImportData->SourceData.SourceFiles[0].AbsoluteFilename` 获取源文件磁盘路径（标准导入器写入的原始 PNG/TIF 路径，非 .uasset）。无 AssetImportData 的纹理跳过。

5. **`OnBakeSelected()` + `BakeNextInQueue()` + `HandleBatchBakeCompleted()`**：串行队列烘焙。每个材质走 convert→manifest→ntc-cli compress→（可选）decompress preview。`HandleBatchBakeCompleted` 里提取 PSNR、更新 `bHasNtc`/`NtcSize`/`Status`，然后 `BakeQueueIndex++` 继续下一个。

6. **UI**：`BuildSceneBatchSection()` 在窗口顶部加：Scan 按钮、Select All/None/Unbaked/Baked(Rebake) 按钮、可滚动材质列表（`EntryListBox`，`OnScanScene` 末尾 `ClearChildren`+`AddSlot` 重建）、Bake All Selected 按钮、实时进度文本。原有单材质模式保留在下方。

**受影响文件**：`NtcBakeWindow.h`（加 `FNtcSceneBakeEntry` + 10 个方法声明 + `EntryListBox`）、`NtcBakeWindow.cpp`（+~350 行：scene scan + collect + batch queue + UI）、`NtcPluginEditor.Build.cs`（注释更新）。已迁移到测试工程 `E:\SceneProject\Unreal-SpaceDesert\Plugins\NtcPlugin\`，shader 缓存已清。

**已知限制**：
- 源文件路径依赖 `AssetImportData`。若纹理是通过非标准方式导入（无 SourceData），该材质的纹理会被跳过，列表显示 tex=0。
- 非 PNG 源文件需用户先转 PNG（`ConvertSourceToPng` 只复制 .png，不调 Python/PIL）。Unity 侧用 Python+PIL 转 TIF，UE 侧暂未集成 Python 依赖。
- decompress preview 是异步启动的（不阻塞队列），preview PNG 可能稍后才出现。

### 2026-07-14：打通 Bake → Scene Manager 完整工作流（对齐 Unity）

**问题**：用户反馈"tools 界面没有办法像 Unity 那样烘焙一下再解决"。根因是 Bake 窗口和 Scene Controller 之间没打通：
1. Bake 窗口输出到 `Content/NtcData/`，但 Scene Controller 的 `NtcDataPath` 默认为空 → `ResolveDataPath()` 空时返回 `Content/`（不含 `NtcData/` 子目录）→ 烘焙完的 .ntc 运行时找不到。
2. 两个窗口（Bake + Scene Manager）完全独立，没有快捷入口互通。

**修复**（3 个文件）：

1. **`NtcSceneController.h`**：`NtcDataPath` 默认值从空改为 `TEXT("NtcData")`，和 Bake 窗口输出目录一致。
2. **`NtcSceneController.cpp` 的 `ResolveDataPath()`**：双保险——即使 `NtcDataPath` 被用户清空，也默认搜 `Content/NtcData/` 而不是 `Content/`。
3. **`NtcSceneManagerWindow.h/.cpp`**：新增 `OnOpenBakeWindow()` 方法 + `Open NTC Bake` 按钮，一键从 Scene Manager 跳到 Bake 窗口。用 `FGlobalTabmanager::Get()->TryInvokeTab(FNtcPluginEditorModule::NtcBakeWindowTabName)` 调出 Tab。

**统一后的完整工作流**（和 Unity 一样四步）：
1. Scene Manager → 点 `Open NTC Bake` → Bake 窗口打开
2. Bake 窗口 → `Scan All Project Materials` → 勾选 → `Bake All Selected` → .ntc 生成到 `Content/NtcData/`
3. 回到 Scene Manager → `Scan Scene Materials` → 自动在 `Content/NtcData/` 下匹配 `{MaterialName}.ntc`
4. 点 `on-Load` 或 `on-Sample` → 完成

不需要手动配置 `NtcDataPath`——默认值已经和 Bake 输出目录对齐。
















