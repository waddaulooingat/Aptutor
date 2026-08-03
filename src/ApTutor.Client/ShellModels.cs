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
public sealed class NodeItemVm : ITreeItem
{
    public NodeItemVm(DagNode node, bool mastered, bool available)
    {
        Node = node;
        Mastered = mastered;
        Available = available;
    }

    public DagNode Node { get; }
    public bool Mastered { get; }
    public bool Available { get; }
    public string Display => (Mastered ? "✓ " : Available ? "▶ " : "   ") + Node.Id + " — " + Node.Title;
    public IBrush Foreground => Mastered || Available ? Brushes.Black : Brushes.Gray;
    public IReadOnlyList<ITreeItem> Children => Array.Empty<ITreeItem>();
}
