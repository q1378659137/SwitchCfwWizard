# Switch CFW 配置向导

一个 .NET 8 / WPF 桌面程序：自动从 GitHub 拉取最新（含预览版）的 Switch 自制系统组件，按你的选择生成一整套可直接拷进 SD 卡的配置文件。

- **默认语言简体中文**，可在右上角切换（内置 `zh-Hans` / `en-US` / `zh-Hant`）
- **界面主题三档**：跟随系统（默认）/ 浅色 / 暗夜，也在右上角、与语言并排；切换**立刻重画**，不用重启
- **每个下载项都有独立进度条**，并显示速度、剩余时间
- **所有生成物都放在运行目录下的 `out/` 文件夹**
- 组件可任意组合，每个配置项都能自己改

---

## 一、运行环境

| 项目 | 要求 |
| --- | --- |
| 系统 | Windows 10 1809+ / Windows 11（x64） |
| 运行时 | .NET 8 Desktop Runtime |
| 网络 | 能访问 `api.github.com` 与 `github.com`（国内建议在高级设置里填代理或 **GitHub 加速镜像站**，见第六节） |


---

## 二、界面说明

```
┌──────────────────────────────────────────────────────────────────┐
│ Switch CFW 配置向导                语言：[简体中文]  主题：[跟随系统] │
├────────────────────────────────┬─────────────────────────────────┤
│ ☑ Atmosphere   v1.11.2 · 稳定版 │  引导进入系统                    │
│   [进度条]                      │   ☑ 正版系统   ☑ 真实破解   ☑ 虚拟破解│
│   ▸ 配置项（展开后可逐项修改）    │   ☑ 屏蔽真实破解序列号            │
│                                │   ☑ 屏蔽虚拟破解序列号            │
│                                │   ☑ 真实破解 90DNS ☑ 虚拟破解 90DNS│
│ ☑ Hekate       v6.5.3 · 稳定版  │   ☑ 8G 运存支持                  │
│ ☑ Ultrahand    v2.5.3 · 稳定版  │  输出与下载行为                  │
│ ☑ Sys-patch    v1.6.2.3 · 稳定版│   ☑ 把组件文件一并合并进 out      │
│                                │   ☑ 把 payload 放到对应目录       │
│                                │   ☑ 把内置的 boot.dat 放到根目录  │
│                                │   ☑ 优先下载预览版                │
│                                │   ☑ 生成后自动打包为 zip          │
├────────────────────────────────┴─────────────────────────────────┤
│ 总进度 [████████░░░░░░]                     [取消]  [开始]        │
├──────────────────────────────────────────────────────────────────┤
│ 运行日志                                                          │
└──────────────────────────────────────────────────────────────────┘
```

### 左侧：按分类分组的组件

组件区按用途分成若干**分类文件夹**（组标题 + 该组的卡片）。分类与顺序**直接来自 `ComponentCategory` 枚举的声明序**，不另存一份清单 —— 手抄的清单新增分类时必然腐烂，而腐烂的表现恰好是「界面上少一组」这种没人会注意到的事。

| 分类 | 装什么 |
| --- | --- |
| **框架** | CFW 的骨架：Atmosphere / Hekate / Ultrahand / Sys-patch |
| **固件** | 机器要跑的**系统本体**（离线固件包）—— 单开一组，因为「更新固件」是不可逆操作，不该和「装个插件」混在一起看 |
| **组件** | 工具型 homebrew（存档管理、安装器、文件管理……） |
| **后台** | 常驻内存的 sysmodule（网络服务、远程操控……） |
| **主题** | 换肤相关 |
| **底层** | RCM / payload 级工具，风险最高 |
| **Ultrahand插件** | 依附于 Ultrahand Overlay 的 `.ovl` 小工具 |
| **学习** | 与 CFW 本身无关的附加软件 |

> **空组不显示**：某个分类下一个组件都没有时，界面不会渲染出一个光秃秃的标题（那只会让人以为程序坏了）。「某个分类是不是被忘了填组件」由回归里的 `CheckComponentCategoryCoverage()` 负责报出来 —— 那才是该发现它的地方。

**框架**组当前的四个组件：

