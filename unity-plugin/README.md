# NTC Unity Plugin

这个目录可以直接复制到 Unity 工程的 `Assets/NtcPlugin/`。

验证环境为 Unity 2021.3 + URP。插件由 Runtime、Editor、Shaders 和 Resources 四部分组成：

```text
unity-plugin/
├─ Runtime/
│  ├─ NtcContainerReader.cs   NTEX 容器解析
│  ├─ NtcJson.cs              零依赖 JSON 解析
│  ├─ NtcAssetBuilder.cs      latent / 权重 GPU 资产构建
│  ├─ NtcDecompressor.cs      compute 解压调度
│  ├─ NtcTileStreamer.cs      分块缓存与可见性调度
│  ├─ NtcSceneController.cs   场景扫描、模式切换、材质绑定
│  └─ NtcPackingReport.cs     容器结构报告
├─ Editor/
│  ├─ NtcBakeWindow.cs        材质扫描与批量烘焙
│  ├─ NtcSetupMenu.cs         菜单入口
│  └─ NtcSceneControllerEditor.cs
├─ Shaders/
│  ├─ NtcInference.hlsl
│  ├─ NtcInferenceOnSample_URP.shader
│  └─ PBRChannelDebug_URP.shader
└─ Resources/
   └─ NtcDecompress.compute
```

## 安装

把整个目录放进目标工程：

```text
Assets/NtcPlugin/
```

Unity 会根据随附 `.meta` 保持资源 GUID。不要只复制 `.cs` 而漏掉 shader、compute 或 `.meta`。

## 烘焙

烘焙需要 NVIDIA RTXNTC 的 `ntc-cli`，运行时不需要。官方源码在仓库根目录的 `nvidia-backend` submodule 中，也可以从 NVIDIA 官方仓库单独取得。

在 Unity 打开：

```text
NTC / Bake Window
```

推荐流程：

1. 在 `ntc-cli` 字段选择本机编译好的可执行文件；
2. `Scan Scene Materials` 或扫描项目材质；
3. 确认每个条目至少解析到两张贴图；
4. 选择条目并开始批量烘焙；
5. 输出到 `Assets/NtcPlugin/NtcData/`。

默认参数：

```text
Bits per pixel                  16
Metalness loss scale           24
Smoothness/Roughness scale     16
GDeflate                       off
```

不要开启 GDeflate。`NtcContainerReader` 能读 BufferView 描述，但不会解压 GDeflate 数据。

## 运行

创建控制器：

```text
NTC / Add Scene Controller
```

控制器默认查找：

```text
Assets/NtcPlugin/NtcData/{MaterialName}.ntc
```

如果烘焙时设置了文件名前缀，在控制器的 `ntcFilePrefix` 中填写相同前缀。

进入 Play 后，左上角面板可切换：

- `Original`：原始材质；
- `on-Load`：compute 整图解压到 PBR RenderTexture；
- `on-Sample`：默认走可见材质的分块解压缓存；
- `Lit / Base / Normal / Metal / Rough`：通道检查。

`F1` 隐藏或显示面板。

## on-Sample 的准确含义

当前默认路径是 tile cache，而不是每个 fragment 永久执行 MLP。它会为可见材质分配一个受分辨率上限约束的 PBR 缓存，分帧解压 tile，随后让普通 URP 材质采样该缓存。

默认值：

```text
Tile size               256
Cache max dimension     1024
Tiles per frame         8
Eviction delay          5 seconds
```

`NtcInferenceOnSample_URP.shader` 是 compute/tile 路径不可用时的兼容回退。该 shader 逐 fragment 运行 MLP，但只输出 BaseColor，属于 Unlit 验证路径。

## 功能限制

- 默认通过 `Application.dataPath` 读取 Editor 工程中的 `.ntc`；
- Player 数据由宿主工程通过 StreamingAssets、Addressables 或自定义导入器提供；
- 只处理 Renderer 的第一个 `sharedMaterial`；
- 通道识别依赖贴图名称；
- 渲染集成面向 URP；
- compute 路径要求平台支持 ComputeShader；
- on-Sample cache 是整张低分辨率目标纹理，不是稀疏显存页；
- Alpha Cutout 需要包含 `AlphaMask` 通道的 `.ntc`。

更完整的设计、数据格式和验证记录见仓库根目录 [`docs/`](../docs/README.md)。
