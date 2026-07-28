using ApTutor.Curriculum;

namespace ApTutor.Client;

/// Common shape the left tree binds to, so one TreeDataTemplate covers unit headers and nodes.
public interface ITreeItem
{
    string Display { get; }
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
    public IReadOnlyList<ITreeItem> Children => _nodes;
}

public sealed class NodeItemVm : ITreeItem
{
    public NodeItemVm(DagNode node, bool mastered)
    {
        Node = node;
        Mastered = mastered;
    }

    public DagNode Node { get; }
    public bool Mastered { get; }
    public string Display => (Mastered ? "✓ " : "") + Node.Id + " — " + Node.Title;
    public IReadOnlyList<ITreeItem> Children => Array.Empty<ITreeItem>();
}
