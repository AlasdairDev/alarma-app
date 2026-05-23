using AlarmaApp.Controllers;

namespace AlarmaApp.Views;

public partial class HomeView : ContentPage
{
    private readonly HomeController _controller;

    public HomeView(HomeController controller)
    {
        _controller = controller;
        BindingContext = _controller;
        InitializeComponent();
        Appearing += OnAppearing;
    }

    private async void OnAppearing(object? sender, EventArgs e)
    {
        await _controller.InitializeAsync();
    }
}
