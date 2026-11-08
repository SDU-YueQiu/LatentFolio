# LatentFolio 设计语言 · Quiet Studio

> 这份文档是本次整轮界面改版的唯一标准。所有页面共用同一套 token 和组件配方，
> 各页只负责「信息怎么排」，不负责「换个好看的蓝色」。

## 1. 定位

图库类工具。界面是装图片的托盘，不是海报。

三条原则：

1. **图片最响，界面最静。** 除选中/焦点/进度外，界面本体不出现主色块。中性灰打底，图片自己发光。
2. **靠层级，不靠装饰。** 分层只允许三种手段：底色差（White / Panel / Inset）、1px 描边（Border / Hairline）、字重字号差。阴影只给浮层和卡片，网格与列表项一律不准用。
3. **密度即美感。** 这是要放几千张图的工具。行高、间距、字号必须成体系，宁可紧一档也不要散。

字体固定 `F.UI`（微软雅黑 UI）。字号只用以下几档，**不许自创**：
`11 / 11.5 / 12 / 12.5 / 13 / 13.5 / 15 / 20 / 24`。

间距只用 `4 / 6 / 8 / 10 / 12 / 14 / 16 / 20 / 24`。

## 2. Token

颜色一律走 `Theme.xaml` 里的画刷键，**不要在页面里写十六进制**（品牌色/画布渐变除外，那也已有键）。

| 键 | 值 | 用在哪 |
|---|---|---|
| `B.Window` | #FFFFFF | 窗口底、内容主面 |
| `B.Canvas` | #F7F8FB | 卡片外面的画布（统计页、设置页、查看器底部） |
| `B.Panel` | #FBFCFD | 侧栏、详情栏这类「次要面」 |
| `B.Inset` / `B.Tile` | #F1F3F8 | 凹槽：搜索框底、缩略图占位、分段控件底槽、进度条底 |
| `B.Hover` | #F2F4F9 | 悬停 |
| `B.Pressed` | #E9ECF3 | 按下 |
| `B.Sel` / `B.AccentSoft` | #EDEFFE | 选中底 |
| `B.AccentSoft2` | #E2E5FD | 选中底的悬停/强调 |
| `B.Hairline` | #F1F2F6 | 面内分隔线（最轻） |
| `B.Border` | #E7E9F0 | 常规描边 |
| `B.BorderStrong` | #D6DAE4 | 需要更清楚的控件描边 |
| `B.Text` | #101319 | 主要文字 |
| `B.Text2` | #5B6373 | 次要文字、值 |
| `B.Text3` | #98A0AF | 辅助文字、计数、栏标题 |
| `B.Accent` | #4B5CF5 | 选中框、焦点、进度、开关 |
| `B.AccentDark` | #3A48D8 | 主按钮悬停 |
| `B.Star` | #F5A524 | 星级 |
| `B.Danger` / `B.DangerSoft` | #E5484D / #FEECEC | 删除、丢失 |
| `B.Ok` | #12A594 | 成功、同步中 |
| `B.Logo` | 渐变 #5B6CFF→#8B5CF6 | 只给左上角 logo |
| `B.Stage` | 渐变 #F6F7FA→#EDEFF5 | 查看器图片舞台底 |
| `B.Glass` | #F5FFFFFF | 浮在图片上的工具条 |
| `B.GlassDark` | #B31A1E27 | 图片角上的计数胶囊 |

圆角：控件/按钮/输入 **8**，列表行/分段项 **9**，卡片 **12**，胶囊 **999**，缩略图 **8–10**。
阴影：`Fx.Card`（卡片，慎用）、`Fx.Pop`（浮层/菜单）、`Fx.Image`（查看器里的大图）。

## 3. 文字样式（直接引用，不要内联 FontSize 造新层）

| 样式键 | 规格 | 用在哪 |
|---|---|---|
| `T.Page` | 20 SemiBold | 页面标题（总览、人物、统计、设置、标签） |
| `T.Title` | 15 SemiBold | 窗口标题、面板标题、查看器文件名 |
| `T.CardTitle` | 13.5 SemiBold | 卡片标题 |
| `T.Label` | 12 Text2 | 字段名、按钮旁标签 |
| `T.Sub` | 11.5 Text3 | 计数、副信息 |
| `T.Section` | 11.5 SemiBold Text3 | 栏标题（我的栏 / 人物 / 资源库） |
| `T.Value` | 12.5 Text | 字段值、右对齐数字 |
| `T.Num` | 24 SemiBold | 统计指标大数字 |
| `T.Mono` | 11.5 Cascadia Mono Text2 | 路径、尺寸、种子、参数 |
| `T.Empty` | 12.5 Text3 居中 | 空状态一行话 |

