using System.ComponentModel;
using NetworkIsolator.Models;
using NetworkIsolator.Localization;

namespace NetworkIsolator.ViewModels;

/// <summary>One card of the "My applications" tab: a profile plus its real state in the firewall.</summary>
public sealed class AppEntry : INotifyPropertyChanged
{
    public AppEntry(LaunchProfile profile) => Profile = profile;

    public event PropertyChangedEventHandler? PropertyChanged;

    public LaunchProfile Profile { get; }
    public BlockState State { get; private set; } = BlockState.Unknown;
    public bool IsBusy { get; private set; }

    public string Name => Profile.Name;
    public string ExePath => Profile.ExePath;
    public bool CanInteract => !IsBusy;

    public string BadgeText => State switch
    {
        BlockState.Blocked => Loc.T("Block_State_Blocked"),
        BlockState.Partial => Loc.T("Block_State_Partial"),
        BlockState.Free => Loc.T("Block_State_Free"),
        _ => Loc.T("Block_State_Unknown")
    };

    public string LockButtonText => State is BlockState.Blocked or BlockState.Partial
        ? Loc.T("Block_Btn_Unblock")
        : Loc.T("Block_Btn_Block");

    public string Details
    {
        get
        {
            int extra = Profile.ExtraExePaths.Count;
            return extra > 0
                ? Loc.T("Block_Extra", Profile.Summary, extra)
                : Profile.Summary;
        }
    }

    public void SetState(BlockState state)
    {
        State = state;
        Notify(nameof(State), nameof(BadgeText), nameof(LockButtonText));
    }

    /// <summary>Called when the interface language changes: texts built in code must be read again.</summary>
    public void RefreshTexts() => Notify(nameof(BadgeText), nameof(LockButtonText), nameof(Details));

    public void SetBusy(bool busy)
    {
        IsBusy = busy;
        Notify(nameof(IsBusy), nameof(CanInteract));
    }

    private void Notify(params string[] names)
    {
        foreach (string n in names)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
