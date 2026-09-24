using System.IO;
using System.IO.Compression;
using SwitchCfwWizard.Models;

namespace SwitchCfwWizard.Services;

public enum ValidationLevel
{
    Ok,
    Warning,
    Error,
}

/// <summary>
/// 一条校验结果。<see cref="Key"/> 是本地化键，<see cref="Detail"/> 填进 {0}。
/// 之所以返回「键 + 参数」而不是拼好的字符串，是为了让回归测试断言结构，
/// 不受界面语言影响。
/// </summary>
public sealed record ValidationItem(ValidationLevel Level, string Key, string Detail = "");

public sealed class ValidationReport
{
    public List<ValidationItem> Items { get; } = new();

    public int ErrorCount => Items.Count(i => i.Level == ValidationLevel.Error);

    public int WarningCount => Items.Count(i => i.Level == ValidationLevel.Warning);

    public bool IsHealthy => ErrorCount == 0;

    internal void Add(ValidationLevel level, string key, string detail = "")
        => Items.Add(new ValidationItem(level, key, detail));
}

/// <summary>
/// 生成完成后对 out/ 目录做一次体检。
///
/// 目的是拦住「配置看起来生成了、拷进 SD 卡却启动不了」这类问题——最常见的一种是
/// 用户勾了组件却没开「把组件文件合并进 out」，于是 out/ 里只有几个 ini，
/// 缺了真正干活的 package3 / 内核文件。
/// </summary>
public static class OutputValidator
{
    /// <summary>关键启动文件：少了它 Atmosphere 一定起不来。</summary>
    private const string Package3Relative = "atmosphere/package3";