中文与数字混排时，数字优先用 `T.Num` / `T.Value`；路径、种子、尺寸一律 `T.Mono`。

## 4. 组件配方

- **主按钮** `Btn.Primary`：只在每页至多一个（保存/确认/应用）。
- **次按钮** `Btn.Outline`：白底 + `B.Border` 描边，高 30，左右 padding 12。
- **图标按钮** `Btn.Icon`（30×30）/ `Btn.IconSm`（24×24）：glyph 13px，`B.Text2`，悬停给底色。**所有只有图标的动作都必须带 ToolTip。**
- **胶囊按钮** `Btn.Pill`：筛选入口这类"可展开的条件"。
- **条件钉** `Chip`：已生效的条件，`B.AccentSoft` 底 + 主色字。
- **分段控件**：`Border Style="{StaticResource Seg.Track}"` 里放若干 `Btn.Seg`，选中项由代码设 `Background=White`、`Foreground=B.Accent`，并加 `Fx.Card`（或用 `B.AccentSoft` 底）。**不要用 TabControl。**
- **卡片** `Card`：白底 + `B.Border` + 圆角 12 + Padding 16。卡片之间留 10–12。
- **字段行**：左标签 `T.Label`（宽 72–88，右对齐或左对齐但要统一），右值 `T.Value`，行高 24，行间不加分隔线；段落之间才加 `HLine`。
- **列表行**：`RowItem`，高 32–34，左内边距 10，右内边距 10，文字 12.5，计数 11.5 `B.Text3`。
- **空状态**：`T.Empty` 一行。**不写操作指引**（"点击这里开始…"这类不许出现），最多给一个按钮。
- **悬停反馈**：底色变 `B.Hover` 即可，不要位移、不要放大、不要阴影。

## 5. 图标

只用 `Segoe MDL2 Assets`（`F.Icon`），字符写在 `Text` 里。已验证可用的字形：

| 用途 | 码位 |
|---|---|
| 设置 / 重新扫描 | E713 / E72C |
| 搜索 / 清除 | E721 / E711 |
| 返回 / 前进 | E72B / E72A |
| 展开 ▾ / ▸ / ◂ / ▴ | E70D / E76C / E76B / E70E |
| 勾 / 取消 | E73E / E711 |
| 更多 ⋯ | E712 |
| 删除 / 编辑 / 复制 | E74D / E70F / E8C8 |
| 文件夹 / 图片 / 人物 / 标签 | E8B7 / E8B9 / E77B / E8EC |
| 星（空心/实心） | E734 / E735 |
| 筛选 / 排序 / 同步 | E71C / E8CB / E895 |
| 窗口 最小/最大/还原/关闭 | E921 / E922 / E923 / E8BB |
| 信息 / 警告 | E946 / E7BA |
| 首页 | E80F |

不确定的字形不要用（会显示成方框），宁可写字。

## 6. 本页美术方向

### 6.1 主壳 + 图库页（`MainWindow.xaml`：顶栏 / 左栏 / 网格 / 人物墙 / 统计宿主 / 状态栏）

- **顶栏 46px**：logo 24×24 用 `B.Logo` 渐变、圆角 7、白字 "L" 13 SemiBold；标题 15 SemiBold；其后竖线 + 库摘要 `T.Sub`。搜索框做成胶囊：`B.Inset` 底、无描边、高 32、宽 520、内含 E721 图标，聚焦时变白底 + `B.Accent` 描边。右侧动作按钮统一 `Btn.Icon`，窗口三键用 `Btn.Win`（关闭悬停 `B.Danger` 底 + 白字）。底边一条 `HLine`。
- **左栏（232 / 收起 56）**：底色 `B.Panel`，右边界 `B.Border` 竖线。三块之间用 `HLine` 分隔，每块顶部：栏标题 `T.Section` + 右侧计数/链接 `T.Sub`（如「全部 ›」），标题区左右 padding 14、上下 10。
  - 「我的栏」行：高 32，左侧 6px 彩点 + 名称 12.5 + 右对齐计数 11.5 `B.Text3`。
  - 「人物」九宫格：沿用 `PeopleCols` / `PeopleCardSide` / `PeopleBlockHeight` 绑定，图 1:1 圆角 8，右上角不放计数，**右下角**一个 `B.GlassDark` 胶囊显示张数（10px 白字）；悬停给 1.5px `B.Accent` 描边。**不要改这三个绑定，也不要给外层加 Stretch 对齐。**
  - 「资源库」树：行高 30，缩进每级 12，展开箭头 10px `B.Text3`（展开时 E70D、折叠 E76C），名称 12.5，右侧张数 11.5 `B.Text3`；选中行 = `B.AccentSoft` 底 + 左侧 3px 主色杆 + 名称 `B.Text`。
  - 「已删除」行固定在底部，`T.Label` + 右侧张数胶囊（`B.DangerSoft` 底 / `B.Danger` 字）。
