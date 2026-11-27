# 当前评审与可用范围

日期：2026-11-27。结论：受限范围内的安装、烘焙、资产发布、场景绑定、完整 PBR Direct、回退、原图回收/恢复与 Player 构建链路可以使用；尚不能认定为任意 URP 材质、任意设备的完整生产方案。

## 多轮独立 review

本次在先前两轮基础上，分别以全新上下文审阅 Runtime/Shader 与烘焙/资产/构建/工具，再对修复进行交叉复核。已处理：

| 问题 | 结果 |
| --- | --- |
| 初始化跨帧时禁用 GameObject/组件，重新启用卡在 initializing | 取消 coroutine、清理状态与初始化资源，真实PlayMode回归覆盖4种中断时机/方式 |
| Direct 缺 URP12 clustered/light layers/cookies/probes/dynamic lightmap/DBuffer | 补对应变体和decal应用，receive shadows声明与标准Lit对齐；组合场景表现仍需目标项目测试 |
| 部分bake缺少启用的normal/mask/AO/color，却静默丢图 | 明确拒绝Direct并保留Original；未使用语义才采用默认值 |
| Gamma下强制sRGB→linear，Linear常量emission语义不一致 | 依据活动色彩空间解码；EmissionColor按URP Lit声明HDR；分别实跑Gamma/Linear |
| mipMapBias未捕获，放大区域提前丢弃负LOD | 保存/绑定bias，在最终LOD截断前应用；增加放大与缩小fragment对照 |
| Benchmark模式失败/fence失败仍成功、默认绕过渲染 | 退出码与JSON均校验，四模式；ResourceOnly需显式选择，每模式记录采样期间相机帧 |
| 新项目manifest缺直接依赖，PS无法新增属性 | Add-Member支持新增依赖，最小manifest实际内存验证 |
| UPM包内输入纹理虚拟路径无效 | PackageInfo.resolvedPath映射与路径回归 |
| 无私有语料不能跑CPU合约测试，空目录误认为corpus | 独立CPU子集与完整语料GPU入口；空目录不当有效corpus |
| Null图形设备初始化compute报kernel错误 | 检查图形设备、compute能力和kernel存在，安全不可用 |
| 索引透明PNG丢失alpha | Pillow转换前保留transparency，索引/RGBA/RGB实际输入验证 |
| Player整个NTC数据目录缺失仍通过后处理 | required非空时缺目录/文件即失败 |
| 独立DrawMesh探针与管线/剥离变体不一致，部分构建空图 | 探针管线明确关闭未设置的shadow/additional-light功能，保留精确OFF变体，显式设置viewport；D3D12专用构建三次实际出图，失败仍拒绝 |

## 证据

- Unity2021.3.33f1、URP12.1.13、RTX5080、D3D12：58项全套GPU/合约测试，Gamma与Linear各58通过，0失败、0跳过。
- 全新宿主、不传corpus、Null图形设备：36通过、0失败、22明确跳过；不是完整GPU验收。
- 非Development Windows x64 Player重建成功；生命周期探针真实出图、共享消费者保护、最后消费者移除回收、Original恢复和第二轮释放通过。
- 首次实际绘制+同步回读约496ms，warm约3.1ms；驱动磁盘缓存未清除，非GPU帧耗。进程内峰值工作集查询0代表不可用，不能报成0内存。
- Showcase四模式资源/队列检查完成，失败组0、fence通过；Direct22/24组，2组明确Original回退。
- 严格渲染负例：隐藏Player无相机回调时退出2，脚本拒绝；没有自动降级为性能通过。
- 离线CLI实际64x64 color+normal/100steps smoke通过（此前轮次），仅证明工具链活性，非高质量训练或GUI全流程验收。

## 未完成的验收与限制

标准URP/Lit opaque/alpha-clip Metallic是支持边界。透明、视差、Specular、detail、clear coat、纹理emission和自定义shader明确回退。不能据variant存在声称所有阴影/贴花/光照组合、XR/stereo、其他URP版本都已验证。

完整Sponza前台帧性能、清除驱动磁盘缓存后的首次使用、跨GPU/驱动、正式资产高质量GUI烘焙仍需验收。当前隐藏Showcase没有采样期camera frames，其帧时间无效；独立出图不能替代完整场景性能。

当前纹理及NTC缓冲估算Original86.3MiB、Direct109.2MiB。大部分原图确已回收，但这套数据未实现净显存收益；应在相同分辨率/质量下调整bpp、量化与过滤预算。估算不含深度、MSAA副本、对齐、CPU副本和其他GPU缓冲，驱动总量不可用。

## 仓库与可复现性

主页、使用说明、包README、接入契约、基准方法与工具表统一为当前四模式/GUID链路。历史论文/实验与首轮快照单独标记，不作为当前能力承诺。通用包排除NtcData、SDK、Showcase美术、隐藏IDE目录；附metadata、许可证与测试。

公开CI只导出并验证源码ZIP，未接入带Unity license/GPU/私有语料的runner，因此不伪装成Unity测试CI。25文件真实回归语料与Sponza工程不随仓库公开；公共clone可安装并使用自己素材烘焙，也能运行CPU合约子集。
