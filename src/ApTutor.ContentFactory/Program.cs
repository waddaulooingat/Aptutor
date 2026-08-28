// ApTutor.ContentFactory — Phase 7 build-time content generator/reviewer.
// This is the ONE tool that talks to the live Claude API; it is deliberately its own project so
// no API key or network dependency ever ships inside ApTutor.Client. See README.md for usage.

using ApTutor.Content;
using ApTutor.ContentFactory;
using ApTutor.Curriculum;

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

return args[0] switch
{
    "generate" => await RunGenerateAsync(args[1..]),
    "review" => RunReview(args[1..]),
    _ => Fail($"Unknown command '{args[0]}'."),
};

static void PrintUsage() => Console.WriteLine("""
    ApTutor.ContentFactory — Phase 7 build-time content generator/reviewer.

    Usage:
      generate --dag <skill-dag.json> --course <courseId> --content <content-dir> [--units 1-5] [--difficulty easy|medium|hard] --model <model-id>
      review   --content <content-dir>

    generate calls the live Claude API (needs ANTHROPIC_API_KEY set in the environment — this is
    real, billed spend on your own account) and writes one "<nodeId>.json" file per DAG node under
    --content, each starting unverified. --difficulty defaults to "medium" if omitted.

    review walks every unverified file under --content and lets you approve, reject (delete), or
    skip each one. Only approved (Verified: true) files are ever served to the running app — see
    ApTutor.Content.FileContentSource / AuthoredStepProvider.
    """);

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}

static async Task<int> RunGenerateAsync(string[] args)
{
    var opts = ParseOptions(args);
    if (!opts.TryGetValue("dag", out var dagPath) ||
        !opts.TryGetValue("course", out var courseId) ||
        !opts.TryGetValue("content", out var contentDir))
        return Fail("generate needs --dag, --course, and --content.");

    // No default model id here on purpose — model ids change over time, and hardcoding one risks
    // silently pointing at something stale or unavailable on the caller's account. Pick one at
    // https://docs.anthropic.com when you run this.
    if (!opts.TryGetValue("model", out var model) || string.IsNullOrWhiteSpace(model))
        return Fail("generate needs --model <model-id> (check https://docs.anthropic.com for current model ids).");

    var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
    if (string.IsNullOrWhiteSpace(apiKey))
        return Fail("ANTHROPIC_API_KEY is not set. This command makes real, billed API calls with your own key.");

    var graph = SkillDagLoader.Load(dagPath);
    IEnumerable<DagNode> nodes = graph.Dag.Nodes;
    if (opts.TryGetValue("units", out var unitsSpec))
        nodes = FilterByUnits(nodes, unitsSpec);

    var nodeList = nodes.ToList();
    if (nodeList.Count == 0)
        return Fail("No nodes matched --units.");

    var difficulty = Difficulty.Medium;
    if (opts.TryGetValue("difficulty", out var difficultySpec) && !Enum.TryParse(difficultySpec, ignoreCase: true, out difficulty))
        return Fail($"--difficulty must be one of: easy, medium, hard (got '{difficultySpec}').");

    var client = new ClaudeClient(apiKey, model);
    var generator = new Generator(client);

    Console.WriteLine($"Generating {difficulty} content for {nodeList.Count} node(s) into '{contentDir}' using model '{model}'...");
    var failures = 0;
    foreach (var node in nodeList)
    {
        Console.Write($"  {node.Id} ... ");
        try
        {
            var pack = await generator.GenerateAsync(courseId, node, difficulty);
            ContentPackStore.Save(contentDir, pack);
            Console.WriteLine("done (unverified — run 'review' next).");
        }
        catch (Exception ex)
        {
            failures++;
            Console.WriteLine($"FAILED: {ex.Message}");
        }
    }

    Console.WriteLine($"\n{nodeList.Count - failures}/{nodeList.Count} generated. Run 'review --content {contentDir}' before this content ships.");
    return failures == 0 ? 0 : 1;
}

static int RunReview(string[] args)
{
    var opts = ParseOptions(args);
    if (!opts.TryGetValue("content", out var contentDir))
        return Fail("review needs --content.");

    Reviewer.Run(contentDir);
    return 0;
}

static Dictionary<string, string> ParseOptions(string[] args)
{
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 0; i < args.Length - 1; i++)
        if (args[i].StartsWith("--", StringComparison.Ordinal))
            result[args[i][2..]] = args[i + 1];
    return result;
}

static IEnumerable<DagNode> FilterByUnits(IEnumerable<DagNode> nodes, string spec)
{
    // "1-5", "1,2,3", or "1-3,7"
    var allowed = new HashSet<int>();
    foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
    {
        var range = part.Split('-');
        if (range.Length == 2 && int.TryParse(range[0], out var lo) && int.TryParse(range[1], out var hi))
            for (var u = lo; u <= hi; u++) allowed.Add(u);
        else if (int.TryParse(part, out var single))
            allowed.Add(single);
    }
    return nodes.Where(n => allowed.Contains(n.Unit));
}
