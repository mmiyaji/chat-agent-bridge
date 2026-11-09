using ChatAgentBridge.Core;
using ChatAgentBridge_Desktop.Localization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChatAgentBridge_Desktop.ViewModels;

public partial class MainPageViewModel
{
    private UpdateNoticeStore UpdateNoticeStore => new(_settingsStore.DirectoryPath);

    [ObservableProperty, NotifyPropertyChangedFor(nameof(UpdateNoticeTitle))]
    public partial string UpdateNoticeVersion { get; set; } = "";

    [ObservableProperty]
    public partial bool IsUpdateNoticeOpen { get; set; }

    public string UpdateNoticeTitle => ViewModelText.Get("UpdateNoticeTitle", UpdateNoticeVersion);

    public async Task InitializeUpdateNoticeAsync(string appVersion)
    {
        UpdateNoticeVersion = appVersion;
        try
        {
            IsUpdateNoticeOpen = await UpdateNoticeStore.ShouldShowAsync(appVersion, _hasSavedSettings);
        }
        catch
        {
            IsUpdateNoticeOpen = false;
        }
    }

    public async Task DismissUpdateNoticeAsync()
    {
        IsUpdateNoticeOpen = false;
        try { await UpdateNoticeStore.DismissAsync(UpdateNoticeVersion); }
        catch { /* A notice dismissal must not interfere with normal app use. */ }
    }
}
