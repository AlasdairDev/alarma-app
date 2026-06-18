// security notes:
// SOS has a 30s cooldown so it can't be spammed. onboarding + perms are gated in
// HomeView.OnAppearing before we init anything.
// inputs are capped (query 200, contact name 50) and phone numbers run through PhoneRegex.
// map labels go through JsonSerializer (no XSS), coords are always InvariantCulture F6.
// backup validates before clearing, so a junk backup can't wipe real data.
// alarm sound / lead mins / vibration are all checked against allowed values.

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Input;
using AlarmaApp.Models;
using AlarmaApp.Services;
using AlarmaApp.Services.Interfaces;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;

namespace AlarmaApp.Controllers;

public class HomeController : INotifyPropertyChanged
{
    private const double EarthRadiusMeters = 6_371_000;
    private const double MetersPerKilometer = 1000;
    private const double ArrivalThresholdMeters = 200;
    // Keeps the Stage-2 ring a safe margin outside the arrival/Emergency ring so the escalation order
    // (Stage 1 → Stage 2 → Emergency) can never invert when the trigger radius is small or floored.
    private const double Stage2BufferMeters = 100;
    private const double OvershootBufferMeters = 250;
    private const double OvershootThresholdMeters = ArrivalThresholdMeters + OvershootBufferMeters;
    // Route-deviation detection (multi-leg-commute aware). A flat buffer is too tight for city
    // transfers, so the buffer is max(base, fraction × closest approach). Deviation is only armed
    // once reasonably close, requires sustained movement away, and re-baselines on a transfer dwell.
    private const double DeviationBaseBufferMeters = 400;
    private const double DeviationProportionalFraction = 0.5;
    private const double DeviationArmRadiusMeters = 3000;
    private const double DwellSpeedThresholdMetersPerSecond = 0.5;
    private const int DeviationPersistenceFixes = 4;
    private const int TripHistoryLimit = 20;
    private const int MaxSavedRoutes = 5;
    private const int MaxEmergencyContacts = 3;
    // Floor for the Stage-1 lead distance — the alarm never arms closer than this regardless of speed.
    private const double MinAlarmDistanceMeters = 300;
    // Ceiling for the lead distance — even a (possibly GPS-spiked) high speed can't arm the alarm more
    // than this far out, so the alarm can't "fire at incorrect times" kilometres from the stop.
    private const double MaxAlarmDistanceMeters = 5000;
    // Per-fix movement below this (or below the fix's own accuracy) is treated as GPS jitter and
    // excluded from the accumulated trip distance.
    private const double MinSegmentMeters = 8;
    // Arrival (Stage 2) must be seen on this many consecutive fixes before it latches, so a single
    // outlier fix that lands near the destination can't falsely mark arrival (and then overshoot).
    private const int ArrivalPersistenceFixes = 2;
    // How many consecutive increasing-distance fixes past the stop confirm a genuine overshoot.
    private const int OvershootIncreasePersistenceFixes = 3;

    private readonly DatabaseService _databaseService;
    private readonly PreferencesService _preferencesService;
    private readonly PermissionsService _permissionsService;
    private readonly GeocodingService _geocodingService;
    private readonly IConnectivityService _connectivityService;
    private readonly ILocationService _locationService;
    private readonly ISmsService _smsService;
    private readonly IGoogleMapsLauncher _googleMapsLauncher;
    private readonly IAlarmNotificationService _notificationService;
    private readonly IAlarmAudioService _alarmAudioService;
    private readonly BackupService _backupService;
    private readonly IBatteryOptimizationService _batteryOptimizationService;
    private readonly IEarphoneService _earphoneService;

    private readonly ObservableCollection<EmergencyContact> _emergencyContacts = new();
    private readonly ObservableCollection<SavedRoute> _savedRoutes = new();
    private readonly ObservableCollection<TripHistory> _tripHistoryEntries = new();
    // Unfiltered snapshot of what's loaded from the DB. The collection above is the filtered view shown
    // in the list; we filter from this so clearing the search box restores the full list without a re-query.
    private readonly List<TripHistory> _allTripHistory = new();
    private readonly ObservableCollection<GeocodingResult> _searchResults = new();
    private readonly ObservableCollection<string> _vibrationIntensityOptions = new() { "Low", "Medium", "High" };

    private string _statusText = "Loading...";
    private string _connectivityText = string.Empty;
    private string _lastActionText = string.Empty;
    private string _destinationQuery = string.Empty;
    private string _destinationSummaryText = "Search";
    private string _distanceToDestinationText = string.Empty;
    private string _availabilityStatusText = string.Empty;
    private string _batteryOptimizationStatusText = string.Empty;
    private string _earphoneStatusText = string.Empty;
    private string _backupStatusText = string.Empty;
    private string _lastBackupText = "No backup created yet.";
    private GeocodingResult? _lastDestinationResult;
    private HtmlWebViewSource _mapHtmlSource = BuildDefaultMapHtml();
    private bool _isTracking;
    private string _trackingStatusText = "Tracking inactive.";
    private LocationSnapshot? _lastTrackedLocation;
    private double _totalDistanceMeters;
    private TripHistory? _activeTrip;
    private bool _hasArrivedAtDestination;
    private bool _overshootAlerted;
    // Consecutive "getting farther past the stop" tracking — overshoot only confirms after several
    // increasing fixes, so a single GPS wobble right at the destination can't trip the recovery flow.
    private int _overshootIncreaseStreak;
    private double _lastOvershootDistance = double.MaxValue;
    private bool _routeDeviationAlerted;
    private int _deviationAwayStreak;
    private int _arrivalStreak;
    private bool _isOvershootPending;
    private bool _isOvershootConfirmed;
    // The two new recovery-flow screens that follow a confirmed overshoot.
    private bool _isAreaSafetyVisible;
    private bool _isReroutingVisible;
    private string _areaSafetyText = string.Empty;
    private string _reroutingHeadingText = string.Empty;
    private readonly ObservableCollection<string> _reroutingSteps = new();
    private string _overshootDistanceText = string.Empty;
    private string _chipDistanceText = string.Empty;
    private AlarmStage _currentAlarmStage = AlarmStage.None;

    public AlarmStage CurrentAlarmStage
    {
        get => _currentAlarmStage;
        private set
        {
            if (SetProperty(ref _currentAlarmStage, value))
            {
                OnPropertyChanged(nameof(AlarmStageLabel));
                OnPropertyChanged(nameof(AlarmStageTitle));
                OnPropertyChanged(nameof(AlarmStageBody));
                OnPropertyChanged(nameof(IsAlarmActive));
                OnPropertyChanged(nameof(IsNotAlarmActive));
                OnPropertyChanged(nameof(IsStage3Active));
                OnPropertyChanged(nameof(IsStage3Wake));
                OnPropertyChanged(nameof(IsStage1Active));
                OnPropertyChanged(nameof(IsStage1Or2Active));
                OnPropertyChanged(nameof(IsChipVisible));
                OnPropertyChanged(nameof(TopStatusLabel));
            }
        }
    }

    public bool IsOvershootPending
    {
        get => _isOvershootPending;
        private set
        {
            if (SetProperty(ref _isOvershootPending, value))
            {
                OnPropertyChanged(nameof(IsStage3Wake));
                OnPropertyChanged(nameof(IsChipVisible));
                OnPropertyChanged(nameof(TopStatusLabel));
            }
        }
    }

    public bool IsOvershootConfirmed
    {
        get => _isOvershootConfirmed;
        private set
        {
            if (SetProperty(ref _isOvershootConfirmed, value))
            {
                OnPropertyChanged(nameof(IsStage3Wake));
                OnPropertyChanged(nameof(IsChipVisible));
                OnPropertyChanged(nameof(TopStatusLabel));
            }
        }
    }

    public bool IsStage3Wake => IsStage3Active
        && !_isOvershootPending && !_isOvershootConfirmed
        && !_isAreaSafetyVisible && !_isReroutingVisible;

    // ── Overshoot recovery flow screens ───────────────────────────────────────
    public bool IsAreaSafetyVisible
    {
        get => _isAreaSafetyVisible;
        private set
        {
            if (SetProperty(ref _isAreaSafetyVisible, value))
            {
                OnPropertyChanged(nameof(IsStage3Wake));
                OnPropertyChanged(nameof(IsChipVisible));
                OnPropertyChanged(nameof(TopStatusLabel));
            }
        }
    }

    public bool IsReroutingVisible
    {
        get => _isReroutingVisible;
        private set
        {
            if (SetProperty(ref _isReroutingVisible, value))
            {
                OnPropertyChanged(nameof(IsStage3Wake));
                OnPropertyChanged(nameof(IsChipVisible));
                OnPropertyChanged(nameof(TopStatusLabel));
            }
        }
    }

    public string AreaSafetyText
    {
        get => _areaSafetyText;
        private set => SetProperty(ref _areaSafetyText, value);
    }

    public string ReroutingHeadingText
    {
        get => _reroutingHeadingText;
        private set => SetProperty(ref _reroutingHeadingText, value);
    }

    public ObservableCollection<string> ReroutingSteps => _reroutingSteps;

    public string OvershootDistanceText
    {
        get => _overshootDistanceText;
        private set => SetProperty(ref _overshootDistanceText, value);
    }

    public string ChipDistanceText
    {
        get => _chipDistanceText;
        private set => SetProperty(ref _chipDistanceText, value);
    }

    public bool IsChipVisible => !IsStage3Wake
        && !_isOvershootPending && !_isOvershootConfirmed
        && !_isAreaSafetyVisible && !_isReroutingVisible;

    public string TopStatusLabel => (IsOvershootPending || IsOvershootConfirmed || IsAreaSafetyVisible || IsReroutingVisible)
        ? "Overshoot Detected"
        : AlarmStageLabel;
    private double _lastSpeedMetersPerSecond;
    private double _minDistanceToDestination = double.MaxValue;

    private string _newContactName = string.Empty;
    private string _newContactNumber = string.Empty;
    private string _newRouteName = string.Empty;
    private string _alarmSound;
    private double _alarmLeadMinutes;
    private bool _vibrationOnly;
    private string _vibrationIntensity;
    private bool _isOnboardingComplete;
    private bool _isDatabaseInitialized;
    private bool _hasInitialized;
    private string _currentLocationText = "Fetching location...";
    private readonly SemaphoreSlim _initializeSemaphore = new(1, 1);
    private readonly SemaphoreSlim _databaseInitSemaphore = new(1, 1);
    // Exactly five clearly-distinct alarm voices. The audio service maps each name to a different, loud
    // system sound URI (enumerated from the device's ringtone catalogue) so the live preview in Settings
    // sounds obviously different between every option.
    private readonly ObservableCollection<string> _alarmSoundOptions = new()
    {
        "Default",
        "Alarm",
        "Chime",
        "Bell",
        "Siren"
    };
    private bool _wasOnline;
    private bool _availabilityChecked;
    private string _primaryContactNumber = string.Empty;

    private DateTimeOffset? _lastSosSentAt;
    // Re-entrancy latch for the SOS dispatch. It guards against a second press landing while an SMS send
    // is still in flight; the finally block in SendSosAsync ALWAYS clears it, so the button can never get
    // stuck "stuck on" after the first press (the single-fire bug).
    private bool _isSendingSos;
    private static readonly TimeSpan SosCooldown = TimeSpan.FromSeconds(30);
    private const int MaxContactNameLength = 50;
    private const int MaxDisplayNameLength = 200;

    private string _locationPermissionLabel = "Check";
    private string _notificationPermissionLabel = "Check";
    private string _bluetoothPermissionLabel = "Settings";

