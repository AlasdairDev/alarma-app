namespace AlarmaApp;

public partial class App : Application
{
    private readonly Views.HomeView _homeView;

    public App(Views.HomeView homeView)
    {
        _homeView = homeView;
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        return new Window(new NavigationPage(_homeView));
    }
}
