using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using AirDrop.App.ViewModels;

namespace AirDrop.App.Views.Pages;

/// <summary>
/// "Ayarlar" (DataContext = SettingsViewModel; sync/phone/driver sections bind to the window's MainViewModel).
/// Wide (≥ 1100): a category column made from the visible section titles (SectionTitle style), so new sections join
/// it automatically; it follows the scroll position. With the sidebar visible the back button is redundant and hidden.
/// </summary>
public partial class SettingsPage : UserControl
{
    private readonly List<(TextBlock Title, RadioButton Nav)> _categories = new();
    private bool _syncingNav;

    public SettingsPage()
    {
        InitializeComponent();
        DependencyPropertyDescriptor.FromProperty(Responsive.ModeProperty, typeof(SettingsPage))
            .AddValueChanged(this, (_, _) => ApplyMode());
        Loaded += (_, _) => ApplyMode();
        IsVisibleChanged += (_, _) => { if (IsVisible) Dispatcher.BeginInvoke(RebuildCategories, System.Windows.Threading.DispatcherPriority.Loaded); };
    }

    private LayoutMode Mode => Responsive.GetMode(this);

    private void ApplyMode()
    {
        bool wide = Mode == LayoutMode.Wide;
        BackButton.Visibility = Mode == LayoutMode.Compact ? Visibility.Visible : Visibility.Collapsed;
        CategoryColumn.Width = wide ? new GridLength(250) : new GridLength(0);
        CategoryScroll.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        Header.MaxWidth = wide ? double.PositiveInfinity : 664;
        Header.Margin = wide ? new Thickness(26, 10, 24, 14) : new Thickness(18, 10, 24, 14);
        if (wide) RebuildCategories();
    }

    /// <summary>One nav entry per visible section title, in document order (titles are live {l:Loc} text).</summary>
    private void RebuildCategories()
    {
        if (Mode != LayoutMode.Wide) return;
        var titles = Descendants(ContentPanel).OfType<TextBlock>()
            .Where(t => ReferenceEquals(t.Style, TryFindResource("SectionTitle")) && t.IsVisible).ToList();
        if (titles.SequenceEqual(_categories.Select(c => c.Title))) return;
        Categories.Children.Clear();
        _categories.Clear();
        var style = (Style)FindResource("NavItem");
        foreach (var t in titles)
        {
            var nav = new RadioButton { Style = style, GroupName = "settings-categories", FontSize = 12 };
            nav.SetBinding(ContentProperty, new System.Windows.Data.Binding(nameof(TextBlock.Text)) { Source = t });
            nav.Checked += (_, _) => { if (!_syncingNav) ScrollTo(t); };
            Categories.Children.Add(nav);
            _categories.Add((t, nav));
        }
        SyncNav();
    }

    private void ScrollTo(TextBlock title)
    {
        var y = title.TranslatePoint(new Point(0, 0), ContentPanel).Y;
        ContentScroll.ScrollToVerticalOffset(System.Math.Max(0, y - 4));
    }

    private void ContentScroll_ScrollChanged(object sender, ScrollChangedEventArgs e) => SyncNav();

    /// <summary>Checks the last section whose title is at or above the top of the viewport.</summary>
    private void SyncNav()
    {
        if (_categories.Count == 0 || Mode != LayoutMode.Wide) return;
        double top = ContentScroll.VerticalOffset + 24;
        var current = _categories[0];
        foreach (var c in _categories)
        {
            if (!c.Title.IsVisible) continue;
            if (c.Title.TranslatePoint(new Point(0, 0), ContentPanel).Y <= top) current = c;
        }
        if (ContentScroll.VerticalOffset >= ContentScroll.ScrollableHeight - 1 && ContentScroll.ScrollableHeight > 0)
            current = _categories.Last(c => c.Title.IsVisible);
        _syncingNav = true;
        current.Nav.IsChecked = true;
        _syncingNav = false;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }

    private void Settings_Back(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this)?.DataContext is MainViewModel vm) vm.Tab = MainTab.Nearby;
    }

    private void ShowOnboarding_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).ShowOnboarding();
}
