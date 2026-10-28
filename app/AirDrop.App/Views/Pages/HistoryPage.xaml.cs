using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AirDrop.App.ViewModels;

namespace AirDrop.App.Views.Pages;

/// <summary>
/// "Geçmiş". Compact/regular: one list. Wide (≥ 1100): the list on the left and a preview of the selected transfer
/// (image, files, device, full time, Aç / Klasörde göster) on the right. Double-click opens in every layout.
/// </summary>
public partial class HistoryPage : UserControl
{
    private MainViewModel? _vm;

    public HistoryPage()
    {
        InitializeComponent();
        DependencyPropertyDescriptor.FromProperty(Responsive.ModeProperty, typeof(HistoryPage))
            .AddValueChanged(this, (_, _) => ApplyMode());
        DataContextChanged += (_, _) => Attach(DataContext as MainViewModel);
        Loaded += (_, _) => ApplyMode();
    }

    private bool IsWide => Responsive.GetMode(this) == LayoutMode.Wide;

    private void Attach(MainViewModel? vm)
    {
        if (_vm is not null) _vm.History.CollectionChanged -= History_Changed;
        _vm = vm;
        if (_vm is not null) _vm.History.CollectionChanged += History_Changed;
        EnsureSelection();
    }

    private void History_Changed(object? sender, NotifyCollectionChangedEventArgs e) => EnsureSelection();

    private void ApplyMode()
    {
        bool wide = IsWide;
        Root.MaxWidth = wide ? 1240 : 720;
        EnsureSelection();
    }

    private void EnsureSelection()
    {
        if (_vm is null || !IsWide)
        {
            DetailPane.Visibility = Visibility.Collapsed;
            DetailColumn.Width = new GridLength(0);
            return;
        }
        var current = DetailHost.Content as HistoryItemViewModel;
        Select(current is not null && _vm.History.Contains(current) ? current : _vm.History.FirstOrDefault());
    }

    private void Select(HistoryItemViewModel? item)
    {
        if (_vm is not null)
            foreach (var h in _vm.History) h.IsSelected = ReferenceEquals(h, item);
        DetailHost.Content = item;
        bool show = item is not null && IsWide;
        DetailPane.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        DetailColumn.Width = show ? new GridLength(1.1, GridUnitType.Star) : new GridLength(0);
    }

    private void Row_Click(object sender, MouseButtonEventArgs e)
    {
        if (IsWide && (sender as FrameworkElement)?.DataContext is HistoryItemViewModel item) Select(item);
    }
}