    private DateTime _lastMapLocationUpdate = DateTime.MinValue;
    // Push the live dot to the map roughly as often as a fresh GPS fix arrives. The WebView animates
    // each hop, so a 2s cadence keeps the dot feeling alive without hammering EvaluateJavaScript.
    private static readonly TimeSpan MapLocationUpdateInterval = TimeSpan.FromSeconds(2);
    private CancellationTokenSource? _searchCts;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<AlarmStage>? AlarmStageActivated;
    public event EventHandler<(double Lat, double Lon)>? LiveLocationUpdated;
    public event EventHandler<(double Lat, double Lon)>? CenterMapRequested;
    // JS string we run against the live WebView map. HomeView replays state on re-appear via
    // LastDestinationResult.
    public event EventHandler<string>? MapJsRequested;
    public event EventHandler? NavigateToAddFavoriteRequested;
    public event EventHandler? FavoriteSaved;
    public event EventHandler? SosDispatched;
    public event EventHandler? SmsDenied;
    // Raised when adding an emergency contact fails validation, so the view can show a styled
    // DisplayAlert modal instead of a quiet inline line of text.
    public event EventHandler<string>? EmergencyContactValidationFailed;
    // Raised when a trip start is blocked because the device's master location switch is off.
    // The view turns this into a "go to Settings" prompt (the controller has no Page of its own).
    public event EventHandler? LocationServicesDisabled;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string ConnectivityText
    {
        get => _connectivityText;
        private set
        {
            if (SetProperty(ref _connectivityText, value))
                OnPropertyChanged(nameof(IsOffline));
        }
    }

    public bool IsOffline => _connectivityText.Contains("Offline", StringComparison.OrdinalIgnoreCase);

    public string LastActionText
    {
        get => _lastActionText;
        private set => SetProperty(ref _lastActionText, value);
    }

    public HtmlWebViewSource MapHtmlSource
    {
        get => _mapHtmlSource;
        private set => SetProperty(ref _mapHtmlSource, value);
    }

    // lets HomeView replay setDestination() on re-appear without reloading the WebView.
    // null = no destination marker.
    public GeocodingResult? LastDestinationResult => _lastDestinationResult;

    public bool IsTracking
    {
        get => _isTracking;
        private set
        {
            if (SetProperty(ref _isTracking, value))
            {
                OnPropertyChanged(nameof(IsNotTracking));
                OnPropertyChanged(nameof(ShowStartTripCard));
                OnPropertyChanged(nameof(ShowGreetingHeader));
            }
        }
    }

    public bool IsNotTracking => !IsTracking;

    public bool ShowStartTripCard => HasDestination && !IsTracking;

    public bool ShowGreetingHeader => !ShowStartTripCard;

    public string TrackingStatusText
    {
        get => _trackingStatusText;
        private set => SetProperty(ref _trackingStatusText, value);
    }

    public string DestinationQuery
    {
        get => _destinationQuery;
        set
        {
            if (SetProperty(ref _destinationQuery, value))
                OnPropertyChanged(nameof(ShowNoResults));
        }
    }

    public string DestinationSummaryText
    {
        get => _destinationSummaryText;
        private set
        {
            if (SetProperty(ref _destinationSummaryText, value))
                OnPropertyChanged(nameof(DestinationShortNameText));
        }
    }

    public string DistanceToDestinationText
    {
        get => _distanceToDestinationText;
        private set => SetProperty(ref _distanceToDestinationText, value);
    }

    public string DestinationShortNameText => _lastDestinationResult is { DisplayName: var n }
        ? (n.Length > 28 ? n[..28] + "…" : n)
        : string.Empty;

    public string CurrentLocationText
    {
        get => _currentLocationText;
        private set => SetProperty(ref _currentLocationText, value);
    }

    public string AvailabilityStatusText
    {
        get => _availabilityStatusText;
        private set => SetProperty(ref _availabilityStatusText, value);
    }

    public string BatteryOptimizationStatusText
    {
        get => _batteryOptimizationStatusText;
        private set => SetProperty(ref _batteryOptimizationStatusText, value);
    }

    public string EarphoneStatusText
    {
        get => _earphoneStatusText;
        private set
        {
            if (SetProperty(ref _earphoneStatusText, value))
                OnPropertyChanged(nameof(IsEarphonesConnected));
        }
    }

    public bool IsEarphonesConnected =>
        _earphoneStatusText.StartsWith("Earphones connected:", StringComparison.OrdinalIgnoreCase);

    public string Greeting => DateTime.Now.Hour switch
    {
        >= 5 and < 12 => "Good Morning,",
        >= 12 and < 18 => "Good Afternoon,",
        _ => "Good Evening,"
    };

    public string AlarmStageLabel => CurrentAlarmStage switch
    {
        AlarmStage.Stage1 => "Alarm Stage 1",
        AlarmStage.Stage2 => "Alarm Stage 2",
        AlarmStage.Stage3 => "Alarm Stage 3",
        _ => "Trip in Progress"
    };

    public string AlarmStageTitle => CurrentAlarmStage switch
    {
        AlarmStage.Stage1 => "Approaching Stop",
        AlarmStage.Stage2 => "Get Ready",
        AlarmStage.Stage3 => "WAKE UP",
        _ => "Tracking Active"
    };

    public string AlarmStageBody => CurrentAlarmStage switch
    {
        AlarmStage.Stage1 => "Get ready to go down.",
        AlarmStage.Stage2 => "You are near your destination.",
        AlarmStage.Stage3 => "YOU'VE REACHED YOUR STOP.",
        _ => DistanceToDestinationText
    };

    public bool IsAlarmActive => CurrentAlarmStage != AlarmStage.None;
    public bool IsNotAlarmActive => !IsAlarmActive;
    public bool IsStage3Active => CurrentAlarmStage == AlarmStage.Stage3;
    public bool IsStage1Active => CurrentAlarmStage == AlarmStage.Stage1;
    public bool IsStage1Or2Active => CurrentAlarmStage == AlarmStage.Stage1 || CurrentAlarmStage == AlarmStage.Stage2;

    public string LocationPermissionLabel
    {
        get => _locationPermissionLabel;
        private set => SetProperty(ref _locationPermissionLabel, value);
    }

    public string NotificationPermissionLabel
    {
        get => _notificationPermissionLabel;
        private set => SetProperty(ref _notificationPermissionLabel, value);
    }

    public string BatteryOptimizationLabel =>
        _batteryOptimizationService.IsIgnoringOptimizations() ? "Allowed" : "Required";

    public string BluetoothPermissionLabel
    {
        get => _bluetoothPermissionLabel;
        private set => SetProperty(ref _bluetoothPermissionLabel, value);
    }

    public string BackupStatusText
    {
        get => _backupStatusText;
        private set => SetProperty(ref _backupStatusText, value);
    }

    public string LastBackupText
    {
        get => _lastBackupText;
        private set => SetProperty(ref _lastBackupText, value);
    }

    public string NewContactName
    {
        get => _newContactName;
        set => SetProperty(ref _newContactName, value);
    }

    public string NewContactNumber
    {
        get => _newContactNumber;
        set => SetProperty(ref _newContactNumber, value);
    }

    public string NewRouteName
    {
        get => _newRouteName;
        set => SetProperty(ref _newRouteName, value);
    }

    public string AlarmSound
    {
        get => _alarmSound;
        set
        {
            var normalized = NormalizeSoundKey(value);
            if (SetProperty(ref _alarmSound, normalized))
            {
                _preferencesService.AlarmSound = normalized;
            }
        }
    }

    public double AlarmLeadMinutes
    {
        get => _alarmLeadMinutes;
        set
        {
            var clamped = Math.Clamp(value, 1, 60);
            if (SetProperty(ref _alarmLeadMinutes, clamped))
            {
                _preferencesService.AlarmLeadMinutes = (int)clamped;
            }
        }
    }

    public bool VibrationOnly
    {
        get => _vibrationOnly;
        set
        {
            if (SetProperty(ref _vibrationOnly, value))
            {
                _preferencesService.VibrationOnly = value;
            }
        }
    }

    public ObservableCollection<string> AlarmSoundOptions => _alarmSoundOptions;
    public ObservableCollection<string> VibrationIntensityOptions => _vibrationIntensityOptions;
    public ObservableCollection<GeocodingResult> SearchResults => _searchResults;

    public bool HasSearchResults => _searchResults.Count > 0;

    public bool IsSearchingDestination
    {
        get => _isSearchingDestination;
        private set
        {
            if (SetProperty(ref _isSearchingDestination, value))
                OnPropertyChanged(nameof(ShowNoResults));
        }
    }

    private bool _isSearchingDestination;

    public bool ShowNoResults =>
        !HasSearchResults && !string.IsNullOrWhiteSpace(_destinationQuery) && !IsSearchingDestination;

    public string VibrationIntensity
    {
        get => _vibrationIntensity;
        set
        {
            var valid = _vibrationIntensityOptions.Contains(value, StringComparer.OrdinalIgnoreCase)
                ? _vibrationIntensityOptions.First(o => o.Equals(value, StringComparison.OrdinalIgnoreCase))
                : "Medium";
            if (SetProperty(ref _vibrationIntensity, valid))
                _preferencesService.VibrationIntensity = valid;
        }
    }

    public bool IsOnboardingComplete
    {
        get => _isOnboardingComplete;
        private set
        {
            if (SetProperty(ref _isOnboardingComplete, value))
            {
                OnPropertyChanged(nameof(ShowOnboardingPrompt));
                OnPropertyChanged(nameof(OnboardingStatusText));
            }
        }
    }

    public bool ShowOnboardingPrompt => !IsOnboardingComplete;

    public string OnboardingStatusText => IsOnboardingComplete
        ? "Onboarding complete."
        : "Onboarding pending: add an emergency contact and confirm alarm settings.";

    public bool HasDestination => _lastDestinationResult is not null;

    public bool HasEmergencyContacts => _emergencyContacts.Count > 0;

    public bool HasNoEmergencyContacts => !HasEmergencyContacts;

    public bool HasSavedRoutes => _savedRoutes.Count > 0;

    public bool HasTripHistory => _tripHistoryEntries.Count > 0;
    public bool HasNoTripHistory => !HasTripHistory;
    public bool HasNoSavedRoutes => !HasSavedRoutes;

    public bool CanSendSos => HasEmergencyContacts || !string.IsNullOrWhiteSpace(_primaryContactNumber);

    public string SosHoldPrompt
    {
        get
        {
            var count = _emergencyContacts.Count;
            return count > 0
                ? $"Hold for 2 seconds to notify {count} contact{(count == 1 ? "" : "s")}"
                : "Hold for 2 seconds to send SOS";
        }
    }

    public bool CanSaveRoute => _lastDestinationResult is not null;

    public bool IsDestinationSaved =>
        _lastDestinationResult is not null &&
        _savedRoutes.Any(r => IsSameDestination(r, _lastDestinationResult));

    public bool HasBackupAvailable => _preferencesService.LastBackupUtc.HasValue;

    public ObservableCollection<EmergencyContact> EmergencyContacts => _emergencyContacts;

    public ObservableCollection<SavedRoute> SavedRoutes => _savedRoutes;

    public ObservableCollection<TripHistory> TripHistoryEntries => _tripHistoryEntries;

    // Bound two-way to the Trip History search box. Entry pushes each keystroke through here, so the
    // list re-filters live as the user types — no separate command or button needed.
    public string HistorySearchQuery
    {
        get => _historySearchQuery;
        set
        {
            if (SetProperty(ref _historySearchQuery, value))
                ApplyTripHistoryFilter();
        }
    }
    private string _historySearchQuery = string.Empty;

    // Which quick-filter chip is active ("All", "Recent", "By Route"). Layered on top of the free-text
    // search box — the two narrow the list together. Setting it re-runs the filter so the chips feel
    // instant, and the chip UI lights up by binding its DataTrigger against this same string.
    public string HistoryFilterCategory
    {
        get => _historyFilterCategory;
        set
        {
            if (SetProperty(ref _historyFilterCategory, value))
                ApplyTripHistoryFilter();
        }
    }
    private string _historyFilterCategory = "All";

