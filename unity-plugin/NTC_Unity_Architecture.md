# NTC → Unity 实时神经纹理管线 · 完整框架方案

> 版本：v0.1（草案，用于评审）
> 目标管线：`项目纹理 → Editor右键导出PBR贴图 → ntc-cli压缩 → 导入为NTC资产 → Shader采样时实时MLP推理`
> 核心约束：**不是"解压成普通纹理"，而是在 `tex2D()` 采样点替换成 MLP 推理（Inference on Sample）**

---

## 0. 关键决策复核（基于对 RTXNTC SDK 源码的实测分析）

| # | 决策 | 结论 | 依据 |
|---|------|------|------|
| 1 | 推理模式 = Inference on Sample | **可行，但有硬性前置条件** | 见 §1.3 风险评估 |
| 2 | 优先引擎 = Unity | 可行，架构上比 UE 简单 | Unity 支持 DXC/SM6.x 编译、ComputeBuffer Raw View |
| 3 | MLP实现 = 手写Shader（先）+ LibNTC headers（后） | **建议调整顺序**，见下 | 官方 fallback 本身就是"手写"的 DP4A HLSL，不依赖 CoopVec |
| 4 | 压缩后端 = ntc-cli | 可行，但**必须独立于 Unity 项目**，需 CUDA Toolkit | 已验证：`ntc-cli`/`ntc-explorer` 强制 `find_package(CUDAToolkit REQUIRED)`，Unity 机器不一定有 |

### 1.3 关于"手写 Shader"的重要澄清

官方 SDK 本身提供两套 Inference on Sample 实现，都在 `Inference.hlsli` / `InferenceCoopVec.hlsli` 里，都是"手写 HLSL"，**没有任何黑盒库调用**：

- **`NtcSampleTextureSet`**（`Inference.hlsli`）：基于 `dot4add_i8packed`（DP4a 整数点积指令）的 INT8 MLP 前向推理，**不需要 CoopVec 扩展**，但需要 **Shader Model 6.4+**（DXC 编译，DX12/Vulkan）。
- **`NtcSampleTextureSet_CoopVec_FP8`**（`InferenceCoopVec.hlsli`）：用 Cooperative Vector 硬件加速，需要 RTX 40/50 系 + 驱动支持。

也就是说：**决策3不需要"先手写再升级 LibNTC headers"这两个阶段**——`Inference.hlsli` 本身就是可直接搬进 Unity 的手写 DP4A 版本。真正的分阶段应该是：

- **阶段A（首个原型）**：把 `dot4add_i8packed` 换成逐分量 `float` 乘加模拟（兼容 SM5.0 / 所有 Unity 渠道，性能差但能在编辑器里马上跑通验证正确性）。
- **阶段B（性能优化）**：在 Unity 里启用 DXC + `#pragma target 6.4`，直接使用 `dot4add_i8packed`，对齐官方 DP4A 路径。
- **阶段C（旗舰GPU优化，可选）**：引入 CoopVec 版本（仅 RTX 40/50 系列 + DX12/Vulkan 扩展，Unity 目前没有官方 CoopVec 绑定，需要自行通过 native plugin 暴露，风险较高，建议排到最后）。

### 1.4 MLP 网络结构（已从源码确认，是固定的、不需要训练框架即可复刻）

```
输入层  48 (16个特征×2个neural mip + 12位位置编码 + 2位LOD)
        ↓  Linear(48→64) + HGELUClamp
隐藏层0 64
        ↓  Linear(64→48) + HGELUClamp
隐藏层1 48
        ↓  Linear(48→32) + HGELUClamp     ← 仅4层MLP有此层
隐藏层2 32
        ↓  Linear(32→16)                  ← 输出层，无激活
输出层  16   (最多16个颜色通道，如 BaseColor.rgb / Normal.rgb / ORM / Emissive.rgb / Transmission …)
```

- 权重：INT8，按列存储（ColumnMajor），每层配一个 FP32 scale 向量 + FP32/FP16/Int32 bias 向量。
- 激活函数：`HGELUClamp`：`y = min(x,3) * saturate(x/3 + 0.5)`，量化时按 `round(y*80) - 113` 转回 INT8。
- 输入特征来自 **Latent 纹理**：`Texture2DArray`，格式 `BGRA4`（每层4个特征），双线性采样，wrap 寻址；每个 texel 采样 2 个 neural mip 层（当前mip + 下一级）共 16×2=32 特征，再拼 12 维位置编码 + LOD，补齐到48。
- 这套数学在 `NTC_MLP_*` 宏里是编译期常量（`InferenceConstants.h`），**没有运行时超参数**，只要 Unity Shader 按同样常量重写一遍即可，不依赖 NVIDIA 私有指令集。

