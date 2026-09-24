namespace SwitchCfwWizard.Models;

/// <summary>
/// 组件的**分类文件夹**（2026-09-18 用户要求：把插件按用途分组显示）。
///
/// ⚠️ **枚举成员的书写顺序就是界面上的分组顺序**（<see cref="System.Enum.GetValues{T}"/> 按声明序返回），
/// 所以这里不是随便排的：先「框架」—— 那四个是整套 CFW 的骨架，没有它们别的都跑不起来；
/// 紧接着「固件」—— 离线固件是给机器**升级/修复系统**用的那一份东西，同样在最前面才找得到；
/// 再「组件」「后台」「主题」「底层」，然后是依附于 Ultrahand 的「Ultrahand插件」，
/// 最后是「学习」（与 CFW 本身无关的附加软件）。
///
/// 刻意**不**在别处再写一份「分类清单」：那种手抄清单自己会腐烂（新增分类忘了加进去，
/// 界面上就少一组，而且没有任何东西会报错）。分组直接用这个枚举，护栏也从它反射生成。
/// </summary>
public enum ComponentCategory
{
    /// <summary>框架：Atmosphere / Hekate / Ultrahand / Sys-patch —— CFW 的骨架。</summary>
    Framework,

    /// <summary>
    /// 固件：机器要跑的那个**系统本体**（离线固件包）。
    ///
    /// 刻意单开一组，不并进「框架」：框架是「把系统跑起来的东西」，固件是「系统本身」——
    /// 混在一起会让用户在「我要不要更新系统」这件事上失去判断（而更新固件是**不可逆**的操作）。
    /// </summary>
    Firmware,

    /// <summary>组件：常驻后台之外的工具型 homebrew（存档管理、安装器、文件管理……）。</summary>
    Component,

    /// <summary>后台：常驻内存的 sysmodule（网络服务、远程操控……）。</summary>
    Background,

    /// <summary>主题：换肤相关。</summary>
    Theme,

    /// <summary>底层：RCM / payload 级的工具，坏了要拆机，风险最高。</summary>
    LowLevel,

    /// <summary>Ultrahand插件：依附于 Ultrahand Overlay 的小工具（<c>.ovl</c>）。</summary>
    UltrahandPlugin,

    /// <summary>学习：与 CFW 无关的附加软件（媒体播放、串流……）。</summary>
    Learning,
}
