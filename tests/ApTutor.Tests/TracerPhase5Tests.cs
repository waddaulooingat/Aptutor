using ApTutor.Scene;
using ApTutor.Tracer;
using Xunit;

namespace ApTutor.Tests;

// Phase 5: tracer/validator extension to Units 6-10 (1D/2D arrays, ArrayList<E>, single
// inheritance, recursion). Scoped per the phase-5 kickoff decision: primitives + tracer
// extension only (no verified item bank, no FRQ sandbox).
public class TracerPhase5Tests
{
    private static SceneState ApplyAll(TraceResult result)
    {
        var state = new SceneState();
        foreach (var step in result.Steps)
        {
            state.ClearFlashes();
            state.Apply(step.Delta);
        }
        return state;
    }

    [Fact]
    public void Trace_OneDArray_AllocatesWritesAndReads()
    {
        const string source = """
            class Demo {
                public static void main(String[] args) {
                    int[] nums = new int[3];
                    nums[1] = 99;
                    int x = nums[1];
                    int len = nums.length;
                }
            }
            """;

        var result = new Tracer.Tracer().Trace(source);
        Assert.True(result.Completed, result.HaltReason);

        var allOps = result.Steps.SelectMany(s => s.Delta.Ops).ToList();
        var alloc = Assert.Single(allOps.OfType<ArrayAlloc>());
        Assert.Equal(3, alloc.InitialValues.Count);

        var write = Assert.Single(allOps.OfType<ArrayWrite>());
        Assert.Equal(1, write.Index);
        Assert.Equal("99", write.Value);

        var state = ApplyAll(result);
        var frame = Assert.Single(state.Frames);
        Assert.Equal("99", frame.Cell("x")!.Value);
        Assert.Equal("3", frame.Cell("len")!.Value);

        var arr = Assert.Single(state.Arrays.Values);
        Assert.Equal("99", arr.Values[1]);
    }

    [Fact]
    public void Trace_TwoDArray_AllocatesWritesAndReads()
    {
        const string source = """
            class Demo {
                public static void main(String[] args) {
                    int[][] grid = new int[2][3];
                    grid[1][2] = 7;
                    int v = grid[1][2];
                }
            }
            """;

        var result = new Tracer.Tracer().Trace(source);
        Assert.True(result.Completed, result.HaltReason);

        var allOps = result.Steps.SelectMany(s => s.Delta.Ops).ToList();
        var alloc = Assert.Single(allOps.OfType<Grid2dAlloc>());
        Assert.Equal(2, alloc.Rows);
        Assert.Equal(3, alloc.Cols);

        var write = Assert.Single(allOps.OfType<Grid2dWrite>());
        Assert.Equal(1, write.Row);
        Assert.Equal(2, write.Col);
        Assert.Equal("7", write.Value);

        var state = ApplyAll(result);
        var frame = Assert.Single(state.Frames);
        Assert.Equal("7", frame.Cell("v")!.Value);

        var grid = Assert.Single(state.Grids.Values);
        Assert.Equal("7", grid[1, 2]);
    }

    [Fact]
    public void Trace_ArrayList_AddGetSetSizeAllWorkAndAppendGrowsTheStrip()
    {
        const string source = """
            class Demo {
                public static void main(String[] args) {
                    ArrayList<Integer> list = new ArrayList<>();
                    list.add(5);
                    list.add(10);
                    int first = list.get(0);
                    list.set(0, 99);
                    int n = list.size();
                }
            }
            """;

        var result = new Tracer.Tracer().Trace(source);
        Assert.True(result.Completed, result.HaltReason);

        var state = ApplyAll(result);
        var frame = Assert.Single(state.Frames);
        Assert.Equal("5", frame.Cell("first")!.Value);
        Assert.Equal("2", frame.Cell("n")!.Value);

        var list = Assert.Single(state.Arrays.Values);
        Assert.Equal(2, list.Values.Count);
        Assert.Equal("99", list.Values[0]); // set(0, 99) overwrote the appended add(5)
        Assert.Equal("10", list.Values[1]);
    }

    // Inheritance + dynamic dispatch: d.speak() must resolve to Dog's override (starts the
    // method search at the receiver's runtime class), and super(name) must chain the inherited
    // field into place during construction.
    [Fact]
    public void Trace_SingleInheritance_ConstructorChainsAndOverrideDispatchesDynamically()
    {
        const string source = """
            class Animal {
                String name;
                Animal(String name) {
                    this.name = name;
                }
                String speak() {
                    return name + " makes a sound";
                }
            }
            class Dog extends Animal {
                Dog(String name) {
                    super(name);
                }
                String speak() {
                    return name + " says Woof";
                }
            }
            class Demo {
                public static void main(String[] args) {
                    Dog d = new Dog("Rex");
                    String s = d.speak();
                }
            }
            """;

        var result = new Tracer.Tracer().Trace(source);
        Assert.True(result.Completed, result.HaltReason);

        var allOps = result.Steps.SelectMany(s => s.Delta.Ops).ToList();
        var alloc = Assert.Single(allOps.OfType<HeapAlloc>());
        Assert.Contains(alloc.Fields, f => f.Key == "name" && f.Value == "Rex");

        var state = ApplyAll(result);
        var frame = Assert.Single(state.Frames);
        Assert.Equal("Rex says Woof", frame.Cell("s")!.Value);
    }

    // Recursion: unqualified self-calls (no receiver) must resolve via the frame's defining
    // class, and every call gets a callTree node so the branching structure is visible.
    [Fact]
    public void Trace_Recursion_BuildsBranchingCallTreeAndReturnsCorrectValue()
    {
        const string source = """
            class Demo {
                static int fib(int n) {
                    if (n < 2) return n;
                    return fib(n - 1) + fib(n - 2);
                }
                public static void main(String[] args) {
                    int result = fib(4);
                }
            }
            """;

        var result = new Tracer.Tracer().Trace(source);
        Assert.True(result.Completed, result.HaltReason);

        var allOps = result.Steps.SelectMany(s => s.Delta.Ops).ToList();
        var nodes = allOps.OfType<CallTreeNode>().ToList();
        var returns = allOps.OfType<CallTreeReturn>().ToList();

        Assert.Equal(9, nodes.Count); // recursion-tree node count for fib(4): T(n)=1+T(n-1)+T(n-2)
        Assert.Equal(nodes.Count, returns.Count);
        Assert.Contains(nodes, n => n.ParentId == null); // root call has no parent
        Assert.Contains(nodes, n => n.ParentId != null);  // recursive calls are parented

        var state = ApplyAll(result);
        var frame = Assert.Single(state.Frames);
        Assert.Equal("3", frame.Cell("result")!.Value); // fib(4) == 3

        Assert.Equal(9, state.CallTree.Count);
        Assert.All(state.CallTree, n => Assert.True(n.Returned));
    }
}
