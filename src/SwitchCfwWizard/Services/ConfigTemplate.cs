using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SwitchCfwWizard.Infrastructure;
using SwitchCfwWizard.Localization;

namespace SwitchCfwWizard.Services;

/// <summary>
/// 一份「配置模板」。
///
/// 两种来源：
/// <list type="bullet">
///   <item><b>内嵌</b>（<see cref="IsBuiltIn"/>）：<c>play</c> / <c>study</c> 两份编进程序，不往 exe 旁边放文件
///         （用户 2026-11-21 要求「不单独放出」）。显示名走语言包：<c>Template.Play</c> / <c>Template.Study</c>，
///         所以切到繁体/英文时下拉框里的名字也跟着变 —— 这也是 <see cref="Name"/> 要实现
///         <see cref="INotifyPropertyChanged"/> 的原因。</item>
///   <item><b>导入的</b>：用户点「导入模板…」选中的 json，复制进 exe 同级的 <c>templates/</c> 目录，
///         显示名 = 文件名。</item>
/// </list>
/// </summary>
public sealed class ConfigTemplate : INotifyPropertyChanged
{
    private string _name = string.Empty;

    /// <summary>稳定标识：内嵌的是 <c>play</c> / <c>study</c>，导入的是文件名（不含扩展名）。</summary>
    public required string Id { get; init; }

    /// <summary>界面上显示的名字（内嵌的来自语言包，可随语言变化）。</summary>
    public string Name
    {
        get => _name;
        set
        {
            if (_name == value)
            {
                return;
            }

            _name = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
        }
    }

    /// <summary>磁盘模板的完整路径；内嵌模板为 <c>null</c>。</summary>
    public string? FilePath { get; init; }

    /// <summary>内嵌资源名；磁盘模板为 <c>null</c>。</summary>
    public string? ResourceName { get; init; }

    public bool IsBuiltIn => ResourceName is not null;

    public event PropertyChangedEventHandler? PropertyChanged;

    public override string ToString() => Name;
}

/// <summary>
/// 应用一份模板后「实际改到了多少」的账单：界面拿它写一句提示，
/// 日志拿它记一行，回归直接对它下断言。
/// </summary>
/// <param name="Components">真正改到勾选的组件数。</param>
/// <param name="Options">真正写进去的选项数。</param>
/// <param name="Settings">其它设置项（工作台开关、引导项标题、地址、四种屏蔽开关…）写入的条数。</param>
/// <param name="MissingComponents">模板里有、界面上没有的组件名。</param>
/// <param name="MissingOptions">模板里有、界面上没有的选项键。</param>
public sealed record TemplateApplyReport(
    int Components,
    int Options,
    int Settings,
    IReadOnlyList<string> MissingComponents,
    IReadOnlyList<string> MissingOptions);

/// <summary>
/// 模板的发现、导入与解析。文件操作 + 纯解析，回归可以直接调。
///
/// 模板的**格式就是 <c>settings.json</c>**：用户 2026-11-21 要求「有配置模板的时候把 json 的所有信息
/// 全部导入」，所以这里返回的是一整份 <see cref="AppSettings"/>，不再挑字段。
/// </summary>
public static class TemplateStore
{
    /// <summary>内嵌模板：<c>id</c> → 资源名。加一份新模板就在这儿加一行 + csproj 加一条 + 语言包加两个键。</summary>
    private static readonly (string Id, string Resource)[] BuiltIns =
    [
        ("play", "SwitchCfwWizard.Templates.play.json"),
        ("study", "SwitchCfwWizard.Templates.study.json"),
    ];

    /// <summary>导入的模板落在 exe 同级的 <c>templates/</c>（只有导入这个动作才会创建它）。</summary>
    public static string Folder => AppPaths.TemplateRoot;

