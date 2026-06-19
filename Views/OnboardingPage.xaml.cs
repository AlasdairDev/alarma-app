using AlarmaApp.Services;
using System.Collections.ObjectModel;
using System.Windows.Input;

namespace AlarmaApp.Views;

public class OnboardingSlide
{
    public string ImageSource { get; set; } = "";
    public bool IsLastSlide { get; set; }
}

public partial class OnboardingPage : ContentPage
{
    private readonly PreferencesService _preferencesService;
    private bool _finishing;
    private bool _showTermsPopup;
    private bool _isAgreed;

    // Full-design PNGs: the title, subtitle, dots and Skip/Next pills are baked into each image.
    public ObservableCollection<OnboardingSlide> Slides { get; } = new()
    {
        new OnboardingSlide { ImageSource = "onboarding_full_1.png" },
        new OnboardingSlide { ImageSource = "onboarding_full_2.png" },
        new OnboardingSlide { ImageSource = "onboarding_full_3.png" },
        new OnboardingSlide { ImageSource = "onboarding_full_4.png", IsLastSlide = true },
    };

    public bool ShowTermsPopup
    {
        get => _showTermsPopup;
        set { _showTermsPopup = value; OnPropertyChanged(); }
    }

    public bool IsAgreed
    {
        get => _isAgreed;
        set { _isAgreed = value; OnPropertyChanged(); }
    }

    public ICommand NextCommand { get; }
    public ICommand SkipCommand { get; }
    public ICommand GetStartedCommand { get; }
    public ICommand CloseTermsCommand { get; }

    public OnboardingPage(PreferencesService preferencesService)
    {
        _preferencesService = preferencesService;

        NextCommand = new Command(() =>
        {
            // On the final slide, Next opens the Terms & Conditions popup instead of advancing.
            if (carousel.Position < Slides.Count - 1)
                carousel.ScrollTo(carousel.Position + 1, position: ScrollToPosition.Center, animate: false);
            else
                ShowTermsPopup = true;
        });

        SkipCommand = new Command(() =>
        {
            // Skip jumps straight to the last slide; on the last slide it opens the popup.
            if (carousel.Position < Slides.Count - 1)
                carousel.ScrollTo(Slides.Count - 1, position: ScrollToPosition.Center, animate: false);
            else
                ShowTermsPopup = true;
        });

        GetStartedCommand = new Command(async () => await OnGetStartedAsync());
        CloseTermsCommand = new Command(() => ShowTermsPopup = false);

        BindingContext = this;
        InitializeComponent();
    }

    private async void OnTermsLinkTapped(object? sender, TappedEventArgs e)
        => await Navigation.PushModalAsync(new TermsAndPrivacyView(initialTab: 0), animated: false);

    private async void OnPrivacyPolicyLinkTapped(object? sender, TappedEventArgs e)
        => await Navigation.PushModalAsync(new TermsAndPrivacyView(initialTab: 1), animated: false);

    private async Task OnGetStartedAsync()
    {
        // The checkbox must be ticked before onboarding can complete.
        if (!IsAgreed)
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
