using AlarmaApp.Services;

namespace AlarmaApp.Views;

public partial class OnboardingPage : ContentPage
{
    private readonly PreferencesService _preferencesService;
    private bool _finishing;
    private bool _isAgreed;
    private int _index;

    // Full-design PNGs: the title, subtitle, dots and Skip/Next pills are baked into each image.
    private static readonly string[] SlideImages =
    {
        "onboarding_full_1.png",
        "onboarding_full_2.png",
        "onboarding_full_3.png",
        "onboarding_full_4.png",
    };

    private bool IsLastSlide => _index == SlideImages.Length - 1;

    public OnboardingPage(PreferencesService preferencesService)
    {
        _preferencesService = preferencesService;
        InitializeComponent();
        ShowSlide(0);
    }

    private void ShowSlide(int index)
    {
        _index = Math.Clamp(index, 0, SlideImages.Length - 1);
        SlideImage.Source = SlideImages[_index];
    }

    private void OnNextTapped(object? sender, TappedEventArgs e)
    {
        // On the final slide, Next opens the Terms & Conditions popup instead of advancing.
        if (IsLastSlide)
            TermsPopup.IsVisible = true;
        else
            ShowSlide(_index + 1);
    }

    private void OnSkipTapped(object? sender, TappedEventArgs e)
    {
        // Skip jumps straight to the last slide; on the last slide it opens the popup.
        if (IsLastSlide)
            TermsPopup.IsVisible = true;
        else
            ShowSlide(SlideImages.Length - 1);
    }

    private void OnAgreeCheckChanged(object? sender, CheckedChangedEventArgs e)
        => _isAgreed = e.Value;

    private void OnCloseTermsTapped(object? sender, TappedEventArgs e)
        => TermsPopup.IsVisible = false;

    private async void OnTermsLinkTapped(object? sender, TappedEventArgs e)
        => await Navigation.PushModalAsync(new TermsAndPrivacyView(initialTab: 0), animated: false);

    private async void OnPrivacyPolicyLinkTapped(object? sender, TappedEventArgs e)
        => await Navigation.PushModalAsync(new TermsAndPrivacyView(initialTab: 1), animated: false);

    private async void OnGetStartedClicked(object? sender, EventArgs e)
    {
        // The checkbox must be ticked before onboarding can complete.
        if (!_isAgreed)
        {
            await DisplayAlert(
                "Agreement Required",
                "Please agree to the Privacy Policy and Terms & Conditions to continue.",
                "OK");
            return;
        }

        await FinishOnboardingAsync();
    }

    private async Task FinishOnboardingAsync()
    {
        if (_finishing) return;
        _finishing = true;

        // Commit the agreement, then redirect to the main Home/Map interface.
        // HomeView's own gate forwards to permissions setup if that step is still pending.
        _preferencesService.HasSeenTutorial = true;
        _preferencesService.HasAgreedToTerms = true;
        await Shell.Current.GoToAsync("//home", animate: false);
    }
}
