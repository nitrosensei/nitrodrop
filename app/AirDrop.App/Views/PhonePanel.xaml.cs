using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using AirDrop.App.ViewModels;

namespace AirDrop.App.Views;

/// <summary>"Telefon" tab content. DataContext = <see cref="PhoneViewModel"/>.</summary>
public partial class PhonePanel : UserControl
{
    private PhoneViewModel? _vm;
    private ConversationItem? _watched;

    public PhonePanel()
    {
        InitializeComponent();
        PcNameText.Text = AirDrop.App.Services.LocalPcName.Value;
        DataContextChanged += (_, _) => Attach(DataContext as PhoneViewModel);
        // Conversation list width follows the main window layout (Views/Responsive.cs).
        DependencyPropertyDescriptor.FromProperty(AirDrop.App.Views.Responsive.ModeProperty, typeof(PhonePanel))
            .AddValueChanged(this, (_, _) => ApplyMode());
        Loaded += (_, _) => ApplyMode();
    }

    private void ApplyMode() => ConversationColumn.Width = new GridLength(Responsive.GetMode(this) switch
    {
        LayoutMode.Wide => 300,
        LayoutMode.Regular => 240,
        _ => 188,
    });

    private void Attach(PhoneViewModel? vm)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
        _vm = vm;
        if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
        WatchConversation(_vm?.SelectedConversation);
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PhoneViewModel.SelectedConversation) or "")
            WatchConversation(_vm?.SelectedConversation);
    }

    private void WatchConversation(ConversationItem? c)
    {
        if (_watched is not null) _watched.Messages.CollectionChanged -= OnMessagesChanged;
        _watched = c;
        if (_watched is not null) _watched.Messages.CollectionChanged += OnMessagesChanged;
        ScrollToEnd();
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScrollToEnd();

    private void ScrollToEnd() =>
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => BubbleScroll.ScrollToEnd()));

    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (MoreButton.ContextMenu is not { } menu) return;
        menu.PlacementTarget = MoreButton;
        menu.Placement = PlacementMode.Bottom;
        menu.DataContext = DataContext;
        menu.IsOpen = true;
    }

    private void Reply_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _vm is null) return;
        e.Handled = true;
        if (_vm.SendReply.CanExecute(null)) _vm.SendReply.Execute(null);
    }
}
