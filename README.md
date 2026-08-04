# NTCinUnity

NTCinUnity 是一套 Unity 侧的 NVIDIA Neural Texture Compression（NTC）接入实验。项目包含 NVIDIA 官方 RTXNTC 后端、Unity 插件，以及对实际实现和验证结果的记录。

目标不是重新实现 NTC 的训练算法，而是打通下面这条链路：

```text
Unity 材质与源贴图
    -> Editor 烘焙窗口调用 ntc-cli
    -> .ntc（NTEX 容器）
    -> Unity 纯 C# 解析
    -> latent / MLP 权重上传 GPU
    -> compute 解压或分块缓存
    -> 绑定回 URP 材质
```

## 仓库内容

```text
NTCinUnity/
├─ nvidia-backend/   NVIDIA RTXNTC 官方仓库（Git submodule）
├─ unity-plugin/     Unity 插件源码
├─ docs/             技术报告、使用与验证记录
├─ LICENSE
└─ THIRD-PARTY-NOTICES.md
```

`nvidia-backend` 不是拷贝进来的 SDK 源码，而是指向 NVIDIA 官方仓库的 submodule。这样可以保留上游提交和许可证边界，也避免把本地 2.7 GB 的构建目录、嵌套 `.git` 和二进制误传到本仓库。

克隆时需要带子模块：

```bash
git clone --recursive https://github.com/kiiye9697/NTCinUnity.git
```

已经克隆过主仓库的话：

```bash
git submodule update --init --recursive
```

## 当前实现

### Editor 烘焙

`unity-plugin/Editor/NtcBakeWindow.cs` 会扫描场景或项目材质，提取 BaseColor、Normal、AO、Metallic、Smoothness 和 Alpha Cutout 数据，生成 manifest，然后以外部进程调用 `ntc-cli`。

默认设置：

- 16 bpp，界面上限 20 bpp；
- Metalness loss scale 24；
- Smoothness/Roughness loss scale 16；
- 强制 `--gdeflate off`，因为 Unity 运行时没有实现 GDeflate 解码。

`ntc-cli` 属于离线烘焙工具。它不参与 Unity Player 的运行时逻辑，也不会被 `Editor/` 目录中的代码带进 Player。

### Unity Runtime

Runtime 不链接 `libntc.dll`，也不调用 CUDA。核心路径是：

1. `NtcContainerReader` 读取 NTEX Header、JSON Chunk 和 Data Chunk；
2. `NtcAssetBuilder` 把 latent 展开为 `Texture2DArray`，把 INT8 网络参数上传为 Raw `ComputeBuffer`；
3. `NtcDecompressor` 用 compute shader 执行 MLP；
4. `NtcSceneController` 负责材质扫描、模式切换和 PBR 贴图绑定；
5. `NtcTileStreamer` 在 on-Sample 模式下按材质可见性和距离分帧填充缓存。

网络拓扑来自 `.ntc` 数据：`48 -> 64 -> 48 -> 32 -> 16`，隐藏层使用 HGELUClamp。

## 三种模式

### Original

恢复场景原始材质和贴图，作为画面对照。

### Inference on Load

进入模式时将整套 `.ntc` 解压到普通 PBR RenderTexture，再绑定回原材质。默认使用 compute shader；compute 不可用或失败时，可以退回到预先解压的 PNG。

### Inference on Sample

当前首选实现不是 fragment shader 每帧逐像素执行 MLP，而是 **tile cache**：

- 只处理当前可见材质；
- 近处材质优先；
- 默认每块 `256 x 256`；
- 缓存最长边默认限制为 1024；
- 每帧最多调度 8 个 tile；
- 材质不可见 5 秒后释放整张缓存。

MLP 在 tile 解压阶段执行，最终 URP 材质采样的是缓存 RenderTexture。工程还保留了一个直接在 fragment 中执行 MLP 的回退 shader，但该路径只输出 BaseColor，属于 Unlit 验证路径，不代表完整 PBR 的默认实现。

