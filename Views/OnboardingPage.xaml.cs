using AlarmaApp.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace AlarmaApp.Views;

public class OnboardingSlide : INotifyPropertyChanged
{
    public string ImageSource { get; set; } = "";
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public bool IsLastSlide { get; set; }

    // Two-way bound to the agreement checkbox on the final slide.
    private bool _termsAccepted;
    public bool TermsAccepted
    {
        get => _termsAccepted;
        set { _termsAccepted = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public partial class OnboardingPage : ContentPage
{
    private readonly PreferencesService _preferencesService;
    private bool _finishing;

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

    public ICommand NextCommand { get; }
    public ICommand SkipCommand { get; }
    public ICommand GetStartedCommand { get; }

    public OnboardingPage(PreferencesService preferencesService)
    {
        _preferencesService = preferencesService;

        NextCommand = new Command(() =>
        {
            if (carousel.Position < Slides.Count - 1)
                carousel.ScrollTo(carousel.Position + 1, position: ScrollToPosition.Center, animate: false);
        });
        SkipCommand = new Command(() =>
            carousel.ScrollTo(Slides.Count - 1, position: ScrollToPosition.Center, animate: false));
        GetStartedCommand = new Command(async () => await OnGetStartedAsync());

        BindingContext = this;
        InitializeComponent();

        // Wire the page-level indicator to the carousel so the dots track position.
        carousel.IndicatorView = indicator;
    }

    private async Task OnGetStartedAsync()
    {
        var lastSlide = Slides.FirstOrDefault(s => s.IsLastSlide);
        if (lastSlide is null || !lastSlide.TermsAccepted)
        {
            await DisplayAlert(
                "Agreement Required",
                "Please agree to the Terms and Privacy Policy to continue.",
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