- **网格工具栏**：标题 `T.Page`（"总览"）+ 张数 `T.Sub`；右侧按「筛选入口（`Btn.Pill`）→ 排序（ComboBox 30 高 + 方向 `Btn.Icon`）→ 竖线 → 尺寸四档」分组，组间 14，组内 6。尺寸四档放进 `Seg.Track`，选中档 `B.AccentSoft` 底。条件钉行在标题下方 10px，`Chip` + 悬停变深。
- **网格**：`Spacing` 从 8 改 **12**，`Pad` 用 `20,6,20,24`，`CaptionHeight` 保持 **38**（模板里两行 21 + 17 必须对上）。格子图圆角 10、底色 `B.Inset`；文件名 11.5 `B.Text2` 单行省略；星级 `StarSize=8.5`、未点亮的星是极浅灰（`RatingBar` 已改好，别动它）。选中态：**2px 主色描边 + 2px 白内衬**，悬停只给淡底色。
- **人物墙**：标题 `T.Page` + 计数；卡片宽 200，图 1:1 圆角 12，名字 13 SemiBold，tag 10.5 `B.Text3`，右上角 `B.GlassDark` 张数胶囊；悬停描边 `B.BorderStrong`。
- **统计宿主**：`返回图库` 用图标按钮 + 文字，标题 `T.Page`，门槛选择器用 `Seg.Track`。
- **状态栏 26px**：左：选中张数/提示（12 `B.Text3`）；右：`命中 x / y`（11.5 `B.Text3`）。顶边一条 `HLine`。

### 6.2 查看器（`MainWindow.xaml` 里 `ViewerPane`，由第二阶段负责）

- 舞台底 `B.Stage`，大图圆角 12 + `Fx.Image` 阴影，居中留白 24。
- 舞台顶部一条 46px 工具条：`‹ 返回`、文件名 `T.Title` + 尺寸 `T.Sub`、右侧 `RTX VSR`（开启时主色底白字，关闭时 `Btn.Outline`）、原图/结果分段、上一张/下一张（E76B/E76C）、`在文件夹中显示`。
- 胶片条：白底 + 顶部 `HLine`，缩略图圆角 6，当前那张 2px 主色描边，其余不透明度 0.7、悬停 1.0；条高仍由 `FilmImageHeight` 决定。
- 分隔条（7px）不用画线，悬停时给 `B.Accent`。

### 6.3 详情栏（`Views/DetailPanel.xaml`）

- 底 `B.Panel`，与图库之间已经有竖线，内部不要再加大块描边。
- 从上到下：文件名 `T.Title`（两行内省略）+ 一行元信息 `T.Sub`（尺寸 · 大小 · 类型）；星级（`StarSize=20`）+ 备注框（`B.Inset` 底、无描边、圆角 9、高 64、聚焦白底主色描边）。
- 三个段落标题用 `T.Section`，段内字段用「标签 11.5 `B.Text3` 左 / 值 `T.Value` 右」的 24px 行；采样参数的值用 `T.Mono`。
- 提示词块：`B.Inset` 底、圆角 10、padding 10、`T.Mono` 行高 17，右上角一个 `Btn.IconSm`（E8C8 复制）。正向块最多 7 行滚动，负面块 2 行。
- 段与段之间 14，不要用大卡片套小卡片；只有提示词块和参数块这两处用凹槽底。

### 6.4 标签 / 候选（`Views/TagsWindow.xaml`）