## 使用

### 1. 准备插件

把 `unity-plugin/` 复制到 Unity 工程的 `Assets/NtcPlugin/`。当前代码以 Unity 2021.3 URP 为验证环境。

### 2. 准备 ntc-cli

按照 `nvidia-backend/README.md` 构建 RTXNTC。烘焙窗口中的 `ntc-cli` 字段可以直接填写编译产物路径。

### 3. 烘焙

在 Unity 中打开：

```text
NTC / Bake Window
```

扫描材质、选择要处理的条目，然后执行批量烘焙。默认输出目录为：

```text
Assets/NtcPlugin/NtcData/
```

生成文件名通常是 `{MaterialName}.ntc`。

### 4. 运行

通过菜单创建控制器：

```text
NTC / Add Scene Controller
```

进入 Play 后，通过左上角面板切换 Original、on-Load 和 on-Sample；`F1` 可以隐藏面板。控制器默认在 `NtcPlugin/NtcData` 下按材质名查找 `.ntc`。

## 当前边界

这个版本首先保证 Editor 中的完整验证流程。需要注意：

- 当前 `NtcSceneController` 使用 `Application.dataPath` 读取 `.ntc`，Player 数据打包方案尚未作为正式功能提交；
- `.ntc` 不会因为放进任意 `Assets` 子目录就自动进入 Player，后续需要采用 StreamingAssets、Addressables 或自定义导入器，并同步修改加载接口；
- 场景扫描目前读取 Renderer 的第一个 `sharedMaterial`，多材质槽没有完整覆盖；
- 材质通道按名称启发式识别；
- 当前验证目标是 URP；
- on-Sample 的默认路径会分配普通 PBR tile cache，不能描述为“只驻留 latent 和权重”；
- GDeflate BufferView 不支持，烘焙必须关闭 GDeflate。

## Showcase

本地使用 Classic Sponza 做效果验证，但该场景、美术资源和由其烘焙出的 `.ntc` 不在公开仓库中。

演示 GIF 尚未录制。录制完成后会放在 `docs/images/showcase.gif`，用于展示 Original、on-Load 和 on-Sample 的实际切换效果。

## 验证数据

文档只保留能够对应到原始记录的数据：

- MetalPlates013：将 `ntc-cli` 解压结果和源图逐像素比较，overall PSNR 为 **45.26 dB**；
- SponzaBricks：`ntc-cli` 训练输出 PSNR 为 **51.37 dB**，这是单个材质的训练指标，不是 Sponza 全场景平均值；
- `7/7 files parsed successfully` 指七个测试 `.ntc` 文件通过容器解析，不代表七个 Sponza 材质的统一质量统计；
- 早期“16 MB 对 96 MB，节省 83%”是 prototype 的静态估算，没有计入当前 tile cache，因此不作为现版本显存结论。

详细方法和限制见 [`docs/验证记录.md`](docs/验证记录.md)。

## 文档

- [`docs/技术报告.md`](docs/技术报告.md)：当前实现、数据流、关键设计和已知边界；
- [`docs/使用说明.md`](docs/使用说明.md)：安装、烘焙、运行和排错；
- [`docs/验证记录.md`](docs/验证记录.md)：保留下来的可复现数据和问题修复依据；
- [`docs/README.md`](docs/README.md)：文档索引。

## 许可证

仓库不是 MIT、Apache-2.0 等开源许可证项目。Unity 插件中的两个 shader 文件移植了 NVIDIA RTXNTC SDK 的推理数学，受 NVIDIA RTX SDKs License Agreement 约束。本仓库使用研究与评估许可，具体边界见 [`LICENSE`](LICENSE) 和 [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md)。

`nvidia-backend/` 作为官方 submodule 使用 NVIDIA 自己的许可证，本仓库的 `LICENSE` 不覆盖该子模块。
