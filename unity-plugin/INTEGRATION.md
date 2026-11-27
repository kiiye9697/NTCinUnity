# NTC Unity 0.2 接入与验收

当前主线是 Windows D3D12、Unity 2021.3.33f1、URP 12.1.13。Showcase 使用 Linear 色彩空间。Runtime 不需要 CUDA、`ntc-cli` 或原生 NTC DLL；离线烘焙需要 NVIDIA CUDA 工具链、`ntc-cli` 和 Python/Pillow（部分 TIFF 需要 tifffile）。

## 安装和烘焙

将 `unity-plugin` 作为 UPM 包安装，或完整复制到 `Assets/NtcPlugin`，保留 Runtime、Editor、Resources、Shaders 和 `.meta`。宿主先安装匹配版本的 URP。示例 NtcData 不属于通用包。

1. 将材质保存为项目资产；临时运行时材质没有可持久化的 GUID。
2. 打开 `NTC / Bake Window`，扫描当前场景。扫描覆盖全部 `sharedMaterials` 槽位，按材质对象去重。
3. 检查 CLI/Python 路径、bpp、损失权重和训练步数。至少一张输入纹理即可。默认 16 bpp 偏向质量，不能据此认定最省显存。
4. Bake。后台线程只处理冻结的文件/通道数据；完成后主线程验证容器并创建绑定资产。训练失败和资产发布失败都不会显示为成功。
5. 使用 `NTC / Rescan Scene Materials` 并保存场景。

新输出使用 `GUID_localFileId`，不以显示名称命名：

- Bake 输出目录：`<id>.ntc`、`Decompressed/<id>/` 预览。
- `Assets/StreamingAssets/NtcData/<id>.ntc`：Player 数据。
- `Assets/NtcBindings/<id>.asset`：材质纹理组绑定。
- `<id>_Template.mat`：保留源 shader/参数但清除全部原纹理引用。
- `<id>_Direct.mat`：直接采样材质模板，同时保留构建需要的光照关键字变体。
- `Assets/Resources/NtcOriginals/<id>.mat`：按需恢复 Original 的入口。

绑定的 Editor 数据路径使用项目相对路径，Player 使用 StreamingAssets 相对路径。显示名重名不会覆盖另一资产。显式绑定失效会报错，不会悄悄选择同名旧文件。

旧数据可通过材质右键 `NTC / Bind Existing NTC To Material` 或 Bake 窗口 `Bind Existing Selected` 明确绑定。Showcase 准备工具仅自动迁移唯一名称；多路径副本必须 SHA-256 相同，否则拒绝自动选择。

AO 从 URP `_OcclusionMap.g` 烘焙；旧版误用 R 的 packed AO 需要重烘。保留源材质法线强度、光滑度乘数及贴图/光照开关。Alpha 会按 cutout、透明或 BaseMap-alpha smoothness 的使用方式采集。生成数据关闭 GDeflate。

## 四种模式

| 模式 | 路径 | 原图/显存含义 |
|---|---|---|
| Original | 原始材质 | 基准和恢复入口 |
| InferenceOnLoad | Compute 解压整图到 PBR RenderTexture | 完整解压缓存；不等同于 BCn 显存占用 |
| InferenceOnSample | 原有分块解压缓存 | 预算只限制解压 cache，超预算材质保留原图 |
| InferenceOnSampleDirect | Shader 直接解码纹理组并进行 URP PBR 着色 | 无完整解压缓存；仍需 latent、权重和未覆盖材质的原图 |

Direct 支持 opaque/alpha-clip、Metallic 工作流的标准 URP Lit：BaseColor、Normal、Metallic、Smoothness/Roughness、AO 和 BaseMap Alpha。源材质未使用的语义采用标量默认值；已启用贴图缺少所需通道时明确回退Original，而非静默丢图，关闭的贴图关键字不会重新开启。

透明、Specular 工作流、视差、Detail、Clear Coat、贴图 Emission 和非标准 Lit 等当前不属于 Direct 范围，控制器明确保留原材质并记录 `direct original fallback`。Showcase 的 24 组中有 22 组可走 Direct，2 组保留原材质；这不是全场景纯 NTC。

## 统一采样接口与滤波

`NtcMaterialSampling.hlsl` 提供：

```hlsl
LoadMaterialTexel(descriptor, texel, mip, wrapModes, channels);
NtcMaterialSamples material = SampleMaterialTextures(uv, ddx(uv), ddy(uv));
```

一次 neural texel load 返回整组通道；语义映射、乘数和缺失默认值在材质层处理。相同采样状态的 map 共用结果，不为每个 PBR 通道重复解码。