- 左列 220：分组列表（`RowItem`），选中 `B.AccentSoft`；顶部一个胶囊搜索框。
- 右侧：标题 `T.Page` + 计数、右侧主按钮（新增）；表格行高 34，表头 11.5 `B.Text3`，行内不画竖线，只画 `B.Hairline` 横线；数字右对齐；空状态一行。
- 底部一行统计用 `T.Sub`。

### 6.5 列编辑器 / 筛选（`Views/ColumnEditorWindow.xaml`）

- 两栏：左「列」列表 260（可拖拽顺序，缩略用 E76C 拖杆不行就用 6 个点的 `B.Text3` 文本），右「筛选条件」树。
- 每列卡片：`Card.Flat`，高 44，左侧 8px 色块圆点，名字 12.5，右侧开关（CheckBox）和更多按钮；选中描边 `B.Accent`。
- 条件树：每层一行 30，`▾` 箭头 + 字段名 + 运算符下拉（高 26）+ 值输入，行与行之间 6。
- 底部按钮排右对齐：`取消` `Btn.Ghost` + `确定` `Btn.Primary`。

### 6.6 设置 / 对话框（`Views/SettingsWindow.xaml`、`TagDialog.xaml`、`PromptWindow.xaml`）

- 页面标题 `T.Page`，其上留 20。
- 每个分组一张 `Card`（圆角 12、padding 16、组间 14），组标题 `T.CardTitle` + 右侧状态 `T.Sub`；组内字段行高 32，控件靠右。
- 危险动作（清空缓存、清空判重）文字用 `B.Danger`，但仍是 `Btn.Outline`，不要红色实心块。
- 底部按钮排：右对齐 `关闭` 用 `Btn.Primary`（宽 96 高 32）。
- 框内不要出现"提示/说明"段落，除已有的必要解释文字外不新增。

### 6.7 统计页（`Views/StatsBuilder.cs`，C# 建 UI）

- 指标卡：7 张等宽 `Card.Flat`，间距 10，内 padding 14：标签 11.5 `B.Text3` + 数字 `T.Num` + 副行 11 `B.Text3`。
- 分段门槛放标题右侧。
- 分布条：标签列宽 76 右对齐 11.5 `B.Text3`，条高 6 圆角 3，底 `B.Inset`、填充 `B.Accent`，右端百分比 11.5 `B.Text3` 宽 56 右对齐；行高 26。
- 表格：表头 11.5 `B.Text3`，行高 28，数字右对齐，悬停 `B.Hover`，不画网格线。
- 全部通过 `Application.Current.FindResource("B.Accent")` 这类键取色，不要写字面量。

## 7. 硬约束（违反就算失败）

1. **不改行为**：绑定路径、`x:Name`、命令、事件处理、`Tag`、`AutomationProperties.Name` 一律保持。`AutomationProperties.Name` 是对外契约（自检和 UI 自动化在用）。
2. **不改结构尺寸**：窗口三行 46 / * / 26；左栏默认 232、收起 56、下限 180；详情栏 380；折叠手柄 22×88；网格 `CaptionHeight=38`；缩略图四档 `136/188/252/330`；胶片条高度由 `FilmImageHeight` 决定。
3. **不加文案**：不写解释性提示、不留假数据、不加 emoji。别把 `ToolTip` 写成教程。
4. **不加依赖**：不引 NuGet、不加图片资源、不改字体族。
5. **性能**：网格格子、列表行里不许出现 `DropShadowEffect` / `BlurEffect` / 动画 / 半透明叠层超过一层。
6. **只改自己的文件**：`Theme/Theme.xaml`、`App.xaml`、`ViewModels/`、`Core/`、别人的页面文件都不许动。需要新样式就在自己文件里定义，键名加 `P.<页面>.` 前缀。
7. **构建必须干净**：`dotnet build src\LatentFolio.App\LatentFolio.App.csproj -c Release` 0 error、0 新警告。不要运行程序、不要截图、不要执行 git 命令。

## 8. 自检红线（会被 `--self-test` 打回的写法）

- 手柄尺寸/位置、右栏 380、左栏 56 收起、左栏 320 拖动、网格行等高、缩略图不裁剪、滚动锚定。
- 人物卡跟着栏宽缩放（依赖 `PeopleCols` / `PeopleCardSide` 绑定）。
- 目录树默认折叠、展开/折叠所有。
- 已删除/丢失计数的文字格式。

改完自己对着这一节读一遍。
