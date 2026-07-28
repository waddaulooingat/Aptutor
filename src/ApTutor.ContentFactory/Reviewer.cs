using ApTutor.Content;

namespace ApTutor.ContentFactory;

/// The human-verification pass build-plan.md requires before anything ships ("nothing ships
/// unverified — a paid product cannot ship a wrong explanation"). Walks every unverified pack in a
/// content directory and lets a human approve, reject (delete, for regeneration later), or skip.
/// Only Verified: true packs are ever served by FileContentSource/AuthoredStepProvider.
public static class Reviewer
{
    public static void Run(string contentDir)
    {
        var pending = ContentPackStore.LoadAll(contentDir).Where(p => !p.Verified).ToList();
        if (pending.Count == 0)
        {
            Console.WriteLine("Nothing pending review.");
            return;
        }

        Console.WriteLine($"{pending.Count} node(s) pending review.\n");
        foreach (var pack in pending)
        {
            Print(pack);
            Console.Write("[a]pprove / [r]eject (delete) / [s]kip? ");
            var choice = Console.ReadLine()?.Trim().ToLowerInvariant();
            switch (choice)
            {
                case "a":
                    ContentPackStore.Save(contentDir, pack with { Verified = true });
                    Console.WriteLine($"  approved {pack.NodeId}\n");
                    break;
                case "r":
                    ContentPackStore.Delete(contentDir, pack.NodeId);
                    Console.WriteLine($"  rejected + deleted {pack.NodeId} (regenerate it later)\n");
                    break;
                default:
                    Console.WriteLine($"  skipped {pack.NodeId}\n");
                    break;
            }
        }
    }

    private static void Print(NodeContentPack pack)
    {
        Console.WriteLine(new string('=', 72));
        Console.WriteLine($"Node: {pack.NodeId}   generated {pack.GeneratedAt:u} by {pack.Model}");
        Console.WriteLine(new string('-', 72));

        Console.WriteLine("Walkthrough text:");
        Console.WriteLine($"  {pack.WalkthroughText}\n");

        Console.WriteLine($"Practice items ({pack.PracticeItems.Count}):");
        foreach (var item in pack.PracticeItems)
        {
            Console.WriteLine($"  Q: {item.Prompt}");
            for (var i = 0; i < item.Choices.Count; i++)
                Console.WriteLine($"     {(i == item.CorrectIndex ? "*" : " ")} {(char)('A' + i)}. {item.Choices[i]}");
            Console.WriteLine($"     explanation: {item.Explanation}\n");
        }

        Console.WriteLine($"Walkthrough steps ({pack.WalkthroughSteps.Count}):");
        foreach (var step in pack.WalkthroughSteps)
        {
            var opSummary = string.Join(", ", step.Delta.Ops.Select(o => o.GetType().Name));
            Console.WriteLine($"  [{step.Index}] {step.Caption}  ({step.Delta.Ops.Count} op(s): {opSummary})");
        }
        Console.WriteLine();
    }
}
