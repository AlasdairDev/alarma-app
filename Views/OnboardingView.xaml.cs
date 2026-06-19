using AlarmaApp.Services;
using System.Collections.ObjectModel;
using System.Windows.Input;

namespace AlarmaApp.Views;

public class OnboardingSlide
{
    public string ImageSource { get; set; } = "";
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public bool IsLastSlide { get; set; }
}

public partial class OnboardingView : ContentPage
{
    private readonly PreferencesService _preferencesService;
    private bool _finishing;
    private bool _isAgreed;

    public ObservableCollection<OnboardingSlide> Slides { get; } = new()
    {
        new OnboardingSlide
        {
            ImageSource = "onboarding_1.jpg",
            Title = "Welcome to Alarma.",
            Subtitle = "Never miss your stop again.",
        },
        new OnboardingSlide
        {
            ImageSource = "onboarding_2.jpg",
            Title = "Set Your Destination",
            Subtitle = "Pick a spot on the map and track your destination in real-time.",
        },
        new OnboardingSlide
        {
            ImageSource = "onboarding_3.jpg",
            Title = "Rest Easy",
            Subtitle = "Close your eyes and let us handle the rest. Wake up to personalized sounds and vibrations.",
        },
        new OnboardingSlide
        {
            ImageSource = "onboarding_4.jpg",
            Title = "Smart Travel Safety",
            Subtitle = "Get adaptive ETA alarms, travel stop alerts, overshoot detection, and SOS features.",
            IsLastSlide = true,
        },
    };

    public bool IsAgreed
    {
        get => _isAgreed;
        set { _isAgreed = value; OnPropertyChanged(); }
    }

    public ICommand NextCommand { get; }
    public ICommand SkipCommand { get; }

    public OnboardingView(PreferencesService preferencesService)
    {
        _preferencesService = preferencesService;
        NextCommand = new Command(() =>
        {
            if (OnboardingCarousel.Position < 3)
                OnboardingCarousel.ScrollTo(OnboardingCarousel.Position + 1, position: ScrollToPosition.Center, animate: false);
        });
        SkipCommand = new Command(() =>
        {
            OnboardingCarousel.ScrollTo(3, position: ScrollToPosition.Center, animate: false);
        });
        BindingContext = this;
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        Content.Opacity = 0;
        base.OnAppearing();
        await Content.FadeTo(1, 500, Easing.CubicOut);
    }

    private void OnAgreeCheckChanged(object? sender, CheckedChangedEventArgs e)
        => IsAgreed = e.Value;

    private async void OnTermsLinkTapped(object? sender, TappedEventArgs e)
        => await Navigation.PushModalAsync(new TermsAndPrivacyView(initialTab: 0), animated: false);

    private async void OnPrivacyPolicyLinkTapped(object? sender, TappedEventArgs e)
        => await Navigation.PushModalAsync(new TermsAndPrivacyView(initialTab: 1), animated: false);

    private async void OnGetStartedClicked(object? sender, EventArgs e)
    {
        // Gate on the agreement checkbox: warn instead of silently doing nothing.
        if (!IsAgreed)
        {
            await DisplayAlert(
                "Agreement Required",
                "Please read and agree to the Terms and Conditions and Privacy Policy to continue.",
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
        await Content.FadeTo(0, 300, Easing.CubicIn);
        await Shell.Current.GoToAsync("//home", animate: false);
    }
}
