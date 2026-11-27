# 回归测试

测试宿主需要匹配 URP。公开 clone 不含 Sponza 素材或25文件烘焙语料；可以先运行 CPU/合约子集，不能把它称为完整 GPU 验收。

```powershell
.\tools\Run-NtcEditModeTests.ps1 -UnityEditor '<Unity.exe>' -CpuOnly
.\tools\Run-NtcEditModeTests.ps1 -UnityEditor '<Unity.exe>' -Corpus '<25-file corpus>'
```

`-ProjectPath` 默认 build/ntc-review-tests，是一次性工程，Packages manifest 与镜像会重建。Editor 不含 bundled URP 时指定 `-UrpVersion`；需要时指定 `-RegistryUrl`。不要使用用户正在开发的工程作为测试宿主。

## 覆盖

- 合成 NTEX 解析、异常格式、固定网络与 mip 契约；材质槽位、通道和标量语义。
- GUID 重名隔离、无纹理模板与原图恢复资产、UPM 输入文件解析、Player 缺数据拒绝。
- 初始化期间 GameObject/组件 disable 后恢复的真实 PlayMode 往返。
- 有语料和 D3D12 时：真实模型 compute/portable 推理、packed16 latent 数值对照。
- 真实 Direct fragment 对 decoded texture backend 与 stock URP Lit；BaseColor/Normal/Mask/AO、纹理/光照开关、alpha smoothness、滤波与 mip bias。
- 内存去重、块格式和未创建 RenderTexture。

完整模式要求 passed>0、failed=0、skipped=0；CPU子集可跳过GPU和语料项，日志明确标subset。语料需含恰好25个标准测试容器，且 Bricks.ntc、Flagpoles.ntc 的通道/分辨率匹配既有回归。它们不是可任意替换名称的fixture。可用环境变量 NTC_TEST_DATA_DIR 指定目录，但脚本以 -Corpus 为准并在结束恢复环境变量。

源素材授权限制使该套真实语料暂未公开；目前公共CI只验证源码包导出，不伪装成完整Unity/GPU测试。完整复现实验需要自己取得对应语料；在自己的项目烘焙和使用插件不需要这套测试素材。

数值测试不是完整场景帧性能；stock片段对照不代表所有灯光/阴影/renderer feature组合已验收。独立构建的生命周期Player补足真实出图、共享消费者保护、释放、恢复和首绘计时，详见仓库 docs/BENCHMARK.md。
