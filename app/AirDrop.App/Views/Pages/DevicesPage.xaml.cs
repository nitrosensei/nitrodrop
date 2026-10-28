using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AirDrop.App.ViewModels;

namespace AirDrop.App.Views.Pages;

/// <summary>
/// "Cihazlar". Compact/regular: one column of PC cards that expand in place. Wide (≥ 1100): the cards become a list
/// (collapsed heads) and the selected PC's tabs and apps fill a detail pane on the right.
/// </summary>
public partial class DevicesPage : UserControl
{
    private DevicesViewModel? _vm;

    public DevicesPage()
    {
        InitializeComponent();
        DependencyPropertyDescriptor.FromProperty(Responsive.ModeProperty, typeof(DevicesPage))
            .AddValueChanged(this, (_, _) => ApplyMode());
        DataContextChanged += (_, _) => Attach(DataContext as DevicesViewModel);
        Loaded += (_, _) => ApplyMode();
    }

    private bool IsWide => Responsive.GetMode(this) == LayoutMode.Wide;

    private void Attach(DevicesViewModel? vm)
    {
        if (_vm is not null) _vm.Paired.CollectionChanged -= Paired_Changed;
        _vm = vm;
        if (_vm is not null) _vm.Paired.CollectionChanged += Paired_Changed;
        EnsureSelection();
    }

    private void Paired_Changed(object? sender, NotifyCollectionChangedEventArgs e) => EnsureSelection();

    private void ApplyMode()
    {
        bool wide = IsWide;
        Root.MaxWidth = wide ? 1240 : 720;
        ListColumn.Width = wide ? new GridLength(380) : new GridLength(1, GridUnitType.Star);
        DetailColumn.Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        EnsureSelection();
    }

    private void EnsureSelection()
    {
        var current = DetailHost.Content as SyncPcViewModel;
        if (_vm is null || !IsWide)
        {
            DetailPane.Visibility = Visibility.Collapsed;
            return;
        }
        if (current is null || !_vm.Paired.Contains(current))
            Select(_vm.Paired.FirstOrDefault(p => p.CanExpand) ?? _vm.Paired.FirstOrDefault());
        else DetailPane.Visibility = Visibility.Visible;
    }

    private void Select(SyncPcViewModel? pc)
    {
        if (_vm is not null)
            foreach (var p in _vm.Paired) p.IsSelected = ReferenceEquals(p, pc);
        DetailHost.Content = pc;
        DetailPane.Visibility = pc is not null && IsWide ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>"Şu an iPhone'da" card: below ~520 px the "continue on PC" button moves under the text.</summary>
    private void IphoneCard_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool narrow = e.NewSize.Width < 520;
        Grid.SetRow(ContinueButton, narrow ? 1 : 0);
        Grid.SetColumn(ContinueButton, narrow ? 1 : 2);
        ContinueButton.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        ContinueButton.Margin = narrow ? new Thickness(15, 10, 0, 0) : new Thickness(0);
    }

    /// <summary>Card head: expands in place (compact/regular) or selects into the detail pane (wide).</summary>
    private void PcHead_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SyncPcViewModel pc) return;
        if (IsWide) Select(pc);
        else pc.ToggleExpanded.Execute(null);
    }
}
