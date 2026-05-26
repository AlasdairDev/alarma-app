// Security Considerations (OWASP Top 10)
// A01 Broken Access Control: HasSeenTutorial and HasAgreedToTerms are set only on explicit
//   completion of the last slide with the checkbox validated — not on any intermediate step.
// A04 Insecure Design: _finishing flag prevents double-navigation; _isAnimating prevents
//   concurrent slide animations from corrupting _currentPage state.

using AlarmaApp.Services;
using Microsoft.Maui.Controls.Shapes;

namespace AlarmaApp.Views;

public partial class OnboardingView : ContentPage
{
    private readonly PreferencesService _preferencesService;

    private int _currentPage;
    private const int TotalPages = 4;
    private bool _isAnimating;
    private bool _finishing;

    private readonly View[] _slides;
    private readonly Ellipse[] _dots;

    public OnboardingView(PreferencesService preferencesService)
    {
        _preferencesService = preferencesService;
        InitializeComponent();
        _slides = [Slide0, Slide1, Slide2, Slide3];
        _dots = [Dot0, Dot1, Dot2, Dot3];
    }

    protected override async void OnAppearing()
    {
        Content.Opacity = 0;
        base.OnAppearing();
        await Content.FadeTo(1, 500, Easing.CubicOut);
    }

    private async void OnNextClicked(object? sender, EventArgs e)
    {
        if (_currentPage < TotalPages - 1)
            await ShowPageAsync(_currentPage + 1);
        else
            await FinishOnboardingAsync();
    }

    // Skip jumps to the last slide so the T&C gate is always shown
    private void OnSkipTapped(object? sender, TappedEventArgs e) => _ = ShowPageAsync(TotalPages - 1);

    private void OnSwipedLeft(object? sender, SwipedEventArgs e)
    {
        if (_currentPage < TotalPages - 1)
            _ = ShowPageAsync(_currentPage + 1);
    }

    private void OnSwipedRight(object? sender, SwipedEventArgs e)
    {
        if (_currentPage > 0)
            _ = ShowPageAsync(_currentPage - 1);
    }

    private void OnAgreeCheckChanged(object? sender, CheckedChangedEventArgs e)
    {
        if (_currentPage == TotalPages - 1)
            NextButton.IsEnabled = e.Value;
    }

    private async void OnPolicyLinkTapped(object? sender, TappedEventArgs e)
    {
        await DisplayAlert(
            "Privacy Policy & Terms",
            "Privacy Policy: We collect your GPS location solely to power trip-tracking and proximity alarms. All data is stored locally on your device and never shared with third parties.\n\n" +
            "Terms & Conditions: (1) This app provides travel-assistance only and is not a substitute for personal safety. (2) Emergency SOS requires valid phone numbers and active mobile service. (3) Alarm accuracy depends on GPS signal quality. (4) You are responsible for your own safety and departure decisions.",
            "Close");
    }

    private async Task ShowPageAsync(int toIndex)
    {
        if (_isAnimating || toIndex < 0 || toIndex >= TotalPages || toIndex == _currentPage)
            return;

        _isAnimating = true;
        try
        {
            var outgoing = _slides[_currentPage];
            var incoming = _slides[toIndex];

            incoming.Opacity = 0;
            incoming.IsVisible = true;

            await Task.WhenAll(
                outgoing.FadeTo(0, 200),
                incoming.FadeTo(1, 200));

            outgoing.IsVisible = false;
            outgoing.Opacity = 1;

            _currentPage = toIndex;
            UpdateDots(_currentPage);
            SkipLabel.IsVisible = _currentPage < TotalPages - 1;
            NextButton.Text = _currentPage == TotalPages - 1 ? "Get Started" : "Next";
            // Disable "Get Started" until the agree checkbox is ticked
            NextButton.IsEnabled = _currentPage != TotalPages - 1 || AgreeCheckBox.IsChecked;
        }
        finally
        {
            _isAnimating = false;
        }
    }

    private void UpdateDots(int activePage)
    {
        for (int i = 0; i < _dots.Length; i++)
        {
            _dots[i].Fill = new SolidColorBrush(i == activePage
                ? Color.FromArgb("#9B6FBF")
                : Color.FromArgb("#4A3570"));
            _dots[i].WidthRequest = i == activePage ? 24 : 8;
        }
    }

    private async Task FinishOnboardingAsync()
    {
        if (_finishing) return;
        _finishing = true;
        _preferencesService.HasSeenTutorial = true;
        _preferencesService.HasAgreedToTerms = true;
        await Content.FadeTo(0, 300, Easing.CubicIn);
        // Navigate to the permissions setup screen (Figma Page 3) before entering the app.
        await Shell.Current.GoToAsync("permissions-setup", animate: false);
    }
}
