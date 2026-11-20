using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;
using Optimizer.Core.Changes;

namespace Optimizer.App.ViewModels;

public sealed class HistoryEntryViewModel
{
    public HistoryEntryViewModel(JournalEntry entry, Func<HistoryEntryViewModel, Task> revert)
    {
        Entry = entry;
        RevertCommand = new AsyncRelayCommand(() => revert(this));
    }

    public JournalEntry Entry { get; }

    public IAsyncRelayCommand RevertCommand { get; }

    public string Title => Entry.Title;
    public string DateText => Entry.CreatedAt.ToString("dd.MM.yyyy HH:mm");
    public bool CanRevert => Entry.CanRevert;
    public IReadOnlyList<string> ChangeLines => Entry.Changes.Select(c => c.Describe()).ToList();
    public IReadOnlyList<string> Notes => Entry.Notes;
    public bool HasNotes => Entry.Notes.Count > 0;

    public string StatusText => Entry.Status switch
    {
        JournalStatus.Applied => "UYGULANDI",
        JournalStatus.Pending => "ONAY BEKLİYOR",
        JournalStatus.Reverted => "GERİ ALINDI",
        JournalStatus.PartiallyReverted => "KISMEN GERİ ALINDI",
        JournalStatus.RolledBack => "BAŞARISIZ, GERİ ALINDI",
        _ => Entry.Status.ToString(),
    };

    public IBrush StatusBrush => Entry.Status switch
    {
        JournalStatus.Applied => FindingViewModel.OkBrush,
        JournalStatus.Pending => FindingViewModel.SuggestionBrush,
        JournalStatus.RolledBack or JournalStatus.PartiallyReverted => FindingViewModel.WarningBrush,
        _ => FindingViewModel.InfoBrush,
    };
}
