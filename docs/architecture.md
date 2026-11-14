# 架构与实现

给要改代码的人。用户向的用法在根目录 [README](../README.md)。

```
扫描 ──► PNG tEXt（prompt / workflow）──► GraphParser ──► SQLite + 缩略图缓存
                                                              │
                                       筛选 / 排序 ◄──────────┘
                                            │
                                    虚拟化网格（只实例化视口内）
```

## 数据

图库数据库是唯一事实来源，**元数据永不删除**。

- 文件没了 → `state = missing`，评分、批注、标签都还在，文件回来自动接上
- 「删除」→ `state = deleted`，进「已删除」，可以还原
- 导入时按内容哈希（前 64KB + 尾 64KB + 大小）判重，撞上的记进 `skip` 表，下次扫描不再重解析
- 原图缩略图 256px WebP 永久保留（约 12.3 KB/张），每张图长期成本约 16.6 KB

| 位置 | 内容 |
| --- | --- |
| `%LOCALAPPDATA%\LatentFolio\library.db` | 索引 + 提示词 + 评分 + 工作流（gzip） |
| `%LOCALAPPDATA%\LatentFolio\thumbs\grid` | 640px WebP q86（约 45 KB/张），按 `路径+mtime+大小+规格` 哈希命名，可整目录删 |
| `%LOCALAPPDATA%\LatentFolio\thumbs\archive` | 256px WebP，永久 |

缩略图的关键不是尺寸而是**解码尺寸怎么定**：按「铺满格子需要的长边」倒推（竖图的长边是高），
解出来正好是屏幕上的像素数，WPF 只做 1:1 贴图。最早拿格子**宽度**当长边，竖图解出来比格子还窄，
被放大一次就是糊的。规格写在缓存键里，换规格时整目录重建，不会拿老图顶替。

相似图分组用 64 位 dHash 的汉明距离。旧库迁移时有一批图的 dHash 是 0（当年走的是
「缩略图已存在」的分支，只在新建缓存时才顺带算一次），所以相似组几乎分不出来——后来从 320 的
网格缓存补算了一遍（约 20 秒，一次性），分组从「5 组 / 10 张」变成「最大一组 12 张，堆叠 ≥3 共 364 张」。

## 提示词识别

`src/LatentFolio.Core/Comfy/GraphParser.cs`，行为对齐 `tools/ref/prompt_parse2.py`：

- 从任意带 `positive` / `negative` 输入的节点出发，反向走图找文本源
- 只从**这张图真正走过的那一支**收：一次排队同时跑好几个模型时，图里有好几套 `CLIPTextEncode`，
  别的分支可能挂着同一段提示词的变体，全图收一遍就会拼成两遍（实测 `anima-pre_00005_`：
  正文后面又多了 `masterpiece…` + `(flat color),(anime coloring )` 那一段）。分支认不出来时
  才退回全图，扫一遍更保险
- 穿过中间节点时**只跟同角色的那一口**（否则 `ToBasicPipe` 这类会把正负两份都收进同一个角色，
  实测 2,495 张被这样搞错）
- 支持 `StringConcatenate` / `Text Concatenate` 这类拼接节点（`text_a`–`text_d` 都认）、
  `Efficient Loader` 的字符串 widget
- 通配符（`Wildcards`）和随机分支（`easy anythingIndexSwitch`）最后选中哪一条只在运行时才知道，
  图里推不出来。`ShowText|pysssss` 会把那次运行实际显示的整段文本写进 `text_0`，
  所以**子树里有随机源时**才采用它 —— 不然只能拼出半截（实测：一条 wildcard 工作流的
  正向词从 80 字符修成完整的 208 字符）
- 但 `text_0` 是**控件上现在显示的字**，不一定是这次运行喂进去的：ShowText 只是挂在链路上的显示节点时，
  它的值可能是很久以前留下的。所以图**能算出确定结果**（字面量拼接）时以图为准，只有子树里有
  通配符/随机分支（`ShowText` 才有可能被刷新、图才算不出来）才信快照。
  实测反例：`ComfyUI_02052_` 和 `02054_` 两张图的 ShowText 都写着同一段 `ligne claire…`，
  而 `02054` 真正喂给 `CLIPTextEncode` 的是 `node 94` 里那串 `hanfu / silver wolf`
- `workflow` 段里的控件值比 `prompt` 段新（通配符抽完写回控件，保存时 workflow 段已更新），
  两边都在、又必须用快照时以 `workflow` 为准（实测 `Krea2-turbo_00047_`：`prompt` 写着上一轮的
  `arched bangs, trilby, harness`，画出来的却是 `workflow` 里那身 `silvery white, teal, kimono skirt`）
