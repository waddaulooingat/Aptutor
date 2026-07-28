using ApTutor.Curriculum;

namespace ApTutor.ContentFactory;

public static class PromptTemplates
{
    public static string System(string courseId) => $"""
        You are an expert AP Computer Science A curriculum author working for a test-prep product
        (course id: "{courseId}"). Everything you generate is ORIGINAL content — you must never
        reproduce College Board question text, rubric text, or any other copyrighted exam material.
        Write clear, correct, original content suitable for a paying high-school student.

        Every animated walkthrough you emit is a sequence of scene-primitive operations from a
        FIXED vocabulary (described in the tool schema) — the same primitives the product's live
        code tracer uses, so authored and traced walkthroughs look identical to a student. Keep
        each walkthrough self-contained, deterministic, and pedagogically sequenced: start from an
        empty state and build up one small step at a time, narrating each step's caption exactly as
        you'd say it out loud while teaching.
        """;

    public static string ForNode(DagNode node) => $"""
        Generate practice items, an explanation, and an animated walkthrough for this curriculum node:

        id: {node.Id}
        unit: {node.Unit}
        type: {node.Type}
        title: {node.Title}
        prereqs: {(node.Prereqs.Count == 0 ? "(none)" : string.Join(", ", node.Prereqs))}
        primary visualization: {node.Viz}

        Assume the student has already mastered every listed prereq, but nothing beyond that. The
        walkthrough should center on the "{node.Viz}" primitive named above and directly illustrate
        this node's title.
        """;
}
