using AlarmaApp.Services;
using System.Collections.ObjectModel;
using System.Windows.Input;

namespace AlarmaApp.Views;

public class OnboardingSlide
{
    public string ImageSource { get; set; } = "";
    public bool IsLastSlide { get; set; }
}

public class OnboardingTemplateSelector : DataTemplateSelector
{
    public DataTemplate ImageTemplate { get; set; } = null!;
    public DataTemplate CardTemplate { get; set; } = null!;

    protected override DataTemplate OnSelectTemplate(object item, BindableObject container)
        => item is OnboardingSlide { IsLastSlide: true } ? CardTemplate : ImageTemplate;
}

public partial class OnboardingView : ContentPage
{
    private readonly PreferencesService _preferencesService;
    private bool _finishing;
    private bool _isAgreed;

    public ObservableCollection<OnboardingSlide> Slides { get; } = new()
    {
        new OnboardingSlide { ImageSource = "tutorial1.png" },
        new OnboardingSlide { ImageSource = "tutorial2.png" },
        new OnboardingSlide { ImageSource = "tutorial3.png" },
        new OnboardingSlide { ImageSource = "tutorial4.png", IsLastSlide = true },
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
        => await FinishOnboardingAsync();

    private async Task FinishOnboardingAsync()
    {
        if (_finishing) return;
        _finishing = true;
        _preferencesService.HasSeenTutorial = true;
        _preferencesService.HasAgreedToTerms = true;
        await Content.FadeTo(0, 300, Easing.CubicIn);
        await Shell.Current.GoToAsync("permissions-setup", animate: false);
    }
}