    public static ValidationReport Validate(
        WizardOptions options,
        string outputRoot,
        ConfigGenerationResult result)
    {
        var report = new ValidationReport();

        // 1) 清单里记录的每个文件都必须真实落在磁盘上。
        //    清单现在包含配置文件 + payload + 合并进来的组件本体，所以这里能一并体检。
        var missing = result.WrittenFiles
            .Where(relative => !File.Exists(Path.Combine(outputRoot, relative)))
            .ToList();

        if (missing.Count > 0)
        {
            report.Add(ValidationLevel.Error, "Check.Error.MissingFile", string.Join("、", missing));
        }
        else
        {
            report.Add(ValidationLevel.Ok, "Check.Ok.ConfigFiles", result.ConfigFiles.Count.ToString());
        }

        // 2) 90DNS hosts 文件是否按选项齐全。
        //    两个开关各管一个系统：真实 → default.txt + sysmmc.txt，虚拟 → default.txt + emummc.txt，
        //    两个都开 → 三份都要在（default.txt 是两者共用的遥测屏蔽表）。
        //    与 ConfigGenerator.WriteHostsFiles 用**同一套判据**（开关 && 对应引导模式），
        //    否则这里会去要一份生成端根本不会写的文件。
        var wantSysmmcHosts = options.Use90DnsSysmmc && options.BootSysNand;
        var wantEmummcHosts = options.Use90DnsEmummc && options.BootEmuNand;

        if (options.Atmosphere && (wantSysmmcHosts || wantEmummcHosts))
        {
            var expected = new List<string> { "default.txt" };
            if (wantSysmmcHosts)
            {
                expected.Add("sysmmc.txt");
            }

            if (wantEmummcHosts)
            {
                expected.Add("emummc.txt");
            }

            var hostsRoot = Path.Combine(outputRoot, "atmosphere", "hosts");
            var missingHosts = expected.Where(name => !File.Exists(Path.Combine(hostsRoot, name))).ToList();

            if (missingHosts.Count > 0)
            {
                report.Add(ValidationLevel.Error, "Check.Error.MissingHosts", string.Join("、", missingHosts));
            }
            else
            {
                report.Add(ValidationLevel.Ok, "Check.Ok.Hosts", string.Join("、", expected));
            }
        }

        // 3) Hekate 勾了却没勾任何引导项 —— 生成的 hekate_ipl.ini 是空的，进不去系统
        if (options.Hekate && !options.HasAnyBootEntry)
        {
            report.Add(ValidationLevel.Warning, "Check.Warn.NoBootEntry");
        }

        // 4) 组件文件合并情况
        if (options.IncludeComponentFilesInOutput)
        {
            if (result.MergedFileCount > 0)
            {
                report.Add(ValidationLevel.Ok, "Check.Ok.Merged", result.MergedFileCount.ToString());
            }
            else
            {
                // 开了合并却一个文件都没合并进来：多半是解压目录被删了或组件没下完
                report.Add(ValidationLevel.Error, "Check.Error.MergedNothing");
            }

            // 关键文件单独确认：合并了 47 个文件但恰好漏了 package3 也照样开不了机
            if (options.Atmosphere)
            {
                if (File.Exists(Path.Combine(outputRoot, Package3Relative)))
                {
                    report.Add(ValidationLevel.Ok, "Check.Ok.Package3");
                }
                else
                {
                    report.Add(ValidationLevel.Error, "Check.Error.Package3");
                }
            }

            // 逐个组件对账。上面两条都是**计数/单点**判断，证明不了「每个勾了的组件都在」：
            // 四个组件里有一个一个文件都没合进来，只要另外三个合进来几十个文件，
            // MergedFileCount 照样 > 0，报告写着「已合并 N 个组件文件」，用户拿到的是缺件产物。
            //
            // 名单从 ComponentCatalog.All 里取（**不是手抄**），以后加第 5 个组件自动覆盖。
            // 这里只报 Warning 不报 Error：Ultrahand 在「手动安装 + 三个子项全关」时本来就
            // 没有文件可合并，那种情况下载阶段已另有告警 —— 漏报比误报贵，但误报会让人
            // 学会无视告警，所以分级要保守。
            var notMerged = ComponentCatalog.All
                .Where(definition => options.IsSelected(definition.Kind))
                .Where(definition => result.MergedFilesByComponent.GetValueOrDefault(definition.Kind) == 0)
                .Select(definition => ConfigGenerator.FolderName(definition.Kind))
                .ToList();

            if (notMerged.Count > 0)
            {
                report.Add(
                    ValidationLevel.Warning,
                    "Check.Warn.ComponentNotMerged",
                    string.Join("、", notMerged));
            }
        }
        else if (options.HasAnyComponentSelected)
        {
            // 最常见的使用误区：只拷 out/ 就以为完事了
            report.Add(ValidationLevel.Warning, "Check.Warn.MergeOff");
        }

        // 5) payload 落点。payload 是散装下载的（不在解压目录里），落点是**固定契约**：
        //      · Atmosphere 的 fusee.bin → bootloader/payloads/
        //      · Hekate 的 payload       → 根目录 payload.bin + bootloader/update.bin
        //    这里按「文件到底在不在它该在的地方」判断，而不是数「复制了几个文件」——
        //    以前只数个数，所以「复制到了，但放错目录」照样报通过。
        if (options.IncludePayloads)
        {
            var expectedPayloads = new List<string>();
            if (options.Atmosphere)
            {
                expectedPayloads.Add("bootloader/payloads/fusee.bin");
            }

            if (options.Hekate)
            {
                expectedPayloads.Add("payload.bin");
                expectedPayloads.Add("bootloader/update.bin");
            }

            if (expectedPayloads.Count > 0)
            {
                var missingPayloads = expectedPayloads
                    .Where(relative => !File.Exists(Path.Combine(outputRoot, relative.Replace('/', Path.DirectorySeparatorChar))))
                    .ToList();

                if (missingPayloads.Count > 0)
                {
                    report.Add(ValidationLevel.Warning, "Check.Warn.PayloadsMissing", string.Join("、", missingPayloads));
                }
                else
                {
                    report.Add(ValidationLevel.Ok, "Check.Ok.Payloads", string.Join("、", expectedPayloads));
                }
            }
        }

        // 6) Ultrahand 的 default_lang 指向的语言包，必须在产物里真的存在。
        //
        //    上游 source/main.cpp 拿这个值拼 config/ultrahand/lang/<代码>.json；文件不存在时
        //    该语言在 Ultrahand 自己的设置里会被**跳过**，于是「配置说中文、卡上没有中文包」
        //    会静默退回编译进去的英文 —— 用户以为换了语言，实际什么都没发生。
        //    en 是唯一例外（英文文案在 ovlmenu.ovl 里），所以只有非 en 才需要这份文件。
        //
        //    判据与生成端**同源**（同一个 ResolveUltrahandDefaultLang / UltrahandLangFileFor），
        //    不各写一套 —— 否则这里会去要一份生成端根本不会写的文件。
        if (options.Ultrahand)
        {
            var requestedLang = ComponentCatalog.ResolveUltrahandDefaultLang(
                options.Get(ComponentKind.Ultrahand, "default_lang"),
                options.Language);

            if (!string.Equals(requestedLang, ComponentCatalog.FallbackLanguageCode, StringComparison.Ordinal))
            {
                var relative = ComponentCatalog.UltrahandLangFileFor(requestedLang);
                if (!File.Exists(Path.Combine(outputRoot, relative.Replace('/', Path.DirectorySeparatorChar))))
                {
                    report.Add(ValidationLevel.Warning, "Check.Warn.DefaultLangNoLangFile", requestedLang);
                }
            }
        }

        // 7) 两个勾了的组件装同一个 sysmodule title —— 必然互相覆盖。
        //
        //    这是**产物层面**的硬冲突，不是「建议别这么选」：两个包都把内容铺到
        //    atmosphere/contents/<title>/ 下，合并阶段后做的那个赢，用户拿到一张
        //    「两个都勾了、界面没报错、但只有一个真装上了」的卡 —— 正是本项目最在意的静默失败。
        //
        //    判据来自各槽自己声明的 SysmoduleTitleId（实测得来），所以是**穷举规则**：
        //    再来一个撞车的 sysmodule 不需要改代码（见 ComponentCatalog.FindTitleConflicts）。
        foreach (var conflict in ComponentCatalog.FindTitleConflicts(options))
        {
            report.Add(
                ValidationLevel.Error,
                "Check.Error.TitleConflict",
                string.Join("、", conflict.Kinds.Select(ConfigGenerator.FolderName)));
        }

        return report;
    }

    /// <summary>
    /// 打包后回读压缩包，确认清单里的文件一个都不少。
    /// 返回缺失的相对路径（空列表表示完整）。
    /// </summary>
    public static IReadOnlyList<string> FindMissingInZip(
        string zipPath,
        IEnumerable<string> expectedRelativePaths)
    {
        using var archive = ZipFile.OpenRead(zipPath);

        var present = new HashSet<string>(
            archive.Entries.Select(e => e.FullName.Replace('\\', '/')),
            StringComparer.OrdinalIgnoreCase);

        return expectedRelativePaths
            .Select(path => path.Replace('\\', '/'))
            .Where(path => !present.Contains(path))
            .ToList();
    }
}
