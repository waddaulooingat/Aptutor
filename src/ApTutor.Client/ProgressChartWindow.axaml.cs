using ApTutor.Client.Services;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace ApTutor.Client;

/// A minimal, hand-rolled bar chart — no charting library in this codebase yet, and one week-over-
/// week accuracy view doesn't justify adding a new dependency for it (see the difficulty-and-
/// progress-tracking plan's Part B: "line or bar chart of mastery percentage/accuracy rate over
/// time," picked over a mastery-map or streak-counter view).
public partial class ProgressChartWindow : Window
{
    private const double MaxBarHeight = 160;

    public ProgressChartWindow(string courseDisplayName, IReadOnlyList<AttemptAnalysis.WeeklyAccuracy> weeks)
    {
        InitializeComponent();
        HeaderText.Text = $"Progress — {courseDisplayName}";
        BuildChart(weeks);
    }

    private void BuildChart(IReadOnlyList<AttemptAnalysis.WeeklyAccuracy> weeks)
    {
        ChartPanel.Children.Clear();

        if (weeks.All(w => w.Total == 0))
        {
            ChartPanel.Children.Add(new TextBlock
            {
                Text = "No practice recorded yet. Answer some practice questions to start building this chart.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Gray,
                Width = 400,
            });
            return;
        }

        foreach (var week in weeks)
            ChartPanel.Children.Add(BuildColumn(week));
    }

    private static Control BuildColumn(AttemptAnalysis.WeeklyAccuracy week)
    {
        var column = new StackPanel { Width = 44, Spacing = 4 };

        column.Children.Add(new TextBlock
        {
            Text = week.PercentCorrect is { } pct ? $"{pct:P0}" : "—",
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        var barHeight = week.PercentCorrect is { } p ? Math.Max(3, p * MaxBarHeight) : 3;
        var barSlot = new Border
        {
            Height = MaxBarHeight,
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = new Border
            {
                Width = 28,
                Height = barHeight,
                VerticalAlignment = VerticalAlignment.Bottom,
                CornerRadius = new Avalonia.CornerRadius(3, 3, 0, 0),
                Background = week.Total == 0 ? Brushes.LightGray : Brushes.SteelBlue,
            },
        };
        column.Children.Add(barSlot);

        column.Children.Add(new TextBlock
        {
            Text = week.WeekStart.ToString("MMM d"),
            FontSize = 10,
            Foreground = Brushes.Gray,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        return column;
    }
}
