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
| 运行时 | .NET 8 Desktop Runtime（`dist/` 里的自包含版本无需安装） |
| 网络 | 能访问 `api.github.com` 与 `github.com`（国内建议在高级设置里填代理或 **GitHub 加速镜像站**，见第六节） |

### 构建（开发用）

```bash
cd SwitchCFWizard/src/SwitchCfwWizard
dotnet build -c Release
```

### 打包发布：四种形态，一条命令

`tools/build.sh` 可以一次产出下面四种版本，你按需要挑：

| 形态代号 | 含运行库 | 单文件 | 体积 | 目标机要求 |
| --- | --- | --- | --- | --- |
| `sc` | ✅ | ❌ | ~162 MB（exe + 一堆 dll） | 什么都不用装 |
| `fx` | ❌ | ❌ | ~2 MB（exe + 少量 dll） | 需装 .NET 8 Desktop Runtime |
| `sc-single` | ✅ | ✅ | ~150 MB（一个 exe） | 什么都不用装 |
| `fx-single` | ❌ | ✅ | ~1 MB（一个 exe） | 需装 .NET 8 Desktop Runtime |

```bash
cd SwitchCFWizard

bash tools/build.sh                    # 不给参数 → 出交互菜单，按编号选
bash tools/build.sh all                # 四种全做
bash tools/build.sh sc fx              # 只做其中两种
bash tools/build.sh sc-single --compress --zip --verify
bash tools/build.sh --list             # 看现有产物，不构建
bash tools/build.sh --help             # 全部选项
```

| 选项 | 作用 |
| --- | --- |
| `-o, --out DIR` | 换输出根目录（默认 `publish/`） |
| `-c, --config CFG` | 换构建配置（默认 `Release`） |
| `--compress` | 自包含单文件启用压缩，体积能减一半以上（首次启动稍慢）；框架依赖的单文件没有运行时可压，会提示后忽略 |
| `--zip` | 每个形态额外打一个同名 zip |
| `--verify` | 构建后跑一次 `--selftest`，确认产物真能起来（`fx` 形态需本机已装运行库） |
| `--clean` | 先清空整个输出根目录 |

产物落在 `publish/<形态全名>/`，每个目录里除了程序本体还会有：

- `lang/` —— 三份语言包（`zh-Hans` / `zh-Hant` / `en-US`）。程序内已内置同样三份，放在这里是为了**不改程序就能换文案**，同名文件会覆盖内置版本，也可以再放一份 `Strings.<语言代码>.json` 新增语言。
- `_BUILD-INFO.txt` —— 说明这个版本是什么、目标机要不要装运行库。

脚本会顺手做三件容易忘的事：**同步 `lang/`**（`dotnet publish` 不产出它）、**清掉运行痕迹**（`settings.json` / `*.log` / `logs/` / `download/` / `out/` / `SwitchCFW-*.zip`，否则用户第一次打开就看到别人的存档）、以及**给产物做形态体检** —— 体检是**对产物本身下断言**，不只看命令退出码：

- 读 `runtimeconfig.json` 里有没有 `includedFrameworks`，确认「含不含运行库」真的是你要的那个；
- 单文件形态查根目录文件数（必须恰好 1 个）与 exe 体积（`sc-single` 应 > 30 MB、`fx-single` 应 < 30 MB）；
- 查产物里**有没有残留运行痕迹**（`settings.json` / `*.log` / `logs/` / `download/` / `out/`）。

参数写错时 `publish` 不会报错，只会安静地产出另一种东西；清理步骤失败时也是安静的 —— 这一层就是为了拦住它们。构建完会**改名换入**（先构建到 `<目录>.new-<pid>`，再把旧目录改名成 `.stale-<时间>` 挪走），这样不依赖批量删除，旧目录删不掉也只告警、不算失败。

> **清理遇到「删不掉」时**：脚本先直接删；要是所在环境（沙箱、安全软件）把 `rm` 拦下了 —— 典型表现是**只拒删 `*.log`、别的都放行**，而且**重试也没用**（因为那不是文件被占用）—— 脚本会退一步，把痕迹**挪到系统临时目录**（`%TEMP%` / `/tmp` 下的 `cfw-build-traces-*`）。效果与删除等价：只要它不出现在产物里就行，所以构建日志里不会有 ✗。
>
> 反过来，`publish/` 下那些 `.stale-<时间>` 旧产物**不会**被挪走 —— 那是你自己看得见的目录，挪去临时目录反而让你找不到，所以删不掉时脚本只把路径打印出来，交给用户处理。

> **两个 SDK 坑**（脚本里已绕开，手动发布时会撞上）：
> 1. **`PublishSingleFile=true` 会盖掉 `--self-contained false`** —— 想发布「不含运行库的单文件」时必须同时写 `-p:SelfContained=false`，否则产物是一个 153 MB 的自包含单文件（还额外漏出 5 个 native dll）。命令不报错，只有体积露馅。
> 2. **`EnableCompressionInSingleFile` 只对自包含单文件有效** —— 加在框架依赖的单文件上会直接 `error NETSDK1176: 仅在发布独立应用程序时才支持在单个文件捆绑包中进行压缩`。

### `dist/` 是怎么来的

`dist/` 仍然是项目的正式发布目录 —— 它是 **`publish/self-contained/` 的副本**（逐字节相同），因为 `sc` 形态就是「自包含、多文件」，正是 `dist/` 要的东西。

> ⚠️ 别写成 `bash tools/build.sh sc -o dist`：`-o` 是**输出根**，那样会产出 `dist/self-contained/`，平白多一层目录。

刷新 `dist/` 的正确姿势是「先产出、再复制」，`tools/refresh-dist.py` 把这一步连同体检一起做了：

```bash
bash tools/build.sh sc --verify      # 产出并验证 publish/self-contained/
python tools/refresh-dist.py         # 体检 → 旧 dist 改名挪走 → 复制 → 逐字节复核
```

它会拒绝四种情况（退出码 1 并点名）：源目录不存在、源目录里混进运行痕迹、`runtimeconfig.json` 里没有 `includedFrameworks`（说明那不是自包含版）、复制后逐字节复核不通过。旧 `dist/` 是**改名**成 `dist.stale-<月日-时分秒>` 而不是删除（改名是原子操作，任何环境都能过），路径会打印出来，确认没问题后你自己删。

### 手动发布（不想用脚本时）

```bash
cd SwitchCFWizard/src/SwitchCfwWizard
dotnet publish -c Release -r win-x64 --self-contained true -o ../../dist
```

发布后直接双击 `dist/SwitchCfwWizard.exe` 即可，不需要预装 .NET。
注意 `--self-contained true` 不能省，判据是产物里的 `runtimeconfig.json` 含 `includedFrameworks`。

⚠️ 手动发布**不会**替你做 `build.sh` 那三件事，其中两件会静默出问题：

- **语言包不会同步** —— `dotnet publish` 不产出 `lang/`，而 `lang/` 里的同名文件会**覆盖程序内置文案**。改了 `Strings.*.json` 却忘了同步 `lang/`，用户看到的还是旧文案 —— 程序跑得好好的，只是字不对。
- **运行痕迹不会清** —— 要是你在发布目录里跑过 `--run` / `--selftest`，`settings.json` / `out/` / `download/` / `run.log` 会留在里面一起发出去（2026-09-17 就发生过一次）。

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

## 七、开发辅助

**界面自检**（不需要人工点，跑完自动退出）：

```bash
SwitchCfwWizard.exe --selftest
# 结果写入 selftest.log，全部正常时退出码为 0
```

会解析全部 XAML、渲染一次窗口、跑完数据绑定，并把所有绑定错误收集起来。

加 `--shot` 还会**把窗口渲染成一张 PNG**（默认写到 `selftest.png`，与 `selftest.log` 同目录）：

```bash
SwitchCfwWizard.exe --selftest --shot                 # 出到 selftest.png
SwitchCfwWizard.exe --selftest --shot=D:\tmp\ui.png  # 指定路径
```

视觉类要求（「这两个框一样宽吗」「那行字写的是哪两个字」「这句被折成了几行」）靠它**看图**确认 ——
XAML 里写了多少 margin 证明不了屏幕上的样子。出图前它会把两个要看的元素各自 `BringIntoView`：
「检查更新的地址」那个框在右列的滚动区里、`boot.dat` 卡片在左列的滚动区里，不滚过去图上看不到它们。

**一键打包源码**（把可发布的源码树打成 zip；最省事的用法是**双击仓库根目录的 `打包源码.bat`**）：

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File tools\pack-source.ps1              # 直接打包
powershell -NoProfile -ExecutionPolicy Bypass -File tools\pack-source.ps1 -List        # 只列会打包什么，不打
powershell -NoProfile -ExecutionPolicy Bypass -File tools\pack-source.ps1 -Out D:\bak  # 换输出目录
```

产物是 `publish/source/SwitchCFWizard-<版本>-src.zip`（当前 **88 个文件 / 解压 2.0 MB → 压缩后约 700 KB**）。
包内**自带一个顶层目录**（解压不会散落一地）和一份 `_SOURCE-INFO.txt`：版本、打包时间、内容概览、
**这一轮跳过了什么**、以及拿到包之后怎么构建 / 跑回归。

打包内容 = 项目根下、除「构建产物 / 发布产物 / 运行痕迹 / 编辑器缓存」之外的全部文件。规则就是脚本里那三个
`$Skip*` 列表，没有别的暗规则；实测跳过 `bin/×3`、`obj/×3`、`dist/`、`publish/`、`__pycache__/`、
`.workbuddy-ai/` 与 `*.log`。**不挑着打会得到一个 300+ MB 的「源码包」**——这个仓库根下同时躺着
165 MB 的 `publish/`、163 MB 的 `dist/`、161 MB 的 `src/**/bin|obj`，而真正的源码不到 2 MB。

打完之后脚本**换一条读取路径把 zip 重新打开复核**（不是自说自话「已生成」）：条目数对不对、禁用路径有没有混进来、
关键文件（`ico.ico` / `README.md` / csproj / 三份语言包 / `tools/build.sh`）齐不齐 —— 不通过就报错退出。

⚠️ 这两个文件的**行尾与编码是硬要求**，改的时候别踩：

| 文件 | 要求 | 踩了会怎样 |
| --- | --- | --- |
| `tools/pack-source.ps1` | **UTF-8 带 BOM** + CRLF | 没 BOM 时 PowerShell 5.1 按 GBK 解码 ⇒ 中文全乱，**由中文拼出来的路径会直接不存在** |
| `打包源码.bat` | **纯 ASCII** + CRLF | cmd.exe 按控制台代码页读 `.bat` ⇒ 带 BOM/UTF-8 中文会被执行成乱码 |

所以中文提示一律写在 `.ps1` 里，`.bat` 只是一个 6 行启动器（它只负责转交 `-OpenOutputDir` 与额外参数）。
改完 `.ps1` 记得重新规范化一次（换行 + BOM），编辑器往返可能把 BOM 吃掉。

**命令行执行一次完整流程**（真实联网下载 + 生成配置，不弹界面）：

```bash
SwitchCfwWizard.exe --run
# 结果写入 run.log，无 Error 级日志时退出码为 0
```

组件勾选、引导项、90DNS、8G 等全部沿用 `settings.json`；**首次运行（文件里没有组件勾选记录）时由 `--run` 这条路径自己默认全选**。

> 界面双击打开**不会**这么做 —— 用户要求打开软件时组件全不勾、由自己挑（见第二节）。
> 所以「`--run` 无人值守跑全套」和「界面默认什么都不勾」是并存的：前者要能一步跑完，
> 后者要尊重用户的选择。**唯一调用点在 `App.xaml.cs` 的 `RunHeadless()`**，且必须排在
> `RunOnceAsync()` 之前（`RunAsync` 一开头就读勾选状态，晚了等于没勾）。

适合做批处理或验证下载链路。

**上游事实核对**（**要联网**，手工跑，不在回归里）：

```bash
python tools/check-upstream-facts.py            # 首次要下载清单里全部 15 个包（约 68 MB），之后走缓存
# 逐条核对「注释里写的实测」：全部一致 → 退出码 0
# 读不到 ComponentCatalog.cs、或解析不出任何条目（清单格式被改坏）→ 退出码 2，拒绝静默通过
```

它**从 `ComponentCatalog.cs` 的注释里现取**那份「上游实测」清单（不手抄第二份），逐个核对：资源名是否**逐字**存在、字节数是否一致、包内第一层是不是 SD 卡根名（多一层包装目录 = 落点全错的**静默失败**）、KeyX 两包逐条目差在哪、包内 sysmodule title 与已声明的有没有交集。

> **第 ⑤ 项：按语言切换的候选包，包内结构必须一致。** `KeyX` 与 `linkalho` 都是「两个包/两个仓库、落点声明只有一份」，所以两个候选的**包内路径集合**必须相同 —— 若一个多了一层目录，那种语言的产物会静默落到错地方，而另一种语言完全正常。
> ⚠️ 这一项**第一版只比了「第一层目录名」**（`{n.split("/")[0]}`），而落点声明依赖的是**包内完整路径**（`StripPrefix` / `KeepFileNames` 都作用在完整路径上）—— **自称的覆盖面大于实际**，正是第八节第 70 条那类毛病。已改成比**完整路径集合**，并把差异条目打出来。
> ★ **注入验证过**：给其中一个包凭空加一条路径 → 该检查报 `✗ linkalho 组的候选包包内路径不一致（… 独有 ['__INJECTED__/only-in-one.zip']）`、**退出码 1**，其余检查不受影响。
> ⚠️ 顺带记一条方法论：**第一次注入「没有触发」不是因为检查是橡皮图章，而是因为注入是对称的**（给两个包加同一条路径 ⇒ 它们仍然相等）。**注入必须真的制造出那个条件**，否则「绿」什么都不说明 —— 这是「先证明 X 可能发生」的一个具体形态。

> 为什么需要它：离线回归读的是 **C# 声明**，只能钉住「人有没有改声明」；**上游改了包它抓不到**（声明一个字都不会变）。
> 而 e2e 只能证明「行为对」，证明不了「我写下的那句话是真的」—— 就算包名写错，容错瀑布「猜中」也会让 e2e 变绿。
> **注释里的「实测」必须有人来核对，否则它会一直错下去**（2026-09-18 真的查出一条编造的，见第八节第 69 条）。

**看包内结构**（**要联网**，手工跑，不下载整个包）：

```bash
python tools/zip-listing.py xfangfang/wiliwili wiliwili-NintendoSwitch.zip
python tools/zip-listing.py --specs-file specs.txt        # 每行「owner/repo 资源名」
```

用 HTTP Range 只取 ZIP 末尾的**中央目录**（几十 KB）就能拿到完整条目清单 —— 包内有没有多一层包装目录、某个文件在不在子目录里，一眼看清。**这正是「声明落点 / 声明 `ExtractPlan`」需要的依据**：`wiliwili` 的 `StripPrefix` 就是这么定下来的。正常网络下直接下整包更省事，它是给**限速链路**（本项目沙箱实测 ~25 KB/s）准备的。

**整理一次 e2e 的证据**（跑完 `--run` 之后，**不联网**）：

```bash
python tools/e2e-evidence.py src/SwitchCfwWizard/bin/Debug/net8.0-windows \
    .workbuddy-ai/scratch/e2e-study.md --label "学习 7 个" \
    --cache .workbuddy-ai/scratch/zips \
    --expect switch/wiliwili/wiliwili.nro \
    --absent switch/wiliwili/wiliwili/wiliwili.nro \
    --bytecheck "switch/linkalho/linkalho.nro=linkalho.zip,linkalho-v2.0.2.zip"