### 1.5 文件格式（NTC容器，已确认，可独立解析，不依赖 LibNTC 动态库）

```
File Header (32 bytes, little-endian):
  "NTEX"           4 bytes  签名
  containerVersion 4 bytes  当前 0x100
  jsonChunkOffset  8 bytes
  jsonChunkSize    8 bytes
  dataChunkOffset  8 bytes
  dataChunkSize    8 bytes
──────────────────────────
JSON Chunk: 描述 width/height/numChannels/latentShape/mlpVersions/textures/channels/latents/colorMips/views
Data Chunk: 所有 BufferView 引用的二进制数据（latent 图、权重、bias、scale，可选 GDeflate 压缩）
```

这意味着：**Unity 侧完全可以用纯 C# 写一个 `.ntc` 容器解析器，不需要调用 LibNTC 的 DLL/CUDA**，只在"压缩"这一步才需要 NVIDIA 的 `ntc-cli`。解压/推理所需的全部信息（latent 尺寸、MLP 权重偏移、layer 结构）都在 JSON chunk 里可读。这是本方案能把「压缩」和「Unity运行时」彻底解耦的关键。

---

## 1. 总体架构：三个独立子系统

```
┌─────────────────────┐     .png/.tga PBR贴图 + manifest.json      ┌──────────────────────┐
│  A. Unity Editor     │ ───────────────────────────────────────▶  │  B. 压缩后端服务       │
│     导出插件          │                                            │  (ntc-cli Wrapper)    │
│  (右键导出 + 触发压缩) │ ◀─────────────────────────────────────── │  独立机器/独立环境     │
└─────────────────────┘        .ntc 压缩文件                        │  需 CUDA Toolkit       │
          │                                                          └──────────────────────┘
          │ 导入
          ▼
┌─────────────────────┐
│  C. NTC 资产导入管线   │  纯C#解析.ntc容器 → 生成 Unity 资产:
│  (ScriptedImporter)  │   - LatentTexture2DArray (.asset / Texture2DArray)
└─────────────────────┘   - WeightBuffer (byte[] → ComputeBuffer)
          │                - NtcMaterialAsset (ScriptableObject: 常量、通道映射)
          ▼
┌─────────────────────┐
│  D. Runtime 渲染集成   │  自定义 Shader (HLSL, 手写MLP DP4A) +
│  (Shader + C#组件)    │  C#端每材质绑定 ComputeBuffer/Texture2DArray
└─────────────────────┘
```

**三方解耦原则**（对应用户提出的"压缩、传输、Unity架构分开"）：

1. **压缩后端（A的对端 + 独立进程）**：只认识 `manifest.json + 图片目录` → `.ntc文件`，是一个纯命令行工具封装，**可以跑在任何装了CUDA的机器上**，跟 Unity 版本、引擎无关。
2. **资产传输/序列化格式（.ntc容器）**：是双方的契约，Unity 侧只需要一个纯 C# reader，不需要链接 NVIDIA 的库。
3. **Unity 渲染集成**：只依赖"资产传输"产出的 Texture2DArray + ComputeBuffer + 常量，对压缩后端的实现细节完全无感。

这样压缩后端可以先用命令行脚本占位，以后随时替换为云端压缩服务、批处理队列等，不影响 Unity 侧代码。

---

## 2. 模块 A：Unity Editor 导出插件

**职责**：从 Unity 材质反向抽取 PBR 贴图，生成 ntc-cli 认识的 `manifest.json`。

```
Editor/
  NtcExportWindow.cs         — 右键菜单 "Export to NTC..." 入口 (MonoBehaviour/Material context menu)
  NtcManifestBuilder.cs      — 读取 Material 的 _BaseMap/_BumpMap/_MetallicGlossMap 等贴图
                                按语义(Albedo/Normal/Roughness/Metalness/AO/Emissive)写 manifest.json
  NtcTextureExporter.cs      — 把 Unity Texture2D（含sRGB/线性判断）导出为 PNG/TGA 到临时目录
  NtcCompressJob.cs          — 调用模块B（进程/HTTP），异步等待 .ntc 产出，显示进度条
```

**关键设计点**：
- Manifest 的 `semantics` 字段直接对应 URP/HDRP Lit 材质的标准贴图槎位，做一个固定映射表（可配置）。
- 贴图导出时要保留正确的色彩空间（`isSRGB`），Unity 的 `Texture2D.isDataSRGB`/导入设置可直接读出。
- 支持"单材质"和"批量材质"两种导出粒度；批量时可以把同类材质合批进同一个 NTC 纹理集（省 latent 网格的重复开销），但初期先做 1 material : 1 .ntc 的简单映射。