    /// <summary>
    /// 模板的解析选项：比 <c>settings.json</c> 宽容一点 —— 允许注释与尾逗号、键名大小写不敏感。
    /// 模板是给人看、给人改的，为了一个尾逗号报错只会让人以为程序坏了。
    /// </summary>
    private static readonly JsonSerializerOptions TemplateJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        // 手写模板时 `"bootwait": 5`（数字）和 `"bootwait": "5"`（字符串）都该收 ——
        // 存档里全是字符串，但人写 json 时很自然就写成数字。
        Converters = { new LenientStringConverter(), new LenientInt32Converter() },
    };

    /// <summary>
    /// 能用的模板：先内嵌的两份，再 <c>templates/</c> 里用户导入的（按名字排序）。
    /// 目录不存在就只返回内嵌的 —— 它可能**从来没被创建过**（没人导入过东西）。
    /// </summary>
    public static IReadOnlyList<ConfigTemplate> List()
    {
        var list = new List<ConfigTemplate>();

        foreach (var (id, resource) in BuiltIns)
        {
            list.Add(new ConfigTemplate
            {
                Id = id,
                Name = DisplayName(id),
                ResourceName = resource,
            });
        }

        try
        {
            if (Directory.Exists(Folder))
            {
                list.AddRange(Directory
                    .EnumerateFiles(Folder, "*.json", SearchOption.TopDirectoryOnly)
                    .Select(path =>
                    {
                        var id = Path.GetFileNameWithoutExtension(path);
                        return new ConfigTemplate
                        {
                            Id = id,
                            Name = DisplayName(id),
                            FilePath = path,
                        };
                    })
                    .OrderBy(t => t.Name, StringComparer.CurrentCulture));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 目录读不了就当没有 —— 内嵌的两份仍然可用，不该因为一个权限问题整块卡片变空。
        }

        return list;
    }

    /// <summary>
    /// 内嵌模板的显示名取自哪个语言键；不是内嵌的返回 <c>null</c>。
    ///
    /// 刻意写成 <c>switch</c> + **字面量**，而不是 <c>"Template." + id</c> 拼出来：
    /// 语言键检查器按源码里的字面量对账，拼出来的键会被判成「没人引用」的死键，
    /// 而那种报告会盖住真正该修的漏翻译。
    /// </summary>
    private static string? BuiltInNameKey(string id) => id.ToLowerInvariant() switch
    {
        "play" => "Template.Play",
        "study" => "Template.Study",
        _ => null,
    };

    /// <summary>
    /// 模板的显示名：内嵌的走语言包（<c>Template.Play</c> / <c>Template.Study</c>），
    /// 导入的直接用文件名。
    /// </summary>
    public static string DisplayName(string id)
    {
        var key = BuiltInNameKey(id);
        return key is null ? id : LocalizationService.Instance[key];
    }

    /// <summary>把模板内容读成文本（内嵌的从资源流读，磁盘的直接读文件）。</summary>
    public static string ReadText(ConfigTemplate template)
    {
        if (template.ResourceName is { } resource)
        {
            using var stream = typeof(TemplateStore).Assembly.GetManifestResourceStream(resource)
                               ?? throw new IOException($"内置模板缺失：{resource}");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        if (template.FilePath is { } path)
        {
            return File.ReadAllText(path);
        }

        throw new IOException("这个模板既没有内嵌资源也没有文件路径。");
    }

    /// <summary>
    /// 解析成**一整份** <see cref="AppSettings"/>（不挑字段）。
    ///
    /// <see cref="SettingsStore.Normalize"/> 会补齐 null 的字符串/字典字段并做旧字段迁移 ——
    /// 走的是与读 <c>settings.json</c> 完全相同的收尾，所以「模板能读」与「存档能读」是同一套规矩。
    /// </summary>
    public static AppSettings Load(ConfigTemplate template) => Parse(ReadText(template));

    /// <summary>从一段 JSON 文本解析（回归用它塞内联样本）。</summary>
    public static AppSettings Parse(string json)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(json, TemplateJsonOptions)
                       ?? throw new InvalidDataException("模板内容为空。");

        return SettingsStore.Normalize(settings);
    }

    /// <summary>
    /// 把用户选的一个 <c>.json</c> 复制进 <c>templates/</c>，返回导入后的模板。
    ///
    /// 重名（**含与内嵌模板同名**）时加 <c>-2</c>、<c>-3</c> 后缀而不是覆盖：
    /// 用户导入自己的「学习.json」很可能就是想留两份对比。
    /// </summary>
    public static ConfigTemplate Import(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            throw new IOException("找不到要导入的文件。");
        }

        // 先解析一遍：选错文件（比如一张图片）时宁可在导入前报错，
        // 也别把它塞进目录、等用户点选时才炸。
        Parse(File.ReadAllText(sourcePath));

        Directory.CreateDirectory(Folder);

        var name = Path.GetFileNameWithoutExtension(sourcePath);
        var extension = Path.GetExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".json";
        }

        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var builtIn in BuiltIns)
        {
            taken.Add(builtIn.Id);
            // 显示名也占着位置：用户导入「学习.json」时与内嵌那份显示名撞车，同样要改名。
            if (BuiltInNameKey(builtIn.Id) is { } nameKey)
            {
                taken.Add(LocalizationService.Instance[nameKey]);
            }
        }

        foreach (var existing in Directory.EnumerateFiles(Folder, "*.json", SearchOption.TopDirectoryOnly))
        {
            taken.Add(Path.GetFileNameWithoutExtension(existing));
        }

        var id = name;
        for (var index = 2; taken.Contains(id); index++)
        {
            id = $"{name}-{index}";
        }

        var target = Path.Combine(Folder, id + extension);
        File.Copy(sourcePath, target);

        return new ConfigTemplate
        {
            Id = id,
            Name = DisplayName(id),
            FilePath = target,
        };
    }
}

/// <summary>
/// 把 <c>5</c> / <c>true</c> 这类非字符串 token 也读成字符串。
///
/// 为什么需要：模板里的选项值在存档中统统是字符串，但人手写 json 时会写成 <c>"bootwait": 5</c>，
/// 默认的转换器会直接抛「The JSON value could not be converted to System.String」。
/// 这条只挂在模板的解析选项上，**不影响** settings.json 的读写。
/// </summary>
internal sealed class LenientStringConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => Encoding.UTF8.GetString(reader.ValueSpan),
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",
            JsonTokenType.Null => null,
            _ => throw new JsonException($"模板里的这个值不是字符串，也不是数字/布尔：{reader.TokenType}"),
        };

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        => writer.WriteStringValue(value);
}

/// <summary>字符串形式的整数（<c>"90"</c>）也要能读进 <c>int</c> 字段 —— 与上一条同源。</summary>
internal sealed class LenientInt32Converter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var number))
        {
            return number;
        }

        if (reader.TokenType == JsonTokenType.String
            && int.TryParse(reader.GetString(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        throw new JsonException("这个值既不是整数，也不是能转成整数的字符串。");
    }

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value);
}
