using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Fable2Launcher;

// Achievements page: button with progress ring next to "Minimize", and a list
// with All / Completed / Not completed tabs. Completed cards are bright,
// the rest are dimmed.
public partial class MainWindow
{
    private Dictionary<int, DateTime?> _unlocked = new();
    private string _achievementProfile = "";
    private bool _achievementStateFileExists;

    private static readonly Brush AchOnName = new SolidColorBrush(Color.FromRgb(0xF7, 0xE6, 0xC0));
    private static readonly Brush AchOnDesc = new SolidColorBrush(Color.FromRgb(0xCD, 0xBF, 0xA8));
    private static readonly Brush AchOffName = new SolidColorBrush(Color.FromRgb(0x8D, 0x84, 0x7B));
    private static readonly Brush AchOffDesc = new SolidColorBrush(Color.FromRgb(0x6F, 0x67, 0x60));
    private static readonly Brush AchOnBorder = new SolidColorBrush(Color.FromRgb(0xC9, 0xA0, 0x60));
    private static readonly Brush AchOffBorder = new SolidColorBrush(Color.FromRgb(0x3A, 0x2E, 0x24));
    private static readonly Brush AchOffBackground = new SolidColorBrush(Color.FromArgb(0xBF, 0x10, 0x0B, 0x0A));
    private static readonly Brush AchOnBackground = new LinearGradientBrush(
        Color.FromArgb(0xF2, 0x55, 0x39, 0x23), Color.FromArgb(0xEB, 0x28, 0x1B, 0x13), 0);
    private static readonly Brush MedalOnRim = new LinearGradientBrush(
        Color.FromRgb(0xF5, 0xDD, 0xA8), Color.FromRgb(0xA7, 0x7A, 0x45), 90);
    private static readonly Brush MedalOnFace = new RadialGradientBrush(
        Color.FromRgb(0xE7, 0xC3, 0x87), Color.FromRgb(0xB4, 0x84, 0x4A)) { GradientOrigin = new Point(0.35, 0.3) };
    private static readonly Brush MedalOffRim = new SolidColorBrush(Color.FromRgb(0x3A, 0x33, 0x30));
    private static readonly Brush MedalOffFace = new SolidColorBrush(Color.FromRgb(0x22, 0x1C, 0x19));
    private static readonly Brush TrophyOn = new SolidColorBrush(Color.FromRgb(0x2A, 0x1A, 0x0C));
    private static readonly Brush TrophyOff = new SolidColorBrush(Color.FromRgb(0x6F, 0x67, 0x60));

    /// <summary>Re-reads saves/achievements/4D5307F1.toml and updates button + page.</summary>
    private void RefreshAchievements()
    {
        if (AchievementsBadgeText is null) return;
        string directory = _executableDirectory ?? AppContext.BaseDirectory;
        _achievementStateFileExists = System.IO.File.Exists(Achievements.StatePath(directory));
        _unlocked = Achievements.ReadUnlocked(directory);

        IReadOnlyList<AchievementInfo> catalog = Achievements.Catalog;
        int total = catalog.Count;
        int done = catalog.Count(a => _unlocked.ContainsKey(a.Id));
        int pointsTotal = catalog.Sum(a => a.Points);
        int pointsDone = catalog.Where(a => _unlocked.ContainsKey(a.Id)).Sum(a => a.Points);
        double fraction = total == 0 ? 0 : (double)done / total;

        AchievementsBadgeText.Text = $"{done} / {total}";
        // Dash units are multiples of the stroke thickness (3); ring diameter 28.
        double circumference = Math.PI * (28 - 3) / 3.0;
        AchRing.StrokeDashArray = new DoubleCollection { fraction * circumference, 1000 };

        AchProfileRun.Text = _achievementProfile.Length > 0 ? "Fable II · " + _achievementProfile : "Fable II";
        AchDoneRun.Text = done.ToString(CultureInfo.InvariantCulture);
        AchTotalRun.Text = total.ToString(CultureInfo.InvariantCulture);
        AchPointsDoneRun.Text = pointsDone.ToString(CultureInfo.InvariantCulture);
        AchPointsTotalRun.Text = $" / {pointsTotal} G";
        AchProgressFill.Width = 520 * fraction;
        AchCountAll.Text = total.ToString(CultureInfo.InvariantCulture);
        AchCountDone.Text = done.ToString(CultureInfo.InvariantCulture);
        AchCountTodo.Text = (total - done).ToString(CultureInfo.InvariantCulture);
        AchStateHint.Text = _achievementStateFileExists ? "" :
            "Файл прогресса ещё не создан: он появится после первого достижения в игре.";

        if (AchievementsPage.Visibility == Visibility.Visible) BuildAchievementList();
    }

    private void OpenAchievementsClicked(object sender, RoutedEventArgs e)
    {
        CancelKeyCapture();
        RefreshAchievements();
        BuildAchievementList();
        AchievementsPage.Visibility = Visibility.Visible;
        AchievementsButton.Visibility = Visibility.Collapsed;
        AchievementsScroll.ScrollToTop();
        ApplyLocalizedTexts();
    }

    private void AchievementsBackClicked(object sender, RoutedEventArgs e) => CloseAchievements();

