using Sentinel.App.ViewModels;

namespace Sentinel.App.Views;

public sealed partial class TimeMachinePage : SentinelPage
{
    public TimeMachineViewModel Vm { get; } = Create<TimeMachineViewModel>();

    public TimeMachinePage()
    {
        ViewModel = Vm;
        InitializeComponent();
        Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TimeMachineViewModel.Chart) && Vm.Chart is { } m) Chart.Apply(m);
            if (e.PropertyName == nameof(TimeMachineViewModel.Minute)) MarkMoment();
        };
    }

    private void MarkMoment()
    {
        if (Vm.Chart is not { } m) return;
        var moment = m.From.AddMinutes(Vm.Minute);
        m.Bands.Clear();
        m.Bands.Add(new Controls.ChartBand(moment.AddMinutes(-3), moment.AddMinutes(3), "Selected moment"));
        Chart.Apply(m);
    }
}