---

## 3. 模块 B：压缩后端服务（ntc-cli Wrapper）

**职责**：接收 manifest + 图片目录，跑 `ntc-cli`，回传 `.ntc` 文件。**完全独立于 Unity 进程和 Unity 项目**。

### 3.1 形态选择（由轻到重，建议从 v0 开始）

| 版本 | 形态 | 触发方式 | 适用阶段 |
|------|------|----------|----------|
| v0 | 本机命令行脚本 (`compress.bat`/`compress.ps1`) | Unity Editor 用 `Process.Start` 直接调用本机 `ntc-cli.exe` | 单机开发验证 |
| v1 | 本机常驻 Watcher 服务 | 监视一个 `pending/` 目录，出现 manifest 就跑压缩，产出放 `done/` | 团队本地共享一台带CUDA的"压缩机" |
| v2 | 局域网 HTTP 压缩服务 (FastAPI/ASP.NET) | Unity Editor `HttpClient` POST manifest+贴图 zip，长轮询/webhook 拿 `.ntc` | 团队协作、CI |
| v3 | 云端队列 (对象存储+任务队列) | 上传贴图到 OSS，任务入队，Worker 拉取压缩，结果回传 | 规模化生产 |

初期建议直接做 **v0**：因为我们已经确认 `ntc-cli` 需要独立编译（依赖 CUDA Toolkit，跟当前已编译的、跳过CLI的 Renderer 分支是两回事）。v0 不需要额外开发，只要在装了 CUDA 的机器上编译出 `ntc-cli.exe`，Unity Editor 侧写一个"调用外部exe + 读取返回码/stdout"的封装类即可。

### 3.2 压缩参数封装

```csharp
// 伪代码：NtcCompressJob.cs
struct NtcCompressRequest {
    string manifestPath;
    string outputNtcPath;
    float  targetBpp;      // 或 targetPsnr
    bool   generateMips;
}
// 命令行等价于：
// ntc-cli --loadManifest <manifest.json> -g -c -b <bpp> -D -o <output.ntc>
```

- 质量策略：先用固定 `--bitsPerPixel`（简单、确定性强），后续再切到 `--targetPsnr` 自适应压缩（`IAdaptiveCompressionSession`），做美术可控的"画质优先"选项。
- 每个材质对应一个 manifest + 一个 `.ntc`，导出目录结构：
  ```
  NtcExport/
    <MaterialName>/
      basecolor.png, normal.png, orm.png, emissive.png ...
      manifest.json
      <MaterialName>.ntc      ← 压缩产物，回传给Unity
  ```

---

## 4. 模块 C：NTC 资产导入管线（Unity 侧，纯 C#）

**职责**：把 `.ntc` 二进制文件解析成 Unity 原生资产，供 Runtime 使用。**不依赖 NVIDIA 任何动态库**。

### 4.1 容器解析器 `NtcContainerReader.cs`

按 §1.5 的格式解析：
1. 读 32 字节 Header，校验签名 `"NTEX"`。
2. 读 JSON chunk（`System.Text.Json` 或 `Newtonsoft.Json` 反序列化到 POCO：`NtcDocument/NtcMlp/NtcTexture/NtcLatentImage/NtcColorMip/NtcBufferView`）。
3. 按 `BufferView.offset/storedSize/compression` 从 Data chunk 里切出：
   - 每个 `LatentImage` 的每层 latent 数据（可能 GDeflate 压缩 → 需要一个 C# GDeflate/Deflate 解码器，或先用 `--gdeflate none` 让 ntc-cli 产出不压缩的 latent，简化 v0 实现）。
   - 每层 MLP 的 `weightView` / `scaleView` / `biasView`。

### 4.2 生成的 Unity 资产

| 源数据 | Unity 侧目标 | 说明 |
|--------|--------------|------|
| Latent 图（每 neural mip、每 array layer） | `Texture2DArray`（`TextureFormat.RGBA4444` 或等效格式，逐 texel 手动 swizzle 到 BGRA） | **风险点**：Unity 无原生 `BGRA4`，见 §6 风险清单 |
| MLP 权重(Int8)+scale(FP32)+bias | 拼接成一段 `byte[]`，上传为 `ComputeBuffer(ComputeBufferType.Raw)` | 对应 shader 里的 `ByteAddressBuffer` |
| `NtcTextureSetConstants`（imageWidth/Height/Mips、每 mip 的 neuralMip/positionLod/positionScale、通道颜色空间、权重offsets） | `NtcMaterialAsset : ScriptableObject` 字段 + 每帧填充到 `ConstantBuffer`/`MaterialPropertyBlock` | 直接照抄 `InferenceConstants.h` 的结构体字段 |
| 通道 → PBR 语义映射（`Texture.firstChannel/numChannels` + 语义名） | `NtcMaterialAsset.ChannelMapping` | 用于 shader 里把 `outputs[16]` 分配给 BaseColor/Normal/ORM/Emissive |

