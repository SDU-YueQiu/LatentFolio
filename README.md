# LatentFolio

**给 ComfyUI 用户的本地图库。** 出图自动入库，按人物、标签、评分和提示词找回来——纯本地，不联网。

> 替代停更的鲸鱼图库 v1.0.25。它那五个毛病——目录不排序、提示词靠猜、只有深色主题、导入串行、几千张就卡——在这里都不再成立。

![platform](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D6)
![.NET](https://img.shields.io/badge/.NET-10-512BD4)
![offline](https://img.shields.io/badge/network-offline-6E7781)
![self-test](https://img.shields.io/badge/self--test-120%20passed-2DA44E)

![总览](docs/shots/overview.jpg)

## 它替你做的三件事

**出图就在库里。** 目录监视常开，ComfyUI 一落盘就进库；同样的图按内容哈希只收一份，不用手动导入。

**按人、按词找图。** 从提示词里认出角色、画师、模型（94% 的图能拿到正向词），点一下就是筛选；记不住就搜。

**几千张也不卡。** 自写虚拟化网格，只实例化屏幕里那几十格。本机 4,900 张，刷新一次 1–3 ms。

## 快速开始

需要 Windows 10 1809+ / 11，以及 .NET 10 SDK（只运行的话 Desktop Runtime 就够）。

```powershell
git clone https://github.com/SDU-YueQiu/LatentFolio.git
cd LatentFolio
dotnet build LatentFolio.slnx -c Release
.\src\LatentFolio.App\bin\Release\net10.0-windows\LatentFolio.exe
```

也可以直接双击仓库根目录的 `LatentFolio.lnk`（指向 Release 产物）。

首次启动会让你选一次 `ComfyUI/output`（找不到就猜几个常见路径），之后自动扫描。
数据都在 `%LOCALAPPDATA%\LatentFolio`：`library.db` 加 `thumbs\`。删掉这个目录 = 清空重来。

## 按任务用

| 你想 | 怎么做 |
| --- | --- |
| 找今天出的图 | 左栏「资源库」→ 日期 |
| 找某个角色 | 顶部搜索框，或左栏「人物」、人物墙 |
| 只看高分 | 星级筛选，或「我的栏」建一条「评分 ≥ 4」 |
| 看提示词 / 种子 / 工作流 | 点一张图，右侧详情栏（工作流展开时才解析） |
| 放大看细节 | 双击进查看器：滚轮切图、Ctrl+滚缩放、1:1、RTX VSR 2× |
| 删错了 | 「已删除」→ 还原，评分和批注都还在 |
| 按自己的规则存一批 | 「我的栏」→ 新建：22 个字段的条件树 + 实时预览 |

## 证据

本机实测（约 4,900 张 / 271 个目录）：

| 指标 | 数字 |
| --- | --- |
| 冷启动到可交互 | 1.6 s |
| 全量读库 | 43–70 ms |
| 刷新网格 | 1–3 ms |
| 提示词解析率 | 94.0% |
| 内置自检 | 120 条，全绿 |

`--bench 50000` 会造一个 5 万张的临时库（不碰真库），Core 侧实测：

```
合成 50,000 张
落库          2316 ms
全量读入       423 ms
三条件筛选      16 ms  -> 12,339 张
排序             4 ms
相似分组        97 ms  -> 0 组 / 0 张
```

滚动和筛选的开销跟图库总量基本无关，只跟屏幕上有几个格子有关。

| 查看器 | 统计 |
| --- | --- |
| ![查看器](docs/shots/viewer.jpg) | ![统计](docs/shots/stats.jpg) |

## 边界

- **只有 Windows**：WPF + SkiaSharp，没有跨平台计划
- **不联网**：没有账号、没有云同步，不上传任何图
- **不碰 ComfyUI**：不内置模型、不替你跑图，只读 `output`
- 多选批量改批注**故意不做**
- 库里已有的同哈希重复图不会自动清（判重只管新导入）

## 深入

- [架构与实现](docs/architecture.md)：提示词解析、数据模型、缩略图、虚拟化、VSR
- [设计语言](docs/design-language.md)：配色、字阶、圆角、间距
- [旧版文档](docs/legacy/)：需求规格、页面设计、逆向分析

<details>
<summary>开发与自检</summary>

```powershell
.\tools\link-release.ps1        # 编译 Release + 更新根目录快捷方式
LatentFolio.exe --self-test     # 120 条交互回归，不抢前台，结果写 %TEMP%\lf-selftest.txt
```

三个工程：`LatentFolio.Core`（解析 / 扫描 / 筛选，无 UI 依赖）、`LatentFolio.App`（WPF）、`LatentFolio.Vsr`（P/Invoke NVIDIA Video Effects）。

</details>
