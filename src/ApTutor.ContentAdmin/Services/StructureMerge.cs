using ApTutor.Curriculum;

namespace ApTutor.ContentAdmin.Services;

/// Pure merge logic for course structure — mirrors ManifestMerge's shape (pure, zero I/O;
/// S3ContentStore does the actual read-merge-write around content, and here the caller does the
/// merge itself before calling ApproveStructureAsync, since the merge needs to know the FULL
/// existing SkillDag, not just a manifest pointer). Replaces every existing node in the target unit
/// with the newly-approved set and adds/updates that unit's title, leaving every other unit's nodes
/// and title untouched.
public static class StructureMerge
{
    public static SkillDag UpsertUnit(SkillDag current, int unit, string unitTitle, IReadOnlyList<DagNode> nodes)
    {
        var units = current.Units
            .Where(u => u.Unit != unit)
            .Append(new UnitInfo(unit, unitTitle))
            .OrderBy(u => u.Unit)
            .ToList();

        var mergedNodes = current.Nodes
            .Where(n => n.Unit != unit)
            .Concat(nodes)
            .OrderBy(n => n.Unit)
            .ThenBy(n => n.Id, StringComparer.Ordinal)
            .ToList();

        return current with
        {
            Units = units,
            Nodes = mergedNodes,
            Meta = current.Meta with { NodeCount = mergedNodes.Count },
        };
    }

    /// A brand-new course (or one that's never had a structure approved yet) has nothing to merge
    /// into — this is the starting point UpsertUnit is called against in that case.
    public static SkillDag EmptyStructure(string courseId, string displayName) => new(
        Meta: new DagMeta(Course: displayName, NodeCount: 0, DisplayName: displayName),
        ScenePrimitives: new Dictionary<string, string>(),
        Units: Array.Empty<UnitInfo>(),
        Nodes: Array.Empty<DagNode>());
}
