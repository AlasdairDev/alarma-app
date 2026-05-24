namespace AlarmaApp;

public partial class App : Application
{
    private readonly AppShell _shell;
    private readonly Views.LaunchView _launchView;

    public App(AppShell shell, Views.LaunchView launchView)
    {
        _shell = shell;
        _launchView = launchView;
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        _launchView.Completed += OnLaunchCompleted;
        return new Window(_launchView);
    }

    private async void OnLaunchCompleted(object? sender, EventArgs e)
    {
        _launchView.Completed -= OnLaunchCompleted;
        Windows[0].Page = _shell;
        await _shell.GoToAsync("//home");
    }
}
