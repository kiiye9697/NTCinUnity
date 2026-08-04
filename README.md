# NTC Test — Neural Texture Compression Unity 接入实现

本仓库验证 **NTC（NVIDIA Neural Texture Compression）** 在 Unity 中的完整接入方案，
覆盖从 `.ntc` 容器解析、GPU 解压、到材质替换与按需 tile 流送的全链路。所有 NTC 相关代码以插件形式提供，
不改动原始美术资产，遵循"原有加载流程不动、只在取贴图那一刻注入 NTC"的原则。

> 上游：基于 NVIDIA [RTXNTC / LibNTC 0.9.2](https://github.com/NVIDIA-RTX/RTXNTC) 的 `.ntc` 容器格式与 MLP 推理数学。
> 论文：《Random-Access Neural Compression of Material Textures》(SIGGRAPH 2023)。

---

## 仓库结构

本仓库只跟踪**可分发的插件源码 + 文档**：

```
NTCinUnity/
├── unity-plugin/        ★ Unity 接入插件（可直接拷进任意工程）
│   ├── Runtime/         容器解析 / 资产构建 / 解压器 / 场景控制器 / tile 流送
│   ├── Editor/          烘焙窗口 / Inspector扩展 / 一键挂载菜单
│   ├── Shaders/MLP 推理数学 + on-Sample shader + 通道调试 shader
│   └── Resources/       NtcDecompress.compute（随包进Build）
├── docs/                技术报告（原理精读 + 架构 + 踩坑实录）
├── PROGRESS.md          开发日志（按日期记录的排查/修复/里程碑，最详尽的技术细节）
├── README.md            本文件
└── .gitignore
```

**插件不依赖任何示例工程**，直接把 `unity-plugin/` 拷到目标工程的 `Assets/NtcPlugin/` 即可使用
（已去除场景特化硬编码，纹理源目录 / `.ntc` 前缀 / ntc-cli 路径均可配置）。

### 两个外部依赖（不在仓库内，按需自取）

| 依赖 | 用途 | 获取方式 | 为何不入库 |
|------|------|----------|-----------|
| **NVIDIA RTXNTC SDK** | 提供 `ntc-cli.exe`，**仅烘焙期需要** | `git clone https://github.com/NVIDIA-RTX/RTXNTC` | 上游仓库（约 2.7GB，自带独立 .git，含 >100MB 文件） |
| **Unity 示例工程** | Classic-Sponza (URP) 验证场景 | 自备任意 URP 工程即可 | 第三方美术资产授权 + 体积约 3.2GB |

烘焙产物 `unity-plugin/NtcData/*.ntc`（约 372MB）同样不入库——它们可用 `NtcBakeWindow` 从源贴图
重新烘焙。运行时**不需要** RTXNTC SDK，详见下方「打包与发布」。

---

## 三种接入模式

| 模式 | 按钮 | 做法 | 显存 |
|------|------|------|------|
| **A. Original** | `Original` | 保持工程原始材质与贴图（基线对照） | 原始贴图全量 |
| **B. Inference on Load** | `on-Load` | 材质/shader 不变，加载时用 compute shader 把 `.ntc` 整图解压成 RT 绑回原材质（含 mips） | 与原始相当 |
| **C. Inference on Sample** | `on-Sample` | 按需 tile 流送：可见材质才解压，封顶分辨率，超时驱逐 | 仅 latent+权重，大幅省显存 |

- **A/B** 共用同一套 URP Lit 材质，区别只在贴图来源（原始vs NTC 解压 RT）。
- **C** 走独立的 tile 流送调度器（`NtcTileStreamer`）：每帧按可见性+距离+预算解压 tile，填满缓存后生成 mips。

三种模式不是"哪个最好"的单选，而是**验证链路上的三个阶段**：先跑 Original 建立基线 → 切on-Load 验证
解压通道映射是否正确（不改 shader，有差异必在解压侧）→ 最后上 on-Sample 验证逐像素 MLP 推理。
排查问题时**先问清楚"对比的是哪两个模式"**，比直接看代码高效得多。

---

## 快速开始

### 1. 装插件

把 `unity-plugin/` 整个目录拷到目标 URP 工程的 `Assets/NtcPlugin/`（Unity 2021.3+）。

### 2. 烘焙 `.ntc`

需要先取到 `ntc-cli.exe`（`git clone https://github.com/NVIDIA-RTX/RTXNTC` 后按其文档编译，需 CUDA Toolkit）。

菜单 **`NTC / Bake`** → `Scan Scene Materials` → 勾选 → `Bake All Selected`，产物落到 `Assets/NtcPlugin/NtcData/`。

### 3. 跑起来

1. 菜单 **`NTC / Add Scene Controller To Open Scene`** 创建 `NTC_Controller`。
2. 进入 **Play**，左上角面板点 `Original` / `on-Load` / `on-Sample` 切换模式（`F1` 开关面板）。
3. 控制器自动扫描场景材质，并在 `ntcDataPath`（默认 `NtcPlugin/NtcData`）下按 `{材质名}.ntc` 匹配。

面板还提供 `View` 按钮组（`Lit / Base / Normal / Metal / Rough`）做PBR 通道隔离查看，用于验证解码与通道映射。

详见 `unity-plugin/README_NTC.md` 与 `unity-plugin/NTC_Unity_Architecture.md`。

---

## 架构

三层解耦，确保压缩后端、传输格式、渲染集成互不绑定：

```
┌──────────────────────────────────────────────────────┐
│  渲染集成层   NtcSceneController ←→ NtcTileStreamer   │
├──────────────────────────────────────────────────────┤
│  GPU 解压层   NtcDecompressor + NtcDecompress.compute │
│               NtcInference.hlsl（共享 MLP 数学）       │
├──────────────────────────────────────────────────────┤
│  资产构建层   NtcAssetBuilder（→ Texture2DArray+Buffer）│
│               NtcContainerReader（NTEX 纯 C# 解析）    │
├──────────────────────────────────────────────────────┤
│  压缩后端（离线）  ntc-cli（NVIDIA 官方, CUDA）         │
│                    NtcBakeWindow（Editor UI 驱动）     │
└──────────────────────────────────────────────────────┘
```

| 职责 | 文件 |
|------|------|
| `.ntc` 容器解析（NTEX：32B Header + JSON chunk + Data chunk） | `Runtime/NtcContainerReader.cs` + `NtcJson.cs` |
| 容器结构报告（调试） | `Runtime/NtcPackingReport.cs` |
| GPU 资产构建（latent → Texture2DArray，权重 → ComputeBuffer） | `Runtime/NtcAssetBuilder.cs` |
| Compute 解压器（DecompressFull / DecompressRegion / GenerateMips） | `Runtime/NtcDecompressor.cs` + `Resources/NtcDecompress.compute` |
| MLP 推理数学（4层 `48→64→48→32→16`） | `Shaders/NtcInference.hlsl` |
| 场景级控制器（扫描/分组/三模式切换/材质绑定/统计面板） | `Runtime/NtcSceneController.cs` |
| on-Sample tile 流送（可见性/预算/封顶/驱逐/mips） | `Runtime/NtcTileStreamer.cs` |
| 烘焙窗口（扫描场景 → 批量烘焙 + 手动单材质） | `Editor/NtcBakeWindow.cs` |
| Inspector 扩展 / 一键挂载菜单 | `Editor/NtcSceneControllerEditor.cs` / `NtcSetupMenu.cs` |
| on-Sample 展示 shader / 通道调试 shader | `Shaders/NtcInferenceOnSample_URP.shader` / `PBRChannelDebug_URP.shader` |

---

## 压缩工作流

`.ntc` 由 RTXNTC SDK 的 `ntc-cli.exe` 生成（外部依赖，仅烘焙期需要），Editor 侧 `NtcBakeWindow` 驱动：

```
菜单 NTC / Bake → Scan Scene / Project Materials → 勾选 → Bake All Selected
  → 每材质：源图转 PNG → 写 Manifest.json → ntc-cli 压缩 → （可选）解压预览
  → 输出到 Assets/NtcPlugin/NtcData/{材质名}.ntc
```

`NtcBakeWindow` 会自动探测工程根 / SDK 目录 / PATH 下的 `ntc-cli.exe`，按 semantic 选择 BC 格式
（标量通道 BC4，RGB 通道 BC7），并支持 alpha cutout mask slot。

### 两个必须遵守的烘焙约束

1. **必须 `--gdeflate off`**（BakeWindow 已默认加上）。运行时 `NtcContainerReader` **故意不实现 GDeflate 解码**，
   遇到压缩过的 BufferView 会直接抛异常提示重烤。列表里状态显示 `GDeflate: rebake` 的旧文件必须重新烘焙。
2. **二值/高对比度 mask 通道要给 loss 权重**。NTC 让一个材质的所有通道共享同一个 bpp 预算，Metallic、
   Alpha 这类接近二值的 mask 在与 Color/Normal 共训时会被"压扁"成常数。用 `lossFunctionScale`
   （Metal 默认 24x、Smooth 默认 16x）解决——**它不增加文件大小**，只改变固定预算在通道间的分配。
   `ntc-cli` 的 bpp 硬上限是 20（`KnownLatentShapes` 预设表决定），超过会报错。

---

## 打包与发布

### 运行时不需要 `ntc-cli.exe` —— 发包不用带这个 exe

这一点代码层面是确定的：

- **`ntc-cli` 只被 `Editor/NtcBakeWindow.cs` 调用**（`Process.Start`），属于**离线烘焙期**工具链。
  Runtime 目录里出现的"ntc-cli" 字样全是注释。
- **Runtime 全链路是纯 C# + compute shader**：`NtcContainerReader` 纯 C# 解析 NTEX 容器，
  `NtcAssetBuilder` 纯 C# 上传 GPU 资产（源码注释即写明 *"no ntc-cli dependency at runtime"*），
  `NtcDecompressor` 走Unity 自己的 ComputeShader。**不依赖 `libntc.dll`，不依赖 CUDA Toolkit。**
- `Editor/` 是Unity 的**特殊文件夹名**，其中代码不会被编译进 Player，所以那段 exe 探测/调用代码
  根本不会进包，也不会造成"找不到 ntc-cli"的运行时报错。

> 结论：**玩家机器上不需要 ntc-cli.exe、不需要 NVIDIA SDK、不需要 CUDA。** 只需要一块支持
> ComputeShader 的 GPU。ntc-cli 只在你自己烘焙 `.ntc` 时用。

### 但 `.ntc` 数据文件当前进不了包 —— 这是发包前必须改的

现状（`NtcSceneController`）：`ntcDataPath` 默认 `"NtcPlugin/NtcData"`，经`ToAbsolute()`拼到
`Application.dataPath` 后用 `File.ReadAllBytes` 直读。这在Editor 下成立，Build 后会失效，两个原因叠加：

| 问题 | Editor | Build |
|------|--------|-------|
| `Application.dataPath` 指向 | `<工程>/Assets` ✅ | `<游戏>_Data`（含义完全变了）❌ |
| `.ntc` 文件是否在包里 | 在 Assets 里，直读没问题 ✅ | `.ntc` 无导入器 → 被当未知资产，**不进包** ❌ |

三种修法，按需选：

| 方案 | 做法 | 优点 | 代价 |
|------|------|------|------|
| **A. StreamingAssets**（推荐，改动最小） | `.ntc` 移到 `Assets/StreamingAssets/NtcData/`，`ToAbsolute()` 改用 `Application.streamingAssetsPath` | 原样保留文件、仍可`File.ReadAllBytes`、可后期替换文件 | Android/WebGL 需改用 `UnityWebRequest` 异步读 |
| **B. Resources + TextAsset** | `.ntc` 改名 `.ntc.bytes` 放 `Resources/`，用 `Resources.Load<TextAsset>().bytes` | 全平台一致、同步 API 最简单 | 全部无条件进包并常驻，无法热更/按需卸载 |
| **C. Addressables / AssetBundle** | 打成 bundle 按需加载 | 生产级：按需加载、可热更、可分包 | 接入成本最高 |

配套注意事项：

- **legacy 解压 PNG 不必打包**。`NtcData/Decompressed/*.png` 只是 on-Load 的 legacy 兜底路径
  （compute 路径只需 `.ntc`）。发包时**只带 `.ntc`** 可显著省包体——这也正是用 NTC 的意义。
- **两个 shader 有被裁剪的风险**。`NtcInferenceOnSample_URP` 和 `PBRChannelDebug_URP` 在代码里走
  `Shader.Find()` 查找，而`Shader.Find` 找不到被裁掉的 shader。发包前请把它们**序列化引用**到
  Controller 的 `onSampleShader` / `pbrDebugShader` 字段（`NtcSetupMenu` 挂载时会自动填 `onSampleShader`），
  或加进 **Project Settings → Graphics → Always Included Shaders**。
- `Resources/NtcDecompress.compute` 在 `Resources/` 下，**会自动进包**，无需额外处理。
- 若目标平台不支持 ComputeShader，on-Load/on-Sample 都不可用，需回退 Original。

---

## 技术栈

| 组件 | 版本 |
|------|------|
| OS | Windows 11 |
| GPU | NVIDIA RTX 5060（8GB，SM 12.0） |
| CUDA Toolkit | 12.9.86（**仅编译/运行 ntc-cli 需要**） |
| Unity Editor | 2021.3.33f1c1（URP） |
| ntc-cli / LibNTC | 0.9.2 |
| DXC | v1.8.2505.1 |

MLP 参数：4 层 `48→64→48→32→16`，HGELUClamp 激活，权重 INT8（DP4A 路径）或 FloatE4M3（CoopVec 路径），
`weightLayout=RowMajor`（数据驱动，非文档默认的 ColumnMajor）。
Latent 为 Texture2DArray（源 BGRA4_UNORM，Unity侧手动拆 nibble 展开为 RGBA32），8 neural mip，每 mip 4 layer views。

### 质量实测

| 材质 | PSNR | 备注 |
|------|------|------|
| MetalPlates013 | 45.26 dB | 单材质解压验证 |
| Sponza 全场景（7 材质） | 51.37 dB | 真实游戏资产批量验证 |
| Flagpoles（金属） | — | Metallic 均值 201.84 → 201.80（loss scale 24x 修复后） |

均远超视觉无损阈值（>40 dB）。on-Sample 相对 on-Load 省显存约 **83%**（latent 16MB + weights 9KB vs 6×2048² ≈ 96MB）。

---

## 文档索引

| 文档 | 内容 |
|------|------|
| `README.md` | 本文件，项目总览 |
| `PROGRESS.md` | 开发日志：按日期记录的排查、bug 修复、里程碑（最详尽的技术细节） |
| [`docs/NTC技术报告-从论文到Unity插件落地.md`](docs/NTC技术报告-从论文到Unity插件落地.md) | **★ 技术报告**：论文精读（特征金字塔/量化感知训练/位置编码/hardGELU）、三层架构设计、烘焙工具链、打包约束、10 个踩坑实录 + 红线总结表 |
| `unity-plugin/README_NTC.md` | Unity 插件使用说明（面板操作、已知点） |
| `unity-plugin/NTC_Unity_Architecture.md` | Unity 侧完整架构方案文档 |
| [RTXNTC 上游 SDK](https://github.com/NVIDIA-RTX/RTXNTC) | NVIDIA 官方 LibNTC / ntc-cli / Explorer / Renderer 说明（外部依赖） |

---

## 已知限制

**打包相关（发包前需处理）**
- **`.ntc` 加载走 `Application.dataPath` + `File.ReadAllBytes`，仅 Editor 有效**，Build 前必须按上面
  「打包与发布」章节迁移到 StreamingAssets / Resources / Addressables 之一。
- `Shader.Find` 查找的两个 shader 需序列化引用或加入 Always Included Shaders，否则会被裁剪。

**功能限制**
- **on-Sample 输出 Unlit**：`NtcInferenceOnSample_URP.shader` 是纯展示 shader，只解码 BaseColor 三通道，
  不参与 URP 完整光照/BRDF。用于验证逐像素解码正确性与显存优势，如需光照版需扩展为 URP Surface shader。
- **材质参数匹配为启发式**：按属性名/纹理名关键词匹配（`basecolor`/`normal`/`metallic`/`rough`/`occl`/`alpha` 等），
  不支持自定义映射表。
- **无 asmdef**：Runtime/Editor 目前靠 `Editor/` 文件夹名隔离（功能上正确，Editor 代码不会进包），
  但没有 Assembly Definition，无法约束依赖方向、也不便作为 UPM 包分发。

**工程/文档遗留**
- `unity-plugin/README_NTC.md` 文案仍是 Sponza 特化版（开头写"本工程是 Classic-Sponza 的副本"），
  与插件已通用化的现状不符，待重写。
- RTXNTC SDK 里的 `batch_compress_sponza.py`（另一套命令行批量压缩路径）**未同步** Editor 侧的
  PIL TIFF alpha 读取修复（见 `PROGRESS.md` 2026-07-10 续2）。若用该脚本重烤，Smoothness/Alpha
  会重现静默丢失问题。
- 示例场景里 `Bricks.mat` 是 HDRP/Lit，URP 下可能显示品红（仅影响 A/B 模式，C 模式不受影响）。
