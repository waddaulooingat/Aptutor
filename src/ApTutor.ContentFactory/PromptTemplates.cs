using ApTutor.Content;
using ApTutor.Curriculum;

namespace ApTutor.ContentFactory;

public static class PromptTemplates
{
    public static string System(string courseId) => $"""
        You are an expert curriculum author working for a test-prep product (course id: "{courseId}").
        Everything you generate is ORIGINAL content — you must never reproduce College Board question
        text, rubric text, or any other copyrighted exam material. Write clear, correct, original
        content suitable for a paying high-school student.

        Every animated walkthrough you emit is a sequence of scene-primitive operations from a
        FIXED vocabulary (described in the tool schema) — the same primitives the product's live
        code tracer uses, so authored and traced walkthroughs look identical to a student. Keep
        each walkthrough self-contained, deterministic, and pedagogically sequenced: start from an
        empty state and build up one small step at a time, narrating each step's caption exactly as
        you'd say it out loud while teaching.
        """;

    public static string ForNode(DagNode node, Difficulty difficulty, bool allowGraphChoices) => $"""
        Generate practice items, an explanation, and an animated walkthrough for this curriculum node:

        {NodeSummary(node)}

        Assume the student has already mastered every listed prereq, but nothing beyond that. The
        walkthrough should center on the "{node.Viz}" primitive named above and directly illustrate
        this node's title.

        Target difficulty for the practice items: {DifficultyGuidance(difficulty)}
        {(allowGraphChoices ? GraphChoiceGuidance : "")}
        """;

    /// Only appended when the SME has explicitly opted into graph-based answer choices for this
    /// generation (see the graph-spec-rendering plan's Part 2) — real AP Physics/Chemistry-style
    /// questions where an option is itself a graph (e.g. four lettered velocity-time plots), not
    /// text. Kept out of the prompt entirely otherwise, so ordinary text-only generation (every
    /// course/node that hasn't opted in) is unaffected.
    private const string GraphChoiceGuidance = """
        This node may call for GRAPH-BASED answer choices instead of (or alongside) plain text — use
        your judgment on whether this specific question actually needs a graph, and only use one
        when it genuinely does. A graph choice must state real axis labels and units matching the
        problem's physical quantities (e.g. "Velocity (relative to the ground)" / "Time (s)"), and
        its line segments' slope/values must actually be consistent with the choice they represent —
        an incorrect choice should be graphically wrong in a way a student who misunderstood the
        concept could plausibly draw, not an arbitrary scribble. Use the solid-vs-dashed distinction
        deliberately (e.g. two different objects, or before-vs-after some event) — never just for
        visual variety. A question's four choices can all be graphs, all be text, or mix, whichever
        the question actually calls for.
        """;

    /// Difficulty only shapes how demanding the practice items are (see the difficulty-levels plan)
    /// — the walkthrough/explanation itself stays a clear, correct teaching of the concept regardless
    /// of difficulty, since a harder walkthrough would just make a "hard" node illegible rather than
    /// more rigorous.
    private static string DifficultyGuidance(Difficulty difficulty) => difficulty switch
    {
        Difficulty.Easy =>
            "EASY. Straightforward, single-step recall or direct application of the concept just " +
            "explained. The correct answer should be clear to a student who understood the " +
            "walkthrough; distractors should be plausible but not tricky.",
        Difficulty.Hard =>
            "HARD. Multi-step reasoning, edge cases, or combining this concept with an earlier " +
            "prereq. Distractors should reflect common misconceptions a student could plausibly " +
            "believe, not just be obviously wrong.",
        _ =>
            "MEDIUM. A step beyond pure recall — requires applying the concept to a situation that " +
            "isn't identical to the walkthrough's example, but doesn't require combining multiple " +
            "concepts.",
    };

    /// Content Admin's Learn-mode teaching content (see the learn-quiz-mode-switch plan's Part B) —
    /// generated and reviewed alongside practice items, never replacing them. Deliberately one
    /// generic prompt for every subject rather than a Physics-vs-English branch: the "steps" concept
    /// is described flexibly enough (a worked-example step OR a structured section, whichever suits
    /// the node) that the model adapts naturally from the node/course context already given, the
    /// same way practice-item generation already adapts its own wording per course today.
    public static string ForLearnContent(DagNode node) => $"""
        Write teaching content for a student encountering this curriculum node for the first time,
        BEFORE they attempt any practice questions on it:

        {NodeSummary(node)}

        Assume the student has already mastered every listed prereq, but nothing beyond that.

        Write an overview (1-3 sentences framing what this node covers and why it matters) followed
        by an ordered sequence of steps building understanding one piece at a time. Judge from the
        node's subject matter which shape best serves a step: for a Math/Physics-style node, each
        step should be a worked-example step (a concrete action or calculation, with the reasoning or
        equation behind it); for an English/History-style node, each step should be a short section
        (a subheading-style caption with an explanatory paragraph, including a concrete illustrative
        example). Do not just restate the practice items' explanations — this is the teaching moment
        that comes before a student ever sees a question, not a recap after one.
        """;

    /// Content Admin's "Create new course" (see the Shell-display-only/course-authoring plan's
    /// Part C) — the top-level entry point, one level above "Generate unit structure": drafts only
    /// a course's unit list (its table of contents), not any unit's node structure.
    public static string ForCourse(string courseName, string? guidance) => $"""
        Draft the unit list (table of contents) for a new course:

        course: {courseName}
        {(string.IsNullOrWhiteSpace(guidance) ? "" : $"additional guidance from the reviewer: {guidance}\n")}
        List the units in the order a student should take them, numbered starting at 1. Give each a
        short, clear title describing its scope. Do not generate any topics/nodes within a unit —
        only the unit list itself, nothing more.
        """;

    /// Content Admin's "Generate unit structure" (one level above per-node content generation —
    /// see the Shell-display-only/course-authoring plan's Part B). existingNodes gives the model
    /// real node ids it can reference in prereqs; it's never trusted to invent cross-unit prereqs
    /// out of nothing.
    public static string ForUnit(int unit, string unitTitle, IReadOnlyList<DagNode> existingNodes, string? guidance) => $"""
        Draft the node list for a new curriculum unit within this course:

        unit number: {unit}
        unit title: {unitTitle}
        {(string.IsNullOrWhiteSpace(guidance) ? "" : $"additional guidance from the reviewer: {guidance}\n")}
        Existing nodes already in this course, for prereq reference only — do not repeat or modify
        any of these; only generate NEW nodes for the unit above:
        {ExistingNodesSummary(existingNodes)}

        Each new node's id must start with "u{unit}." followed by a number (e.g. "u{unit}.1",
        "u{unit}.2", ...), in the order a student should learn them. A node's prereqs may reference
        any of the existing node ids listed above, or any earlier node id you generate in this same
        unit — never a node id that doesn't exist and never itself. Order your nodes so every prereq
        you reference already appears either in the existing list above or earlier in your own output.
        """;

    private static string ExistingNodesSummary(IReadOnlyList<DagNode> nodes) =>
        nodes.Count == 0 ? "(none yet)" : string.Join("\n", nodes.Select(n => $"{n.Id}: {n.Title}"));

    private static string NodeSummary(DagNode node) => $"""
        id: {node.Id}
        unit: {node.Unit}
        type: {node.Type}
        title: {node.Title}
        prereqs: {(node.Prereqs.Count == 0 ? "(none)" : string.Join(", ", node.Prereqs))}
        primary visualization: {node.Viz}
        """;
}