```

e2e 的判据是**退出码**，可退出码只说「没炸」，说不出「落对地方了没有」。这个脚本把「跑完之后磁盘上到底有什么」固定成文本，专钉三件**只有看磁盘才能确认**的事：

1. `out/` 顶层是不是 SD 卡根名（出现别的东西 = 多了一层包装目录，落点全错）；
2. `--expect` 的落点是否真的命中、`--absent` 的路径是否真的**没有**出现 —— 后者专逮「包装目录没剥干净」：`wiliwili` 若漏了 `StripPrefix`，产物会变成 `switch/wiliwili/wiliwili/wiliwili.nro`，**文件在、程序不报错、homebrew 菜单里就是不显示**；
3. **「按语言选包」到底选了哪个** —— 把落盘文件与候选包里的同名条目**逐字节比**才作数。⚠️ 就算声明里的包名写错了，容错瀑布「猜中」也会让 e2e 变绿、只在日志里留一行痕；**只有比字节看得见**。

有问题时**退出码 1 并逐项点名**（Error 数 / 非 SD 根名 / 落点缺失 / 不该存在的路径 / 字节对不上任何候选包）。

**验证镜像站能不能用**（**要联网**，手工跑，不在回归里）：

```bash
cd tools/MirrorProbe && dotnet run -c Release                            # 用内置的样例镜像
cd tools/MirrorProbe && dotnet run -c Release -- https://你的镜像根地址   # 换自己的（`--` 不能省）
```

它用**程序自己的 `DownloadService`** 真下两个文件（一个 raw 文件、一个整仓压缩包），后者还会**解开归档看条目** —— 光验 gzip 魔数不够，一个「恰好也是 gzip 的错误页」能骗过它；最后打一句「直连通不通」作对照。退出码 0 = 镜像侧两条形态都通过。

为什么需要一个联网工具：回归里的镜像用例**全是离线的**（纯函数 + 桩 `HttpMessageHandler`），它们能证明「请求打在了哪个 URI 上」，但证明不了「HttpClient 真的跟着镜像的 302 走、把字节流出来落成文件」—— 而配镜像的全部价值就在后半句上。另外**镜像站各不相同**（有的只代理 release 资源、有的连 raw 都不代理），「同一套规则换一个镜像还行不行」只能实测：新增插件时若发现某条地址没走镜像，先跑它，就知道该改配置还是该改规则。

> ⚠️ 2026-09-19 用它在开发沙箱里跑出的结果：raw 242 字节、整仓 23660 字节（**解开后 21 个条目、README 在里面**）**都通过**，而同一条 raw 地址**不走镜像时失败**（`HttpRequestException`）—— 对照组成立，说明那两条成功确实是镜像带来的，不是「本来就能通」。

> 四个工具分工不同，**谁也不能冒充谁**：
> `zip-listing.py` 回答「**包长什么样**」（读上游），`check-upstream-facts.py` 回答「**我写下的那句话是不是真的**」（读注释），
> `e2e-evidence.py` 回答「**跑完之后磁盘上是什么样**」（读产物），
> `tools/MirrorProbe` 回答「**这个镜像站现在还能不能用**」（读真实网络）。
> `zip-listing.py` / `check-upstream-facts.py` 的 Token 都**只从 `GITHUB_TOKEN` 环境变量读，绝不写进文件**；`e2e-evidence.py` 不联网；`MirrorProbe` 不需要 Token。
>
> 另有一个**发布侧**的脚本 `tools/refresh-dist.py`（读本地产物，不联网）：把 `publish/self-contained/` 刷成正式发布目录 `dist/`，带**三项体检**（必需文件齐全 / 无运行痕迹 / `runtimeconfig.json` 里有 `includedFrameworks` ⇒ 真是自包含版）**加逐字节复核**，旧 `dist/` 一律**改名**留档而不是删除 —— 见第一节「`dist/` 是怎么来的」。它自带夹具测试 **`tools/test-refresh-dist.py`**（10 项：正常路径 2 条覆盖「新建 / 改名留档」两条分支，加 5 条拒绝路径 —— 源目录不存在 / 混进运行痕迹 / 不是自包含产物 / 脚本放错位置 / **`--work-dir` 指向真项目根**）。最后那条是防误删的：夹具脚本会递归删除工作目录，而 `--work-dir` 是用户可传参数。

**配置生成回归测试**（不联网，1703 项断言）：

```bash
cd SwitchCFWizard/tools/ConfigSmokeTest
dotnet run
```

覆盖：多语言加载（三语键集一致）、**默认值快照**（`AppSettings` 与 `WizardOptions` 的默认值用独立用例钉死，避免默认值漂移悄悄改变测试语义）、**全新安装的界面状态**（删掉 `settings.json` 后真的构造一次 `MainViewModel`，逐项核对用户打开软件时看到的东西：**全部组件一个都不勾**、三种引导模式全勾、两个屏蔽项默认**不勾**、Sys-patch 未勾且可自由勾选（没勾屏蔽项就不该被锁定）、`CanStart` **不可用**且显示「请至少勾选一个组件」的提示、打包开关默认不勾、三个显示名与启动图默认为空、下载源清单已就位，并且**界面上全部 76 个选项的取值与目录默认值逐项一致** —— 最后这条同时守住「不勾组件 ≠ 选项没有默认值」，以后新增选项会自动被覆盖）、**用户配置基线**（把「向导默认值生成出来的 8 份 ini」与你实际在用的配置逐项对齐，**73 项断言**，含「勾上屏蔽序列号写 `1`」与「取消勾选写回 `0`」两个方向；任何一处默认值被改回上游值都会立刻报警）、**选项定义自洽性**（把全部 76 个选项的定义静态扫一遍：`DefaultValue` 必须落在 `Choices` 里、下拉框必须有候选项（候选项运行期动态生成的除外）—— 第 27 条的根因正是这条不变量被悄悄破坏，而它此前没有任何检查）、网络错误翻译与可重试判定、**屏蔽序列号 ↔ Sys-patch 双向联动**（两个屏蔽项互相独立、任一项触发强制、手动勾 Sys-patch 时两项默认勾上、可自行取消、程序自动勾选不引发连锁、隐藏时不死锁）、**配置层屏蔽独立性**（`blank_prodinfo_sysmmc` / `blank_prodinfo_emummc` 互不影响）、**`settings.json` 向后迁移**（旧单开关 `BlankSerial=true/false` 正确展开、迁移后旧字段清空、新格式原样保留、全新安装不替用户开屏蔽）、**迁移链路贯通**（旧 `settings.json` → `Load()` → `MainViewModel` → 界面两项勾选状态与强制联动结果一致，防止「迁移对了但界面漏读」）、**界面状态 → `WizardOptions` → `exosphere.ini` 的装配链路**（刻意用「一开一关」的不对称取值，专门逮住「两个屏蔽项在装配时被写成同一个来源」这类复制粘贴错位 —— 这种错在界面上完全看不出来，只有生成的 ini 才是证据）、**界面设置与 `WizardOptions` 的接线完整性**（反射遍历，自维护：把界面上每个「`WizardOptions` 里有同名属性」的开关翻成**与默认值相反**的值，装配后逐项断言它真的被翻过来了 —— 将来新增设置项若忘了在 `BuildWizardOptions()` 里接线，它会停在默认值、立刻报警；反向再扫一遍 `WizardOptions` 的每个标量属性是否都有界面来源；连例外清单本身也钉住「名字必须真实存在」，免得属性改名后清单里留个死名字、照样「通过」。**已用注入 bug 验证过会失败**）、**配置项落点完整性**（目录里每个选项都能声明自己的落点 `TargetFile`，生成器按落点遍历去写 —— 所以「声明了一个新落点却没写对应循环」会让选项在界面上能改、生成结果里却一行都没有；这条把「声明的落点集合」与「产出的配置文件集合」双向比成穷举，路径分隔符归一化后再比；现有的「应写入 12 个文件」只是**计数**断言，少写一个文件数量不变、挡不住它 —— 注入实测印证了这点。**已用注入 bug 验证过会失败**）、**保存往返**（界面状态 → `settings.json` → 重新加载，且断言旧字段 `BlankSerial` 不会被写回成 `true`，否则下次启动会二次迁移、把用户的分项选择盖掉）、**全部 76 个组件配置项逐个往返**（不挑已知 key，而是把每个组件的每个选项都设成可辨识的值再走一遍，附「越界值仍会被纠正」的反向护栏）、**组件勾选状态的持久化往返**（只勾一部分的不对称取值；未勾的项必须以 `false` 显式落盘；「开始」按钮可用性 `CanStart` 必须随勾选立刻变；界面路径**不**默认勾选任何组件，而 `EnsureComponentsSelected()`（命令行 `--run` 用）只在文件里没有勾选记录时才默认全选，不得改动已有的勾选，也不得把「用户主动取消全部勾选」改回全选）、**改动引导项后 `autoboot` 仍指向同一个系统**（编号是位置相关的，覆盖取消/恢复勾选、目标系统自己被取消、三个全不勾等情形）、**`autoboot` 编号在界面与生成的 `hekate_ipl.ini` 里指向同一个系统**（跨层核对：界面候选项的名字必须等于文件里第 N 个引导项的名字，两处顺序一旦不一致，界面显示「虚拟破解」而机器实际进「正版」）、完整场景（三引导 + 屏蔽序列号 + 90DNS + 8G）的 12 个输出文件、最小场景、kip1 开关、Ultrahand 呼出组合键与 `[memory]` 自定义 Overlay 内存档位（默认值 / 自定义 / 规范化 / 非法输入回退）、payload 放到对应目录（`fusee.bin` → `bootloader/payloads/`；hekate payload 同时落 `payload.bin` 与 `bootloader/update.bin`；8G 模式下标准版退居 `bootloader/payloads/` 不盖掉正在用的那份；独立于组件合并开关、关掉开关不摆放、缺文件时给提醒）、**产物落点**（`OutputTarget` 的路径拼接、压缩包声明多个落点直接报错、以及拿**真实 picker** 取落点后跑一整套「暂存 → 生成」，逐条核对：`out/` 里不该有 `sdout/` / `nx-ovlloader/` 这一层、`lang.zip` 的裸 json 落到 `config/ultrahand/lang/`、`.ovl` 落到 `switch/.overlays/`、hekate payload 的双落点、**payload 摆放必须排在组件合并之后**、以及「旧版本遗留的暂存目录会被清掉」与「被清理清空的目录会一并删掉」）、zip 解压（返回真实文件数而非条目数、嵌套目录、`../` 路径穿越被拒）、组件文件合并进 `out/`（合并结果进清单、同名碰撞只记一次、**生成的配置压过组件包里的示例**、关掉合并后旧文件被清单清理）、90DNS 行数、引导项键值、类型化值格式（`u8!0x1` / `u64!` / `str!`）、zip 打包（不套外层目录、可重复覆盖、**反复运行会清掉早先留下的旧压缩包，且不误删用户自己改名保存的或其它工具生成的**）、完整性校验（缺文件/未合并/无引导项各自的分支）、**下载备用通道**（用桩 `HttpMessageHandler` 离线跑「直链 502 → 自动改走 GitHub API asset 端点」，逐条核对切换时机、切换通知、`Accept: application/octet-stream` 是否真的发出去、返回 JSON 时是否拒绝写盘、404 是否不换通道、`Authorization` 是否只发给 asset 端点那一跳）、**引导项图标 `icon`**（图复制进 `bootloader/res/`、`icon=` 写相对 SD 根路径、文件缺失只跳过这一项、进产物清单、同一项同时有 `logopath` 与 `icon` 时两行都在、**跨键撞名**：A 项的启动图与 B 项的图标同名不同目录时后来者加 id 前缀、同一文件被两个键共用时不重复复制）、**XAML 绑定名可解析**（把 `MainWindow.xaml` 里每个 `{Binding X}` 的根名字扫出来，逐个确认程序集里真有同名公开属性 —— 写错**不编译报错**、运行期只静默绑到空值，此前只有 `--selftest` 兜、而它要人主动跑且报告只写文件；名字清单**从程序集反射生成**，出现护栏不认识的绑定写法时**报错而不是静默跳过**）、**设置项的落盘/读回接线**（`SaveSettings()` 的写入块与构造函数读回块都是手写的，漏任一处都只在「下次启动」才现形：前者让用户改完一重启就丢、后者让文件里存着而界面显示默认值；判据是「`AppSettings` 的每个标量字段有没有在这两段代码里出现过」，名字清单**从 `AppSettings` 反射生成**，两段代码体从源码里按缩进切出来并附前置条件断言防止空转）、压缩包与清单比对。全部通过时输出 `SMOKE: ALL OK` 并返回退出码 0。

2026-09-16 新增七项功能后补的护栏：**引导项显示名**（留空/纯空白回落到界面语言的默认名、填了就用用户的并去首尾空白、段名真的写进 `hekate_ipl.ini` 且内容一字不改、改名后不再留默认名的段、界面 `autoboot` 候选项跟着改名 —— 14 项）、**hekate 启动图**（图被复制进 `bootloader/res/`、`logopath` 写的是相对 SD 卡根目录的路径而不是本机绝对路径、文件不存在时只跳过这一项不影响别的、进产物清单、**两个引导项选不同目录下的同名图时后来者加 id 前缀**、两项选同一个文件时共用一张不重复复制 —— 19 项）、**中文本地化 hekate 源**（简繁都算中文、判据只有「界面语言」这一条、`ResolveRepo` 的三级优先级、按 `_sc`/`_tc` 后缀挑包、**繁体不会被判成简体**、官方源没有本地化包时回落通用规则；2026-09-18 第十五轮又加了「8G 也走汉化包」「两个请求的结构与 `Applies` 三种取值」「跨语言 payload 落点等价」—— 19 项 ⇒ 37 项）、**下载源可配置**（`owner/name` 与完整 URL 与 `.git` 与省略协议的解析、五种非法输入一律返回 `null` 不猜、非法地址就地标红且运行时回落默认、填了等于默认的值不算「改过」、界面清单覆盖全部仓库、Hint 文案取得到、用户地址真的被用上 —— 26 项）、**payload.bin 兜底**（从包内 `hekate_ctcaer_*.bin` 复制、内容一致、进清单、**已存在时不覆盖**、候选不唯一时什么都不做 —— 5 项）、以及**多语言键覆盖率**（从源码里扫出 325 个被引用的键，逐个确认三份语言包都有文案；另补扫 `"Component." + kind + ".Name"` 这类动态拼键 —— 3 项）。

2026-09-16 后续改动（**首次运行组件默认一个都不勾**）补的护栏 —— `CheckFirstRunSelectionContract()`（10 项）：**保护分支真的会被走到**（全新状态下 `BuildWizardOptions().HasAnyComponentSelected` 必须为 false，否则 `RunAsync` 里那条「没选组件就提示并返回」的分支形同虚设）、**提示的显示与消失**（`ShowNoSelectionHint` 未勾时显示、勾上任意一个立刻消失）、以及**源码契约核对**（`App.xaml.cs` 的 `RunHeadless` 里 `EnsureComponentsSelected()` 必须排在 `RunOnceAsync()` 之前）。

2026-09-17 四项需求（**90DNS 拆两个开关**、**90DNS ↔ `enable_dns_mitm` 双向绑定**、**下载源 Hekate 拆两行**、**`[hbl_config]` 五个键可配置**）补的护栏 —— `Check90DnsSwitches()`（12 项）+ `CheckHblConfigOptionsAreWired()`（21 项）+ `CheckRepoSources()` 由 26 项扩到 37 项 + `CheckHblConfigFallbacks()`（9 项）+ `CheckLocalizedHekateSource()` 的 8G payload 回退用例（3 项）：

- **90DNS 四种组合 → hosts 文件集合逐项对齐**（都不勾 = 一份都不写；只勾真实 = `default.txt` + `sysmmc.txt`；只勾虚拟 = `default.txt` + `emummc.txt`；都勾 = 三份）。断言的是**集合相等**而不是「某文件存在」—— 顺手也守住了「上一轮写的 hosts 文件会被清单清理掉」，否则从「都勾」切到「都不勾」会留下 `sysmmc.txt`。
- **联动的两个方向都断言**（这是最容易写成「看着对、其实单向」的地方）：勾任一 90DNS → `enable_dns_mitm` 自动开；都取消 → 自动关；**手动取消 `enable_dns_mitm` → 两个 90DNS 同时取消**；以及**勾上 mitm → 自动补勾当前可见的 90DNS**（第二版加的第四条，见下方「第二版」一段）。起点先钉住 `Atmosphere` 组件里确实有 `enable_dns_mitm` 这个键 —— 键名拼错时 `FindOption` 返回 `null`，联动会**静默失效**。
- **`[hbl_config]` 的 ini 键 ↔ 存储键对应关系从写入点扫出来**（不手抄），再双向比对「声明了就要写 / 写了就要有声明」。因为两段都有 `override_key`，存储键必须加 `hbl_` 前缀，而写进文件时又必须是真名 —— 中间任何一处错位，产物里就会多一个 Atmosphere 不认识的键、少一个它要的键，界面上却一片祥和。
- **行为验证而非只比默认值**：把五个选项各自改成**非默认值**再生成，逐项断言它出现在 `[hbl_config]` 里。只比默认值是恒真的 —— 写入器写死同一个字面量时照样通过（这正是改动前的状态）。文本选项的探针值放在一张小表里，表里没有就**报错而不是跳过**，免得这条用例对新增选项视而不见。
- **下载源逐行可达性**：对清单里**每一行**都做一次探针（填一个自定义地址 → 断言它真的被 `ResolveRepo` 采纳）。加一行到清单里很容易，让解析逻辑真的读它才难；不读的话界面上一切正常、下载时纹丝不动。另外把 hekate 两行的组合语义钉死（只填一行 → 那行生效，含「英文界面填汉化行」；两行都填 → 官方优先；都不填 → 回落到语言默认；8G → 不影响包的语言，只让 payload 回官方）。

> 这一轮同时把上一轮遗留的**两个坏习惯**改掉了：护栏里的清单不再手抄（`DeclaredRepos` 用**反射**扫 `ComponentCatalog` 的 `RepoSpec` 字段），以及**旧护栏里的硬编码期望值**（「下载源清单应有 6 项」）换成了双向集合比对 —— 那个数字正是「计数断言不是完整性断言」的又一个例子。

2026-09-17 两项决策（**8G 拿不到 8G payload 时告警**、**两个 90DNS 都不勾 ⇒ `enable_dns_mitm` 关**）补的护栏 —— `CheckRam8GbPayloadWarning()`（9 项）+ `Check90DnsSwitches()` 加 2 项 + `CheckUserConfigBaseline()` 的期望值改成 `u8!0x0`，另加审计新发现的 **Ultrahand 语言包误选** 用例 `CheckUltrahandPickerAssets()`（8 项），合计 745 → 764：

- **8G 缺失的判定是纯函数，可以穷举**（`ComponentCatalog.IsRam8GbPayloadMissing`）：带 `__ram8GB.bin` → 不告警；勾了 8G 而发布里没有 → **告警**；没勾 8G → 不告警；**一个 `.bin` 都没有属于另一种情况，也不告警**（两条告警同时打，用户不知道该看哪条）。判据写反比不告警更糟 —— 没勾 8G 的正常用户会天天看到假告警，真告警就没人看了。
- **告警本身够不着，用源码契约钉住**：它发生在下载循环里，真实触发要网络 + 一个恰好不带 8G payload 的发布。所以直接扫 `MainViewModel.cs` —— 调用点**恰好一处**、必须被 `IncludePayloads` 挡住、必须打 `LogLevel.Warning`、**且不得是 `Error`**（方案 A 要的是告警不是报错：8G 缺失只影响 payload 版本，报错会让整次生成失败、用户拿不到任何产物）。⚠️ 只钉「有调用」是不够的：把 `Warning` 降成 `Info` 用户就再也看不见这条告警，而所有断言照样全绿 —— 所以级别一起钉。
- **命名判据只能有一份**：带引号的 `"ram8GB"` 字面量在整个 `src/` 里只允许出现一次，且必须在 `ComponentCatalog.IsRam8GbPayloadName` 里（只看代码行，注释不算）。**这条护栏是真抓到过东西的** —— 改完 `ComponentCatalog` 与 `ConfigGenerator` 的第二处后，第一处漏改了，它立刻报「实际 2 处」。同一条规则此前散在**三个地方**（picker 挑 payload、`ConfigGenerator` 挑根目录 `payload.bin` 的来源、8G 模式下删掉标准版），上游改命名就会一处认、一处不认 —— 而两边都不报错：8G 用户会拿到 4G 的 `payload.bin`，或者 8G 版 payload 被当成标准版删掉。
- **起点一致性单独钉一条**（`Check90DnsSwitches()` 里，在动任何开关之前）：删掉 `settings.json` 后构造一次 `MainViewModel`，断言两个 90DNS 都不勾**且** `enable_dns_mitm` 为关。这条钉的不是「某字段等于某字面量」，而是**默认值 ⇔ 联动规则**这条自洽性 —— 默认值留 `1` 的话，界面一打开就是「90DNS 全关、mitm 却开着」，规则与起点互相矛盾，而产物会照默认值写出 `mitm=1`（遥测被挡住的假象：`hosts` 一份都没生成）。注意 `CheckUserConfigBaseline` 里那条 `u8!0x0` 只保证「生成器照默认值写」，**不保证「界面打开时看到的就是关」** —— 三条链路（对象默认值 / 生成的 ini / 界面初始勾选）必须各有一条。
- **Ultrahand 的「随便拿一个 zip」回退会拿到语言包**（`CheckUltrahandPickerAssets()`，8 项）：上游 `Ultrahand-Overlay` 的 Release 里**同时**有 `sdout.zip`（整套 SD 内容）与 `lang.zip`（14 个语言 json），而 GitHub 的 `assets` 是**按名字排序**返回的 —— `lang.zip` 排在前面。于是 `sdout.zip` 缺失时回退会拿到语言包。用例**用上游真实的资源名与真实顺序**构造 ReleaseInfo，把四个方向都钉住：整包模式只挑 `sdout.zip`；勾了语言包时两个都下、且语言包的落点必须**显式声明**；`sdout.zip` 缺失时**一个都不挑**（交给既有的「未找到匹配的资源文件」告警），绝不退回 `lang.zip`；手动安装模式只挑 `ovlmenu.ovl`。
- **`CheckUserConfigBaseline` 里出现了一处「刻意例外」**：`enable_dns_mitm` 的默认值不再是用户那份 `system_settings.ini` 里的 `1`。这是唯一一项被明确推翻的基线，注释里写清了「别按本节口径把它改回 `u8!0x1`」。

> **注入验证**：① 把 `enable_dns_mitm` 的默认值改回 `"1"` → 只有 `CheckUserConfigBaseline` 的 `u8!0x0` 与 `Check90DnsSwitches` 的起点一致性变红（754 = 756 − 2）；② 把 8G 告警的 `LogLevel.Warning` 降成 `Info` → 只有「必须以 Warning 级别打日志」那一条变红（755）；③ 在 `ConfigGenerator` 里再写一份带引号的 `"ram8GB"` 字面量 → 只有「命名判据只应有一份」变红；④ 把 Ultrahand 的 `sdout.zip` 回退改回裸的「任意 `.zip`」 → 只有那两条变红（762 = 764 − 2），**且报错里直接印出「实际选中：lang.zip」—— 实证了缺口是真的，不是纸上推演**。四次注入都还原后 md5 逐字节校验通过，且**还原后重新构建**才继续跑。

2026-09-17 第二版（**90DNS ↔ `enable_dns_mitm` 改成完全双向绑定** + **清理 16 个语言包死键**）—— `Check90DnsSwitches()` 由 12 项扩到 17 项（删掉 1 条「刻意不对称」，新增 6 条）+ 新增 `CheckNoDeadLanguageKeys()`（3 项），合计 764 → 772：

- **前三条规则其实早就在了**，这轮补的是第四条：勾上 `enable_dns_mitm` → 自动补勾当前可见的 90DNS。前三条（勾任一 90DNS → 打开 mitm；都取消 → 关掉；取消 mitm → 两个 90DNS 一起取消）合起来只是**事件规则**，所以「mitm 开着、两个 90DNS 都不勾」这个状态仍然**手动可达** —— 旧用例甚至就是靠这个状态在跑的（它那条「勾上 mitm 不该反过来勾 90DNS」的断言，反过来证明了缺口可达）。而它会产出**误导性产物**：`enable_dns_mitm=1` 写进 ini，`hosts` 一份都没有，用户以为遥测已经被挡住。
- **补勾只补「当前可见」的那几项**：跟 `ShowSysmmcDns` / `ShowEmummcDns` 走。没勾对应引导模式的 90DNS 开关在界面上是隐藏的，硬勾上只会让用户在别处看到一个自己没点过的勾，产物里也不会多出 `emummc.txt`（生成端同样要求对应引导模式）。两个引导模式都没勾时退化成「两个都补」：此时整个 90DNS 区域都不可见、不会立刻产生任何文件，但守住了 `mitm ⇔ 至少一个 90DNS` 这条不变式。
- **载入时也要归一化**：老版本 `enable_dns_mitm` 的默认值是 `1`，升级上来的 `settings.json` 正是「mitm=1 而两个 90DNS 都不勾」。构造函数里调用一次 `ApplyDnsMitmCoupling()` 把它拉回「关」；**但走的是「关 mitm」而不是「补勾 90DNS」** —— 载入不是用户动作，替他写出一份没要过的 `hosts` 更越权。⚠️ 为此把两个 90DNS 的字段赋值**提前到 `SelectedLanguage` 之前**：`SelectedLanguage` 的 setter 会顺手落盘一次，排在它后面的话修正值要等下次保存才进 `settings.json`，界面与文件会长期不一致。
- **死键护栏的白名单不手抄**：唯一放行的「拼出来的键族」是 `Component.<Kind>.Name/.Desc`，成员**从枚举本身生成**（§1 教训 4）。此前不敢加这条护栏，正是因为它需要一份 16 条的手写白名单 —— 而手写清单自己会腐烂（第八节第 41 条）。
- **删键用逐行删除，不重新序列化**：重新序列化会把整份文件过一遍 JSON 写出器，转义/缩进任何差异都会变成几千行假 diff，真改动被淹没。逐行删除要额外处理一个边界：删掉的若是**对象最后一条**，前一行会留下多余逗号 → JSON 语法错误（逐行删看不见）。

> **注入验证（这轮三次，逐个单独做）**：① 让 `OnDnsMitmOptionChanged` 在「被勾上」时直接返回（即退回上一版的刻意不对称）→ **只有**三条规则④断言变红（769 = 772 − 3），「复位」那条照样绿 —— 因为取消方向的行为没动；② 去掉构造函数里的归一化整块 → **只有**载入归一化的两条变红（770 = 772 − 2），且报错里直接印出「实际读回的是 1」；③ 往**三份**语言包各插一个 `Common.DeadKeyProbe` → **只有**死键护栏变红并**直接点名这个键**（771 = 772 − 1），而键集一致性护栏保持全绿 —— 三份都插正是为了让归属无歧义（只插一份的话，报红的是「三份包键集不一致」，那条不指向真因）。三次都还原后 md5 逐字节校验通过，且**还原后重新构建**才继续跑。

2026-09-17 第四版（**Ultrahand 界面语言 `default_lang` 跟随向导语言 + 可手动覆盖**）—— 新增 `CheckUltrahandDefaultLang()` + `CheckLangPackCoupling()`，`CheckUserConfigBaseline()` 加一条 `default_lang=zh-cn`，合计 772 → 826：

- **取值域用集合比对而不是计数**：14 个语言代码从上游 `source/main.cpp` 的 `defaultLanguages` 数组抄出来，并与 `lang.zip` 里 14 个 json 文件名**互相印证**。断言写成 `SetEquals` —— 「14 个」这个数字本身挡不住「抄错了一个、另一个抄重了」（§1 教训 1）。
- **语言名必须是 endonym**（`English` / `简体中文` / `Русский`…），不查多语言表。理由和 `LocalizationService` 取语言包 `meta.name` 是同一个：用户在「我不认识当前界面语言」的时候才会去翻语言列表，此时把选项名翻译成他看不懂的文字毫无意义。
- **中文按书写系统分，不看主语言子标签**：`zh-Hans → zh-cn`、`zh-Hant → zh-tw`，且 `Hant` / `TW` / `HK` / `MO` 任一命中即判繁体。只看 `zh` 前缀等于**替用户换语言**，而且用户很难察觉 —— 生成的 ini 里两串都是「中文」。
- **坏值回退 + 不落 ini**：哨兵 `auto`、空串、以及 7 个坏值（`klingon` / `zh` / `en_US` / `ja-JP` / `简体中文` / `auto1` / `EN-US`）逐个验证 —— 解析结果必须是合法代码，**且这个坏值一个字都不能出现在产物 ini 里**。大小写差异（手写 `ZH-CN`）单独验一条：它要被**规范化**而不是被当成坏值（判据用 `IsKnownUltrahandLanguage` 而非「解析结果 ≠ 原值」，后者会凭空多报一条警告）。
- **界面语言逐个枚举**：不手写「三种界面语言」的清单，而是遍历 `LocalizationService.Instance.Languages`，对**每一个**都断言映射结果落在取值域里。以后加一门界面语言，这条自动覆盖。
- **软联动的门控在 `IsSelected`**：组件没勾时**不动作**。这不是偷懒 —— 用户还没决定装不装 Ultrahand，替他勾一个安装项是越权；代价是必须在「勾上 Ultrahand」时补一次触发，所以 `OnComponentPropertyChanged` 里也挂了一处。用例把**六种**情形都钉住：中文界面勾上组件 → 补勾；用户可取消且不被改回；切英文 → 不勾回；显式选 `en` → 不勾；改回 `auto` 再勾 → 补勾；英文界面全新安装 → 不勾。
- **载入归一化与用户动作分开**：存档里是「Ultrahand 已勾 + 中文界面」时，载入后要补勾 —— 这条单独一个用例，且**只回写被修正的那一项**（`_settings.OptionValues[...]` + `SettingsStore.Save`，**不能用 `SaveSettings()`**：构造函数里 `RepoSources` 还没建好，会顺手把下载源覆盖成空）。
- **校验器与生成端判据同源**：`OutputValidator` 复用 `ResolveUltrahandDefaultLang` + `ComponentCatalog.UltrahandLangFileFor`，不另写一份路径字面量。告警验了**四个方向**：语言包缺 → 报；文件在位 → 不报；`en` → 不报（英文文案编译在 `ovlmenu.ovl` 里，不需要 json）；没勾 Ultrahand → 不报。

> **注入验证（这轮两次主动 + 一次意外，逐个单独做）**：① 在 `ConfigGenerator` 里跳过哨兵解析（`langRaw.Length > 0 ? langRaw : Resolve(...)`）→ **15 项红，全部是新护栏**（`auto` 与 7 个坏值原样落进 ini、`ZH-CN` 未规范化、基线那条 `default_lang=zh-cn` 也红），而**旧的 772 项一条都没红** —— 实证了此前对「哨兵漏进 ini」完全瞎；② 把 `MapUiLanguageToUltrahand` 的繁体分支改成恒 `zh-cn` → **9 项红，全部是新护栏**；③ **意外获得的天然注入**：`OnComponentPropertyChanged` 里那处钩子被并行 Edit 静默丢掉，结果**只有**「中文界面下勾上 Ultrahand，应自动补勾语言包」一条红，其余 7 条联动断言全绿 —— 这条极具迷惑性，也正好证明那条断言真的挂在组件勾选路径上，而不是恒真。三次都还原后**重新构建**才继续跑（`grep -rn "INJECTION"` 为空）。

> **已知边界（写下来免得下次误以为它证明了什么）**：`OutputValidator` 读的是 `options` 而不是产物 ini，所以注入 ① 时**它没红** —— `options` 里是 `"auto"`、解析后仍是 `zh-cn`，「文件在不在」这个判据依旧成立。这不是漏洞（生成端解析错由 ini 断言抓、校验端解析错由四个方向的告警断言抓），但它证明不了「生成端解析对了」。

2026-09-17 第五版（**引导项图标 `icon`** + 两个新护栏）—— 新增 `CheckBootIcon()`、`CheckXamlBindingsResolve()`、`CheckEverySettingIsPersisted()`，合计 826 → 856：

- **上游事实先核**（两条独立来源互相印证）：`icon=` 与 `logopath=` 同属「stylistic key」一族（上游模板原文：*like logopath= key which is for bootlogo and icon= key for Nyx icon*），都是**引导项级**键、路径按 **SD 卡根目录**算；`icon=` 缺失时 Nyx 按引导项名去 `bootloader/res/<段名>.bmp` 找，再退回内置默认图标。`README_BOOTLOGO.md` 另给出格式约束：启动图必须是 **32 位（ARGB）BMP**、24 位（RGB）不支持、最大 720×1280、先做横图再**逆时针**转 90° —— 顺带修正了本文档此前「只说 BMP」的宽松写法。
- **泛化而不是复制**：`AppendBootLogo` 改造成 `AppendBootImage(sourcePath, BootImageKind kind, …)`，由 `BootImageKind` 这个 record struct 携带 `(IniKey, MissingLogKey, CopyFailedLogKey)`。理由：撞名去重那段逻辑很微妙，复制一份必然在下次改动后漂移，而漂移的后果（A 项的启动图被 B 项的图标**静默**盖掉）在界面上完全看不出来。
- **两个键共用同一张去重表**（`stagedImages`）：两者落在同一个 `bootloader/res/` 目录，撞名规则必须一致 —— 否则「A 项的启动图」会盖掉「B 项的图标」。这是本轮最值得测的一条，专门为它写了**跨键撞名**用例。
- **`CheckXamlBindingsResolve()` 的由来**：做图标界面时想到，XAML 里 `{Binding X}` 写错**不会编译报错**、运行期只静默绑到空值，回归测试也兜不住（它直接 `new MainViewModel()`，不经过 XAML）；唯一兜底是 `--selftest` 的 `binding-errors` 计数，而那要人主动跑、报告还只写 `selftest.log`。名字清单**从程序集反射生成**（§1 教训 4）；遇到护栏不认识的绑定写法（`RelativeSource` / `ElementName` / `Source`）时**报错而不是静默跳过**（§1 教训 2）。实测扫到 **110 个绑定全部可解析**。
- **`CheckEverySettingIsPersisted()` 的由来**：`SaveSettings()` 的写入块与构造函数的读回块都是**手写**的，而本轮刚给图标加了 3 个字段（存与读两侧共 6 行）。漏掉任一处都只在「下次启动」才现形：漏写 → 用户改完一重启就丢；漏读 → 文件里存着而界面显示默认值。判据是「`AppSettings` 的每个标量字段有没有在这两段代码里出现过」，名字清单**从 `AppSettings` 反射生成**，两段代码体按缩进切出来（实测构造函数 112 行、`SaveSettings()` 64 行）并附**前置条件断言**防止切失败后变成空转的恒真断言。
- **只服务于旧存档迁移的两个字段**（`BlankSerial` / `Use90Dns`）走例外清单，且清单本身也被钉住「这些名字必须真实存在」，免得字段改名后清单里留个死名字、照样「通过」。

> **注入验证（这轮两次，逐个单独做）**：① 给图标换一张**独立的**去重表（还原成「两套规则」）→ **5 项红，全部在 `CheckBootIcon`**，含「后来者要加 id 前缀」「前一张的内容不该被覆盖」—— 实测报出的正是那个静默后果：`clash.bmp` 里剩下的是 `CLASH-AS-ICON`，A 项的启动图被 B 项的图标盖掉了；② 把 `BootImageKind.Icon` 的 ini 键误写成 `logopath`（即「复制 Logo 那行忘了改键」）→ **5 项红，同样全在 `CheckBootIcon`**，旧断言一条没红 —— 证明图标断言真的在区分两个键，而不是恒真。两次都还原后 `grep -rn "INJECTION"` 为空并**重新构建**才继续跑。

> **已知边界**：`CheckEverySettingIsPersisted()` 是**源码扫描**而不是行为测试 —— 它证明「这个字段在两段代码里被提到过」，证明不了「提到它的那行真的把值赋对了」。不过 `SaveSettings()` 里那种「写错字段」的错位会立刻被这条抓（写进了别的字段名 ⇒ 自己的名字就没出现过），而值算错的路径另有 `CheckSettingsRoundTrip()` / `CheckOptionValuesPersistence()` / `CheckComponentSelectionPersistence()` 三条往返用例。另：注释行被排除，但**行尾注释**仍会被计入 —— 宁可漏报也不误报。

> **⚠️ 本轮踩到的测试代码缺陷（已修）**：撞名用例里两处裸 `File.ReadAllText(Path.Combine(resDir, …))` 没有存在性守卫。注入 ① 让文件不存在时它抛 `FileNotFoundException` **未捕获**，整个套件崩掉（退出码 127），**后面所有断言的结果全被吞掉**，只能看到一条崩溃堆栈。修法是新增 `ReadFileOrEmpty(absolutePath)`（吃绝对路径的 `ReadOrEmpty` 同族），一并替换掉启动图/图标用例里全部 6 处裸读。教训：**断言「文件内容应等于 …」之前先确认文件在不在** —— 一条断言红不该把整套测试带走。


> 最后那条为什么值得单独写一个测试：`App.RunHeadless()` 是 `private`、还要真开窗口，行为测试够不着它。
> 而一旦有人觉得那行「多余」顺手删掉，**没有任何行为测试会变红** —— 命令行会撞上「一个组件都没勾」的
> 保护分支，提示一句就返回，`run.log` 里没有 Error 级日志，**退出码照样是 0**。整条端到端验证变成空跑，
> 却看起来像通过了。所以这里退一步用源码扫描把调用点和顺序钉死。

**示例输出**：`SwitchCFWizard/samples/` 是上面测试跑出来的完整场景结果（12 份配置文件 + 清单，共 13 个文件，含新增的 `config/ovl-sysmodules/config.ini`），不用运行程序就能直接翻看生成的每个文件长什么样。测试在跑完全部断言后会**再生成一遍完整场景**，所以 `out/` 里留下的就是这套内容 —— `samples/` 直接照抄它即可。
### 当前验证状态

| 验证项 | 方式 | 结果 |
| --- | --- | --- |
| 配置生成正确性 | `ConfigSmokeTest`（离线） | 1703 项断言全通过 |
| 界面与数据绑定 | `SwitchCfwWizard.exe --selftest` | 0 个绑定错误 |
| 真实联网全链路 | `SwitchCfwWizard.exe --run`（在已发布的 `dist/` 上） | 六组都跑过：① 4 个框架组件下载 + 解压 + 生成配置 + 合并 + 打包，0 Error、退出码 0；② 主题组（`NXThemesInstaller.nro` 10394050 字节 + 20 个 `.ips`）；③ 「底层」组；④ 目录槽走**整仓打包**后 20/20 个 `.ips` 全落盘（1 次请求替代 20 次 API 调用）；⑤ 「Ultrahand插件」6 个 —— `out/` **1327 个文件**、顶层正是四个 SD 卡根名，且 `out/switch/.overlays/ovl-KeyX.ovl` 的 sha256 与 `KeyX-CN.zip` 内同名条目**逐字节相同**；⑥ 「学习」7 个 —— 10 个落点全命中、`--absent` 确认 `wiliwili` 没多落一层，且落盘 `switch/linkalho/linkalho.nro` 的 sha256 与 **`linkalho.zip`（中文镜像）** 逐字节相同（**按界面语言换仓库**真的落到了磁盘上）。Warning 视网络而定 —— 代理返回 502 时的重试告警会被「改用 GitHub API asset 端点」救回来，属环境噪声不是产品缺陷 |
| 三语言键集一致性 | `ConfigSmokeTest` 内的 `CheckLocalizationKeyCoverage()` + `CheckNoDeadLanguageKeys()` | zh-Hans / zh-Hant / en-US 均为 428 键（含 `_meta`；非 `_meta` 的 427 个），键集完全相同；代码/XAML 引用的键全部存在（370 个），**且包里没有任何没人引用的死键**（唯一的例外是动态拼出来的 `Component.<Kind>.Name/.Desc`，成员从枚举生成） |

> **「首次运行组件默认不勾」那次改动的端到端情况**（2026-09-16 晚）：**完整跑通**。
>
> 第一次跑了 44 分钟卡在 Ultrahand 的下载上（开发沙箱代理极慢，20 分钟零字节进展），中止后重试 ——
> 第二次 **8 分 41 秒跑完，退出码 0**。日志实证：四个组件**全部 `[x]`**
> （Atmosphere 1.11.2 / Hekate v6.5.3 / Ultrahand v2.5.3+v1.5.3+v2.0.3 / Sys-patch v1.6.2.3），
> 其中 Hekate 走的是简中本地化包 `hekate_ctcaer_6.5.3_Nyx_1.9.3_sc.zip`；
> 生成 9 份配置文件、合并 76 个组件文件、清单登记 88 个文件、完整性校验通过（4 项）。
> `payload.bin` / `bootloader/update.bin` / `hekate_ctcaer_6.5.3.bin` 三者同 md5（`486337b7…`）。
>
> 这条同时验证了本次改动的核心契约：**界面路径不勾、命令行路径自动全选** —— 跑之前
> `settings.json` 里 `Components` 是空的，四个组件能被自动勾上，说明 `App.RunHeadless` 那步生效了。

> **2026-09-17 两项决策 + Ultrahand 修复后的端到端情况**：这次特意在**已发布的 `dist/`** 上跑（而不是 `src/` 的构建产物），并预先在 `settings.json` 里放 `{"OptionValues": {"Ultrahand/installLang": "1"}}`，让**一次运行同时证明两条分支**。
>
> 结果：**退出码 0，0 Error、0 Warning，4 个组件全部 `[x]`**（Atmosphere 1.11.2 / Hekate v6.5.3 / Ultrahand v2.5.3+v1.5.3+v2.0.3 / Sys-patch v1.6.2.3），耗时 39 秒，完整性校验 4 项通过（9 份配置文件 / 1 个 payload / 合并 76 个组件文件 / 清单登记 88 个文件）。
>
> 关键证据：Ultrahand **同时下载了 `sdout.zip` 与 `lang.zip`** —— 既证明「排除语言包」的修复没有影响主路径（`sdout.zip` 仍按名字命中），又**首次端到端验证了 `installLang` 这个开关真的有用**：14 个语言 json 全部落在 `out/config/ultrahand/lang/`，**`out/` 根目录一个 json 都没有**。此前这条落点只有离线用例覆盖，没有真跑过。
>
> 顺带记一条：`--run` 会在 **exe 所在目录**留下 `download/` `out/` `run.log` `settings.json`，发布包验证完必须清掉 —— 否则会把作者本机的设置一起发出去（本次 `dist/` 就出现过一次，已清）。

> **2026-09-17 第四版（`default_lang`）后的端到端情况**：重新发布 `dist/`（自包含、语言包逐字节一致）后跑 `--run`，**退出码 0、0 Error**。
>
> 关键证据 —— **软联动在真实流水线里生效了**：全新 `settings.json`（`installLang` 默认 `"0"`）+ 中文界面下，
> 程序自己把它改成 `"1"`（落盘为 `"Ultrahand/installLang": "1"`），**`lang.zip` 确实被下载**，
> 14 份 json 全部落进 `out/config/ultrahand/lang/`，生成的 `config/ultrahand/config.ini` 里写着 `default_lang=zh-cn`。
> 这一条此前只有离线用例覆盖，没有在真跑里验证过。
>
> ⚠️ **日志里看不到那条「已自动勾选」**（`EnsureComponentsSelected()` 排在 `RunOnceAsync()` 之前，后者开头 `Logs.Clear()`）——
> 判据要看 `settings.json` 与 `download/`，不是看 `run.log`。详见第三节的说明。
>
> 本次 **17 条 Warning 全是沙箱代理的 502 重试**，且每条后面都跟着「改用 GitHub API asset 端点重试…」并且成功了 ——
> 备用通道顺带被真实环境走了一遍（`fusee.bin` / hekate 包 / `sdout.zip` / `lang.zip` / `ovlSysmodules.ovl` / `nx-ovlloader.zip`）。
> 四个组件全部 `[x]`（Atmosphere 1.11.2 / Hekate v6.5.3 / Ultrahand v2.5.3+v1.5.3+v2.0.3 / Sys-patch v1.6.2.3），
> 耗时 9 分 59 秒，9 份配置文件 / 1 个 payload / 合并 76 个组件文件 / 清单登记 88 个文件 / 完整性校验 4 项通过。

> **2026-09-17 第五版（图标 `icon`）后的端到端情况**：四形态发布包与 `dist/` 全部重建，四个形态的 `--selftest` 均 **`binding-errors=0`**（这也顺带验证了新增的 XAML 绑定护栏的前提 —— 它扫出的 110 个绑定名在真实自检里确实是 0 错误）。`dist/` 跑 `--run`：**退出码 0、0 Error / 6 Warning**，耗时 5 分 25 秒，9 份配置文件 / 1 个 payload / 合并 76 个组件文件 / 清单登记 88 个文件 / 完整性校验 4 项通过。6 条 Warning 仍是沙箱代理 502 重试（`nx-ovlloader.zip`、`sys-patch-*.zip` 各 3 条），全部被「改用 GitHub API asset 端点」救回。
>
> **本轮新增的实机证据**（图标功能在真实流水线里的行为）：
> - 本次**没有**配置任何图标，所以 `hekate_ipl.ini` 里 `icon=` 出现 **0 次** —— 未配置就不写键，符合预期（Nyx 会自己按引导项名去找同名 bmp）。
> - 但 `out/bootloader/res/` **确实存在**，里面是 `icon_switch.bmp` 与 `icon_payload.bmp` —— 来自 hekate 组件包自带的默认图标。也就是说 README 第三节写的回退链（`icon=` → `bootloader/res/<段名>.bmp` → 内置默认图）**末两级在卡上是真实存在的**，不是纸面约定。
> - 第四版的软联动没有回归：`"Ultrahand/installLang": "1"`、`default_lang=zh-cn`、14 份 json 全落位。
> （没生成 zip 是**预期**的：全新 `settings.json` 里打包开关默认不勾。）

---

## 八、本次验证中发现并修复的问题

> 本节按发现顺序记录，因此**各条里引用的断言总数、以及单个护栏的条数，都是「当时」的值**，不是当前值：
> 断言总数从 375 → 476 → 502 → 509 → 608 → 619 → 623 → 627 → 634 → 637 → 638 → 745 → 764 → 772 → 826 → 856 → 1067 → 1174 → 1280 → 1353 → 1384 → 1409 → 1524 → 1673 → 1680 → **1703** 一路涨上来（中途删掉过一条恒真的假断言），选项总数从 55 涨到 **76**，各护栏也在持续加项。
> 想看当前值请看第二节（功能说明）与第七节「当前验证状态」。

1. **语言包完全没生效** — `Strings.zh-Hans.json` 这类文件名被 MSBuild 当成「zh-Hans 区域性资源」编进了卫星程序集，主程序集里读不到。已在 csproj 上加 `WithCulture="false"` 修正。
2. **`MainViewModel` 构造顺序错误** — `SelectedLanguage` 的 setter 会调用 `RefreshAutobootChoices()`，而当时 `Components` 还是 `null`，启动即崩溃。问题 1 修好后才暴露出来（此前 `Languages` 为空，setter 提前返回，把 bug 掩盖了）。
3. **`kip1patch=nosigchk` 默认开启是错的** — 该补丁在 hekate 官方 `patches.ini` 里只针对 **FS 1.0.0**，官方明确标注为「过时、如今无用」。用 `pkg3` 启动 Atmosphere 时签名校验由 Atmosphere 自己处理，加它反而会让 hekate 报「补丁未找到」。已改为默认关闭，并把原因写进了界面提示。
4. **补上遗漏的 `nyx.ini` 键 `jcforceright`**（hekate 官方文档里有，之前漏了）。
5. **`nyx.ini` 各选项的说明文案**原本是 `nyx.ini → xxx` 这种占位符，已按官方文档改成实际含义。
6. 新增 `crash.log`：未处理异常会写到运行目录，方便反馈问题。
7. **下载完全没有重试机制** — 端到端实测时一次瞬时的 502 就让整个流程中止。已加上重试（3 次，退避 2s / 4s）+ 45 秒停滞超时 + 人话化错误提示（见上一节）。
8. **补上 `kip1=atmosphere/kips/*` 开关**，并确保它排在 `pkg3` 之后（hekate 按顺序解析）。
9. **错误提示不可操作** — 原来直接把 `The proxy tunnel request to proxy '...' failed with status code '502'` 这种原始异常甩给用户。新增 `NetworkErrors` 统一翻译：502/503 提示换代理、403/429 提示填 GitHub Token、404 提示资源已被删除、超时/连接被拒分别给建议。
10. **一个文件下载失败就中止全部** — 实测中 `fusee.bin` 失败后，Hekate / Ultrahand / Sys-patch 明明没被尝试过，却都显示「失败 0%」。已改为每个组件独立 `try/catch`，失败项汇总成清单，且**跳过配置生成**（避免生成一套残缺配置）。
11. **状态码提取漏判** — 代理隧道失败时 `HttpRequestException.StatusCode` 是 `null`，导致翻译分支走不到。已加正则从异常消息里兜底提取状态码。
12. **`NetworkClientFactory` 是死代码** — `MainViewModel` 里另有一份重复实现，两边的 User-Agent / 代理 / 解压设置还不一致。已统一为前者。
13. **回归测试项目编译不过** — `ConfigSmokeTest` 依赖 `System.Net.Http` 的隐式 using，但 WPF 项目（`UseWPF=true`）生成的全局 using 里**没有** `System.Net.Http`，导致 `HttpRequestException` 无法解析。已改为显式 `using System.Net.Http;`，避免以后再被隐式 using 的差异坑到。
14. **解压日志的文件数口径错了** — `ZipExtractor.Extract` 返回的是压缩包**条目数**（含目录项），日志却写成「共 N 个文件」。Atmosphere 的包 22 个条目里只有 13 个真文件，用户看到的数字虚高近一倍。已改为返回实际写出的文件数。
15. **合并进来的组件文件没进清单**（严重）— `MergeComponentFiles` 只把文件拷进 `out/`，没有记进 `.wizard-manifest.json`。后果有两个：换组件或换版本后，旧文件因为不在清单里**永远清不掉**，会被一起拷进 SD 卡；打包后的完整性校验只覆盖配置文件，会给出「清单中的 9 个文件全部在包内」这种**虚假的安心感**（实际 `out/` 里有 42 个）。已让合并结果一并记入清单，并对同名路径去重。
16. **合并顺序会让组件包覆盖用户勾的配置** — 合并原本排在配置生成**之后**，组件包里的同名文件会赢。Ultrahand 的 `sdout.zip` 里就带 `config/ultrahand/config.ini`，hekate 早期版本的包里带过 `bootloader/hekate_ipl.ini` —— 一旦撞上，用户在界面上勾的引导项和组合键会被悄悄冲掉，且没有任何提示。已把合并提到配置生成**之前**，让生成的配置始终胜出。
17. **「把组件文件合并进 out」默认值错误** — 它原本默认**关**，于是默认产出的 `out/` 里只有配置文件和 payload，**拷进 SD 卡开不了机**。本工具的产出就是「一份能直接拷进 SD 卡的完整内容」，默认值产出一份不能用的东西是自相矛盾的。已改为默认**开启**（`AppSettings` 与 `WizardOptions` 同步改），并在测试里加了 `CheckDefaults()` 把这个决定钉住。已存在 `settings.json` 的用户保留上次的显式选择，不受影响。
18. **测试隐式依赖默认值** — 改动 17 之后暴露出：多个用例靠 `WizardOptions` 的默认值来决定是否合并，默认值一变就有 3 项断言失败，而且失败原因（`Check.Warn.MergeOff` 消失、`Check.Error.MergedNothing` 出现）和用例本意无关。已让每个用例**显式声明** `IncludeComponentFilesInOutput`，测试意图不再跟着默认值漂移。
19. **Sys-patch 强制勾选的触发条件过宽** — 原本是 `BootEmuNand || BlankSerial`，只要勾「虚拟破解系统」就锁死 Sys-patch。按产品决定收窄为**只由「屏蔽序列号」触发**。emuMMC 下是否用 Sys-patch 交给用户自己决定。
20. **README 承诺的「黄色提示」根本没实现** — 文档写着锁定后会「右侧出现黄色提示」，但 `Component.SysPatch.ForcedHint` 这个多语言键虽然三语齐全，**在 XAML 里从未被绑定**。结果是复选框变灰、用户完全不知道为什么。已给 `ComponentViewModel` 加 `LockHintKey` / `LockHint` / `HasLockHint`（存 key 而非文案，切语言能跟着刷新），并在组件卡片模板里绑定显示。
21. **「可见性」与「锁定条件」不一致会造出死局** — 屏蔽序列号的复选框只在勾了「真实破解 / 虚拟破解」时才可见（`ShowSerialOptions`）。如果锁定条件只看「屏蔽项是否勾着」而不看可见性，就会出现「两个破解引导都不勾 → 屏蔽项被隐藏 → 但 Sys-patch 仍被锁死」的死局：用户看不见那个能解锁的勾，也就永远解不开锁。已让 `SysPatchForced` 的每一项都**同时**要求对应的引导模式可见。
22. **程序自动勾选引发连锁，把两个屏蔽项一起打开**（严重）— 需求②（屏蔽任一项 → 强制 Sys-patch）与需求③（勾 Sys-patch → 默认勾上两个屏蔽项）方向相反。程序为满足②而自动勾上 Sys-patch 时，会触发③的「默认勾上屏蔽项」逻辑，把两个屏蔽项**一起**勾上 —— 结果是「勾一个屏蔽项 = 两个都勾」，需求①的「想屏蔽什么就屏蔽什么」被彻底废掉。已加 `_applyingForcedSelections` 重入保护标志位，区分「程序勾的」与「用户勾的」，只有后者才触发默认逻辑。
23. **旧 `settings.json` 里的屏蔽设置会被静默关掉**（严重）— 把单个 `BlankSerial` 拆成 `BlankSerialSysmmc` / `BlankSerialEmummc` 后，旧文件里的 `BlankSerial` 无人读取，升级后用户原本开着的屏蔽会变成「两项都关」。屏蔽序列号是**隐私相关设置**，静默失效不可接受（用户以为还开着，实际序列号已经暴露）。已保留 `bool? BlankSerial` 作旧格式标记 + 一次性 `Migrate()`（迁移后置回 `null`，保证只迁移一次，也保证此后用户的新选择不会被旧值反复覆盖），四种情形（`true` / `false` / 新格式 / 全新安装）由 `CheckSettingsMigration()` 覆盖。
24. **迁移只验了一半**（验证盲区）— `CheckSettingsMigration()` 只验到 `SettingsStore.Load()` 为止。但用户看到的是**界面**：若 `MainViewModel` 构造函数漏读新字段，迁移再正确也没用 —— 配置里写着 `blank_prodinfo_*=1`，界面却显示两项都没勾，属静默不一致。已补 `CheckMigrationThroughViewModel()`，走完整链路「旧 `settings.json` → `Load()` → `MainViewModel` → 界面勾选状态 + 强制联动结果」。
25. **界面状态 → `WizardOptions` 的装配完全没有测试**（验证盲区，最隐蔽）— `BuildWizardOptions()` 是 `private`，唯一调用方 `RunAsync` 必须联网下载，离线测试够不着。也就是说：把 `BlankSerialEmummc = BlankSerialEmummc` 手滑写成 `= BlankSerialSysmmc`，**原有的 252 项断言会全部通过** —— 界面看着正常，生成的 `exosphere.ini` 却把两个序列号都抹了，而用户明明只勾了一个。已用 `InternalsVisibleTo` 把该方法开放为 `internal`，新增 `CheckViewModelToOptionsMapping()`：刻意用「一开一关」的**不对称取值**（对称取值测不出这类错位），既断言 `WizardOptions` 的字段，也断言最终写出的 `exosphere.ini` 内容。**已用注入 bug 的方式验证过这条测试确实会失败**（4 项断言报错），不是摆设。
26. **保存方向同样没有测试，而且错得更久**（验证盲区，跨进程）— `SaveSettings()` 也从未被任何测试执行过。它和上一条是同一类风险，但写的是**磁盘文件**，错了会跨进程留下来。最要命的一种：保存时「顺手」把旧字段也同步上（`_settings.BlankSerial = BlankSerialSysmmc || BlankSerialEmummc`）—— 看着像是在维护向后兼容，实际会让下次启动的 `Migrate()` **再跑一次**，拿旧值把两个屏蔽项一起盖成 `true`，用户「只屏蔽一个」的选择被悄悄改回「两个都屏蔽」。已新增 `CheckSettingsRoundTrip()`，走**公开路径**做完整往返（改设置 → 自动落盘，无需再开测试接缝；原先是点那个「保存设置」按钮，该按钮已于 2026-09-19 按用户要求取消，见第八节第 85 条），并显式断言旧字段不会被写回成 `true`。**同样用注入 bug 验证过**：3 项断言报错，其余 264 项照常通过。
27. **`autoboot` 存的值每次启动都被清成「关闭」**（真 bug，用户可见，由新增测试查出）— 这一条不是「测试没覆盖」，而是**测试一加上去就抓到的真实缺陷**。把全部 55 个组件配置项逐个往返后发现只有 `Hekate/autoboot` 过不去：存的是 `"3"`（自动进入虚拟破解系统），读回 `"0"`（关闭）。根因有两层：
    - `autoboot` 的静态定义里 `Choices` 只有 `["0"]` 一项，那只是**占位** —— 真正的候选项由 `MainViewModel.RefreshAutobootChoices()` 按当前勾选的引导项动态生成。而 `OptionViewModel` 构造函数会拿静态列表做「值不在候选项里就回退到第一项」的自愈，于是存档里的 `"3"` 在构造那一刻就被改成 `"0"`，等真正的候选项建出来时值已经没了。
    - 构造函数里 `SelectedLanguage` 的 setter 会调用 `RefreshAutobootChoices()`，而当时 `_bootStock` / `_bootSysNand` / `_bootEmuNand` 还没赋值（字段默认 `false`），重建出来的候选项只有「关闭」一项，进一步坐实了这次误改。
    - 更隐蔽的是：`settings.json` 里的 `Hekate/autoboot` 一直是 `"3"`（setter 保存的是没被改动的 `_settings.OptionValues`），**界面显示「关闭」而文件写着 `"3"`**，两者长期不一致，直到用户某次落盘（当时是点那个「保存设置」按钮）才把 `"0"` 永久写回去。
    - 修法：给 `OptionDefinition` 加 `DynamicChoices` 标记，`autoboot` 标上；构造时对这类选项**原样保留值**，自愈交给重建候选项那一步；同时把引导项状态的赋值挪到 `SelectedLanguage` 之前。并加了反向护栏：越界值（`"99"`）仍必须被纠正回 `"0"`，避免「保留值」变成「什么垃圾都留着」。
28. **改动引导项会让 `autoboot` 静默换一个系统**（真 bug，承接第 27 条）— 修完 27 之后顺着同一处继续查，发现 `autoboot` 的编号是**位置相关**的：`0` = 关闭，之后按「正版 / 真实破解 / 虚拟破解」里**当前勾着的**顺序依次编号。原实现重建候选项时只保留「旧数字还在不在新列表里」，于是取消勾选靠前的一项后会出现两种错：
    - 旧编号**越界** → 被重置成「关闭」，用户设的自动引导项凭空消失；
    - 旧编号**仍合法但含义变了** → **用户要「真实破解」，实际自动进入「虚拟破解」**。
    - 实测确认了后一种：三个引导项全勾时 `autoboot=2` 表示「真实破解系统」；取消「正版系统」后值仍为 `2`，但此时 `2` 已经是「虚拟破解系统」——**没有任何提示地换了一个系统**。
    - 修法：重建候选项时先把旧编号按**当时的启用状态**反查成「指向哪个系统」，再按新排列把**同一个系统**重新编号；只有被指向的系统本身被取消勾选，才回落到「关闭」。为此记住上次构建候选项时的启用状态（`_autobootChoiceBasis`）。测试覆盖取消/恢复勾选、目标系统自己被取消、反复切换、三个全不勾共 7 种情形。
29. **选项定义缺一道静态护栏**（预防性护栏，承接第 27 条）— 第 27 条的根因其实在**定义层面**：`autoboot` 的静态 `Choices` 只有 `["0"]` 这一个占位值，`DefaultValue` 恰好也是 `"0"`，于是「默认值必须落在候选项里」这条不变量表面上成立、实际上毫无保障。任何一次手滑（把 `DefaultValue` 写成 `"9"`、或给某个下拉框删光 `Choices`）都不会在编译期或启动期报错，只会等到运行期被 `OptionViewModel` 的自愈逻辑悄悄改成第一项 —— 和第 27 条一样是**静默**的。已新增 `CheckOptionDefinitionInvariants()`：遍历全部选项（**新增选项会被这条自动覆盖，不用手工补测试**），静态校验「`DefaultValue` 必须落在 `Choices` 里」+「下拉框必须有候选项（`DynamicChoices` 除外）」。**已用注入 bug 验证过这条测试确实会失败**：把 `autoboot` 的 `DefaultValue` 改成 `"9"` 后，该检查精确报出 `Hekate/autoboot 的默认值「9」不在候选项里`，其余 292 项照常通过。
30. **组件勾选状态的持久化整条链路没有测试**（验证盲区）— 前面 24～26 条把「屏蔽序列号」「组件配置项」的读写都补上了，但同一份 `settings.json` 里的 `Components`（勾了哪几个组件）一直是空白。它的读写分处两地、且都用 `ComponentKind` 的**名字**当键：保存侧 `Components.ToDictionary(c => c.Kind.ToString(), c => c.IsSelected)`，加载侧 `_settings.Components.TryGetValue(kind.ToString(), out var s)`。一旦两处写法不一致（或有人改了枚举成员名），**所有勾选会静默复位成「一个都没勾」** —— 用户每次打开程序都要重新勾一遍，不报任何错。已新增 `CheckComponentSelectionPersistence()`，覆盖不对称勾选往返、反向取值、`CanStart` 随勾选立刻变、以及 `EnsureComponentsSelected()` 的两条边界。

    - 顺带钉住一个**容易被当成 bug 改掉的设计**：`EnsureComponentsSelected()` 的判断依据是 `_settings.Components`（文件里存了什么），**不是**当前的 `Components` 状态。两者不等价 —— 构造函数末尾的 `UpdateForcedSelections()` 会在用户上次开着任一「屏蔽序列号」时把 Sys-patch **强制勾上**（需求②在重启后同样生效），此时实时状态非空、但文件里从没被显式勾过。按实时状态判断就会漏掉首次运行的默认全选。测试里专门留了一条断言证明「实时状态已有强制勾选时，仍应触发默认全选」。
    - 同理，保存时**未勾选的项必须显式写成 `false`**，不能只写 `true`。省略的话「用户主动取消全部勾选」和「从来没勾过」在文件里长得一模一样，而这两者的正确行为**相反**：前者要尊重（保持全不勾），后者要默认全选。
    - **两种注入 bug 各验证一次**：① 保存时改成只写勾上的项 → 精确报出「未勾选的组件应以 false 显式落盘」，其余 312 项照常通过；② 加载端改用枚举数值当键 → 报出 2 项往返不一致（`Atmosphere 应为 True 实际 False`），其余 311 项照常通过。
    - 另外修正了 `EnsureComponentsSelected()` 上一段**已经过时的注释**：它原本写「因为『虚拟破解系统』默认开启，构造函数会先把 Sys-patch 自动勾上」，但需求②后来已收窄为**只由「屏蔽序列号」触发**，虚拟破解系统本身不再触发强制勾选。结论（要读文件而不是读实时状态）仍然成立，但理由必须换掉，否则下一个读代码的人会顺着错误的理由把它「修正」回去。

31. **六处产物落盘路径错位**（真 bug，用户报告）— 上游各发布包的内部结构并不统一，而旧代码靠「按文件后缀猜 + 一律平铺到 `out/` 根目录」糊过去：

    | # | 现象 | 根因 |
    | --- | --- | --- |
    | 1 | `sdout.zip` 解压后在 `out/` 里多套一层 `sdout/` | 解压时「一个组件有多个 zip 就各解到以文件名命名的子目录」，之后又把该目录整棵铺到 `out/` 根 |
    | 2 | `nx-ovlloader.zip` 同样多套一层 `nx-ovlloader/` | 同上 |
    | 3 | `ovlSysmodules.ovl` 被平铺在 `out/` 根目录 | 裸文件没有落点概念，一律铺根目录；正确位置是 `switch/.overlays/` |
    | 4 | `lang.zip` 里的 14 个裸 json 散落在 `out/` 根目录 | 同上；正确位置是 `config/ultrahand/lang/` |
    | 5 | `fusee.bin` 被平铺在 `out/` 根目录 | 同上；正确位置是 `bootloader/payloads/`（hekate 的 payload 菜单目录） |
    | 6 | hekate payload 只以原名躺在 `out/` 根目录 | 同上；正确位置是根目录 `payload.bin` **和** `bootloader/update.bin`（两处都要） |

    已改成**落点跟着资源走**：每个下载物在选取的那一刻（`ComponentCatalog` 的 picker 里）声明自己的 `AssetPick.Targets`，再由 `OutputStager` 把下载物先摆成「相对 `out/` 根目录」的样子，最后 `ConfigGenerator` 整棵合并进 `out/`。两个下游环节都不再认识任何具体文件名。

    顺手补掉两个**同源的隐藏问题**：
    - **顺序**：hekate 的发布包里自带一份 `bootloader/update.bin`（标准 4G 版），而界面上可能选的是 8G 版 payload。payload 的摆放必须排在组件合并**之后**，否则包里的那份会把选中的版本盖掉 —— 界面显示 8G、机器实际按 4G 起，完全静默。已把顺序写进代码注释并由测试盯住。
    - **升级路径**：旧版本已经把 `sdout.zip` 解到 `download/Ultrahand/unpacked/sdout/`。新版若只是「换个地方解压」，那两棵旧目录树会**继续**被合并进 `out/` —— 用户升级后看到的还是老毛病。现在每次重新暂存前先清空暂存区（`OutputStager.StageComponentAsync` 内部完成，和暂存绑在同一个入口上，避免落进「测试够不着 `MainViewModel`」的盲区），并且清单清理会把**被清理清空**的目录一并删掉，不留空文件夹。

    `CheckOutputStaging()`（61 项）逐条盯住上述每一条，并且**落点声明取自真实的 picker**（喂假 `ReleaseInfo`）而不是在测试里手写 —— 手写的话 catalog 里把落点写错测试照样过。另外单独用**真实上游发布包**（Atmosphere 1.11.2 / Hekate 6.5.3 / Ultrahand 2.5.3 含 `sdout.zip`+`nx-ovlloader.zip`+`lang.zip` / Sys-patch 1.6.2.3）跑了一遍暂存 + 生成，逐字节核对 `payload.bin` 与 `bootloader/update.bin` 就是选中的 8G 版 payload，结论一致。

    - **五次注入 bug 各验证一次**：① 让 `nx-ovlloader.zip` 声明一个多套一层的落点 → 精确报出「`out/` 里不应出现 `nx-ovlloader/` 这一层」，其余 348 项照常通过；② 把 payload 摆放挪到组件合并**之前** → 报出「`bootloader/update.bin` 应被 8G 版 payload 覆盖，而不是留着组件包里的标准版」；③ 去掉 `fusee` / `lang` / `ovlSysmodules` / `ovlmenu` 的落点声明 → 一次报出 11 项（覆盖第 3、4、5 条）；④ 去掉 hekate payload 的双落点声明 → 报出 7 项（覆盖第 6 条）；⑤ 撤掉「清空暂存区」→ 报出 4 项，正是升级路径上的症状（`sdout/`、`nx-ovlloader/` 又冒出来了）。
    - 其中 ② 第一次注入**没被抓住**：当时只是在组件合并之前**新增**了一次摆放，后面那次（正确顺序）还在，后者覆盖了前者 —— 测的是「重复调用」而不是「顺序」。改成「删掉正确那次、只留错误那次」后才精确报错。**注入验证必须真·替换，并确认报错项数落在预期的那几条上。**

32. **反复运行会在运行目录堆下一串旧压缩包**（真实端到端跑完才发现的瑕疵）— 每次生成都会产出一个 `SwitchCFW-<时间戳>.zip`（约 7 MB），但旧的一直不清理。实测连着跑两次就留下两个。对「改一次配置跑一次、来回对比」这种用法，运行目录很快就堆满了。

    已改为：打包**成功后**清掉同目录下早先由本工具生成的压缩包，只留刚生成的这一个。两个刻意的选择 ——

    - **放在打包成功之后**做，而不是之前：万一这次打包失败，上一次的压缩包还在，不至于两头落空。
    - **只认 `SwitchCFW-yyyyMMdd-HHmmss.zip` 这个精确形状**（前缀 + 8 位日期 + `-` + 6 位时间 + 后缀）。用户自己改名保存的、或其它工具生成的压缩包一律不碰 —— 「清理」绝不能变成「删用户文件」。

    护栏 `CheckArchivePruning()`（7 项）：造出两个旧压缩包 + 三个不该被碰的文件（一个改了名的、一个形状不符的、一个别家工具的），打包后断言旧的两个被清掉、另外三个原样保留。**注入验证过**：注释掉清理调用 → 精确报出 3 项（两条「应被清掉」+ 一条「清理后应只剩本次生成的那一个」，实际列出了 4 个），其余 357 项照常通过。

33. **8G 运存模式下 out 根目录还留着标准版（4G）hekate payload**（真 bug，用户报告）— hekate 的 Nyx 包里**同时**带着 `bootloader/update.bin` 和根目录的 `hekate_ctcaer_<版本>.bin`，整棵铺开之后根目录就多出一份 4G 版。用户选了 8G 之后，这个文件既不是正在用的那份、名字又只差一个 `__ram8GB` 后缀，肉眼几乎分不出 —— 很容易让人以为 8G 没生效。

    已改为：**只要开着 8G 运存**，就把 out 根目录里名字匹配 `hekate_ctcaer_*.bin` 且不含 `ram8GB` 的文件删掉，并把它从清单里一并抹掉（不抹的话打包校验会报「清单中的文件不在包内」这种自相矛盾的结论）。标准版本身仍保留在 `bootloader/payloads/`，hekate 的 payload 菜单里可以直接选中切回 4G。

    护栏是 `CheckOutputStaging()` 里新增的几条：8G 模式下根目录的标准版必须消失、清单里也不能再有它，而 `bootloader/payloads/` 那份仍要在清单里；4G 模式下根目录那份则必须保留。为此把假 hekate 包也改成了**和真实 Nyx 包同构**（根目录带一份标准版 payload）。**注入验证过**：注释掉删除调用 → 精确报出 3 项，其余 373 项照常通过。

34. **全新安装时界面四个组件一个都没勾，「开始」按钮是灰的**（真 bug，由新增测试查出，正好命中「打开软件就按我的配置勾好」这条需求）— 默认值当时已经全部改对了，但**界面根本不看那些默认值**：

    > ⚠️ **这条已被第 37 条推翻**（2026-09-16 需求变更）：用户后来要求首次运行就是「一个都不勾」。
    > 下面保留当时的判断与修法原样不改（历史记录就该是当时的事实），但请注意「默认全选」现在
    > **只在 `--run` 路径生效**，界面路径已改回不勾 —— 详情见第 37 条。

    - `EnsureComponentsSelected()`（首次运行时默认全选组件）的**唯一调用点**在 `App.xaml.cs` 的 `RunHeadless()` 里 —— 也就是**只有 `--run` 命令行模式会默认全选**。正常双击启动走的是 `window.Show()`，那条路径从不调用它。
    - 而 `MainViewModel` 构造函数里 `vm.IsSelected = _settings.Components.TryGetValue(...) && selected`，全新安装时字典是空的 → 四个组件全是 `false` → `CanStart` 为 `false`，**「开始」按钮是灰的，用户必须先自己勾一遍**。

    修法有两处，第二、三处是顺着查出来的**更隐蔽的连带缺陷**：

    - **① 调用点搬到构造函数末尾。** 界面启动、`--selftest`、`--run` 三条路径都经过构造函数，所以只在这里实现一次就够了；`App.xaml.cs` 里那次 `--run` 专用调用随之删掉（否则重复，而且它写的是硬编码中文、不走多语言）。顺带补了语言键 `Log.ComponentsDefaultSelected`。
    - **② 默认全选必须走重入保护。** 默认全选是**程序**行为，但它把 Sys-patch 勾上会触发「用户手动勾 Sys-patch → 默认勾上屏蔽序列号」的**软默认**联动 —— 后果是用户从没勾过的屏蔽项被程序悄悄打开，生成的 `exosphere.ini` 里多出 `blank_prodinfo_*=1`，还会把 Sys-patch 一起锁死，全程**静默**。修法是把默认全选的循环包进已有的 `_applyingForcedSelections` 重入保护里（该字段原先只管「强制勾选」那一处，现在两处共用，文档注释一并改写）。
    - **③ 判据从「有没有哪个是 `true`」改成「有没有勾选记录」。** 原来用 `_settings.Components.Values.Any(v => v)` 判断，这会把「用户主动取消全部勾选」（文件里是四个显式 `false`）和「从来没勾过」（文件里根本没有 `Components` 段）混为一谈 —— 而这两者的正确行为**相反**：前者要尊重，后者才默认全选。以前这个错只在 `--run` 里发作、不容易被看见；现在它每次启动都跑，就会把用户明确表达的「我什么都不要」悄悄改回全选。改成 `_settings.Components.Count > 0` 即可。

    **验证**：新增 `CheckFreshUiState()`（删掉 `settings.json` 后真的构造一次 `MainViewModel`，逐项核对界面打开时的状态），并把 `CheckComponentSelectionPersistence()` 的边界用例重写成「文件里没有 `Components` 段」与「用户主动取消全部勾选」两种情形。**注入验证过**：把重入保护去掉 → 精确报出 4 项失败（`旧配置为关时，界面两项都不应勾选`、`旧配置为关时不应强制 Sys-patch`，以及两条组件勾选往返），其余断言照常通过；加回去即 509 项全绿。

35. **有一处校验提示会把原始键名直接显示给用户**（真 bug，由新增护栏查出）— `OutputValidator` 在「90DNS hosts 文件缺失」这条分支上引用了 `Check.Error.MissingConfigFile`，而这个键**三份语言包里都没有**。`LocalizationService` 缺键时的行为是**把键名本身返回**，所以界面上会赫然显示一串 `Check.Error.MissingConfigFile`，后面跟着缺哪些文件。编译通过、启动自检通过、509 项断言全绿 —— 因为没有任何一条断言检查过「引用的键是否真的存在」。

    修法：把键名改成与语义相符的 `Check.Error.MissingHosts`（这条说的是 hosts 文件，不是通用配置文件），三份语言包补上文案。

    更重要的是**补一道总护栏**，否则同类问题还会再出现：新增 `CheckLocalizationKeyCoverage()`，从源码里把所有 `loc:Loc X`（XAML）、`LocalizationService["X"]`、以及形如 `"Log.Xxx"` / `"Check.Xxx"` 的字符串全收集起来（本次 325 个），逐个确认三份语言包都能取到文案；另有一条补扫「动态拼出来的键」（`"Component." + kind + ".Name"` 这类正则够不着的）。以后加新文案忘了补语言包，会立刻在这里报出来，而不是等用户看见一串英文点号。

    > 写这条护栏时先踩了一次自己的坑：正则最初用 `[A-Za-z0-9_.]+` 收尾，于是 `"Component." + definition.Kind + ".Name"` 的**前半截**被当成了一个键（`Component.`）而误报。改成「每个点分段都必须非空」后正常。

36. **中文界面下 hekate 的 payload 会「凭空消失」**（设计缺陷，随新功能一起发现）— 为了让 hekate 自带的图形界面（Nyx）也是中文，语言为简/繁中文时改为从 `easyworld/hekate` 下载汉化包。但查过该仓库的实际资源后发现：它**只发 zip、不发独立的 `.bin`**（官方 `CTCaer/hekate` 是把 payload 单独挂一份的）。而「把 payload 放到对应目录」那一步是靠 picker 挑 `.bin` 资源实现的 —— 于是走本地化源时这一步无货可摆，用户会以为「换个语言把 payload 弄丢了」。

    修法：在组件合并之后加一步 `EnsureRootPayloadBin` —— 根目录没有 `payload.bin` 时，从包内自带的 `hekate_ctcaer_<版本>.bin` 复制一份过去（名字不同：包内是带版本号的，hekate 约定根目录叫 `payload.bin`）。三条保守规则：**只在恰好一个候选时动手**（多个候选说明情况不明，与其猜一个不如不做 —— 猜错等于把一个错的 payload 当启动项）、**已存在 `payload.bin` 时绝不覆盖**（官方源 / 8G 模式摆好的那份才是对的）、复制成功要 `AddProducedFile("payload.bin")` 记进清单（否则下次生成会被当垃圾清掉）。护栏 `CheckPayloadBinFallback()` 5 项。

    另外 8G 运存**不走本地化源**（本地化仓库里没有 `__ram8GB.bin`），这条由 `CheckLocalizedHekateSource()` 的 19 项断言钉住 —— 其中专门有一条防「繁体判成简体」：`zh-Hant` 也以 `zh` 开头，判断顺序反了繁体用户会**静默**拿到简体界面。

    注：这也解释了为什么之前的测试没抓到它 —— `CheckDefaults()` 测的是 `AppSettings` / `WizardOptions` 的**对象默认值**，`CheckUserConfigBaseline()` 测的是**生成出来的 ini 内容**，两者都绕开了「界面刚打开时那些勾到底勾没勾」。`CheckFreshUiState()` 补的就是这个缺口。

37. **首次运行的默认勾选行为，按用户要求整体反了过来**（需求变更，2026-09-16）— 第 34 条当时把「打开软件时组件全不勾、开始按钮是灰的」当成 bug 修掉了，修法是「调用点搬到构造函数末尾 → 首次运行默认全选」。用户后来明确要求**反过来**：首次运行**一个组件都不勾**，由用户自己挑；但**组件的默认配置选项要保留**（勾上就能用，不必重设一遍）。

    这不是把第 34 条简单回滚 —— 那样会把 `--run` 一起打回原形。命令行是无人值守路径，没有界面可以勾，一旦不自动选就会撞上 `RunAsync` 里「一个组件都没勾」的保护分支，提示一句就返回。**整条端到端验证变成空跑，而 `run.log` 里没有 Error 级日志，退出码照样是 0** —— 看起来像通过了，这是最危险的一种失败。

    最终做法是把「默认全选的能力」和「界面的默认状态」拆开：

    - **界面路径**（`MainViewModel` 构造函数）**不再调用** `EnsureComponentsSelected()` → 首次运行全不勾。组件内部的选项仍然照 `ExtractSavedValues` 填好目录默认值 ——「不勾组件」和「选项没有默认值」是两件事，别一起做掉。
    - **命令行路径**（`App.RunHeadless`）**补上** `EnsureComponentsSelected()`，且必须排在 `RunOnceAsync()` 之前（`RunAsync` 一开头就读勾选状态，晚了等于没勾）。调用点回到第 34 条之前的位置，但理由完全不同：那时是「只有 `--run` 能用」，现在是「界面故意不用」。
    - **补一句界面提示**：按钮灰着就点不动，`RunAsync` 里那句 `Common.NoSelection` 永远弹不出来，用户只能对着灰按钮猜为什么。所以组件列表上方直接显示「请至少勾选一个组件」（新属性 `ShowNoSelectionHint`），勾上任意一个就消失。

    护栏：`CheckFreshUiState()` 里「四个组件一个都不勾」「`CanStart` 不可用」「提示要显示」三条改写；`CheckComponentSelectionPersistence()` 的 C 段改成「先断言界面路径不该勾 → 再显式调 `EnsureComponentsSelected()` 验证全选」两步走；另新增 `CheckFirstRunSelectionContract()`（10 项）专钉这条契约。

    > 其中**源码契约核对**那条是必需的：`App.RunHeadless()` 是 `private`、还要真开窗口，行为测试够不着它。
    > 有人顺手删掉那行调用，**不会有任何行为测试变红**，而后果是端到端静默空跑。所以退一步扫描
    > `App.xaml.cs`，确认「调用存在」且「排在 `RunOnceAsync()` 之前」。
    >
    > 一条经验：这次改的是**默认值语义**而不是新功能，所以动手前先把「哪些断言在守这个默认值」grep 出来
    > （`IsSelected` / `CanStart` / `EnsureComponentsSelected` 三个词就够），一次改完再跑 ——
    > 否则白等一轮两分钟的回归。

38. **「界面设置 → `WizardOptions`」的接线只有定点测试，整行漏掉无人发现**（验证盲区，2026-09-16）— 第 25 条用 `CheckViewModelToOptionsMapping()` 补上了「两个屏蔽项在装配时不能串」这个**具体**错位，但它只点名了几项。真正的缺口是：`BuildWizardOptions()` 里**整行漏掉**某个设置时，没有任何用例会红。

    用注入 bug 实测确认了这个缺口：把 `IncludePayloads = IncludePayloads,` 那一行注释掉后跑回归 —— **623 项里只有新增的这一条失败**，其余 622 项全绿。也就是说在这个护栏出现之前，「用户在界面上关掉『摆放 payload』、生成结果照旧摆放」是完全静默的。

    新增 `CheckEverySettingIsWiredIntoOptions()`（4 项）。做法是反射 + 自维护，**不点名任何设置项**：

    - 把界面上每个「`WizardOptions` 里有同名属性」的开关翻成**与默认值相反**的值（本次 17 项）；
    - 装配后逐项断言它确实翻过来了 —— 漏接的会停在默认值，立刻暴露（**注入验证过：4 项里恰好报 1 项，且指名道姓写出 `IncludePayloads`**）；
    - 反向再扫一遍 `WizardOptions` 的每个标量属性，看还有哪个**没有任何界面来源**（将来新增属性忘了接，这里会报警）；
    - 例外清单（`Language` 来自界面语言、四个组件布尔来自组件勾选状态）本身也钉住「名字必须真实存在」，否则属性改名后清单里留个死名字，照样「通过」。

    > 一条经验：**「新增 X 时要记得同时改 Y」这种约定，只要没有穷举断言，迟早会漏。** 在此之前
    > `CheckDefaults` / `CheckFreshUiState` / `CheckUserConfigBaseline` 三条护栏把**默认值**守得很死，
    > 但**接线**只有定点覆盖 —— 而「默认值对」和「接线对」是两件事：前者保证全新安装可用，
    > 后者保证用户改了开关真的生效。补的时候优先选**反射 / 遍历**而不是再加几条点名断言，
    > 这样将来新增设置项会被自动覆盖，不用回来补测试。

    > 另一条：这轮审计还顺手核对了 `AppSettings` 与 `WizardOptions` 的**同名属性默认值**
    > （19 个可比属性，18 项一致，唯一不同的 `Language` 是有意为之 —— `WizardOptions` 用空串表示
    > 「跟随当前界面语言」，实际值由 `BuildWizardOptions()` 填）。结论是**没有漂移**。
    > 顺带纠正上一轮的一个说法：`_autoPackZip` 那个 `= true` 初始值虽然与权威默认值相反，
    > 但 `CheckFreshUiState()` 里 `Assert(!vm.AutoPackZip, …)` 已经守着，删掉构造函数里的赋值会立刻报错 ——
    > 所以它是**可读性问题**（写了个会被误读的初值），不是安全漏洞。改成不写初值仍然是对的。

39. **「配置项声明了落点、却没写出去」同样无人发现**（验证盲区，2026-09-16 收尾）— 顺着第 38 条那条线继续查「同一类缺口还有没有别处」，下一个就是落点：`OptionDefinition.TargetFile` 声明这个选项写进哪个文件，`ConfigGenerator` 按落点遍历去写。

    缺口在于：**声明了一个新落点（比如 `bootloader/foo.ini`）、却没有对应的写出循环** → 选项在界面上能改、也存进了 `settings.json`，生成结果里**一行都没有**。而 `CheckFullScenario` 里的 `WrittenFiles.Count == 12` 是**计数**断言 —— 少写一个文件数量不变，挡不住它。（`TargetFile` 在测试里此前只有**一处定点断言**：Ultrahand 那条 `memoryOption.TargetFile == "config/ultrahand/config.ini"`。）

    新增 `CheckEveryDeclaredTargetFileIsWritten()`（4 项），双向穷举：

    - **正向**：目录里声明的全部 `TargetFile`（本次 **9 个**）都必须出现在产出的配置文件集合里；
    - **反向**：产出的每份 `.ini` 都应有目录选项声明它（防止写了没人声明的文件，多半是改名时漏改了一边）；
    - 复用 `CheckFullScenario` 的产出（`_fullResult`），不重新生成、不碰 `out/`；
    - 比对前**归一化路径分隔符**（`TargetFile` 用 `/`，清单历史上用 `\` —— 这是本项目的老坑）。

    `TargetFile == null` 不算漏：那是「本就不写进配置文件」的**显式**标记，`OptionDefinition` 的注释里写明了。

    **注入验证**：把 `themebg` 的落点从 `bootloader/nyx.ini` 改成 `bootloader/nyx2.ini`（没有写出循环）→ 回归 **626 通过 / 1 失败**，报「实际没写出的 1 个：`bootloader/nyx2.ini`」。**只有新护栏响**，而且 `WrittenFiles.Count == 12` 那条计数断言**照样通过** —— 两点都印证了缺口真实、且此前无人覆盖。

    > 一条经验：**「计数断言」不是完整性断言。** `Count == 12` 只说明数量对，不说明**是那 12 个**。
    > 凡是「一组东西必须齐全」的契约，都要比**集合**而不是比**个数**。

40. **「某一份语言包单独缺键」被索引器的回退掩盖了**（验证盲区，2026-09-16 收尾之三）— 继续顺着「同一类缺口还有没有别处」往下查，这一处落在多语言上，而且**旧护栏的断言名字就写着它要守的那个属性**。

    `LocalizationService` 的索引器在缺键时**回退到默认语言（`zh-Hans`）**，只有默认语言也没有才返回键名本身。而 `CheckLocalizationKeyCoverage` 的判据是 `text == key` —— 于是：

    | 情形 | 索引器返回 | 旧护栏 |
    | --- | --- | --- |
    | 三份包都缺 | 键名本身 | 报错 |
    | 只有 `zh-Hans` 缺 | 键名本身 | 报错 |
    | **只有 `zh-Hant` / `en-US` 缺** | **简体中文文案** | **静默通过** |

    也就是说：往 `Strings.zh-Hans.json` 和 `Strings.en-US.json` 加了键、忘了 `zh-Hant`，护栏全绿，而**繁体用户看到的是简体文案**。

    新增 `CheckLanguagePackKeyParity()`（7 项），**直接读三份 JSON 的原始键集**（刻意不经索引器）：

    - 三份包的**键集必须完全一致**（比集合、不比个数 —— 少一个键和换一个键，个数都看不出来）；
    - 源码引用的 325 个键**逐包直接查原始键集**；
    - 加了**前置条件断言**（最少的一份包必须有 200+ 键），否则解析失效时两条集合断言会恒真、变成橡皮图章；
    - 顺手把「键长什么样」的正则抽成 `CollectReferencedKeys()` 单一来源 —— 原来它内联在旧护栏里，再抄一份迟早漂移成一条松一条紧。

    **注入验证**：只从 `Strings.zh-Hant.json` 删掉 `Log.ComponentsDefaultSelected`（另两份不动）→ 回归 **633 通过 / 1 失败**，精确报出「`zh-Hans` 有而 `zh-Hant` 无：`Log.ComponentsDefaultSelected`」。

    最值得记的一点：**旧护栏那条断言的名字就是「源码里引用的 325 个语言键三份语言包都要有文案」，而它在这种情况下照样全绿。** 断言名字声称的属性，未必是断言真正验证的属性 —— 这种「名义上覆盖了、实际上没覆盖」的护栏比没有护栏更危险，因为它会让人放心地不再去看。

    > 一条经验：**「查不到就回退」这种设计会把缺口从「报错」降级成「串味」，而任何走这条回退路径的检查都验不出缺口。**
    > 要验证「某个容器里到底有没有 X」，必须直接查容器的原始键集，不能查「经过回退解析之后的值」。

41. **护栏里的手写清单自己会腐烂 —— 而且它就烂在「唯一覆盖某类键」的位置上**（验证盲区，2026-09-16 收尾之四）— 继续顺着同一族查，这次落在一处**硬编码**上。

    `Component.<Kind>.Name` / `.Desc` 这类键是**运行期拼出来**的（`$"Component.{kind}.Name"`），源码里没有字面量，所以静态扫描**永远看不到**它们。为此 `CheckLocalizationKeyCoverage()` 末尾专门补了一条动态键检查 —— 但它把组件写死了：

    ```csharp
    foreach (var kind in new[] { ComponentKind.Atmosphere, ComponentKind.Hekate,
                                 ComponentKind.Ultrahand, ComponentKind.SysPatch })
    ```

    于是**往 `ComponentKind` 加第 5 个组件时，这条检查静默不覆盖它**：新组件的 `Component.X.Name` 三份语言包全缺也照样绿，界面直接显示 `Component.X.Name` 给用户。

    顺着往下还发现同族第二处：**没有任何断言要求「每个 `ComponentKind` 都有目录定义」** —— 往枚举加一个成员、忘了在 `ComponentCatalog.Definitions` 里加，该组件在界面上**根本不会出现**（界面是按 `Catalog.All` 建的），同样无人发现。

    两处一起修：

    - 动态键检查改成遍历枚举 `Enum.GetValues<ComponentKind>()` —— **自维护**，将来新增组件自动被覆盖；
    - 新增 `CheckComponentKindCoverage()`（3 项）：每个 `ComponentKind` 都要有目录定义、目录项数必须等于枚举项数（同时挡住重复声明）、带前置条件断言防橡皮图章。

    **注入验证**：只往 `ComponentKind` 加一个 `Emuiibo`（不给目录定义、不加语言键）→ 回归 **634 通过 / 3 失败**，3 条全是新护栏报的（2 条来自 `CheckComponentKindCoverage`，1 条来自枚举驱动的动态键检查）。

    为证明「旧写法确实是瞎的」，做了一次**对照实验**：同一注入下只把循环改回写死的 4 个 → 那条动态键断言**变绿**，失败数从 3 降到 2。

    | 循环写法 | 动态键断言 | 失败数 |
    | --- | --- | --- |
    | 写死 4 个（原样） | ✅ **绿**（`Emuiibo` 根本没进循环） | 2 |
    | 遍历枚举（新） | ✗ **红**，点名 6 个缺失键 | 3 |

    > 一条经验：**护栏里的手写清单自己会腐烂。** 「补自动化检查覆盖不到的部分」的那段代码，如果用手写清单，就等于把要修的那个腐烂问题又搬进了护栏里。
    > **清单的来源必须是真源本身**（枚举、注册表、目录），不能是手抄 —— 否则护栏会一边绿着一边失明。

42. **一条「恒真的断言」：拿「某条日志没出现」当护栏，而那条日志根本不会被打印**（2026-09-16 收尾之五）— 这轮是从**语言包的死键**查出来的。

    `CheckFreshUiState` 里原本有这么一条：

    ```csharp
    var defaultSelectedLog = LocalizationService.Instance["Log.ComponentsDefaultSelected"];
    Assert(!vm.Logs.Any(l => l.Message == defaultSelectedLog),
        "界面路径不该记录「已默认勾选全部组件」的日志（那是 --run 专用；界面默认全不勾）");
    ```

    它**永远不可能失败**：`Log.ComponentsDefaultSelected` 在整个 `src/` 里一次都没出现 —— `EnsureComponentsSelected()` 只把 `IsSelected` 设成 `true` 然后 `return`，**不打任何日志**。所以「某条日志没出现」恒成立，与行为无关。

    **怎么发现的**：做了一次语言包**死键审计** —— 包里 349 个键，`src/` 引用 325 个，未被引用的 24 个里有 8 个是动态键家族（已有专门检查覆盖），剩下 **16 个是历史遗留**（改名或改设计后没清）。其中一个正好喂给了上面那条断言。

    **修法**：删掉这条假护栏，并补上它**本该守**的那个契约。真属性现在有两处覆盖：

    - **状态侧**（原有）：`CheckFreshUiState` 断言全新界面 `Components.Where(c => c.IsSelected).Count == 0`；
    - **源码侧**（新增两条）：`CheckFirstRunSelectionContract()` 里补上 ① `App.xaml.cs` 里 `EnsureComponentsSelected()` 的调用点**有且只有一处、且必须落在 `RunHeadless` 内**；② 其余源文件**一处都不许有**。

    **注入验证**：往 **UI 路径** `OnStartup` 里加一句 `viewModel.EnsureComponentsSelected();` → 回归 **637 通过 / 1 失败**，精确命中新契约。

    > 那次注入里 **`CheckFreshUiState` 的行为断言没有响** —— 测试直接构造 `MainViewModel`，不走 `App.OnStartup`，所以**行为护栏结构上够不到 UI 启动路径**。源码契约是唯一能抓到它的。
    > **「行为测试 + 源码契约」不是冗余，是互补**：行为测试证明运行时状态对，源码契约覆盖那些测试够不到的入口。

    > 一条经验：**拿「某个事件没发生」当护栏之前，先确认那个事件真的会发生。** 断言「日志里没有 X」的前提是「X 有代码路径能打出来」，否则它守的是一个不可能发生的事件。
    > 推论：**语言包里的死键是线索** —— 一个没人引用的键，往往意味着某条检查挂在了一条没人会发出的消息上。

43. **一个「忘了填」的默认值：`TargetFile` 为 null 既是「本就不写进配置文件」的标记，也正是漏填的默认值**（2026-09-16 收尾之六）— 这轮是把上一轮的**反向审计**手法用到「配置项」上查出来的。

    目录里每个配置项都可以声明 `TargetFile`（写进哪份 ini）。`CheckEveryDeclaredTargetFileIsWritten()` 守的是「**声明了**的落点必须真的被写出来」，但它的过滤条件是 `!string.IsNullOrEmpty(o.TargetFile)` —— **`TargetFile == null` 被整类排除**。

    问题在于 `null` 同时承担两种含义，而在类型上无法区分：

    - 「本就不写进配置文件」的**显式**标记（例如 Ultrahand 的安装范围选项）；
    - **忘了填** `TargetFile`。

    于是「新增一个选项、忘了写 `TargetFile`」会静默通过：界面上多一个能改的开关，**改了什么也不发生**。

    **顺带把这一层反查了一遍（结论：其余无缺口）**：`WizardOptions` 的 24 个可写属性全部至少被读过一次；`TargetFile` 为空的 **9 个**选项，其 Key 在源码里都另有消费点（5 个由引导项专用代码消费，4 个是 Ultrahand 安装范围、在 `ComponentCatalog` 自己的 picker lambda 里读）。

    **新增 `CheckEveryDeclaredOptionIsConsumed()`**：`TargetFile` 为空的选项，其 Key 在源码里必须**至少出现两次**（一次声明 + 至少一次消费）；只看代码行、跳过注释行（注释里出现同一个字符串不算消费）。**没有白名单** —— 今天不存在「界面上有、但什么都不做」的合法选项，将来也不该有。

    **注入验证**：往 `BuildSysPatchOptions()` 里加一个不写 `TargetFile` 的新选项 `brand_new_toggle` → 回归 **639 通过 / 1 失败**，失败的正是新护栏、且点名 `brand_new_toggle（仅 1 处）`，**其他护栏一条都没响**（说明这个缺口此前完全没被覆盖）。

    > 一条经验：**可空字段同时承担「显式不适用」与「忘了填」两种含义时，后者永远不会被发现。** 要么给它一个具名的哨兵值（`TargetFile = NotWritten`），要么像这里一样补一条「它必须另有消费点」的护栏 —— 让「只有声明、无人消费」变成会红的断言。
    > 排查手法（本轮踩到）：**审计脚本不要按「文件」排除。** 第一版脚本把 `ComponentCatalog.cs` 整个排除掉，而消费点恰好就在这个文件里（picker lambda），于是报了 4 个假阳性。要排除的是**声明本身**，不是声明所在的文件。

44. **一个「被合法分支吸收」的错误：`nogc` 的 Key 拼错后产物逐字节相同，而它的写入分支此前从未被任何用例走到过**（验证盲区，2026-09-16 收尾之七）— 这轮把「反向审计」用到**选项取值**上查出来的。

    `ConfigGenerator` 取选项值都走同一个辅助方法：

    ```csharp
    private static string Option(WizardOptions options, ComponentDefinition definition, string key)
    {
        var optionDefinition = definition.Options.FirstOrDefault(o => o.Key == key);
        return options.Get(definition.Kind, key, optionDefinition?.DefaultValue ?? string.Empty);
    }
    ```

    也就是说 **Key 拼错不报错，只会静默回退成空串**。10 个调用点全是字面量，此前没有任何东西在守。

    但更值得记的是：**这个错误能不能被看见，取决于「空串」会不会落进一个合法的分支**。同一种拼错，两个调用点给出完全不同的结果：

    - `override_key`：无条件 `Set("default_config", "override_key", …)` → 拼错 → 写出 `override_key=` 空值行 → 基线的 `Contains("override_key=!L")` 报错。**可见。**
    - `nogc`：写入器有三个分支 —— `"0"`/`"1"` 写键值行，**其它一切值（包括空串）只写一条说明注释** → 拼错 → 产物**逐字节相同**。**完全隐形。**

    而且 `nogc` 的写入分支在整个测试套件里**从未被走到过**：目录默认值是 `"auto"`、完整场景传的是 `"-1"`，两者都落进「只写注释」分支 —— 连**正确行为**都没被断言过（`stratosphere.ini` 只在「合并碰撞」用例里被碰过，那两条断言只看段头）。所以拼错与否，产物都是「没有 `nogc=` 这一行」，没有任何断言能看出差别。

    **修法两半（都要）：**

    1. 新增 `CheckOptionAccessorKeysAreDeclared()`（3 项）：每个 `Option(options, definition, "…")` 调用点的 Key，必须声明在**该调用点作用域里 `definition` 指向的那个组件**的目录里（向上找最近的 `definition = ComponentCatalog.Get(ComponentKind.X)`）。声明侧直接查 `ComponentCatalog`（真源），不是手抄清单。
    2. 新增 `CheckStratosphereNogc()`（4 项）：把 `nogc` 的三个分支都钉住（`auto` 不写行 / `1` 写 `nogc=1` / `0` 写 `nogc=0`）—— **让这个功能从「不可观察」变成「可观察」**。静态护栏防拼错，行为用例防「分支本身写错」。

    **注入 + 对照实验**（同一注入方式：把某个 Key 字面量拼错）：

    | 注入的 Key | 基线是否点名 | 失败数 | 谁报错 |
    | --- | --- | --- | --- |
    | `nogc`（未点名，且空串落进合法分支） | ✗ | **1** | 只有新护栏 |
    | `override_key`（基线上有 `Contains("override_key=!L")`） | ✓ | **2** | 基线断言 + 新护栏 |

    对照组证明了两件事：新护栏**不是冗余**（`nogc` 那侧只有它报错）；以及**「字面量断言的覆盖范围只等于它点名的那几个键」** —— 从断言本身看不出「还有哪些键没被点名」。

    > 一条经验：**判断一个缺口是否可见，要问「错误值会不会产生与正确值不同的可观察结果」。** 当回退值（空串 / 默认值）恰好落进一个合法分支、产物不变时，**行为测试在原理上不可能覆盖它** —— 这类缺口只有静态契约能守。
    > 这和第 42 条「启动路径够不着」同属「行为测试原理上够不着」，但机制不同：那条是**路径没被执行**，这条是**路径执行了、错误被分支吸收了**。

45. **一个「段写错」的缺口：键落在错误的 ini 段里，固件按段分发时读不到 —— 而所有断言都是整文件 `Contains`，段根本不参与**（验证盲区，2026-09-16 收尾之八）— 这轮把前两轮的「回退掩盖缺口」做成**系统扫描**，顺着 `option.Section ?? "atmosphere"` 找到的。

    `system_settings.ini` 是**按模块分段**的：`[eupld]` / `[usb]` / `[ro]` / `[atmosphere]` / `[hbloader]` / `[lm]`。Atmosphere 按段把这些键分发给对应模块 —— **键落在错误的段里，固件根本不读它，等于没配**。

    而写入器是泛化的：

    ```csharp
    settings.Set(option.Section ?? "atmosphere", option.Key,
        FormatTypedValue(option.IniValuePrefix, Option(options, definition, option.Key)));
    ```

    两件事让这个错误难以发现：

    - **兜底 `"atmosphere"` 对 22 个选项里的 15 个恰好正确** —— 「段没那么要紧」这个心智模型在多数情况下确实成立，所以错误的少数情况更难被怀疑；
    - **`Section` 是 `string?`**，「忘了写」与「本来就不需要」在类型上无法区分。

    而现有断言**全是整文件 `Contains("key=value")`** —— `[usb]` / `[eupld]` / `[ro]` / `[hbloader]` / `[lm]` 在测试里**一次都没出现**。把键挪到别的段，所有断言照样全绿。

    **新增 `CheckIniSectionsMatchDeclarations()`（7 项）**：把生成的文件解析成「键 → 所在段」，然后对**每个**声明写进该文件的选项，断言它的键就落在它声明的段里 —— 覆盖 **28 个**选项（22 + 6），而不是基线点名的 8 个。

    其中三条是前置条件，值得单说：① 应能从源码里找出「泛化写出的目标文件」；② 每个这样的文件都应有选项声明（**认错文件会在这里炸，不会静默漏过**）；③ **这些文件应跨多个段** —— 只有一个段时「段写错」本来就不可能发生，这条用例也就失去意义。

    **不手抄文件清单**：目标文件是从 `ConfigGenerator.cs` 里**发现**的 —— 每个 `option.Section ??` 出现处向上找最近的、以 `.ini` 结尾且含 `/` 的字符串字面量。新增第三个泛化循环会自动被带上（这也是前三轮反复写进技能的那条：护栏里的手写清单自己会腐烂）。

    **注入验证**：把 `usb30_force_enabled` 的 `Section = "usb"` 删掉（模拟「忘了写段」）→ 回归 **653 通过 / 1 失败**，失败的正是新护栏、点名 `usb30_force_enabled 声明 [未声明]，实际落在 [atmosphere]`。两条旁证：

    - **基线那条 `Contains("usb30_force_enabled=u8!0x1")` 仍然是 ✓** —— 整文件匹配看不见段，这就是「旧断言是瞎的」的实证；
    - 生成的 `system_settings.ini` 里 **`[usb]` 段整个消失了**（它只有这一个键）。真机上 Atmosphere 的 USB 模块读 `[usb]` 会读到空 —— 设置静默不生效。

    **顺带修掉一处自己写的瑕疵**：这条护栏的前置条件消息里我写死了「当前 0 个」，而 `Assert` **在成功时也会打印消息** —— 于是输出显示「✓ …（当前 0 个）」，**输出在撒谎**。改成插值 `{declared.Count}`。这正是本文件第 40 条「断言名字与实现不符」的同类问题，只不过这次是我自己犯的。

    > 一条经验：**当 `Assert` 的成功路径也会打印消息时，消息必须是中性陈述**，不能只在失败时读得通。
    > 更一般的：**ini 的段是「寻址」的一部分，不是排版。** 只断言「键值对存在」而不问「在哪个段里」，等于只验证了一半的地址。

46. **`TargetFile` / `Section` / `IniValuePrefix` 是同一族的第三个成员，而它是唯一还没护栏的**（验证盲区，2026-09-16 收尾之九）— 这轮先把「回退掩盖缺口」**做成系统扫描**（`grep -rn "??"` + `grep -rn "_ =>"`，147 处候选），再按「只有配置产出路径能静默写出错产物」筛到 3 个文件，最后逐个问「合法疏忽能走到这个回退吗？后果可见吗」。

    `IniValuePrefix` 有**两种**漏法，而且两种都以「裸值」告终 —— 裸值正是另外 48 个选项的**正常**产物，所以产物看着完全合理：

    ```csharp
    private static string FormatTypedValue(string? prefix, string raw) => prefix switch
    {
        "u8"  => value is "1" or "true" ? "u8!0x1" : "u8!0x0",
        "u16" => "u16!" + value,   "u32" => …,  "u64" => …,  "str" or "string" => "str!" + value,
        _ => value,                // ← 拼错的前缀落在这里；漏写则压根不进 switch
    };
    ```

    - **漏写** → `upload_enabled=1` 而不是 `upload_enabled=u8!0x1`，Atmosphere 按类型解析时读不出来；
    - **拼错**（`"u18"` / `"u8 "` / `"i32"`）→ 落进 `_ => value`，产物与「本来就不需要前缀」**逐字节相同** —— 与第 44 条 `nogc` 同族：**错误被合法分支吸收**。

    而现有断言是 22 条 `Contains("键=u8!…")`，**只点名了当前的 22 个选项**；第 23 个漏写前缀时，一条都不会红（第 43 条：字面量断言的覆盖只等于它点名的键）。

    **新增 `CheckIniValuePrefixesAreRecognized()`（5 项）**，两条不变量都**从声明集合推出来**、不手抄清单：

    1. **认得的集合从源码扫出来** —— 扫 `FormatTypedValue` 的 switch 分支里 `=>` **左边**的字面量（右边的 `"u8!0x1"` 是产物不是前缀）。新增一个分支会自动被带上。声明的前缀必须在这个集合里；
    2. **同一目标文件内前缀声明必须统一** —— 一个文件的值语法是**文件的属性**，不是单个选项的：要么全带（`system_settings.ini` 的 `u8!`/`u64!`/`str!`），要么全是裸值（`hekate_ipl.ini` / `nyx.ini` / `exosphere.ini` …）。实测 9 个文件**全部**满足「全带或全无」，只有 `system_settings.ini` 带前缀。

    **对照实验**（两种漏法各注入一次）：

    | 注入 | 漏法 | 基线看得见吗 | 失败数 | 谁报错 |
    |---|---|---|---|---|
    | 新增第 23 个 `system_settings.ini` 选项，不写前缀 | 漏写 | **看不见**（只点名了 22 个） | **1** | **只有新护栏**，点名 `22/23 个声明了前缀，缺前缀的：probe_unprefixed` |
    | `upload_enabled` 的 `IniValuePrefix` `"u8"` → `"u18"` | 拼错 | 看得见 | 3 | 基线 ×2（`u8 类型值格式正确`、`upload_enabled 默认应为 u8!0x0`）+ 新护栏 ×1 |

    第一条注入的产物里能直接看到两种写法并存：`probe_unprefixed=0`（裸值）紧挨着 `upload_enabled=u8!0x0`。**这就是「漏写前缀」在真机上的样子** —— 文件看着对，设置不生效。

    > 一条经验：**判断一个缺口是否可见，还要问「这个回退值在多数情况下是不是恰好正确」。** 兜底值只对少数情况正确时，错误会立刻暴露；恰恰是「对大多数情况都对」，才让「这个字段没那么要紧」的心智模型成立，从而掩盖少数情况。`Section ?? "atmosphere"`（22 里 15 个对）和 `IniValuePrefix` 的 `_ => value`（70 里 48 个对）都是这个形状。
    >
    > 排查手法（本轮固化）：**先扫描、再筛选、最后排序**，别靠直觉挑对象。`grep -rn "_ =>"` + `grep -rn "??"` 列全候选 → 按「能不能静默写出错产物」筛到配置产出路径 → 逐个问「合法疏忽能走到吗」。**排除掉的候选也是结论**（本轮排除了 `FolderName` 的 `_ => kind.ToString()`：它是暂存路径的唯一来源，自洽；以及 `FormatTypedValue` 之外的另一处 `_ => value`：刻意宽松）。

    **顺带修正两处「注释在撒谎」**：`CheckIniSectionsMatchDeclarations` 的注释与第 45 条正文都写「21/16 个选项」，实测是 **22 个选项、15 个落 `[atmosphere]`、7 个落别的段** —— 已全部改正（README ×2、`MEMORY.md` ×1、`MEMORY-details.md` ×1，代码注释 ×1）。

    **另一处被排除但值得记下的候选**：`ComponentCatalog` 里 `var primary = options.Ram8Gb ? ram8GbBin ?? standardBin : standardBin;` —— 若上游发布包没有 `__ram8GB.bin`，这里会**静默退回标准版 payload**，而两个变体的落点完全相同（`payload.bin` + `bootloader/update.bin`），产物看着完全合法，注释却写着「「界面选了 8G、机器却按 4G 起」这种错位不可能发生」。测试的 8G 用例（第 2887 行）**总是**提供 `__ram8GB.bin`，所以这个回退分支**从未被走到过**。**本轮未改动**（属行为变更：正确做法可能是「8G 拿不到 8G payload 就报错而不是静默降级」），留待确认 —— 后续：**注释与用例已在 2026-09-17 审计里补齐**（见第 51 条与第九节第 13 条），「要不要从静默改成看得见」仍待你定。

47. **运行期校验器里的「计数断言不是完整性断言」：`MergedFileCount > 0` 证明不了「每个勾了的组件都在」**（验证盲区，2026-09-16 收尾之十）— 前面几轮找的都是**测试**够不着的缺口，这轮回到**运行期**校验器上：`OutputValidator` 的第 4 条检查只看合并**总数**。

    ```csharp
    if (result.MergedFileCount > 0)  report.Add(Ok,    "Check.Ok.Merged", ...);
    else                            report.Add(Error, "Check.Error.MergedNothing");
    ```

    它自己其实已经承认不够用 —— 紧跟着一条**只针对 Atmosphere 的特例**：`if (options.Atmosphere) { package3 在不在？ }`，注释写着「合并了 47 个文件但恰好漏了 package3 也照样开不了机」。**特例是缺口的自白书**：既然单个关键文件要单独确认，那么「单个组件一个文件都没进来」同样要单独确认，而它没有。

    缺口形态：勾了四个组件，其中一个的解压目录不在（解压中途失败 / 目录被清过），另外三个合进来几十个文件 → `MergedFileCount = 47 > 0` → 报告写着「已合并 47 个组件文件」，用户拿到的是**缺了一个组件**的 SD 卡。而 `MergeComponentFiles` 在解压目录不存在时是 `continue` **静默跳过**的，「跳过了谁」在数据上根本不可见。

    **修法分两半**：

    1. `MergeComponentFiles` 不再只是 `continue`，而是按组件把结果记进 `ConfigGenerationResult.MergedFilesByComponent`（**含 0**），让「跳过」变成**可对账的数据**；
    2. `OutputValidator` 增加逐组件对账：`ComponentCatalog.All` 里每个**被勾选**的组件都必须贡献 > 0 个文件，否则一条 `Check.Warn.ComponentNotMerged` 点名它。

    名单从 `ComponentCatalog.All` 取（**不手抄**）—— 以后加第 5 个组件自动覆盖。分级刻意选 **Warning 不是 Error**：Ultrahand 在「手动安装 + 三个子项全关」时本来就没有文件可合并（那种情况下载阶段已另有告警）；漏报比误报贵，但误报会让人学会无视告警，所以分级要保守。

    **新增 `CheckEverySelectedComponentMergesIntoOut()`（11 项）**，同时补上一个**覆盖缺口**：在此之前唯一的合并用例（`CheckMergeIntoOut`）**只造了 Atmosphere 与 Hekate 的解压目录**，`Ultrahand` / `Sys-patch` 的合并路径**从来没被走到过**（那个用例显式写着 `Ultrahand = false, SysPatch = false`）。新用例按 `ComponentCatalog.All` 循环，给四个组件各造一个**唯一文件名**的标记文件（都叫同一个名字的话四个组件写同一个目标路径，最后只剩一份，就分不出是谁了），断言每个组件都有文件进来、每个标记文件都进了清单。

    **对照实验（就写在用例里，不靠外部注入）**：只删掉**一个**组件的解压目录，然后断言

    - 旧的总数检查 `Check.Ok.Merged` **仍然通过**（证明它确实看不见）；
    - 新的按组件对账**点名**那个组件；
    - 没被删的组件**不被点名**（防这条检查退化成「凡有组件就报警」）。

    外部注入也做了一次：把逐组件对账的条件改成永不命中（退回旧行为），**只有**「按组件对账应点名缺件组件」这一条变红，其余 **669 条全绿**。

    > 一条经验：**「特例」是缺口的自白书。** 一个聚合检查旁边跟着针对某个具体对象的特例，说明作者已经意识到聚合不够用，却只补了眼前那一个对象。看到 `if (某一个) { 单独再查一次 }` 就该问：**同类对象里，另外那些谁来查？**
    >
    > 另一条（本轮踩到）：**注入验证的备份要分清楚是「改之前」还是「改之后」。** 本轮把注入前录的 md5 与**本轮开始前**的文件备份混在一起用，还原时把新写的代码一起抹掉了 —— 是 md5 校验对不上才发现。**还原用「修复后」的备份，md5 用「注入前」录的。**

---

48. **一个「双向绑定只做了一半」的缺口：勾了 90DNS 会打开 `enable_dns_mitm`，但取消 `enable_dns_mitm` 不会关掉 90DNS**（验证盲区，2026-09-17 收尾之十一）— 需求原话是三个方向（勾任一 → 打开 mitm；都取消 → 关掉 mitm；mitm 被取消 → 两个 90DNS 一起取消），而**最容易漏的正是第三个**：前两个方向写在 90DNS 的 setter 里，第三个方向必须去订阅**另一个组件**（`Atmosphere`）里的选项变化 —— 是另一条数据链路，不写的话前两个方向照样全绿。

    漏掉的后果不是崩溃，而是**界面与产物长期不一致**：界面显示「已启用真实破解系统 90DNS」，生成的 `system_settings.ini` 里 `enable_dns_mitm=u8!0x0` —— 屏蔽表生成了、开关关着、遥测照常上报。用户没有任何线索能看出这件事。

    **修法**：`MainViewModel` 里加一个 `_applyingDnsMitmCoupling` 重入闸（两个方向的 setter 都会回头看对方，少了它就会「勾一个 → 打开 mitm → 反向又去关 90DNS」互相触发），正向由两个 90DNS 属性驱动，反向由 `OnDnsMitmOptionChanged` 订阅选项的 `ValueChanged`。当时的取舍是**刻意不对称**：勾上 mitm 不反过来替你勾 90DNS（理由：mitm 是更基础的开关，用户可能自己配 hosts）。

    ⚠️ **这条取舍当天就被推翻了** —— 见第 57 条。「刻意不对称」只保证了「状态机怎么走」，没保证「哪些状态合法」，于是「mitm 开着、两个 90DNS 都不勾」仍可手动走到，产物是 `enable_dns_mitm=1` 却一份 hosts 都没有。第二版改成**完全双向绑定**（勾 mitm 时自动补勾当前可见的 90DNS），`Check90DnsSwitches()` 相应由 12 项扩到 17 项。

    **新增 `Check90DnsSwitches()`（12 项）**：四种开关组合 → hosts 文件**集合**逐项对齐（顺带守住「上一轮写的 hosts 会被清单清掉」）；联动的三个方向逐个断言；并先钉住 `Atmosphere` 组件里确实有 `enable_dns_mitm` 这个键 —— 键名拼错时 `FindOption` 返回 `null`，联动会**静默失效**（这一族坑见第 43、46 条）。注入实测：把反向那一半改成永不命中 → **只有**「手动取消 mitm → 两个 90DNS 同时取消」及其紧随的一条变红，其余 708 条全绿。

49. **「两段有同名键」时存储键必须加前缀，于是「存储键 ↔ ini 键」成了新的错位点**（配置项，2026-09-17 收尾之十二）— `override_config.ini` 的 `[hbl_config]` 与 `[default_config]` **都有 `override_key`**，而 `OptionDefinition.Key` 在组件内必须唯一（`Option()` 与 `WizardOptions.Values` 都按 Key 查），所以 `[hbl_config]` 那五个只能改名成 `hbl_*`，写进文件时再换回真名。

    这条链路上有两个**都静默**的错法：① 写入点传了个没声明的存储键 → `Option()` 回退到 `DefaultValue`，产物**看着完全正常**（值就是默认值）；② ini 键名写错 → 产物里多一个 Atmosphere 不认识的键、少一个它要的键。而 `[hbl_config]` 的键名此前是**硬编码**的，所以这两个错法以前根本不存在 —— 是「改成可配置」这个动作**新引入**的风险。

    **修法**：`CheckHblConfigOptionsAreWired()`（21 项）从**写入点本身**扫出「ini 键 ↔ 存储键」的对应关系（不手抄），再双向比对「声明了就要写 / 写了就要有声明」；接着做**行为验证** —— 把五个选项各自改成**非默认值**再生成，逐项断言它出现在 `[hbl_config]` 里。

    > 只比默认值是**恒真**的：写入器写死同一个字面量时照样通过 —— 而那正是改动前的状态。这正是第 1 条「恒真断言 = 橡皮图章」的又一次现身。文本选项（`program_id` / `path`）没法从 `Choices` 推探针值，所以给了一张小表；**表里没有就报错而不是跳过**，免得这条用例对将来新增的选项视而不见。

    顺带把两个键的**取值也加了校验**：`program_id` 必须是 16 位十六进制、`path` 必须是 SD 卡内的相对路径（无盘符、无 `..`）。格式不对时**回落到官方默认值并告警**，而不是原样写进去 —— 写错不会报错，只会让 Homebrew Menu 在真机上永远起不来。

50. **新增一行「下载源」很容易，让它真的生效才难 —— 而旧护栏的期望值是个硬编码的数字**（验证盲区，2026-09-17 收尾之十三）— 需求是「Hekate 改成两行：官方 + `easyworld/hekate`」。表面上只是往 `RepoSources` 清单里加一项，但那会做出一个**死控件**：`RepoOverrides` 按「默认地址」做键，而 `ResolveRepo` 只读**官方**那一行的键，镜像行填的值**没有任何人读**。界面上能填、能保存、下次打开还在，下载时纹丝不动。

    **修法**：`ResolveRepo` 改成「任一行填了自定义地址即生效；两行都填时以官方行为准」（顺序固定，不看运气；提示文案里也这么写）。两行都没填才回落到「中文界面用镜像」的老规矩。

    **护栏**：`CheckRepoSources()` 由 26 项扩到 **37 项** —— 对清单里**每一行**做一次探针（填一个自定义地址 → 断言它真的被 `ResolveRepo` 采纳），再把 hekate 两行的组合语义钉死（只填一行 → 那行生效，含「英文界面填汉化行」；两行都填 → 官方优先；都不填 → 语言默认；8G → 不影响包的语言，只让 payload 回官方 —— 这一条 2026-09-18 改过，见第 72 条）。

    > 顺手改掉两个坏习惯：旧护栏里的 `expected` 是**手抄的仓库清单**（第 41 条那条教训的翻版），现在改成 `DeclaredRepos` **反射扫** `ComponentCatalog` 的 `RepoSpec` 字段、与清单做**双向集合比对**；旧护栏里那句「下载源清单应有 **6** 项」则是「计数断言不是完整性断言」（第 1 条）的又一个例子 —— 数字对不上会报错，但**少的是哪一个**它说不出来。

51. **「回退分支从未被走到」+ 一句说反了的注释：8G 拿不到 8G payload 时会静默退回标准版**（验证盲区，2026-09-17 审计）— `var primary = options.Ram8Gb ? ram8GbBin ?? standardBin : standardBin;` 里那条 `??` 回退，在发布里没有 `__ram8GB.bin` 时会让 `primary` 悄悄变成标准版；而 `exosphere.ini` 照样写 `enable_mem_mode=1`、引导项照样写 `memmode=1`，产物于是呈现**「配置说 8G、payload 是 4G」**。偏偏紧挨着的那句注释写着「这种错位不可能发生」—— 它**只对非回退路径成立**。

    为什么一直没被发现：8G 用例**总是**提供 `__ram8GB.bin`，这条分支**从未被走到**；而 picker 的委托签名 `Func<ReleaseInfo, WizardOptions, IReadOnlyList<AssetPick>>` 里**没有 logger**，想就地告警也没有出口。

    **已做**：把注释改成真话（写清回退会造成什么），并补一条用例把**当前行为**钉住 —— 造一个不含 `__ram8GB.bin` 的 Release、断言 `primary` 确实落在标准版上。这样分支从「不可达」变成「可达且被记录」，将来若改成「报错而非静默降级」，这条用例会立刻变红、逼着一起改。**是否改成报错仍等你定**（属行为变更，见第九节）。

52. **「回落 + 告警」只验了回落、没验告警：`[hbl_config]` 两个自由文本项的非法输入路径没有任何用例**（验证盲区，2026-09-17 审计）— `program_id` / `path` 各有一个 normalizer，非法输入会**回落官方默认值 + 告警**。但既有用例的探针值填的**全是合法值**，于是「非法 → 回落」这条分支从未被走到：把校验条件改坏（比如写成恒真），**没有任何断言会红**，而后果是 Homebrew Menu 在真机上永远起不来 —— 文件看着完全正常、界面上也没有任何提示。

    **修法**：新增 `CheckHblConfigFallbacks()`（9 项）—— 非法输入必须**不**被原样写出、必须回落到官方默认值、**必须留下告警**；再加一条反向护栏「合法输入不该被回落」（少了它，normalizer 被写成恒回落也照样全绿）。为此加了 `CapturingSink`：本项目里「回落 + 告警」是标准修法，而**告警本身也是功能**，只断言值不断言告警等于只验了一半。

    > 写这条用例时自己踩了两个坑，都是**断言作用域**的问题，值得记下来：
    > ① `CapturingSink` 起初连 Info 一起收，而生成器每次都会打一堆「已写入 xxx.ini」→ 断言「无告警」必然失败。**要判空就得按级别过滤**（`Warnings`）。
    > ② 改成按级别过滤后仍失败，因为生成器在**别的理由**上本来就会告警（「未勾选 Hekate，引导项不会被写入」）。**断言要盯住被测对象，不是整个世界的状态** —— 最终收窄成「不该有 *hbl_config* 的告警」。

53. **用例里手抄了全部 `ComponentKind`**（护栏自身腐烂，2026-09-17 审计）— 暂存/生成那条用例写的是 `foreach (var kind in new[] { Atmosphere, Hekate, Ultrahand, SysPatch })`。这正是第 4 条教训：**手抄的清单会腐烂** —— 将来加第 5 个组件时，这条用例会**静默跳过它**，「每个组件都能被暂存/生成」看起来一直成立。本文件另外三处（组件枚举覆盖、选项往返、落点契约）早就是 `Enum.GetValues<ComponentKind>()`，只有这一处漏了。**已改齐**。
54. **同一份「命名判据」散在三个地方 —— 而护栏第一次跑就抓到了真实漏改**（护栏自身腐烂，2026-09-17 决策落地）— 「一个文件名是不是 8G 版 payload」这条规则，此前用带引号的 `"ram8GB"` 字面量写在**三处**：`ComponentCatalog` 的 picker（挑 payload）、`ConfigGenerator.EnsureRootPayloadBin`（挑根目录 `payload.bin` 的来源）、`ConfigGenerator.RemoveStandardHekatePayloadFromRoot`（8G 模式下删掉标准版）。三处各自独立 —— 上游一旦改命名，就会**一处认、一处不认，而两边都不报错**：8G 用户会拿到 4G 的 `payload.bin`，或者反过来，8G 版 payload 被当成标准版**删掉**。现在三处都走 `ComponentCatalog.IsRam8GbPayloadName`，并加了一条护栏：这个字面量在整个 `src/` 的**代码行**里只允许出现一次。**这条护栏在真实施工中就抓到了东西** —— 改前两处时第三处漏改了（并行编辑同一文件时被覆盖），护栏立刻报「实际 2 处：ComponentCatalog.cs / ConfigGenerator.cs」。

55. **「告警是否看得见」完全取决于日志级别，而改级别不会有任何行为测试变红**（验证盲区，2026-09-17 决策落地）— 方案 A 落地后，如果只钉住「`MainViewModel` 调了 `IsRam8GbPayloadMissing`」，那么把 `LogLevel.Warning` 降成 `Info` 就没有任何断言会红：`run.log` 里那一行从 `[Warning]` 变成 `[Info]`、界面上也不再按告警着色，**告警语义丢了**（用户扫一眼不会再注意到它），而产物依旧是「配置说 8G、payload 是 4G」。所以源码契约里连**级别**一起钉，并加一条反向断言「不得是 `Error`」—— 方案 A 明确是**告警**不是报错：8G 缺失只影响 payload 版本，其余文件仍然可用，报错会让整次生成失败、用户拿不到任何产物。这是第 42 条（恒真断言）的同一族：**断言引用的「标识」必须真的被生产端产生，而级别也是标识的一部分。**


56. **「找不到就随便拿一个 zip」的回退会拿到语言包 —— 而且它真实可达、还没有任何检查能发现**（静默错产物，2026-09-17 收尾）— Ultrahand 的 picker 写的是 `FindAssetByName("sdout.zip") ?? 任意 .zip`。看着是个稳妥的「上游改名也能兜住」，但上游 `Ultrahand-Overlay` 的 Release 里**同时**有 `sdout.zip`（整套 SD 内容）与 `lang.zip`（14 个语言 json），而 GitHub 的 `assets` 是**按名字排序**返回的 —— `lang.zip` 排在 `sdout.zip` **前面**（v2.5.3 实测顺序就是 `lang.zip` → `ovlmenu.ovl` → `sdout.zip`）。于是 `sdout.zip` 缺失时，回退拿到的是语言包：14 个 json 被当成整套 SD 内容铺到 `out/` 根目录，**Ultrahand 本体一个文件都没有**。

    **这个状态真实可达**：Release 刚发布时资源是**逐个上传**的，那一刻 `sdout.zip` 可能还没传上去（GitHub 在资源传完前就已经把它标成 latest）。**而且它不会被任何检查抓到**：语言包合并进来照样让「已合并 N 个组件文件」> 0，`Check.Warn.ComponentNotMerged` 的逐组件对账也过 —— 用户拿到一张缺 Ultrahand 的 SD 卡，日志里一片祥和。

    **已修**：回退加上「排除语言包」，判据抽成 `ComponentCatalog.IsUltrahandLangZipName`（与 `installLang` 那处共用同一份，不再各写一遍 `Contains("lang")`）。**用例用上游真实的资源名与真实顺序**构造 ReleaseInfo，四个方向都钉住；`sdout.zip` 缺失时**一个都不挑**，交给既有的「未找到匹配的资源文件」告警，绝不退回 `lang.zip`。

    > 顺带逐个确认过另外三个仓库**没有**同类风险：`nx-ovlloader` v2.0.2 只有 `nx-ovlloader.zip`；Atmosphere 1.11.2 只有 `atmosphere-*.zip` + `fusee.bin`；`sys-patch` v1.6.2.3 只有 `sys-patch-v1.6.2.3.zip`。`ovlmenu.ovl` 的回退（任意 `.ovl`）在该仓库里也只有它自己一个 `.ovl`。**「只有一个候选」是这条风险不成立的唯一理由 —— 所以一旦上游加第二个 zip，这四处就要重新审。**

57. **前三条规则全都实现了，缺口却依然存在 —— 「事件规则」不等于「不变式」**（验证盲区，2026-09-17 第二版）— 你给的三条规则（勾任一 90DNS → 打开 `enable_dns_mitm`；都取消 → 关掉；取消 `enable_dns_mitm` → 两个 90DNS 一起取消）**在这之前就已经全部实现、而且有断言覆盖**，`Check90DnsSwitches()` 里逐条都是绿的。但三条都是**事件规则**（「当 X 变化时，把 Y 也改掉」），所以「`enable_dns_mitm` 开着、两个 90DNS 都不勾」这个**状态**依然可达 —— 只要用户直接去点那个复选框就行。旧用例里那条「勾上 mitm 不该反过来勾 90DNS」的断言，其实正是这个缺口的**自白书**：它证明了该状态可达，只是当时把它当成了设计意图。

    后果不是崩溃，而是**误导性产物**：`enable_dns_mitm=1` 写进 ini，而 `hosts` 一份都没生成 —— 用户以为遥测已经被挡住。所以补上第四条（勾 mitm → 补勾当前可见的 90DNS），把「事件规则」升级成**不变式**：`mitm ⇔ 至少一个 90DNS`。

    **教训**：需求用「当……的时候」描述时，实现者很容易只写出那几条**迁移规则**就宣布完成 —— 而迁移规则描述的是**状态机怎么走**，不是**哪些状态合法**。要判断有没有缺口，问的不是「这几条都实现了吗」，而是「**按这几条走，还能走到哪些不该存在的状态**」。

58. **载入时修正存档：「两个方向都能满足规则」，选错的那条会替用户写文件**（决策依据，2026-09-17 第二版）— 老版本 `enable_dns_mitm` 的默认值是 `1`，所以升级上来的 `settings.json` 里很容易是「mitm=1 而两个 90DNS 都不勾」—— 正是第 57 条要消灭的状态。修它有两个方向，**都能让规则成立**：关掉 mitm，或者补勾 90DNS。

    选了前者。判据是**「这是不是用户动作」**：补勾 90DNS 会让程序在启动时写出一份用户从没要过的 `hosts` 文件（还可能盖掉他自己配的），而关掉 mitm 只影响一个**默认值本来就该是 `0`** 的开关。同一条判据在别处也用过：规则④只在用户**亲手**勾 mitm 时才走，载入路径不走。

    ⚠️ **「修正了」不等于「落盘了」** —— 这条是拿**已发布的 exe** 跑 `--selftest` 才发现的，当时行为测试全绿：测试只断言了**内存里**的选项值，而 `settings.json` 里那行 `"Atmosphere/enable_dns_mitm": "1"` **原封不动**。原因是我想当然地以为「`SelectedLanguage` 的 setter 会顺手 `Save()` 一次」—— 实际上那个 setter 只在语言**真的变了**时才落盘（`SetProperty` 返回 `false` 就直接返回，什么都不写），语言没变时构造函数根本不保存。

    修法是让 `ApplyDnsMitmCoupling()` 返回「有没有改动」，构造函数在改动时**只回写那一项**再 `SettingsStore.Save()`，并补一条断言直接读回 `settings.json` 核对。⚠️ 这里**不能**图省事调 `SaveSettings()`：它会遍历 `RepoSources` 与全部组件选项，而这两者在构造函数里**此刻还没建好** —— 会把用户的下载源覆盖成空、选项值写丢。

    **教训**：行为测试断言的是**对象状态**，而用户看到的是**文件**。中间隔着一层「什么时候落盘」，那一层不在测试的视野里 —— 只有拿真实二进制跑一遍才能看见。

59. **两段 `/// <summary>` 叠在同一个方法上 —— 前一段被静默丢弃，护栏的文档挂到了错的方法头上**（文档腐烂，2026-09-17 第二版）— 本轮加死键护栏时发现的：`CheckLocalizationKeyCoverage()` 的整段说明（「总护栏：源码里引用到的每一个语言键……」）被写在了 `CheckComponentKindCoverage()` 的 summary **正上方**，两段连着。C# 只取紧邻的那一段，于是前者**被静默丢弃** —— 编译 0 警告、方法本身工作正常，只是它的设计意图（为什么存在、修的是哪个真 bug）**在代码里彻底消失了**，只剩一个看不出所以然的方法名。

    修法就是把它挪回正确的位置。**判据**：`/// <summary>` 出现「连着两段」时一定是错的 —— 无论编译器报不报警。

60. **测试辅助函数漏拷了一个真实输入 —— 克隆出来的场景静默用上空串**（潜在缺陷，2026-09-17 第四版）— `ConfigSmokeTest.CloneFlags()` 逐字段复制 `WizardOptions`，但**漏了 `Language`**。它是生成的真实输入（决定 `default_lang`，也决定 hekate 走哪个仓库），漏拷后克隆出来的场景里是 `string.Empty` —— 而空串会被映射成 `en`，断言**看着通过、其实测的是另一回事**。本轮做 `default_lang` 时才发现：我需要在克隆后单独补 `scenario.Language = ...`，否则三种界面语言的用例全都会退化成同一种。
    修法两半：给 `CloneFlags` 补上 `Language`；同时在新用例里仍然**显式**赋值，不依赖克隆函数的完整性。**判据**：一个「逐字段手抄」的复制函数，只要字段表是手写的，就会腐烂 —— 要么补全，要么让调用方显式给。

61. **用例隐式依赖磁盘上恰好没有 `settings.json`**（潜在缺陷，2026-09-17 第四版）— `CheckOptionValuesPersistence` 的意图是「选项值原样往返」，它 `new MainViewModel()` 直接读真实的 `settings.json`，却从没显式设置过组件勾选状态。加上「载入归一化」（`default_lang` 非 `en` ⇒ 自动补勾语言包）之后，**只要环境里恰好有一份勾了 Ultrahand 的 `settings.json`**，重载后的值就会与保存前差一项 —— 那不是往返失败，是另一条功能。本轮实测没红，纯粹是因为那台机器上恰好没有那份文件。
    修法：用例开头显式把组件全部 `IsSelected = false`，并在注释里写明归一化有它自己的用例（`CheckLangPackCoupling`）。**判据**：测试里出现 `new MainViewModel()`（读真实文件）时，凡是被读到的状态都要**显式**设定，不能指望环境干净。

62. **护栏验的是「测试里现造的一份副本」，而不是「声明本身」—— 把声明删掉照样全绿**（恒真断言，2026-09-18 收尾之十八）— 给 `sys-ftpd` 加「解压后筛一遍」时，第一版护栏是在测试里**现造**一份 `ExtractPlan`（`StripPrefix = "out"`）然后逐个断言 `Map()` 的返回值。全部通过。但把 `ComponentCatalog` 里那个槽的 `Extract` **整块删掉**再跑，测试**照样全绿** —— 因为断言测的是它自己造的那份副本，与真实声明毫无关系。而产物会变成 `out/out/atmosphere/…`（多一层 `out/`），SD 卡上那个插件**根本不会被加载**，界面上一切正常。
    修法：改成从**声明本身**取（`sysFtpd.Slots.First(s => s.Key == "SysFtpd").Extract`），并显式断言 `!KeepsEverything` / `StripPrefix == "out"` / 白名单内容。**这是第 1 条「恒真断言 = 橡皮图章」的又一次现身，形态是「护栏自己造了被测对象」。** 判据：写护栏时问一句「把生产端那一行删掉，这条断言会不会红？」
    **注入验证**：把 `sys-ftpd` 的 `Extract` 整块注释掉 → **12 项红，全部是新护栏**（含「产物会变成 `out/out/atmosphere/…`」这条），旧断言一条没红。

63. **只验「格式合法 + 组内自洽」抓不到「值抄错了」**（验证盲区，2026-09-18 收尾之十八）— `sysmodule title` 冲突规则的护栏第一版验的是：每个声明的 title 都是 16 位十六进制、同一组内的组件确实被算成冲突、不同 title 不算冲突。把 `SysCon` 的 title **抄成 botbase 那个**（`430000000000000B`，格式完全合法）再跑 —— 原断言**全部照常通过**：它只证明「声明的 title 自洽」，证明不了「声明的是**这个插件真正的** title」。而后果是误报：勾 `sys-con` 会被告知与 `sys-botbase` 冲突，用户于是放弃其中一个本来该装的插件。
    修法：补一张**实测对照表**（5 个 title 逐一对齐，含覆盖性双向断言 —— 既查「声明的都在表里」，也查「表里的都还被声明着」，免得将来删了插件、表里留个死条目）。**判据**：护栏若只检查「内部一致性」，那么它守的是**格式**不是**事实**；凡是对外部世界有断言义务的值（版本号、title、文件名、落点），都要有一份「与上游实测对齐」的对照表。
    **注入验证**：把 `SysCon` 的 title 改成 `430000000000000B` → **通过 1279 项、失败 1 项**，失败的正是新增的对照表那条（`✗ SysCon 的 sysmodule title 实测是 690000000000000D，实际声明的是 430000000000000B`）。

64. **我自己「顺手写」的一个值 —— 那不是实测，是编的**（流程教训，2026-09-18 收尾之十九）— 给 `sys-ticon` 声明系统模块 ID 时，我照着别的 sysmodule 的样子**随手写了一个** `0100000000001000`，连注释都写好了。写完才反应过来：这个字段的**全部意义**就是「实测出来的真值」（第 63 条刚讲过它必须与上游对齐），而我对 sys-ticon 的包内结构一无所知。于是先下包看结构，实测是 `atmosphere/contents/00FF747765616BFF/`（twili 的通用 title），跟编的那个毫无关系。
    修法：把实测依据写进声明的注释里（连同「这不是猜的，一开始随手写的那个是错的」这句），并让新加的 `expectedTitles` 对照表覆盖它。
    > 一条经验：**这一族字段（title、版本号、文件名、落点）上没有「看起来对」这回事，只有「实测过」和「没实测过」。** 更值得记的是它的**成因**：刚给 4 个同类插件写完声明，第 5 个就顺着样式「补」了一个 —— 手上有大量同类样本时，最容易把「编一个像样的值」误当成「写一个声明」。**判据：写下这个值的时候，我的依据是「我读过它的包/文档」，还是「它长得像别的那个」？**
    **顺带一个副产品**：`sys-ticon` 的 Release 里同时有 `sys-ticon.zip`（正式版）与 `sys-ticon-log.zip`（带日志的调试版），两者装的是**同一个 title**、包内结构逐项一致。逐字命中当然拿正式版，但正式版一旦改名消失，「去版本号前缀」那级容错会把调试版选走 —— 这不是灾难（同功能，只是会打日志），但**必须留痕**。两条都用例钉住了，免得将来有人把那一级的判据改宽/改窄而无人察觉（「可达且被记录」比「不可达所以不用管」诚实）。

65. **「长得像 payload」的字段，标上去就变成一个静默的开关**（语义陷阱，2026-09-18 收尾之二十）— 「底层」那两个 `.bin`（`Lockpick_RCM.bin` / `TegraExplorer.bin`）从名字、扩展名到落点（`bootloader/payloads/`）都和 hekate 的引导 payload 一模一样，最自然的做法就是「为了保持一致」给它们标上 `IsPayload = true`。而 `IsPayload` 的真实语义不是「这是个 payload 文件」，是**「散装下载的、由『把 payload 放到对应目录』这个开关单独控制的引导 payload」** —— 带它的文件在 `OutputStager` 与 `MainViewModel` 两处都有 `if (IsPayload && !IncludePayloads) continue`，会被那个开关**整个跳过**。
    后果：用户勾了「Lockpick_RCM」、没勾那个开关（它默认虽为 `true`，但用户完全可能因为只想要组件文件而关掉），产物里**没有这个文件，日志里也只有一行「跳过 payload」，退出码 0** —— 又是一个「错误被合法分支吸收」。
    修法：不标它，并且**不是只钉这两个**，而是新加一条**穷举所有文件槽**的断言（`slotComponents.SelectMany(d => d.Slots)` 里不许有任何 `IsPayload`）。理由：这条断言恒真与否，取决于「将来有没有人标」，而不是取决于「我记得住几个特例」。同时反向钉住这两个的 `Source == Release`、落点恰为 `bootloader/payloads`、`Extract.KeepsEverything`（散装文件不该有解压筛选）。
    > 一条更普遍的判据：**当一个字段的「字面含义」与「它在代码里实际触发什么」不一致时，凡是长得像的条目都会被顺手标上。** 判据不是「这个文件是不是 payload」，而是「我加上它之后，哪些既有开关会开始管它」。

66. **「一次请求」与「N 次请求」的差别不在速度，在配额**（2026-09-18 收尾之二十一）— 目录槽原来对每个文件各发一次请求（`raw` 不可达时就是一次 API 调用），20 个补丁 = 20 次；未登录配额 60 次/小时，实测第 17 个就被 403 拦下。当时的第一反应是「让用户填 Token」，但更本质的问法是**「这 20 个文件本来就在同一个仓库里，为什么要问 20 次？」** —— 换成 `codeload.github.com/<owner>/<repo>/tar.gz/HEAD` 后一次拿回整棵树，**且 codeload 不是 API 端点、不消耗配额**。三个值得记的点：
    ① **符号引用比分支名稳**：`HEAD` 不必先查默认分支（省一次 API 调用），且上游把 `master` 改名 `main` 也不会失效。代价是 tarball 根目录名**是动态的**（实测同一仓库两种形式都出现过：`theme-patches-HEAD/` 与 `exelix11-theme-patches-e9ba165/`），所以**不能**复用只吃常量前缀的 `ExtractPlan.StripPrefix` —— 新写的纯函数是「剥掉第一层、不管它叫什么」。
    ② **新通道的产物必须与老通道同构**：解出来的东西包成同样的 `RepoDirectoryFile`、走同一个 `PickRepoDirectorySlot` 入口，于是落点、备用通道、逐槽对账**全部自动一致，下游一行都不用改**。反过来，如果让快路径自己写文件、绕过 picker，就会多出一套需要单独维护的落点逻辑 —— 那是 bug 的温床。
    ③ **「已下载」要用标记，不能用 `File.Exists` 事后判断**：后者会把**上一次运行残留的旧文件**也算成已下载，于是「换个版本重跑」会**静默沿用旧内容**。这是同一个坑的第三种形态（前两种：把「计数」当「完整性」、把「回退默认值」当「没问题」）。
    > 判据：**当一个操作「每个条目都要问一次」时，先问「这些东西是不是同一个来源」** —— 若是，通常有一次拿回全部的办法，而那个办法往往还绕开了配额。

67. **「两个都真实存在」的选项不是容错问题，是用户意图问题**（2026-09-18 收尾之二十二）— `TOM-BadEN/KeyX` 的 Release 里挂着两个包：`KeyX-CN.zip`（991665 字节）与 `KeyX-EN.zip`（991658 字节），**只差 7 字节**。差别在哪，**逐条目量过**（2026-09-18，见第 69 条）：两包 9 个条目名字集合完全相同、其中 8 个逐字节相同，唯一差异是 `ovl-KeyX.ovl` 里内嵌的**显示名**（CN 版中文「按键助手」、EN 版 `KeyX`）—— 也就是呼出菜单里那一行字。我一开始想把它当成「容错瀑布要解决的问题」——「名字对不上就猜一个」。**这个归类是错的**：容错瀑布的前提是「**你要的那个不存在了**，我尽力找一个替代并留痕」；而这里**两个都存在、都可用**，选哪个是**用户意图**，不是猜测。把意图问题塞进容错机制，后果是「中文用户拿到英文界面，日志里还写着『逐字命中』」—— 看起来一切正常。
    修法：判据只能是界面语言（`ComponentCatalog.KeyXFileName`，与 DBI 的翻译文件同一套求值方式），四个语言方向都验过（`zh-Hans`/`zh-Hant` → CN，`en-US`/`ja-JP` → EN）。
    > 判据：**先问「这个选项的候选是「都不存在了」还是「都存在」」** —— 前者是容错（该猜、该留痕），后者是配置（该按意图取，绝不能猜）。把后者写成前者，是**用「尽力而为」的外衣掩盖「没问用户想要哪个」**。
    > **注入验证**：把 `KeyXFileName` 改成恒返回 `KeyX-EN.zip`（模拟「两个差不多，随便挑一个」）→ 见下方注入记录。

68. **「不声明」也是声明**（2026-09-18 收尾之二十二）— 这一批里有三个包内含 sysmodule（emuiibo 的 `0100000000000352`、KeyX 的 `0100000000251020` 与 `4100000002025924`），而 `AssetSlot.SysmoduleTitleId` 的文档注释写着「为空表示**不是 sysmodule**」。我最终**一个都没声明**，理由是：KeyX 装**两个** title 而那个字段是**单值**的（写一个就是半个真话），且现有约定只有「冲突可预期」的才声明（组件类里的 `SysClk`/`SysDvr`/`LdnMitm` 同样是 sysmodule、同样没声明）。
    这件事本身没问题，但它暴露了**文档与代码在说两件事**：字段的实际语义是「**未声明、不参与 title 冲突检测**」，而不是「不是 sysmodule」。于是我把那段注释改成了前者，并把实测到的 title 写进声明处的注释、细节档里也留了一份。
    > 判据：**当一段注释描述的规则和代码的实际做法不一致时，先想清楚「是代码漏了」还是「注释旧了」** —— 然后修**对的那个**。默认该怀疑注释：注释不会让任何断言变红，所以它会一直错下去。
    > ⏳ 待办：若要把这条约定收紧成「所有 sysmodule 都必须声明」，得先把 `SysmoduleTitleId` 改成**多值**，那时 `FindTitleConflicts`、护栏 ⑨、那张实测对照表三处要一起动。
    > ⚠️ 同一段注释里的**枚举**也漏了一个：写的是「三个包内含 sysmodule」，逐包扫 `atmosphere/contents/<16 位>/` 后实际是**四个** —— `ReverseNX-RT.zip` 里还有 `0000000000534C56`（ASCII 拼出来是 "SLV"，即 SaltySD，ReverseNX-RT 依赖它）。结论（与已声明的五个 title 无交集）不变，但**枚举同样要扫全集再写**。

69. **数字是量过的，理由是编的 —— 于是理由借了数字的可信度蒙混过关**（2026-09-18 收尾之二十三）— 给 KeyX 写「按界面语言取哪个包」的注释时，两个包的字节数（991665 / 991658，只差 7 字节）我**真的量过**；顺手又补了一句理由：「实测 CN 包的 `switch/.overlays/lang/KeyX/` 里有 `zh-tw/en/ja/de.json`，**EN 包则只有英文那一份**」。**那句是编的。** 逐条目对比后的真相是：两包 9 个条目**名字集合完全相同**，其中 8 个（**含全部四个语言 json**）逐字节相同，唯一差异在 `switch/.overlays/ovl-KeyX.ovl` 里的 **144 个字节** —— 覆盖层内嵌的显示名。
    结论（中文界面取 CN 包）**没变**，所以**没有任何断言会红** —— 这正是它危险的地方：**一段注释里混着「量过的数」和「编出来的理由」时，读者（包括我自己）会用前半段的可信度替后半段背书**，而后者才是真正需要依据的那半。写这段注释时我甚至没觉得在编 —— 它「听起来就该是这样」（中文包嘛，当然只有中文）。
    > 判据：**写完「实测」两个字，立刻问「依据是什么」** —— 是「我跑过 / 看过 / 数过」，还是「它应该会是这样」？后者要么改成「未实测」，要么当场去测。**「数字是量过的」不能给「理由是编的」担保。**
    > 同一类错误在这批里出现了**三处**（`ComponentCatalog.KeyXFileName` 的文档注释、`ConfigSmokeTest` 里照抄它的段落注释、README 第 67 条那句含糊的「差别只在覆盖层界面语言」）—— **假话会被人（包括我自己）从一处抄到另一处**，所以发现后要按「所有出现过的地方」清一遍。

70. **护栏自称覆盖的范围，往往比它真能覆盖的大**（2026-09-18 收尾之二十三）— 「Ultrahand插件」那 6 个组件的落点是 `out/` 根，依据是实测「包内路径已以 SD 卡根为基准、没有多余的包装目录」。我在那条断言旁写了它的作用：「上游哪天改了打包方式（多出一层 `<仓库名>/`），落点会全错但不会有任何报错 —— **那时它会红**」。**这句是假的。** 那条断言读的是 `slot.Extract.KeepsEverything` 与 `slot.Targets.Count == 0` —— 全是 **C# 声明**。上游改了打包方式，声明一个字都没变，它**照样是绿的**。它真正钉得住的只有「**有人去改这 6 个组件的声明**」（那时逼他先解释包结构是不是变了）。
    > 判据：**给一条断言写「它的作用」之前，先把它读的输入列出来** —— 输入里没有的东西，它不可能覆盖到。**「离线回归钉声明、联网 e2e 钉上游」，这两半边不能互相冒充。**
    > 这与「恒真断言 = 橡皮图章」是同一族，但更隐蔽：**断言本身不假，假的是它自称覆盖的范围** —— 而这类假话恰恰藏在注释里，永远不会红。

71. **「跟着语言变」的声明，护栏必须在全部语言下求值 —— 只取一个语言 = 半个穷举**（2026-09-18 收尾之二十四）— `linkalho` 是本项目第一个**按界面语言换仓库**的槽（中文走汉化镜像 `SwitchScriptTW/linkalho`、其余走上游 `impeeza/linkalho`）。为此把 `AssetSlot.Address` 从 `RepoSpec` 改成了 `Func<string?, RepoSpec>`（与 `FileName` 对称）。改完编译一过，测试里那 6 处 `s.Address` 只要写成 `s.Address(null)` 就能编译通过 —— **而那正是坑**：`Address(null)` 对 linkalho 只会返回 EN 仓库，**CN 仓库从此不会被任何断言看见**。它恰好又是个「声明过的静态字段」，所以「声明的仓库都有入口」那条检查也**照样绿**（`DeclaredRepos` 是反射扫字段，扫得到它）。真正的修法是引入 `UiLanguageCodes`（三份语言包）并**逐个语言求值**，于是那几条既有检查自动变成了穷举。
    > 判据：**一个 `Func<语言, T>` 型的声明，没有「它的值」这回事，只有「它在每种语言下的值」** —— 护栏比对的集合必须是「全部语言求值结果的并集」，否则漏掉的那一半永远没人管。这与第 1 条教训（计数断言 ≠ 完整性断言）同源，只是这里「不完整的集合」是由**求值方式**造成的，不是由计数造成的。

72. **想知道压缩包里有什么，不必把包下回来 —— 中央目录在文件末尾**（2026-09-18 收尾之二十四）— 这一轮要确认 7 个包的**包内结构**（有没有多一层包装目录、`wiliwili.nro` 在不在子目录里），而本沙箱把下载限速到 ~25 KB/s：`wiliwili` 一个包 18 MB，光它就要十几分钟，7 个包下来一小时的活。改用 **HTTP Range** 只取「末尾 128 KB」找到 EOCD 记录，再按它给的偏移取回**中央目录**那一段 —— **几十 KB 就拿到完整条目清单**（文件名 + 原始大小 + 压缩大小），7 个包几秒钟全部拿到。
    > ⚠️ 两个前提：服务器要支持 Range（`206` + `Content-Range`），且要能取到 EOCD（末尾 128 KB 够用，除非有超大注释字段）。
    > 更重要的**局限**：中央目录只给**条目名与大小**，给不了**内容**。所以它能回答「包里有没有多一层目录」「某个文件在不在」这类**结构**问题，回答不了「两个包里那个文件是不是逐字节相同」—— 后者仍然只能真下载（见第 69 条，那条编造的实测正是栽在「只看结构、没比内容」上）。**知道一个技巧能回答什么、不能回答什么，比会用它更重要。**
    > 这个技巧已经沉淀成项目工具 **`tools/zip-listing.py`**（`python tools/zip-listing.py <owner/repo> <资源名>`，可一次给多组，或 `--specs-file` 批量）—— 它是「**只看包内结构、不下载整个包**」的通用入口，与 `check-upstream-facts.py`（核对注释里的实测）分工不同：前者回答「包长什么样」，后者回答「我写下的那句话是不是真的」。⚠️ Token 同样**只从 `GITHUB_TOKEN` 环境变量读，绝不写进文件**。

73. **注入验证的第一次「没触发」，问题往往出在注入本身 —— 对称的注入证明不了任何东西**（2026-09-18 收尾之二十四）— 给 `check-upstream-facts.py` 新增第 ⑤ 项（「按语言切换的候选包，包内路径必须一致」）后照例做注入验证：给包内路径集合凭空加一条 `__INJECTED__/only-in-one.zip`。结果**检查没红**。第一反应是「这条检查是橡皮图章」—— **错**：我的注入给**两个包都**加了同一条路径，于是它们仍然相等。改成「只给其中一个包加」（`if "v2" in name`）后立刻红，并精确点名差异条目。
    > 判据：**注入的前提是「让被检查的那个条件真的不成立」**。这与第 4 条教训（先证明「X 可能发生」）是同一件事，只是发生在**验证护栏的人**身上 —— 我先证明了「这条检查能红」吗？没有，我只是**运行了一次没红的实验**，而没红的实验什么都说明不了。**做注入验证前，先用手推一遍：按这个改法，那个条件到底成不成立？**
    > 顺带一条同类：这项检查**第一版只比了「第一层目录名」**（`{n.split("/")[0]}`），而落点声明（`StripPrefix` / `KeepFileNames`）作用在**包内完整路径**上 —— 自称的覆盖面大于实际，属第 70 条那一族。已改成比完整路径集合。

74. **「非逐字命中」不等于「猜」—— 把「按你给的模式取到」写成「没精确匹配、已替你猜了一个」，是在教用户改掉他自己给的名字**（2026-09-18 收尾之二十四）— 联网 e2e 跑到 TriPlayer 时，`run.log` 里冒出这条 Warning：「插件文件「TriPlayer」声明的「triplayer-x.x.x.zip」在 Release 里没有精确匹配，已按模式选用「triplayer-1.1.1.zip」（匹配方式：Wildcard）。**若这不是你要的文件，请在高级设置里改成确切的文件名。**」—— 可**用户清单里给的就是这个通配写法**（`triplayer-x.x.x.zip`），模式是他自己的意图（跟着版本走）。这条提示等于在建议他改掉自己刚给的名字。
    > 根因在 `AssetMatchKind` 的分类：它的注释写着「除了 `Exact`，其余都是『用户给的名字没对上、我们替它猜了一个』」，于是调用方按「非 `Exact` 就报 Warning」处理。而四级瀑布里**前两级都是「如你所愿」**：① 逐字命中；② **照用户给的模式**命中（`x` / `X` / `*` 是他亲手写的占位符）。只有 ③（去掉版本号比前缀）与 ④（唯一同扩展名候选）才是**猜** —— 那两个是「声明里根本没有模式，我们降级挑了一个」。
    > 修法：把「算不算猜」抽成**单一真源** `AssetMatchKindExtensions.IsGuess(kind)`（只有 `Stem` / `SoleCandidate` 为真），`AssetMatch.IsGuess` 与 `SlotMatch.IsGuess` 都从它取；日志按它分流 —— 猜报 **Warning** 并建议改名，**按模式命中只报一句中性 Info**（`Log.AssetPatternMatch`）。⚠️ 两个消费点各写一遍 `Kind is Stem or SoleCandidate` 迟早会漂开，而漂开的后果是**日志级别不一致，没有任何断言会红**。护栏用 `Enum.GetValues<AssetMatchKind>()` 穷举，将来新增一级会自动落进来。
    > 判据：**问「这个结果是用户要的吗」，而不是问「它是不是逐字命中的」** —— 前者决定该不该告警，后者只是个实现细节。这与第 6 条教训（错误被「合法分支」吸收时行为测试够不着）同源：**「非 `Exact`」正是一个合法分支**，把告警挂在它上面，就把「用户自己的意图」和「我们的降级」混成了一类。
    > 同一次核对还查出一条：`linkalho` 的 EN 侧包名我写成了**实测到的** `linkalho-v2.0.2.zip`，而用户清单给的是**模式** `linkalho-x.x.x.zip` —— 把声明「具体化」成实测版本号，会让**每次上游发版都让逐字命中失效一次**。已改回模式，并给「这个模式确实能对上上游的真实名字」补了一条拿**实测名字**喂匹配器的断言（只钉字符串相等不够：把模式写成 `linkalho-zzz.zip` 也「相等」）。**「声明」与「上游实测」是两份不同的东西，不能互相覆盖。**

75. **修完一个静默错误，要接着问「这个修法会被改回去吗、改回去时谁会红」**（2026-09-18 收尾之二十四）— 第 74 条把「按模式命中」从 Warning 改成 Info 之后，我盯着那段代码又看了一遍：**这个修法本身没有护栏**。原来的写法是 ViewModel 里三段内联 `if`（`非 Exact 就 Warning`），改完是 `switch (match.Kind.NoticeFor())`。可**谁把它写回内联 `if`，不会有任何断言变红** —— 分类表一个字都不会变，所有行为断言全绿，而日志又变回「请改成确切的文件名」，继续教用户改掉自己给的通配名。
    > 两个动作。① **把「哪些命中算猜」抽成单一真源**：新增 `SlotMatchNotice { Silent, PatternHit, Guess }` 与 `AssetMatchKindExtensions.NoticeFor(kind)`（`switch` 表达式 + **`default` 直接抛异常**，新增枚举值没归类会当场炸），`IsGuess` 改成 `NoticeFor() == Guess` 的派生 —— 于是「算不算猜」和「该说什么」物理上只有一处能改。② **补源码契约**（分类对、日志级别照样可能是错的，而那要联网 + 恰好按模式命中才触发，行为测试够不着）：扫 `MainViewModel.NoteSlotFilled` 的方法体，断言它**调了 `NoticeFor()`**、**不出现任何 `AssetMatchKind.` 直接比较**、两条日志各自**报了正确的级别**（`Log.AssetPatternMatch` 附近必须有 `LogLevel.Info` 且**不能有** `Warning`；`Log.AssetFuzzyMatch` 反过来）。
    > 判据：**修 bug 的「修法」与 bug 本身是两件事** —— 前者也需要一条断言。特别地，**如果修法是「把判断挪到一个地方」，那就要钉住「没有第二个地方还在判断」**（第 7 条教训的另一副面孔：编译器管不到的地方要单独立护栏，而 ViewModel 里的 `if` 编译器永远管不着）。⚠️ 写这类源码扫描时**先剔除注释行**，否则解释「为什么不这么写」的注释会把断言弄红。

> **收尾之十九的注入验证**：把目录槽声明里的 `Source = SlotSource.RepoDirectory` 整行注释掉（退回默认的 Release 语义）→ **通过 1348 项、失败 5 项，5 条全部在新增的 ⑩ 段**，而且每条都直接点明后果：「必须走 RepoDirectory —— 走 Release 会去查一个从来没有 Release 的仓库」「应恰好有一个目录槽」「只有 NXThemesInstaller.nro 那一个槽该去查 Release」「目录槽不该被算进 Release 请求里」「界面上的目录槽标记没了」。旧断言一条没红 —— 证明这一段真的钉在声明上，而不是橡皮图章。还原后 `grep -rn "INJECTION"` 为空并**重新构建**才继续跑。
>
> **另一条更有意思的**：补完 3 个新组件后第一次跑回归，**只失败 1 项** —— ⑨ 段那条覆盖性断言「声明了 `SysmoduleTitleId` 的组件都要在这张实测对照表里有值；缺的：`SysTicon`」。这正是那条护栏存在的意义：新加一个 sysmodule 时，**不用任何人记得回来补表**，它自己会报出来。（顺带说明：这条护栏上一轮刚加，这一轮就抓到了东西。）

> **收尾之二十的注入验证**：给 `LockpickRcm` 槽加上 `IsPayload = true` → **通过 1383 项、失败 1 项**，唯一失败正是新加的 ⑤b 那条，且直接点名「标了的：`LockpickRcm/LockpickRcm`」。**旧断言一条没红** —— 与 ⑤b 那段的意图完全吻合（它只对「有没有人标」这件事敏感）。还原后 `grep -rn "INJECTION"` 为空并**重新构建**才继续跑。
>
> **顺带做了一次「断言增量对账」**：这一轮基线从 1353 涨到 1384（+31）。按 MEMORY 第 1 条教训（计数断言 ≠ 完整性断言），一个**解释不了的增量**意味着某条护栏的**执行次数**变了，那可能是真问题。于是按组件名分组统计完整回归日志，31 条**全部命中新组件**，逐类对得上：`picker 选出来的文件都该在假下载目录里（<NEW> 缺少：）` 8 条（那个函数被调 4 次 × 2 组件）、`默认全选后应勾上全部 33 个组件` 1 条（33 = 4 框架 + 19 组件 + 5 后台 + 3 主题 + 2 底层 ✓）、其余 11 类各 2 条。**没有任何护栏的执行次数发生变化。**
>
> **本轮的一个操作教训**（与产品无关，但差点造成误判）：注入实验还在跑时我去 `grep` 了日志，看到失败行就**当成跑完了**，随即还原代码并启动下一轮 —— 而上一轮的测试进程还活着、锁着 DLL，于是构建报 `MSB3021/MSB3027`（看着像编译错误，其实是文件占用）。**判据必须是「最终汇总行」**（`通过 N 项断言，失败 M 项。` / `SMOKE: ALL OK`），中途的 `grep -c` 只能说明「跑到哪了」，不能说明「跑完了」。顺带一提，`ConfigSmokeTest` 断言失败时会 `return 1`，所以失败的那一轮进程要等它自己走完退出。
>
> **同一个坑的另一副面孔**（2026-09-18 第十轮）：这次不是「误判跑完」，是「误判**没在跑**」—— 我用 `tasklist //FI "IMAGENAME eq SwitchCfwWizard.exe"` 查进程，输出为空，于是判断 e2e 已经退出、`run.log` 却没生成，「看着像崩了」。其实进程（PID 80668）跑得好好的，是 **Git Bash 把 `//FI` 这个参数转义吃掉了**。**一律用 `tasklist | grep -i 名字`。** 两副面孔指向同一条规则：**别用一个自己没验证过的观察手段去下结论。**

> **收尾之二十一的注入验证**（两次，都精确命中，旧断言一条没红）：① 把整包地址改成 `api.github.com/repos/…/tarball/master`（模拟「用官方文档里的那个端点」）→ **通过 1406 项、失败 3 项**，三条全在新增的 ⑪ 段：「整包地址应是 codeload + tar.gz + HEAD」「整包地址不该走 api.github.com —— 那样又占一次 API 配额，快路径就白做了」「整包地址不该写死分支名（上游把 master 改名成 main 就会静默 404），要用符号引用 HEAD」。② 把「剥第一层」改成**写死根目录名** `"theme-patches-HEAD/"`（模拟照搬 `ExtractPlan.StripPrefix` 的思路）→ **通过 1408 项、失败 1 项**，正是那条「剥第一层目录时不该关心那一层叫什么 —— codeload 给 `<仓库名>-HEAD/`，legacy 形式给 `<owner>-<repo>-<sha>/`，两者都得能吃下」。
>
> **收尾之二十二的注入验证**：把 `KeyXFileName` 改成恒返回 `KeyX-EN.zip`（模拟「两个包差不多，随便挑一个」）→ **通过 1521 项、失败 3 项**，三条全是 KeyX 相关：`zh-Hans` / `zh-Hant` 两个方向「应取 `KeyX-CN.zip`，实际 `KeyX-EN.zip`」+「KeyX 的包名必须是上游实测存在的 `KeyX-CN.zip` / `KeyX-EN.zip`」。**旧断言一条没红** —— 与 ③b 那段的意图吻合（它只对「中文界面该拿哪个包」敏感）。
>
> **收尾之二十三的核对记录**（这一轮查的不是断言，是**注释里的「实测」**，所以没有注入实验）：`tools/check-upstream-facts.py` 从 `ComponentCatalog.cs` 现取 7 条清单联网核对 → **7/7 逐字命中、字节数全对、包内第一层全是 SD 卡根名、KeyX 两包差异与注释一致、包内 title 与已声明五个交集为空**，退出码 0。真正的产出是**查出了那条编造的实测**（第 69 条）：它骗过了回归（结论没变）、也会骗过 e2e（语言选对了、文件也落对了）—— **只有逐条目比对才看得见**。这正好说明为什么需要一个「把注释当待验证对象」的工具：**离线回归和 e2e 都只验行为，不验「我写下的那句话是不是真的」。**
> **联网 e2e**（收尾之二十三）：只勾 6 个 Ultrahand 组件跑 `--run` → 退出码 **0**、**0 个 Error**、6 个 Warning（全部是已知的 502 降级链）、`out/` **1327 个文件**、顶层 `SaltySD`/`atmosphere`/`config`/`switch`；`ovl-KeyX.ovl` 的 sha256 与 `KeyX-CN.zip` 内同名条目**逐字节相同**（证据留在 `.workbuddy-ai/scratch/e2e-ultrahand-evidence.md`）。
>
> **再顺带做了一次「断言增量对账」**（基线 1409 → 1524，+115）：按组件名分组统计完整回归日志，`grep '✓' 日志 | grep -c '<新组件名>'` = **116**，其中 1 条是**改了文案的旧断言**（「默认全选后应勾上全部 N 个组件」里的 33 变成 **39** = 4 框架 + 19 组件 + 5 后台 + 3 主题 + 2 底层 + 6 Ultrahand插件 ✓），所以**净增 115**，逐类对得上：24（`picker 选出来的文件都该在假下载目录里`，那个函数被调 4 次 × 6 组件）+ 84（14 类既有「每组件一条」的护栏 × 6）+ 4（KeyX 语言四方向）+ 3（KeyX 包名 / `SaveAs` / `Targets`）。**没有任何既有护栏的执行次数发生变化。**

> **收尾之二十四的注入验证**：把 `MainViewModel.NoteSlotFilled` 整段**写回修复前的形态**（`if (match.Kind == AssetMatchKind.Exact) return;` + 一律 `LogLevel.Warning`）—— 模拟「有人觉得 `switch (NoticeFor())` 太绕，顺手改回一眼看懂的写法」。这份注入**能编译、能跑**（它就是修复前的合法代码），所以只有源码契约拦得住它：**通过 1695 项、失败 5 项**，其中 **4 条正是新护栏**（「必须用 `NoticeFor()` 决定日志级别」「不该再出现任何 `AssetMatchKind.` 直接比较」「要有一句中性说明，用语言键 `Log.AssetPatternMatch`」「`switch` 要有 `default` 分支」），**第 5 条是既有的死键检查**（`Log.AssetPatternMatch` 没人引用了）—— **同一个回归被两个独立的护栏抓到**，这比一条独苗更让人放心。
>
> **护栏精度也验了**：注入版仍然对 `Log.AssetFuzzyMatch` 报 Warning，所以「「猜」必须报 Warning」「「猜」不能报 Info」这两条**没有红**；而「「按模式命中」必须报 Info」那两条因为前置条件（`Log.AssetPatternMatch` 存在）不成立而**被跳过**，于是总数是 1703 − 5 失败 − 3 跳过 = 1695。**红的正是该红的、不红的一条没多。**
>
> **另一条**（同轮，属第 75 条那条教训的反面）：新加的那几条断言，消息原本写成**诊断式**（「X 与 Y 不一致」）—— 而这套 `Assert(cond, message)` 在**通过时也会打印消息**，于是日志里出现 `✓ AssetMatchKind.Exact 的 IsGuess 与「哪些算猜」的清单不一致`：**一句通过了的假陈述**。本文件其余断言都是要求式措辞，已统一改成「应当等于…，实际 …」。**「话与事实不符」这一族不只长在注释里，也会长在断言消息里。**

76. **「8G 就别汉化了」是个不必要的取舍 —— 报 bug 的人是对的，错的是我们给的那个例外**（2026-09-18 第十五轮）— 用户报：「软件界面是简体中文，但是 Hekate 没有从 `easyworld/hekate` 下载」。
    > 先按规矩**实测再动手**（第 36 条那条教训）：只勾 Hekate、`zh-Hans`、**不勾** 8G 跑一次 `--run` → 日志里 `CTCaer/hekate 的下载源已改为 easyworld/hekate`、下到 `_sc.zip` 并解压 16 个文件。**这条路径本来就是好的**，所以没盲目去改它。当前代码里只有两处会把 Hekate 打回官方：① 勾了 8G 运存；② 在「下载源」里手填过官方地址。把这两条摆给用户之后，答案是 ①。
    > 根因是一个**以为不得不做的取舍**：汉化仓库 `easyworld/hekate` 只发 `_sc.zip` / `_tc.zip`，**一个 `.bin` 都没有**，而 8G 必须要官方那个 `__ram8GB.bin` —— 于是当初的结论是「8G 整个组件走官方」。可这两件事**本来就不冲突**：汉化包（`bootloader/**` + Nyx）和 payload 是两批文件、两个来源，**完全可以一个组件同时取两个仓库**。原来那条「8G 必须走官方」的注释把「哪一批文件」和「整个组件」混成了一件东西（与第 12 条「分辨率」「按图索骥」同族）。代价是：中文界面的 8G 用户长期拿到英文 Nyx，而日志里那一行写着 `CTCaer/hekate`，看起来还挺正常。
    > 修法：`ShouldUseLocalizedHekate` 去掉 `&& !options.Ram8Gb`（判据只剩「界面语言是中文」），并给 Hekate 加**第二个请求** —— 固定走官方、只取 8G payload 与那份备用标准 payload。
    > ⚠️ 三处不能想当然：**①** `RepoSpec` 是 record，`ResolveRepo` 判的是 `declared == HekateRepo`（比**值**），所以「另声明一个 `("CTCaer","hekate")` 字段把它区分开」**不管用**，必须显式带一个开关（`RepoRequest.AllowLanguageMirror`）。**②** 那个开关只能关掉「按语言换镜像」**那一条规则**，不能退化成「跳过整个 `ResolveRepo`」—— 否则用户在官方行填的地址会对 8G 那一路失效，界面上看不出来的死控件（第 50 条那个坑的另一副面孔）。**③** 第二个请求必须能被**查询之前**过滤掉（`RepoRequest.Applies`）：让 picker 返回空表也能跑，但会白花一次 API 配额、留一条「未找到匹配的资源文件」假告警，还把同一个版本号记进 `VersionText` 两遍（`v6.5.3 + v6.5.3`）。
    > 护栏：`CheckLocalizedHekateSource` ③b 钉住「两个请求 + 第二个必须钉官方」与 `Applies` 的三种取值（中文+8G 两个都查 / 中文+4G 只查一个 / 英文+8G 只查一个）；⑤c 则钉**跨语言等价** —— 「中文+8G（镜像包 + 官方 payload）」与「非中文+8G（官方一次拿齐）」的 payload 落点必须**逐项相同**，否则同一个 8G 开关会因为界面语言不同产出不同布局。真实 e2e 复跑：日志里两条「正在查询」（`easyworld/hekate` 取 `_sc.zip`、`CTCaer/hekate` 取 `__ram8GB.bin`），`out/payload.bin` 与 `out/bootloader/update.bin` 的 md5 与官方 `__ram8GB.bin` **逐字节相同**，标准版按约定落在 `bootloader/payloads/`、根目录那份被删掉。

77. **「改完没存」是静默的 —— 而自动落盘的定时器本身踩了一个「构造函数还没跑完就来通知」的时序坑**（2026-09-18 第十五轮）— 用户要求「打开软件后的所有操作写入配置文件，再次打开时读回来」。原先只有「保存设置」按钮与语言下拉会落盘，其余改动关掉就没了，而**失败是静默的**：界面上一切正常，下次启动才发现被打回原形。
    > 做法是在**通知那一层接线**（`MainViewModel` 的属性变更 + 组件勾选 + 配置项 `ValueChanged` + 下载源 + 插件文件槽），而不是回到几十个 setter 里各手写一次保存 —— 手写块漏一处就是同一个病，而且漏了不报错（第 7 条教训的同族）。
    > ⚠️ **第一次就崩了，而且是回归当场抓到的**：`NullReferenceException at ScheduleAutoSave`。根因是时序 —— 组件变更通知在构造函数**很早**就订阅了（`Components` 建好时），而构造函数**中途**就会去改 `IsSelected`（`UpdateForcedSelections` 的联动），这时定时器还没建 → 空引用。两个动作：**①** 定时器改成**字段声明处就建**（不依赖构造顺序）；**②** 加一道 `_autoSaveArmed` 闸，构造函数**跑完才上膛** —— 这第二道闸的用意是：构造函数中途那些赋值是「读回存档」和「按存档做联动归一化」，**不是用户操作**，上膛早了会把刚读进来的存档原样写回去，而「启动即写」会让排查的人无法用文件时间戳判断一个值是用户改的还是程序写的。两条都由 `CheckAutoSaveWiring` 钉住（含「上膛必须排在 `HookAutoSaveSources()` 之后」）。
    > ⚠️ **这道闸只关住了自动落盘那条路**：`SelectedLanguage` 的 setter 里独立挂着一句 `SettingsStore.Save(_settings)`，构造函数读回语言时照样写盘 ⇒ 「启动即写」一直存在，只是换了个入口。**一个闸管不住绕开它的第二条路**，见第 82 条（第十六轮才把它拆掉）。
    > ⚠️ 另一条同族：**「保存设置」按钮与自动落盘必须写同一份快照**（同一个 `SaveSettings()`），两条路各写一份赋值块的话，「点保存」和「改完自动存」的产物迟早不一样，而用户不知道该信哪份。护栏直接扫 `SaveSettingsQuietly` 的方法体，断言它调的是 `SaveSettings()`。（2026-09-19：那个按钮已按要求取消，自动落盘成了唯一调用者 —— 这条断言因此变得更重要，见第 85 条。）
    > ⚠️ 跳过表（`TransientPropertyNames`）是**性能跳过表、不是白名单**，判据是「漏一个名字会怎样」：漏了最多多写几次文件（无害），而白名单漏一个名字就是**悄悄不落盘**。所以宁可多写也不许少写，并补一条断言钉住「跳过表里不许出现 `settings.json` 的任何字段名」（顺带钉住表里的名字都还真实存在，防止改名后表自己腐烂）。
    > `DispatcherTimer` **只在消息泵里才 Tick** —— 控制台回归里永远不会触发，所以光有源码契约等于没有一条真跑过的断言。补了 `CheckAutoSaveBehaviour()`：显式推一个 `DispatcherFrame` 把 400ms 那一段泵过去，断言「刚改完还没落盘（节流生效）→ 等过去之后文件里真的是新值 → 反向再走一遍」。**「行为测试够不着」这句话要先问一句「是不是我没给它消息泵」。**

78. **一个组件要两个仓库时，「跳过解析」是最省事也最容易做错的写法**（2026-09-18 第十五轮）— 见第 76 条 ②：给 8G payload 那个请求钉官方地址时，最省事的写法是在调用点写 `if (request.AllowLanguageMirror) repo = ResolveRepo(...)`（跳过整个方法）。它能跑、8G 也正常，但**用户给官方行填的地址从此对 8G 那一路失效** —— 界面上那一行照样能填、能存、下次打开还在，下载时纹丝不动，正是第 50 条那个「死控件」的翻版。
    > 判据：**关掉一条规则时，要说清关的是哪一条**。`ResolveRepo` 里其实有**三条**规则（用户自定义 > 语言镜像 > 声明默认），要关的只有中间那条 —— 那就把 `allowLanguageMirror` 做成**参数**穿进去，而不是在调用点把整个方法绕过去。参数化之后，「用户填的地址仍须生效」与「镜像行的地址不许改道 payload」（那一行是「语言镜像」入口，跟过去就等于没有 8G payload）两条都有断言。

79. **「所有操作写入日志文件」—— 而「所有」两个字决定了实现方式，不是覆盖面**（2026-09-18 第十六轮）— 用户要求：把所有操作写进日志文件，出了问题方便排查；补充说明是「需要看到我点击了哪个按钮，输入了什么文字」。
    > 需求看着像「多写点日志」，其实是**两条覆盖面问题**：按钮 24 个（当时 25 个 —— 后一轮取消了「保存设置」按钮，见第 85 条）、设置项 76 个，逐个手写日志必然漏，而漏记是**静默的**（下次出问题时，日志里恰好缺的就是关键那一条）。
    > **按钮**：所有按钮都是 `Command="{Binding XxxCommand}"`（一处 `Click=` 都没有），于是把「建命令」收成唯一入口 `Command(CommandLabel, …)`，日志在那一层打。护栏直接断言「`MainViewModel` 里除该方法外不出现 `new RelayCommand(`」—— 新加按钮时**绕不过去**。标签不是一个键而是**若干键拼出来**的：界面上有 6 个「选择…」6 个「清除」，只记按钮文字等于没记（日志里会是一串「点击「选择…」」）；拼成「正版系统 · 启动图 · 选择…」才答得了「最后那一下到底点了什么」。最后一个键必须**就是按钮上显示的那句**（`Content="{loc:Loc X}"` 的 X），这条由扫 XAML 的断言钉住 —— 否则日志说的可能是另一个按钮。
    > **文字**：不做「哪个输入框触发就记哪个」（`TextBox` 是 `UpdateSourceTrigger=PropertyChanged`，打一个词会记一串），而是**拿两份设置快照做差**。能被改的东西必然出现在快照里，所以「有没有漏」不再取决于谁记得住；而它天然复用自动落盘那 400ms 的节流，连续输入被合并成一条。差异函数是**纯函数**（`SettingsSnapshot.Diff`），所以能对着固定输入断言输出。
    > ⚠️ **机密是硬约束，不是礼貌**：日志**是要发给别人看的**，`GitHubToken` 一旦写进去就收不回来。所以机密项只记「已设置 / 已清空」，判定按**键名包含** `Token`/`Proxy`/`Password`/`Secret` 而不是精确相等 —— 判据是「漏挡一个的后果远比多挡一个严重」（`Proxy` 常常长成 `http://用户名:密码@主机:端口`，同样按机密处理）。回归里那条断言是**注入一个假 Token，然后断言它在整个新增日志里一次都不出现**。
    > ⚠️ 保留策略（只留最近 10 份、只删自己前缀的文件）在正常路径下**一整个进程只跑一次**，集成测试根本触发不到 —— 为此把 `PruneOldFiles` 开成 `internal` 测试接缝直接调它。**「这条逻辑测不到」有时只是「没给它一个入口」，与自动落盘那条「行为测试够不着」其实是同一件事的另一面。**
    > ⚠️ 上面那几行样例（`运行库 .NET 8.0.31`、`系统 Microsoft Windows 10.0.26200`、`v0.1.0`）是**跑了 `--selftest` 之后从真实的日志文件里粘过来的**，不是照 csproj / 凭印象写的。会话头的全部价值就在于「它说的是真的」，编一行版本号等于把唯一的线索变成误导（第 69 条那条教训的另一副面孔）。

80. **`logs/` 早就被列进了「不该进发布包」的清单，可它一直到今天才真的存在**（2026-09-18 第十六轮）— 要加日志文件时先翻了一圈流水线，发现 `AppPaths.LogRoot` 这个属性**从第一版起就声明着、却没有任何人用过**；`build.sh` 的 `scrub_traces` 里写着 `rm -rf "$dir/logs"`，`check_shape` 会因它残留而**报错**，`refresh-dist.py` 的 `FORBIDDEN` 清单第一个版本里就有 `"logs"`。
    > 也就是说：**上游（基础设施）早就为这个功能预留了位置，只是功能一直没做**。这件事本身是个信号 —— 一个「声明了却没人用」的常量，和语言包里的死键是同一族线索（第 42 条）。
    > 但预留不等于接得上，真正做起来还剩两处**只有跑流水线才会暴露**的坑：① `rm -rf` 在本机沙箱里对 `*.log` 会被拦下（`scrub_traces` 的注释里记着这件事），所以必须把 `logs/` 也加进「删不掉就挪走」的兜底清单里，否则 `check_shape` 会红；② 那条兜底原来用的是 `ls -1`，而 `ls -1 logs` 列出的是**目录里的文件**，`mv` 走之后只会留下一个空目录 —— 必须写 `ls -1d`。**兜底逻辑自己也需要一条「它真的兜住了吗」的验证**，否则它只是把「报错」换成了「看起来很努力但什么都没做」。


81. **「界面 → 存档」的映射只许有一处，而且差异基线必须等于「保存时会写出来的那份」**（2026-09-18 第十六轮）— 做「改了什么都记进日志」时，日志的差异是拿两份设置快照做出来的。第一版直接 `Flatten(_settings)` 当基线，**首次运行**下日志的第一条就成了「设置变更：…（另有 110 项，见 settings.json）」—— 而用户一件事都没做。**日志的第一句话就是假的。**
    > 根因：`_settings` 里的 `Components` / `OptionValues` 是**等到保存那一刻**才被填进去的，构造期摊平只会读到「空」。所以「40 项组件 + 76 个配置项」全被算成「新增」。
    > 修法不是给首次运行打个补丁，而是**让基线等于真实产物**：把「界面 → 存档」的整段映射抽成唯一的 `MaterializeInto(target, …)`，构造函数拿一份**新读的存档副本**当探针、把当前界面取值搬进去、再摊平当基线。
    > ⚠️ 探针**不能**用 `_settings` 本身：它的 `Components` 被 `EnsureComponentsSelected` 当作「文件里到底有没有记录」的判据（`--run` 的首次默认全选就靠它），在构造期把全部组件条目塞进去会让那个功能当场失效。
    > ⚠️ 反向也要钉：`SaveSettings` 里**不许**再出现自己手写的 `_settings.X = `。两处各写一份的话，症状是「日志说的改动」与「文件里实际写的东西」对不上，而两句话都出自同一个程序，用户不知道该信哪一份。
82. **属性 setter 里写盘，是「启动即写」的后门**（2026-09-18 第十六轮）— 上一轮给自动落盘加了 `_autoSaveArmed` 闸（构造函数跑完才上膛），理由是「启动即写会让文件时间戳失去意义」。可 `SelectedLanguage` 的 setter 里一直挂着一句 `SettingsStore.Save(_settings)`，而构造函数末尾**正要把存档里的语言读回**成这个属性 —— 于是每次打开软件都实实在在写了一次盘，那道闸被从侧路绕过去了。
    > 更糟的是**它写的是没映射过的快照**：首次运行切一次语言，写出来的存档是 `"Components": {}` —— 而**空 `Components` 在别处是有语义的**（「用户从没勾过 ⇒ 命令行默认全选」，见第 81 条那个判据）。同一族的还有 `AutoPackZip` 的 setter。
    > 判据：**能立刻生效的是「界面」（`LocalizationService.SetLanguage` 这类），落盘一律交给统一落盘层**；属性 setter 不许碰 `_settings`、不许调 `SettingsStore.Save`。`CheckPropertySettersDoNotWriteSettings` 扫全部属性的方法体钉住这一条（扫描前必须**剔除注释行** —— 解释「为什么不这么写」的那段注释自己就写着 `_settings.Language`，第 75 条记过的坑，本轮又踩一次）。
83. **用例必须自己还原它改过的存档**（2026-09-18 第十六轮）— 新加的「操作日志」用例里有一句 `vm.BootSysNand = !vm.BootSysNand` 并落盘，于是它把 `settings.json` 里的引导项**永久翻了个面**；而后面那条「屏蔽序列号 ⇒ 强制勾上 Sys-patch」的判据是 `(屏蔽sysmmc && BootSysNand) || (屏蔽emuMMC && BootEmuNand)`，**含引导项** —— 于是这条断言的结果在连续几次运行之间**红绿交替**，而报告里只有一句「实际：Ultrahand」，完全看不出是引导项在作祟（最后是打印出磁盘上那份 `settings.json`、看见 `BootSysNand` 变成 `false` 才定的案）。
    > 三件事：① 那个用例外面套了一层「备份 / 还原」（顺带挡住了另一件事 —— 测试里那枚**假 Token** 会被留在用户的存档里，一个专门用来证明「机密不落日志」的字符串自己先落进了一个明文文件）；② 出问题的那条断言**自己把前提摆好**，不再沿用环境状态；③ 给 Main 末尾加了一条**幂等护栏**：跑完 `settings.json` 必须与开跑前**逐字节相同**。
    > ⚠️ 幂等护栏只会报「多出来一个文件 / 内容不一样」，**看不出是谁写的**（实测第一次就是如此）。为此又加了一个按**小节**定位的探针：`Section()` 每次调用时看一眼存档是否已经存在，把「最早出现在哪一节之前」写进护栏消息里 —— 一跑就指到了具体用例。
    > ⚠️ **延迟写也是写**：定时器是「几百毫秒后再写」，所以用例就算自己还原了，那枚 400ms 的自动落盘定时器仍会在还原**之后**补一刀。收尾必须先把表卸了（`FlushSettings()`）。
    > 判据：**一个用例的结果取决于上一次谁跑过，它就不再是断言，而是一枚硬币。**

84. **需求里那个目录名，包内根本没有 —— 它只能从文件名里造出来**（2026-09-18 第十七轮）— 用户要求：从 `THZoria/NX_Firmware` 下 `Firmware x.x.x.zip`，解压后放在 `out\Firmware\Firmware x.x.x\`。听起来是「下一个包、解压到某个目录」，实际去量上游之后是两件不同的事：
    > **量出来的两件事**（走 GitHub API 把 92 个版本逐个数过 + `tools/zip-listing.py` 读中央目录）：① 资源名一律 `Firmware.<版本>.zip` —— **用点分隔，从来没有空格**（用户写的是空格）；② 包内**是平的**：238 个 `.nca` / `.cnmt.nca` 全在压缩包根，**没有顶层目录**。
    > ⇒ 所以「`Firmware x.x.x` 这个文件夹」**包里不存在**，只能从**下载下来的那个文件名**推导。做法是给落点加一个占位符 `{asset}`（= 下载文件名去扩展名），声明成 `new OutputTarget("Firmware/{asset}")`。
    > ⚠️ **有歧义时不要替用户选**：用户写的是空格、实测是点，我没有默默按任一种去造名字，而是把两条实测摆出来让他选 —— 他选了「跟资源名一致（点）」，于是落点是 `out/Firmware/Firmware.23.0.0/`。**「先量、再问、最后动手」的顺序不能省**（第十五轮那个 Hekate 的 bug 也是这么定的案）。
    > ⚠️ **替换点必须在「落点的唯一出口」**（`AssetPick.ResolvedTargets`）：`OutputStager` 的压缩包分支读 `ResolvedTargets[0].Directory`、散装分支读 `Resolve()`，两处都从那里取。放到调用点去替换就是「两条分支各写一份」，迟早分叉。
    > ⚠️ **只验「属性算得对」是不够的**：谁把 `OutputStager` 改回去读裸的 `Targets`，属性那几条断言**照样全绿**，而产物会落进一个字面量的 `out/Firmware/{asset}/` 目录。所以单独立了 `CheckFirmwareStaging()` —— 用**实测的真实条目名**造一个压缩包，走一遍 `StageComponentAsync`，断言文件真的解在 `Firmware/Firmware.23.0.0/` 下、且**没有任何**目录名里带 `{asset}`。
    > ⚠️ **默认文件名必须是「模式」而不是版本号**：`Firmware.x.x.x.zip`（`x` 是版本占位，走匹配的第二级瀑布）。写死 `Firmware.23.0.0.zip` 的话，上游一发新版这个默认值就当场作废 —— 而用户完全不知道自己「改坏」了什么。代价是日志里那条会标成「通配」而非「逐字命中」；这是**如实**的，想锁定版本就把名字改成具体版本号（那时就是逐字命中）。

85. **镜像站：先量清它代理什么、不代理什么，再决定接在哪一层**（2026-09-19 第十八轮）— 用户给了一个 GitHub 加速镜像的用法（把 `github.com` 主域名换成镜像域名、raw 走 `<镜像>/raw/…`），问能不能做成高级设置里的一项、以后新增插件也要用到它；同一条消息还要求取消「保存设置」按钮、改成改完即存。
    > **先量上游**（这一条链的判断全部来自实测，逐条 `curl` 打过）：① `github.com/<owner>/<name>/…` → `<镜像>/<owner>/<name>/…` 200，**包括 340 MB 的 Release 资源包**（`content-length` 正确、`application/octet-stream`）；它的 302 目标是**镜像自己**的 `/proxy/release-assets.githubusercontent.com/…`，即整条下载都走镜像 —— 不是「只给个跳转、字节还是从 GitHub 拉」。② raw 走 `<镜像>/raw/…`，取回真实内容。③ api / codeload 这类**其它主机**走 `<镜像>/proxy/<主机>/…`：`<镜像>/proxy/codeload.github.com/<o>/<n>/tar.gz/HEAD` 回 200 + `application/x-gzip`，`<镜像>/proxy/api.github.com/repos/…` 回 **403 + application/json**（GitHub 的限流答复 ⇒ 请求真的代理过去了）。**三种主机三种写法**。
    > ⚠️⚠️ **本轮最大的一个教训：我量错了形态，还差点把它写进交付物。** 第一次只试了按「主域名换掉」拼出来的 `<镜像>/api.github.com/…`，回 404，于是我判定「镜像站不代理 API」，并据此写了「查询只能保持直连」「连 API 都不通的网络镜像帮不上忙」——**这几句都是假的**。真正的规则是通用前缀 `/proxy/<主机>/`，而**镜像自己那个 302 目标**（`/proxy/release-assets.githubusercontent.com/…`）早就把这个规律摆在眼前了，我却没把它推广出去。教训有两条：**① 一个「不支持」的结论必须问一句「是不是我拼错了」**（只试一种拼法就下结论，等于把「我不会拼」当成「它不支持」）；**② 项目技能 `switchcfwizard-setting-wiring` 的 §5 早就记着这条实测事实，我动手前没读它** —— 同一件事被量了第二遍，还量错了一遍。凡「这个仓库里可能有人量过」的问题，先查技能与 details 再动手。
    > ⇒ 结论：镜像**既管取字节、也管查询**，但两边的用法**故意不同** —— 下载「填了就直接走镜像」，查询「官方优先、只有**连接层**失败才改走镜像」。因为镜像出口 IP 是**公用**的，未登录的 API 额度按 IP 算：实测把查询直接改走镜像，**第一次查询**就 `403 API rate limit exceeded for 104.23.168.80`，即「填了镜像站反而一个组件都查不到」。`403` 是 GitHub 的**回答**而不是网络问题，所以判据收窄成连接层失败（换了只会撞上更挤的额度，还把真正原因盖掉）。
    > ⚠️ **改写落在两处「唯一出口」**：下载在 `DownloadService.DownloadAsync`、查询/列目录在 `GitHubReleaseService.GetReleasesAsync`/`GetDirectoryFilesAsync`。放调用点的话，将来新增一条下载路径就会静默漏掉镜像，症状是「有的包走镜像、有的不走」—— 用户只会觉得「镜像时灵时不灵」。源码契约钉住了这三条（含「MainViewModel 必须把镜像传给这两个服务」）。
    > ⚠️ **两条下载通道都要改**（直链 + API asset 端点）：只改直链会出「直链走镜像、备用通道偷偷回官方」的半吊子状态，而官方地址恰恰是镜像存在的理由。桩 `HttpMessageHandler` **记下实际请求的 URI** 来验（`CheckGitHubMirror` ③，不联网）：没配镜像一个字不变、配了打在镜像上（两条通道各一条断言）、填错整条退回官方。
    > ⚠️ **日志要如实**：下载前那两行打印的是 `GitHubMirror.Apply(...)` 的结果，而不是自己手里的直链 —— 印一条谁也没请求过的地址，会把「镜像挂了」读成「GitHub 挂了」，方向全错。它调的是与下载器**同一个纯函数、同样的入参**，所以不可能印错。
    > ⚠️ **幂等 + 拦「粘了仓库路径」**：已经在镜像站上的地址不再改一次（再改会得到 `<镜像>/proxy/<镜像>/…`，一条必定 404 的地址）；把 `owner/repo` 整段粘进输入框时**判为非法**（它结构上完全合法，不拦的话每条地址都会被再拼一次仓库路径而 404，而用户手里那条地址看着就是对的）。
    > ⚠️ **第三方转发必须说出来**：请求经镜像转发，配了 Token 时 Token 也经它转发 —— 界面提示里写明，日志会话头**单独提醒一行**（`Log.Session.MirrorToken`）。「验证 Token」则**刻意不走镜像**（它回答的是「我自己的额度」，走镜像显示的是那个公用出口的额度，用户会以为 Token 坏了），这条由**反向**源码契约钉住，防「顺手也给它套上」。
    > **顺带取消「保存设置」按钮**（用户同一条要求）：改动本来就已由自动落盘写进 `settings.json`，留个按钮只是让用户猜「我改了没点它，到底存没存」。删按钮带出两件事：① `MaterializeInto` 上的 `reportInvalidAddresses` 旗标失去了唯一为 true 的调用者（成了**恒假的死参数**）⇒ 映射回归纯函数，非法地址告警单独成一步（`ReportInvalidAddressesOnce`）；又因为它的触发点从「用户按一次」变成了「自动落盘反复结算」，必须**按值去重** —— 否则边打字边刷屏，真正要看的那条被淹掉（镜像站填错也归进这一步，措辞指向 `Settings.Mirror.Invalid`）。② 用例里十几处 `SaveSettingsCommand.Execute(null)` 失去意义（它曾经**无条件写盘**，而现在的判据是「快照真的变了」）⇒ 新增 `TouchAndFlush()` 显式制造一次改动再落盘，免得用例红在一句与它无关的断言上。
    > 护栏：`CheckGitHubMirror()`（① 规范化 6 组 ② 三种形态改写 + 非 GitHub 不动 + 幂等 + 不成形不抛 ③ 桩里看 **URI**：两条下载通道各一条 + 填错退回 ④ 查询通道：官方优先、镜像兜底、没配镜像就一条都不许打到别处 ⑤ 源码契约：两处业务出口 + NetworkSignature + **「验证 Token」反向不许走镜像**）、`CheckMirrorSettingPersistence()`（写进 `settings.json` → **重开一个 VM** 读回来；只断言「界面上还显示着」等于没测），另有两条「**「保存设置」按钮不许回来**」（命令属性 + XAML 绑定各查一遍；WPF 的绑定失败是静默的）。
    > ⚠️ 造非法测试数据时踩了一个小坑，值得记下来：拿 `"这不是一个 owner/name"` 当「非法地址」—— 它**带一个斜杠**，而 `owner/name` 的判据正是「恰好两段」，于是它其实**合法**，断言当场红了。红的原因是数据不对，不是程序不对 —— 「造不出预期的坏输入」时先怀疑输入，别急着改被测代码。



---

## 九、待你确认 / 已知限制

1. **emuMMC 分区创建没做** — 这步必须在 hekate 的 Nyx 界面里操作（Nyx → emuMMC → Create emuMMC），本工具只生成配置，不代劳。
2. **payload 注入没做** — 首次进 CFW 需要 RCM 注入 `hekate_ctcaer_*.bin`（或用 AutoRCM），属于硬件操作。
3. **引导项名称可自定义，留空则用固定默认名** —— `[zbxt]` / `[zspjxt]` / `[xnpjxt]` 是 hekate 菜单里显示的文字。这三个默认名**与界面语言无关**（三份语言包取同一个值，理由见第二节）；想换成自己的叫法，就在每个引导项后面的「显示名」框里填一个（见第二节）。改了名字之后，界面上的 `autoboot` 候选项会同步显示新名字 —— 两处共用同一套解析规则，不会出现「界面显示 A、文件里是 B」。
4. **`kip1=atmosphere/kips/*` 已做成开关**（默认关闭）——位于「hekate_ipl.ini · 引导项附加参数」里。开启后会把 `atmosphere/kips/` 下的额外 kip 一并加载（模拟 fusee 行为）。目录不存在时 hekate 会提示但能正常启动，所以默认没开。
5. **`cal0blank` 与 `exosphere.ini` 已自动保持一致** — hekate 的引导项参数会覆盖 `exosphere.ini` 的 `blank_prodinfo_*`。现在按「屏蔽序列号」的勾选同步写 `cal0blank=1`（正版系统项不写），不会再出现「exosphere 说屏蔽了、引导项又放开了」这种自相矛盾且完全静默的组合。
6. **首次运行组件默认一个都不勾，而 `--run` 会自动全选** — 两者刻意不一致：界面让用户自己挑要装什么（没勾任何组件时「开始」按钮是灰的，组件列表上方会显示「请至少勾选一个组件」），命令行是无人值守路径、必须能一步跑完。想固定自己的选择，就在界面里勾好并保存一次 `settings.json`；**保存过之后程序不会再替你改勾选**（包括「全部取消」这种选择 —— 文件里会留下显式 `false`，`--run` 也会尊重它、不再替你全选）。
7. **Ultrahand 自定义 Overlay 内存是「加一档」而不是「直接设定」** —— 写入的 `[memory] custom_overlay_memory_MB` 只会让 Ultrahand 的 Overlay 内存滑条多出一个档位，仍需在 Ultrahand 里手动选中才生效。这是上游行为，不是本工具的限制；同理它只接受大于 8 的偶数，`4/6/8` 会被上游忽略，本工具因此直接跳过并警告。
8. **端到端已在开发环境跑通** — `--run` 完整走了一遍真实联网流程：Atmosphere 1.11.2（4.51 MB zip + 109 KB `fusee.bin`）、Hekate v6.5.3（874 KB zip + 107 KB payload）、Ultrahand（`sdout.zip` + `ovlSysmodules.ovl` + `nx-ovlloader.zip`）、Sys-patch v1.6.2.3（173 KB）全部下载并解压完成，随后生成配置文件，退出码 0，耗时约 6 分钟。需要注意：开发沙箱注入了 `HTTP_PROXY`，速度只有 40–70 KB/s 且偶发 502（`curl` 直连同样失败，属环境限制），你在本机直连应该明显更快。如果遇到下载问题，把 `run.log` 发我。
   - 落点修复之后又用**发布版**（`dist/` 里的 exe，不是源码版）跑了一次完整流程：4 个组件全下载 + 解压 → 合并 76 个组件文件 → 8 个配置文件 → 3 个 payload → 打包 7.03 MB zip → 回读校验「清单中的 86 个文件全部在包内」，退出码 0，这次只用了 **18 秒**。逐条核对产出的 `out/`：无 `sdout/`、无 `nx-ovlloader/`，`switch/.overlays/` 下三个 `.ovl`，`config/ultrahand/lang/` 下 14 个 json，`bootloader/payloads/fusee.bin`，`payload.bin` 与 `bootloader/update.bin` 同 md5 —— 六条修复全部在真实产出里成立。
   - 那次跑的是默认配置（`Ram8Gb=false`），所以 payload 是标准 4G 版；8G 分支由离线用例 + 真实包用例覆盖。
   - 落点改动之后又拿**已下载的真实发布包**重跑了一遍暂存 + 生成，逐条核对产物位置，结论一致（`payload.bin` 与 `bootloader/update.bin` 逐字节等于选中的 8G 版 payload，`lang.zip` 的 14 个 json 落在 `config/ultrahand/lang/`，`out/` 里没有 `sdout/` / `nx-ovlloader/` 这一层）。写这段时开发环境的代理对 GitHub release 下载已连续返回 502（应用按预期重试 3 次后跳过配置生成，避免产出一套不完整的 SD 卡内容），所以那次重跑是拿本机已有的真实包做的。**这条限制现已由「下载失败自动改走 API asset 端点」解决 —— 见第九节第 11 条。**
   - 「8G 模式清根目录标准版 payload」与「内置 boot.dat」两条需求落地后，又用**发布版**各跑了一次完整端到端：
     - **4G 默认**（59 秒）：`out/` 根目录保留 `hekate_ctcaer_6.5.3.bin`，并新出现 `boot.dat`（md5 `20cf385a…`，与项目根目录那份逐字节一致）；`payload.bin` / `bootloader/update.bin` / `hekate_ctcaer_6.5.3.bin` 三者同 md5。
     - **8G**（37 秒）：日志出现「已移除 out 根目录的标准版 payload `hekate_ctcaer_6.5.3.bin`」，该文件确实不在 `out/` 根目录、也不在压缩包里，而 `bootloader/payloads/hekate_ctcaer_6.5.3.bin` 仍在（可在 hekate 的 payload 菜单里切回 4G）。
     - md5 是这两次核对的关键证据：8G 版 `__ram8GB.bin` = `d453cf4544b6d109f0ce686824f47c0d`，标准版 = `486337b7b929e6679f310d9b7f2d595f`。**两者尺寸完全相同（都是 110028 字节），只看文件大小分不出 4G/8G。**
     - 两次产物的清单与包内文件都是 87 = 87 双向零差异；`--selftest` 报 `binding-errors=0`。
   - **备用通道落地后又从零跑了一次完整端到端**（7 分 32 秒，退出码 0）：清空 `download/` 与 `out/` 后跑 `--run`，四个组件全部下载 + 解压，生成 9 份配置文件，合并 76 个组件文件，打包 7.04 MB zip，回读校验「清单中的 88 个文件全部在包内」。这次日志正好把新通道的每一步都记下来了 ——
     Atmosphere 那个 4.51 MB 的 zip：**直链连续 3 次 HTTP 502**（19:08:43 与 19:08:55 两条普通「重试」，19:09:09 一条「直链连续失败…改用 GitHub API asset 端点重试…」），换通道后 **3 分 12 秒下载成功**。其余 7 个资源（`fusee.bin`、hekate 的 zip 与 payload、Ultrahand 三件套、Sys-patch）直链一次就过，**没有**被无谓地切到备用地址 —— 这正是「不第一次失败就换」想要的效果。
   - 紧接着又拿 **`dist/` 里的发布版**跑了一次同样的完整流程（2 分 27 秒，退出码 0，四个组件全 `[x]`，打包 7.04 MB、88 个文件全部在包内）。这一次代理状态好，8 个资源直链全部一次通过，**日志里连一条 Warning 都没有** —— 备用通道安静地待着没被触发。两次合起来正好是一对对照：**该用的时候能用，不该用的时候不打扰**。
   - ⚠️ 排查时踩到一个坑：**注入验证（故意改坏代码看测试会不会报错）只还原了源码，没有重新构建**，`bin/` 里留着的是被注入过的那份二进制。结果紧接着跑的 e2e 日志里出现「已改用备用地址」却**没有**「直链连续失败…换通道」那一行 —— 这两件事本来自相矛盾，一查才发现验的是注入版。**注入验证之后一定要先重新构建再跑端到端。**
   - 之后按组件分组又各跑了一遍**真实联网 e2e**：**「Ultrahand插件」6 个**（`out/` 1327 个文件，顶层四个 SD 卡根名，落盘 `ovl-KeyX.ovl` 与 `KeyX-CN.zip` 逐字节相同）与**「学习」7 个**（耗时 25 分 44 秒，10 个落点全命中，落盘 `linkalho.nro` 与**中文镜像仓库**的 `linkalho.zip` 逐字节相同）。⚠️ 这两轮是**分组**跑的，不是「全部组件一次勾完」—— 沙箱把下载限速到约 25 KB/s（`wiliwili` 一个包就 18 MB），全量一次跑要几小时。分组覆盖了每个组件的下载 / 解压 / 落点 / 生成路径，**没有**覆盖「全部组件同时存在时的合并与打包」（那一半由离线回归的逐组件对账覆盖）。
   - 上游事实核对（`tools/check-upstream-facts.py`）**首次完整跑通全部 15 条**：资源名逐字命中、字节数全对、包内第一层全是 SD 卡根名、两包差异与注释一致、title 交集为空、按语言切换的两个候选包包内路径完全一致，退出码 0。
9. **`fusee.bin` / hekate payload 已按上游约定放到对应目录** — 见第三节「payload 放到对应目录」，默认开启，可在界面里关掉。
10. **组件包里刻意带的空目录不会被还原** — 上游的 `sdout.zip` 里有几个只有目录项、没有文件的空目录（`switch/.packages/`、`config/ultrahand/downloads/`、`config/ultrahand/notifications/`）。合并进 `out/` 时只搬文件，这些空目录不会建出来。Ultrahand 在需要时会自己创建，实测不影响使用；如果你希望 `out/` 与压缩包结构完全一致，说一声我补上。
11. **下载失败时会自动改走 GitHub API 的 asset 端点（已实现）** — release 直链会 302 跳到 `objects.githubusercontent.com`，开发环境的代理恰好在这一跳上返回 502（实测直连 HTTP 000、代理 502），表现为「版本查询一切正常、下载全军覆没」。同一次会话里 `https://api.github.com/repos/CTCaer/hekate/releases/assets/<id>`（带 `Accept: application/octet-stream`）却能**完整下载成功**。

    现在 `DownloadService` 有**两条通道**：先把直链的重试机会（3 次）用光，之后自动改走 asset 端点再试 3 次。日志会明确写出来，不会让你以为是同一个地址在原地重试：

    ```
    [Warning] 下载 atmosphere-….zip 失败：代理或网络返回 HTTP 502（网关错误）。…（2 秒后重试，第 1/3 次）
    [Warning] 下载 atmosphere-….zip 失败：代理或网络返回 HTTP 502（网关错误）。…（4 秒后重试，第 2/3 次）
    [Warning] 下载 atmosphere-….zip 的直链连续失败：代理或网络返回 HTTP 502（网关错误）。…；改用 GitHub API asset 端点重试…
    [Success] 已保存到 …\download\Atmosphere\atmosphere-….zip
    ```

    （以上是真实跑出来的日志，见第九节第 8 条最后一条。）

    几个刻意的选择：

    - **不「第一次失败就换」** —— 直链偶发抖动很常见，为此去打 API 只会白白消耗配额（未登录只有 60 次/小时），而且备用地址同样要走同一个代理。要等直链真的没救才换。
    - **`Accept: application/octet-stream` 是必须的，而且是这条通道最危险的地方** —— 少了它 GitHub 返回的是这个资源的 **JSON 元数据**（**HTTP 200**，内容是一段 JSON）。程序会认为「下载成功」，把一段 JSON 当成组件包写进 SD 卡，一路静默到拷进机器才发现。为此下载**单独用一个不带 GitHub API 默认 Accept 的 `HttpClient`**（查询 Release 的那个 client 挂着 `application/vnd.github+json`），并且逐请求显式指定；万一还是拿回了 JSON（`Content-Type` 含 `json`），直接当失败报出来 —— 宁可失败也不写盘。
    - **非暂时性错误不换通道** —— 404 换过去也一样 404，只是白等退避。
    - **拿不到 asset id 就只走直链** —— 老 JSON / 手写样例里没有 `id` 字段时行为与以前完全一致。
   - **配了 GitHub Token 会带上 `Authorization`** —— asset 端点计入 API 配额，带上 Token 配额更宽松。这里有个看着吓人的隐患：asset 端点会 302 跳到 S3 的预签名地址，而 S3 对「URL 已带签名 + 请求头又带 `Authorization`」会判 **400**。查过 .NET 官方文档与 `RedirectHandler` 源码后确认**不会发生** —— 自动跳转时会清掉 `Authorization`（原文：*"The Authorization header is cleared on auto-redirects … No other headers are cleared."*，依据 `dotnet/runtime#122609`），所以 Token 只出现在发给 `api.github.com` 的那一跳上。

   护栏 `CheckDownloadFallback()`（**33 项**）：用桩 `HttpMessageHandler` **离线**跑完整条路径，逐条核对「直链用满 2 次才换通道」「换通道恰好报 1 次且标明已在备用地址」「asset 端点发的是 `octet-stream`」「client 挂着默认 Accept 时也盖得住」「返回 JSON 时抛错而不是写盘」「没有备用地址时异常照常抛」「备用地址与直链相同时不重复跑」「404 不换通道」「`Authorization` 只发给 asset 端点那一跳、直链不带」「`BuildDownloadFallback` 的三条分支（带 Token / Token 全是空白 / 拿不到 asset id）」「`ParseReleases` 解析出 id」。**不联网**，任何环境都能跑。

12. **【已清理】语言包里的 16 个历史遗留键** — 做死键审计时发现的：改名或改设计后没清掉的旧键：

    `App.Paths`、`App.OutputContent.ConfigOnly`（及其 `.Desc`）、`Common.CollapseAll`、`Common.CopyPath`、`Common.ExpandAll`、`Common.NotSelectedYet`（已被 `Common.NotSelected` 取代）、`Common.OverallProgress`、`Common.Skipped`、`Common.Version`、`Log.BlankSerialApplied`、`Log.CleaningOutput`、`Log.ComponentsDefaultSelected`、`Log.NoComponentSelected`、`Log.Preview`、`Log.Ram8GbApplied`。

    当时判断「删键要重新发布 `dist/`，收益不值成本」，所以留着没动。**2026-09-17 你要求清掉，已清**：三份语言包各删 16 行（`364 → 348` 个键），`dist/lang/` 同步。删键用的是**逐行删除**（不是「解析 → 删键 → 重新序列化」，后者会把整份文件过一遍 JSON 写出器、把真改动淹没在几千行假 diff 里），并处理了「删掉的正好是最后一条 → 前一行留下多余逗号」这个边界。

    **同时补上了「死键护栏」**（`CheckNoDeadLanguageKeys()`，3 项）—— 之前不敢加是因为它需要一份 16 条的白名单，而手写清单自己会腐烂（见第八节第 41 条）。现在换个做法：白名单**不手抄**，唯一放行的「拼出来的键族」是 `Component.<Kind>.Name/.Desc`，成员**从枚举本身生成**，新增第 5 个组件时自动跟着放行。清完之后这个护栏是**零容忍**的：以后再加没人引用的键会立刻报红。

13. **【已按方案 A 落地】8G 运存拿不到 8G payload 时告警（不再静默）** — 原问题：`ComponentCatalog` 里 `var primary = options.Ram8Gb ? ram8GbBin ?? standardBin : standardBin;` 这条 `??` 回退是**静默**的，而两个变体的落点**完全相同**（`payload.bin` + `bootloader/update.bin`），产物看着完全合法 —— 与 `exosphere.ini` 里已经写下的 `enable_mem_mode=1` 组合起来，就是「按 8G 配置、跑 4G payload」。

    **你选了方案 A（告警，不报错），已实现**：

    - **判定抽成纯函数** `ComponentCatalog.IsRam8GbPayloadMissing(release, options)`：勾了 8G、且这个 Release **确实带 `.bin`**、但没有任何一个 `.bin` 是 8G 版时返回 `true`。第三个条件不能少 —— 一个 `.bin` 都没有属于另一种情况（「连 payload 都没有」另有告警），两条告警同时打只会让用户不知道该看哪条。
    - **告警加在下载循环里**（`MainViewModel` 的 picker 调用点）。为什么不在 picker 里就地打：picker 的委托签名 `Func<ReleaseInfo, WizardOptions, IReadOnlyList<AssetPick>>` **没有 logger**，想告警也没有出口 —— 而调用点同时有 `ReleaseInfo`、选项和日志出口。所以「判定」与「告警」**必须分在两处**，靠共用同一个谓词保持一致。
    - **顺带修掉了一个真问题**：那条命名规则此前散在**三个地方**（picker 挑 payload、`ConfigGenerator` 挑根目录 `payload.bin` 的来源、8G 模式下删掉标准版），三处各写一份带引号的 `"ram8GB"` 字面量。上游改命名就会一处认、一处不认 —— 而两边都不报错：8G 用户会拿到 4G 的 `payload.bin`，或者 8G 版 payload 被当成标准版**删掉**。现在三处都走 `IsRam8GbPayloadName`，并有护栏钉住「这个字面量在 `src/` 里只能出现一次」。
    - **注入验证**：在 `ConfigGenerator` 里再写一份字面量 → 只有「命名判据只应有一份」变红（这条护栏在真实施工中就抓到过一次漏改：`ConfigGenerator` 的第一处替换被并行编辑覆盖，护栏立刻报「实际 2 处」）。把 `LogLevel.Warning` 降成 `Info` → 只有「必须以 Warning 级别打日志」变红。

    > 为什么「降成 `Info`」也值得单独钉一条：告警是否**看得见**完全取决于级别，而级别改动不会让任何行为测试变红 —— 只钉「有调用」是不够的。


14. **【已按方案 B 落地】`enable_dns_mitm` 的默认值改成关，起点与联动规则一致** — 原矛盾：「90DNS ↔ `enable_dns_mitm` 等价」与「默认值 = 你那 8 份 ini」在起点上对不上 —— 你的 `system_settings.ini` 里是 `enable_dns_mitm=u8!0x1`，所以它被做成了默认值；而两个 90DNS 默认都**不勾**。按等价关系，都不勾时 `enable_dns_mitm` 应当是 `0`；按默认值约定它应当是 `1`。**两者不能同时成立。**

    **你选了方案 B（自洽），已实现**：

    - `ComponentCatalog` 里 `enable_dns_mitm` 的 `DefaultValue` 由 `"1"` 改成 `"0"` —— 这是「默认值 = 用户 ini」既有约定里**唯一一项被明确推翻**的，`CheckUserConfigBaseline()` 里那条断言旁边写清了「别按本节口径把它改回 `u8!0x1`」。
    - **起点一致性单独一条护栏**：删掉 `settings.json` 后构造一次 `MainViewModel`，断言两个 90DNS 都不勾**且** `enable_dns_mitm` 为关。它钉的是**默认值 ⇔ 联动规则**这条自洽性，而不是某个字面量。注意 `CheckUserConfigBaseline` 里那条只保证「生成器照默认值写」，**不保证「界面打开时看到的就是关」** —— 三条链路（对象默认值 / 生成的 ini / 界面初始勾选）必须各有一条。
    - **界面文案同步**：三份语言包里的说明改成「默认关闭；勾选任一项「90DNS」会自动打开它」—— 原来的「90DNS 依赖此功能」在「默认关」的语境下会让人以为它是开着的。
    - **代价**：默认生成结果与你现在的 `system_settings.ini` 差这一个值（`1` → `0`），`samples/` 已随之重生成。功能不受影响 —— 两个 90DNS 一旦勾上，`enable_dns_mitm` 会自动变回 `1`。
    - **注入验证**：把默认值改回 `"1"` → 只有 `CheckUserConfigBaseline` 的 `u8!0x0` 与 `Check90DnsSwitches` 的起点一致性两条变红，其余 754 条全绿。

86. **暗夜模式：先量清「资源能不能改」，再决定接在哪一层**（2026-09-20 第十九轮）— 用户要求「新增暗夜模式主题，放在语言选项旁边」，随后明确「默认主题跟随系统」；暗夜底色用户选了**近纯黑**。

    > ⚠️ **这一轮的核心是一个被实测推翻的假设，值得单独记**：看代码时我认定「颜色都集中在 `Themes/Styles.xaml` 的那批 Brush 键里，所以**原地改这些共享实例的 `Color`** 就够了 —— 界面里 160 多处 `{StaticResource}` 一个都不用动」。写完一跑 `--selftest`，体检当场报出 **22 个 Brush 全部 `IsFrozen=true`**：Application 级资源字典里的 Freezable 会被 WPF **自动冻结**，改它的 `Color` 直接抛 `InvalidOperationException`。而 `StaticResource` 是**解析时拷贝引用**的 —— 就算把字典里的对象换掉，已经建好的界面也还指着旧的那个，症状是「切了主题纹丝不动」，而且**什么都不报**。
    > ⇒ 改走 WPF 的正路（**两条腿缺一不可**）：资源侧用 `host[key] = new SolidColorBrush(color)` 换**未冻结的新实例**；界面侧 63 处颜色引用从 `StaticResource` 改成 `DynamicResource`（只有它会订阅资源变更）。
    > ⚠️ **如果没加那条体检，我会交出一个「切主题毫无反应、也不报错」的功能。** 教训：**「改共享对象」这类省事方案，必须先量「这个对象到底可不可变」** —— 而量它的成本只是一次自检。

    ★ **端到端判据（而不是「理论上应该」）**：`--selftest` 除了逐键核对「资源字典里换对了吗」，还**读真实控件的颜色** —— 实测窗口 `#FFF4F6F9 → #FF0B0B0D`、文本 `#FF1D2129 → #FFEDEEF1`。资源换对了但引用方式写错时，前者全绿、后者立刻红；这一条是区分「字典对了」与「界面真的动了」的唯一直接判据。
    ★ **系统原生控件的坑**：ComboBox / CheckBox / Expander 的默认模板用系统画刷，**不跟着资源键走** —— 不重画的话它们在暗夜下保持浅色。这三个用最小模板覆盖（只替换视觉；`IsDropDownOpen` / `IsChecked` / `IsExpanded` 仍是标准双向绑定，不碰交互逻辑）。**CheckBox 的模板根刻意用 `Border` 而不是 `StackPanel`**：让整行都可点（收窄成「只有方框和文字能点」是一种不易察觉的退化）。
    ★ **默认「跟随系统」**（用户明确要求）：读注册表 `AppsUseLightTheme`，并订阅 `SystemEvents.UserPreferenceChanged` 实时跟随 —— ⚠️ 该事件在**非 UI 线程**触发，必须 marshal 回 UI 线程才能改资源；无人值守两条路径（`--selftest` / `--run`）**刻意不订阅**，`App.OnExit` 摘掉订阅。
    ★ **7 条新护栏**：`CheckThemePaletteIsComplete()`（两套表**键集必须完全一致** + `Keys` 要列全（它就是 `Apply` 遍历的那份清单）+ 色值格式 + `For(System)` 必须抛）、`CheckThemePaletteMatchesStyles()`（`Styles.xaml` 的初值必须与浅色表**逐字一致** —— 两份数据必然并存，只能靠这条钉在一起）、**`CheckThemeColorsAreDynamic()`**（颜色键不许用 `StaticResource`、XAML 里不许有内联颜色 —— 这是整个暗夜模式唯一的「静默失效」入口）、`CheckThemeModeCodec()`（坏值只回落不抛 + 把**「默认跟随系统」钉死**）、`CheckThemeNamesAreLocalized()`、`CheckThemeApply()`（注入假 `ResourceDictionary` 逐键断言 + 幂等 + `System` 由探针决定 + 缺键要**被记下来**）、`CheckThemeSettingPersistence()`（含手改坏值回落 + 「下拉框不许是空的」）。
    ★ **顺带清掉两处「必然漏一块」的地方**：删掉一个从没被引用的死资源 `AppBackgroundColor`（`<Color>` 类型，与 Brush 那一套并存的遗留物）；把 1 处 XAML 内联色与 8 处 `Styles.xaml` 内联色全部提成资源键。
    ★ 回归 1846 → **1886**（+40）；`SMOKE: ALL OK`、0 失败；`--selftest` 0 绑定错误、主题体检 0 问题。

87. **「取消内置」不是删一行 `EmbeddedResource`，是三条链路一起改**（2026-09-21 第二十轮）— 用户要求 boot.dat 改为从仓库下载、开关从「输出内容」移到「框架」、地址可改、要有日志与落盘、未填写时走内置默认地址。
    数一下真正被牵动的东西：`csproj` 去掉内嵌条目、`EmbeddedAssets`（只为读它而生的那个类）删掉、`ConfigGenerator` 从「读嵌入资源」改成「复制下载好的文件」、`WizardOptions` 加一个**运行期**字段 `BootDatPath` 把「下载」与「生成」连起来、`MainViewModel` 在下载阶段新增一步（走 `DownloadService` ⇒ 自动吃镜像与备用通道）、界面把勾选框搬进分组模板、三份语言包改键名。
    ★ 那一份**被删掉的** `Resources/boot.dat` 是安全的：先量过仓库里那份 = **11520 字节、sha256 `df8003f0…`**，与内嵌的那份**逐字节相同** ⇒ 取消内置不丢任何东西。
    ★ 顺带发现 `WizardOptions` 上多一个标量属性会**被既有护栏当场抓住**（「每个标量属性都应有界面设置接上去」）—— 它抓得对，那是给界面设置用的护栏；`BootDatPath` 是运行期管道值，进例外清单并写明理由，而不是把护栏放松。

88. **用户给的地址是「给人看的地址」，程序得自己翻译成「能取到字节的地址」**（2026-09-21 第二十轮）— 用户给的两条默认地址（`…/blob/main/boot.dat` 与 `…/releases`）都是网页：前者下下来是**一整页 HTML**（HTTP 200、有字节、也写出了文件 —— 完全静默地产出一个 11 KB 的假 boot.dat），后者里根本没有版本 JSON（真正能查的是 `api.github.com/repos/…/releases`）。
    ⇒ 各加一层**纯函数**解析（`BootDatSource` / `UpdateSource`），把「用户说的地址」翻成「真正能取到东西的地址」，翻不出来就**明确拒绝**（界面红字 + 日志），绝不猜。
    ★ 两条地址的坑不同：boot.dat 那条是「形态错」（`blob` → `raw`，另加 `api.github.com` 的 contents 端点兜底 —— 本沙箱里 raw 域名 **DNS 都解析不出来**，兜底是唯一能取回字节的路）；检查更新那条是「根本没有数据」（releases 页 → `api.github.com/repos/…/releases`）。
    ★ ⚠️ 造坏输入时踩过一次：`"这不是一个 owner/name"` 带一个斜杠，恰好满足「恰好两段」，**它其实合法** —— 断言红在数据上，不是红在代码上。造不出预期的坏输入时，先怀疑输入。

---

## 十、下一步计划（等你确认后继续）

- ✅ 已完成：`boot.dat`（SX GEAR 引导文件）+ 界面开关（默认勾选），点「开始下载并生成配置」后写到 `out/` 根目录（**2026-09-21 起取消内置、改为下载**，开关移进「框架」分组，见第八节第 87 条）
- ✅ 已完成：8G 运存模式下清掉 out 根目录的标准版 payload（标准版保留在 `bootloader/payloads/`）
- ✅ 已完成：把 8 份实际配置文件里的取值做成界面默认值（含新增 `config/ovl-sysmodules/config.ini`、补齐 `override_config.ini` 的 `[hbl_config]` 段、引导项改 `pkg3` 并写入你的 `id`、组合键改 `L+DDOWN`）
- ✅ 已完成：下载直链连续失败后**自动改走 GitHub API asset 端点**（见第九节第 11 条）
- ✅ 已完成（2026-09-16 七项）：引导项显示名可改、`logopath` 启动图、中文界面走 `easyworld/hekate` 汉化包（当时写成「8G 除外」——**2026-09-18 第十五轮已改**：8G 也走汉化包，运存只影响 payload 来源，见第八节第 76 条）、各插件下载源可改、GitHub Token 一键打开生成页 + 一键验证、打包开关与屏蔽序列号默认不勾（见第八节第 35、36 条与第二节）
- ✅ 已完成（2026-09-16 后续）：**首次运行组件默认一个都不勾**，各组件内部的默认配置选项原样保留；`--run` 命令行路径仍自动全选，未勾任何组件时界面直接给出提示（见第八节第 37 条）
- ✅ 已完成（2026-09-16 收尾）：**补上「界面设置 → `WizardOptions`」的接线穷举护栏** —— 之前整行漏掉某个设置没有任何用例会红（已用注入 bug 实测确认），现在由反射遍历自动覆盖，将来新增设置项无需回来补测试（见第八节第 38 条）
- ✅ 已完成（2026-09-16 收尾之二）：**补上「配置项落点」的完整性护栏** —— 之前「声明了新落点却没写出去」同样无人发现（计数断言挡不住，已用注入 bug 实测确认），现在声明的落点集合与产出的配置文件集合双向穷举比对（见第八节第 39 条）
- ✅ 已完成（2026-09-16 收尾之三）：**补上「语言包键集完整性」护栏** —— 之前某一份语言包单独缺键会被索引器的回退掩盖成「有文案」（旧护栏那条断言的名字就写着「三份都要有文案」，实测却全绿），现在直接比三份 JSON 的原始键集（见第八节第 40 条）
- ✅ 已完成（2026-09-16 收尾之四）：**补上「组件枚举 ↔ 目录定义」一一对应护栏，并把动态拼键检查改成枚举驱动** —— 之前往 `ComponentKind` 加第 5 个组件会静默漏过动态键检查（它写死了 4 个组件，而那是唯一覆盖 `Component.<Kind>.Name` 这类拼出来的键的地方），已用对照实验实测确认（见第八节第 41 条）
- ✅ 已完成（2026-09-16 收尾之五）：**删掉一条恒真的假断言，并补上它本该守的源码契约** —— `CheckFreshUiState` 里「界面路径不该出现『已默认勾选全部组件』日志」那条断言永远不可能失败（那条日志的代码路径根本不存在），真正管用的是状态断言 + 新增的「`EnsureComponentsSelected()` 调用点只允许落在 `RunHeadless`」源码契约（见第八节第 42 条与第九节第 12 条）
- ✅ 已完成（2026-09-16 收尾之六）：**补上「配置项是否真被消费」护栏** —— `TargetFile` 为 null 的选项此前完全不受检查（`null` 既是「本就不写进配置文件」也是「忘了填」，类型上无法区分），新增选项时漏写 `TargetFile` 会静默通过；同时把 `WizardOptions` 的 24 个可写属性与 9 个「不写文件」的选项全部反查一遍，确认其余无缺口（已用注入实测确认只有新护栏报错，见第八节第 43 条）
- ✅ 已完成（2026-09-16 收尾之七）：**补上「`Option()` 的 Key 拼错」护栏，并把 `nogc` 从「不可观察」变成「可观察」** —— `Option()` 在 Key 找不到时会静默回退成空串，而 `nogc` 的写入器正好把空串和 `auto` 归进同一个「只写注释」分支，产物逐字节相同、任何断言都看不出差别（它的写入分支此前从未被走到过）。现在由「Key 必须声明在所属组件的目录里」的静态契约 + `nogc` 三取值分支的行为用例共同守住；对照实验（同一注入分别落在 `nogc` 与 `override_key` 上）证明新护栏不是冗余（见第八节第 44 条）
- ✅ 已完成（2026-09-16 收尾之八）：**补上「ini 段」护栏 —— 键必须落在它声明的段里** —— `system_settings.ini` 是按模块分段的（`[eupld]`/`[usb]`/`[ro]`/`[atmosphere]`/`[hbloader]`/`[lm]`），写入器的兜底 `option.Section ?? "atmosphere"` 对 22 个选项里的 15 个恰好正确，而现有断言全是整文件 `Contains`、**段从不参与断言**：把键挪到别的段，固件按段分发时读不到（等于没配），所有断言照样全绿。现在按「键 → 所在段」逐项比对，覆盖 28 个选项；目标文件从源码里发现而非手抄（见第八节第 45 条）
- ✅ 已完成（2026-09-16 收尾之九）：**把「回退掩盖缺口」做成系统扫描，并把 `TargetFile`/`Section`/`IniValuePrefix` 这一族的最后一块补上** —— 147 处候选按「能不能静默写出错产物」筛到配置产出路径，命中 `IniValuePrefix`（漏写 → 裸值；拼错 → 落进 `FormatTypedValue` 的 `_ => value`，产物与「本来就不需要前缀」逐字节相同）。新增 `CheckIniValuePrefixesAreRecognized()`：认得的前缀集合**从源码 switch 分支扫出来**、同一文件内前缀声明必须**统一**（实测 9 个文件全部满足「全带或全无」）。对照实验证明**基线对「新增第 23 个选项漏写前缀」完全看不见**（那条注入下只有新护栏报错，回归 658 通过 / 1 失败）（见第八节第 46 条）
- ✅ 已完成（2026-09-16 收尾之十）：**把「计数断言不是完整性断言」从测试搬进运行期校验器 —— 逐组件对账** —— `OutputValidator` 的第 4 条检查只看合并**总数**（`MergedFileCount > 0`），而它旁边那条「只针对 Atmosphere 的 `package3` 特例」正是聚合不够用的自白书：勾了四个组件、其中一个一个文件都没进来（解压中途失败 / 目录被清过），另外三个合进来几十个文件，总数照样 > 0，报告写着「已合并 47 个组件文件」。现在 `MergeComponentFiles` 按组件记数（**含 0**），校验器按 `ComponentCatalog.All` 逐个对账、缺哪个点名哪个。顺带补上覆盖缺口：此前唯一的合并用例**只造了 Atmosphere / Hekate 的解压目录**，`Ultrahand` / `Sys-patch` 的合并路径从未被走到过；新用例按目录循环覆盖四个组件，并把对照实验写进用例（旧检查仍通过 + 新检查点名 + 未缺者不被点名）。外部注入实测：**669 通过 / 1 失败，只有新断言报错**（见第八节第 47 条）
- ⏳ 待确认（2026-09-16 收尾之九发现）：**8G 拿不到 8G payload 时会静默退回标准版** —— `ram8GbBin ?? standardBin`，两个变体落点相同、产物看着合法，而 8G 用例**总是**提供 `__ram8GB.bin`，该回退分支从未被走到。属行为变更（可能需要「报错而非静默降级」），未改动
- ✅ 已完成（2026-09-17 收尾之十一）：**90DNS 拆成「真实破解系统」与「虚拟破解系统」两个独立开关** —— 之前只有一个 `Use90Dns`，hosts 文件跟着引导模式走，「只想给虚拟系统挡遥测」做不到。现在两个开关各自跟着对应引导模式显示、各自决定写哪一份（真实 → `default.txt` + `sysmmc.txt`，虚拟 → `default.txt` + `emummc.txt`，都不勾 → 一份都不写）。`OutputValidator` 的 hosts 校验同步拆分（与生成端**同一套判据**，否则会去要一份生成端根本不会写的文件）；`AppSettings` 加了旧单开关 `Use90Dns` 的迁移（与 `BlankSerial` 同款，迁完置回 `null` 防二次迁移）（见第八节第 48 条）
- ✅ 已完成（2026-09-17 收尾之十二）：**`override_config.ini` 的 `[hbl_config]` 五个键从硬编码改成可配置项** —— `program_id`、`override_any_app`、`path`、`override_key`、`override_any_app_key` 现在都是界面选项（存储键加 `hbl_` 前缀以避开 `[default_config]` 的同名键，写入时换回真名）。`program_id` 与 `path` 加了取值校验（16 位十六进制 / SD 卡内相对路径），格式不对时回落到官方默认值并告警 —— 写错不会报错，只会让 Homebrew Menu 在真机上永远起不来。`override_any_app` 刻意用下拉（`true`/`false`）而不是勾选框：勾选框统一产出 `1`/`0`，而官方模板的取值语法是字面量 `true`（见第八节第 49 条）
- ✅ 已完成（2026-09-17 收尾之十三）：**下载源的 Hekate 拆成两行（官方 + `easyworld/hekate`）** —— 原先「用官方还是汉化包」完全跟着界面语言自动切、界面上看不见，英文界面想要汉化 Nyx 根本做不到。现在两行都能填：只填一行 → 那行生效（含「英文界面填汉化行」）；两行都填 → 官方优先（顺序固定）；都不填 → 回落到语言默认；8G → 当时定的规则是「永远官方」，**2026-09-18 第十五轮已改**（8G 也走汉化包，运存只影响 payload 来源，见第八节第 76 条）。⚠️ 这不是「往清单里加一项」那么简单：`RepoOverrides` 按默认地址做键，而 `ResolveRepo` 只读官方那一行，直接加会做出一个**能填、能存、下载时纹丝不动**的死控件（见第八节第 50 条）
- ✅ 已完成（2026-09-17 收尾之十四）：**90DNS ↔ `enable_dns_mitm` 改成完全双向绑定** —— 勾任一 90DNS → 自动打开 `enable_dns_mitm`；两个都取消 → 自动关掉；取消 `enable_dns_mitm` → 两个 90DNS 同时取消；**勾上 `enable_dns_mitm` → 自动补勾当前可见的 90DNS**。第 4 条是这轮补的（你选了「勾 mitm 时自动补勾 90DNS」），补上它之后 `enable_dns_mitm` ⇔ 至少一个 90DNS 勾着，前三条留下的那个「手动可达」的口子才真正堵死（见第八节第 57 条）。载入 `settings.json` 时也会归一化（见第八节第 58 条）
- ✅ 已完成（2026-09-17）：**`enable_dns_mitm` 的默认值改成 `0`**（方案 B）—— 起点与「两个 90DNS 都不勾 ⇒ mitm 关」自洽，见第九节第 14 条
- ✅ 已完成（2026-09-17）：**语言包里 16 个历史遗留键已清理**，并补上**零容忍的死键护栏**（`CheckNoDeadLanguageKeys()`），见第九节第 12 条
- ✅ 已完成（2026-09-17 收尾之十五）：**Ultrahand 界面语言 `default_lang` 跟随本向导的界面语言，并允许手动覆盖** —— 界面新增一项下拉（默认「跟随界面语言」）。默认值存哨兵 `"auto"`、**生成期才求值**（存值那一步拿不到界面语言），且哨兵绝不能落进 ini。中文**按书写系统分**（`zh-Hans → zh-cn`、`zh-Hant → zh-tw`），语言名用 endonym。配合**软联动**：解析结果不是 `en` 且 Ultrahand 已勾选时自动补勾「安装语言包」，**不锁定、可取消**（门控在 `IsSelected` —— 用户还没决定装不装 Ultrahand 时不替他勾）。`OutputValidator` 与生成端**判据同源**，四个方向的告警都验过（见第八节第 59 条、第二节「Ultrahand 界面语言」）
- ✅ 已完成（2026-09-17 收尾之十六）：**三个引导项各加一个图标 `icon`（Nyx 菜单里的小图标）** —— 用户选本地图片 → 复制进 `bootloader/res/` → 在 `bootloader/hekate_ipl.ini` 的对应引导项里写 `icon=bootloader/res/<文件名>`。实现上**没有复制一份 `AppendBootLogo`**，而是泛化成 `AppendBootImage(sourcePath, BootImageKind kind, …)`，`logopath` 与 `icon` **共用同一张撞名去重表** —— 两者落在同一个 `bootloader/res/` 目录，规则不一致会让「A 项的启动图」被「B 项的图标」静默盖掉。顺带补上两个护栏：`CheckXamlBindingsResolve()`（XAML 绑定名写错不编译报错、运行期静默绑空值）与 `CheckEverySettingIsPersisted()`（`SaveSettings()` 与构造函数两处手写块漏一处，只在下次启动才现形）。同时修掉撞名用例里两处裸 `File.ReadAllText` 无守卫、断言失败会把整套测试打成未捕获异常的缺陷（见第八节「2026-09-17 第五版」与第二节「图标」）
- ✅ 已完成（2026-09-18 收尾之十七）：**引入「文件槽」（`AssetSlot`）机制，「组件」类 19 个插件全部接入** —— 用户要求「每个插件的**下载地址**与**下载文件名**都要能在界面上改，有多个文件就出多个输入框，没改就用我给的默认值，放高级设置里」。做法不是在界面上摆 40 多个手写框，而是给每个可下载文件声明一个**槽**（`Key` + 默认 `Address` + 默认 `FileName`），界面、存档、下载三边都从这份声明生成 —— 加一个插件只要加一条声明，输入框、设置键、落点提示自动出现。默认文件名写成 `Func<string?, string>`（参数是界面语言代码）而不是常量，因为**有语言相关的槽**：DBI 的翻译文件要按界面语言取 `translation_zhcn.bin` / `translation_zhtw.bin` / `translation_en.bin`，且必须**重命名**为 `translation.bin` 才被 DBI 认（`SaveAs`）。名字对不上时不硬失败，走**四级容错瀑布**（逐字 → 通配 → 去版本号前缀 + 同扩展名 → 同扩展名唯一候选），但**命中方式随结果写进日志** —— 「声明叫 `sys-botbasexx.zip`、实际下到 `sys-botbase25.zip`」这件事必须留痕，否则用户永远不知道版本号从哪来、也无从判断猜得对不对。同时把「从仓库**文件树**取」（`SlotSource.RepoFile`）做进同一条链路：Luna 的 `enctemplate.zip` 躺在仓库根目录、从来没发布过，raw 直链不通时自动改走 contents 端点（⚠️ 必须配 `Accept: application/vnd.github.raw`，否则拿回来的是**这段文件的 JSON 元数据**、HTTP 200、会被当成组件包写进 SD 卡）。回归 856 → 1067 → 1174
- ✅ 已完成（2026-09-18 收尾之十八）：**「后台」类 5 个插件接入，并顺手补上两条通用机制** —— ① **解压后筛一遍**（`ExtractPlan`）：`sys-ftpd` 的 `release.zip` 包内比落点多一层 `out/`（实测 `out/atmosphere/…` + `out/config/…`），所以给槽加了 `Extract` 声明（`StripPrefix` + 顶层目录白名单 + 文件名白名单），而不是在解压器里写一句「如果叫 sys-ftpd 就……」；⚠️ 声明了筛选却**一个文件都没留下**时必须**抛错**，不能留下一个空文件夹 —— 那在日志里看不见，是本项目最忌讳的失败形态。② **sysmodule title 冲突规则**：用户清单里写「`sys-botbase` 提示与 `usb-botbase` 冲突，只能二选一」，实测确认这是**真实的技术冲突**（两者装的是同一个系统模块 `atmosphere/contents/430000000000000B/`）。做成规则而不是写死一对名字：每个槽声明自己的 `SysmoduleTitleId`，判据是「任意两个被勾选的槽声明了同一个 title 即冲突」—— 界面卡片上的提示与生成后的完整性校验**调同一个方法**（`ComponentCatalog.FindTitleConflicts`），将来再加一个撞车的 sysmodule 不改代码就会自动被抓出来。回归 1174 → 1280（见第八节第 62、63 条）
- ✅ 已完成（2026-09-18 收尾之十九）：**「主题」类 3 个插件接入，并新增「仓库目录槽」（`SlotSource.RepoDirectory`）** —— NXThemes Installer 除了本体 `NXThemesInstaller.nro`，还要取 `exelix11/theme-patches` 的 `systemPatches/` 目录下**所有文件（不要文件夹）**放到 `themes/systemPatches/`，与本体**同框绑定**（同一个组件、同一个勾选框）。这不是「再加一个下载项」那么简单：目录槽是第三种来源语义 —— 既不是「下一个压缩包再解压筛」（那个仓库根本没有 Release），也不是「下一个文件」；它是**一个槽 → 一批下载物**。做法是先用 contents 端点列目录（只留 `type == "file"`，子目录与子模块丢掉，结果按文件名排序），再把每个文件变成一个下载物；每个下载物都带 `RepoFileRef`，于是**白捡了仓库文件树那套备用通道**（raw 直连不通时自动改走 contents 端点）。⚠️ 列目录的地址**不能带 `?ref=HEAD`** —— contents 端点的 `ref` 只认分支名/标签/SHA，带 `HEAD` 会 404；不带则走默认分支（实测该仓库默认分支正是 `master`，与用户给的 `tree/master/...` 一致），顺带免疫「上游哪天把默认分支改名成 main」。界面上那一列的第二个框在目录槽里代表**仓库内的目录名**（列头因此改成「文件名 / 目录」，行内另有一句说明），否则用户会以为要去下某个叫 `systemPatches` 的文件。回归 1280 → **1353**（见第八节第 64 条）
- ✅ 已完成（2026-09-18 收尾之二十）：**「底层」类 2 个接入（Lockpick_RCM / TegraExplorer），并补一条「payload 归属」护栏** —— 两个散装 `.bin` payload 都落 `out/bootloader/payloads/`，与 hekate 自带的 `payloads/` 同目录，装进去由 hekate 的「Payloads」菜单直接启动。★ 关键决策是**刻意不标 `IsPayload`**：`IsPayload` 的语义是「散装下载的**引导** payload（`fusee.bin` / `hekate_ctcaer_*.bin`）」，带它的文件会被「把 payload 放到对应目录」开关**整个跳过**（`OutputStager` 与 `MainViewModel` 两处都有 `if (IsPayload && !IncludePayloads) continue`）。这两个是**普通组件文件**，标上它只有一个后果 —— 用户勾了「Lockpick_RCM」却没勾那个开关时**静默什么都不产出**。所以新增的护栏 **⑤b** 不是只钉这两个，而是**穷举所有文件槽**断言 `IsPayload == false`：恒真与否由「有没有人标」决定，不是靠谁记得住（见第八节第 65 条）。回归 1353 → **1384**。同批次还完成了第九轮的**真实联网 e2e**：`NXThemesInstaller.nro`（10394050 字节）与 16/20 个 `.ips` 实际落盘，每个文件都验证了「直链 502 → 自动改走 API 端点」的降级链；第 17 个起撞上未登录的 API 限流（403），程序**明确报错并拒绝生成配置**，`out/` 里 0 个文件 —— 宁可不产出，也不给半套 SD 卡。这也暴露了一个真实约束：**目录槽的文件数 = API 调用数**（在 `raw.githubusercontent.com` 不可达的网络里），20 个补丁就是 20 次调用，而未登录配额只有 60 次/小时（见第二节「主题」组的配额提示）
- ✅ 已完成（2026-09-18 收尾之二十一）：**目录槽改走「整仓打包一次取回」快路径，把 20 次 API 调用压成 1 次** —— 上一轮的 e2e 暴露了一个真实约束：目录槽的**文件数 = API 调用数**（`raw.githubusercontent.com` 不可达时每个文件都要改走 API），20 个补丁就是 20 次调用，未登录配额 60 次/小时，实测第 17 个就被 403 拦下。改法不是「多给几个 Token」，而是**换传输层**：先试 `codeload.github.com/<owner>/<repo>/tar.gz/HEAD` —— 实测 HTTP 200、gzip、**一次请求拿回整棵树、且不消耗 API 配额**（codeload 不是 API 端点）。取回失败则**静默回落**到原来的「列目录 + 逐个文件」，老路逐字节未动（按用户要求：打包失败时就是每个文件一次请求，日志里明说这个后果）。三个设计点：① 用符号引用 `HEAD` 而不是分支名，**不必先查默认分支**、也免疫上游改名 `master → main`；② tarball 根目录名是**动态的**（实测 `theme-patches-HEAD/` 与 `exelix11-theme-patches-e9ba165/` 两种都有），所以**不能**复用只吃常量前缀的 `ExtractPlan.StripPrefix`，新写了一个「剥掉第一层、不管它叫什么」的纯函数 `SelectDirectoryEntries`；③ 解出来的东西**包成与老路同构的 `RepoDirectoryFile`**，两条通道都走同一个 `PickRepoDirectorySlot` 入口 —— 落点、备用通道、逐槽对账全部自动一致，**下游一行都不用改**。⚠️ 用「已预下载」标记而不是事后 `File.Exists` 判断：后者会把**上一次运行残留的旧文件**也当成已下载，导致「换个版本重跑」静默沿用旧内容。解包用 .NET 8 内置的 `System.Formats.Tar`（不引新依赖，且测试能用同一个库造 fixture）。回归 1384 → **1409**（见第八节第 66 条）
- ✅ 已完成（2026-09-18 收尾之二十二）：**「Ultrahand插件」类 6 个接入（emuiibo / Zing / ReverseNX-RT / Status-Monitor-Overlay / QuickNTP / KeyX）** —— 六个 Tesla 覆盖层（`.ovl`），整包铺 `out/` 根。这一批的价值主要在**实测**：把七个包（KeyX 两个）全拉下来数条目、看顶层目录，确认**顶层全是 SD 卡根目录名**（`atmosphere`/`switch`/`config`/`SaltySD`）、**没有一层多余的包装目录** —— 于是「不声明 `Extract`、不声明落点」是**实测结论**而不是默认值的巧合，回归里有一条断言把这个结论钉住（上游哪天改了打包方式，落点会全错但不会有任何报错）。两个容易踩的点：① ⚠️ **仓库名 ≠ 文件名**：`Status-Monitor-Overlay` 的包叫 `StatusMonitor.zip`（少了连字符与 `Overlay`），这是上游命名，**不要「顺手对齐」**（对齐后逐字命中失效、静默落进容错瀑布）；② **KeyX 按界面语言取包**（中文 `KeyX-CN.zip` / 其余 `KeyX-EN.zip`），它与 DBI 的翻译文件**不是同一类问题** —— 那两个包在 Release 里**都真实存在、都可用**（实测只差 7 字节），所以「取哪个」是**用户意图**而不是「名字对不上要容错」，判据只能是界面语言（见第八节第 67 条）。这一组里有**四个**包内含 sysmodule（emuiibo 的 `0100000000000352`、KeyX 的 `0100000000251020` 与 `4100000002025924`、ReverseNX-RT 的 `0000000000534C56` 即 SaltySD），**都不声明 `SysmoduleTitleId`**：KeyX 装**两个** title 而那个字段是单值的，写一个就是半个真话；且按现有约定只有「冲突可预期」的才声明。回归 1409 → **1524**（+115，逐项对账见下）
- ✅ 已完成（2026-09-18 收尾之二十三）：**逐字复核「写进注释的实测」，并补上「上游事实」的核对工具** —— 这一轮做的是**上一轮的账**：收尾之二十二里写下了 7 条「实测」，但我只在其中一部分真的量过。逐条目复核（两包各 9 个条目、逐字节比）后查出**一条编造的实测**：「CN 包里有四个语言 json、EN 包只有英文那一份」—— 真相是两包 8 个文件逐字节相同（**含全部四个语言 json**），唯一差异在 `ovl-KeyX.ovl` 里的 **144 个字节**（内嵌显示名：CN 中文「按键助手」/ EN `KeyX`）；结论没变，所以**没有任何断言会红**（见第八节第 69 条）。同一轮还查出两处**枚举不全**（「三个包内含 sysmodule」实为四个 —— `ReverseNX-RT.zip` 里还有 `0000000000534C56`，ASCII 拼出来就是 SaltySD）与一处**护栏自称覆盖范围过大**（「上游改了打包方式它会红」是假的 —— 它只读 C# 声明，见第八节第 70 条）。
    ★ 为了让「凡写实测必有可复核依据」这条规矩**可执行**，新增 `tools/check-upstream-facts.py`：它**从 `ComponentCatalog.cs` 的注释里现取**那份「上游实测」清单（**不手抄第二份** —— 手抄的清单一定会腐烂，改了一处忘了另一处且不会有任何断言变红），联网逐个核对 ①资源名是否**逐字**存在 ②字节数是否一致 ③包内第一层是不是 SD 卡根名（**多一层包装目录 = 落点全错的静默失败**）④KeyX 两包逐条目差异 ⑤包内 sysmodule title 与已声明的有没有交集。解析不到任何条目就**报错退出**，不许静默「全部通过」。它是**手工联网工具，不在离线回归里** —— 因为离线回归只能钉声明，上游那半边只有联网才够得着。
    ★ 同批完成**真实联网 e2e**（只勾这 6 个组件）：退出码 **0**、`run.log` **0 个 Error**（6 个 Warning 全是那条已知的「直链 502 → 改用 API asset 端点」降级链，属环境噪声），`out/` **1327 个文件**，顶层正是 `SaltySD` / `atmosphere` / `config` / `switch` 四个 SD 卡根名 —— **正面印证了「包内已是 SD 根、没有包装目录」**；`out/atmosphere/contents/` 下四个 title 与扫描结果逐一对应。最有力的一条是 **`out/switch/.overlays/ovl-KeyX.ovl` 的 sha256 与 `KeyX-CN.zip` 里的同名条目逐字节相同** —— 界面语言选包这件事一路正确落到了磁盘上（⚠️ 这件事**只有比字节才作数**：就算声明里的包名写错了，容错瀑布「猜中」也会让 e2e 变绿，只在日志里留一行痕）。
- ✅ 已完成（2026-09-18 收尾之二十四）：**最后 7 个「学习」类接入，并补上「按界面语言换仓库」机制** —— `AtmoXL` / `Awoo` / `linkalho` / `Switch_90DNS_tester` / `wiliwili` / `Moonlight-Switch` / `TriPlayer`，用户清单至此**全部接完**。这一批引入了本项目第一个**按界面语言换仓库**的槽（`linkalho`：中文走汉化镜像 `SwitchScriptTW/linkalho`、其余走上游 `impeeza/linkalho`），为此把 `AssetSlot.Address` 从 `RepoSpec` 改成 `Func<string?, RepoSpec>`（与 `FileName` 对称；38 处既有声明用带硬断言的脚本机械包装成 `AssetSlot.Always(...)`）。⚠️ 最要命的一点：**地址与文件名必须成对**（两边的**声明**不同：中文侧 `linkalho.zip`（与上游同名），其余侧是**模式** `linkalho-x.x.x.zip`；上游此刻实际发的是 `linkalho-v2.0.2.zip`），只切其中一个会让逐字命中失效、静默落进容错瀑布「猜」一个 —— 能跑通，但日志里会留一条本不该有的猜中记录；所以两处共用同一个判据函数（见第八节第 71 条）。
    ★ **包内结构是量出来的**：本沙箱把下载限速到 ~25 KB/s（`wiliwili` 一个包 18 MB 要十几分钟），改用 **HTTP Range 只取 ZIP 中央目录** —— 几十 KB 拿到完整条目清单。实测结论：`AtmoXL`/`Awoo`/`linkalho`/`TriPlayer` 的包内已以 SD 卡根为基准（整棵铺 `out/` 根）；**`wiliwili` 的包内多一层 `wiliwili/` 子目录**（⇒ `StripPrefix` + `KeepFileNames = ["wiliwili.nro"]`，不剥会落成 `out/switch/wiliwili/wiliwili/wiliwili.nro`）；`90DNS` 与 `Moonlight` 是**散装 `.nro`**（⇒ 直接声明落点、不声明 `Extract`）。⚠️ 顺带发现 **`TriPlayer` 的包里含一个 sysmodule**（title `4200000000000FFF`，与 sys-ftpd 的 `420000000000000E` 只差末两位、**不是同一个**）—— 按现有约定不声明，交集结论交给核对脚本第 ④ 项联网复核（见第八节第 72 条）。
    ★ `check-upstream-facts.py` 扩了三处：支持 `GITHUB_TOKEN` **环境变量**（⚠️ 只从环境变量读，绝不写进文件）、第 ② 项允许「声明了 `StripPrefix` 的包装目录」（该清单同样从源码现取）、新增第 ⑤ 项「**按语言切换的候选包，包内结构必须一致**」（落点声明对 CN/EN 是同一份，若一个多了一层目录，那种语言会静默落错而另一种完全正常）。
    ★ 回归里新增「学习」组 ③d 段（分类 / 槽数 / 通道 / 落点 / 解压声明逐项钉住，并用**实测的真实包内条目名**喂给 `ExtractPlan.Map` 验证筛选真的对）与 linkalho 的四语言方向断言；`UiLanguageCodes` 的引入让三处既有的「仓库清单」检查从「只取一个语言」升级成**穷举全部语言**（见第八节第 71 条）。回归 1524 → **1673**（+149，逐项对账见下）。
    ★ **增量对账**（按第 1 条教训，用**差集**做而不是靠 grep 计数）：把两份回归日志的断言文本归一化（数字→`N`）后取 `new - old`，得到**净增 150、净减 1** —— 净减那条正是「默认全选后应勾上全部 N 个组件」的文案变化（**39 → 46** = 4 框架 + 19 组件 + 5 后台 + 3 主题 + 2 底层 + 6 Ultrahand插件 + 7 学习），所以**净增 149**，与 1524 → 1673 完全吻合。再看净增的 150 条**全部含新组件名**（不含新组件名的净增 = **0**）⇒ **没有任何既有护栏的执行次数发生变化**。（第一次跑 1666 时是「通过 1665 / 失败 1」——失败的那条正是**覆盖性护栏自己报出的缺口**：「每个用文件槽声明的组件都要在这张落点对照表里有位置；缺的：`AtmoXL、Awoo、Linkalho、Dns90Tester、Wiliwili、Moonlight、TriPlayer`」，补表后 +7 条断言 → 1673 全通过。）
    ★ **真实联网 e2e（7/7 全跑通）**：退出码 **0**、`run.log` **0 个 Error / 7 个 Warning**（6 条是已知的「直链 502 ×2 → 改用 API asset 端点」降级链，AtmoXL 与 TriPlayer 各 3 条；第 7 条见下），耗时 25 分 44 秒（本沙箱把下载限速到 ~25 KB/s，`wiliwili` 一个包 18 MB）。`out/` 顶层只有 `atmosphere` / `switch` 两个 SD 卡根名 + 向导自己写的 `.wizard-manifest.json`，**10 个落点全部命中**：7 个 `.nro`（`AtmoXL`/`Awoo`/`linkalho`/`90DNS`/`wiliwili`/`Moonlight`/`TriPlayer`）+ TriPlayer 的覆盖层 `switch/.overlays/ovl-TriPlayer.ovl` + 它的 sysmodule `atmosphere/contents/4200000000000FFF/{exefs.nsp,flags/boot2.flag}`。**`--absent` 也确认了** `switch/wiliwili/wiliwili/wiliwili.nro` **不存在**（`StripPrefix` 生效，没有多落一层）。
    ★ **决定性的一条**：落盘 `switch/linkalho/linkalho.nro` 的 sha256 = `c2b0c018…3ffcdd4`，与 **`linkalho.zip`（中文镜像仓库）** 里的同名条目**逐字节相同**（EN 仓库那个包的同名条目是 `5e2697cc…53ce81c0`）⇒ **「按界面语言换仓库」这件事一路正确落到了磁盘上**。⚠️ 这件事**只有比字节才作数** —— 两个仓库的包内路径完全相同（都是 `switch/linkalho/linkalho.nro`），落点、文件名、退出码全都一样，**只有内容能分辨**。
    ★ **e2e 顺带查出一条真问题**（见第八节第 74 条）：TriPlayer 那条 Warning 说「声明的 `triplayer-x.x.x.zip` 没有精确匹配，已按模式选用 `triplayer-1.1.1.zip`……**若这不是你要的文件，请在高级设置里改成确切的文件名**」—— 可**用户清单里给的就是这个通配写法**。根因是 `AssetMatchKind` 把「非 `Exact`」一律当成「猜」，而四级瀑布的**前两级都是「如你所愿」**（① 逐字 ② 照用户给的模式）。修法是把「算不算猜」抽成单一真源 `IsGuess`（只有 `Stem`/`SoleCandidate` 为真），日志按它分流：猜报 Warning 并建议改名，**按模式命中只报一句中性 Info**。同一次核对还查出 `linkalho` 的 EN 侧包名被我写成了**实测到的** `linkalho-v2.0.2.zip`，而用户清单给的是**模式** `linkalho-x.x.x.zip` —— 已改回模式，并补一条拿**实测名字**喂匹配器的断言（只钉字符串相等不够：把模式写成 `linkalho-zzz.zip` 也「相等」）。
    ★ **两个新工具**（都是「手工跑、不进离线回归」）：`tools/zip-listing.py`（**HTTP Range 只取 ZIP 中央目录** —— 几十 KB 拿到完整条目清单，不必下整包，见第八节第 72 条）与 `tools/e2e-evidence.py`（把「跑完之后磁盘上到底有什么」固定成文本：Error 计数 / `out/` 顶层是否 SD 根名 / `--expect` 落点命中 / `--absent` 不该存在的路径 / `--bytecheck` 逐字节比语言选包）。⚠️ 后者**首跑就误报一条**：把向导自己写在 `out/` 根的 `.wizard-manifest.json` 当成「非 SD 根名 ⇒ 多了一层包装目录」，已加白名单（**工具自己的误报也要当成 bug 修**，否则下次真出问题时没人信它）。
    ★ **第 74 条那一轮改动后的增量对账**（同样用**差集**）：基线 1673 → **1680**，**净增 10 / 净减 3 = +7**。净增 10 条逐条可解释 —— linkalho 的两个语言方向断言从具体版本号改成模式（`en-US`/`ja-JP` 各 1 条）、「CN 包名逐字 + EN 侧是模式」1 条、「模式必须能对上实测真实名、且属于按声明命中」1 条、「CN 侧必须走 `Exact` 一级命中」1 条、「按模式命中不是猜」1 条、`IsGuess` 穷举 4 条（`Enum.GetValues` 四个枚举值各一条）。净减 3 条**全是文案变更**（两条包名断言里的 `linkalho-vN.N.N.zip` → `linkalho-x.x.x.zip`，加一条被拆成上面两条更精确的「两个包名必须是实测存在的」）。⇒ 与 1680 吻合，且**没有任何既有护栏的执行次数发生变化**。
    ★ **第 75 条那一轮改动后的增量对账**（仍用**差集**）：基线 1680 → **1703**，**净增 27 / 净减 4 = +23**。净减 4 条**全是措辞改写**（4 条 `IsGuess` 断言原先把「不一致」写进通过时的文案里 —— 通过时那句话本身是假的，已改成「应当等于…实际…」）。净增 27 条逐项可解释：4（改写后的 `IsGuess`）+ 1（前提断言：枚举与穷举表都非空）+ 8（穷举表逐项：4「必须在表里」+ 4「`NoticeFor` 一致」）+ 1（不该有已删掉的命中方式）+ 1（两族分界：`Wildcard` 必须归「按声明命中」）+ 6（源码契约：方法体取到 / 调了 `NoticeFor()` / 不出现 `AssetMatchKind.` 直接比较 / 两个语言键各 1 条 / 前置条件）+ 4（Info/Warning 反向）+ 2（`switch` 必须有 `default`，且 `default` 必须抛异常）⇒ 与 1703 吻合，且**没有任何既有护栏的执行次数发生变化**。
    > ⚠️ 途中**又踩了一次「同文件多处 Edit 并行 ⇒ 报 success 却静默丢掉」**：一次发 4 个同文件 Edit，其中 2 处实际没写进去，是事后 `grep` 复查才发现的。**判据：同一文件的多次修改，一次消息只发一个 Edit；改完逐处 grep 复查，别抽查一处就以为全成。**
- ✅ 已完成（2026-09-18 第十五轮）：**修掉「中文界面 + 8G 拿到英文 Nyx」，并完成底部声明 / 软件图标 / 设置自动落盘** —— 用户给的四件事。
    ★ **Bug**：`ShouldUseLocalizedHekate` 原判据是「中文 **且** 没勾 8G」，于是勾了 8G 的中文用户长期拿到英文 Nyx（日志里那一行写着 `CTCaer/hekate`，看着很正常）。**先实测再动手**（第 36 条教训）：只勾 Hekate、`zh-Hans`、不勾 8G 跑一次 `--run`，日志证明这条路**本来就是好的**，于是把「只有两处会打回官方」摆给用户确认，答案是 8G 那条。修法是去掉 `&& !options.Ram8Gb`（判据只剩界面语言），并给 Hekate 加**第二个固定走官方的请求**去取 `__ram8GB.bin` 与备用标准 payload —— 汉化包与 payload 本来就是两批文件、两个来源，一个组件同时取两个仓库并不冲突。⚠️ 三个不能想当然的点（`RepoSpec` 是 record ⇒ 不能靠另起字段区分；开关只许关掉「语言镜像」那一条规则，不能跳过整个 `ResolveRepo`；第二个请求必须在**查询之前**被 `Applies` 滤掉，否则白花配额 + 假告警 + `v6.5.3 + v6.5.3`）见第八节第 76、78 条。护栏 ③b 钉两请求结构与 `Applies` 三种取值，⑤c 钉**跨语言等价**（中文+8G 的「镜像包 + 官方 payload」与非中文+8G 的「官方一次拿齐」payload 落点必须逐项相同）。真实 e2e 复跑：日志两条「正在查询」，`out/payload.bin` 与 `bootloader/update.bin` 的 md5 与官方 `__ram8GB.bin` **逐字节相同**。
    ★ **底部声明**：窗口最下一行固定显示「本软件免费分享，请勿用于商业用途。by：念若安止」。走语言键 `App.Notice`，**三份语言包取同一句中文** —— 署名与授权声明不随界面语言改写，所以「三份一样」是刻意为之（不是漏翻译），已在注释里写明免得后人「顺手补上翻译」。
    ★ **软件图标**：仓库根目录的 `ico.ico`（16/32/48/64 四种规格，32 位带 alpha）接到三处 —— exe 文件图标（csproj `<ApplicationIcon>`）、窗口标题栏与任务栏（`<Resource Link>` + XAML `Icon="/ico.ico"`）。⚠️ 前两者是**两套机制**（Win32 资源 vs WPF 程序集资源），只做 `ApplicationIcon` 的话任务栏/标题栏仍是默认图标；两处指向同一个文件，换图标只改那一个 ico。已核实 4 张图都真的嵌进了 exe（PE 里逐个 DIB 头命中）。
    ★ **设置自动落盘**：界面上的每一次改动都写进 `settings.json`，下次启动原样读回（组件勾选 / 配置项 / 全局开关 / 引导项名与图 / 下载源 / 插件文件槽 / 语言 / Token / 超时 / 代理）。做法是**在通知那一层接线**而不是回到几十个 setter 里手写保存；400ms 节流 + 关窗前补写。⚠️ 第一次就崩了（`NullReferenceException at ScheduleAutoSave`，回归当场抓到）：构造函数**中途**会改 `IsSelected` 而那时定时器还没建 —— 已改成字段声明处就建 + 加一道「构造函数跑完才上膛」的闸（顺带解决「启动即写存档」）。护栏 `CheckAutoSaveWiring()` 钉接线与次序、`CheckAutoSaveBehaviour()` **显式推 `DispatcherFrame`** 真跑一遍「改完不点保存 → 文件里真的有新值」（`DispatcherTimer` 只在消息泵里才 Tick，不推泵就等于没有行为断言），见第八节第 77 条。
    ★ 回归 1703 → **1731**（+28），**`SMOKE: ALL OK`、0 失败**；`--selftest` 0 绑定错误；8G + 中文的真实 `--run` 退出码 0、确实跑了两个仓库。
    **增量对账**（逐项，不用 grep 计数瞎猜）：②「语言是唯一判据」由 4 条改成 6 条（**+2**，简/繁 × 8G 两个方向都补上了，另加「英文界面 + 8G 也不该走」）+ ③b 请求结构与 `Applies` 三种取值 **+6** + ⑤c 跨语言落点等价（前置条件 + 等价）**+2** + `CheckRepoSources` ⑦ 的「官方行填充地址对 payload 那一路仍生效 / 镜像行的地址不许改道」**+2** + `CheckAutoSaveWiring` **+13** + `CheckAutoSaveBehaviour` **+3** = **+28** ⇒ 与 1731 吻合，其余既有护栏**一条执行次数都没变**。
    > ⚠️ 差点又写出一个编造的数字：这一格最初填的是「1735」（凭新增条目估的）。**计数必须来自最终汇总行**（`通过 N 项断言，失败 M 项。`），估出来的数字与编造的实测是同一宗罪（第 69 条）。
- ✅ 已完成（2026-09-18 第十六轮）：**「所有操作写入日志文件」—— 新增 `<exe>/logs/` 完整日志** —— 用户的原话是「需要看到我点击了哪个按钮，输入了什么文字」，所以这一轮的关键不是「多写日志」，而是**覆盖面**：按钮 25 个（现为 **24** —— 2026-09-19 按要求取消了「保存设置」按钮）、设置项 76 个，逐个手写必然漏，而漏记是静默的。
    ★ **按钮**：所有按钮（一处 `Click=` 都没有）统一经 `Command(CommandLabel, …)` 这一个入口建，日志在那一层打；源码契约断言「除该方法外不出现 `new RelayCommand(`」，新加按钮时**绕不过去**。标签由**若干语言键拼成**——界面上有 6 个「选择…」6 个「清除」，只记按钮文字等于没记；拼成「正版系统 · 启动图 · 选择…」才答得了「最后那一下到底点了什么」，且**最后一个键必须就是按钮上显示的那句**（扫 XAML 的断言钉住）。
    ★ **文字/勾选/下拉**：不做「哪个输入框触发就记哪个」（`TextBox` 是逐键更新，打一个词会记一串），而是**拿两份设置快照做差**（`SettingsSnapshot.Flatten/Diff`，纯函数）——能被改的东西必然出现在快照里，「有没有漏」不再取决于谁记得住；它天然复用自动落盘的 400ms 节流，连续输入被合并成一条，单行超过 12 项时只列前 12 项 + 「另有 N 项」。
    ★ **机密是硬约束**：日志是要发给别人看的，`GitHubToken` 写进去就收不回来。机密项只记「已设置 / 已清空」，判定按**键名包含** `Token`/`Proxy`/`Password`/`Secret`（`Proxy` 常带用户名密码，同样按机密处理）。回归里那条断言是**注入一个假 Token，再断言它在整个新增日志里一次都不出现**。
    ★ **会话头/尾**：开头记程序版本、运行库与系统版本、工作目录/设置文件/日志文件三个路径、代理是否已设置；结尾记「警告 N 条、错误 M 条」。崩溃也按行写进同一份日志（仍保留 `crash.log`）。
    ★ **保留策略**：按天分文件、只留最近 10 份；清理**只删自己前缀**的文件（用户手动放进 `logs/` 的东西一律不碰）——这条在正常路径下一整个进程只跑一次，集成测试够不着，所以 `PruneOldFiles` 开成 `internal` 测试接缝直接调它。
    ★ 界面上新增「日志目录」一行 + 「打开日志目录」按钮，提示文字里带**当天那个文件名**（用户不必在一堆日志里猜该发哪一份）。
    ★ **流水线早就预留了这个位置**：`AppPaths.LogRoot` 从第一版就声明着却没人用过，`build.sh` 的 `scrub_traces` 里写着 `rm -rf logs`、`check_shape` 会因它残留而报错、`refresh-dist.py` 的 `FORBIDDEN` 第一个版本就有 `"logs"` —— 但真做起来还剩两处只有跑流水线才暴露的坑（`rm` 对 `*.log` 被本机沙箱拦下 ⇒ 要进「删不掉就挪走」的兜底；那条兜底原来用 `ls -1`，而 `ls -1 logs` 列的是**目录里的文件** ⇒ 必须写 `ls -1d`，否则只会留下一个空目录）。见第八节第 79、80 条。
    ★ **差异基线 = 「保存时会写出来的那份快照」**：把「界面 → 存档」的映射抽成唯一的 `MaterializeInto`，构造函数拿一份新读的存档当探针取基线 —— 不然首次运行会把**当时那 46 个勾选 + 76 个配置项**全报成「新增」，**日志的第一句话就是假的**。见第 81 条。
    ★ **属性 setter 不再写盘**（曾经 `SelectedLanguage` / `AutoPackZip` 各写一次）：那会造成「启动即写」+ 写出 `"Components": {}` 的快照。实测现在**打开软件不会创建 `settings.json`**（改了东西才写），且自动落盘只在快照真的变了时才写。见第 82 条；护栏 `CheckPropertySettersDoNotWriteSettings` 已用注入验证确认「只有它报错」。
    ★ **用例自己必须幂等**：一个用例把 `settings.json` 里的 `BootSysNand` 翻了面没还原，导致另一条断言红绿交替；现在用例备份/还原、断言的**前提自己摆**、Main 末尾还有一条「跑完 `settings.json` 与开跑前逐字节相同」的幂等护栏（外加按小节定位的探针）。见第 83 条。
    ★ 回归 1731 → **1761**（+30），**`SMOKE: ALL OK`、0 失败**，**连跑两次结果一致**；`--selftest` 0 绑定错误、退出码 0，并留下真实的 `logs/SwitchCfwWizard-2026-09-18.log`（会话头、三条启动日志、会话尾）。
- ✅ 已完成（2026-09-18 第十七轮）：**新增「离线固件」组件（THZoria/NX_Firmware）** —— 用户要求：从该仓库下 `Firmware x.x.x.zip`，解压放到 `out\Firmware\Firmware x.x.x\`；**下载地址与下载文件名都要能改**（放高级设置），不改就用他给的那一对，并且照旧走日志与 `settings.json` 往返。
    ★ **新开一个「固件」分类**（顺序紧跟「框架」）：固件是**系统本体**，而框架是「把系统跑起来的东西」；混在一起会让用户在「我要不要更新系统」这件事上失去判断，而更新固件是**不可逆**操作。
    ★ **实测先行**：走 GitHub API 把 92 个版本逐个数过（资源名一律 `Firmware.<版本>.zip`，**点分隔、从无空格**），再用 `zip-listing.py` 读中央目录（**包内是平的**：238 个 `.nca` 全在压缩包根，没有顶层目录）。两条都写进了代码注释与断言里（第 70 条那条纪律）。
    ★ **落点新增 `{asset}` 占位符**：既然包内没有版本目录，那个目录就只能从**下载文件名**造出来 ⇒ `new OutputTarget("Firmware/{asset}")`，替换点在落点的唯一出口 `AssetPick.ResolvedTargets`。副产物是个好性质：**目录跟着文件名走**，改名字不会出现「文件叫 A、目录叫 B」。
    ★ **默认文件名是模式不是版本号**（`Firmware.x.x.x.zip`）：写死版本号的话上游一发新版默认值就作废，而用户不知道自己改坏了什么。
    ★ 新增护栏：落点对照表补 `Firmware`（这张表的覆盖性断言**当场就把漏掉的组件点了名**）+ ⑤d 组（默认值/通配命中实测资源名/落点解析/解压计划/界面落点提示）+ 界面那一行（默认地址与文件名、落点提示）+ `CheckFirmwareStaging()`（**真跑一次暂存**，用实测的真实条目名造包，断言文件真的解在 `Firmware/Firmware.23.0.0/` 下且没有字面量 `{asset}` 目录）。
    ★ 新增一条**之前完全没有覆盖**的用例：高级设置里**槽那一行**的地址/文件名过一遍 `settings.json` 往返 —— 既有的持久化用例只挑标量属性（`IsScalar`）与 `OptionValues`，而这两项存在 `AssetAddresses`/`AssetFileNames` 两个**字典**里，40 多个插件的地址能不能存下来其实一直没被测过。`CheckAssetSlotRowPersistence()`
    ★ 回归 1761 → **1798**（+37）；`SMOKE: ALL OK`、0 失败。⚠️ **这一轮几乎没有被「新增组件」打穿**：既有的「每组件一条」护栏（槽 Key 唯一性、`WizardOptions` 同名 bool、`IsSelected` 反射分派、分类文案键三份齐全、假下载目录覆盖、分组投影不丢卡片……）全部**自动**把新组件纳了进来 —— 唯一红的那一条是**手写**的落点对照表。这正是把「清单从真源现算」坚持下来的回报。
- ✅ 已完成（2026-09-19 第十八轮）：**GitHub 镜像站可配 + 取消「保存设置」按钮** —— 用户给了一个加速镜像的用法（换主域名 / raw 走 `<镜像>/raw/…`），要求做成高级设置里的一项、以后新增插件也要用到；同一条消息里还要求高级设置改完即存、取消保存按钮。
    ★ **镜像站的三种形态是量出来的**：`github.com/…`（含 340 MB 资源包）、`raw.githubusercontent.com/…`（走 `/raw/…`）、以及**其它主机**（api / codeload）走 `<镜像>/proxy/<主机>/…`。⚠️ **本轮量错了一次并差点写进交付物**：只试了按「主域名换掉」拼的 `<镜像>/api.github.com/…` 得 404，就判定「镜像不代理 API」，据此写下「查询只能直连」「连 API 都不通的网络镜像帮不上忙」——都是假的；真正的规则是通用前缀，而**镜像自己的 302 目标 `/proxy/release-assets.githubusercontent.com/…` 早就把规律摆出来了**。教训：**一个「不支持」的结论必须先问「是不是我拼错了」，并且动手前先读项目技能 §5（那条实测事实早就记在那里）**。
    ★ **下载与查询用法故意不同**：下载「填了直接走镜像」，查询「官方优先、只有**连接层**失败才换镜像」—— 镜像出口 IP 公用，未登录的 API 额度按 IP 算，实测把查询直接换过去**第一次就 403 限流**（「填了镜像反而一个组件都查不到」）。`403` 是 GitHub 的回答不是网络问题，所以不换。
    ★ **改写落在两处唯一出口**（`DownloadService.DownloadAsync` / `GitHubReleaseService` 的两个查询方法）⇒ 以后新增路径**绕不过去**；**两条下载通道都改**（直链 + API asset 端点）⇒ 不会出「直链走镜像、备用通道偷偷回官方」的半吊子状态。日志印的是同一个纯函数、同样入参的结果 ⇒ 印的与真请求的必然同源。
    ★ **填错不许更糟**：解析不出可用地址（含「整段粘了 `owner/repo`」这一种结构合法但必 404 的）就当成没填、整条退回官方，界面红字提示 + 会话头写明「本次仍直接访问 GitHub」。**「验证 Token」刻意不走镜像**（反向源码契约钉住）。
    ★ **取消按钮带出的两件事**：① `MaterializeInto` 上的 `reportInvalidAddresses` 旗标成了**恒假的死参数** ⇒ 映射回归纯函数，非法地址告警单独成一步并**按值去重**（触发点从「按一次」变成了「反复结算」）；② 用例里十几处 `SaveSettingsCommand.Execute(null)` 失去意义（它曾**无条件写盘**）⇒ 新增 `TouchAndFlush()` 显式制造一次改动再落盘。
    ★ 回归 1798 → **1846**（+48）；`SMOKE: ALL OK`、0 失败、**连跑两次一致**。新增护栏：`CheckGitHubMirror()`（纯函数穷举 + **桩 handler 看实际请求的 URI**（不联网就能验「真的打在镜像主机上」，两条下载通道各一条）+ 查询通道的「官方优先/镜像兜底」+ 源码契约 + **「验证 Token」反向契约**）、`CheckMirrorSettingPersistence()`（**重开一个 VM** 读回来）、以及两条「**「保存设置」按钮不许回来**」。见第八节第 85 条。
    ⚠️ 唯一那 2 项红是**测试数据**造的坑：`"这不是一个 owner/name"` 带一个斜杠，恰好满足 `owner/name` 的「恰好两段」判据，**它其实合法** —— 造不出预期的坏输入时，先怀疑输入。
- ✅ 已完成（2026-09-20 第十九轮）：**暗夜模式主题**（顶部 / 语言旁边，三档：跟随系统（默认）/ 浅色 / 暗夜；近纯黑配色）—— 用户要求「新增暗夜模式主题，放在语言选项旁边」，随后明确「默认主题跟随系统」。
    ★ **被实测推翻的省事方案**：本以为「颜色集中在一处，原地改共享 Brush 的 `Color` 就行、160 多处引用一处都不用动」，实测 Application 级资源里的 Freezable **全被 WPF 自动冻结**（22/22）⇒ 改不了。最终走「**换未冻结的新实例 + `DynamicResource`**」（63 处引用改过来）。⚠️ 抓住这件事的是 `--selftest` 里新加的那条**资源体检** —— 没有它，交付物会是一个「切主题毫无反应、也不报错」的功能。
    ★ **端到端证据**：除逐键核对资源值外，还读**真实控件**的颜色 —— 窗口 `#FFF4F6F9 → #FF0B0B0D`、文本 `#FF1D2129 → #FFEDEEF1`。
    ★ **ComboBox / CheckBox / Expander / ScrollBar 重画了模板**（系统原生模板不跟资源键走，暗夜下会保持浅色）；**只有系统对话框（MessageBox / 文件选择器）如实留着系统外观**（它们走 Win32/COM，WPF 资源字典碰不到）。
    ★ 回归 1846 → **1886**（+40）；`SMOKE: ALL OK`、0 失败；`--selftest` 主题体检 0 问题。见第八节第 86 条。
    ★ **用户实测报出两个 bug，都是本轮重画模板引入的，而且根因同类**：① 语言 / 主题下拉框**关闭时显示类型名**（`LanguageInfo{Code…`、`SwitchCfwWizara.`）、展开却正常 —— 重画 ComboBox 模板时漏了 `ContentTemplateSelector="{TemplateBinding ItemTemplateSelector}"`，而 `DisplayMemberPath` 正是**通过它**生效（漏了 ⇒ `SelectionBoxItemTemplate` 为 null ⇒ 关闭态退回 `SelectedItem.ToString()`）。② **「配置选项」四个字在暗夜下是黑的** —— 重画 Expander 模板时漏了 `Foreground="{TemplateBinding Foreground}"`，`ToggleButton` 拿不到前景色就退回系统黑。**「展开正常、关着异常」是这类错误的特征**：两条路不同源。
    ★ ⚠️⚠️ **同一类错误犯了两次，所以补了两条通用护栏**（不再靠「下次记得」）：`--selftest` 现在**读渲染出来的实际文本**（`combo-shown lang="简体中文" theme="跟随系统"`），并**扫暗夜下全部文字的前景色**（实测 **749 个文字、0 个过暗**）—— 前者验「显示对不对」，后者验「有没有哪块文字退回系统黑」。
- ✅ 已完成（2026-09-21 第二十轮）：**boot.dat 改为下载 + 「检查更新」+ Cloudflare 建站入口** —— 用户要求：① boot.dat 取消内置、从仓库下载、地址可改（未填写用默认）、要日志与落盘、开关从「输出内容」移到「框架」、可用镜像站、落到 `out` 根目录；② 镜像站旁边加「Cloudflare搭建镜像站」按钮跳说明文档；③ 新增「检查更新」（查最新发布 → 比版本 → 下载成 `SwitchCfwWizard_<版本>.exe` → 启动它 → 退出旧版；地址可改、要日志、不写配置、可用镜像站）。
    ★ **先量上游：两条默认地址都不是「能直接下」的地址** —— `…/blob/main/boot.dat` 是网页形态（下下来是 HTML、HTTP 200，**静默**产出一个假引导文件）；`…/releases` 是列表页（没有版本 JSON，能查的是 `api.github.com/repos/…/releases`）。⇒ 各加一层纯函数解析（`BootDatSource` / `UpdateSource`）：**把用户给的地址翻译成真正取字节的地址**，翻译不出来就明确拒绝、绝不猜。
    ★ **boot.dat 的实测对照**：仓库里那份 = **11520 字节、sha256 `df8003f0…`**，与原先内嵌的那份**逐字节相同** ⇒ 取消内置不丢任何东西（因此才敢删掉 `Resources/boot.dat`）。它同时给出**备用通道**（`api.github.com` 的 contents 端点）：本沙箱里 `raw.githubusercontent.com` **DNS 都解析不出来**，兜底是唯一能把字节取回来的那条路。
    ★ **boot.dat 失败只警告不中止**，但**不留空文件** —— 写一个 0 字节的 `boot.dat` 比不写更坏：它看起来「有」，而 SD 卡启动时才失败，用户找不到是哪个文件的问题（回归里单有一条断言）。
    ★ **检查更新的顺序是刻意的**：先启动新版、**等两秒确认它活着**，再退出自己。反过来的话，缺 .NET 8 桌面运行时的机器上会落成「旧版关了、新版起不来」—— 最难恢复的一种失败。退旧版走 `Application.Shutdown()`（正常关窗路径：会补写存档、摘掉主题订阅），不是 `Environment.Exit`。
    ★ **版本比较的坑**：程序集版本是 `0.1.0.0`、Release 的 tag 常是 `0.1.0`，直接 `Version.CompareTo` 会认为 `0.1.0 < 0.1.0.0` ⇒「同一个版本」被判成「有新版本」，每次点都提示更新。统一补零到四段再比。
    ★ **回归那 4 项红全是新护栏抓到的，而且抓的是真问题**：① `BootDatPath` 被「每个 WizardOptions 标量属性都要有界面来源」判成漏接线 —— 它是**运行期管道值**，已进例外清单并写明理由（而不是放松护栏）；②③ 按钮文案键与命令日志键必须是**同一句**（`App.CheckUpdate`），改之前日志会把这次点击说成另一个按钮、语言键覆盖护栏还会报缺键。
    ★ 回归 1886 → **1956**（+70）；`--selftest` 新增一条**跨 DataContext 绑定**的判据（`bootdat-boxes=1`，读**渲染出来的**勾选状态）：分组模板里的 DataContext 是 `ComponentCategoryViewModel`，漏写 `RelativeSource` 时框照常显示、只是永远勾不动 —— 那种失效**不报任何错**。
    ★ 顺手修掉一处旧瑕疵：`Settings.Mirror.Invalid` 文案里带 `{0}`，而它在输入框下面是**静态显示**的（`{loc:Loc}` 只绑原文、不做格式化）⇒ 界面上真的显示着一个没被替换的 `「{0}」`。新增的同类文案一律不带占位符。
    ★ 顺手改掉一处**过时数字**：第五节写着「三份内置语言包各有 359 个键」，实测是 **491**（文案键 490）—— 它在前几轮的加键里漂了，没人发现。
- ✅ 已完成（2026-09-21 第二十一轮）：**boot.dat 的五处调整 + 两个地址框的格式提示** —— ① 勾选框文案改为「boot.dat」；② 加下载进度条；③ **默认不勾选**；④ 下载落到 `download/boot/`；⑤ 两个地址框加框内格式示例，并把可填样式逐条写清。
    ★ **「默认不勾选」有三处默认值**：`AppSettings`（存档读不到这个字段时的兜底）、`WizardOptions`（构建选项时的初值）、视图模型的字段初值 —— 漏改任何一处，都会在某条路径上「看起来改了、其实没改」。回归里两边各断言一次，`--selftest` 再读**渲染出来的勾选状态**（`bootdat-boxes=1 checked=False`）把它钉死。
    ★ **进度条挂在主视图模型上**（`BootDatProgress`/`BootDatStatus`/`HasBootDatStatus`），因为 boot.dat 不是组件、没有卡片可挂；三者都进 `TransientPropertyNames`（运行时状态，不许落盘）。没勾 / 地址坏 / 被取消时**清空状态** —— 留着上一次的「已下载到 …」比不显示更坏（用户会以为这一轮也下过了）。（注：`HasBootDatStatus` 是**当时**的成员 —— 第二十二轮改成「那一行常驻」之后它没有消费者了，已连跳过表一起删掉。）
    ★ ⚠️⚠️ **两个地址框的格式提示没走 `ElementName` 绑 TextBox.Text**：绑定护栏要求每个 `{Binding X}` 的 X 是**本程序集**某类型的公开属性，而 `Text` 是 WPF 控件的属性（程序集里没有同名属性）⇒ 那样写会被判「绑定名不存在」。改用**派生属性** `BootDatUrlIsEmpty` / `UpdateUrlIsEmpty` 驱动可见性，与 `MirrorIsInvalid` / `HasTokenStatus` 同一套做法。
    ★ ⚠️⚠️ **改 XAML 括号时踩了一个自己造的坑，值得单记一条**：我为了修「脚本写出的双大括号」跑了一遍
      `line.replace("{{","{").replace("}}","}")` —— 而 XAML 里**嵌套标记扩展的收尾本来就是 `}}`**
      （`Visibility="{Binding X, Converter={StaticResource BoolToVis}}"`）。那一下把 **42 行**的属性名后面插进了多余的 `}`，
      文件当场编不过。修法不是继续打补丁，而是**重建**：回退包里的 `MainWindow.xaml`
      正是「上一轮之前」的状态，而这两轮的 XAML 改动都有带「恰好命中一次」断言的脚本 ⇒
      还原 + 重放两个脚本（写回后字符数与当初逐字节一致），再补上那次手工 Edit。
      **教训：`}}` 在 XAML 里是合法收尾，不是重复括号；要判断括号是否缺失，只能用括号深度，不能数个数。**
    ★ 回归 1960 → **1964**；`--selftest` 0 绑定错误、`bootdat-boxes=1 checked=False`、
  **`url-hints bootdat=1 update=1`（空值）→ `after fill=0/0` → `after clear=1/1`**（框内提示的可见性往返）、`dark-texts=776 too-dark=0`。语言键 **491 → 493**（两个框内提示键）。
- ✅ 已完成（2026-09-22 第二十二轮）：**「检查更新」加下载百分比 + boot.dat 卡片进度条改为始终显示** ——
  ① 版本更新那行结论后面显示下载百分比；② 「框架」里 boot.dat 那张卡片上的进度条不再按「有没有下过」隐藏。
    ★ **百分比与状态文字刻意分开**：状态文字整句进日志（一条干净的结论），而百分比是高频变化的界面数字 ——
      混进日志只会把它刷满。它只在 `IsUpdateDownloading` 那段时间出现（查完是最新版时留个「0%」会让人以为还在下）。
    ★ **「始终显示」带出的连带问题**：那一行文字原来只在真的有下载状态时才有内容 ⇒ 进度条常驻之后必须保证
      **文字永不为空**，否则用户看到的是「一条 0% 的进度条 + 一行空白」，像卡片坏了。新增派生属性
      `BootDatStatusText`（空状态取「未勾选 / 就绪」），切语言时在 `RefreshLocalizedText` 里刷新；
      已经没有消费者的 `HasBootDatStatus` 连同跳过表里那一条一起删掉。
    ★ **新判据 + 注入验证**：`--selftest` 从勾选框往上找到那张卡片的 `Border`，读卡片里**渲染出来的**
      进度条可见性（`bootdat-bar visible=1/1`）。⚠️ 光有判据不够 —— 我临时把 `Visibility="Collapsed"` 注回去跑了一次，
      自检当场报 `visible=0/1` + `theme-problems=1`，这才证明它不是恒真的；撤掉注入再跑恢复全绿。
    ★ 回归加两条：`CheckUpdateProgressIsWired()`（**不许**再传 `progress: null` + 界面那一侧真绑了
      `UpdateProgressText` / `IsUpdateDownloading`）、`CheckBootDatStatusText()`（两种空状态各一条，
      外加「两条文案必须不同」的反向断言 —— 否则那两条等于只测了一条）。
    ★ 回归 1964 → **1973**；`--selftest` 0 绑定错误、`bootdat-bar visible=1/1 value=0`、
      `dark-texts=777 too-dark=0`、`theme-problems=0`；语言键不变（复用了现成的 `Common.Waiting` / `Common.NotSelected`）。
- ✅ 已完成（2026-09-22 第二十三轮）：**boot.dat 卡片与组件卡片逐项对齐 + 检查更新地址的回落与对齐** ——
  ① 那张卡片的字体与「未勾选」都改成以 `Sys-patch` 卡片为准；② 「检查更新的地址」输入框与下方「下载源」那些行左对齐（以 `Atmosphere` 为准），可填 `owner/name` 或完整链接，**填了但格式不对回落到默认地址**。
    ★ **「以某处为准」这种要求，判据要拿那个「某处」当参照物**：`--selftest` 现在直接找**运行时的
      Sys-patch 卡片**，逐项比标题 / 说明 / 状态文字的字号、字重、前景色（`card-parity`）——
      比「我写了 `FontSize=14`」硬得多，而且这种「两处应当一样」的约定最容易在后续某次改动里悄悄分叉。
    ★ **对齐也是可以量的**：判据读**渲染出来的坐标**（`TransformToAncestor`），当时实测 `box-align update=907 "Atmosphere"=907`。
      ⚠️ **这条对齐在第二十四轮按用户要求撤销了**（改成与「boot.dat 下载地址」那个框同宽同左边缘）——
      下面那一轮记的才是现状；这里保留原样是作为「当时是怎么理解的」，别照着它改回去。
      ⚠️ 定位时踩了一次：「Atmosphere」在界面上有**两处**（组件卡片标题 + 下载源那一行的标签），
      第一版直接取第一个匹配，命中的是前者、于是判据自己先失效 —— 加一条「同一行里得有输入框」才准。
      **诊断信息写清「哪一侧缺失」比只写「缺失」省一整轮**。
    ★ **改锚点 = 自检会当场失效**：把标题从勾选框的 `Content` 搬到 `TextBlock` 之后，靠 `Content` 找那张卡片的
      判据立刻一个都找不到 —— 判据失效这件事本身被自检报了出来，这正是它该有的样子。
    ★ **行为抽成纯函数，才谈得上「测过」**：`ResolveWithFallback` 以前埋在 `CheckUpdateAsync` 里（联网才跑得到），
      抽出来之后「填坏了回落默认地址」这条行为第一次可以被**离线穷举**。
    ★ 回归 → **1985**；`--selftest` 0 绑定错误、`card-parity` 三项一致、`box-align` 两侧同为 907、
      `theme-problems=0`。语言键不变（沿用现成的 `Common.Waiting` / `Common.NotSelected`）。
- ✅ 已完成（2026-09-22 第二十四轮）：**「检查更新的地址」的框与「boot.dat 下载地址」同宽同对齐 + 勾选后显示「就绪」** ——
  ① 撤销上一轮按「下载源」行加的 96px 左缩进（用户：「前面空出来很多，有点难看」），两个地址框都是通栏；
  ② `boot.dat` 卡片上那一行空状态文案由「等待中」改成「**就绪**」（`Common.Ready`，与组件卡片同一句文案）。
    ★ ⚠️ **需求翻转 ⇒ 判据要换掉，不是留着报警**：上一轮那条 `box-align update=907 "Atmosphere"=907`
      在这一轮**当场就该失效** —— 它不是「坏了」，是新要求不要它了。换成 `box-parity`：比两个地址框的
      **左边缘**与**宽度**（实测 `update=(811,339) bootdat=(811,339)`）；并且**改用绑定名定位**
      （`BootDatUrl` / `UpdateUrl`）—— 上一轮靠「旁边的文字」找框，用户一改文案判据就自己失效。
    ★ ⚠️ **沙箱里 shell 的路径视图会失效**（这一轮踩了两次）：构建**成功后** `cd` 进
      `bin/Release/net8.0-windows` 报「不存在」，而就在同一条命令里、同一个 shell 刚构建成功；
      换 Python 一看，目录在、exe 是最新时间戳。⇒ **「构建后 cd 失败」不等于「构建没产出」**，
      别据此重跑构建。自检与回归现在都由 `.workbuddy-ai/scratch/run-selftest.py` /
      `run-smoke.py`（Python 起子进程 + 自己清产物）驱动。
    ★ **自检新增 `--shot=路径`：把窗口渲染成 PNG**（`RenderTargetBitmap`）。这一轮的两条要求都是视觉的 ——
      「框一样宽吗」「卡片上写的是哪两个字」，看图比读 XAML 硬。
    ★ 回归加一条：切换 `IncludeBootDat` 时**必须通知** `BootDatStatusText`。派生属性算得对 ≠ 界面会更新，
      而只读属性值的断言在「漏了 `OnPropertyChanged`」时**照样全绿** —— 用户要的那一半恰恰是通知。
    ★ 回归 1985 → **1986**；`--selftest` 0 绑定错误、`box-parity` 两框同为 `(811,339)`、
      `card-parity` 三项一致、`theme-problems=0`。语言键不变（`Common.Ready` 是三份包里现成的）。
- emuMMC 分区创建与 payload 注入仍属手动操作（见第九节第 1、2 条）
