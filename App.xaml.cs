namespace AlarmaApp;

public partial class App : Application
{
    private readonly AppShell _shell;

    public App(AppShell shell)
    {
        _shell = shell;
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        return new Window(_shell);
    }
}
