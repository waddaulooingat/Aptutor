// AP Tutor — Phase 1: Curriculum DAG loader.
// Target: .NET 8, C# 12, nullable enabled. No external deps (System.Text.Json only).
// Loads apcsa-skill-dag.json, validates it's a well-formed DAG, and exposes
// topological order + prerequisite-gated unlocking for the mastery tracker.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace ApTutor.Curriculum;

// ---------- Data model (mirrors apcsa-skill-dag.json) ----------

public sealed record SkillDag(
    [property: JsonPropertyName("meta")] DagMeta Meta,
    [property: JsonPropertyName("scenePrimitives")] IReadOnlyDictionary<string, string> ScenePrimitives,
    [property: JsonPropertyName("units")] IReadOnlyList<UnitInfo> Units,
    [property: JsonPropertyName("nodes")] IReadOnlyList<DagNode> Nodes);

public sealed record DagMeta(
    [property: JsonPropertyName("course")] string Course,
    [property: JsonPropertyName("nodeCount")] int NodeCount);

public sealed record UnitInfo(
    [property: JsonPropertyName("unit")] int Unit,
    [property: JsonPropertyName("title")] string Title);

public enum NodeType { Concept, Skill, Synthesis, Frq }

public sealed record DagNode(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("unit")] int Unit,
    [property: JsonPropertyName("type")] NodeType Type,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("prereqs")] IReadOnlyList<string> Prereqs,
    [property: JsonPropertyName("viz")] string Viz);

// ---------- Loader ----------

public static class SkillDagLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static SkillGraph Load(string path) => LoadFromJson(File.ReadAllText(path));

    /// Split out of Load so a DAG downloaded from S3 (see the Shell-display-only/course-authoring
    /// plan) can be validated the exact same way as one read from a local file, without needing to
    /// stage the bytes to disk first just to satisfy a file-path-only API.
    public static SkillGraph LoadFromJson(string json)
    {
        var dag = JsonSerializer.Deserialize<SkillDag>(json, Options)
                  ?? throw new InvalidDataException("Skill DAG deserialized to null.");
        return new SkillGraph(dag); // ctor validates
    }
}

// ---------- Runtime graph ----------

public sealed class SkillGraph
{
    private readonly Dictionary<string, DagNode> _byId;
    private readonly Dictionary<string, List<string>> _dependents; // id -> nodes that require it
    private readonly List<string> _topoOrder;

    public SkillDag Dag { get; }
    public IReadOnlyList<string> TopologicalOrder => _topoOrder;

    public SkillGraph(SkillDag dag)
    {
        Dag = dag;
        _byId = new Dictionary<string, DagNode>(dag.Nodes.Count);
        foreach (var n in dag.Nodes)
        {
            if (!_byId.TryAdd(n.Id, n))
                throw new InvalidDataException($"Duplicate node id: {n.Id}");
        }

        // Every prereq must resolve to a real node.
        foreach (var n in dag.Nodes)
            foreach (var p in n.Prereqs)
                if (!_byId.ContainsKey(p))
                    throw new InvalidDataException($"Node '{n.Id}' references unknown prereq '{p}'.");

        _dependents = dag.Nodes.ToDictionary(n => n.Id, _ => new List<string>());
        foreach (var n in dag.Nodes)
            foreach (var p in n.Prereqs)
                _dependents[p].Add(n.Id);

        _topoOrder = TopoSortOrThrow(dag.Nodes);
    }

    public DagNode Node(string id) =>
        _byId.TryGetValue(id, out var n) ? n : throw new KeyNotFoundException(id);

    public bool Exists(string id) => _byId.ContainsKey(id);

    public IReadOnlyList<string> Dependents(string id) => _dependents[id];

    /// Nodes whose prereqs are all mastered and which aren't mastered yet.
    public IEnumerable<DagNode> Unlocked(IReadOnlySet<string> mastered) =>
        _topoOrder
            .Where(id => !mastered.Contains(id))
            .Where(id => Node(id).Prereqs.All(mastered.Contains))
            .Select(Node);

    // Kahn's algorithm; leftover nodes => cycle (not a DAG).
    private static List<string> TopoSortOrThrow(IReadOnlyList<DagNode> nodes)
    {
        var indeg = nodes.ToDictionary(n => n.Id, n => n.Prereqs.Count);
        var adj = nodes.ToDictionary(n => n.Id, _ => new List<string>());
        foreach (var n in nodes)
            foreach (var p in n.Prereqs)
                adj[p].Add(n.Id);

        // Deterministic order: unit then id, so learning order is stable across runs.
        var ready = new SortedSet<string>(
            indeg.Where(kv => kv.Value == 0).Select(kv => kv.Key),
            StringComparer.Ordinal);

        var order = new List<string>(nodes.Count);
        while (ready.Count > 0)
        {
            var id = ready.Min!;
            ready.Remove(id);
            order.Add(id);
            foreach (var d in adj[id])
                if (--indeg[d] == 0)
                    ready.Add(d);
        }

        if (order.Count != nodes.Count)
        {
            var stuck = string.Join(", ", indeg.Where(kv => kv.Value > 0).Select(kv => kv.Key));
            throw new InvalidDataException($"Skill DAG contains a cycle. Unresolved nodes: {stuck}");
        }
        return order;
    }
}

// ---------- Mastery tracker ----------

public sealed class MasteryTracker
{
    private readonly SkillGraph _graph;
    private readonly HashSet<string> _mastered = new(StringComparer.Ordinal);

    public MasteryTracker(SkillGraph graph) => _graph = graph;

    public IReadOnlyCollection<string> Mastered => _mastered;
    public bool IsMastered(string id) => _mastered.Contains(id);

    public void MarkMastered(string id)
    {
        if (!_graph.Exists(id)) throw new KeyNotFoundException(id);
        _mastered.Add(id);
    }

    /// A missed mock-exam question on this node's material means the student isn't solid on it
    /// after all — un-master it so it reappears in Available() for review, even if it was
    /// mastered before. Intentionally local: dependents that are already mastered stay mastered
    /// (this flags one weak node for review, it doesn't invalidate everything built on top of it).
    public void MarkWeak(string id)
    {
        if (!_graph.Exists(id)) throw new KeyNotFoundException(id);
        _mastered.Remove(id);
    }

    /// Frontier the student can work on right now.
    public IReadOnlyList<DagNode> Available() =>
        _graph.Unlocked(_mastered).ToList();

    /// Next N recommended nodes in learning order.
    public IReadOnlyList<DagNode> NextRecommended(int count) =>
        Available().Take(count).ToList();

    public double PercentComplete =>
        _graph.Dag.Nodes.Count == 0 ? 0 : (double)_mastered.Count / _graph.Dag.Nodes.Count;
}
