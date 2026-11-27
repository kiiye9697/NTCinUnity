# 工具入口

这些 PowerShell 脚本在 Windows 上运行。`-UnityEditor` 是目标 Editor 的完整路径；需要自己安装 Unity、匹配 URP 和对应 Windows build support。工具不下载 Sponza、不提供 Unity license，也不提供私有回归语料。

| 入口 | 前置条件 | 产物与成功标准 |
| --- | --- | --- |
| `Export-NtcPackage.ps1` | 仅仓库源码 | build 下干净 UPM ZIP；验证 metadata，排除 NtcData/SDK/Showcase/隐藏IDE文件 |
| `Run-NtcEditModeTests.ps1 -CpuOnly` | Unity、可获取 URP | 无私有语料的合约子集；GPU/语料检查可 skipped，不能称完整验收 |
| `Run-NtcEditModeTests.ps1 -Corpus ...` | GPU/D3D12、外部25文件语料 | 全套 EditMode XML/log，拒绝 failed、skipped 或零通过 |
| `Prepare-NtcShowcase.ps1` | 本地已有 URP 项目、场景与烘焙数据 | 同步插件/metadata、相对包依赖、迁移绑定、保存场景；失败退出非零 |
| `Run-NtcBenchmark.ps1 -ResourceOnly` | 已准备场景或 Player | 显式资源/队列检查，四模式成功与 fence；不提供帧性能结论 |
| `Run-NtcBenchmark.ps1` | 正常相机渲染的 Player | 严格要求 camera frame；隐藏窗口无回调时失败，不自动降级 |

## 包与测试

```powershell
.\tools\Export-NtcPackage.ps1
.\tools\Run-NtcEditModeTests.ps1 -UnityEditor '<Unity.exe>' -CpuOnly
.\tools\Run-NtcEditModeTests.ps1 -UnityEditor '<Unity.exe>' -Corpus '<25-file corpus>'
```

测试宿主默认 `build/ntc-review-tests`，是一次性项目，其 Packages manifest 和插件镜像会被覆盖；不要把用户正在开发的工程传给 `-ProjectPath`。若 Editor 不含 URP，指定 `-UrpVersion`。网络有需求时可指定 `-RegistryUrl`，不修改系统代理。外部语料包含测试使用的 Bricks.ntc/Flagpoles.ntc；目前不随公开 clone 发布，不能把缺少语料跳过的结果称 full pass。

## 已有本地 Showcase

```powershell
.\tools\Prepare-NtcShowcase.ps1 -UnityEditor '<Unity.exe>' -Project '<existing project>' `
  -Scene '<Assets/...unity>' -PipelineAsset '<Assets/...asset>' -UseBundledPackages
.\tools\Run-NtcBenchmark.ps1 -UnityEditor '<Unity.exe>' -Project '<existing project>' `
  -Scene '<Assets/...unity>' -SkipPrepare -ResourceOnly
```

准备脚本会检查目标镜像是否有本地修改；第一次明确使用 `-InitializePluginMirror` 可先备份再初始化。`-SyncOnly` 只同步代码/所请求包配置，不运行 Editor。`-UseBundledPackages` 仅用于随附 URP 的 Editor，复制至项目相对 LocalPackages；非 bundled Editor 请自行配置匹配 URP。准备不会取得第三方场景素材。

运行脚本默认构建四模式 Player；`-SkipBuild -Player '<exe>'` 可运行现有构建，仍传 `-UnityEditor`。输出中的旧 benchmark.json 在启动前移除，必须由本次 Player 生成；模式失败、缺输出、fence失败或缺少严格渲染条件都失败。

合并验收构建入口为 `Ntc.UnityEditorTools.NtcAcceptanceBuild.BuildAll`，参数包括 `-ntcLifecyclePlayer`、`-ntcBenchmarkPlayer`、`-ntcPipelineAsset` 和可选 `-ntcReleaseOriginalTextures`。它先准备场景，再构建小生命周期 Player 与指定 Showcase。当前小案例依赖本地已准备的 Column_A 绑定，不是公开 clone 自带的通用场景；自己的工程可单独使用 Ntc.Unity.EditorTools.NtcBenchmarkBuild.BuildWindows64。小 Player 用 `-ntcLifecycleOutput '<directory>'` 输出出图与生命周期 JSON；用 `-ntcFirstDrawBudgetSeconds` 配置首次绘制加回读预算（默认10秒）。

[基准说明](../docs/BENCHMARK.md) 区分实际渲染、资源验收、GPU frame time 和驱动缓存影响。
