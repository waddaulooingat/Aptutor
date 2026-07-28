// The Java-source counterpart to ApTutor.Scene's ReferenceVsValueDemo fixture — traceable source
// text that Tracer.Trace() should turn into (structurally) the same walkthrough. Shared between
// the closing-loop test and the CS A module's step provider so the two copies can't drift apart.

namespace ApTutor.Tracer;

public static class SampleProgram
{
    public const string ReferenceVsValueDemoJava = """
        class Point {
            int x;
            void setX(int x) { this.x = x; }
        }

        class Demo {
            public static void main(String[] args) {
                int x = 5;
                Point p = new Point();
                p.setX(5);
                Point q = p;
                q.setX(9);
            }
        }
        """;
}
