# NTC Unity Plugin 0.2

Unity URP 的离线材质烘焙与运行时神经纹理访问包。使用完整目录连同 `.meta`；支持 UPM 本地安装，或复制到 `Assets/NtcPlugin`，不要重复安装。宿主工程先配置兼容 URP。

[完整接入说明](INTEGRATION.md) 是本包当前使用契约。烘焙生成 GUID/localId 绑定和数据；添加 Controller 后重新扫描、保存场景，再进行 Player 验收。旧的按材质名称寻找文件仅作迁移兼容。

| 内容 | 职责 |
| --- | --- |
| Runtime | 解析/格式校验、GPU 资产、完整 PBR 绑定、原图恢复与资源统计 |
| Editor | 烘焙、主线程发布资产、精确材质槽、准备/构建验收场景 |
| Shaders / Resources | Direct URP Lit、统一纹理组访问、compute 解码、调试 |
| Tests | 合约、真实数值/fragment、资产与资源回归，见 Tests/README.md |

Direct 支持标准 URP/Lit opaque/alpha clip 的 Metallic 工作流，输出 BaseColor、Normal、Metallic、Roughness、AO 和 Alpha。透明、视差、Specular、detail、clear coat、纹理 emission 等特性显式回退 Original。Runtime 无 CUDA/ntc-cli 依赖；离线烘焙需要对应工具及 Python/Pillow。

已验证 Windows D3D12 / Unity 2021.3.33f1 / URP12.1.13 / RTX5080 / Linear。58 项测试通过和小 Player 实际绘制/回收验证不能替代目标项目的完整场景性能验收。当前示例 Direct 估算显存高于原 BCn 纹理；不保证净显存收益。首次使用计时包含同步回读且未清驱动磁盘缓存。

本包不含 Sponza 美术、大型 NtcData 或 NVIDIA SDK。许可见 LICENSE.md 与 THIRD-PARTY-NOTICES.md。
