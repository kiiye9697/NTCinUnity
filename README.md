# NTCinUnity

把 NVIDIA 的神经纹理压缩（NTC）接到 Unity 里的一套插件。`.ntc` 容器用纯 C# 解析，解压跑在 compute shader 上，运行时不依赖 NVIDIA 的任何 DLL、也不需要 CUDA。

做这个项目的出发点是想搞清楚一件事：NTC 论文里那些好看的压缩率，在真实引擎、真实美术资产上还剩多少。所以它不是一个 demo 级的验证，而是把烘焙工具、三种接入模式、显存流送、打包链路都补齐了，踩过的坑也都记在文档里。

实测结果：Classic-Sponza 全场景 7 个材质，PSNR 51.37 dB；on-Sample 模式相比常规贴图省下约 83% 显存。

> 上游是 NVIDIA [RTXNTC / LibNTC 0.9.2](https://github.com/NVIDIA-RTX/RTXNTC)，论文为《Random-Access Neural Compression of Material Textures》(SIGGRAPH 2023)。本仓库不包含 NVIDIA 的任何源码或二进制，详见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

## 三种接入模式

插件的设计原则是「原有加载流程不动，只在取贴图那一刻注入 NTC」。据此有三种模式，运行时可随时切换：

| 模式 | 做法 | 显存 |
|------|------|------|
| Original | 用工程原始贴图，什么都不改 | 原始贴图全量 |
| Inference on Load | 加载时把 `.ntc` 整图解压成 RenderTexture，绑回原材质。shader 不用改 | 与原始相当 |
| Inference on Sample | 按可见性和距离逐 tile 解压，封顶分辨率，超时驱逐 | 只驻留 latent + 权重 |

这三个不是「哪个最好」的选择题，而是排查链路的三个阶段。先跑 Original 建立基线；切到 on-Load，因为它不动 shader，画面若和基线有差异就一定出在解压或通道映射；最后上 on-Sample，验证逐像素 MLP 推理。开发过程中这个顺序反复证明有效——遇到画面问题时，先问清楚「你对比的是哪两个模式」，比一头扎进代码里快得多。

面板上还有一组通道隔离视图（Lit / Base / Normal / Metal / Rough），用来定位解码和通道映射问题。

## 目录结构

```
unity-plugin/
  Runtime/     容器解析、GPU 资产构建、compute 解压器、场景控制器、tile 流送
  Editor/      烘焙窗口、Inspector 扩展、菜单
  Shaders/     MLP 推理数学、on-Sample shader、通道调试 shader
  Resources/   NtcDecompress.compute（放这里是为了保证进包）
docs/          技术报告：论文精读、架构、烘焙工具链、踩坑实录
PROGRESS.md    开发日志，按日期记录，技术细节最全的一份
```

插件不依赖任何示例工程，把 `unity-plugin/` 整个拷到目标工程的 `Assets/NtcPlugin/` 就能用。场景特化的硬编码已经清掉了，贴图源目录、`.ntc` 前缀、ntc-cli 路径都可配。

仓库里没有 `.ntc` 样例，也没有示例场景。原因是它们都是 Unity Classic-Sponza 那批美术资产的衍生物，没有随附授权，不适合放在公开仓库里。要复现测试数据的话，自己取得 Sponza 再烘一遍就行。

## 上手

需要 Unity 2021.3 或更高，URP。

**准备 `.ntc`。** 烘焙依赖 NVIDIA 的 `ntc-cli.exe`，得自己去 clone [RTXNTC](https://github.com/NVIDIA-RTX/RTXNTC) 编译（需要 CUDA Toolkit）。编好之后开 `NTC / Bake Window`，点 `Scan Scene Materials`，勾上要烘的材质，`Bake All Selected`。窗口会自动去工程根目录、SDK 目录和 PATH 里找 `ntc-cli.exe`。

**跑起来。** 菜单 `NTC / Add Scene Controller` 创建控制器，进 Play，左上角面板切模式，F1 收起面板。控制器会扫描场景材质，在 `ntcDataPath`（默认 `NtcPlugin/NtcData`）下按材质名匹配 `.ntc`。

烘焙有两条硬约束，绕不过去：

一是**必须 `--gdeflate off`**。运行时的容器解析是纯 C#，没有实现 GDeflate 解码，碰到压缩过的 BufferView 会直接抛异常。烘焙窗口已经默认带上了这个开关，列表里状态显示 `GDeflate: rebake` 的旧文件得重烤。

二是**二值 mask 通道要单独给 loss 权重**。NTC 让一个材质的所有通道共享同一个 bpp 预算，Metallic、Alpha 这种接近二值的 mask 跟 Color、Normal 一起训练时会被压成常数。插件默认给 Metal 24 倍、Smooth 16 倍的 `lossFunctionScale`，这个参数不增加文件大小，只改变固定预算在通道间的分配。另外 `ntc-cli` 的 bpp 上限是 20，超了会报 `Cannot select a latent shape`。

## 打包

**运行时不需要 `ntc-cli.exe`，也不需要 `libntc.dll` 和 CUDA。** `ntc-cli` 只在 `Editor/NtcBakeWindow.cs` 里被调用，属于烘焙期工具；Runtime 那几个文件是纯 C# 加 compute shader。而 `Editor/` 是 Unity 的特殊目录名，里面的代码不会编译进 Player，所以那段找 exe 的逻辑根本不会出现在包里。玩家机器上只要有一块支持 ComputeShader 的 GPU。

**但 `.ntc` 得自己放对位置。** `.ntc` 没有 Unity导入器，Unity 把它当未知资产，不在 StreamingAssets 里就不会进包。菜单 `NTC / Copy NtcData To StreamingAssets (for builds)` 会把烘好的 `.ntc` 镜像到 `StreamingAssets/NtcData/`。运行时的路径解析会依次尝试 `Assets/<配置路径>`（编辑器）、`StreamingAssets/<配置路径>`、`StreamingAssets/<末级目录名>`、`StreamingAssets` 根，取第一个存在的，所以同一份序列化配置在编辑器和 Player 里都能用，不用为每个平台改设置。

还有两处容易漏：

`NtcInferenceOnSample_URP` 和 `PBRChannelDebug_URP` 是用 `Shader.Find` 找的，而 `Shader.Find` 找不到已经被裁掉的 shader。打包前把它们加进 Project Settings → Graphics → Always Included Shaders，或者序列化引用到控制器字段上。`Resources/NtcDecompress.compute` 在 `Resources/` 下会自动进包，不用管。

`NtcData/Decompressed/` 里那些解压预览 PNG 不用打包。它只是 on-Load 的legacy 兜底路径，compute 路径只需要 `.ntc`——只带 `.ntc` 才能体现用 NTC 的意义。

如果目标平台不支持 ComputeShader，on-Load 和 on-Sample 都不可用，得回退 Original。

## 实测数据

测试环境是 RTX 5060 (8GB, SM 12.0)、Unity 2021.3.33f1c1URP、ntc-cli 0.9.2、CUDA 12.9.86（只有编译 ntc-cli 需要）。

| 对象 | PSNR |
|------|------|
| MetalPlates013（单材质） | 45.26 dB |
| Sponza 全场景，7 个材质 | 51.37 dB |

都远超视觉无损的 40 dB 线。显存方面，on-Sample 只驻留 16MB latent 加 9KB 权重，对比 on-Load 的 6 张 2048² 贴图约 96MB，省了83%，跟 NVIDIA宣称的 85% 基本吻合。

从源码和真实文件确认的几个参数：MLP 是 4 层 `48→64→48→32→16`，HGELUClamp 激活，权重 INT8（DP4A 路径）或 FloatE4M3（CoopVec 路径）。权重布局是 RowMajor，这一点是数据驱动读出来的，跟文档写的 ColumnMajor 默认值不一致，不能硬编码。Latent 源格式 BGRA4_UNORM，8 个 neural mip，每 mip 4 个 layer view，Unity 侧手动拆 nibble 展开成 RGBA32 的 Texture2DArray。

## 架构

分三层，压缩后端、传输格式、渲染集成互不绑定：

```
渲染集成   NtcSceneController<-> NtcTileStreamer <-> 材质
GPU 解压   NtcDecompressor + NtcDecompress.compute + NtcInference.hlsl
资产构建   NtcAssetBuilder（latent -> Texture2DArray，权重 -> ComputeBuffer）
           NtcContainerReader（NTEX 容器纯 C# 解析）
离线压缩   ntc-cli（NVIDIA 官方，CUDA）+ NtcBakeWindow 驱动
```

这么分的好处是 ntc-cli 升级不影响插件代码，渲染层换 HDRP 或自定义 SRP 不用动下层。容器解析用纯 C# 写而不是绑 NVIDIA 运行时，直接决定了打包后不用带任何原生DLL——这是当初做架构决策时最重要的一条。

## 已知限制

on-Sample 输出的是 Unlit。`NtcInferenceOnSample_URP` 只解码 BaseColor 三通道，不参与 URP 的完整光照和 BRDF，它的作用是验证逐像素解码正确性和显存优势。要光照版得扩成URP Surface shader。

材质参数匹配是启发式的，按属性名和贴图名里的关键词（`basecolor`、`normal`、`metallic`、`rough`、`occl`、`alpha` 等）识别，没有自定义映射表。

Runtime 和 Editor 目前靠 `Editor/` 目录名隔离，功能上没问题，但没有 Assembly Definition，约束不了依赖方向，也不方便做成 UPM 包。

`unity-plugin/README_NTC.md` 的文案还是早期 Sponza 特化版，开头写着「本工程是 Classic-Sponza 的副本」，跟插件已经通用化的现状不符，待重写。

## 文档

[docs/NTC技术报告-从论文到Unity插件落地.md](docs/NTC技术报告-从论文到Unity插件落地.md) 是主要的技术文档，前半部分精读论文（特征金字塔、量化感知训练、平铺位置编码、hardGELU），后半部分是实践：架构决策、烘焙工具链、打包约束，以及十个踩坑实录和一张红线总结表。

`PROGRESS.md` 是按日期写的开发日志，比技术报告更细，排查过程的原始记录都在里面。

## 许可

研究和评估用途，不是开源许可证，细节见 [LICENSE](LICENSE)。

这样授权不是随意选的：本仓库有两个 shader 文件是 NVIDIA RTXNTC SDK 源码的移植（`NtcInference.hlsl` 和 `NtcDecompress.compute`），而 NVIDIA 的 SDK 许可证第 4(e) 条明确禁止让SDK 受开源许可证约束。给整个仓库挂 MIT 或 Apache-2.0 会违反这一条。这两个文件各自带了归属声明，说明它们受 NVIDIA 条款而非本仓库 LICENSE 约束。

商业使用或再分发请先开 issue 联系。
