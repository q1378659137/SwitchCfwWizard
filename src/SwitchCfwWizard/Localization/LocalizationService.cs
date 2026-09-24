using System.IO;
using System.Reflection;
using System.Text.Json;
using SwitchCfwWizard.Infrastructure;

namespace SwitchCfwWizard.Localization;

public sealed record LanguageInfo(string Code, string DisplayName);

/// <summary>
/// 轻量多语言服务。
/// 内置语言以嵌入式 JSON 资源提供（Localization/Strings.&lt;code&gt;.json），
/// 运行期可在 exe 同级 lang/ 目录放置同名文件来覆盖或新增语言。
/// </summary>
public sealed class LocalizationService : ObservableObject
{
    public const string DefaultLanguageCode = "zh-Hans";

    private const string EmbeddedPrefix = "Strings.";
    private const string EmbeddedSuffix = ".json";

    private readonly Dictionary<string, Dictionary<string, string>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<LanguageInfo> _languages = new();
    private Dictionary<string, string> _current = new();

    private LocalizationService()
    {
        DiscoverLanguages();

        var initial = _languages.Any(l => l.Code == DefaultLanguageCode)
            ? DefaultLanguageCode
            : _languages.FirstOrDefault()?.Code ?? DefaultLanguageCode;

        Current = new LanguageInfo(initial, initial);
        Load(initial);
    }

    public static LocalizationService Instance { get; } = new();

    /// <summary>WPF 绑定用别名（Loc.Instance）。</summary>
    public static LocalizationService Loc => Instance;

    public IReadOnlyList<LanguageInfo> Languages => _languages;

    public LanguageInfo Current { get; private set; }

    public event EventHandler? LanguageChanged;

    /// <summary>取翻译文本；缺失时回退到默认语言，再回退到 key 本身。</summary>
    public string this[string key]
    {
        get
        {
            if (string.IsNullOrEmpty(key))
            {
                return string.Empty;
            }

            if (_current.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value))
            {
                return value;
            }

            if (_cache.TryGetValue(DefaultLanguageCode, out var fallback)
                && fallback.TryGetValue(key, out var fallbackValue))
            {
                return fallbackValue;
            }

            return key;
        }
    }

    public string Format(string key, params object?[] args)
    {
        var template = this[key];
        try
        {
            return string.Format(template, args);
        }
        catch (FormatException)
        {
            return template;
        }
    }

    public void SetLanguage(string code)
    {
        if (string.IsNullOrWhiteSpace(code) || string.Equals(code, Current.Code, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Load(code);
        var display = _languages.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? code;
        Current = new LanguageInfo(code, display);

        OnPropertyChanged(nameof(Current));
        // 通知所有 {Binding Path=[Key]} 绑定刷新
        OnPropertyChanged("Item[]");
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Load(string code)
    {
        if (_cache.TryGetValue(code, out var cached))
        {
            _current = cached;
            return;
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        // 1) 嵌入式资源
        var assembly = typeof(LocalizationService).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(EmbeddedPrefix + code + EmbeddedSuffix, StringComparison.OrdinalIgnoreCase));

        if (resourceName is not null)
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is not null)
            {
                MergeJson(map, stream);
            }
        }

        // 2) 外部覆盖（exe 同级 lang/ 目录）
        var external = Path.Combine(AppPaths.LanguageRoot, EmbeddedPrefix + code + EmbeddedSuffix);
        if (File.Exists(external))
        {
            try
            {
                using var stream = File.OpenRead(external);
                MergeJson(map, stream);
            }
            catch (IOException)
            {
                // 外部语言包损坏时忽略，继续使用内置资源
            }
            catch (JsonException)
            {
                // 同上
            }
        }

        _cache[code] = map;
        _current = map;
    }

    private static void MergeJson(Dictionary<string, string> target, Stream stream)
    {
        using var document = JsonDocument.Parse(stream);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Name.StartsWith('_'))
            {
                continue; // _meta 之类的元数据节点
            }

            if (property.Value.ValueKind == JsonValueKind.String)
            {
                target[property.Name] = property.Value.GetString() ?? string.Empty;
            }
        }
    }

    private void DiscoverLanguages()
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 内置
        var assembly = typeof(LocalizationService).Assembly;
        foreach (var name in assembly.GetManifestResourceNames())
        {
            var index = name.IndexOf(EmbeddedPrefix, StringComparison.OrdinalIgnoreCase);
            if (index < 0 || !name.EndsWith(EmbeddedSuffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var code = name.Substring(index + EmbeddedPrefix.Length, name.Length - index - EmbeddedPrefix.Length - EmbeddedSuffix.Length);
            if (code.Length > 0)
            {
                found[code] = ReadDisplayName(name, assembly) ?? code;
            }
        }

        // 外部
        if (Directory.Exists(AppPaths.LanguageRoot))
        {
            foreach (var file in Directory.EnumerateFiles(AppPaths.LanguageRoot, EmbeddedPrefix + "*" + EmbeddedSuffix))
            {
                var fileName = Path.GetFileName(file);
                var code = fileName.Substring(EmbeddedPrefix.Length, fileName.Length - EmbeddedPrefix.Length - EmbeddedSuffix.Length);
                if (code.Length == 0)
                {
                    continue;
                }

                string? displayName = null;
                try
                {
                    using var stream = File.OpenRead(file);
                    using var document = JsonDocument.Parse(stream);
                    displayName = ReadMetaName(document);
                }
                catch (Exception ex) when (ex is IOException or JsonException)
                {
                    // 忽略无法解析的语言文件
                }

                found[code] = displayName ?? found.GetValueOrDefault(code) ?? code;
            }
        }

        _languages.Clear();

        // 简体中文固定排在第一位
        if (found.Remove(DefaultLanguageCode, out var zhName))
        {
            _languages.Add(new LanguageInfo(DefaultLanguageCode, zhName));
        }

        foreach (var pair in found.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            _languages.Add(new LanguageInfo(pair.Key, pair.Value));
        }
    }

    private static string? ReadDisplayName(string resourceName, Assembly assembly)
    {
        try
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                return null;
            }

            using var document = JsonDocument.Parse(stream);
            return ReadMetaName(document);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    private static string? ReadMetaName(JsonDocument document)
    {
        if (document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("_meta", out var meta)
            && meta.ValueKind == JsonValueKind.Object
            && meta.TryGetProperty("name", out var name)
            && name.ValueKind == JsonValueKind.String)
        {
            return name.GetString();
        }

        return null;
    }
}