用 **ScriptedImporter**（`[ScriptedImporter(1, "ntc")]`）把 `.ntc` 变成 Unity 能直接拖拽使用的资产类型，双击可在 Inspector 里看到解析出的元数据（宽高、通道、bpp等），这样美术也能直观检查压缩结果。

### 4.3 与 Inference on Load 的取舍

一并实现 `.ntc → 解压成普通 Texture2D（BC7等）` 的路径（对应官方 Inference on Load）作为 **调试/对比模式**：
- 用于验证"资产管线"本身是否正确（先用CPU/简单shader把NTC解码成看得懂的贴图，跟原始PBR贴图对比PSNR），
- 排除"latent读取错" vs "MLP shader写错"两类问题的干扰，建议**先打通 Inference on Load 对比模式，再攻 Inference on Sample**。

---

## 5. 模块 D：Runtime 渲染集成（Shader + C#）

### 5.1 C# 端（每材质一次性绑定）

```csharp
class NtcMaterialBinding : MonoBehaviour {
    public NtcMaterialAsset asset;
    Texture2DArray latentTexture;
    ComputeBuffer weightBuffer;      // Raw
    ComputeBuffer constantsBuffer;   // 或直接用 MaterialPropertyBlock 塞 int4[]

    void OnEnable() {
        // 从 asset 创建/缓存 GPU 资源，Bind 到 Renderer 的 MaterialPropertyBlock
        mpb.SetTexture("_NtcLatents", latentTexture);
        mpb.SetBuffer("_NtcWeights", weightBuffer);
        mpb.SetConstantBuffer("_NtcConstants", constantsBuffer, 0, size);
    }
}
```

### 5.2 Shader 端（手写 HLSL，移植 `Inference.hlsli`）

分两版本，用 `#pragma multi_compile` 或 shader variant 切换：

**Variant 1 —— Float 兼容版（阶段A，先落地）**
- 把 `dot4add_i8packed(a,b,acc)` 换成：手动 unpack 4×int8 → 转 float → `dot(a4,b4)+acc`，逐 4 元素展开累加。
- 好处：SM5.0 即可跑（Unity 默认 `#pragma target 3.5/4.5` 就够），能在 URP/HDRP/Built-in 上无条件验证正确性。
- 代价：ALU 开销比官方 DP4A 版本高（沿用同样的数学，只是没有硬件整数点积加速），但**逐像素还是比传统"先整图解压+存显存"省显存**，可以先用它验证画质与美术可控性。

**Variant 2 —— DP4A 版（阶段B，性能优化）**
- Unity 需要用 DXC 编译该 shader pass：`#pragma target 6.4` + 确保项目 Player 设置里 Graphics API 是 **DX12 或 Vulkan**（DP4A/`dot4add_i8packed` 在 DX11/OpenGL 上不可用）。
- 直接照抄官方 `NtcEvaluateLayerINT8`，几乎不用改数学，只需要把 `ByteAddressBuffer`/`Texture2DArray`/`SamplerState` 换成 Unity 的等价声明方式（Unity HLSL 语法基本兼容 DXC 语法，只是 cbuffer 声明用 `CBUFFER_START/END` 宏）。

**接入点**：在自定义 Lit shader 的 `SurfaceFunction`/`Fragment` 里，把原本
```hlsl
half4 albedo = tex2D(_BaseMap, uv);
```
替换为
```hlsl
float outputs[16];
NtcSampleTextureSet(g_NtcConstants, t_NtcLatents, s_LatentSampler, t_NtcWeights, 0, texel, mipLevel, false, outputs);
half4 albedo = half4(outputs[0], outputs[1], outputs[2], outputs[CHANNEL_OPACITY]);
```
其中 `texel/mipLevel` 的计算：初期可以先用**确定性**的 `floor(uv * textureSize)` + 常规 mip 选择（`tex.CalculateLevelOfDetail`或 shader里手动算LOD），STF（随机抖动滤波）留到阶段C 再叠加，先保证"该点该mip"能正确采样出正确颜色。