- 解析器逻辑改了要 `ParserVersion++`：老库下次启动会强制重解析一遍元数据（不是只重打标）
- 一张图里可能并排放着几条完整流水线（一次排队把 Krea2 / anima / MOSS 各存一张）。模型、步数、CFG、
  种子这些**只认这张图真正走过的那一支**：先按文件名对 `SaveImage` 的 `filename_prefix` 认出输出节点
  （同一前缀挂在多个保存点上时它们算一起），再顺着 `model` 连线找到加载器。以前取「JSON 里第一个
  `ckpt_name`」，于是 1,950 张图的模型是别的分支的；`UpscaleModelLoader` / SeedVR 这类放大模型也不再
  被当成出图模型（纯放大出来的图模型留空）
- **文件名只用来定位是哪个保存点写的，模型永远来自那条流水线里的加载器**：前缀改名、多个前缀只是
  开头相同（`anima` vs `animaMIX`）、同一个前缀挂在两条不同模型的流水线上时，认不出就三个字段一起
  留空，绝不拿第一条分支顶上。整张图只有一种模型时，文件名对不上也照样能认
- 都没有时退化为「全图找文本」，再按节点类型猜正负
- 正面里内嵌 `Negative prompt:` 分隔符时自动切开

旧库迁移时实测（4,820 张，含从旧库还原的 131 张已删除）：

| | |
| --- | --- |
| 有正向提示词 | 4,601（94.0%） |
| 有负向提示词 | 3,345 |
| 能拿到步数 | 4,632 |
| 能拿到种子 | 4,814 |
| 命中角色 | 1,632 张 / 191 种 |
| 命中画师 | 689 张 |

「负面提示词和正面一模一样」这件事一开始报了 1,719 张，**大部分是解析器的锅**：
很多工作流根本不给负面，而是把正面的 `CLIPTextEncode` 接进 `ConditioningZeroOut` 再喂给
采样器的 negative 口（等于「不要负面词」）。顺着 ZeroOut 往上走，就会把正面文本当成负面，
于是误报「正负相同」。修掉之后只剩 1 张是真的重复。

两个踩过的坑：

1. **JSON 里会出现 JS 的 `NaN`**。ComfyUI 有些节点把 `"is_changed": NaN` 写进 prompt 段，
   这不是合法 JSON。Python 的 `json.loads` 默认容忍，`System.Text.Json` 不容忍，
   一个 `NaN` 就让整份解析失败——实测 1,192 张图因此完全没提示词。现在解析失败后会先清洗
   `NaN` / `Infinity` 再试一次。
2. **数字不能被统一提升成 double**。C# 里 `cond ? someLong : someDouble` 的公共类型是 `double`，
   所以从 JSON 取 `steps` 时写成三元表达式，拿到的永远是 double，`is long` 判断失败——
   1,200 张图的步数种子全是 null。

裸名（`solo`、`blue_eyes` 这种）进**候选池**，标了建议的正式 tag，可以逐条或批量确认/忽略，
确认后写进本地别名表，以后同一写法自动命中。「重新识别全库」会重打一遍标，不用重扫文件。

## 词表

`Assets/` 下四张表，加载在 `Core/Text/Vocabulary.cs`（后台线程，7 MB 文本约 30 ms）：

| 文件 | 内容 | 规模 |
| --- | --- | --- |
| `characters.tsv` | `tag<TAB>名字` | 233,236 |
| `artists.txt` | 每行一个画师 tag | 387,187 |
| `general_tags.txt` | 通用 tag（`white_dress` / `smile` / `solo`） | 19,304 |
| `copyright_tags.txt` | 作品 tag（`honkai:_star_rail` / `fate`） | 25,979 |

`tools/build-vocab.py` 从 `_data/` 重建这四张表（`characters.csv` / `artists.csv` 是旧图库逆向导出的
Danbooru 全量，`danbooru_tags.csv` 是 DominikDoom/a1111-sd-webui-tagcomplete 的快照）。
**safebooru / danbooru 的 API 直连被 Cloudflare 挡了**（`Just a moment...`，脚本拿不到 403 之外的东西），
所以走的是 GitHub 上维护的快照。

三个规则，都是踩过坑的：

1. **通用词先挡**：角色表里有一堆「裸名也像普通词」的条目（`emerald` / `coral` / `plum` / `fedora` /
   `jungle` / `train`）。通用词表只有 530 条手写词时，库里 40 条待确认候选有 27 条是这种噪声；
   扩到 1.9 万条后候选池降到 10 条，剩下的确实是「真有同名角色」的（`jinx`、`tinker_bell`、`joy`）。