    private void CloseAchievements()
    {
        AchievementsPage.Visibility = Visibility.Collapsed;
        AchievementsButton.Visibility = HomePage.Visibility == Visibility.Visible
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AchievementFilterChanged(object sender, RoutedEventArgs e) => RebuildIfOpen();

    private void AchievementFilterChanged(object sender, SelectionChangedEventArgs e) => RebuildIfOpen();

    private void RebuildIfOpen()
    {
        if (AchievementsList is null || AchievementsPage is null) return;
        if (AchievementsPage.Visibility != Visibility.Visible) return;
        BuildAchievementList();
        AchievementsScroll.ScrollToTop();
    }

    private string SelectedAchievementFilter()
    {
        foreach (UIElement child in ((Panel)AchTabAll.Parent).Children)
            if (child is RadioButton { IsChecked: true } tab) return (string)tab.Tag;
        return "all";
    }

    private void BuildAchievementList()
    {
        string filter = SelectedAchievementFilter();
        string sort = SelectedTag(AchSortCombo) ?? "unlocked";
        IEnumerable<AchievementInfo> items = Achievements.Catalog;
        items = filter switch
        {
            "done" => items.Where(a => _unlocked.ContainsKey(a.Id)),
            "todo" => items.Where(a => !_unlocked.ContainsKey(a.Id)),
            _ => items,
        };
        items = sort switch
        {
            "order" => items.OrderBy(a => a.Id),
            "points" => items.OrderByDescending(a => a.Points).ThenBy(a => a.Id),
            "name" => items.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase),
            // Unlocked first (newest first), then the rest in game order.
            _ => items.OrderBy(a => _unlocked.ContainsKey(a.Id) ? 0 : 1)
                      .ThenByDescending(a => _unlocked.TryGetValue(a.Id, out DateTime? t) ? t ?? DateTime.MinValue : DateTime.MinValue)
                      .ThenBy(a => a.Id),
        };

        AchievementsList.Children.Clear();
        foreach (AchievementInfo item in items)
            AchievementsList.Children.Add(BuildAchievementCard(item));

        bool empty = AchievementsList.Children.Count == 0;
        AchievementsEmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        AchievementsEmptyText.Text = filter == "done"
            ? "Пока нет полученных достижений."
            : "Все достижения получены!";
        ApplyLocalizedTexts();
    }

    private FrameworkElement BuildAchievementCard(AchievementInfo item)
    {
        bool on = _unlocked.TryGetValue(item.Id, out DateTime? when);

        var medal = new Grid { Width = 56, Height = 56, VerticalAlignment = VerticalAlignment.Center };
        medal.Children.Add(new Ellipse { Fill = on ? MedalOnRim : MedalOffRim });
        medal.Children.Add(new Ellipse { Fill = on ? MedalOnFace : MedalOffFace, Margin = new Thickness(3) });
        medal.Children.Add(new Path
        {
            Data = (Geometry)FindResource("TrophyGeometry"),
            Fill = on ? TrophyOn : TrophyOff,
            Stretch = Stretch.Uniform,
            Width = 24,
            Height = 24,
        });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 12, 0) };
        text.Children.Add(new TextBlock
        {
            Text = item.Name,
            FontSize = 19,
            FontWeight = FontWeights.Bold,
            Foreground = on ? AchOnName : AchOffName,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        text.Children.Add(new TextBlock
        {
            Text = item.Description,
            FontSize = 14.5,
            Foreground = on ? AchOnDesc : AchOffDesc,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 4, 0, 0),
        });
        var state = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 0) };
        if (on)
        {
            state.Children.Add(new TextBlock { Text = "Получено", FontSize = 13.5, Foreground = SuccessBrush });
            if (when is DateTime date)
                state.Children.Add(new TextBlock
                {
                    Text = " " + date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
                    FontSize = 13.5,
                    Foreground = SuccessBrush,
                });
        }
        else
        {
            state.Children.Add(new Path
            {
                Data = (Geometry)FindResource("LockGeometry"),
                Fill = AchOffName,
                Stretch = Stretch.Uniform,
                Width = 12,
                Height = 12,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            state.Children.Add(new TextBlock { Text = "Не получено", FontSize = 13.5, Foreground = AchOffName });
        }
        text.Children.Add(state);

        var points = new TextBlock
        {
            FontFamily = (FontFamily)FindResource("HeadingFont"),
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = on ? (Brush)FindResource("GoldLightBrush") : AchOffName,
        };
        points.Inlines.Add(new System.Windows.Documents.Run(item.Points.ToString(CultureInfo.InvariantCulture)) { FontSize = 24 });
        points.Inlines.Add(new System.Windows.Documents.Run(" G") { FontSize = 14 });

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(text, 1);
        Grid.SetColumn(points, 2);
        grid.Children.Add(medal);
        grid.Children.Add(text);
        grid.Children.Add(points);

        return new Border
        {
            Height = 82,
            Margin = new Thickness(0, 0, 16, 12),
            Padding = new Thickness(12, 0, 18, 0),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = on ? AchOnBorder : AchOffBorder,
            Background = on ? AchOnBackground : AchOffBackground,
            Child = grid,
            ToolTip = item.Name + "\n" + item.Description,
        };
    }
}
