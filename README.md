# NTCinUnity — NVIDIA Neural Texture Compression Unity Toolkit

本仓库围绕 **NVIDIA Neural Texture Compression（NTC）在 Unity URP 中的接入**，包含两部分：

1. **[`nvidia-backend/`](nvidia-backend/)**：NVIDIA RTXNTC 官方仓库，以 Git submodule 固定上游版本，提供离线压缩工具 `ntc-cli`
2. **[`unity-plugin/`](unity-plugin/)**：Unity 侧的材质烘焙、NTEX 容器解析、GPU 解压、PBR 材质绑定和 tile cache

如果你只是想**安装和使用**插件，详见 **[`docs/使用说明.md`](docs/使用说明.md)**。

如果你想了解 **NTEX 数据布局、MLP 推理、GPU 资源组织和工程限制**，见
**[`docs/技术报告.md`](docs/技术报告.md)** 与 [`docs/验证记录.md`](docs/验证记录.md)。

---

## 方案总览与对比

插件覆盖离线烘焙和三种运行模式。`ntc-cli` 只参与离线压缩；Unity Runtime 不链接 `libntc.dll`，也不依赖 CUDA 或 `ntc-cli.exe`。

### 运行模式

| 模式 | GPU 路径 | 输出资源 | 定位 |
| --- | --- | --- | --- |
| **Original** | 不执行 NTC 推理 | 场景原始贴图 | 画面对照 |
| **Inference on Load** | Compute Shader 整图解压 | 完整 PBR RenderTexture | 质量验证与常规运行路径 |
| **Inference on Sample** | Compute Shader 分帧解压 tile | 受分辨率上限约束的 PBR cache | 可见材质优先、降低单帧解压开销 |
| Fragment MLP fallback | Fragment Shader 直接执行 MLP | BaseColor-only Unlit 输出 | Compute/tile 路径的兼容性回退 |

### 核心组件

| 组件 | 职责 | 实现 |
| --- | --- | --- |
| `NtcBakeWindow` | 扫描 Unity 材质、生成 manifest、调用 `ntc-cli` | Unity Editor |
| `NtcContainerReader` | 读取 NTEX Header、JSON Chunk 和 Data Chunk | 纯 C# |
| `NtcAssetBuilder` | 构建 latent `Texture2DArray` 与 INT8 网络参数 | Unity Runtime |
| `NtcDecompressor` | 执行整图或指定区域的 MLP 解压 | Compute Shader |
| `NtcTileStreamer` | 按可见性、距离和每帧预算填充 PBR cache | Unity Runtime |
| `NtcSceneController` | 扫描场景、切换模式、绑定 URP 材质 | Unity Runtime |

测试文件使用 `48 -> 64 -> 48 -> 32 -> 16` 的 INT8 MLP，隐藏层激活函数为 HGELUClamp。

完整的数据流、通道映射和实现约束见
**[`docs/技术报告.md`](docs/技术报告.md)**。

---

## 快速开始

克隆仓库并初始化 NVIDIA RTXNTC 子模块：

```bash
git clone --recursive https://github.com/kiiye9697/NTCinUnity.git
```

将 `unity-plugin/` 复制到 Unity 工程：

```text
Assets/NtcPlugin/
```

按照 [`nvidia-backend/README.md`](nvidia-backend/README.md) 构建 `ntc-cli`，然后在 Unity 中完成烘焙：

```text
NTC / Bake Window
    -> 选择 ntc-cli.exe
    -> Scan Scene Materials
    -> Bake All Selected
```

默认输出：

```text
Assets/NtcPlugin/NtcData/{MaterialName}.ntc
```

创建场景控制器并进入 Play：

```text
NTC / Add Scene Controller
```

左上角面板用于切换 `Original`、`on-Load`、`on-Sample` 和 PBR 通道检查；`F1` 控制面板显隐。

验证环境为 **Unity 2021.3 + URP**。详细参数和排错流程见
[`docs/使用说明.md`](docs/使用说明.md)。

---

## Unity 插件（`unity-plugin/`）

`unity-plugin/` 是 Unity 侧的完整接入代码，包含 Runtime、Editor 工具和 GPU shader。

```text
unity-plugin/
├── Runtime/
│   ├── NtcContainerReader.cs      # NTEX 容器解析
│   ├── NtcJson.cs                 # 零依赖 JSON 解析
│   ├── NtcAssetBuilder.cs         # latent 与网络参数构建
│   ├── NtcDecompressor.cs         # Compute Shader 解压调度
│   ├── NtcTileStreamer.cs         # tile cache 调度与驱逐
│   ├── NtcSceneController.cs      # 场景扫描、模式切换与材质绑定
│   └── NtcPackingReport.cs        # 容器结构报告
├── Editor/
│   ├── NtcBakeWindow.cs           # 材质扫描与批量烘焙
│   ├── NtcSetupMenu.cs            # Unity 菜单入口
│   └── NtcSceneControllerEditor.cs
├── Shaders/
│   ├── NtcInference.hlsl          # INT8 MLP 推理
│   ├── NtcInferenceOnSample_URP.shader
│   └── PBRChannelDebug_URP.shader
└── Resources/
    └── NtcDecompress.compute      # 整图与区域解压 kernel
```

默认烘焙参数：

| 参数 | 默认值 | 说明 |
| --- | ---: | --- |
| Bits per pixel | 16 | UI 支持 1–20 bpp |
| Metalness loss scale | 24 | 保留近二值金属遮罩的对比度 |
| Smoothness/Roughness loss scale | 16 | 降低光滑度通道的细节损失 |
| GDeflate | off | Runtime 不解码 GDeflate BufferView |
| Tile size | 256 | on-Sample 分块尺寸 |
| Cache max dimension | 1024 | on-Sample cache 最长边 |
| Tiles per frame | 8 | 每帧解压预算 |
| Eviction delay | 5 s | 不可见材质缓存释放延迟 |

Player 工程需要通过 StreamingAssets、Addressables 或自定义导入器提供 `.ntc` 数据；默认路径实现面向 Unity Editor 的 `Assets/NtcPlugin/NtcData`。

---

## 验证结果

| 测试对象 | 指标 | 结果 |
| --- | --- | ---: |
| MetalPlates013 | 解压结果对源图 overall PSNR | **45.26 dB** |
| SponzaBricks | 单材质 `ntc-cli` 训练 PSNR | **51.37 dB** |
| NTEX 测试集 | C# 容器解析 | **7 / 7 files** |
| Flagpoles Metalness | loss scale 24 后解压均值 | **201.80**（源图 201.84） |

`51.37 dB` 是单材质训练指标；`45.26 dB` 是解压图与源图的外部逐像素测量。测试方法、通道统计和数据布局验证见
[`docs/验证记录.md`](docs/验证记录.md)。

---

## License

本仓库采用 **NTCinUnity Research and Evaluation License**，见 [`LICENSE`](LICENSE)。

`nvidia-backend/` 由 NVIDIA RTXNTC 自身许可证管理。`NtcInference.hlsl` 与 `NtcDecompress.compute` 中的 NVIDIA 衍生部分同样受 NVIDIA 条款约束，归属和文件边界见 [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md)。

## 致谢

- NVIDIA RTXNTC / LibNTC — NTC 压缩工具、容器格式与推理参考实现
- Vaidyanathan et al. *Random-Access Neural Compression of Material Textures.* SIGGRAPH 2023
- Unity Universal Render Pipeline — PBR 材质与渲染集成环境