2. **`角色_(形态)_(作品)` 三段式**：`firefly_(spring_missive)_(honkai:_star_rail)` 光靠剥后缀
   只能剥到 `firefly`，认不出来。拿版权表认出最后一段是作品，把作品接回去就是
   `firefly_(honkai:_star_rail)`。实测 122 个 token / 98 张图靠这条才命中。
3. **作品标签不是角色**：`kirby_(series)` 剥后缀会剥成 `kirby`，于是「星之卡比这个作品」
   被当成角色 Kirby。版权表命中就直接判空。

画师还有个格式坑：有的工作流写 `artist:[a|b|c]`（NovelAI 语法），方括号和竖线会把名字糊成一坨，
一个画师都认不出来。`GraphParser.ExpandArtistWrapper` 把它拆成多个名字——库里 114 张这样的图，
现在 111 张能认出画师（每张 5–8 位）。

整库重打标会**重建**待确认候选（`ImageStore.PruneCandidates`）：只清 `state=0`，
用户确认/忽略过的记录一条不动。不然词表变准了，旧噪声还会赖在池子里。

## 「我的栏」是布尔单元，不是筛选开关

每一栏有自己的名字、条件树、命中集合和缓存计数，可以有任意多个。

```
栏   = Group(AND | OR | NOT) 递归嵌套
条件 = { 字段, 算子, 值 }
```

条件字段共 22 个，覆盖内容 / 提示词 / 文件 / 我的 / 状态五组。编辑器右栏常驻「可用字段」，
点一下插进当前选中的组，下面实时给命中数和 24 张缩略图，写歪了立刻能看出来。
「快捷模板」里有高分 / 未评分 / 人物特写 · 4K / 相似堆叠 ≥2 / 有 LoRA。

字段的枚举值**只往最后追加**：条件树在库里存的是数字，插在中间会让所有老栏的字段整体串位
（差点把「评分 ≥4」变成「日期 ≥4」）。

命中数会写回 `col.count`，重启不用重算；只有条件变了或图库版本变了才重算。

## 网格与滚动

`Controls/JustifiedGridPanel.cs` 是自写的 `VirtualizingPanel`：等高布局、按宽度算行、
只实例化视口内的行。滚动时锚的是**像素位置**（顶部那行留在原处），不是图片索引——
列数一变，同一个索引会换行。

滚动条也是自己画的（`Controls/SlimThumb.cs`）：WPF 的 `Track` 按「视口 / 内容」比例算滑块，
几千张图时只剩十几像素，而且排布时会覆盖 `Thumb.MinHeight`，最小长度只能自己算。
`SlimThumb` 直接叫 `ScrollViewer.ScrollToXxxOffset`——改 `ScrollBar.Value` 并不会让
ScrollViewer 跟着走。

缩略图 4 档（小/中/大/特大），按屏幕实际像素解码；换档不会因为缓存变糊。

## 浏览历史（鼠标侧键 / 返回）

侧键是**浏览器那样的历史**，不是一条写死的链子。一屏状态记成 `MainWindow.NavStep`：统计页开没开、
人物页开没开、查看器里是哪张、当前视图（总览 / 目录 / 我的栏 / 人物 / 已删除）、搜索词、临时筛选。
状态一变就记一笔（250 ms 内连续变化合并成一次，连续打字只算一条），后退 / 前进在栈里移动。

以前 `GoBack` 是「关查看器 → 清搜索 → 清筛选 → 回总览」的固定顺序，所以
「总览 → 图片 → 总览 → 统计 → 总览」之后按前进，回到的是那张图而不是统计页。
回放时用 `_navSuppress` 把写入的副作用挡掉，否则回放本身又会被记成新的一步。

## VSR（可选）

只用 `NVVideoEffects.dll` 那 6 个 DLL，不进仓。按下面顺序找：

