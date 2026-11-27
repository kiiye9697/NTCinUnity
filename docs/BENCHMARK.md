# Player 验收与基准

当前 `NtcBenchmarkRunner` 默认按 Original、InferenceOnLoad、InferenceOnSample、InferenceOnSampleDirect 四模式运行。模式完成、GPU fence 和失败材质组都参与成功判定，模式失败时退出非零。脚本同时校验本次 JSON，不能只看日志里出现 Complete。

## 资源验证与渲染性能

```powershell
# 已准备项目，构建后显式运行资源/队列验收
.\tools\Run-NtcBenchmark.ps1 -UnityEditor '<Unity.exe>' -SkipPrepare -ResourceOnly
# 已有 Player，严格要求实际相机渲染
.\tools\Run-NtcBenchmark.ps1 -UnityEditor '<Unity.exe>' -SkipBuild -Player '<exe>'
```

默认输出 build/benchmark-results：benchmark.json、summary.csv、frames.csv、Player/build log。工具进程隐藏启动；窗口状态可能令相机不提交帧，严格模式会失败。`-ResourceOnly` 显式允许此情况，并只能发布资源、模式准备时间及队列完成情况。不要从该运行中的 frame interval 推导帧率。

`framePerformanceValid=true` 至少要求相机回调存在；正式性能分析还需目标分辨率、可见材质、已完成的 residency、GPU 时间可用性和固定运行条件。GPU frame timing 不可用时不能用 CPU/主线程时间替代。RenderDoc 注入会影响时序，应只用于工作流诊断。

## 首次使用专项

`NtcLifecycleValidationBuild` 生成独立小 Player（依赖本地已准备的 Column_A 绑定），实测 Direct fragment，并记录首次与第二次 draw 加同步回读、提交、回读等待、共享消费者保护、最后消费者移除后的回收、原图恢复和第二轮切换。

```text
<LifecyclePlayer.exe> -force-d3d12 -ntcLifecycleOutput <directory> -ntcFirstDrawBudgetSeconds 10
```

本机 RTX5080 / Unity2021.3.33f1 / URP12.1.13：新 Player 首次496ms、warm3.1ms；后续两个新进程首次21/15ms。**驱动磁盘缓存未清除**，新进程不代表驱动缓存冷启动。计时含同步回读，不能标为 GPU frame time。滤波已改为循环共用解码调用点，避免多层展开复制网络；完整场景首次使用和其他驱动仍需验证。

## 当前证据边界（2026-11-27）

本机正式 Showcase 的四模式资源检查完成，failedGroups=0、GPU fence 全部通过，Direct22/24组、2组明确Original回退。隐藏运行没有相机回调，framePerformanceValid=false；独立真实 fragment 和生命周期探针提供实际出图证据，但不替代完整场景帧性能。

估算颜色纹理与NTC buffer：Original86.3MiB、Direct109.2MiB；源图确能释放，但当前数据没有净省显存。预算不包含深度附件、MSAA副本、对齐、CPU副本和其他GPU缓冲；驱动总量查询无效时单独标 unavailable。

## 参数

| Player 参数 | 默认值 | 说明 |
| --- | --- | --- |
| `-ntcBenchmarkModes` | 四模式 | 逗号分隔 |
| `-ntcBenchmarkWarmupFrames` | 180 | 每模式预热帧 |
| `-ntcBenchmarkSampleFrames` | 600 | 每模式样本帧 |
| `-ntcBenchmarkInitTimeout` | 180 | 初始化秒数 |
| `-ntcBenchmarkModeTimeout` | 180 | 模式/驻留秒数 |
| `-ntcBenchmarkGpuFenceTimeout` | 30 | 队列完成秒数 |
| `-ntcBenchmarkOutput` | persistentDataPath | 输出目录 |
| `-ntcBenchmarkAllowNoRenderedFrame` | 关闭 | 仅显式ResourceOnly脚本传入 |
| `-ntcBenchmarkAllowCameraMotion` | 关闭 | 默认锁定相机 |
| `-ntcBenchmarkRenderDocCapture` | 关闭 | 需要预先注入RenderDoc |
| `-ntcBenchmarkNoQuit` | 关闭 | 调试保持进程 |

构建默认非Development Windows x64/D3D12，临时启用FrameTimingStats并恢复配置。性能比较固定GPU/驱动、电源、分辨率、Unity/URP、场景/相机、编码数据与流预算，记录缓存条件，多进程重复测量。启动到Controller ready从BeforeSplashScreen托管回调计时，不含之前原生启动阶段。
