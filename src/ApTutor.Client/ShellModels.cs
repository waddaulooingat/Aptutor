using System.ComponentModel;
using System.Runtime.CompilerServices;
using ApTutor.Curriculum;
using Avalonia.Media;

namespace ApTutor.Client;

/// Common shape the left tree binds to, so one TreeDataTemplate covers unit headers and nodes.
public interface ITreeItem
{
    string Display { get; }
    IBrush Foreground { get; }
    IReadOnlyList<ITreeItem> Children { get; }
}

public sealed class UnitGroupVm : ITreeItem
{
    private readonly List<NodeItemVm> _nodes;

    public UnitGroupVm(int unit, string title, List<NodeItemVm> nodes)
    {
        Unit = unit;
        Title = title;
        _nodes = nodes;
    }

    public int Unit { get; }
    public string Title { get; }
    public string Display => $"Unit {Unit}: {Title}";
    public IBrush Foreground => Brushes.Black;
    public IReadOnlyList<ITreeItem> Children => _nodes;
}

/// Available folded straight into the tree (no separate "Available now" panel): mastered nodes get
/// a checkmark, unlocked-but-not-mastered nodes get an arrow, and anything still locked behind a
/// prereq is greyed out — so the "what can I work on next" signal lives in one place, not two.
///
/// INotifyPropertyChanged on purpose: the tree is built ONCE (see MainWindow.BuildTree) and these
/// same instances are updated in place afterward via SetState, rather than the whole ItemsSource
/// being torn down and rebuilt on every mastery change — replacing ItemsSource was collapsing the
/// user's tree expansion back to the top level on every "Mark mastered" click.
public sealed class NodeItemVm : ITreeItem, INotifyPropertyChanged
{
    private bool _mastered;
    private bool _available;

    public NodeItemVm(DagNode node, bool mastered, bool available)
    {
        Node = node;
        _mastered = mastered;
        _available = available;
    }

    public DagNode Node { get; }
    public bool Mastered => _mastered;
    public bool Available => _available;
    public string Display => (Mastered ? "✓ " : Available ? "▶ " : "   ") + Node.Id + " — " + Node.Title;
    public IBrush Foreground => Mastered || Available ? Brushes.Black : Brushes.Gray;
    public IReadOnlyList<ITreeItem> Children => Array.Empty<ITreeItem>();

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetState(bool mastered, bool available)
    {
        if (mastered == _mastered && available == _available) return;
        _mastered = mastered;
        _available = available;
        OnPropertyChanged(nameof(Mastered));
        OnPropertyChanged(nameof(Available));
        OnPropertyChanged(nameof(Display));
        OnPropertyChanged(nameof(Foreground));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