1. `<程序目录>\vsr\`
2. `%LOCALAPPDATA%\LatentFolio\vsr\`
3. ComfyUI Python 里 `nvvfx\libs\`

找到就自动拷一份到第 2 个位置；都找不到时右键菜单里的 VSR 项是灰的，其它功能不受影响。
需要的文件和来源见 `tools/build-vsr-bundle.ps1`。

三个实测结论（写在 `src/LatentFolio.Vsr/VsrEngine.cs` 的注释里）：

1. selector 就是 `"VideoSuperRes"`，`QualityLevel = 19` 是 `HIGHBITRATE_ULTRA`
   （普通 `ULTRA` 会「抑制压缩伪影」，对干净的 AI 图就是涂抹感）
2. **8 位 BGRA + `NVCV_CPU_PINNED` 内存可以直接喂**，不需要走 float32 CUDA 张量
3. `NvVFX_Load` 一次要 ~2.9 s，但之后换任意尺寸直接 `Run` 就行（16–38 ms），
   所以整个进程只 Load 一次——这一条决定了 VSR 能不能做成右键即用

设置里五组模式（值来自 `nvvfx` 的 `QualityLevel` 枚举，见 `VsrEngine.QualityGroups`）：

| 模式 | 值 | 输出尺寸 |
| --- | --- | --- |
| 放大 | 1–4（LOW…ULTRA） | 2× / 4× |
| 降噪 | 8–11（DENOISE_*） | 同分辨率 |
| 锐化（去模糊） | 12–15（DEBLUR_*） | 同分辨率 |
| 高清放大 | 16–19（HIGHBITRATE_*） | 2× / 4× |
| 直播 | 21 / 23（STREAMING_*） | 2× / 4× |

换档位要重新 `Load`（模型是按档位挑的，实测 3.4–6 s），这一步**挪到下一次 `Process` 的后台线程**里做，
不然在设置里点一下卡五秒。14 个档位依次跑过，输出尺寸和校验和都不同（`--test-vsr` 会打出来）。

## 性能与内存

实测（4,762 张）：

| | |
| --- | --- |
| 冷启动到窗口可交互 | 1.6 s |
| 启动流程（含扫一遍目录） | 1.2 s |
| 刷新网格（全量） | 1–3 ms |
| 全量读库 | 43–70 ms |
| 重算人物 | 0–1 ms |
| 重建目录树 | 12–46 ms |
| 常驻内存 | 798 MB（RTX VSR 常驻占三百多兆） |

内存这块试过把 VSR 改成按需加载，能降到 393 MB，但第一次点右键要等 3 秒——**改成常驻了**，
代价换手感。真正省下来的是另外两处：网格数据源换成批量替换版（一次 Reset 代替几千次 Add 通知），
缩略图解码缓存从「按条数」改成「按字节封顶 288 MB」。

## 代码结构

```
src/LatentFolio.Core            无 UI 依赖
  Comfy/GraphParser.cs          正负面提示词识别
  Comfy/PngReader.cs            PNG tEXt 元数据（tEXt/iTXt/zTXt）
  Db/                           SQLite 连接与读写
  Filtering/                    布尔条件树求值、命中缓存、相似图分组
  Imaging/                      SkiaSharp 缩略图、dHash、磁盘缓存
  Scan/LibraryScanner.cs        扫描 / 单文件解析 / 旧库导入
  Scan/LibraryWatcher.cs        目录监视 + 去抖
  Text/Vocabulary.cs            角色 / 画师词表

src/LatentFolio.App             WPF
  Controls/JustifiedGridPanel.cs       等高虚拟化网格
  Controls/SlimThumb.cs                自绘滚动条滑块
  Controls/RatingBar.cs                5 点评分条
  Controls/SelectableText.cs           只读但可选中的文本（右栏文字用它替 TextBlock）
  Services/TrayIcon.cs                 托盘图标（WinForms 的 NotifyIcon + WPF 菜单）
  ViewModels/AppVm.cs                  全部状态与命令
  Views/                               详情栏 / 自定义栏编辑器 / 人物墙 / 标签 / 统计

src/LatentFolio.Vsr             P/Invoke NVIDIA Video Effects SDK
```

## 开发工具

```powershell
.\tools\link-release.ps1        # 编译 Release + 更新根目录快捷方式
.\tools\snap.ps1 -Name main -Args "--sort-rating --select-first"   # 抓窗口（PrintWindow，不抢前台）
.\tools\dbcheck2.py             # 看库里的解析结果
tools\vsr-ctypes.py             # 不经过 C# 直接验 VSR 通路
```

内置自检：`LatentFolio.exe --self-test` 跑一遍主要交互（单选出详情栏、滚动换批、评分/批注落库、
删 1 张、一次删 50 张再还原、开查看器、读工作流、我的栏排序、换人物头像、跑 VSR 并检查缩放没被重置、
缩略图 4 档、临时筛选、相似堆叠 ≥N、候选池确认与回滚、目录树折叠、左右两栏折叠、拖边框调宽、
详情栏开合不丢选中位置、滚动条滑块最小长度……），结果写 `%TEMP%\lf-selftest.txt`，不用鼠标、不抢前台。

其它开发参数：`--parse-one <png>`、`--test-vsr`、`--bench 50000`、`--bench-decode`、`--mem-report`、
`--show-stats`、`--show-editor`、`--show-filter`、`--show-tags`、`--show-candidates`、`--show-people`、
`--show-settings`、`--select-first`、`--open-first`、`--sort-rating`。