### 5.3 兼容性/回退策略

- 材质上放一个 `NtcRenderMode` 开关：`Off(走原始贴图) / InferenceOnLoad(调试对比) / InferenceOnSample(手写Float) / InferenceOnSample(DP4A)`，方便逐步验证和性能AB。
- 检测不到 DX12/Vulkan + SM6.4 时自动降级到 Float 版，避免线上崩溃。

---

## 6. 风险清单（按优先级）

| 风险 | 说明 | 缓解方案 |
|------|------|----------|
| **Unity 无原生 BGRA4 纹理格式** | Latent 纹理官方要求 `BGRA4_UNORM`/`A4R4G4B4`，Unity `TextureFormat` 枚举里没有直接等价项 | 方案A：用 `RGBA32`（每分量转成 8bit，存 4bit 值到高4位，shader里位移还原）替代，牺牲一点显存但通用；方案B：验证 `GraphicsFormat.B4G4R4A4_UNormPack16` 是否在目标平台受支持（DX12/Vulkan 上大概率可以），走 `Texture2DArray.Create(GraphicsFormat)` |
| **DP4A/SM6.4 在 Unity 中的可用性** | 需 DXC 编译 + DX12/Vulkan；URP/HDRP 默认 shader 用的编译路径需要确认支持 `#pragma target 6.4` | 先用 Float 兼容版验证功能；性能优化阶段单独做 DX12/Vulkan 专项验证，Built-in/移动端不支持则自动回退 |
| **GDeflate 压缩的 latent 数据** | C# 侧没有现成 GDeflate 解码器 | v0 用 `ntc-cli --gdeflate none` 强制不压缩 latent，后续再评估引入 GDeflate 解码或用 `System.IO.Compression`（如果 ntc-cli 支持标准 deflate 兜底） |
| **ntc-cli 依赖 CUDA Toolkit** | 已验证 ntc-cli/ntc-explorer 强制要求 CUDA Toolkit，Unity 开发机未必有 | 压缩后端与 Unity 项目物理隔离（模块B独立机器/服务），符合本方案"三方解耦"的设计初衷 |
| **STF（Stochastic Texture Filtering）** | 官方 Inference on Sample 强烈建议配合STF+去噪，否则贴图会有明显 aliasing（因为每次只采样1个未过滤texel） | 阶段性妥协：先接受轻微锯齿做功能验证；后续引入简单的时间性抖动（TAA叠加）或移植 `STFSamplerState.hlsli` 的核心逻辑 |
| **多材质/多Draw Call 的 Buffer 管理** | 每个材质都要绑定自己的 latent/weight，Unity SRP Batcher 对 per-material 的 buffer/texture 兼容性需验证 | 初期先不追求 SRP Batcher 兼容，用普通 Material 实例 + `MaterialPropertyBlock`；后续可探索多材质合并到一个大 latent Texture2DArray + weight buffer，用 index 索引，兼容批处理 |

---

## 7. 里程碑与任务分解

```
M0  资产管线验证（不涉及Shader）——本次要做的"packing流程"
    ├─ 用SDK自带的样例.ntc文件（FlightHelmet 材质）在Unity里跑通 NtcContainerReader
    ├─ 验证JSON chunk解析正确（宽高、通道、MLP层结构、latent图信息）
    └─ 验证能把latent数据正确切出、上传成Texture2DArray，权重切出成byte[]

M1  Inference on Load 调试模式（CPU或Compute Shader全图解压对比）
    ├─ 移植 NtcEvaluateLayerINT8 数学到一个 ComputeShader，全图跑一遍
    └─ 与原始PBR贴图做PSNR对比，验证MLP复刻数学正确

M2  Inference on Sample · Float兼容版
    ├─ 自定义 Unlit/Lit shader，在fragment里逐像素跑MLP（float模拟int8点积）
    └─ 效果对比 M1，确认逐像素路径与全图路径一致

M3  压缩后端 v0 打通
    ├─ Editor右键导出PBR贴图 + 生成manifest.json
    └─ 调用本机ntc-cli生成.ntc，全链路走通（Unity材质 → .ntc → Unity渲染）

M4  性能优化：DP4A/SM6.4版本 + STF
M5  批量化/多材质合批 + SRP Batcher兼容性
```

**本次会话的实际验证目标 = M0**：用 RTXNTC SDK 自带的真实 `.ntc` 文件（`assets/models/FlightHelmet/*.ntc`），在新建的 Unity 项目里写 `NtcContainerReader` 并验证解析结果，为后续 M1+ 打基础。
