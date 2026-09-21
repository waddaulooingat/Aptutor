// Advanced Test Prepper — generic static line/point graph-spec (see the graph-spec-rendering plan).
// Triggered by real AP Physics practice questions where multiple-choice options are themselves
// graphs (e.g. velocity-vs-time plots with solid/dashed segments and labeled reference values), not
// text. Chemistry is expected to need the same capability (titration curves, reaction-rate curves).
//
// Deliberately scoped to line/point graphs only — axes, line segments, labeled reference values.
// Does NOT cover molecular structure diagrams (Lewis structures, reaction schemes); that's a
// genuinely different visual grammar and a separate, later project. Do not extend this schema to
// try to cover that.
//
// This is a pure data shape with no rendering logic in it — the actual draw code is two separate,
// thin adapters (an Avalonia control in the Shell, an inline-SVG Razor helper in Content Admin) each
// built on top of a shared layout/geometry library, since a desktop app and a server-rendered web
// page can't literally share draw calls. See the plan's investigation note on why. Lives in
// ApTutor.Platform (alongside PracticeItem, which now references it) since both the Shell and
// Content Admin already depend on this project.

namespace ApTutor.Platform;

/// One axis of a graph. Min/Max are optional — a renderer can auto-scale from the segments' own
/// points when they're absent, which also means this schema doesn't need updating just because a
/// future renderer adds auto-scaling.
public sealed record GraphAxis(
    string Label,
    string? Unit = null,
    double? Min = null,
    double? Max = null);

public sealed record GraphPoint(double X, double Y);

/// One drawn line: a sequence of points connected in order — two points for a straight segment of
/// constant slope, more for a piecewise or curved line. A graph with multiple curves on it is
/// already just multiple GraphSegment entries; no separate "curve" concept is needed for that.
/// Solid/Label are about HOW to draw the line, not what it physically means — the generation layer
/// (see the plan's Part 2) decides what solid vs. dashed represents for a given question; this
/// schema only records which style to render.
public sealed record GraphSegment(
    IReadOnlyList<GraphPoint> Points,
    bool Solid = true,
    string? Label = null);

public enum GraphAxisKind { X, Y }

/// A labeled reference value on one axis (e.g. a tick mark labeled "v_t") — a labeled tick/guide at
/// that value, not a full segment.
public sealed record GraphReferenceValue(GraphAxisKind Axis, double Value, string Label);

/// A complete static graph. Extend by adding new optional fields (e.g. shaded regions) rather than
/// changing what's here — every field beyond XAxis/YAxis/Segments is nullable/defaulted specifically
/// so an older renderer that doesn't know about a newer field still draws everything it does
/// understand, instead of failing to parse a newer graph entirely.
public sealed record GraphSpec(
    GraphAxis XAxis,
    GraphAxis YAxis,
    IReadOnlyList<GraphSegment> Segments,
    IReadOnlyList<GraphReferenceValue>? ReferenceValues = null);
