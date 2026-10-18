# SVS Collision Plus

Summer Vacation Scramble（SamabakeScramble）的胸部软碰撞插件。主要用于减轻孕肚变大后，胸部陷入腹部的问题，同时保留原有胸部晃动。

Soft breast collisions for Summer Vacation Scramble, focused on breast–belly clipping after pregnancy deformation. Based on [AL Collision Plus](https://github.com/rock-1995/AL_CollisionPlus).

## 下载与安装

从 [Releases](https://github.com/rock-1995/SVS_CollisionPlus/releases/latest) 下载 `SVS_CollisionPlus_0.1.4.zip`。

1. 完全退出游戏。
2. 将压缩包中的 `BepInEx` 文件夹合并到游戏目录。
3. 启动游戏。最终文件位置应为 `BepInEx/plugins/SVS_CollisionPlus/SVS_CollisionPlus.dll`。

需要游戏已安装 **BepInEx 6 IL2CPP**。本版适配 SVS 原生 MagicaCloth，在 SamabakeScramble v1.1.5P1 环境下开发验证。孕肚形状仍由 [SVS Pregnancy](https://github.com/rock-1995/SVS.Pregnancy.Plugin.dll) 等形变插件控制，本插件只补充胸部接触处理。

升级替换 DLL 即可，保留原有碰撞参数。**用过 0.1.3 诊断版的用户，请将 `CaptureEvidence` 设为 `false`**：旧配置中的 `true` 不会因为升级 DLL 自动关闭。

卸载时删除上述 DLL。

## 设置

装有 BepInEx Configuration Manager 时，按 **F1** 搜索 `SVS Collision Plus`。也可以退出游戏后编辑 `BepInEx/config/local.svs.collisionplus.cfg`。

| 设置 | 作用 | 默认 |
| --- | --- | --- |
| `Enabled` | 总开关；关闭时清除本插件附加位移 | 开启 |
| `BreastAbdomen` | 胸部与腹部软接触 | 开启 |
| `BreastBody` | 胸部与其他身体部位接触 | 关闭 |
| `BreastGround` | 胸部与场景地面、承托面接触 | 关闭 |
| `CaptureEvidence` | 保存可离线分析的全量现场，有额外开销 | 关闭 |

建议先沿用默认参数。需要调整时：

- `BreastAbdomenStrength`：推离强度。
- `BreastAbdomenMaxOffsetHeightRatio`：最大附加位移。
- `BreastAbdomenCompressionHeightRatio`：允许的少量软压入，越小越严格。
- `BreastAbdomenMaxSpeedHeightRatio`、`BreastAbdomenResponsePerSecond`：响应速度。

## 0.1.4 性能优化

优化表面搜索顺序与身体区域边界，保留全部采样点、碰撞参数、求解迭代和平滑逻辑。

9 个实际场景快照、两种搜索树初始化方式的离线对比中，胸腹求解平均耗时从 **12.81 ms 降至 1.66 ms**，减少约 **87%**。432 次完整求解与 44,648 次最近表面查询均与 0.1.3 结果一致。此数据是同机离线求解耗时，不是游戏帧率测试，也不代表胸与其他身体、地面接触的性能。

## 范围与限制

插件读取怀孕变形后的实际身体网格，附加位移只作用于胸部。它不会改变孕肚形状，也不提供新的腹部或头发弹簧模拟。极端体型、服装和姿势仍可能出现局部穿模。

调试日志和屏幕状态条默认关闭。需要排查问题时可以开启 `CaptureEvidence`，现场会保存到插件旁的 `diagnostics` 目录；正常游玩建议关闭。

源码构建、离线测试和诊断工具见 [开发说明](DEVELOPMENT.md)。仓库不包含游戏资产、第三方运行库、角色网格或现场采集数据。