    public ICommand InitializeCommand { get; }
    public ICommand SearchDestinationCommand { get; }
    public ICommand SelectResultCommand { get; }
    public ICommand OpenMapsCommand { get; }
    public ICommand TriggerSosCommand { get; }
    public ICommand StartTrackingCommand { get; }
    public ICommand StopTrackingCommand { get; }
    public ICommand AddEmergencyContactCommand { get; }
    public ICommand SetPrimaryContactCommand { get; }
    public ICommand RemoveEmergencyContactCommand { get; }
    public ICommand SaveDestinationCommand { get; }
    public ICommand ToggleFavoriteCommand { get; }
    public ICommand AddRouteToFavoritesCommand { get; }
    public ICommand SaveSelectedRouteToFavoritesCommand { get; }
    public ICommand ApplySavedRouteCommand { get; }
    public ICommand RemoveSavedRouteCommand { get; }
    public ICommand CompleteOnboardingCommand { get; }
    public ICommand RefreshAvailabilityCommand { get; }
    public ICommand ExportBackupCommand { get; }
    public ICommand RestoreBackupCommand { get; }
    public ICommand RefreshEarphoneStatusCommand { get; }
    public ICommand RequestBatteryOptimizationCommand { get; }
    public ICommand DismissAlarmCommand { get; }
    public ICommand CenterOnUserCommand { get; }
    public ICommand RequestLocationPermissionCommand { get; }
    public ICommand RequestNotificationPermissionCommand { get; }
    public ICommand RequestBluetoothPermissionCommand { get; }
    public ICommand ConfirmOvershootCommand { get; }
    public ICommand DismissOvershootCommand { get; }
    public ICommand OpenInGMapsCommand { get; }
    public ICommand CloseOvershootCommand { get; }
    public ICommand ContinueToReroutingCommand { get; }
    public ICommand FinishReroutingCommand { get; }
    public ICommand DeleteTripHistoryCommand { get; }
    public ICommand ClearTripHistoryCommand { get; }
    public ICommand SetHistoryFilterCommand { get; }
    public ICommand SelectAndPreviewSoundCommand { get; }