绑定保存每张 map 的 Wrap U/V、Point/Bilinear/Trilinear、Aniso、mip bias 和 ST。Direct 按 URP Lit 的约定对所有 map 使用 BaseMap ST，按各 map 的采样状态处理寻址和滤波。不同采样状态需要额外的纹理组读取。

双线性需要多个 texel，三线性需要两个 mip，确定性各向异性参考滤波最多 16 taps。因此一次材质接口调用不保证只执行一次 MLP，也不等价于一次硬件 Texture.Sample 的开销。当前没有实现 STF、时域重建或 Cooperative Vector/Tensor 后端。

滤波器使用动态循环中的单个解码调用点，避免 map × mip × corner 多层函数内联复制整个网络。验收分开记录独立 Player 进程首次绘制与同 Shader 的 warm 绘制，且区分提交和阻塞回读时间。未清驱动磁盘缓存，不能把这些墙钟数值标成纯 GPU 帧耗。

支持时 latent 使用 B4G4R4A4 16 位纹理；CPU 上传交换 R/B nibble，使 shader 看到的数值顺序与原 RGBA32 展开一致。不支持该格式或线性过滤时回退到 RGBA32。真实 Bricks/Flagpoles 测试确认 latent 存储减半，解码 RGB 字节误差为 0。

## 原图生命周期和统计

`releaseOriginalTextures` 默认关闭。开启后，仅在 Direct 材质成功替换、恢复资源可加载时解除相应原材质引用，再执行 `Resources.UnloadUnusedAssets()`。

不会 `Destroy`/强制卸载导入纹理；仍被其他 renderer、fallback、脚本引用的共享纹理继续保留。切回 Original 会先加载恢复材质，再更换 renderer，最后释放不再使用的 NTC 资源。恢复失败保留当前显示内容。OnDisable 的恢复是同步加载，可能有停用时延。

常驻 binding 在 Player 中不保留 Editor source material 引用；无纹理模板也会清除旧 shader 遗留的纹理项。

统计按对象 ID 去重，列出原图、latent、权重、解压 cache 和其他已加载纹理。纹理按实际 graphics format、压缩块和完整 mip 链估算。它是颜色纹理 + NTC buffer 账本，排除深度附件、MSAA 副本、驱动对齐、CPU 副本和其他 GPU buffer；驱动查询独立展示，无有效值时标为 unavailable。

释放原图不保证净显存下降：训练分辨率、bpp、latent 表示与原图 BCn/导入尺寸都影响结果。比较时必须同时记录这些条件和 fallback 比例。

## 可重复准备、构建、测试

```powershell
.\tools\Prepare-NtcShowcase.ps1 -UnityEditor '<Unity.exe>' `
  -UseBundledPackages -RegistryUrl 'https://packages.unity.cn' `
  -PipelineAsset 'Assets/ClassicSponza/Settings/URP/UniversalRenderPipelineAsset.asset'

.\tools\Run-NtcEditModeTests.ps1 -UnityEditor '<Unity.exe>' `
  -RegistryUrl 'https://packages.unity.cn' -Corpus '<25-file regression corpus>'
```

Registry 参数可省略，使用宿主默认 registry。UseBundledPackages 将选定 Editor 随附的 URP/core/shadergraph 放到项目相对 LocalPackages，并读取其 Burst/Mathematics 版本要求；不依赖旧用户名或固定盘符。

准备工具同步完整插件和 metadata，记录源文件 hash；镜像被本地修改时拒绝覆盖。第一次初始化可明确使用 `-InitializePluginMirror`，被替换文件会备份到 build/prepare-backups。测试宿主是一次性工程，测试包镜像排除大型 NtcData。

Unity batch 入口：

- `Ntc.UnityEditorTools.NtcShowcasePreparation.Prepare`
- `Ntc.UnityEditorTools.NtcAcceptanceBuild.BuildAll`
- `Ntc.Unity.EditorTools.NtcBenchmarkBuild.BuildWindows64`
- `Ntc.UnityEditorTools.NtcLifecycleValidationBuild.BuildWindows64`

构建为非 Development Windows x64/D3D12；只清理生成 Player 的未使用 NTC 文件，不删除工程旧数据。生命周期小 Player 验证共享消费者保护、最后消费者移除后的回收、Original 恢复和第二轮切换。

正式 Player 的隐藏运行如果没有相机回调，只能给资源/队列证据，不能发布帧率结论。独立 fragment 测试和生命周期材质 probe 提供实际绘制证据，不能替代完整场景的前台帧性能测量。