| 组件 | 来源仓库 | 下载内容 |
| --- | --- | --- |
| **Atmosphere** | [Atmosphere-NX/Atmosphere](https://github.com/Atmosphere-NX/Atmosphere/releases) | `atmosphere-*.zip` + `fusee.bin` |
| **Hekate** | [CTCaer/hekate](https://github.com/CTCaer/hekate/releases)（中文界面时改用 [easyworld/hekate](https://github.com/easyworld/hekate/releases) 的 Nyx 汉化包；勾了 8G 也照样用汉化包，只有 payload 从官方取） | `hekate_ctcaer_*.zip` + `hekate_ctcaer_*.bin`（勾选 8G 运存时自动改用官方的 `__ram8GB.bin`） |
| **Ultrahand** | [ppkantorski/Ultrahand-Overlay](https://github.com/ppkantorski/Ultrahand-Overlay/releases)、[ovl-sysmodules](https://github.com/ppkantorski/ovl-sysmodules/releases)、[nx-ovlloader](https://github.com/ppkantorski/nx-ovlloader/releases) | 三个仓库的最新文件（三者绑定，一起下载） |
| **Sys-patch** | [impeeza/sys-patch](https://github.com/impeeza/sys-patch/releases) | `sys-patch-*.zip` |

「优先下载预览版」默认开启：只要最新预览版的发布时间不早于最新稳定版，就优先取预览版。

**固件**组（1 个，离线固件）：

| 组件 | 来源仓库 | 下载文件 → 落点 |
| --- | --- | --- |
| **离线固件** | [THZoria/NX_Firmware](https://github.com/THZoria/NX_Firmware/releases) | `Firmware.x.x.x.zip`（版本占位模式）→ `out/Firmware/<下载文件名去扩展名>/` |

- **默认文件名是模式，不是某个具体版本号**：`x` 是版本占位（`Firmware.23.0.0.zip` → `Firmware.20.0.0.zip` 都命中）。写死一个版本号的话，上游一发新版默认值就当场作废，而用户还以为自己什么都没改过。
- **那个版本目录是「从下载文件名造出来」的**，不是包里带的：实测上游 92 个版本的资源名一律是 `Firmware.<版本>.zip`（**用点分隔**），而**包内是平的** —— 238 个 `.nca` / `.cnmt.nca` 全在压缩包根，没有顶层目录能借（见第八节第 84 条）。所以你把文件名改成 `Firmware 20.0.0.zip`，落点就跟着变成 `out/Firmware/Firmware 20.0.0/`，不会出现「文件叫 A、目录叫 B」。
- **约 340 MB**，且固件是**只在你确实要升级或修复系统时才需要**的东西，所以默认不勾（本工具所有组件默认都不勾）。
- 下载地址与文件名两个输入框在**高级设置 →「插件文件」**里（见下文），与其它插件同一套机制。

**组件**组（19 个，工具型 homebrew）：

| 组件 | 来源仓库 | 下载文件 → 落点 |
| --- | --- | --- |
| Breeze-Beta | [tomvita/Breeze-Beta](https://github.com/tomvita/Breeze-Beta/releases) | `Breeze.zip` → `out/` 根 |
| DBIPatcher | [rashevskyv/DBIPatcher](https://github.com/rashevskyv/DBIPatcher/releases) | `DBI.nro` → `switch/DBI/`；翻译包按界面语言取 `translation_zhcn/zhtw/en.bin`，落盘改名 `translation.bin` |
| EdiZon-Overlay | [zdm65477730/EdiZon-Overlay](https://github.com/zdm65477730/EdiZon-Overlay/releases) | `EdiZon.zip` → `out/` 根 |
| Goldleaf | [XorTroll/Goldleaf](https://github.com/XorTroll/Goldleaf/releases) | `Goldleaf.nro` → `switch/Goldleaf/` |
| nxdumptool | [DarkMatterCore/nxdumptool](https://github.com/DarkMatterCore/nxdumptool/releases) | `nxdt_rw_poc.nro` → `switch/nxdt_rw_poc/` |
| NX-Shell | [zdm65477730/NX-Shell](https://github.com/zdm65477730/NX-Shell/releases) | `NX-Shell.nro` → `switch/NX-Shell/` |
| ftpd | [mtheall/ftpd](https://github.com/mtheall/ftpd/releases) | `ftpd.nro` → `switch/ftpd/` |
| Haku33 | [StarDustCFW/Haku33](https://github.com/StarDustCFW/Haku33/releases) | `Haku33.nro` → `switch/Haku33/` |
| JKSV | [J-D-K/JKSV](https://github.com/J-D-K/JKSV/releases) | `JKSV.nro` → `switch/JKSV/` |
| Luna-App | [Ixaruz/Luna-App](https://github.com/Ixaruz/Luna-App/releases) | `luna.zip` → `out/` 根；另取仓库文件树的 `enctemplate.zip` → `config/luna/enctemplate/` |
| Simple Mod Alchemist | [gtiersma/Simple_Mod_Alchemist](https://github.com/gtiersma/Simple_Mod_Alchemist/releases) | `simple-mod-alchemist_x_x_x.zip` → `out/` 根 |
| sys-clk | [zdm65477730/sys-clk](https://github.com/zdm65477730/sys-clk/releases) | `sys-clk.zip` → `out/` 根 |
| SysDVR | [exelix11/SysDVR](https://github.com/exelix11/SysDVR/releases) | `SysDVR.zip` → `out/` 根 |
| ldn_mitm | [Lusamine/ldn_mitm](https://github.com/Lusamine/ldn_mitm/releases) | `ldn_mitm_FW_x.x.x.zip` → `out/` 根 |
| sphaira | [NaGaa95/sphaira](https://github.com/NaGaa95/sphaira/releases) | `sphaira.zip` → `out/` 根 |
| Fizeau | [zdm65477730/Fizeau](https://github.com/zdm65477730/Fizeau/releases) | `Fizeau.zip` → `out/` 根 |
| NX-Activity-Log | [zdm65477730/NX-Activity-Log](https://github.com/zdm65477730/NX-Activity-Log/releases) | `NX-Activity-Log.nro` → `switch/NX-Activity-Log/` |
| Switch-Firmware-Dumper | [mrdude2478/Switch-Firmware-Dumper](https://github.com/mrdude2478/Switch-Firmware-Dumper/releases) | `Firmware-Dumper.zip` → `switch/Firmware-Dumper/` |
| battery_desync_fix_nx | [CTCaer/battery_desync_fix_nx](https://github.com/CTCaer/battery_desync_fix_nx/releases) | `battery_desync_fix_vx.x.x.nro` → `switch/battery_desync_fix_nx/` |

**后台**组（5 个，常驻内存的 sysmodule）：

| 组件 | 来源仓库 | 下载文件 → 落点 |
| --- | --- | --- |
| sys-botbase | [olliz0r/sys-botbase](https://github.com/olliz0r/sys-botbase/releases) | `sys-botbasexx.zip` → `out/` 根 |
| usb-botbase | [Koi-3088/usb-botbase](https://github.com/Koi-3088/usb-botbase/releases) | `atmosphere.7z` → `out/` 根 |
| MissionControl | [ndeadly/MissionControl](https://github.com/ndeadly/MissionControl/releases) | `MissionControl-x.x.x-x-x.zip` → `out/` 根 |
| sys-con | [o0Zz/sys-con](https://github.com/o0Zz/sys-con/releases) | `sys-con-x.x.x.zip` → `out/` 根 |
| sys-ftpd | [ELY3M/sys-ftpd](https://github.com/ELY3M/sys-ftpd/releases) | `release.zip` → 剥掉包内的 `out/` 一层后铺到 `out/` 根（只取 `atmosphere` 与 `config` 两个文件夹） |

> **`sys-botbase` 与 `usb-botbase` 只能二选一**。两者装的是**同一个系统模块**（`atmosphere/contents/430000000000000B/`），同时勾选会互相覆盖 —— 卡片上会给出提示，生成后的完整性校验也会直接报错。
>
> 这个判据不是「把这两个名字写死成互斥」，而是各插件自己声明的**系统模块 ID** 之间的比较：将来再有插件撞上同一个 ID，不用改代码就会自动被认出来。

**主题**组（3 个）：

| 组件 | 来源仓库 | 下载文件 → 落点 |
| --- | --- | --- |
| NXThemes Installer | [exelix11/SwitchThemeInjector](https://github.com/exelix11/SwitchThemeInjector/releases) | `NXThemesInstaller.nro` → `switch/NXThemesInstaller/` |
| ↳ 官方补丁集 | [exelix11/theme-patches](https://github.com/exelix11/theme-patches/tree/master/systemPatches) | 仓库里 `systemPatches/` 目录下的**全部文件**（20 个 `.ips`，不含子目录）→ `themes/systemPatches/` |
| Avatool | [J-D-K/Avatool](https://github.com/J-D-K/Avatool/releases) | `Avatool.nro` → `switch/Avatool/` |
| sys-ticon | [masagrator/sys-ticon](https://github.com/masagrator/sys-ticon/releases) | `sys-ticon.zip` → `out/` 根 |

> **NXThemes Installer 与那 20 个补丁在同一个组件里**（同一个勾选框，两个文件槽）：补丁正是安装器的工作对象，拆成两个组件会让「装了安装器却没有补丁」成为可能 —— 而少了补丁，主题在较新的系统版本上会装不上。
>
> 补丁那一行走的是**目录**而不是单个文件（`SlotSource.RepoDirectory`）：先把仓库目录列出来，再把里面每个文件各下一次。这是唯一一个「一个槽 → 一批下载物」的来源类型；它在高级设置里的第二个框填的是**仓库内的目录名**（所以那一列的表头写的是「文件名 / 目录」）。列目录只认 `type == "file"`，子目录与子模块一律丢掉。
>
> ⚠️ **配额提示**：目录槽原本的文件数**等于**它要发起的下载请求数 —— 因为 `raw.githubusercontent.com` 在部分网络下不可达（本项目实测被代理挡成 502），每个文件会**自动改走 GitHub API 端点**，也就是**一个文件消耗一次 API 调用**；20 个补丁 = 20 次调用，而未登录配额只有 **60 次/小时**（第九轮 e2e 实测到第 17 个就撞上了 403）。触顶后的表现是**明确的报错**（「被 GitHub 限流（HTTP 403）。请在「高级设置」里填写 GitHub Token 后重试。」）并**拒绝生成配置**，不会产出一套缺文件的 SD 卡内容。
>
> ✅ **已修（收尾之二十一）**：目录槽现在**先试「整仓打包取回」**——一次 `codeload.github.com/<owner>/<repo>/tar.gz/HEAD` 请求拿回整棵树，在本地解开、只留目标目录下的直接子文件。**1 次请求替代 20 次，且不消耗 API 配额**（codeload 不是 API 端点）。取回失败就**静默回落**到原来的「列目录 + 逐个文件」老路，行为与以前逐字节相同 —— 只是日志会明说后果：这条路每个文件要一次请求，未登录时可能被限流。⚠️ 这条路用的是符号引用 `HEAD`，所以**不必先查默认分支**，也免疫「上游哪天把 `master` 改名成 `main`」；代价是 tarball 根目录名是**动态的**（`<repo>-HEAD/` 或 `<owner>-<repo>-<sha>/`），因此不能复用只吃常量前缀的 `ExtractPlan.StripPrefix`，而是新写了一个「剥掉第一层、不管它叫什么」的纯函数。
>
> ⚠️ `sys-ticon` 的 Release 里**同时**挂着 `sys-ticon.zip`（正式版）与 `sys-ticon-log.zip`（带日志的调试版），两者装的是同一个系统模块。逐字命中时当然拿正式版；但正式版一旦改名消失，「去版本号前缀」那级容错会把调试版选走 —— 那不是灾难（同功能，只是会打日志），但**会留下一条告警**说明「你要的是这个、我实际用的是那个」。这条行为有专门的用例钉住。

**底层**组（2 个，RCM / payload 级工具）：

| 组件 | 来源仓库 | 下载文件 → 落点 |
| --- | --- | --- |
| Lockpick_RCM | [zdm65477730/Lockpick_RCMDecScots](https://github.com/zdm65477730/Lockpick_RCMDecScots/releases) | `Lockpick_RCM.bin` → `bootloader/payloads/` |
| TegraExplorer | [zdm65477730/TegraExplorer](https://github.com/zdm65477730/TegraExplorer/releases) | `TegraExplorer.bin` → `bootloader/payloads/` |

> 这两个是**散装 `.bin` payload**，落点与 hekate 自带的 `bootloader/payloads/` 是**同一个目录** —— 装进去之后由 hekate 的「Payloads」菜单直接启动，或者用注入器从电脑推。
>
> ⚠️ 它们**刻意不标 `IsPayload`**。`IsPayload` 的语义是「散装下载的**引导** payload（`fusee.bin` / `hekate_ctcaer_*.bin`）」，带它的文件会被「把 payload 放到对应目录」这个开关**整个跳过**。对这两个来说，标上它只有一个后果：用户勾了「Lockpick_RCM」却没勾那个开关时，**静默什么都不产出**。它们是普通组件文件，用户勾了组件就该拿到文件。`CheckAssetSlots` 里有一条穷举所有文件槽的断言钉住这件事 —— 恒真与否由「有没有人标」决定，不是靠谁记得住。

**Ultrahand插件**组（6 个，Tesla 覆盖层 `.ovl`，整包铺 `out/` 根）：

| 组件 | 来源仓库 | 下载文件 |
| --- | --- | --- |
| emuiibo | [zdm65477730/emuiibo](https://github.com/zdm65477730/emuiibo/releases) | `emuiibo.zip` |
| Zing | [zdm65477730/Zing](https://github.com/zdm65477730/Zing/releases) | `Zing.zip` |
| ReverseNX-RT | [zdm65477730/ReverseNX-RT](https://github.com/zdm65477730/ReverseNX-RT/releases) | `ReverseNX-RT.zip` |
| Status-Monitor-Overlay | [zdm65477730/Status-Monitor-Overlay](https://github.com/zdm65477730/Status-Monitor-Overlay/releases) | `StatusMonitor.zip` |
| QuickNTP | [zdm65477730/QuickNTP](https://github.com/zdm65477730/QuickNTP/releases) | `QuickNTP.zip` |
| KeyX | [TOM-BadEN/KeyX](https://github.com/TOM-BadEN/KeyX/releases) | **按界面语言**：中文 → `KeyX-CN.zip`，其余 → `KeyX-EN.zip` |

> 这六个组件（共七个包 —— KeyX 一个组件两个包）都是**整包铺到 `out/` 根**——实测（2026-09-18）七个包的顶层目录**全是 SD 卡根目录名**（`atmosphere` / `switch` / `config` / `SaltySD`），没有一层多余的包装目录，所以既不需要 `ExtractPlan`、也不需要落点声明。回归里有一条断言把这个「实测结论」钉住 —— ⚠️ 但它钉的是**声明**：只有有人去改这 6 个组件的 `Extract`/`Targets` 时才会红，**上游改包它抓不到**（C# 声明一个字都不会变）。上游那半边靠 `tools/check-upstream-facts.py`（联网核对资源名是否逐字存在 / 字节数 / 包内第一层）与联网 e2e，见第八节第 70 条。
>
> ⚠️ **仓库名与文件名不一样的两处是上游的命名，不要「顺手对齐」**：`Status-Monitor-Overlay` 的包叫 `StatusMonitor.zip`（少了连字符与 `Overlay`）。改成「看起来一致」会让逐字命中失效、静默落进容错瀑布去猜。
>
> **KeyX 是唯一按语言取包的**（`ComponentCatalog.KeyXFileName`）。它与 DBI 的翻译文件**不是同一类问题**：那两个包在 Release 里**都真实存在、都可用**（实测 v1.5.6：CN 991665 字节 / EN 991658 字节，只差 7 字节），所以「取哪个」不是「名字对不上要容错」，而是**用户意图**——判据只能是界面语言。中文界面取 CN 包、非中文取 EN 包。⚠️ **两个包的实际差异只有一处**（2026-09-18 逐条目比对：9 个条目里 8 个逐字节相同，**含 `de/en/ja/zh-tw.json` 全部四个语言文件**）：唯一差异在 `switch/.overlays/ovl-KeyX.ovl` 里的 **144 个字节**，是覆盖层内嵌的**显示名**（CN 版是中文「按键助手」，EN 版是 `KeyX`）。也就是说这件事**只影响呼出菜单里那一行字**，与语言包无关（见第八节第 69 条 —— 我第一版注释把理由写成了「EN 包只有英文那一份」，那是编的）。回归里四个语言方向都验过（`zh-Hans`/`zh-Hant` → CN，`en-US`/`ja-JP` → EN）。
>
> 这一组里有**四个**包内含 sysmodule（emuiibo 的 `0100000000000352`、KeyX 的 `0100000000251020` 与 `4100000002025924`、ReverseNX-RT 的 `0000000000534C56` 即 ASCII 的 "SLV"/SaltySD），但**都不声明 `SysmoduleTitleId`**：KeyX 装**两个** title 而那个字段是单值的（写一个就是半个真话）；且按现有约定只有「冲突可预期」的才声明（组件类里的 `SysClk`/`SysDvr`/`LdnMitm` 同样是 sysmodule、同样没声明）。实测这四组 title 与已声明的五个**没有交集**，所以不声明不会漏掉任何真实冲突。

**学习**组（7 个，与 CFW 无关的附加软件）：

| 组件 | 来源仓库 | 下载文件 → 落点 |
| --- | --- | --- |
| AtmoXL-Titel-Installer | [dezem/AtmoXL-Titel-Installer](https://github.com/dezem/AtmoXL-Titel-Installer/releases) | `AtmoXL-Titel-Installer.zip` → `out/` 根 |
| Awoo-Installer | [Huntereb/Awoo-Installer](https://github.com/Huntereb/Awoo-Installer/releases) | `Awoo-Installer.zip` → `out/` 根 |
| linkalho | **按界面语言**：中文 → [SwitchScriptTW/linkalho](https://github.com/SwitchScriptTW/linkalho/releases)，其余 → [impeeza/linkalho](https://github.com/impeeza/linkalho/releases) | 中文 → `linkalho.zip`，其余 → `linkalho-x.x.x.zip`（**包名也成对变**）→ `out/` 根 |
| Switch_90DNS_tester | [meganukebmp/Switch_90DNS_tester](https://github.com/meganukebmp/Switch_90DNS_tester/releases) | `Switch_90DNS_tester.nro` → `switch/Switch_90DNS_tester/` |
| wiliwili | [xfangfang/wiliwili](https://github.com/xfangfang/wiliwili/releases) | `wiliwili-NintendoSwitch.zip`，**只取包内的 `wiliwili.nro`** → `switch/wiliwili/` |
| Moonlight-Switch | [XITRIX/Moonlight-Switch](https://github.com/XITRIX/Moonlight-Switch/releases) | `Moonlight-Switch.nro` → `switch/Moonlight-Switch/` |
| TriPlayer | [tallbl0nde/TriPlayer](https://github.com/tallbl0nde/TriPlayer/releases) | `triplayer-x.x.x.zip` → `out/` 根 |

> 落点分两类，判据是**包内结构**（2026-09-18 逐包实测，用 HTTP Range 只取 ZIP 中央目录拿到的完整条目清单）：
> ① 包内已以 SD 卡根为基准（`switch/…` 或根目录散文件）⇒ 不声明 `Extract`、不声明落点，整棵铺 `out/` 根
> （AtmoXL / Awoo / linkalho / TriPlayer）；
> ② 上游只发散装 `.nro`（90DNS / Moonlight）或只该取其中一个文件（wiliwili）⇒ 显式声明落点。
> 回归里对这两类各有一条断言钉住，并且用**实测的真实包内条目名**喂给 `ExtractPlan.Map` 验证筛选真的对。
>
> ★ **`linkalho` 是本项目第一个「按界面语言换仓库」的槽** —— 与 DBI（换文件名）、KeyX（换包）是三件不同的事。中文走汉化镜像 `SwitchScriptTW/linkalho`、其余走上游 `impeeza/linkalho`；两个仓库**都真实存在、都在发版**，所以这同样是**用户意图**不是容错。⚠️ 最要命的一点：**地址与文件名必须成对**（两个仓库的包名不同：CN 是逐字的 `linkalho.zip`、EN 是模式 `linkalho-x.x.x.zip`），只切其中一个会让名字与仓库对不上、静默落进容错瀑布「猜」一个 —— 能跑通，但日志里会留一条本不该有的记录。为此 `AssetSlot.Address` 从 `RepoSpec` 改成了 `Func<string?, RepoSpec>`（与 `FileName` 对称），两处共用同一个判据函数。
>
> ⚠️ **EN 侧刻意保留用户清单里的模式写法**（`linkalho-x.x.x.zip`），而不是实测到的 `linkalho-v2.0.2.zip` —— 用户给的是「跟着版本走的模式」，写成具体版本号会让**每次上游发版都让逐字命中失效一次**。这里有个容易混的点：**「声明」与「上游实测」是两份不同的东西**。`ComponentCatalog` 里那段「上游实测」注释记的是上游**此刻真实存在的资源名**（带具体版本号，供 `check-upstream-facts.py` 核对），而声明里写的是用户给的**模式**。把声明「具体化」成实测到的版本号，等于**拿一份会过期的信息去覆盖一份不会过期的意图**。（这条是 2026-09-18 收尾之二十四的联网 e2e 跑出来才发现的，见第八节第 74 条。）
>
> ⚠️ **`wiliwili` 是唯一「压缩包 + 剥前缀 + 只取一个文件」的槽**：实测包里是 `wiliwili/wiliwili.nro` 加 `README.md`、`安装必读.txt`，所以声明 `StripPrefix = "wiliwili"` + `KeepFileNames = ["wiliwili.nro"]`。不剥前缀会落成 `out/switch/wiliwili/wiliwili/wiliwili.nro`。
>
> ⚠️ **TriPlayer 的包里含一个 sysmodule**（title `4200000000000FFF`，与 sys-ftpd 的 `420000000000000E` 只差末两位、**不是同一个**），按现有约定不声明 `SysmoduleTitleId`；这条交集结论由 `tools/check-upstream-facts.py` 的第 ④ 项联网复核。

**插件类插件的下载地址与文件名都能改**：见「六、高级设置 → 插件文件」。

### 右侧：全局选项

**引导进入系统**（对应 `hekate_ipl.ini` 的引导项，默认三个全开）

| 选项 | 生成的引导项 | 关键键值 |
| --- | --- | --- |
| 正版系统 | `[zbxt]` | `pkg3=atmosphere/package3`、`stock=1`、`emummc_force_disable=1`、`id=zsxt` |
| 真实破解系统 | `[zspjxt]` | `pkg3=atmosphere/package3`、`emummc_force_disable=1`、`id=zspjxt`（勾了屏蔽序列号时追加 `cal0blank=1`） |
| 虚拟破解系统 | `[xnpjxt]` | `pkg3=atmosphere/package3`、`emummcforce=1`、`id=xunipjx`（同上） |

**显示名**：每一项后面都有一个「显示名」输入框，填了就用作 `hekate_ipl.ini` 里的段名（也就是 hekate 菜单上显示的名字），**留空则用固定的默认名**：`zbxt` / `zspjxt` / `xnpjxt`。

> 这三个默认名**与界面语言无关**（2026-09-18 用户要求）——三份语言包里的 `Boot.Entry.*` 取同一个值。这不只是省事：段名同时进 `hekate_ipl.ini` 与 `autoboot` 候选项，而 `autoboot` 的跨层护栏是**拿名字对齐**的；三份包若不一致，换个界面语言就会给同一张卡生成出不同段名，`autoboot=` 指向的项当场对不上。`CheckBootEntryTitles()` 会逐个键核对三份包取值一致。

界面上的 `autoboot` 候选项也用同一个名字 —— 两处共用 `ComponentCatalog.ResolveBootTitle()`，避免「界面显示 A、文件里是 B」。

**启动图**（`logopath`）：每一项还有一行「启动图」，点「选择…」挑一张图即可（选了之后旁边出现「清除」）。生成时会：

1. 把图片复制到 `out/bootloader/res/`；
2. 在该引导项里写 `logopath=bootloader/res/<文件名>` —— hekate 的 `logopath` 是**引导项级**的键，路径按 **SD 卡根目录**算，所以写的是相对路径（写本机绝对路径拷到机器上必然找不到图）；
3. 两个引导项选了**不同目录下的同名图**时，后一个自动加引导项 id 前缀（`zspjxt_logo.bmp`），否则后复制的会静默盖掉先复制的。

> 格式要求（上游 `README_BOOTLOGO.md` 原文）：启动图必须是 **32 位（ARGB）BMP** —— 经典的 24 位（RGB）BMP **不支持**；最大 720×1280，做法是先做一张横图、再**逆时针**旋转 90°。只说「BMP」是不够的：24 位 BMP 会被 hekate 当成「格式不对」而静默回退到 `bootloader/bootlogo.bmp` 或内置图。
>
> 文件不存在 / 复制失败都只是「这一项不设这张图」，不影响其它配置生成 —— 上游对 `logopath` 与 `icon` 都有回退链，缺失不是致命错误。

**图标**（`icon`）：每一项还有一行「图标」，操作方式与启动图完全相同（选择… / 清除）。区别在**用途与生效时机**：

| 键 | 作用 | 生效时机 |
| --- | --- | --- |
| `logopath` | 这一项**引导时**刷的启动图（全屏） | 选中引导项、开始引导之后 |
| `icon` | 这一项在 **Nyx 菜单里**显示的小图标 | 停留在 hekate 菜单时 |

上游模板把两者归为同一族（原文：*like logopath= key which is for bootlogo and icon= key for Nyx icon*），所以本工具让它们**共用同一套复制 / 去重 / 产物清单逻辑**（`AppendBootImage` + `BootImageKind`），而不是复制一份实现 —— 两套撞名规则必然会漂移，而漂移的后果（A 项的启动图被 B 项的图标静默盖掉）在界面上完全看不出来。

> **留空不写 `icon=` 也没关系**：Nyx 会自己按引导项名去 `bootloader/res/<段名>.bmp` 找同名 bmp，再退回内置默认图标（`icon_switch.bmp`）。所以不设图标只是「用默认的」，不是错误。

> **为什么是 `pkg3` 而不是 `fss0`**：两者功能完全相同（都从 `atmosphere/package3` 里提取 exosphere / warmboot / 核心 kips），但 hekate 官方 README 已把 `fss0` 标注为 `!Deprecated!`，所以新生成的配置一律用 `pkg3`。回归测试里有一条断言专门盯着「`kip1` 必须排在 `pkg3` 之后」—— hekate 按书写顺序解析，顺序反了会静默失效。
>
> **`id` 上限 7 个字符**：hekate 内部按 `char id[8]` 存放，要留一个 NULL 终止符（官方 README 原文 `id=IDNAME … Max 7 chars`）。所以你配置里那个 8 字符的 `xunipjxt`，实际生效的只有前 7 位 —— 本工具直接生成 7 字符版本，避免往文件里写一个「看起来对、其实被截断」的值。
>
> **`cal0blank` 与 `exosphere.ini` 的耦合**：hekate 的**引导项参数会覆盖** `exosphere.ini` 的 `blank_prodinfo_*`。只改一边就会出现「exosphere 说屏蔽了、引导项又放开了」这种自相矛盾的组合，而且完全静默。所以按「屏蔽序列号」的勾选同步写 `cal0blank=1`，两处始终一致。

> **`autoboot` 与引导项顺序是隐式耦合的**：`autoboot=N` 里的 N 由 hekate 按**文件里引导项的顺序**解释（1 = 第一个引导项，0 = 关闭），而界面候选项的顺序由 `MainViewModel.RefreshAutobootChoices()` 排、文件里的顺序由 `ConfigGenerator.BuildBootEntries()` 排 —— **两处独立**。任一处顺序变了而另一处没跟着变，就会出现「界面显示『虚拟破解系统』，机器实际进『正版系统』」，且两边各自看都没问题。
>
> 上面表格里的顺序（正版 → 真实破解 → 虚拟破解）就是这个契约，`CheckAutobootIndexMatchesGeneratedEntries()` 会拿界面候选项的名字和生成文件里第 N 个引导项的名字逐一对齐，任一处被改动都会立刻报错。**调整引导项顺序时必须两处一起改。**

**屏蔽序列号**拆成两个**互相独立**的开关，想屏蔽哪个就屏蔽哪个：

| 开关 | 可见条件 | 写入的键 |
| --- | --- | --- |
| 屏蔽真实破解序列号 | 勾了「真实破解系统」 | `exosphere.ini` → `blank_prodinfo_sysmmc` |
| 屏蔽虚拟破解序列号 | 勾了「虚拟破解系统」 | `exosphere.ini` → `blank_prodinfo_emummc` |

两者完全独立：只勾一个就只屏蔽一个，另一个照常保留原序列号。

**两个开关默认都是关闭的** —— 屏蔽序列号是「有需要才做」的动作，软件不替你决定。想屏蔽就自己勾上（勾上后 Sys-patch 会自动跟着启用，见下一节）。

**90DNS** → 在 `atmosphere/hosts/` 下生成 `default.txt`（2 行遥测屏蔽）、`sysmmc.txt`（40 行，勾选「真实破解系统 90DNS」时）、`emummc.txt`（40 行，勾选「虚拟破解系统 90DNS」时）

两个 90DNS 开关与屏蔽序列号同一套规矩：各自跟着对应的引导模式显示，也只在对应引导模式勾选时才写文件。`default.txt` 是两个系统共用的遥测屏蔽表，任一开关开着就写。

90DNS 靠 `atmosphere/hosts` 里的屏蔽表工作，而这份屏蔽表要生效必须打开 `system_settings.ini` 的 `enable_dns_mitm`，所以两者是**完全双向绑定**的（`enable_dns_mitm` ⇔ 至少一个 90DNS 勾着），共四条规则：

| 你的动作 | 结果 |
| --- | --- |
| 勾上「真实破解系统 90DNS」**或**「虚拟破解系统 90DNS」 | `enable_dns_mitm` 自动打开 |
| 两个 90DNS 都取消 | `enable_dns_mitm` 自动关掉 |
| **取消** `enable_dns_mitm` | 两个 90DNS **同时**取消（mitm 关着时 90DNS 毫无作用，留着勾只会让人以为遥测被挡住了） |
| **勾上** `enable_dns_mitm` | 自动补勾**当前可见**的 90DNS 开关（只勾了「真实破解」→ 只补勾真实那项，不去碰那个界面上隐藏的虚拟项） |

为什么第 4 条必要：只做前三条的话，「`enable_dns_mitm` 开着、两个 90DNS 都不勾」这个状态仍然**手动可达**（直接去点那个复选框就行）—— 产物会写出 `enable_dns_mitm=1` 却一份 hosts 都没有，用户以为遥测已经被挡住。补上第 4 条，这个状态在两条路径上都到不了。

**载入时也会归一化**：`settings.json` 里若留着「`enable_dns_mitm=1` 而两个 90DNS 都不勾」（老版本它的默认值就是 `1`，升级上来正是这状态），启动时会被拉回「关」—— 但**不会**反向替你补勾 90DNS（载入不是你的动作，替你写出一份没要过的 hosts 更越权）。

`enable_dns_mitm` 的**默认值是关**（2026-09-17 决定）。这是「默认值 = 你那份 ini 的取值」这条既有约定里**唯一被明确推翻**的一项，理由是**起点必须与规则自洽**：两个 90DNS 默认都不勾，若 `enable_dns_mitm` 默认为开，界面一打开就是「90DNS 全关、mitm 却开着」—— 规则说「都不勾就关」，起点说「开着」，产物照起点写。护栏：删掉 `settings.json` 后构造一次 `MainViewModel`，断言两个 90DNS 都不勾**且** `enable_dns_mitm` 为关。

### 屏蔽序列号 ↔ Sys-patch 的双向联动

这两个方向**不对称**，一个是硬约束，一个是软默认：

**① 屏蔽序列号 → 强制 Sys-patch（硬约束，会锁定）**

只要**任意一个**屏蔽开关打开（且它对应的引导模式勾着），Sys-patch 就会被自动勾上并**锁定**（复选框变灰，卡片上出现黄色说明）。

原因：屏蔽序列号会写 `exosphere.ini` 的 `blank_prodinfo_*`，把 prodinfo 里的序列号/校准数据抹掉，证书随之失效，系统会拒绝正常启动 —— 必须靠 Sys-patch 在运行期补上签名校验才走得通。所以**只要开了屏蔽，Sys-patch 就必须在**（开一个屏蔽就要一次兜底，开两个也一样）。

取消全部屏蔽开关后会**解锁**，但**不会**替你反选 Sys-patch（保留你原来的选择）。

**② Sys-patch → 默认勾上屏蔽序列号（软默认，可自行取消）**

你**手动**勾上 Sys-patch 时，两个屏蔽序列号会**默认勾上**（只勾当前可见的那几项），省得再点两次。这只是默认值 —— **想取消随时可以取消**，取消后也不会被强行勾回来。

> 为什么方向①需要「重入保护」：程序自动勾 Sys-patch 时，如果不去区分「谁勾的」，就会触发方向②，把两个屏蔽序列号一起勾上 —— 结果变成「勾一个等于两个都勾」，需求①的独立性就废了。实现上用 `_applyingForcedSelections` 标志位区分「程序勾的」和「用户勾的」。

> 为什么方向①的锁定需要叠加可见性条件：屏蔽序列号的复选框只在勾了「真实破解 / 虚拟破解」时才可见。如果两个破解引导都不勾，屏蔽序列号会被隐藏，此时程序**不会再锁死** Sys-patch —— 否则用户会面对一个「能解锁的那个勾已经藏起来了、Sys-patch 却被锁死」的死局。

> 想反过来取消 Sys-patch？先把两个屏蔽序列号都取消，锁定即解除。这是需求②的直接推论：屏蔽项还开着就说明确实需要 Sys-patch 兜底。

这两条规则（含独立性、连锁回归、隐藏时不死锁、可取消）在 `CheckSysPatchForcing()` 里被 40 项断言完整覆盖，配置层的独立性另由 `CheckBlankSerialIndependence()` 的 10 项断言验证。

**8G 运存支持**：同时作用于三处
1. `exosphere.ini` → `enable_mem_mode=1`
2. 每个破解引导项 → `memmode=1`
3. Hekate payload 自动改选 `__ram8GB.bin`

**输出与下载行为**（四张卡片里最后一张）

| 选项 | 默认 | 作用 |
| --- | --- | --- |
| 配置文件 + 组件文件 | 开 | 把解压好的组件本体合并进 `out/` |
| 把 payload 放到对应目录 | **开** | `fusee.bin` → `bootloader/payloads/`；hekate payload → 根目录 `payload.bin` + `bootloader/update.bin` |
| 优先下载预览版 | 开 | 仓库有更新的 Pre-release 时优先使用 |
| 生成后自动打包为 zip | **关** | 勾上才在生成完成后自动产出可直接解压进 SD 卡的压缩包（打包是收尾动作，默认不替你做） |

### 默认值：打开即对齐你实际在用的配置

界面上所有**默认值**都照你给的配置文件取值 —— 勾上组件后生成的 ini 就是你要的状态，不用逐个重设。

> ⚠️ **一个例外：组件勾选默认「一个都不勾」**（2026-09-16 起）。首次运行打开软件时组件全是未勾选状态，
> 由你自己挑要装什么 —— 此时「开始下载并生成配置」按钮是灰的，组件列表上方会显示一句
> 「请至少勾选一个组件」，勾上任意一个即可开始。
>
> 但**每个组件内部的选项仍然按下面的默认值填好**，勾上就能直接用 ——
> 「不勾组件」和「选项没有默认值」是两件事，别混淆。

| 你的配置文件 | 已经做成默认值的项 |
| --- | --- |
| `atmosphere/config/override_config.ini` | `[hbl_config]` 段（`program_id`、`override_any_app`、`path`、`override_key`、`override_any_app_key`）与 `[default_config]` 段的 `override_key`、`cheat_enable_key` **十个键全部可配置**，默认值就是你的取值（`010000000000100D` / `true` / `atmosphere/hbl.nsp` / `!R` / `R`，以及两个 `!L`）。注意两段都有 `override_key`，界面上它们各自独立 |
| `atmosphere/config/system_settings.ini` | `usb30_force_enabled=1`、`dmnt_cheats_enabled_by_default=0`、`dmnt_always_save_cheat_toggles=1`、`enable_external_bluetooth_db=1`；并新增 7 个选项：`enable_sd_card_logging`、`sd_card_log_output_directory`、`enable_hbl_bis_write`、`enable_hbl_cal_read`、`fsmitm_redirect_saves_to_sd`、`enable_deprecated_hid_mitm`、`enable_dns_mitm_debug_log`（取值全部照你的配置） |
| `bootloader/hekate_ipl.ini` | `[config]` 段 `bootwait=2`、`autohosoff=2`、`autonogc=0`、`updater2p=1`、`autoboot=0`、`autoboot_list=0`、`backlight=100`；三个引导项用 `pkg3`，`id` 分别为 `zsxt` / `zspjxt` / `xunipjx` |
| `config/ovl-sysmodules/config.ini` | **新增整个文件**（此前根本没生成过）：`powerControlEnabled=1`、`wifiControlEnabled=1`、`sysmodulesControlEnabled=1`、`bootFileControlEnabled=0`、`hekateRestartControlEnabled=0`、`consoleRegionControlEnabled=0` |
| `config/ultrahand/config.ini` | `key_combo=L+DDOWN` |
| `exosphere.ini` | `debugmode=1`、`debugmode_user=0`、`disable_user_exception_handlers=0`、`enable_user_pmu_access=0`、`enable_mem_mode=0`、`allow_writing_to_cal_sysmmc=0`、`log_port=0`、`log_baud_rate=115200`、`log_inverted=0`（`blank_prodinfo_*` 跟着「屏蔽序列号」走，**该开关默认关闭** → 默认写 `0`） |
| 根目录 `hekate_ctcaer_x.x.x.bin` | 不勾 8G 时保留标准版；勾了 8G 时删掉标准版、只留 `__ram8GB.bin`（见第三节） |

**两个副作用需要你知道**：

1. **屏蔽序列号默认关闭**，所以打开软件时 Sys-patch 只是「可自由勾选、随时可取消」的普通组件（首次运行**不会**替你把任何组件勾上），**不会**被锁定。等你勾上任意一个屏蔽项，Sys-patch 才会被强制启用并锁定（见上文「屏蔽序列号 ↔ Sys-patch 的双向联动」）。
2. **组件配置项多了 15 个**（总数 55 → 70），全部集中在右侧「组件配置」卡片里，仍然每个都能自己改 —— 默认值只是起点，不是写死的。

> 这一整套默认值由回归测试的 `CheckUserConfigBaseline()`（73 项断言）逐条钉死：任何一处被改回上游默认值都会立刻报错，不会等你发现「生成的 ini 又变回去了」。
> 「勾上屏蔽序列号时写 `1`、取消勾选时写回 `0`」两个方向都有断言 —— 只验「勾上写 1」的话，取消勾选后文件里还留着上一次的 `1` 就没人发现了。

### 顶部：界面语言与主题

窗口右上角并排两个下拉框：**语言**与**主题**。主题三档 —— **跟随系统**（默认）/ 浅色 / 暗夜。
切换**立刻重画界面**：既不重启，也不重建任何联网客户端（它和 Token / 代理那一类设置无关）。

同一行最右边是**版本号**与**「检查更新」按钮**（2026-09-21 加）：点一下会去查最新发布、与当前版本比较，
有新版就下载、启动它、再退出旧版 —— 结论就地显示在按钮右边，**下载时结论后面还跟一个百分比**
（2026-09-22 加）；详情进日志。见第六节末「检查更新」。

**默认「跟随系统」**（2026-09-20 用户明确要求）。它读 Windows 的「应用模式」
（注册表 `AppsUseLightTheme`），并在你改系统设置时**实时跟随**（订阅 `SystemEvents.UserPreferenceChanged`）。

> ⚠️ **升级说明**：老存档里没有这个字段 ⇒ 按「跟随系统」处理。也就是说**系统本来就是深色的老用户**，
> 升级后界面会第一次变暗 —— 这是**有意**的行为（跟随你已经表达过的系统偏好），不是 bug。
> 想固定成浅色，在下拉框里选「浅色」即可（随后会写回 `settings.json`）。

**暗夜用的是「近纯黑」**，两个取舍值得一提（都不是随手挑的）：

- 底色近纯黑（`#0B0B0D`），但**主文本不是纯白**（`#EDEEF1`）—— 纯黑底 + 纯白字在暗环境里对比过硬，长时间盯反而累眼。
- **强调色与状态色整体提亮**（`#2B6CF6` → `#5B93FF`，绿 / 红同理）：浅色底上够用的那几个色，挪到近黑底上会「发闷」到看不清。

**滚动条也重画了**（2026-09-20 补做）。它比别的控件更需要小心，因为「画错了」会**弄坏功能**：
拖动与点击翻页由框架的 `Track` 负责，而它按**名字**找 `PART_Track`（还有 `Track.Thumb` /
两个 `Track.*RepeatButton`）—— 名字写错或结构缺失，滚动条就拖不动，而「用鼠标拖一下」这件事离线验不了。
所以：模板**只替换视觉**、拖动完全交给框架的 `Track`；两个翻页 `RepeatButton` 用 `Opacity="0"`
而**不是** `Visibility="Collapsed"`（前者保留命中区域，点轨道空白处仍能翻页）；
`--selftest` 里加了一条结构断言 —— 每个 ScrollBar 的模板里必须真的找得到 `PART_Track` 且 `Thumb` 非空
（实测 **236 个滚动条**全部通过）。

⚠️ **重画模板最大的坑是「漏了默认模板里的属性传递」** —— 本项目踩了两次，都靠用户实测才发现：
ComboBox 漏 `ContentTemplateSelector`（⇒ 关闭态显示类型名）、Expander 漏 `Foreground`（⇒ 标题退回系统黑）。
共同特征：**展开 / 内容区正常、只是某一处不对**，因为两条路不同源。
所以 `--selftest` 里补了一条**通用**体检：暗夜主题下扫过**全部文字的前景色**，
凡「接近黑」的直接报出（实测 **749 个文字、0 个过暗**）—— 这类退化为系统色的错误从此自动现形。

**系统对话框（MessageBox、文件选择器）仍是系统外观 —— 这一处做不到，说明原因**：
它们走的是 Win32 / COM，外观由**系统**的「应用模式」决定，WPF 的资源字典**碰不到**它们。
唯一的 API 途径（`uxtheme` 里未文档化的 `SetPreferredAppMode`）只能让它们跟随**系统**深色，
并不能跟随应用里的选择；而强制深色（`ForceDark`）会污染整个进程，且属未文档化行为。

> 所以想要「对话框也是深色」，现成的路径是：**应用选「跟随系统」（默认就是它）+ 把 Windows 设成深色** ——
> 这样界面与系统对话框**同时**都是深色。这不是绕路：这类对话框的所有者本来就是系统。

⚠️ 顺带说明为什么**不**自绘替代：项目里 `MessageBox` 只有 2 处、文件选择器 1 处，看上去「代价不大」——
但那 2 处正好是**启动失败**与**未处理异常**，而那是**最不该**依赖我们自己的资源与 Dispatcher 的场景
（系统对话框最大的价值就是「崩溃时也一定显示得出来」）；文件选择器要自绘，等于自己实现一整套文件浏览 UI。

ComboBox / CheckBox / Expander 这三个也是**重画过**的：它们的默认模板（Aero2）用系统画刷、不会跟着资源键走，
不重画的话在暗夜下会保持浅色（而顶部下拉框、15 个勾选框、组件卡片里的展开器全在最显眼处）。

### 窗口图标与底部声明

- **图标**：窗口标题栏 / 任务栏 / exe 文件三处用的是同一个 `ico.ico`（仓库根目录，16/32/48/64 四种规格，32 位带 alpha）。exe 那一枚由 csproj 的 `<ApplicationIcon>` 嵌入，窗口那一枚由 `<Resource Link="ico.ico">` + XAML 的 `Icon="/ico.ico"` 取用 —— **改图标只需换掉那一个文件**，不必动代码。
- **底部声明**：窗口最下面一行固定显示「本软件免费分享，请勿用于商业用途。by：念若安止」。文案走语言包的 `App.Notice` 键，**三份语言包取同一句中文** —— 作者署名与授权声明不随界面语言改写，所以「三份一样」是刻意为之，不是漏翻译。

### 日志文件：出了事能查

界面上那份「运行日志」一关窗就没了，而排查问题时最需要的恰恰是「上次跑的时候我点了什么、它写了什么」。所以**每一次运行都会写一份完整日志到文件**：

```
<exe 所在目录>/logs/SwitchCfwWizard-2026-09-18.log
```

界面**右侧「输出与下载行为」卡片**里的「打开日志目录」按钮会直接打开它 —— 上面那行小字写着**当天那个文件的完整路径**（以及「按天分文件、只留最近 10 份」的约定），不必自己翻目录。

**文件里有什么**：

| 内容 | 例子 |
| --- | --- |
| **会话头** | 程序版本、运行库版本、操作系统；工作目录、设置文件、日志文件三个路径；代理是否已设置；镜像站地址（填了的话，记的是**规范化之后**的那个，能看出程序是怎么理解你填的） |
| **每一次按钮点击** | `21:49:11.259 [信息] 操作 · 点击「高级设置」`；引导项那 12 个按钮会带上「哪一行」——`点击「正版系统 · 启动图 · 选择…」` |
| **每一处文字 / 勾选 / 下拉的改动** | `21:49:12.101 [信息] 操作 · 设置变更：BootStockTitle (空) → zbxt；组件「Hekate」的勾选 False → True` |
| **整条主流程** | 与界面日志逐条相同：查版本、挑资源、每条下载与重试、解压、合并、校验、生成配置（每条都带毫秒时戳） |
| **崩溃** | 未处理异常按行写进同一份日志（同时仍写一份 `crash.log`），多行堆栈不串行 |
| **会话尾** | `===== 本次运行结束（警告 3 条、错误 0 条）=====` |

**几个刻意的取舍**：

- **机密永不入日志**。`GitHubToken`、`Proxy` 这类键只记「已设置 / 已清空」，不记内容 —— 日志是要发给别人看的，里面带着一串能用的口令是收不回来的事。判定按**键名包含** `Token`/`Proxy`/`Password`/`Secret`，所以将来多出 `GitHubTokenBackup` 之类也会被挡住（回归里有一条「注入一个假 Token，断言它在整个日志里一次都不出现」的断言）。
- **按天分文件、只留最近 10 份**，长期使用不会把磁盘吃满；清理**只删自己前缀的文件**，用户手动放进 `logs/` 的东西一律不碰。
- **写不进去就停**（目录只读、磁盘满）：只在第一次把原因回显到界面日志，不会把日志系统本身变成卡顿源，也不会让程序因为写日志失败而崩。
- 连**按钮点击**都要记，是因为「用户说他点了开始，日志里却什么都没有」这种情况只能靠它分辨：是按钮没点上，还是点上之后流程没走。

> ⚠️ 发布流水线会**清掉**这个目录：`tools/build.sh` 的 `scrub_traces` 把 `<exe>/logs` 一并挪走，`refresh-dist.py` 又把 `logs` 列为「不该进发布包」的项复查一遍。用户第一次打开发布版时不该看到上一个跑过的人的日志（`logs/` 从第一版起就在这份拦截清单里，这一轮的改动只是让它真的存在了）。

### 设置会自动落盘（没有「保存设置」按钮）

打开软件之后，**界面上的每一次改动都会自动写进 `settings.json`**，下次启动原样读回来：组件勾选、每个组件里的配置项、全局开关、三个引导项的显示名与启动图、下载源、插件文件槽、镜像站、界面语言、**界面主题**、Token / 超时 / 代理……全部在内。

**没有「保存设置」按钮**（2026-09-19 按用户要求取消）：改完就存，不用再想「我改了没点它，到底存没存」—— 这个问题本身就是那个按钮带来的。

- 实现方式是**在通知那一层接线**（`MainViewModel` 的属性变更 + 各子视图模型的值变更），不是在几十个 setter 里各手写一次保存 —— 手写块漏一处就是「改了没存」，而且漏了不报错。
- 写盘有 **400ms 节流**：拖一次文本框、连点几个勾选框只会写一次文件；关窗前还会**补写一次**，所以「改完顺手关窗」也不会丢。「立即保存」指的就是这 400ms —— 再短就变成每敲一个键写一次文件，那不是更保险，只是更吵。真正保证不丢的是关窗那一笔。
- **只在内容真的变了时才写**：触发自动落盘的不止用户操作（子视图模型刷新本地化文本也发通知），不判断的话**每次打开软件都会把 `settings.json` 按一模一样的内容重写一遍** —— 文件时间戳从此不能用来判断「这个值是谁写的」。判据是「快照有没有变」，不是「有没有收到通知」。
- **打开软件不会写存档**：首次运行时 `settings.json` 根本不会被创建，直到你改了什么。
- 「改完之后什么时候才生效」：Token / 超时 / 代理 / **镜像站**这四项一变就立即重建联网客户端；**主题**一变立即重画界面（不走联网那一套）；其余选项在生成时读取，本来就不需要重启。
- 填了**不合法**的下载源 / 插件地址仍会逐条记进日志，但**同一个值只记一次**：取消按钮之后，这条告警从「按一次报一次」挪进了会反复结算的自动落盘，不去重就会边打字边刷屏，真正要看的那条被淹掉。「值」换了就再报一次 —— 判据是「这个值报过没有」，不是「报过没有」。
- 护栏：`CheckAutoSaveWiring()` 钉住接线本身（跳过表不许吞掉真正的设置项、关窗补写必须存在、**「保存设置」按钮不许回来**……），`CheckAutoSaveBehaviour()` 用真实消息泵跑一遍「改完不点保存 → 文件里真的有新值」；`DispatcherTimer` 只在消息泵里才 Tick，所以这一条必须显式推 `DispatcherFrame` 才测得出来。

---

## 三、生成的配置文件

全部输出到 **`<exe 所在目录>/out/`**。

| 文件 | 生成条件 |
| --- | --- |
| `exosphere.ini` | 勾选 Atmosphere |
| `atmosphere/config/system_settings.ini` | 勾选 Atmosphere |
| `atmosphere/config/stratosphere.ini` | 勾选 Atmosphere |
| `atmosphere/config/override_config.ini` | 勾选 Atmosphere |
| `atmosphere/hosts/default.txt` | 勾选 Atmosphere + 任一 90DNS |
| `atmosphere/hosts/sysmmc.txt` | 上面 + 勾选「真实破解系统 90DNS」 |
| `atmosphere/hosts/emummc.txt` | 上面 + 勾选「虚拟破解系统 90DNS」 |
| `bootloader/hekate_ipl.ini` | 勾选 Hekate + 至少一个引导项 |
| `bootloader/nyx.ini` | 勾选 Hekate |
| `config/sys-patch/config.ini` | 勾选 Sys-patch |
| `config/ultrahand/config.ini` | 勾选 Ultrahand |
| `config/ovl-sysmodules/config.ini` | 勾选 Ultrahand（ovl-sysmodules overlay 自己的开关） |
| `bootloader/payloads/fusee.bin` | 勾选 Atmosphere + 「把 payload 放到对应目录」 |
| `payload.bin` | 勾选 Hekate + 上面那个开关（勾了 8G 运存时是 8G 版 payload） |
| `bootloader/update.bin` | 同上，与 `payload.bin` 是**同一份** payload |
| `bootloader/res/<你的图>` | 某个引导项选了启动图或图标（见第二节「启动图」/「图标」）；两者共用这个目录，所以**跨项撞名时会加引导项 id 前缀**（`zspjxt_` 之类） |
| `boot.dat` | 「把内置的 boot.dat 放到根目录」（默认开启，不联网） |

### payload 放到对应目录

`fusee.bin`（Atmosphere）和 `hekate_ctcaer_*.bin`（Hekate）是**散装下载**的，不随 zip 解压出来，所以「把组件文件合并进 out」覆盖不到它们。默认开启的「把 payload 放到对应目录」按上游约定摆放：

| 来源 | 落点 | 为什么是这个位置 |
| --- | --- | --- |
| Atmosphere `fusee.bin` | `bootloader/payloads/fusee.bin` | hekate 的 payload 菜单就是从这个目录读可选 payload |
| Hekate payload（界面上选中的那个） | `payload.bin` **和** `bootloader/update.bin` | 前者是 RCM 注入 / 链式加载直接认的文件名；后者是 hekate 自我更新认的文件名，缺一不可 |
| 8G 模式下额外保留的标准版 payload | `bootloader/payloads/` | 不盖掉正在用的 `payload.bin`，又能在 hekate 的 payload 菜单里直接选中启动 |

- **8G 运存模式下会清掉 out 根目录里的标准版 payload**：它来自 hekate 组件包**自带**的副本
  （包里同时有 `bootloader/update.bin` 和根目录的 `hekate_ctcaer_<版本>.bin`，整棵铺开就落到了根目录）。
  名字跟正在用的 8G 版只差一个 `__ram8GB` 后缀，肉眼极难分辨，留着只会让人搞不清机器到底按哪个起 —— 所以删掉。
  标准版本身仍保留在 `bootloader/payloads/`，随时能在 hekate 的 payload 菜单里切回去。
  **4G 模式下则相反**：根目录那份就是正在用的，必须保留。
- **独立于**「把组件文件合并进 out」开关——即使不合并组件本体，payload 照样会摆放
- 摆放后同样写进 `.wizard-manifest.json`，打包时会被完整性校验一起比对
- 校验时按「文件到底在不在它该在的地方」判断，而不是数「复制了几个」——放错目录也会被抓到

> 顺序上有个坑：hekate 的发布包里**自带**一份 `bootloader/update.bin`（标准 4G 版）。payload 的摆放必须排在组件合并**之后**，否则包里的那份会把界面上选中的 8G 版盖掉 —— 界面显示 8G、机器实际按 4G 起，而且完全静默。这条顺序由回归测试盯着。

这样 `out/` 既能用于 SD 卡根目录，也能直接从电脑端做 RCM 注入。

#### `bootloader/payloads/` 里的 bin 需要配 ini 才能启动吗？

**手动启动不需要，自动启动必须写 ini。**

hekate 官方 README 对 `bootloader/payloads/` 的说明原文是：

> For the 'Payloads' menu. All CFW bootloaders, tools, Linux payloads are supported. **Autoboot only supported by including them into an ini.**

拆开看：

- **手动启动**：进了 hekate 主界面 → 点 `Payloads` → 菜单会直接列出 `bootloader/payloads/` 下的每一个 `.bin`，选中即启动。这条路径**完全不需要任何 ini 文件** —— 目录本身就是一个菜单。
- **自动启动**：`autoboot` 只认 ini 里声明过的条目。要让某个 payload 能被 autoboot 选中，必须把它写进 `bootloader/ini/` 下的某个 ini（键名是 `payload=<bin 文件名>`），或者直接写进 `hekate_ipl.ini`。

顺带把两个目录的职责分清楚 —— 它们不是一回事：

| 目录 | 对应菜单 | 能不能被 `autoboot` 选中 |
| --- | --- | --- |
| `bootloader/payloads/` | hekate 主界面的 **Payloads** | 不能（除非另写 ini 引用它） |
| `bootloader/ini/` | hekate 主界面的 **More configs** | **能** |

`autoboot_list` 就是选从哪儿读列表：`0`（默认）= 从 `hekate_ipl.ini` 读；`1` = 从 `bootloader/ini/` 按 **ASCII 顺序**读。

> 本工具当前**不生成** `bootloader/ini/` 下的文件 —— 你的配置里也没有，`autoboot_list=0` 说明你是从 `hekate_ipl.ini` 读的。如果以后想让 `bootloader/payloads/` 里的某个 payload 参与自动启动，说一声，我加一个「写进 `bootloader/ini/`」的开关。

### boot.dat：改为下载（已取消内置）

`boot.dat`（SX GEAR 的引导文件）**不再随程序内置**（2026-09-21 用户要求改为从仓库下载）。
它现在是一个普通的下载项：勾选框在左侧**「框架」分组**里（原先在右侧「输出与下载行为」，已按要求搬过去），
标签就是 **`boot.dat`**，**默认不勾选**；勾上之后点「开始下载并生成配置」，它会先按地址被下载回来，
再写到 `out/boot.dat`。

- **下载进度就显示在它的卡片上**（进度条 + 状态文字，与组件卡片里的资源行同一套视觉）。
  这一行**始终显示**（2026-09-22 用户要求）：没勾 / 还没跑过时进度条是 0%、状态文字写「未勾选」或「就绪」
  （「就绪」= `Common.Ready`，与组件卡片同一句文案 —— 是「勾好了、只等开始」而不是「在等」），
  而不是整行消失。⚠️ 因此那行文字由 `BootDatStatusText` 保证**永不为空** ——
  一条 0% 的进度条配上一行空白，看上去就像卡片坏了。
- **这张卡片与组件卡片逐项对齐**（2026-09-22 用户要求，以 `Sys-patch` 那张为准）：
  勾选框不再自带文字，标题 14 / SemiBold、说明用 `CaptionText`、状态文字放右上角用 `HintText`
  —— 与 `ComponentTemplate` 的头部一字不差，于是它看起来就是同一组里的一张卡片，
  而不是「一张长得像卡片的别的东西」。`--selftest` 里直接拿**运行时的 Sys-patch 卡片当参照物**
  逐项比字号 / 字重 / 前景色（`card-parity`），比「我写了 14/SemiBold」这种话硬。

- **默认地址**（用户给的那条）：`https://github.com/q1378659137/SwitchCfwWizard/blob/main/boot.dat`
  —— ⚠️ 它是 GitHub 的**网页形态**：浏览器打开是文件预览页，但**下下来的字节是 HTML**。
  所以程序会把它解析成真正取字节的 **raw 形态**
  （`raw.githubusercontent.com/q1378659137/SwitchCfwWizard/main/boot.dat`），
  规则在 `Services/BootDatSource.cs`，回归里穷举钉住（含幂等、子路径、坏值拒绝）。
- 地址可在**高级设置 → boot.dat 下载地址**里改；留空就用默认那条。
  `blob` 形态、`/raw/` 形态、raw 主机原样形态**三种都认**（都落到同一条 raw 地址上）。
- **两条通道**：主通道是 raw 主机；它把重试次数用光后自动改走 `api.github.com` 的 contents 端点
  （raw 域名在部分网络下会被整段阻断，而 api 常常仍然通）。两条都会被镜像站改写。
- **下载落到 `download/boot/boot.dat`**（`AppPaths.BootDatRoot`）—— 与各组件的
  `download/<组件>/` 同一个形状，一眼能看出这个文件是谁下的。它只是缓存，真正要用的那份在 `out/` 根目录。
- **拿不到就跳过**（警告 + 日志写明是「地址解析不出」还是「下载失败」），不中止整轮 ——
  它是可选项，为它放弃其余组件的产物不划算。但**不会**留一个 0 字节的 `boot.dat`：
  那会把「少了文件」变成「文件是坏的」，SD 卡启动时才失败，用户根本找不到是哪个文件的问题。
- 写二进制走的是**字节复制**而不是文本读写 —— 文本读写会按 CRLF / 编码归一化，二进制内容当场损坏，
  而日志里一点异常都看不出来。
- 取消勾选则不会写出；上一次生成的那份会被清单清理一并带走。

### 检查更新（下载新版 → 启动它 → 退出旧版）

窗口右上角的**版本号**旁边有一个「检查更新」按钮（2026-09-21 加）。点一下依次做五件事：

1. 从**高级设置 → 检查更新的地址**（留空 = 项目仓库的 releases 页）推出仓库
   —— ⚠️ 填的地址认不出来时**回落到默认地址**（2026-09-22 用户要求，与「下载源」同一约定），
   界面与日志各说一次，而不是把这次检查作废 ——
   查它的最新**稳定版**发布 —— 刻意**不跟随**「优先预览版」那个开关：那个开关是给组件用的，
   而把整套程序换成预览版是另一回事，用户按的是「检查更新」，期望拿到正式版；
2. 把 tag 与当前程序版本比较（**补零到四段再比**：程序集版本是 `0.1.0.0`、tag 常是 `0.1.0`，
   直接 `Version.CompareTo` 会认为 `0.1.0 < 0.1.0.0`，于是「同一个版本」被判成「有新版本」，每次点都提示更新）；
3. 有新版就找 Release 里那个 `SwitchCfwWizard.exe`，下载成 **`SwitchCfwWizard_<版本号>.exe`**
   （放在当前 exe 同一个目录）。**名字必须带版本号**：与旧版同名的话，新包会去覆盖**正在运行**的那个 exe，
   Windows 上必然失败，而异常信息完全指不出这一点。
   **下载过程中，结论后面会跟一个百分比**（2026-09-22 用户要求）—— 它只在**真的在下载**那段时间出现：
   查完发现已是最新版却留一个「0%」，会让人以为它还在下。百分比**不写日志**：一个几 MB 的包会触发几十次回调，
   逐条记只会把日志淹掉（日志里仍是「开始下载 → 已保存到 …」两条）；
4. 校验头两个字节是 PE 的 `MZ`（代理/网关返回的错误页也可能 200 + 一堆字节），然后**启动它**；
5. 等两秒确认它活着，再退出旧版。

⚠️ **顺序是刻意的**：先确认新进程活着，再退出自己。反过来做的话，缺运行时的机器上用户会落得
「旧版关了、新版起不来」—— 那是所有失败方式里最难恢复的一种。新进程秒退时**不退出**，
并把退出码与文件路径写进日志。

「检查更新」本身**不写配置文件**（用户明确要求，回归里有源码契约钉住）；那个地址是设置，由自动落盘统一负责。

> ⚠️ 下载下来的那份 exe 是**框架依赖**的（Release 里约 3 MB，不是自包含的单文件）：
> 它需要机器上装了 **.NET 8 桌面运行时**，没有就起不来 —— 这一步会被上面第 5 条抓到。

### 组件文件怎么摆：落点跟着资源走

上游各发布包的**内部结构并不统一**，所以「解压出来往哪放」不能靠猜，得逐个声明：

| 下载物 | 包内结构 | `out/` 里的落点 |
| --- | --- | --- |
| Atmosphere zip | `atmosphere/`、`switch/`、`hbmenu.nro` | `out/` 根（整棵铺开） |
| Hekate zip | `bootloader/**` | `out/` 根 |
| Ultrahand `sdout.zip` | `atmosphere/`、`config/`、`switch/` | `out/` 根 |
| Ultrahand `nx-ovlloader.zip` | `atmosphere/`、`switch/` | `out/` 根 |
| Ultrahand `lang.zip` | **14 个裸 json**（连一层目录都没有） | `config/ultrahand/lang/` |
| Ultrahand `ovlmenu.ovl` / `ovlSysmodules.ovl` | 裸文件 | `switch/.overlays/` |
| Sys-patch zip | `atmosphere/`、`switch/` | `out/` 根 |
| Atmosphere `fusee.bin` | 裸文件 | `bootloader/payloads/` |
| Hekate `hekate_ctcaer_*.bin` | 裸文件 | `payload.bin` + `bootloader/update.bin` |

实现上落点写在**选取资源的那一刻**（`ComponentCatalog` 的 picker 里，`AssetPick.Targets`）——写 picker 的人一定知道自己在拿什么，而下游的合并逻辑不需要认识任何具体文件名。

流程分两段，中间隔一层**暂存目录**（把下载物先摆成「相对 `out/` 根目录」的样子）：

```
download/<组件>/<原始文件>
   ├─ 压缩包 ──解压──→ download/<组件>/unpacked/<落点目录>/
   └─ 裸文件 ──复制──→ download/<组件>/unpacked/<落点目录>/<落点文件名>
                        download/<组件>/payload/   ← payload 单独一份，跟着另一个开关走
                                    ↓ 整棵合并
                                  out/
```

两边都不用知道对方的细节：下载阶段不认识 ini，生成阶段也不认识 zip。

> **升级注意**：旧版本是「一个组件有多个 zip 就各解到以文件名命名的子目录」，于是 `out/` 里会多出 `sdout/`、`nx-ovlloader/` 一层，裸文件则一律平铺在 `out/` 根。新版会**清空暂存区**再重新摆，并顺手把「被清理清空」的目录一并删掉 —— 所以升级后直接再生成一次即可，不会留下空文件夹。

---

### Ultrahand 呼出组合键

`config/ultrahand/config.ini` 里写的是呼出 Ultrahand 的按键组合，格式取自官方源码（`source/utils.hpp`）：

```ini
[ultrahand]
key_combo=L+DDOWN
```

- **section 名固定为 `ultrahand`**，键名为 `key_combo`（对应源码里的 `ULTRAHAND_PROJECT_NAME` 与 `KEY_COMBO_STR`）
- 默认值已改成你配置里的 `L+DDOWN`（官方 `defaultCombos` 之一，不是自己编的组合）
- 下拉框里的 28 个候选项**全部来自官方 `defaultCombos`**，顺序与源码一致，没有自己编造
- 可用按键令牌：`A` `B` `X` `Y` `L` `R` `ZL` `ZR` `SL` `SR` `DUP` `DDOWN` `DLEFT` `DRIGHT` `LS` `RS` `MINUS` `PLUS`
- 只写 Ultrahand 这一份：它首次启动时会自动把组合键镜像到 `/config/tesla/config.ini`（源码 `copyTeslaKeyComboToUltrahand`），同时维护两份反而容易写歪
- `settings.json` 是可以手改的，所以写入前会校验：必须是 **2–4 个不重复的合法按键**，不合法则回退为默认值并给出警告，不会把坏值写进 SD 卡

### Ultrahand 自定义 Overlay 内存档位

勾选后会在同一份 `config.ini` 里多写一段 `[memory]`：

```ini
[memory]
custom_overlay_memory_MB=12
```

这一段**语义容易被误会，所以说明白**：

- 它**不会**直接把 Overlay 堆改成 12 MB。Ultrahand 的「Overlay 内存」滑条原本固定 4 / 6 / 8 MB 三档，这个键的作用是**追加第 4 档**，用户还要在 Ultrahand 里**手动选中**才生效
- 取值规则照抄上游 `source/main.cpp`：**只接受纯数字，且 `> 8` 且为偶数** —— 所以候选项是 10 / 12 / 14 / 16 MB；`4` / `6` / `8` 写进去会被 Ultrahand 整条忽略
- 因此 `4/6/8` 这种「看起来合理」的值在这里是**非法的**，本向导会直接跳过整段并给出警告，而不是产出一份「看着配了、其实没生效」的文件
- 段名 `memory` 对应源码常量 `MEMORY_STR`（`libultrahand/libultra/source/global_vars.cpp`），键名对应 `source/main.cpp` 里的 `parseValueFromIniSection(..., MEMORY_STR, "custom_overlay_memory_MB")`
- 不选（默认）时**完全不写** `[memory]` 段，保持官方行为

### Ultrahand 界面语言 `default_lang`

同一个 `[ultrahand]` 段里还会写界面语言：

```ini
[ultrahand]
key_combo=L+DDOWN
default_lang=zh-cn
```

- **默认跟随本向导的界面语言**：`zh-Hans → zh-cn`、`zh-Hant → zh-tw`、`en-US → en`。界面上的这一项默认是「跟随界面语言」，所以你在设置里换语言，生成出来的 `default_lang` 也跟着换
- 也可以在界面上**直接指定**某个语言（下拉框里 14 项），指定之后就不再跟随界面语言了
- 14 个候选值**照抄上游** `source/main.cpp` 的 `defaultLanguages`，也正是 `lang.zip` 里 14 个 json 的文件名：
  `en` `es` `fr` `de` `ja` `ko` `it` `nl` `pt` `ru` `uk` `pl` `zh-cn` `zh-tw`
- 键名对应 `libultrahand/libultra/source/global_vars.cpp` 的 `DEFAULT_LANG_STR = "default_lang"`，段名对应 `ULTRAHAND_PROJECT_NAME = "ultrahand"`，语言文件路径对应 `LANG_PATH = BASE_CONFIG_PATH + "lang/"`

**为什么它和「安装语言包」是绑在一起的**：Ultrahand 拿这个值去拼 `config/ultrahand/lang/<代码>.json`，而源码里对**文件不存在**的语言是这么处理的：

```cpp
if (defaultLangMode != "en" && !isFile(langFile)) { index++; continue; }   // source/main.cpp
```

即「这个语言在 Ultrahand 自己的设置里根本不出现」，运行时退回编译进 `ovlmenu.ovl` 的英文。所以：

- **只有 `en` 不依赖语言包**，其余 13 个都要求卡上真的有那份 json
- 「安装语言包 `lang.zip`」默认是**不勾**的，于是「配置说中文、卡上没有中文包」会**静默**退回英文 —— 用户以为换了语言，实际什么都没发生
- 因此界面做了**软联动**：`default_lang` 解析出来不是 `en` 时（且 Ultrahand 已勾选），程序自动勾上「安装语言包」并记一条日志。**不锁定**，你可以再手动取消（比如打算自己往卡上放语言包，或用 Ultrahand 自带的更新器补）
- 取消之后也不会被自动勾回来 —— 只有「改了这一项」或「换了界面语言」才会再补一次
- ⚠️ **命令行 `--run` 看不到那条「已自动勾选」的日志**，但联动确实发生了：`EnsureComponentsSelected()` 排在
  `RunOnceAsync()` 之前，而后者开头会 `Logs.Clear()`，把勾选阶段产生的日志清掉（`App.xaml.cs` 里有注释）。
  判据要看 `settings.json` 里 `Ultrahand/installLang` 是不是 `"1"`、以及 `lang.zip` 有没有真的下载 —— 不是看日志。
  实测首次运行 `--run`：日志里没有这条，但 `settings.json` 写着 `"Ultrahand/installLang": "1"`，且 `lang.zip` 确实下了、
  14 份 json 全落进 `out/config/ultrahand/lang/`
- 万一真的没装成（取消了勾选、或 `lang.zip` 里没有这个语言），生成后的体检会给出明确告警，而不是让你拿到一张沉默的卡：

  > Ultrahand 界面语言设为 `zh-cn`，但 `config/ultrahand/lang/zh-cn.json` 不在 out 里，Ultrahand 会退回英文。请勾选「安装语言包 lang.zip」重新生成，或把语言包一并拷进 SD 卡

- `settings.json` 可以手改，所以写入前会校验：不是那 14 个代码之一就回退到界面语言对应的代码并给出警告，**哨兵值 `auto` 与坏值都不会出现在 ini 里**（Ultrahand 会拿它当文件名去找语言包）

另外会写一个 `.wizard-manifest.json` 记录本次生成了哪些文件。下次运行时，**只清理上次生成、这次不再生成的文件**，你在 `out/` 里自己放的东西不会被误删。

### zip 解压到哪、怎么进 out

这一步经常被误解，先把链路说清楚：

```
GitHub Release 的 *.zip
        │  自动解压（带进度条）
        ▼
download/<组件>/unpacked/          ← 解压结果固定落在这里
        │  「把组件文件合并进 out」打开时，递归合并
        ▼
out/                                ← 拷到 SD 卡根目录的就是这一份
```

- **解压一定会做**，和界面上的勾选项无关。每个组件下完 zip 就立刻解压到 `download/<组件>/unpacked/`
- 解压时**逐条写入、同名覆盖**，并且会拦截压缩包里的 `../` 路径穿越
- 日志里报的「共 N 个文件」是**实际写出的文件数**（不含压缩包里的目录条目）
- **个别包需要「解压后筛一遍」**。上游的包结构不总是刚好等于 SD 卡上的样子，目前只有一处：`ELY3M/sys-ftpd` 的 `release.zip` 里多包了一层 `out/`（实际是 `out/atmosphere/…` 与 `out/config/…`），需要剥掉这层、只取 `atmosphere` 与 `config` 两个文件夹。这类规则由插件自己声明（`ExtractPlan`：剥前缀 / 顶层目录白名单 / 文件名白名单），默认是「原样铺开」，所以其余包的行为没有变化。
  > ⚠️ **筛完一个文件都不剩时会直接报错，而不是留一个空文件夹**。上游改了包结构（或者规则写错）时，一个空文件夹在日志里是看不出来的 —— 那种「装了个寂寞」的产物必须吵出来。
- 合并**排在配置生成之前**：组件包里可能带着同名文件（Ultrahand 的 `sdout.zip` 里有 `config/ultrahand/config.ini`，hekate 早期版本的包里带过 `bootloader/hekate_ipl.ini`）。后写的赢，所以**我们生成的配置一定压过组件包里的示例配置**，你在界面上勾的引导项不会被悄悄冲掉
- 合并进来的组件本体**会记进 `.wizard-manifest.json`**。这点很关键：清单是「清理旧文件」和「打包校验」的依据，漏记会导致换组件后旧文件永远清不掉、被一起拷进 SD 卡
- **「把组件文件合并进 out」默认开启**。不合并的话 `out/` 里只有配置文件 + payload，拷进 SD 卡是**开不了机**的（缺 `atmosphere/`、`bootloader/`、`switch/` 这些本体）——本工具的产出就是「一份能直接拷进 SD 卡的完整内容」，默认值产出一份不能用的东西说不过去，所以默认打开
- 如果你只想拿配置文件（比如组件本体已经在 SD 卡上了），可以取消勾选。这时程序会在生成后**主动提醒**你（「只拷 out 不够用」那条），不会让你默默拷一张废卡

> 已经存在 `settings.json` 的用户不受影响：上次的显式选择会被保留，不会因为改默认值而被覆盖。

### 一键打包成 zip

默认开启「生成后自动打包为 zip」，生成完成后会自动在运行目录下产出一个压缩包：

```
SwitchCFW-20260915-114500.zip
├─ atmosphere/
├─ bootloader/
├─ config/
├─ exosphere.ini
└─ .wizard-manifest.json
```

压缩包内**不套外层目录**，解压后这些内容正好对应 SD 卡根目录结构，直接拖进去即可。

也可以随时点右侧「打包为 zip」按钮手动打包（生成过一次之后按钮才可用）。压缩包文件名带时间戳，不会互相覆盖；同一路径重复打包会自动覆盖旧文件。

反复运行**不会在运行目录里堆下一串旧压缩包**：每次打包成功后会清掉同目录下早先由本工具生成的 `SwitchCFW-<时间戳>.zip`，只留刚生成的这一个。清理时只认 `SwitchCFW-yyyyMMdd-HHmmss.zip` 这个精确形状 —— 你自己改名保存的、或其它工具生成的压缩包一律不碰。清理放在打包**成功之后**做，万一这次打包失败，上一次的压缩包还在。

打包完成后会**回读压缩包、逐条比对清单**，确认生成的文件一个都没漏，日志里会给出结论。

### 生成结果完整性校验

生成完成后会自动体检一次，拦住「配置看起来生成了、拷进 SD 卡却启动不了」这类问题。逐项结果直接打进日志：

| 检查 | 说明 |
| --- | --- |
| 生成文件齐全 | 清单里记录的每个文件都真实落在磁盘上（清单含配置文件 + payload + 合并进来的组件本体） |
| 90DNS 文件齐全 | 勾了任一 90DNS 时，`default.txt` + **该开关对应的**那份系统屏蔽表（`sysmmc.txt` / `emummc.txt`）都要在 |
| 组件文件已合并 | 开了合并却一个文件都没合并进来 → **报错**（多半是 `download/` 被清理过） |
| 每个组件都在 | **逐组件对账**：勾了 N 个组件，`out/` 里就必须有这 N 个组件**各自**的文件 → 缺了哪个就点名哪个（提醒）。总数 > 0 挡不住「缺一个」（见第八节第 47 条） |
| `atmosphere/package3` 就位 | 开了合并且勾了 Atmosphere 时，关键启动文件必须在 → 否则 **报错** |
| 引导项存在 | 勾了 Hekate 却没勾任何引导项 → 提醒（生成的 `hekate_ipl.ini` 里没有可启动条目） |
| 只拷 out 不够用 | 勾了组件但**手动关掉了**合并 → 提醒（`out/` 里只有 ini，还要把 `download/` 下解压好的组件一起拷过去） |
| payload 已就位 | 勾了「把 payload 放到对应目录」时，逐个核对 `bootloader/payloads/fusee.bin`、`payload.bin`、`bootloader/update.bin` 是否真的在 → 缺了就提醒并列出缺哪些 |

倒数第二项只在**主动关掉**合并时才会出现（默认是开的）：这时 `out/` 里只有配置文件，真正干活的 `atmosphere/`、`bootloader/` 内核文件留在 `download/` 里。要么一并拷过去，要么重新勾上合并再生成一次。

出现**错误**级校验结果时，退出码为 1（`--run` 模式），界面状态栏也会显示「校验未通过」。

---

## 四、目录结构

```
<exe 所在目录>/
├─ out/                        ← 生成的配置文件（含 fusee.bin / hekate_ctcaer_*.bin）
│  └─ Firmware/                ← 勾了「离线固件」才有：每个版本一个子目录（238 个 .nca）
├─ download/                   ← 下载与解压的原始文件
│  ├─ Atmosphere/
│  │  ├─ atmosphere-*.zip
│  │  ├─ fusee.bin
│  │  └─ unpacked/
│  ├─ Hekate/
│  ├─ Ultrahand/
│  ├─ Sys-patch/
│  └─ Firmware/                ← 同样只在勾选时出现
├─ logs/                       ← 每次运行的完整日志（按天分文件、只留最近 10 份）
├─ lang/                       ← 可选：放 Strings.<代码>.json 覆盖或新增语言
├─ SwitchCFW-<时间戳>.zip       ← 自动打包的结果（可在界面里关闭）
├─ settings.json               ← 语言、Token、代理、镜像站、各项选择（下次启动自动恢复）
├─ crash.log                   ← 只在出错时生成
└─ selftest.log                ← 只在 --selftest 模式生成
```

---

## 五、多语言

内置三套语言包（编译进程序集）：

| 代码 | 名称 |
| --- | --- |
| `zh-Hans` | 简体中文（默认） |
| `en-US` | English |
| `zh-Hant` | 繁體中文 |

三份内置语言包各有 **491 个键**（其中 `_meta` 是元数据对象，文案键 490 个），键集完全相同。

**界面语言还会影响组件来源**：语言是简体 / 繁体中文时，Hekate 默认改从 **`easyworld/hekate`** 下载 Nyx 汉化包（`_sc` = 简体、`_tc` = 繁体），这样 hekate 自带的图形界面（Nyx）也是中文的。**勾了「8G 运存支持」时依然如此** —— 运存只影响 payload 的来源，没有理由让 Nyx 退回英文（2026-09-18 修正；此前是「8G 一律走官方」，结果是中文界面的 8G 用户拿到英文 Nyx）。

具体是这么拆的：汉化包（`bootloader/**` + Nyx）来自 `easyworld/hekate`，而 `__ram8GB.bin` 与那份留着备用的标准 payload 来自官方 `CTCaer/hekate` —— 汉化仓库只发 `_sc` / `_tc` 两个 zip，**一个 `.bin` 都没有**。所以 8G + 中文界面下会查**两个**仓库（日志里能看到两条「正在查询」），而 8G + 非中文界面、或者非 8G 的任何语言，都只查一个。`RepoRequest.AllowLanguageMirror = false` 就是用来把「取 payload 的那一路」钉死在官方地址上、同时仍然尊重你手填的官方行地址。

> 本地化仓库**只发 zip、不发独立的 `.bin`**（官方那边 payload 是单独挂一份的）。所以走这个源时，「把 payload 放到对应目录」无货可摆 —— 程序会自动从包内自带的 `hekate_ctcaer_<版本>.bin` 复制一份成根目录的 `payload.bin`。只在**恰好一个**候选时动手，已存在 `payload.bin` 时绝不覆盖，候选有多个就什么都不做（与其猜一个，不如不做）。

要新增或修改语言，在 `lang/` 目录放 `Strings.<代码>.json` 即可，会覆盖同名键、补齐缺失键。文件里用 `_meta.name` 指定下拉框显示名：

```json
{
  "_meta": { "code": "ja-JP", "name": "日本語" },
  "App.Title": "..."
}
```

---

## 六、高级设置

点右侧「高级设置」展开：

| 项 | 说明 |
| --- | --- |
| GitHub Token | 未登录时 GitHub API 限流 60 次/小时。填 Token 可提到 5000 次/小时 |
| 超时（秒） | 单次 API 请求超时，默认 90 |
| 代理 | 形如 `http://127.0.0.1:7890`，下载大文件时建议设置 |
| 镜像站 | 只填域名（如 `https://gh.example.com`），留空 = 直接用 GitHub 官方地址。填了之后下载与版本查询都会用到它（查询是「官方优先、连不上才用它」）；**镜像站是第三方转发，配了 Token 时 Token 也会经它转发** |
| boot.dat 下载地址 | 留空 = 用内置的默认地址。**可填的样式**：① 网页形态 `https://github.com/作者/仓库/blob/分支/路径`；② raw 形态 `https://raw.githubusercontent.com/作者/仓库/分支/路径`；③ GitHub 的 `/raw/` 形态（前两种最后都落到同一条 raw 地址上）。填别的站点也支持，只是那种地址没有备用通道。填了镜像站时它同样跟着走镜像。<br>输入框为空时框里会显示一行格式示例 |
| 检查更新的地址 | 留空 = 用内置的默认地址（本项目的 releases 页）。**可填的样式**：① 直接写 `作者/仓库`；② 仓库地址 `https://github.com/作者/仓库`（它的 releases 页、某个 tag 的页、API 地址也都认得）。**只支持 GitHub 主机** —— 别的站点会明确报「地址用不了」，而不是拿着同一个路径去查 GitHub 得到一个会误导人的答案。<br>⚠️ **填了但格式不对 ⇒ 回落到默认地址**（2026-09-22 用户要求，与「下载源」同一约定），界面标红 + 日志各说一次 —— 不是把这次检查直接作废。<br>输入框为空时框里会显示一行格式示例；这个框与上面的「boot.dat 下载地址」那个框**同宽、同左边缘**（两个框都通栏）—— 曾经按「下载源」行的标签列缩进过 96px，用户 2026-09-22 否掉了（「前面空出来很多，有点难看」）。`--selftest` 用 `box-parity` 读**两个框各自渲染出来的左边缘与宽度**钉住 |
| 下载源 | 逐个插件可改（见下） |

**关于镜像站**：它换的是**地址**，不是「下载器」—— 程序本身没有任何变化，只是把每条 GitHub 地址按镜像站的规则改写一遍。三种改写形态（2026-09-19 用 `gh.shubiao.cc.cd` 逐条 curl 实测）：

| 原地址 | 镜像站上的等价地址 |
| --- | --- |
| `https://github.com/<owner>/<repo>/…` | `<镜像>/<owner>/<repo>/…`（主域名直接换掉） |
| `https://raw.githubusercontent.com/<owner>/<repo>/<ref>/<path>` | `<镜像>/raw/<owner>/<repo>/<ref>/<path>` |
| `https://api.github.com/…`、`https://codeload.github.com/…` | `<镜像>/proxy/<主机>/…`（通用代理前缀） |

第二条与第三条**不是同一个规则**：把 `api.github.com` 按「主域名换掉」那样拼成 `<镜像>/api.github.com/…`，实测回来的是 **404**（`<镜像>/proxy/api.github.com/…` 才是对的，它回 403 —— 那是 GitHub 的限流答复，说明请求真的代理过去了）。这是最容易写错、也最难从日志里看出来的一处。规则只写在 `Services/GitHubMirror.cs` 一处，回归里逐条钉住（含「已经改写过的不许再改一次」这条幂等要求）。

实测里最好的一条：Release 资源大包也走第一条规则 —— 340 MB 的 `Firmware.23.0.0.zip` 回 200 + `content-length` 正确 + `application/octet-stream`，而且它的 302 目标是**镜像自己**的 `/proxy/release-assets.githubusercontent.com/…`，也就是整条下载都由镜像承担，不是「镜像只给个跳转、字节还是从 GitHub 拉」。

**下载与查询的用法不一样，这是有意的**（2026-09-19 联网实测踩出来的）：

| 用途 | 填了镜像站之后 |
| --- | --- |
| 下载（release 资源、raw 文件、整仓 tar.gz） | **直接走镜像站** —— 用户填它就是为了这个（官方地址慢或不通） |
| 版本查询 / 列目录（`api.github.com`） | **先直连官方，只有连不上才改走镜像站** |

查询为什么不能照搬下载的规则：镜像站的出口是**很多人共用的一个 IP**，而未登录的 GitHub API 额度是**按 IP** 算的 60 次/小时。实测把查询直接改走镜像之后，**第一次查询**就撞上 `403 API rate limit exceeded for 104.23.168.80` —— 表现为「填了镜像站反而一个组件都查不到」。直连官方时那 60 次是用户自己的，够用；镜像只该在官方根本打不通时兜底。

判据也刻意收窄成「**连接层**失败」（DNS 解析不了 / 连接被拒 / 超时）才换通道：`403` 是 GitHub 给出的**回答**，不是网络问题 —— 换过去只会撞上镜像那份更挤的额度，还会把真正的原因盖掉。

「验证 Token」按钮**不走镜像站**（哪怕填了）：它回答的是「**我**这个 Token 对 GitHub 有效吗、**我**还剩多少额度」，而额度是按 IP 算的 —— 走镜像拿回来的是镜像那个公用出口的额度，数字会莫名其妙地小，用户会以为自己的 Token 坏了。

**「Cloudflare搭建镜像站」按钮**就在镜像站输入框下面（2026-09-21 加）：镜像站是这套加速能力里**唯一需要你自己准备**的东西（下载源、插件地址都有内置默认值），所以把「怎么搭」的入口放在填地址的地方，点一下用系统浏览器打开说明文档。打不开浏览器时会把地址写进日志，你还能自己粘一遍。

几条让你放心的性质：

- **留空 = 一个字符都不变**：`GitHubMirror.Apply` 对空镜像原样返回，日志里的地址、请求头、通道顺序全都没变。
- **填错了不会更糟**：解析不出可用地址（写成 `ftp://…`、或把 `owner/仓库名` 整段粘进来）就当成没填、整条退回官方地址 —— 界面此刻显示一行红字，日志的会话头也写明「本次仍直接访问 GitHub」。**不会抛错、也不会拼出一条半成品地址。**
- **镜像挂了也还有退路**：两条下载通道（直链 + API asset 端点）都会过一遍镜像，所以不会出现「直链走镜像、备用通道偷偷回官方」的半吊子状态；而查询那条路本来就是「官方优先」，镜像只是兜底。
- 只填**域名**，不要带 `owner/仓库名`。少写 `https://` 没关系（程序会补）；末尾多一个 `/` 也没关系；自建的 `http://主机:端口` 也认。
- **以后新增插件自动吃到它**：改写发生在 `DownloadService`（**取字节的唯一出口**）与 `GitHubReleaseService`（**查询的唯一出口**）—— 新增一条下载路径只要走第一个就绕不过去，不必记得「这里也要加镜像」。

**GitHub Token 不用手填**：Token 输入框下面有两个按钮 ——

- **「打开 Token 生成页」**：直接在浏览器里打开 GitHub 的 Token 创建页（已带上 `Switch CFW Wizard` 的描述），勾选 `public_repo` 后点最下方的 `Generate token`，把生成的字符串粘回输入框即可；
- **「验证 Token」**：填好后点一下就能知道有没有效 —— 有效时显示登录名和剩余额度（`Token 有效：已登录 xxx，剩余额度 4987/5000 次/小时。`），没填则显示当前匿名额度，Token 写错会明确提示 `Token 无效（HTTP 401）`，网络不通则给出可读的失败原因。

**下载源**（每个插件一行，留空 = 用内置默认地址）：

| 插件 | 默认地址 |
| --- | --- |
| Atmosphere | `Atmosphere-NX/Atmosphere` |
| Hekate | `CTCaer/hekate`（官方） |
| Hekate (easyworld) | `easyworld/hekate`（Nyx 汉化包） |
| Ultrahand | `ppkantorski/Ultrahand-Overlay` |
| ovl-sysmodules | `ppkantorski/ovl-sysmodules` |
| nx-ovlloader | `ppkantorski/nx-ovlloader` |
| Sys-patch | `impeeza/sys-patch` |

**Hekate 占两行**，因为「这次用官方还是汉化包」原先完全跟着界面语言自动切、界面上看不见 —— 英文界面想要汉化 Nyx 根本做不到。拆开之后：

- 两行都留空 → 沿用老规矩（简体/繁体中文用汉化包，其它语言用官方；**8G 运存不影响这条**，它只让 `__ram8GB.bin` 与那份备用标准 payload 回官方取）；
- **只填一行 → 那一行生效**（英文界面填汉化那行也能用上，中文界面填官方那行同理）；
- 两行都填 → 以**官方**那行为准（顺序固定，不看运气；提示文案里也这么写）。⚠️ 8G 时那一路「取 payload」的请求**只认官方行**：你给镜像行填的地址不会把它改道（汉化仓库里没有 `.bin`，跟过去就等于没有 8G payload）。

可以填 `owner/name`，也可以直接粘 `https://github.com/owner/name`（末尾带 `.git`、斜杠、前后空格都能识别）。**填了但格式不对时会就地标红提示，并回落到默认地址**（不会让「开始」直接失败）；解析不出来时**绝不猜一个近似仓库** —— 猜错的代价是把组件指向一个陌生仓库。填了与默认值完全相同的地址不会存进 `settings.json`，免得将来默认地址更新时你跟不上。

点「保存」后写入 `settings.json` 并立即生效，无需重启。

**插件文件**（「固件」「组件」「后台」「主题」「底层」「Ultrahand插件」「学习」这几类插件各一行/多行）：

每个插件要下载的文件都列在这里，**地址与文件名都可以改**：

| 列 | 说明 |
| --- | --- |
| 地址 | 默认是内置的仓库地址。留空 = 用默认值；可以填 `owner/name`，也可以直接粘 `https://github.com/owner/name`；格式不对会**就地标红**并回落默认值 |
| 文件名 / 目录 | 默认是用户清单里给的文件名，**灰色占位文字**显示。留空 = 用默认值；填了就以你填的为准。**极少数几行这里填的是「仓库里的目录」**（那一行会把该目录下的文件全部取回来，行内会多一句说明） |

- **一个文件一行**；一个插件有多个文件就出多行（例如 DBI 的 `DBI.nro` 与翻译包、Luna 的 `luna.zip` 与 `enctemplate.zip`）
- **有一行是「一个目录」而不是「一个文件」**：NXThemes Installer 的官方补丁集取自 `exelix11/theme-patches` 的 `systemPatches/` 目录（20 个 `.ips`）。它不是压缩包，所以没法「下一个包再解压筛」—— 而是先把目录列出来，再逐个下载，每个补丁各自落到 `themes/systemPatches/`。列目录只留文件（子目录与子模块丢掉），结果按文件名排序（顺序不稳的话两次运行的日志会对不上）。这一行的「目录」也能改：换了仓库地址就列新仓库的那个目录
- **有语言相关的行会跟着界面语言变**：DBI 的翻译包在简体界面取 `translation_zhcn.bin`、繁体取 `translation_zhtw.bin`、英文取 `translation_en.bin`；KeyX 中文取 `KeyX-CN.zip`、其余取 `KeyX-EN.zip`。占位文字会跟着切，切换语言后不用手动改。★ **`linkalho` 连「地址」那一列也跟着语言变**（中文走汉化镜像 `SwitchScriptTW/linkalho`、其余走上游 `impeeza/linkalho`，两个仓库的包名也不同）—— 两列**成对**切换，占位文字一起刷新
- 每行下面写着**这个文件会落到 `out/` 的哪里**，省得猜
- **有「落点跟着文件名走」的行**：离线固件那一行的落点是 `out/Firmware/{下载文件名去扩展名}/` —— 因为上游那个包**内部是平的**（238 个 `.nca` 全在压缩包根），版本号只存在于文件名里。所以那一行的默认「文件名」是一段**版本占位模式** `Firmware.x.x.x.zip`（不是某个具体版本号：上游一发新版，写死的名字就作废了），而你把名字改成什么，那个目录就叫什么
- 点「重置」把所有行清回默认值

> **文件名对不上时会「按模式猜」，但一定留痕。** 上游的资源名经常带版本号（`sys-botbase25.zip`、`MissionControl-0.15.2-master-d3941d43.zip`），而清单里写的是带 `x` 占位的形式（`sys-botbasexx.zip`）。匹配分四级：逐字 → 通配（`x`/`X`/`*` 当版本占位）→ 去掉版本号比前缀 → 同扩展名唯一候选；**四级都不中就报「这个槽没下到东西」，绝不随便挑一个**。非逐字命中的会在日志里写明「你要的是这个、我用的是那个」。

### 网络健壮性

- **自动重试**：Release 查询和文件下载失败后各重试 2 次（退避 2 秒、4 秒）。只对可恢复的错误重试（超时、连接中断、5xx 网关错误），遇到 403/429 限流、404 不存在则直接报错，不浪费时间。
- **停滞超时**：下载过程中连续 45 秒收不到任何数据就判定为卡死，中断后自动重试。只要数据在持续到达就不会触发，所以慢速下载不会被误杀。
- **错误提示人话化**：底层异常会被翻译成可操作的说明，例如 502 会提示「代理不稳定、代理不支持大文件或地址被拦截，可换代理或清空代理直连重试」，而不是直接抛出 `The proxy tunnel request ... status code '502'`。
- **断点清理**：下载中途失败会删掉 `.part` 临时文件，不会留下半截文件冒充完整包。
- **失败不中止**：某个组件下载失败后，其余组件仍会继续处理，最后统一汇总「哪些成功、哪些失败」。这样你不用改一个跑一次。
- **失败时不生成配置**：只要有组件没准备好，就跳过配置生成，避免产出一套不完整的 SD 卡内容——宁可什么都不生成，也不给你半成品。
- **两条下载通道，直连不通就换**：Release 资源走 `browser_download_url`，失败后改走 GitHub API 的 asset 端点；仓库文件树/目录里的文件走 `raw.githubusercontent.com`，失败后改走 contents 端点（配 `Accept: application/vnd.github.raw`）。实测在部分网络下 `raw.githubusercontent.com` 会被代理挡成 502，而 API 端点同一时刻是通的 —— 补丁目录那 20 个文件正是靠这条通道才下得回来。⚠️ 走 API 端点会计入 GitHub 的速率配额（未登录 60 次/小时），频繁重跑时请填一个 Token。

---