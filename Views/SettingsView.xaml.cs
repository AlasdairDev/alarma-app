using AlarmaApp.Controllers;

namespace AlarmaApp.Views;

public partial class SettingsView : ContentPage
{
    private readonly HomeController _controller;

    public SettingsView(HomeController controller)
    {
        _controller = controller;
        BindingContext = controller;
        InitializeComponent();
    }

    protected override void OnAppearing()
    {
        Content.Opacity = 0;
        base.OnAppearing();
        Content.FadeTo(1, 220, Easing.CubicOut);
    }

    private async void OnUpdateContactsClicked(object? sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("//emergency");
    }

    private async void OnTermsClicked(object? sender, EventArgs e)
    {
        await Navigation.PushModalAsync(new TermsAndPrivacyView(0), animated: false);
    }

    private async void OnPrivacyClicked(object? sender, EventArgs e)
    {
        await Navigation.PushModalAsync(new TermsAndPrivacyView(1), animated: false);
    }
}
