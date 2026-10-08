using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;
using Sentinel.App.ViewModels;
using Sentinel.Intelligence;

namespace Sentinel.App.Views;

public sealed partial class TimelinePage : SentinelPage
{
    private ListView? _selectedList;

    public TimelineViewModel Vm { get; } = Create<TimelineViewModel>();

    public TimelinePage()
    {
        ViewModel = Vm;
        InitializeComponent();
        foreach (var name in Vm.FilterNames)
        {
            var toggle = new ToggleButton { Content = name, IsChecked = true, Style = (Style)Application.Current.Resources["ChipToggleButtonStyle"] };
            toggle.Click += (_, _) => Vm.ToggleFilter(name, toggle.IsChecked == true);
            FilterPanel.Children.Add(toggle);
        }
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is TimeRange) Sections.SelectedItem = Sections.Items[1];
    }

    private void OnSectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var index = sender.Items.IndexOf(sender.SelectedItem);
        TimelinePanel.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        ChangesPanel.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnRangeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RangeBox.SelectedItem is ComboBoxItem { Tag: string t } && int.TryParse(t, out var days)) Vm.DaysBack = days;
    }

    private void OnEventSelected(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListView list || list.SelectedItem is not EventRow row) return;
        // Only one day's list keeps a selection at a time.
        if (_selectedList is not null && _selectedList != list) _selectedList.SelectedItem = null;
        _selectedList = list;
        Vm.Selected = row;
    }

    private void OnCustomDate(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args)
    {
        if (args.NewDate is { } d)
            Vm.LoadChanges(new TimeRange(new DateTimeOffset(d.Date, DateTimeOffset.Now.Offset), DateTimeOffset.Now, $"since {d:d}"));
    }
}