    public HomeController(
        DatabaseService databaseService,
        PreferencesService preferencesService,
        PermissionsService permissionsService,
        GeocodingService geocodingService,
        IConnectivityService connectivityService,
        ILocationService locationService,
        ISmsService smsService,
        IGoogleMapsLauncher googleMapsLauncher,
        IAlarmNotificationService notificationService,
        IAlarmAudioService alarmAudioService,
        BackupService backupService,
        IBatteryOptimizationService batteryOptimizationService,
        IEarphoneService earphoneService)
    {
        _databaseService = databaseService;
        _preferencesService = preferencesService;
        _permissionsService = permissionsService;
        _geocodingService = geocodingService;
        _connectivityService = connectivityService;
        _locationService = locationService;
        _smsService = smsService;
        _googleMapsLauncher = googleMapsLauncher;
        _notificationService = notificationService;
        _alarmAudioService = alarmAudioService;
        _backupService = backupService;
        _batteryOptimizationService = batteryOptimizationService;
        _earphoneService = earphoneService;

        var normalizedSound = NormalizeSoundKey(_preferencesService.AlarmSound);
        _alarmSound = normalizedSound;
        if (!string.Equals(_preferencesService.AlarmSound, normalizedSound, StringComparison.Ordinal))
        {
            _preferencesService.AlarmSound = normalizedSound;
        }

        var storedLeadMinutes = _preferencesService.AlarmLeadMinutes;
        _alarmLeadMinutes = Math.Clamp(storedLeadMinutes, 1, 60);
        if (storedLeadMinutes != (int)_alarmLeadMinutes)
        {
            _preferencesService.AlarmLeadMinutes = (int)_alarmLeadMinutes;
        }
        _vibrationOnly = _preferencesService.VibrationOnly;
        _vibrationIntensity = _preferencesService.VibrationIntensity;
        _isOnboardingComplete = _preferencesService.IsOnboardingComplete;

        InitializeCommand = new Command(async () => await InitializeDatabaseAsync());
        SearchDestinationCommand = new Command(async () => await SearchDestinationAsync());
        SelectResultCommand = new Command<GeocodingResult>(async result => await SelectSearchResultAsync(result));
        OpenMapsCommand = new Command(async () => await OpenMapsAsync());
        TriggerSosCommand = new Command(async () => await SendSosAsync());
        StartTrackingCommand = new Command(async () => await StartTrackingAsync());
        StopTrackingCommand = new Command(async () => await StopTrackingAsync());
        AddEmergencyContactCommand = new Command(async () => await AddEmergencyContactAsync());
        SetPrimaryContactCommand = new Command<EmergencyContact>(async contact => await SetPrimaryContactAsync(contact));
        RemoveEmergencyContactCommand = new Command<EmergencyContact>(async contact => await RemoveEmergencyContactAsync(contact));
        SaveDestinationCommand = new Command(async () => await SaveDestinationAsync());
        ToggleFavoriteCommand = new Command(async () => await ToggleFavoriteAsync());
        AddRouteToFavoritesCommand = new Command(async () => await AddRouteToFavoritesAsync());
        SaveSelectedRouteToFavoritesCommand = new Command<GeocodingResult>(async result => await SaveSelectedRouteToFavoritesAsync(result));
        ApplySavedRouteCommand = new Command<SavedRoute>(async route => await ApplySavedRouteAsync(route));
        RemoveSavedRouteCommand = new Command<SavedRoute>(async route => await RemoveSavedRouteAsync(route));
        CompleteOnboardingCommand = new Command(CompleteOnboarding);
        RefreshAvailabilityCommand = new Command(async () => await RefreshAvailabilityAsync());
        ExportBackupCommand = new Command(async () => await ExportBackupAsync());
        RestoreBackupCommand = new Command(async () => await RestoreBackupAsync());
        RefreshEarphoneStatusCommand = new Command(UpdateEarphoneStatus);
        RequestBatteryOptimizationCommand = new Command(async () => await RequestBatteryOptimizationAsync());
        DismissAlarmCommand = new Command(async () =>
        {
            CurrentAlarmStage = AlarmStage.None;
            ResetOvershootUiState();
            await _alarmAudioService.DisableCriticalAudioAsync();
            LastActionText = "Alarm dismissed.";
        });
        CenterOnUserCommand = new Command(async () => await CenterOnUserAsync());
        RequestLocationPermissionCommand = new Command(async () => await RequestLocationPermissionAsync());
        RequestNotificationPermissionCommand = new Command(async () => await RequestNotificationPermissionAsync());
        RequestBluetoothPermissionCommand = new Command(OpenBluetoothSettings);
        ConfirmOvershootCommand = new Command(() =>
        {
            IsOvershootPending = false;
            IsOvershootConfirmed = true;
        });
        DismissOvershootCommand = new Command(async () =>
        {
            ResetOvershootUiState();
            _overshootAlerted = false;
            CurrentAlarmStage = AlarmStage.None;
            await _alarmAudioService.DisableCriticalAudioAsync();
        });
        // Pure local hand-off to Google Maps (google.navigation: intent, zero network). Does NOT reset
        // the recovery flow, so the rider can launch Maps and still come back to the in-app screens.
        OpenInGMapsCommand = new Command(async () =>
        {
            if (_lastDestinationResult is not null)
                await _googleMapsLauncher.OpenRerouteAsync(
                    _lastDestinationResult.Latitude,
                    _lastDestinationResult.Longitude);
        });
        // Step 1 → 2 of the recovery flow: "Close" the confirmed alert. Silence the alarm and surface the
        // Area Safety overlay rather than just dismissing everything.
        CloseOvershootCommand = new Command(async () =>
        {
            await _alarmAudioService.DisableCriticalAudioAsync();
            BuildAreaSafetyMessage();
            IsOvershootConfirmed = false;
            IsAreaSafetyVisible = true;
        });
        // Step 2 → 3: leave the safety overlay and enter the in-app rerouting screen.
        ContinueToReroutingCommand = new Command(() =>
        {
            BuildReroutingGuidance();
            IsAreaSafetyVisible = false;
            IsReroutingVisible = true;
        });
        // Step 3 done: tear the whole recovery flow down and clear the alarm.
        FinishReroutingCommand = new Command(async () =>
        {
            ResetOvershootUiState();
            _overshootAlerted = false;
            CurrentAlarmStage = AlarmStage.None;
            await _alarmAudioService.DisableCriticalAudioAsync();
        });
        DeleteTripHistoryCommand = new Command<TripHistory>(async trip => await DeleteTripHistoryAsync(trip));
        ClearTripHistoryCommand = new Command(async () => await ClearTripHistoryAsync());
        SetHistoryFilterCommand = new Command<string>(category => HistoryFilterCategory =
            string.IsNullOrWhiteSpace(category) ? "All" : category);
        SelectAndPreviewSoundCommand = new Command<string>(async sound => await SelectAndPreviewSoundAsync(sound));

        _locationService.LocationUpdated += OnLocationUpdated;
        _emergencyContacts.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasEmergencyContacts));
            OnPropertyChanged(nameof(HasNoEmergencyContacts));
            OnPropertyChanged(nameof(CanSendSos));
            OnPropertyChanged(nameof(SosHoldPrompt));
        };
        _savedRoutes.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasSavedRoutes));
            OnPropertyChanged(nameof(HasNoSavedRoutes));
            OnPropertyChanged(nameof(IsDestinationSaved));
        };
        _tripHistoryEntries.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasTripHistory));
            OnPropertyChanged(nameof(HasNoTripHistory));
        };
        _searchResults.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasSearchResults));
            OnPropertyChanged(nameof(ShowNoResults));
        };
    }

    public async Task InitializeAsync()
    {
        await _initializeSemaphore.WaitAsync();
        try
        {
            if (_hasInitialized)
            {
                return;
            }

            _hasInitialized = true;
            StatusText = "Ready to configure an offline-first trip.";
            await _notificationService.EnsureAlarmChannelAsync();
            await RefreshAvailabilityAsync();
            UpdateBatteryOptimizationStatus();
            UpdateEarphoneStatus();
            UpdateBackupStatus();
            await UpdatePermissionLabelsAsync();
            await InitializeDatabaseAsync();
            TrackingStatusText = "Tracking inactive.";
        }
        catch (Exception ex)
        {
            BlackBoxLogger.RecordHandledException(ex, "[HomeController.InitializeAsync]");
            StatusText = "Initialization encountered an error.";
            LastActionText = "Initialization failed. Restart the app and try again.";
        }
        finally
        {
            _initializeSemaphore.Release();
        }
    }

    private bool UpdateConnectivityStatus()
    {
        var isOnline = _connectivityService.HasInternet();
        ConnectivityText = isOnline
            ? "Network detected: destination search and OSM tiles are available."
            : "Offline mode: destination search and map tiles are disabled.";
        AvailabilityStatusText = isOnline
            ? "Availability: Online."
            : "Availability: Offline.";
        return isOnline;
    }

    private async Task RefreshAvailabilityAsync()
    {
        var isOnline = UpdateConnectivityStatus();
        if (!_availabilityChecked)
        {
            _availabilityChecked = true;
            _wasOnline = isOnline;
            return;
        }

        if (_wasOnline == isOnline)
        {
            return;
        }

        _wasOnline = isOnline;
        var message = isOnline
            ? "Load availability restored: online services are available."
            : "Load availability lost: offline mode enabled.";
        await _notificationService.ShowTripAlertAsync("Load availability", message);
    }

    private void UpdateBatteryOptimizationStatus()
    {
        BatteryOptimizationStatusText = _batteryOptimizationService.IsIgnoringOptimizations()
            ? "Battery optimization: ignored (recommended for tracking)."
            : "Battery optimization: active. Consider allowing ignore to keep tracking stable.";
        OnPropertyChanged(nameof(BatteryOptimizationLabel));
    }

    private async Task UpdatePermissionLabelsAsync()
    {
        try
        {
            var locStatus = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();
            LocationPermissionLabel = locStatus == PermissionStatus.Granted ? "Granted" : "Required";

            var notifStatus = await Permissions.CheckStatusAsync<PostNotificationsPermission>();
            NotificationPermissionLabel = notifStatus == PermissionStatus.Granted ? "Granted" : "Required";

            OnPropertyChanged(nameof(BatteryOptimizationLabel));

#if ANDROID
            try
            {
                var btManager = Android.App.Application.Context
                    .GetSystemService(Android.Content.Context.BluetoothService)
                    as Android.Bluetooth.BluetoothManager;
                BluetoothPermissionLabel = btManager?.Adapter?.IsEnabled == true ? "On" : "Off";
            }
            catch
            {
                BluetoothPermissionLabel = "Settings";
            }
#endif
        }
        catch (Exception ex)
        {
            BlackBoxLogger.RecordHandledException(ex, "[HomeController.UpdatePermissionLabelsAsync]");
        }
    }

    private void UpdateEarphoneStatus()
    {
        var (isConnected, details) = _earphoneService.GetConnectionStatus();
        EarphoneStatusText = isConnected
            ? $"Earphones connected: {details}"
            : $"Earphones disconnected: {details}";
    }

    private void UpdateBackupStatus()
    {
        if (_preferencesService.LastBackupUtc is null)
        {
            LastBackupText = "No backup created yet.";
            OnPropertyChanged(nameof(HasBackupAvailable));
            return;
        }

        LastBackupText = $"Last backup: {_preferencesService.LastBackupUtc.Value.ToOffset(TimeSpan.FromHours(8)):g} PHT";
        OnPropertyChanged(nameof(HasBackupAvailable));
    }

    // Lets the view send the user to the OS location-source page after the "location is off" prompt.
    public void OpenLocationSettings() => PermissionsService.OpenLocationSettings();

    // Clears the shared status line so a screen (e.g. the contacts form) can show only its own
    // freshly-produced feedback rather than a stale message left over from another screen.
    public void ClearLastAction() => LastActionText = string.Empty;

    private async Task RequestLocationPermissionAsync()
    {
        try
        {
            var granted = await _permissionsService.EnsureLocationPermissionsAsync(requireBackground: false);
            LocationPermissionLabel = granted ? "Granted" : "Required";
            AvailabilityStatusText = granted
                ? "Location permission granted."
                : "Location permission denied. Enable it in Android Settings → Apps → Alarma → Permissions.";
            LastActionText = AvailabilityStatusText;
        }
        catch (Exception ex)
        {
            BlackBoxLogger.RecordHandledException(ex, "[HomeController.RequestLocationPermissionAsync]");
            LastActionText = "Could not request location permission.";
        }
    }

    private async Task RequestNotificationPermissionAsync()
    {
        try
        {
            var granted = await _permissionsService.EnsureNotificationPermissionAsync();
            NotificationPermissionLabel = granted ? "Granted" : "Required";
            LastActionText = granted
                ? "Notification permission granted."
                : "Notification permission denied. Enable it in Android Settings → Apps → Alarma → Permissions.";
        }
        catch (Exception ex)
        {
            BlackBoxLogger.RecordHandledException(ex, "[HomeController.RequestNotificationPermissionAsync]");
            LastActionText = "Could not request notification permission.";
        }
    }

    private static void OpenBluetoothSettings()
    {
#if ANDROID
        try
        {
            var intent = new Android.Content.Intent(Android.Provider.Settings.ActionBluetoothSettings);
            intent.SetFlags(Android.Content.ActivityFlags.NewTask);
            Android.App.Application.Context.StartActivity(intent);
        }
        catch { }
#endif
    }

    private async Task InitializeDatabaseAsync()
    {
        await _databaseInitSemaphore.WaitAsync();
        try
        {
            if (!_isDatabaseInitialized)
            {
                await _databaseService.InitializeAsync();
                _isDatabaseInitialized = true;
                LastActionText = string.Empty;
            }
            else
            {
                LastActionText = string.Empty;
            }

            await LoadLocalDataAsync();
        }
        catch (Exception ex)
        {
            BlackBoxLogger.RecordHandledException(ex, "[HomeController.InitializeDatabaseAsync]");
            LastActionText = "Database initialization failed. Restart the app and try again.";
        }
        finally
        {
            _databaseInitSemaphore.Release();
        }
    }

    private async Task LoadLocalDataAsync()
    {
        await LoadEmergencyContactsAsync();
        await LoadSavedRoutesAsync();
        await LoadTripHistoryAsync();
    }

    private async Task LoadEmergencyContactsAsync()
    {
        try
        {
            var contacts = await _databaseService.GetEmergencyContactsAsync();
            var primaryContacts = contacts.Where(contact => contact.IsPrimary).ToList();
            if (primaryContacts.Count > 1)
            {
                foreach (var extra in primaryContacts.Skip(1))
                {
                    extra.IsPrimary = false;
                    await _databaseService.SaveEmergencyContactAsync(extra);
                }

                contacts = await _databaseService.GetEmergencyContactsAsync();
            }

            var orderedContacts = contacts
                .OrderByDescending(contact => contact.IsPrimary)
                .ThenBy(contact => contact.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            ReplaceCollection(_emergencyContacts, orderedContacts);

            var primary = orderedContacts.FirstOrDefault(contact => contact.IsPrimary);
            _primaryContactNumber = primary?.PhoneNumber ?? string.Empty;
            OnPropertyChanged(nameof(CanSendSos));
        }
        catch (Exception ex)
        {
            BlackBoxLogger.RecordHandledException(ex, "[HomeController.LoadEmergencyContactsAsync]");
            LastActionText = "Failed to load emergency contacts.";
        }
    }

    private async Task LoadSavedRoutesAsync()
    {
        try
        {
            var routes = await _databaseService.GetSavedRoutesAsync();
            var orderedRoutes = routes
                .OrderBy(route => route.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            ReplaceCollection(_savedRoutes, orderedRoutes);
        }
        catch (Exception ex)
        {
            BlackBoxLogger.RecordHandledException(ex, "[HomeController.LoadSavedRoutesAsync]");
            LastActionText = "Failed to load saved routes.";
        }
    }

    public Task RefreshFavoritesAsync() => LoadSavedRoutesAsync();

    private async Task LoadTripHistoryAsync()
    {
        try
        {
            var orderedHistory = await _databaseService.GetTripHistoryAsync(TripHistoryLimit);
            foreach (var trip in orderedHistory)
            {
                trip.StartedAt = DateTime.SpecifyKind(trip.StartedAt, DateTimeKind.Utc).AddHours(8);
                if (trip.EndedAt.HasValue)
                    trip.EndedAt = DateTime.SpecifyKind(trip.EndedAt.Value, DateTimeKind.Utc).AddHours(8);
            }
            _allTripHistory.Clear();
            _allTripHistory.AddRange(orderedHistory);
            ApplyTripHistoryFilter();
        }
        catch (Exception ex)
        {
            BlackBoxLogger.RecordHandledException(ex, "[HomeController.LoadTripHistoryAsync]");
            LastActionText = "Failed to load trip history.";
        }
    }

    private async Task ExportBackupAsync()
    {
        try
        {
            var path = await _backupService.ExportAsync();
            UpdateBackupStatus();
            BackupStatusText = $"Backup exported to {path}.";
        }
        catch (Exception ex)
        {
            BlackBoxLogger.RecordHandledException(ex, "[HomeController.ExportBackupAsync]");
            BackupStatusText = "Backup export failed. Check storage permissions and try again.";
        }
    }

    private async Task RestoreBackupAsync()
    {
        try
        {
            var path = await _backupService.RestoreLatestAsync();
            if (path is null)
            {
                BackupStatusText = "No backup found to restore.";
                return;
            }

            _alarmSound = NormalizeSoundKey(_preferencesService.AlarmSound);
            _preferencesService.AlarmSound = _alarmSound;
            OnPropertyChanged(nameof(AlarmSound));
            AlarmLeadMinutes = _preferencesService.AlarmLeadMinutes;
            VibrationOnly = _preferencesService.VibrationOnly;
            VibrationIntensity = _preferencesService.VibrationIntensity;
            UpdateBackupStatus();
            await LoadLocalDataAsync();
            BackupStatusText = $"Backup restored from {path}.";
        }
        catch (Exception ex)
        {
            BlackBoxLogger.RecordHandledException(ex, "[HomeController.RestoreBackupAsync]");
            BackupStatusText = "Backup restore failed. The backup file may be corrupted or from a different device.";
        }
    }

    private async Task RequestBatteryOptimizationAsync()
    {
        await _batteryOptimizationService.RequestIgnoreOptimizationsAsync();
        UpdateBatteryOptimizationStatus();
    }

    private async Task SearchDestinationAsync()
    {
        if (!_connectivityService.HasInternet())
        {
            LastActionText = "Destination search requires an internet connection.";
            ClearDestination();
            return;
        }

        var query = DestinationQuery?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(query))
        {
            LastActionText = "Enter a destination query to search.";
            ClearDestination();
            return;
        }

        if (query.Length < 3)
        {
            LastActionText = "Enter at least 3 characters to search.";
            return;
        }

        if (query.Length > 200)
        {
            LastActionText = "Search query is too long. Please shorten it.";
            return;
        }

        // Cancel any in-flight HTTP search before starting a new one.
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;

        IsSearchingDestination = true;
        try
        {
            DestinationQuery = query;
            var results = await Task.Run(() => _geocodingService.SearchAsync(query, token), token);
            ReplaceCollection(_searchResults, results);
            LastActionText = results.Any()
                ? $"{results.Count} result(s) found. Tap one to select."
                : "No results. Try a full name (e.g. \"SM Mall of Asia\") or add a city.";
            if (!results.Any())
                ClearDestination();
        }
        catch (OperationCanceledException)
        {
            // Search was superseded by a newer query — silently drop stale results.
        }
        catch (Exception ex)
        {
            BlackBoxLogger.RecordHandledException(ex, "[HomeController.SearchDestinationAsync]");
            LastActionText = "Search failed. Check your internet connection and try again.";
            ClearDestination();
        }
        finally
        {
            IsSearchingDestination = false;
        }
    }

    private Task SelectSearchResultAsync(GeocodingResult result)
    {
        if (result is null) return Task.CompletedTask;
        ReplaceCollection(_searchResults, Array.Empty<GeocodingResult>());
        var safeResult = result.DisplayName.Length > MaxDisplayNameLength
            ? result with { DisplayName = result.DisplayName[..MaxDisplayNameLength] }
            : result;
        SetDestination(safeResult, "Search result");
        LastActionText = $"Destination set: {safeResult.DisplayName}.";
        return Task.CompletedTask;
    }

    private Task AddRouteToFavoritesAsync()
    {
        NavigateToAddFavoriteRequested?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public async Task RefreshCurrentLocationAsync()
    {
        try
        {
            var location = await _locationService.GetLastKnownLocationAsync();
            if (location is not null)
            {
                var lat = location.Latitude.ToString("F5", System.Globalization.CultureInfo.InvariantCulture);
                var lon = location.Longitude.ToString("F5", System.Globalization.CultureInfo.InvariantCulture);
                CurrentLocationText = $"{lat}, {lon}";
            }
            else
            {
                CurrentLocationText = "Searching for GPS…";
            }
        }
        catch
        {
            CurrentLocationText = "Location unavailable";
        }
    }

    public void ResetSearchState()
    {
        DestinationQuery = string.Empty;
        ReplaceCollection(_searchResults, Array.Empty<GeocodingResult>());
    }

    private async Task SaveSelectedRouteToFavoritesAsync(GeocodingResult result)
    {
        if (result is null) return;

        var safeResult = result.DisplayName.Length > MaxDisplayNameLength
            ? result with { DisplayName = result.DisplayName[..MaxDisplayNameLength] }
            : result;

        if (_savedRoutes.Count >= MaxSavedRoutes)
        {
            LastActionText = $"Saved route limit reached ({MaxSavedRoutes}). Remove a route to add another.";
            return;
        }

        if (_savedRoutes.Any(r => IsSameDestination(r, safeResult)))
        {
            LastActionText = "This destination is already saved.";
            return;
        }

        var rawName = safeResult.DisplayName;
        var shortName = rawName.Length > 30 ? rawName[..30].TrimEnd() : rawName;
        if (shortName.Length < 2) shortName = "Saved route";

        if (_savedRoutes.Any(r => string.Equals(r.DisplayName, shortName, StringComparison.OrdinalIgnoreCase)))
            shortName = shortName.Length <= 27 ? shortName + " (2)" : shortName[..27] + " (2)";

        var route = new SavedRoute
        {
            DisplayName = shortName,
            FullAddress = safeResult.DisplayName,
            Latitude = safeResult.Latitude,
            Longitude = safeResult.Longitude,
            CreatedAt = DateTime.UtcNow
        };

        try
        {
            await _databaseService.SaveRouteAsync(route);
            await LoadSavedRoutesAsync();
            LastActionText = $"Saved: {route.DisplayName}.";
            FavoriteSaved?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            BlackBoxLogger.RecordHandledException(ex, "[HomeController.SaveSelectedRouteToFavoritesAsync]");
            LastActionText = "Failed to save route. Please try again.";
        }
    }

    private async Task OpenMapsAsync()
    {
        try
        {
            if (_lastDestinationResult is not null)
            {
                await _googleMapsLauncher.OpenRerouteAsync(_lastDestinationResult.Latitude, _lastDestinationResult.Longitude);
                LastActionText = "Requested Google Maps reroute intent.";
                return;
            }

            var location = await _locationService.GetLastKnownLocationAsync();
            if (location is null)
            {
                LastActionText = "No destination or location available for reroute.";
                return;
            }

            await _googleMapsLauncher.OpenRerouteAsync(location.Latitude, location.Longitude);
            LastActionText = "Requested Google Maps reroute using current location.";
        }
        catch (Exception ex)
        {
            BlackBoxLogger.RecordHandledException(ex, "[HomeController.OpenMapsAsync]");
            LastActionText = "Unable to open Google Maps. Please ensure it is installed.";
        }
    }

    private async Task ToggleFavoriteAsync()
    {
        if (_lastDestinationResult is null) return;

        var existing = _savedRoutes.FirstOrDefault(r => IsSameDestination(r, _lastDestinationResult));
        if (existing is not null)
        {
            await RemoveSavedRouteAsync(existing);
            return;
        }

        await SaveDestinationAsync();
    }

    private async Task SaveDestinationAsync()
    {
        if (_lastDestinationResult is null)
        {
            LastActionText = "Search for a destination before saving a route.";
            return;
        }

        if (_savedRoutes.Count >= MaxSavedRoutes)
        {
            LastActionText = $"Saved route limit reached ({MaxSavedRoutes}). Remove a route to add another.";
            return;
        }

        if (_savedRoutes.Any(route => IsSameDestination(route, _lastDestinationResult)))
        {
            LastActionText = "This destination is already saved.";
            return;
        }

        // custom name if given, else fall back to the display name.
        // cap at 30 chars so long uni names don't fail validation.
        var rawName = string.IsNullOrWhiteSpace(NewRouteName)
            ? _lastDestinationResult.DisplayName
            : NewRouteName.Trim();
        if (string.IsNullOrWhiteSpace(rawName))
            rawName = "Saved route";
        var routeName = rawName.Length > 30 ? rawName[..30].TrimEnd() : rawName;
        if (routeName.Length < 2)
            routeName = "Saved route";

        if (_savedRoutes.Any(route => string.Equals(route.DisplayName, routeName, StringComparison.OrdinalIgnoreCase)))
        {
            // Append a short suffix to avoid collision while still saving.
            routeName = routeName.Length <= 27 ? routeName + " (2)" : routeName[..27] + " (2)";
        }

        var route = new SavedRoute
        {
            DisplayName = routeName,
            FullAddress = _lastDestinationResult.DisplayName,
            Latitude = _lastDestinationResult.Latitude,
            Longitude = _lastDestinationResult.Longitude,
            CreatedAt = DateTime.UtcNow
        };

        try
        {
            await _databaseService.SaveRouteAsync(route);
            NewRouteName = string.Empty;
            await LoadSavedRoutesAsync();
            LastActionText = $"Saved: {route.DisplayName}.";
        }
        catch (Exception ex)
        {
            BlackBoxLogger.RecordHandledException(ex, "[HomeController.SaveDestinationAsync]");
            LastActionText = "Failed to save route. Please try again.";
        }
    }

    private Task ApplySavedRouteAsync(SavedRoute route)
    {
        if (route is null)
        {
            return Task.CompletedTask;
        }

        var label = string.IsNullOrWhiteSpace(route.FullAddress) ? route.DisplayName : route.FullAddress;
        var destination = new GeocodingResult(label, route.Latitude, route.Longitude);
        SetDestination(destination, "Saved route");
        LastActionText = $"Loaded saved route: {route.DisplayName}.";
        return Task.CompletedTask;
    }

    private async Task RemoveSavedRouteAsync(SavedRoute route)
    {
        if (route is null)
        {
            return;
        }

        try
        {
            await _databaseService.DeleteRouteAsync(route);
            await LoadSavedRoutesAsync();
            LastActionText = $"Removed saved route: {route.DisplayName}.";
        }
        catch (Exception ex)
        {
            BlackBoxLogger.RecordHandledException(ex, "[HomeController.RemoveSavedRouteAsync]");
            LastActionText = "Failed to remove route. Please try again.";
        }
    }

    // Re-inserts a favourite that was just deleted (the "Undo" on the favorites snackbar). The row is
    // gone from the db, so we force a fresh insert by zeroing the primary key before saving.
    public async Task RestoreFavoriteAsync(SavedRoute route)
    {
        if (route is null)
        {
            return;
        }

        try
        {
            route.Id = 0;
            await _databaseService.SaveRouteAsync(route);
            await LoadSavedRoutesAsync();
            LastActionText = $"Restored saved route: {route.DisplayName}.";
        }
        catch (Exception ex)
        {
            BlackBoxLogger.RecordHandledException(ex, "[HomeController.RestoreFavoriteAsync]");
            LastActionText = "Failed to restore route. Please try again.";
        }
    }

    private async Task AddEmergencyContactAsync()
    {
        var name = NewContactName?.Trim() ?? string.Empty;
        var number = NewContactNumber?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(number))
        {
            EmergencyContactValidationFailed?.Invoke(this, "Enter both a contact name and a phone number.");
            return;
        }

        if (name.Length > MaxContactNameLength)
        {
            EmergencyContactValidationFailed?.Invoke(this, $"Contact name must be {MaxContactNameLength} characters or fewer.");
            return;
        }

        if (_emergencyContacts.Count >= MaxEmergencyContacts)
        {
            EmergencyContactValidationFailed?.Invoke(this, $"Maximum {MaxEmergencyContacts} emergency contacts allowed. Remove one to add another.");
            return;
        }

        if (!IsValidPhilippineNumber(number))
        {
            EmergencyContactValidationFailed?.Invoke(this, "Phone number must be in the format 09XXXXXXXXX or +639XXXXXXXXX.");
            return;
        }

        if (_emergencyContacts.Any(contact =>
                string.Equals(contact.PhoneNumber?.Trim(), number, StringComparison.OrdinalIgnoreCase)))
        {
            EmergencyContactValidationFailed?.Invoke(this, "A contact with this phone number already exists.");
            return;
        }

        var isFirstContact = !_emergencyContacts.Any();
        var contact = new EmergencyContact
        {
            Name = name,
            PhoneNumber = number,
            IsPrimary = isFirstContact
        };

        try
        {
            await _databaseService.SaveEmergencyContactAsync(contact);
            NewContactName = string.Empty;
            NewContactNumber = string.Empty;
            await LoadEmergencyContactsAsync();
            LastActionText = contact.IsPrimary
                ? "Emergency contact saved and set as primary."
                : "Emergency contact saved.";
        }
        catch (Exception ex)
        {
            BlackBoxLogger.RecordHandledException(ex, "[HomeController.AddEmergencyContactAsync]");
            LastActionText = "Failed to save emergency contact. Please try again.";
        }
    }

    private async Task SetPrimaryContactAsync(EmergencyContact contact)
    {
        if (contact is null)
        {
            return;
        }

        if (contact.IsPrimary)
        {
            LastActionText = "Primary contact is already selected.";
            return;
        }

        try
        {
            var currentPrimary = _emergencyContacts.FirstOrDefault(existing => existing.IsPrimary);
            if (currentPrimary is not null && currentPrimary.Id != contact.Id)
            {
                currentPrimary.IsPrimary = false;
                await _databaseService.SaveEmergencyContactAsync(currentPrimary);
            }

            contact.IsPrimary = true;
            await _databaseService.SaveEmergencyContactAsync(contact);

            await LoadEmergencyContactsAsync();
            LastActionText = $"Primary contact set to {contact.Name}.";
        }
        catch (Exception ex)
        {
            BlackBoxLogger.RecordHandledException(ex, "[HomeController.SetPrimaryContactAsync]");
            LastActionText = "Failed to update primary contact. Please try again.";
        }
    }

    private async Task RemoveEmergencyContactAsync(EmergencyContact contact)
    {
        if (contact is null)
        {
            return;
        }

        try
        {
            await _databaseService.DeleteEmergencyContactAsync(contact);
            await LoadEmergencyContactsAsync();
            LastActionText = $"Removed contact {contact.Name}.";
        }
        catch (Exception ex)
        {
            BlackBoxLogger.RecordHandledException(ex, "[HomeController.RemoveEmergencyContactAsync]");
            LastActionText = "Failed to remove contact. Please try again.";
        }
    }

    private async Task SendSosAsync()
    {
        // Single-fire guard: ignore a press while a previous dispatch is still running. The finally at
        // the bottom always clears this, so the button stays usable for every subsequent press.
        if (_isSendingSos)
        {
            return;
        }
        _isSendingSos = true;

        try
        {
            // Mandatory location check FIRST — before we fetch any coordinates or send anything. An SOS
            // with no location is far less useful, so if the device's master GPS switch is off we halt
            // and push the user to turn it on (the view turns this event into a settings prompt).
            if (!_locationService.IsLocationServiceEnabled())
            {
                LastActionText = "Turn on device location before sending an SOS.";
                LocationServicesDisabled?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (_lastSosSentAt.HasValue && DateTimeOffset.UtcNow - _lastSosSentAt.Value < SosCooldown)
            {
                var remaining = (int)(SosCooldown - (DateTimeOffset.UtcNow - _lastSosSentAt.Value)).TotalSeconds;
                LastActionText = $"SOS sent recently. Wait {remaining}s before sending again.";
                return;
            }

            if (!await _permissionsService.EnsureSmsPermissionAsync())
            {
                LastActionText = "SMS permission is required to send SOS alerts.";
                SmsDenied?.Invoke(this, EventArgs.Empty);
                return;
            }

            var recipients = _emergencyContacts
                .Select(contact => contact.PhoneNumber)
                .ToList();
            if (!recipients.Any())
            {
                if (!string.IsNullOrWhiteSpace(_primaryContactNumber))
                    recipients.Add(_primaryContactNumber);
            }

            recipients = recipients
                .Where(number => !string.IsNullOrWhiteSpace(number))
                .Select(number => number.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (!recipients.Any())
            {
                LastActionText = "Configure an emergency contact before sending SOS.";
                return;
            }

            // Grab the freshest fix we can at the instant SOS is pressed. If a trip is running, the
            // in-memory _lastTrackedLocation is the exact accepted coordinate the hardened tracking loop
            // just produced — so we use that first. Otherwise (or if it's somehow still null right at the
            // start of a trip) we fall back to the platform's last-known/fresh location. Both are passive
            // reads: nothing here writes back into or disturbs the Haversine tracking loop.
            var location = (IsTracking ? _lastTrackedLocation : null)
                           ?? await _locationService.GetBestLocationAsync(TimeSpan.FromSeconds(5));
            string message;
            if (location is null)
            {
                // Last resort — location briefly unavailable. The SOS itself still goes out so contacts
                // are alerted even without a pin.
                message = "Alarma SOS: Location unavailable. Please check on me.";
            }
            else
            {
                // Clickable Google Maps link so a contact can tap straight through to the rider's spot.
                var lat = location.Latitude.ToString("F5", System.Globalization.CultureInfo.InvariantCulture);
                var lon = location.Longitude.ToString("F5", System.Globalization.CultureInfo.InvariantCulture);
                var phtTime = location.Timestamp.ToOffset(TimeSpan.FromHours(8));
                message = $"Alarma SOS: I may need help. https://maps.google.com/?q={lat},{lon} — {phtTime:hh:mm tt} PHT";
            }

            // SOS is strictly an SMS dispatch. It must NOT hijack the ringer, force volume, or touch
            // Do-Not-Disturb the way the trip alarm does — a discreet confirmation cue is all we want
            // so the user knows the press registered without broadcasting it.
            await _alarmAudioService.PlaySosFeedbackAsync();
            await _smsService.SendEmergencySmsAsync(message, recipients);
            _lastSosSentAt = DateTimeOffset.UtcNow;
            SosDispatched?.Invoke(this, EventArgs.Empty);
            LastActionText = $"SOS sent to {recipients.Count} contact(s).";
        }
        catch (Exception ex)
        {
            BlackBoxLogger.RecordHandledException(ex, "[HomeController.SendSosAsync]");
            LastActionText = "Failed to send SOS. Ensure SMS permission is granted and try again.";
        }
        finally
        {
            // ALWAYS release the latch — this is the core of the multi-fire fix.
            _isSendingSos = false;
        }
    }

    private async Task StartTrackingAsync()
    {
        if (IsTracking)
        {
            return;
        }

        // Pre-flight: even with permission granted, a device whose master location switch is off
        // will never deliver a fix — so block the start and push the user to Settings (the way
        // Google Maps refuses to navigate without GPS) instead of starting a dead trip.
        if (!_locationService.IsLocationServiceEnabled())
        {
            LastActionText = "Turn on device location to start a trip.";
            LocationServicesDisabled?.Invoke(this, EventArgs.Empty);
            return;
        }

        var hasLocation = await _permissionsService.EnsureLocationPermissionsAsync(requireBackground: true);
        if (!hasLocation)
        {
            LastActionText = "Location permission is required to start tracking.";
            return;
        }

        var hasNotification = await _permissionsService.EnsureNotificationPermissionAsync();
        if (!hasNotification)
        {
            LastActionText = "Notification permission is required for background tracking.";
            return;
        }

        _totalDistanceMeters = 0;
        _lastTrackedLocation = null;
        _hasArrivedAtDestination = false;
        _overshootAlerted = false;
        _overshootIncreaseStreak = 0;
        _lastOvershootDistance = double.MaxValue;
        ResetOvershootUiState();
        _routeDeviationAlerted = false;
        _deviationAwayStreak = 0;
        _arrivalStreak = 0;
        CurrentAlarmStage = AlarmStage.None;
        _lastSpeedMetersPerSecond = 0;
        _minDistanceToDestination = double.MaxValue;
        DistanceToDestinationText = string.Empty;
        IsTracking = true;
        TrackingStatusText = "Starting trip tracking...";

        _activeTrip = new TripHistory
        {
            StartedAt = DateTime.UtcNow,
            DestinationName = _lastDestinationResult?.DisplayName,
            DestinationLatitude = _lastDestinationResult?.Latitude,
            DestinationLongitude = _lastDestinationResult?.Longitude,
            Summary = "Trip started."
        };

        try
        {
            await _databaseService.SaveTripHistoryAsync(_activeTrip);
            await _locationService.StartTrackingAsync(CancellationToken.None);
            LastActionText = "Trip tracking started.";
        }
        catch (Exception ex)
        {
            BlackBoxLogger.RecordHandledException(ex, "[HomeController.StartTrackingAsync]");
            await _alarmAudioService.DisableCriticalAudioAsync();
            LastActionText = "Unable to start tracking. Check location permissions and try again.";
            IsTracking = false;
        }
    }

    private async Task StopTrackingAsync()
    {
        if (!IsTracking)
        {
            return;
        }

        try
        {
            await _locationService.StopTrackingAsync();
        }
        catch (Exception ex)
        {
            BlackBoxLogger.RecordHandledException(ex, "[HomeController.StopTrackingAsync]");
            LastActionText = "Unable to stop tracking cleanly.";
        }

        try
        {
            await _alarmAudioService.DisableCriticalAudioAsync();
        }
        catch { }

        IsTracking = false;
        var distanceKm = _totalDistanceMeters / MetersPerKilometer;
        TrackingStatusText = $"Tracking stopped. Distance {distanceKm:F2} km.";
        CurrentAlarmStage = AlarmStage.None;

        if (_activeTrip is not null)
        {
            _activeTrip.EndedAt = DateTime.UtcNow;
            _activeTrip.DistanceMeters = _totalDistanceMeters;
            _activeTrip.OvershootDetected = _overshootAlerted;
            _activeTrip.Summary = BuildTripSummary(_activeTrip, distanceKm);
            try
            {
                await _databaseService.SaveTripHistoryAsync(_activeTrip);
                await LoadTripHistoryAsync();
            }
            catch (Exception ex)
            {
                BlackBoxLogger.RecordHandledException(ex, "[HomeController.StopTrackingAsync.SaveHistory]");
                LastActionText = "Failed to save trip history.";
            }
        }

        // Full reset so the home screen returns to its idle state — no lingering "View Active Trip"
        // pill or start-trip card. ClearDestination drops the destination + map marker (which makes
        // the greeting/search header reappear) and ResetSearchState empties the search box. The
        // foreground service itself was already torn down by StopTrackingAsync above.
        _activeTrip = null;
        ClearDestination();
        ResetSearchState();
        TrackingStatusText = "Tracking inactive.";
        LastActionText = "Trip tracking stopped.";
    }

    private void OnLocationUpdated(object? sender, LocationSnapshot snapshot)
    {
        _ = MainThread.InvokeOnMainThreadAsync(async () =>
        {
            try
            {
                await HandleLocationUpdateAsync(snapshot);
            }
            catch (Exception ex)
            {
                BlackBoxLogger.RecordHandledException(ex, "[HomeController.HandleLocationUpdateAsync]");
                LastActionText = "Location update error.";
            }
        });
    }

    private async Task HandleLocationUpdateAsync(LocationSnapshot snapshot)
    {
        if (!IsTracking)
        {
            return;
        }

        if (_lastTrackedLocation is null)
        {
            _lastTrackedLocation = snapshot;
        }
        else
        {
            var deltaSeconds = (snapshot.Timestamp - _lastTrackedLocation.Timestamp).TotalSeconds;
            // Haversine path-segment length between the last accepted fix and this one.
            var deltaMeters = CalculateDistanceMeters(_lastTrackedLocation, snapshot);

            // Jitter gate: a stationary phone still emits fixes that wander a few metres, which would
            // otherwise inflate the trip distance. Only count a segment once it clears the GPS noise
            // floor (the fix's own accuracy, min 8 m). The anchor advances only on an accepted
            // segment, so genuine slow movement still accumulates across several fixes rather than
            // being silently dropped.
            var noiseFloor = Math.Max(MinSegmentMeters, snapshot.AccuracyMeters);
            if (deltaMeters >= noiseFloor)
            {
                if (deltaSeconds > 0)
                {
                    _lastSpeedMetersPerSecond = deltaMeters / deltaSeconds;
                }

                _totalDistanceMeters += deltaMeters;
                _lastTrackedLocation = snapshot;
            }
        }

        BlackBoxLogger.LastKnownCoords = (snapshot.Latitude, snapshot.Longitude);
        var distanceKm = _totalDistanceMeters / MetersPerKilometer;
        TrackingStatusText = $"Tracking active: {distanceKm:F2} km traveled.";

        // Keep the live "Current Location" readout (shown on the Trip Progress sheet) ticking with each
        // accepted fix so the rider can see their position updating in real time.
        var curLat = snapshot.Latitude.ToString("F5", System.Globalization.CultureInfo.InvariantCulture);
        var curLon = snapshot.Longitude.ToString("F5", System.Globalization.CultureInfo.InvariantCulture);
        CurrentLocationText = $"{curLat}, {curLon}";

        if (DateTime.UtcNow - _lastMapLocationUpdate >= MapLocationUpdateInterval)
        {
            _lastMapLocationUpdate = DateTime.UtcNow;
            LiveLocationUpdated?.Invoke(this, (snapshot.Latitude, snapshot.Longitude));
        }

        await HandleDestinationDistanceAsync(snapshot);
    }

    private async Task HandleDestinationDistanceAsync(LocationSnapshot snapshot)
    {
        if (_lastDestinationResult is null)
        {
            DistanceToDestinationText = "No destination selected for overshoot monitoring.";
            await _notificationService.UpdateTrackingNotificationAsync("Tracking active");
            return;
        }

        var destinationSnapshot = new LocationSnapshot(
            _lastDestinationResult.Latitude,
            _lastDestinationResult.Longitude,
            snapshot.AccuracyMeters,
            snapshot.Timestamp);
        var distanceToDestination = CalculateDistanceMeters(snapshot, destinationSnapshot);
        var accuracyBuffer = Math.Max(snapshot.AccuracyMeters, 0);
        var overshootThreshold = OvershootThresholdMeters + accuracyBuffer;
        var adaptiveLeadDistance = Math.Clamp(
            _lastSpeedMetersPerSecond * AlarmLeadMinutes * 60,
            MinAlarmDistanceMeters,
            MaxAlarmDistanceMeters);
        var adaptiveLeadThreshold = adaptiveLeadDistance + accuracyBuffer;
        _minDistanceToDestination = Math.Min(_minDistanceToDestination, distanceToDestination);
        // Every distance readout on the Trip Progress page is shown in kilometres with the explicit unit
        // (e.g. "0.34 km away", "12 km away") — never a bare number.
        var distanceLabel = FormatKilometres(distanceToDestination);
        DistanceToDestinationText = $"Destination is {distanceLabel} away.";
        ChipDistanceText = $"{distanceLabel} away";

        // Mirror the live distance-to-destination into the ongoing tracking notification so the
        // background notification and foreground state never desync. Stage suffix appears only
        // once an alarm stage is active.
        var stageSuffix = _currentAlarmStage != AlarmStage.None
            ? $" • Stage {(int)_currentAlarmStage}"
            : string.Empty;
        await _notificationService.UpdateTrackingNotificationAsync(
            $"{distanceLabel} to {DestinationShortNameText}{stageSuffix}");

        // ── 3-stage progressive escalation ────────────────────────────────────────
        // The trigger radius is the adaptive lead distance (speed × lead-time, floored). Stage 1 is the
        // gentle alert at that radius; Stage 2 is the louder alert once the rider is halfway inside it;
        // the Emergency stage is the full-screen lockout at the actual drop-off. We keep Stage 2 a little
        // outside the arrival ring so the order never inverts when the radius is small / floored.
        var stage2Threshold = Math.Max(adaptiveLeadDistance * 0.5, ArrivalThresholdMeters + Stage2BufferMeters)
                              + accuracyBuffer;

        // Stage 1 — gentle alert at the initial trigger radius. No screen lockout (see AppShell).
        if (_currentAlarmStage == AlarmStage.None && distanceToDestination <= adaptiveLeadThreshold)
        {
            await TriggerAlarmStageAsync(
                AlarmStage.Stage1,
                "Alarm stage 1",
                "Approaching destination. Prepare to disembark.",
                reroute: false,
                allowRepeat: false);
        }

        // Stage 2 — louder alert once we cross ~50% of the trigger radius and aren't yet at the stop.
        // Strictly sequential: it can only fire AFTER Stage 1 has fired (never skip straight to Stage 2).
        if (_currentAlarmStage == AlarmStage.Stage1
            && !_hasArrivedAtDestination
            && distanceToDestination <= stage2Threshold)
        {
            await TriggerAlarmStageAsync(
                AlarmStage.Stage2,
                "Alarm stage 2",
                "Closing in on your stop. Get ready now.",
                reroute: false,
                allowRepeat: false);
        }

        // Emergency — the final drop-off coordinate is reached. This escalates to Stage 3, which AppShell
        // turns into the full-screen lockout (max volume + continuous vibration until Slide to Stop).
        if (!_hasArrivedAtDestination && distanceToDestination <= ArrivalThresholdMeters)
        {
            // Require the arrival to hold across a couple of fixes — one outlier dropping inside the
            // threshold then bouncing back must not latch arrival (which would later read as overshoot).
            _arrivalStreak++;
            if (_arrivalStreak >= ArrivalPersistenceFixes)
            {
                _hasArrivedAtDestination = true;
                await TriggerAlarmStageAsync(
                    AlarmStage.Stage3,
                    "WAKE UP",
                    "You have reached your destination.",
                    reroute: false,
                    allowRepeat: false);
                return;
            }
        }
        else if (!_hasArrivedAtDestination)
        {
            _arrivalStreak = 0;
        }

        if (_hasArrivedAtDestination && !_overshootAlerted)
        {
            // Overshoot = past the stop AND consistently moving farther. Count consecutive increasing
            // fixes; moving back toward the destination resets the streak so a brief drift can't fire it.
            if (distanceToDestination >= overshootThreshold && distanceToDestination > _lastOvershootDistance)
            {
                _overshootIncreaseStreak++;
            }
            else if (distanceToDestination < _lastOvershootDistance)
            {
                _overshootIncreaseStreak = 0;
            }
            _lastOvershootDistance = distanceToDestination;

            if (_overshootIncreaseStreak >= OvershootIncreasePersistenceFixes)
            {
                _overshootAlerted = true;
                OvershootDistanceText = distanceToDestination >= 1000
                    ? $"{distanceToDestination / 1000:F1} km"
                    : $"{(int)distanceToDestination} m";
                // Recovery flow step 1: skip the "did you miss it?" question and push the confirmed
                // full-screen alert straight away (destination name + exact distance live in the view).
                IsOvershootConfirmed = true;
                await TriggerAlarmStageAsync(
                    AlarmStage.Stage3,
                    "Overshoot Detected",
                    "You have passed your destination.",
                    reroute: false,
                    allowRepeat: false);
                return;
            }
        }

        // ── Route-deviation detection (multi-leg-commute aware) ───────────────────
        // Dwell = stopped/waiting below walking pace (e.g. at a transfer terminal). On a dwell,
        // re-baseline the closest-approach anchor to the current position so the next leg/walk
        // after a transfer isn't measured against a stale "closest ever" point, and clear the
        // away-streak. Walking pace (~1.4 m/s) stays above the dwell threshold, so a short
        // transfer walk is instead absorbed by the buffer + persistence checks below.
        if (_lastSpeedMetersPerSecond <= DwellSpeedThresholdMetersPerSecond)
        {
            _minDistanceToDestination = distanceToDestination;
            _deviationAwayStreak = 0;
        }

        if (!_hasArrivedAtDestination && !_routeDeviationAlerted)
        {
            // Proportional buffer: a flat threshold is far too tight for city transfers. Being a
            // little farther only matters once genuinely near the destination.
            var deviationBuffer = Math.Max(
                DeviationBaseBufferMeters,
                DeviationProportionalFraction * _minDistanceToDestination) + accuracyBuffer;

            // Arm only once reasonably close — early-trip legs routinely head away to reach a main
            // thoroughfare. Tie to the adaptive lead distance, floored at the arm radius.
            var deviationArmRadius = Math.Max(adaptiveLeadDistance, DeviationArmRadiusMeters);
            var isArmed = _minDistanceToDestination <= deviationArmRadius;
            var isMovingAway = distanceToDestination > _minDistanceToDestination + deviationBuffer;

            if (isArmed && isMovingAway)
            {
                // Require sustained movement away (≈4 consecutive fixes) — a single noisy fix or a
                // brief detour that resolves won't fire the alarm.
                _deviationAwayStreak++;
                if (_deviationAwayStreak >= DeviationPersistenceFixes)
                {
                    _routeDeviationAlerted = true;
                    await TriggerAlarmStageAsync(
                        AlarmStage.Stage1,
                        "Route deviation",
                        "You are getting farther from your destination.",
                        reroute: false,
                        allowRepeat: true);
                }
            }
            else
            {
                _deviationAwayStreak = 0;
            }
        }
    }

    private async Task TriggerAlarmStageAsync(
        AlarmStage stage,
        string title,
        string message,
        bool reroute,
        bool allowRepeat)
    {
        try
        {
            if (allowRepeat || stage > CurrentAlarmStage)
            {
                if (stage > CurrentAlarmStage)
                {
                    CurrentAlarmStage = stage;
                    if (_activeTrip is not null && (int)stage > _activeTrip.MaxAlarmStageReached)
                        _activeTrip.MaxAlarmStageReached = (int)stage;
                    AlarmStageActivated?.Invoke(this, stage);
                }

                await _alarmAudioService.TriggerAlarmAsync(stage, AlarmSound, VibrationOnly, VibrationIntensity);
            }

            await _notificationService.ShowTripAlertAsync(title, message);
            LastActionText = message;

            if (reroute && _lastDestinationResult is not null)
            {
                await _googleMapsLauncher.OpenRerouteAsync(
                    _lastDestinationResult.Latitude,
                    _lastDestinationResult.Longitude);
            }
        }
        catch (Exception ex)
        {
            BlackBoxLogger.RecordHandledException(ex, "[HomeController.TriggerAlarmStageAsync]");
            LastActionText = "Alarm alert failed.";
        }
    }

    private void CompleteOnboarding()
    {
        _preferencesService.IsOnboardingComplete = true;
        IsOnboardingComplete = true;
        LastActionText = "Onboarding marked complete.";
    }

    private void SetDestination(GeocodingResult destination, string sourceLabel)
    {
        _lastDestinationResult = destination;
        DestinationSummaryText = $"{sourceLabel}: {destination.DisplayName}";
        var latStr = destination.Latitude.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
        var lonStr = destination.Longitude.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
        var safeLabel = System.Text.Json.JsonSerializer.Serialize(destination.DisplayName);
        MapJsRequested?.Invoke(this, $"setDestination({latStr},{lonStr},{safeLabel})");
        _minDistanceToDestination = double.MaxValue;
        _hasArrivedAtDestination = false;
        _overshootAlerted = false;
        _overshootIncreaseStreak = 0;
        _lastOvershootDistance = double.MaxValue;
        ResetOvershootUiState();
        _routeDeviationAlerted = false;
        _deviationAwayStreak = 0;
        _arrivalStreak = 0;
        CurrentAlarmStage = AlarmStage.None;
        _lastSpeedMetersPerSecond = 0;
        OnPropertyChanged(nameof(HasDestination));
        OnPropertyChanged(nameof(CanSaveRoute));
        OnPropertyChanged(nameof(ShowStartTripCard));
        OnPropertyChanged(nameof(ShowGreetingHeader));
        OnPropertyChanged(nameof(IsDestinationSaved));
    }

    private async Task CenterOnUserAsync()
    {
        try
        {
            var loc = await _locationService.GetLastKnownLocationAsync();
            if (loc is not null)
                CenterMapRequested?.Invoke(this, (loc.Latitude, loc.Longitude));
            else
                LastActionText = "Location not yet available. Ensure GPS is enabled.";
        }
        catch (Exception ex)
        {
            BlackBoxLogger.RecordHandledException(ex, "[HomeController.CenterOnUserAsync]");
            LastActionText = "Could not get current location. Ensure GPS is enabled.";
        }
    }

    private void ClearDestination()
    {
        _lastDestinationResult = null;
        DestinationSummaryText = "Search";
        DistanceToDestinationText = string.Empty;
        ClearMapTile();
        _minDistanceToDestination = double.MaxValue;
        _hasArrivedAtDestination = false;
        _overshootAlerted = false;
        _overshootIncreaseStreak = 0;
        _lastOvershootDistance = double.MaxValue;
        ResetOvershootUiState();
        _routeDeviationAlerted = false;
        _deviationAwayStreak = 0;
        _arrivalStreak = 0;
        CurrentAlarmStage = AlarmStage.None;
        _lastSpeedMetersPerSecond = 0;
        OnPropertyChanged(nameof(HasDestination));
        OnPropertyChanged(nameof(CanSaveRoute));
        OnPropertyChanged(nameof(ShowStartTripCard));
        OnPropertyChanged(nameof(ShowGreetingHeader));
        OnPropertyChanged(nameof(IsDestinationSaved));
    }

    // Drop a single trip the rider swiped/tapped to delete. We wipe it from the encrypted db, then pull
    // it out of the in-memory snapshot and re-run the filter so the list updates without a full re-query.
    private async Task DeleteTripHistoryAsync(TripHistory? trip)
    {
        if (trip is null)
        {
            return;
        }

        try
        {
            await _databaseService.DeleteTripHistoryAsync(trip);
            _allTripHistory.RemoveAll(t => t.Id == trip.Id);
            ApplyTripHistoryFilter();
            LastActionText = "Trip removed from history.";
        }
        catch (Exception ex)
        {
            BlackBoxLogger.RecordHandledException(ex, "[HomeController.DeleteTripHistoryAsync]");
            LastActionText = "Failed to delete trip. Please try again.";
        }
    }

    // "Clear All" button. Filter-aware on purpose: if the rider has a search active, this only wipes the
    // trips they can actually see (the filtered subset) — clearing everything behind a filter would be a
    // nasty surprise. With no filter it's a clean sweep via the single DeleteAll. The view confirms first
    // since there's no undo, so by the time we get here the decision is already made.
    private async Task ClearTripHistoryAsync()
    {
        try
        {
            var isFiltered = !string.IsNullOrWhiteSpace(_historySearchQuery)
                || !string.Equals(_historyFilterCategory, "All", StringComparison.OrdinalIgnoreCase);
            if (isFiltered)
            {
                // Snapshot the visible rows first — we mutate the collection as we delete.
                var visible = _tripHistoryEntries.ToList();
                foreach (var trip in visible)
                {
                    await _databaseService.DeleteTripHistoryAsync(trip);
                }

                var deletedIds = visible.Select(t => t.Id).ToHashSet();
                _allTripHistory.RemoveAll(t => deletedIds.Contains(t.Id));
                ApplyTripHistoryFilter();
                LastActionText = $"Cleared {visible.Count} matching trip(s).";
            }
            else
            {
                await _databaseService.ClearTripHistoryAsync();
                _allTripHistory.Clear();
                ApplyTripHistoryFilter();
                LastActionText = "Trip history cleared.";
            }
        }
        catch (Exception ex)
        {
            BlackBoxLogger.RecordHandledException(ex, "[HomeController.ClearTripHistoryAsync]");
            LastActionText = "Failed to clear trip history. Please try again.";
        }
    }

    // Rebuilds the visible trip list from the full snapshot, applying the current search text. An empty
    // box shows everything; otherwise we keep entries that match on destination name or date.
    private void ApplyTripHistoryFilter()
    {
        IEnumerable<TripHistory> items = _allTripHistory;

        // 1) Free-text search box narrows first.
        var query = _historySearchQuery?.Trim();
        if (!string.IsNullOrWhiteSpace(query))
        {
            items = items.Where(trip => MatchesHistoryQuery(trip, query));
        }

        // 2) Then the active chip category. "All" is a pass-through; "Recent" keeps the last week;
        //    "By Route" reorders alphabetically by destination so trips to the same place sit together.
        items = _historyFilterCategory switch
        {
            "Recent" => items.Where(trip => trip.StartedAt >= DateTime.Now.AddDays(-7)),
            "By Route" => items.OrderBy(trip => trip.DestinationName ?? string.Empty,
                StringComparer.OrdinalIgnoreCase),
            _ => items
        };

        ReplaceCollection(_tripHistoryEntries, items.ToList());
    }

    // Match the typed text against the destination name, the route details summary, and the date/time
    // the way the user sees it on the card — so "june", "2026", a time like "9:30", or a word from the
    // trip summary all narrow the list as expected.
    private static bool MatchesHistoryQuery(TripHistory trip, string query)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        var haystack = string.Join(' ',
            trip.DestinationName ?? string.Empty,
            trip.Summary ?? string.Empty,
            trip.StartedAt.ToString("MMMM d, yyyy", culture),
            trip.StartedAt.ToString("h:mm tt", culture));
        return haystack.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static void ReplaceCollection<T>(ObservableCollection<T> collection, IEnumerable<T> items)
    {
        collection.Clear();
        foreach (var item in items)
        {
            collection.Add(item);
        }
    }

    // Settings sound picker: pick the sound (which persists it via the AlarmSound setter so the 3-stage
    // alarm uses it) and immediately play a short preview so the rider hears their choice. Tapping a
    // different sound stops the previous preview and starts the new one — handled inside the audio service.
    private async Task SelectAndPreviewSoundAsync(string? sound)
    {
        if (string.IsNullOrWhiteSpace(sound)) return;
        AlarmSound = sound;                       // normalizes + saves to Preferences
        try
        {
            await _alarmAudioService.PreviewSoundAsync(_alarmSound);
        }
        catch (Exception ex)
        {
            BlackBoxLogger.RecordHandledException(ex, "[HomeController.SelectAndPreviewSoundAsync]");
        }
    }

    // Called when the rider leaves Settings so a preview can't keep playing after the page is gone.
    public void StopSoundPreview()
    {
        try { _ = _alarmAudioService.StopPreviewAsync(); }
        catch (Exception ex)
        {
            BlackBoxLogger.RecordHandledException(ex, "[HomeController.StopSoundPreview]");
        }
    }

    private string NormalizeSoundKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Default";
        }

        var trimmed = value.Trim();
        return _alarmSoundOptions.Contains(trimmed, StringComparer.OrdinalIgnoreCase)
            ? _alarmSoundOptions.First(option => option.Equals(trimmed, StringComparison.OrdinalIgnoreCase))
            : "Default";
    }

    private static bool IsSameDestination(SavedRoute route, GeocodingResult destination)
    {
        const double tolerance = 0.0001;
        return Math.Abs(route.Latitude - destination.Latitude) <= tolerance
            && Math.Abs(route.Longitude - destination.Longitude) <= tolerance;
    }

    private static string BuildTripSummary(TripHistory trip, double distanceKm)
    {
        var duration = trip.EndedAt.HasValue
            ? trip.EndedAt.Value - trip.StartedAt
            : TimeSpan.Zero;
        var durationText = duration == TimeSpan.Zero
            ? "In progress"
            : $"{duration.TotalMinutes:F0} min";
        var overshootText = trip.OvershootDetected ? "Overshoot detected" : "No overshoot";
        return $"{durationText} · {distanceKm:F2} km · {overshootText}";
    }

    // Clears all four recovery-flow screens in one shot (used on dismiss, trip stop, and new trips).
    private void ResetOvershootUiState()
    {
        IsOvershootPending = false;
        IsOvershootConfirmed = false;
        IsAreaSafetyVisible = false;
        IsReroutingVisible = false;
    }

    // Offline "Area Safety Alert" copy. Deliberately built locally with no network call — it leans on
    // the destination name + how far past it we are, plus general personal-safety guidance for being
    // dropped somewhere unplanned. (Reverse-geocoding the exact area would need the network, which the
    // overshoot/handoff requirement forbids.)
    private void BuildAreaSafetyMessage()
    {
        var dest = DestinationShortNameText;
        var place = string.IsNullOrWhiteSpace(dest) ? "your stop" : dest;
        AreaSafetyText =
            $"You're about {OvershootDistanceText} past {place}, in an area you didn't plan to be.\n\n" +
            "• Move to a well-lit, populated spot and stay aware of your surroundings.\n" +
            "• Keep your phone and belongings close and out of sight.\n" +
            "• Note the nearest landmark, terminal, or store you can head to.\n" +
            "• If anything feels unsafe, call an emergency contact or 911 right away.";
    }

    // Builds the in-app rerouting guidance shown over the mini-map. Everything here is computed locally
    // from the last GPS fix and the saved destination — no routing API / network. For full turn-by-turn
    // the rerouting screen hands off to Google Maps via the local intent.
    private void BuildReroutingGuidance()
    {
        _reroutingSteps.Clear();
        var dest = _lastDestinationResult;
        var place = DestinationShortNameText;
        if (dest is null)
        {
            ReroutingHeadingText = "Destination unavailable.";
            return;
        }

        var here = _lastTrackedLocation;
        var compass = "back toward your stop";
        var distText = OvershootDistanceText;
        if (here is not null)
        {
            var bearing = CalculateBearingDegrees(here.Latitude, here.Longitude, dest.Latitude, dest.Longitude);
            compass = $"{BearingToCompass(bearing)} (back toward your stop)";
            var d = CalculateDistanceMeters(
                here,
                new LocationSnapshot(dest.Latitude, dest.Longitude, 0f, here.Timestamp));
            distText = d >= 1000 ? $"{d / 1000:F1} km" : $"{(int)d} m";
        }

        ReroutingHeadingText = $"Head {compass} • {distText} to {place}";
        _reroutingSteps.Add($"1. Turn around and head {compass}.");
        _reroutingSteps.Add($"2. Travel roughly {distText} back toward {place}.");
        _reroutingSteps.Add("3. Look for the opposite-direction stop, jeepney, or route going back.");
        _reroutingSteps.Add("4. Tap “Open in Google Maps” for live turn-by-turn directions.");
    }

    // Initial compass bearing from one coordinate to another, in degrees [0,360).
    private static double CalculateBearingDegrees(double lat1, double lon1, double lat2, double lon2)
    {
        var phi1 = DegreesToRadians(lat1);
        var phi2 = DegreesToRadians(lat2);
        var deltaLon = DegreesToRadians(lon2 - lon1);
        var y = Math.Sin(deltaLon) * Math.Cos(phi2);
        var x = Math.Cos(phi1) * Math.Sin(phi2) - Math.Sin(phi1) * Math.Cos(phi2) * Math.Cos(deltaLon);
        var bearing = Math.Atan2(y, x) * 180.0 / Math.PI;
        return (bearing + 360.0) % 360.0;
    }

    private static string BearingToCompass(double bearing)
    {
        string[] points = { "north", "north-east", "east", "south-east", "south", "south-west", "west", "north-west" };
        var index = (int)Math.Round(bearing / 45.0) % 8;
        return points[index];
    }

    private static double CalculateDistanceMeters(LocationSnapshot start, LocationSnapshot end)
    {
        var lat1 = DegreesToRadians(start.Latitude);
        var lat2 = DegreesToRadians(end.Latitude);
        var deltaLat = DegreesToRadians(end.Latitude - start.Latitude);
        var deltaLon = DegreesToRadians(end.Longitude - start.Longitude);

        var sinHalfDeltaLat = Math.Sin(deltaLat / 2);
        var sinHalfDeltaLon = Math.Sin(deltaLon / 2);
        var a = sinHalfDeltaLat * sinHalfDeltaLat
                + Math.Cos(lat1) * Math.Cos(lat2)
                * sinHalfDeltaLon * sinHalfDeltaLon;
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return EarthRadiusMeters * c;
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180.0;

    // Always render a distance in kilometres with the unit attached. Under 10 km keeps one decimal so
    // short distances stay meaningful ("0.3 km"); 10 km and up drops to a whole number ("12 km").
    private static string FormatKilometres(double meters)
    {
        var km = meters / MetersPerKilometer;
        return km >= 10 ? $"{km:F0} km" : $"{km:F1} km";
    }

    private void ClearMapTile()
    {
        MapJsRequested?.Invoke(this, "clearDestination()");
    }

    // everything we push into JS (setDestination/updateUserLocation/centerOnUser) is either an
    // F6 InvariantCulture number or JsonSerializer output, so no raw user string hits eval.
    // CSP has to list both *.cartocdn.com and *.basemaps.cartocdn.com because the wildcard only
    // matches one subdomain level (a.basemaps.cartocdn.com is two deep).
    // build the map html once and never swap it - reloading it caused gray tiles on pan / center.
    private static HtmlWebViewSource BuildDefaultMapHtml()
    {
        var html = """
            <!DOCTYPE html>
            <html>
            <head>
              <meta charset="utf-8"/>
              <meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src file: 'unsafe-inline'; style-src file: 'unsafe-inline'; img-src https://*.basemaps.cartocdn.com data: blob:; connect-src https://*.basemaps.cartocdn.com"/>
              <meta name="viewport" content="width=device-width,initial-scale=1,maximum-scale=1,user-scalable=no"/>
              <link rel="stylesheet" href="leaflet.css"/>
              <script src="leaflet.js"></script>
              <style>
                html,body,#map{margin:0;padding:0;width:100%;height:100%;will-change:transform}
                .leaflet-tile{filter:sepia(0.8) hue-rotate(250deg) saturate(2.5) brightness(0.75)}
                .leaflet-zoom-animated{transition:transform 0.4s cubic-bezier(0,0,0.25,1)!important}
                .leaflet-interactive{will-change:transform;transition:none!important}
                /* The dot is now driven frame-by-frame from JS (see _animateUserMarker), so we kill the
                   CSS transform transition here — letting both fight over the same element is what made
                   the dot smear and snap. */
                .user-location-dot{will-change:transform;transition:none!important}
              </style>
            </head>
            <body>
              <div id="map"></div>
              <script>
                var map = L.map('map',{zoomControl:false}).setView([14.5995,120.9842],12);
                var _layer=L.tileLayer('https://{s}.basemaps.cartocdn.com/dark_all/{z}/{x}/{y}{r}.png',{attribution:'&copy; OSM &copy; CARTO',subdomains:'abcd',maxZoom:19}).addTo(map);
                var _userMarker=null;
                var _userAnimFrame=null;
                var _destMarker=null;
                // Glide the live dot from where it is to the new fix instead of teleporting it. We tween
                // lat/lon over a few hundred ms with an ease-out curve so it decelerates into place, and
                // pan the map along with it so the dot stays put on screen while the world slides under it.
                function animateUserMarker(toLat,toLon){
                  if(!_userMarker){return;}
                  var from=_userMarker.getLatLng();
                  var fromLat=from.lat, fromLon=from.lng;
                  var dLat=toLat-fromLat, dLon=toLon-fromLon;
                  // Effectively no movement (GPS jitter) — just settle it and skip the loop.
                  if(Math.abs(dLat)<1e-7 && Math.abs(dLon)<1e-7){_userMarker.setLatLng([toLat,toLon]);map.panTo([toLat,toLon],{animate:false});return;}
                  // A new fix landed mid-glide — drop the old animation and start fresh from here.
                  if(_userAnimFrame){cancelAnimationFrame(_userAnimFrame);_userAnimFrame=null;}
                  var duration=600, start=null;
                  function step(ts){
                    if(start===null){start=ts;}
                    var t=Math.min(1,(ts-start)/duration);
                    var e=1-Math.pow(1-t,3); // cubic ease-out
                    var lat=fromLat+dLat*e, lon=fromLon+dLon*e;
                    _userMarker.setLatLng([lat,lon]);
                    map.panTo([lat,lon],{animate:false});
                    if(t<1){_userAnimFrame=requestAnimationFrame(step);}else{_userAnimFrame=null;}
                  }
                  _userAnimFrame=requestAnimationFrame(step);
                }
                // SVG data-URI pin — avoids broken-image from missing default Leaflet marker PNGs
                var _pinUri='data:image/svg+xml;utf8,'+encodeURIComponent('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="40" height="40"><path d="M12 2C8.13 2 5 5.13 5 9c0 5.25 7 13 7 13s7-7.75 7-13c0-3.87-3.13-7-7-7zm0 9.5c-1.38 0-2.5-1.12-2.5-2.5s1.12-2.5 2.5-2.5 2.5 1.12 2.5 2.5-1.12 2.5-2.5 2.5z" fill="#8B5CF6" stroke="#1E1E2E" stroke-width="1.5"/></svg>');
                var _pinIcon=L.icon({iconUrl:_pinUri,iconSize:[40,40],iconAnchor:[20,40],popupAnchor:[0,-40]});
                function updateUserLocation(lat,lon){
                  var ll=[lat,lon];
                  if(_userMarker){animateUserMarker(lat,lon);}
                  else{
                    // First fix of the trip — drop the dot straight down, nothing to glide from yet.
                    _userMarker=L.circleMarker(ll,{radius:8,color:'#fff',weight:2,fillColor:'#4A90D9',fillOpacity:0.9,className:'user-location-dot'}).addTo(map).bindTooltip('You',{permanent:false,direction:'top'});
                    map.panTo(ll,{animate:false});
                  }
                }
                function centerOnUser(lat,lon){
                  var ll=[lat,lon];
                  // User asked to recenter — cancel any glide in progress so it doesn't fight the flyTo.
                  if(_userAnimFrame){cancelAnimationFrame(_userAnimFrame);_userAnimFrame=null;}
                  if(_userMarker){_userMarker.setLatLng(ll);}
                  else{_userMarker=L.circleMarker(ll,{radius:8,color:'#fff',weight:2,fillColor:'#4A90D9',fillOpacity:0.9,className:'user-location-dot'}).addTo(map).bindTooltip('You',{permanent:false,direction:'top'});}
                  map.flyTo(ll,16,{animate:true,duration:0.4,easeLinearity:0.25});
                }
                function setDestination(lat,lon,label){
                  clearDestination();
                  _destMarker=L.marker([lat,lon],{icon:_pinIcon}).addTo(map).bindPopup(label).openPopup();
                  map.flyTo([lat,lon],15,{animate:true,duration:0.4,easeLinearity:0.25});
                }
                function clearDestination(){
                  if(_destMarker){map.removeLayer(_destMarker);_destMarker=null;}
                }
              </script>
            </body>
            </html>
            """;
        return new HtmlWebViewSource { Html = html, BaseUrl = "file:///android_asset/" };
    }

    private static readonly System.Text.RegularExpressions.Regex PhoneRegex =
        new(@"^(09\d{9}|\+639\d{9})$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    private static bool IsValidPhilippineNumber(string number)
    {
        if (string.IsNullOrWhiteSpace(number)) return false;
        return PhoneRegex.IsMatch(number.Trim());
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
