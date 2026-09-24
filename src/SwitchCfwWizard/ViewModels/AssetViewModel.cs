using SwitchCfwWizard.Infrastructure;
using SwitchCfwWizard.Localization;

namespace SwitchCfwWizard.ViewModels;

/// <summary>单个待下载文件（一个进度条）。</summary>
public sealed class AssetViewModel : ObservableObject
{
    private double _progress;
    private string _status = string.Empty;
    private string _detail = string.Empty;
    private bool _isDone;
    private bool _isFailed;
    private bool _isActive;

    public AssetViewModel(string id, string displayName, string repoName, string sizeText)
    {
        Id = id;
        DisplayName = displayName;
        RepoName = repoName;
        SizeText = sizeText;
        _status = LocalizationService.Instance["Common.Waiting"];
    }

    public string Id { get; }

    public string DisplayName { get; }

    public string RepoName { get; }

    public string SizeText { get; }

    public double Progress
    {
        get => _progress;
        set => SetProperty(ref _progress, value);
    }

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    /// <summary>形如 “1.2 MB / 4.7 MB · 320 KB/s · 00:12”。</summary>
    public string Detail
    {
        get => _detail;
        set => SetProperty(ref _detail, value);
    }

    public bool IsDone
    {
        get => _isDone;
        set => SetProperty(ref _isDone, value);
    }

    public bool IsFailed
    {
        get => _isFailed;
        set => SetProperty(ref _isFailed, value);
    }

    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    public void Reset()
    {
        Progress = 0;
        Detail = string.Empty;
        Status = LocalizationService.Instance["Common.Waiting"];
        IsDone = false;
        IsFailed = false;
        IsActive = false;
    }
}
