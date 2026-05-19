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
        var testPage = new ContentPage
        {
            BackgroundColor = Colors.Red,
            Content = new Label
            {
                Text = "MAUI IS WORKING",
                FontSize = 32,
                TextColor = Colors.White,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center
            }
        };
        return new Window(testPage);
    }
}
