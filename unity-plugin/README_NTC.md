# NTC 接入插件 — Classic-Sponza (URP) 全场景集成

本工程是 `Classic-Sponza` 的副本，用于验证 **NTC（Neural Texture Compression）** 的三种接入方式。
所有 NTC 相关内容在 `Assets/NtcPlugin/` 下，不改动原始美术资产。

## 目录结构

```
Assets/NtcPlugin/
  Runtime/
    NtcJson.cs / NtcContainerReader.cs / NtcPackingReport.cs   .ntc 容器纯 C# 解析
    NtcAssetBuilder.cs                                         .ntc -> Texture2DArray + ComputeBuffer
    NtcSceneController.cs                                       ★ 多材质场景控制器（按钮面板+统计）
  Editor/
    NtcSetupMenu.cs                                            一键挂载菜单
  Shaders/
    NtcInference.hlsl                                         MLP 推理数学（纯 HLSL，无 CG 古法）
    NtcInferenceOnSample_URP.shader                            ★ URP 逐像素解码 shader
  NtcData/
    SponzaBricks.ntc / Arches.ntc / ...                         各材质的 .ntc 压缩产物
    Decompressed/
      SponzaBricks/ Arches/ ...                                 每材质的 NTC 解压 PNG（Color/Normal/AO/Metalness/Smoothness）
```

## 三种接入方式

| 模式 | 按钮 | 做法 | 显存 |
|------|------|------|------|
| **A. Original** | `Original` | 保持工程原始材质与贴图（基线对照） | 原始贴图全量 |
| **B. Inference on Load** | `on-Load` | 材质/shader 不变，只在读贴图入口把贴图换成 NTC 解压版 | 与原始相当 |
| **C. Inference on Sample** | `on-Sample` | 换成 NTC shader，逐像素跑 MLP 从 latent 直接解码 | 仅 latent+权重，大幅省显存 |

三种模式都遵循"原有加载流程不动、只在取贴图那一刻注入 NTC"的原则。

## 面板操作（不靠键盘）

进入 Play 后，左上角自动显示控制面板：
- **三个按钮**：`Original` / `on-Load` / `on-Sample`，点击即切换全局模式
- **材质列表**（可滚动）：显示场景每个材质的 NTC 数据状态（`OK` = 有 .ntc，`PNG` = 仅解压贴图，`--` = 无数据）
- **统计区**：磁盘压缩率、显存占用对比、C 模式相对 A/B 的显存节省%
- **`F1`** 开关面板

## 使用步骤

1. 用 **Unity 2021.3.33f1c1** 打开本工程。
2. 菜单 **`NTC / Open Sponza lookdev Scene`**（或 Showcase）。
3. 菜单 **`NTC / Add Scene Controller To Open Scene`** —— 创建 `NTC_Controller`。
4. 进入 **Play**，左上角面板点按钮切换三种模式。

控制器会**自动扫描场景所有材质**，并自动加载对应的 `.ntc` 和解压贴图。有数据的材质标 `OK`，没数据的标 `--`。

## 批量压缩场景所有材质

场景所有材质的 `.ntc` 由 `RTXNTC/batch_compress_sponza.py` 批量生成：
- 对每个材质目录，转 tif→PNG
- 调 ntc-cli 压缩（16 bpp，生成 mips）
- 调 ntc-cli 解压为 PNG

已覆盖：Arches, Ceiling, Column_A/B/C, Details, Flagpoles, Floor, Ivy, Lion_Head, Lion_Shield, Roof, Vase_*（砖墙已有）。
Fabrics 因多颜色变体命名特殊，未自动处理。

## ⚠️ 已知点

- **Bricks.mat 是 HDRP/Lit**：URP 下可能品红，需改为 `Universal Render Pipeline/Lit` 并重指贴图（A/B 模式）。C 模式不受影响。
- **on-Sample 模式输出 BaseColor（unlit）**：用于验证逐像素解码正确性与显存/带宽优势，不参与完整光照。如需光照版可后续定制 URP Surface shader。
- **Editor Play 模式**：控制器用 `Application.dataPath` 读文件，仅 Editor 可用。Build 需移到 StreamingAssets。
