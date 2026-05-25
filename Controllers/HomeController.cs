// Security Considerations (OWASP Top 10)
// A01 Broken Access Control: InitializeAsync is gated by BiometricPrompt in release builds
//   (#if !DEBUG); SOS dispatch is rate-limited to 30 s cooldown; onboarding gate enforced by
//   HomeView.OnAppearing before InitializeAsync is ever called.
// A03 Injection: DestinationQuery capped at 200 chars; NewContactName capped at 50 chars;
//   phone numbers validated by compiled PhoneRegex ^(09\d{9}|\+639\d{9})$; external API
//   DisplayName capped at 200 chars; map popup labels passed through JsonSerializer.Serialize
//   (XSS-safe); all JS coordinate injections use InvariantCulture F6 numeric strings only.
// A04 Insecure Design: BackupService uses validate-before-clear — tampered backups with 0
//   valid records do not wipe the existing database.
// A05 Security Misconfiguration: AlarmSound whitelisted on read+write; AlarmLeadMinutes
//   clamped to 1–60 on both input and post-restore; VibrationIntensity validated against
//   the allowed options set; VehicleType validated against the allowed options set.
// SQLi: sqlite-net-pcl uses parameterized queries for all CRUD — no raw SQL construction.

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
    private const double OvershootBufferMeters = 250;
    private const double OvershootThresholdMeters = ArrivalThresholdMeters + OvershootBufferMeters;
    private const double RouteDeviationBufferMeters = OvershootBufferMeters;
    private const int TripHistoryLimit = 20;
    private const int MaxSavedRoutes = 5;
    private const int MaxEmergencyContacts = 3;
    private const double MinAlarmDistanceJeepneyMeters = 300;
    private const double MinAlarmDistanceCityBusMeters = 400;
    private const double MinAlarmDistanceUvExpressMeters = 500;

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
    private readonly IBiometricAuthService _biometricAuthService;
    private readonly BackupService _backupService;
    private readonly IBatteryOptimizationService _batteryOptimizationService;
    private readonly IEarphoneService _earphoneService;

    private readonly ObservableCollection<EmergencyContact> _emergencyContacts = new();
    private readonly ObservableCollection<SavedRoute> _savedRoutes = new();
    private readonly ObservableCollection<TripHistory> _tripHistoryEntries = new();
    private readonly ObservableCollection<GeocodingResult> _searchResults = new();
    private readonly ObservableCollection<string> _vibrationIntensityOptions = new() { "Low", "Medium", "High" };
    private readonly ObservableCollection<string> _vehicleTypeOptions = new() { "Jeepney", "UV Express", "City Bus" };

    private string _statusText = "Loading...";
    private string _connectivityText = string.Empty;
    private string _lastActionText = string.Empty;
    private string _destinationQuery = string.Empty;
    private string _destinationSummaryText = "No destination selected.";
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
    private bool _routeDeviationAlerted;
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
                OnPropertyChanged(nameof(IsStage1Active));
                OnPropertyChanged(nameof(IsStage1Or2Active));
            }
        }
    }
    private double _lastSpeedMetersPerSecond;
    private double _minDistanceToDestination = double.MaxValue;

    private string _newContactName = string.Empty;
    private string _newContactNumber = string.Empty;
    private string _newRouteName = string.Empty;
    private string _alarmSound;
    private double _alarmLeadMinutes;
    private bool _vibrationOnly;
    private string _vibrationIntensity;
    private string _vehicleType;
    private bool _isOnboardingComplete;
    private bool _isDatabaseInitialized;
    private bool _hasInitialized;
    private readonly SemaphoreSlim _initializeSemaphore = new(1, 1);
    private readonly SemaphoreSlim _databaseInitSemaphore = new(1, 1);
    private readonly ObservableCollection<string> _alarmSoundOptions = new()
    {
        "Default",
        "Alarm",
        "Chime",
        "Notification",
        "Ringtone"
    };
    private bool _wasOnline;
    private bool _availabilityChecked;
    private int _snoozeCount;
    private const int MaxSnoozeCount = 3;
    private string _primaryContactNumber = string.Empty;

    private DateTimeOffset? _lastSosSentAt;
    private static readonly TimeSpan SosCooldown = TimeSpan.FromSeconds(30);
    private const int MaxContactNameLength = 50;
    private const int MaxDisplayNameLength = 200;

    private string _authStatusText = string.Empty;

    private DateTime _lastMapLocationUpdate = DateTime.MinValue;
    private static readonly TimeSpan MapLocationUpdateInterval = TimeSpan.FromSeconds(5);
    private CancellationTokenSource? _searchCts;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<AlarmStage>? AlarmStageActivated;
    public event EventHandler<(double Lat, double Lon)>? LiveLocationUpdated;
    public event EventHandler<(double Lat, double Lon)>? CenterMapRequested;
    // Fires a JS string to be executed against the live WebView map; HomeView calls
    // EvaluateJavaScriptAsync. On re-appear, HomeView replays state via LastDestinationResult.
    public event EventHandler<string>? MapJsRequested;

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

    // Exposes the active destination so HomeView can replay setDestination() on re-appear
    // without a full WebView reload — null means map should show no destination marker.
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
            }
        }
    }

    public bool IsNotTracking => !IsTracking;

    public bool ShowStartTripCard => HasDestination && !IsTracking;

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
        private set => SetProperty(ref _destinationSummaryText, value);
    }

    public string DistanceToDestinationText
    {
        get => _distanceToDestinationText;
        private set => SetProperty(ref _distanceToDestinationText, value);
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
        AlarmStage.Stage3 => "YOU MIGHT MISS YOUR STOP.",
        _ => DistanceToDestinationText
    };

    public bool IsAlarmActive => CurrentAlarmStage != AlarmStage.None;
    public bool IsNotAlarmActive => !IsAlarmActive;
    public bool IsStage3Active => CurrentAlarmStage == AlarmStage.Stage3;
    public bool IsStage1Active => CurrentAlarmStage == AlarmStage.Stage1;
    public bool IsStage1Or2Active => CurrentAlarmStage == AlarmStage.Stage1 || CurrentAlarmStage == AlarmStage.Stage2;

    public string AuthStatusText
    {
        get => _authStatusText;
        private set => SetProperty(ref _authStatusText, value);
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
    public ObservableCollection<string> VehicleTypeOptions => _vehicleTypeOptions;
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

    public string VehicleType
    {
        get => _vehicleType;
        set
        {
            var valid = _vehicleTypeOptions.Contains(value, StringComparer.OrdinalIgnoreCase)
                ? _vehicleTypeOptions.First(o => o.Equals(value, StringComparison.OrdinalIgnoreCase))
                : "Jeepney";
            if (SetProperty(ref _vehicleType, valid))
                _preferencesService.VehicleType = valid;
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
    public ICommand ApplySavedRouteCommand { get; }
    public ICommand RemoveSavedRouteCommand { get; }
    public ICommand CompleteOnboardingCommand { get; }
    public ICommand RefreshAvailabilityCommand { get; }
    public ICommand ExportBackupCommand { get; }
    public ICommand RestoreBackupCommand { get; }
    public ICommand RefreshEarphoneStatusCommand { get; }
    public ICommand RequestBatteryOptimizationCommand { get; }
    public ICommand DismissAlarmCommand { get; }
    public ICommand SnoozeAlarmCommand { get; }
    public ICommand CenterOnUserCommand { get; }
    public ICommand OpenBiometricEnrollmentCommand { get; }
    public ICommand OpenChangePinCommand { get; }
    public ICommand RequestLocationPermissionCommand { get; }
    public ICommand RequestNotificationPermissionCommand { get; }
    public ICommand RequestBluetoothPermissionCommand { get; }

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
        IBiometricAuthService biometricAuthService,
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
        _biometricAuthService = biometricAuthService;
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
        _vehicleType = _preferencesService.VehicleType;
        _isOnboardingComplete = _preferencesService.IsOnboardingComplete;

        InitializeCommand = new Command(async () => await InitializeDatabaseAsync());
        SearchDestinationCommand = new Command(async () => await SearchDestinationAsync());
        SelectResultCommand = new Command<GeocodingResult>(SelectSearchResult);
        OpenMapsCommand = new Command(async () => await OpenMapsAsync());
        TriggerSosCommand = new Command(async () => await SendSosAsync());
        StartTrackingCommand = new Command(async () => await StartTrackingAsync());
        StopTrackingCommand = new Command(async () => await StopTrackingAsync());
        AddEmergencyContactCommand = new Command(async () => await AddEmergencyContactAsync());
        SetPrimaryContactCommand = new Command<EmergencyContact>(async contact => await SetPrimaryContactAsync(contact));
        RemoveEmergencyContactCommand = new Command<EmergencyContact>(async contact => await RemoveEmergencyContactAsync(contact));
        SaveDestinationCommand = new Command(async () => await SaveDestinationAsync());
        ToggleFavoriteCommand = new Command(async () => await ToggleFavoriteAsync());
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
            _snoozeCount = 0;
            CurrentAlarmStage = AlarmStage.None;
            await _alarmAudioService.DisableCriticalAudioAsync();
            LastActionText = "Alarm dismissed.";
        });
        SnoozeAlarmCommand = new Command(async () => await SnoozeAlarmAsync());
        CenterOnUserCommand = new Command(async () => await CenterOnUserAsync());
        OpenBiometricEnrollmentCommand = new Command(OpenBiometricEnrollment);
        OpenChangePinCommand = new Command(OpenChangePin);
        RequestLocationPermissionCommand = new Command(async () => await RequestLocationPermissionAsync());
        RequestNotificationPermissionCommand = new Command(async () => await RequestNotificationPermissionAsync());
        RequestBluetoothPermissionCommand = new Command(OpenBluetoothSettings);

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

#if !DEBUG
            StatusText = "Authenticating...";
            using var authTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            bool authenticated;
            try
            {
                authenticated = await _biometricAuthService.AuthenticateAsync(
                    "Unlock Alarma to continue.",
                    authTimeout.Token);
            }
            catch (OperationCanceledException)
            {
                StatusText = "Authentication timed out. Restart the app to try again.";
                return;
            }
            if (!authenticated)
            {
                StatusText = "Authentication required to continue.";
                return;
            }
#endif

            _hasInitialized = true;
            StatusText = "Ready to configure an offline-first trip.";
            await _notificationService.EnsureAlarmChannelAsync();
            await RefreshAvailabilityAsync();
            UpdateBatteryOptimizationStatus();
            UpdateEarphoneStatus();
            UpdateBackupStatus();
            UpdateAuthStatus();
            await InitializeDatabaseAsync();
            TrackingStatusText = "Tracking inactive.";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HomeController] InitializeAsync failed: {ex}");
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

        LastBackupText = $"Last backup: {_preferencesService.LastBackupUtc.Value.LocalDateTime:g}";
        OnPropertyChanged(nameof(HasBackupAvailable));
    }

    public void UpdateAuthStatus()
    {
#if ANDROID
        try
        {
            var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity
                as AndroidX.Fragment.App.FragmentActivity;
            if (activity is null)
            {
                AuthStatusText = "Authentication status unavailable.";
                return;
            }
            var mgr = AndroidX.Biometric.BiometricManager.From(activity);
            bool deviceCredentialSupported =
                Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.R;
            int auth = deviceCredentialSupported
                ? AndroidX.Biometric.BiometricManager.Authenticators.BiometricStrong
                  | AndroidX.Biometric.BiometricManager.Authenticators.DeviceCredential
                : AndroidX.Biometric.BiometricManager.Authenticators.BiometricWeak;
            AuthStatusText = mgr.CanAuthenticate(auth) ==
                AndroidX.Biometric.BiometricManager.BiometricSuccess
                ? "Biometric or PIN is set up."
                : "No biometric or PIN enrolled.";
        }
        catch
        {
            AuthStatusText = "Authentication status unavailable.";
        }
#else
        AuthStatusText = "Biometric auth is Android-only.";
#endif
    }

    private static void OpenBiometricEnrollment()
    {
#if ANDROID
        try
        {
#pragma warning disable CA1416
            Android.Content.Intent intent;
            if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.R)
            {
                intent = new Android.Content.Intent(Android.Provider.Settings.ActionBiometricEnroll);
                // Tell the enrollment screen which authenticators this app actually uses.
                // "android.provider.extra.BIOMETRIC_AUTHENTICATORS_ALLOWED" — added in API 30,
                // tells enrollment screen to show only the authenticators this app uses.
                intent.PutExtra(
                    "android.provider.extra.BIOMETRIC_AUTHENTICATORS_ALLOWED",
                    AndroidX.Biometric.BiometricManager.Authenticators.BiometricStrong
                    | AndroidX.Biometric.BiometricManager.Authenticators.DeviceCredential);
            }
            else
            {
                intent = new Android.Content.Intent(Android.Provider.Settings.ActionSecuritySettings);
            }
#pragma warning restore CA1416
            intent.SetFlags(Android.Content.ActivityFlags.NewTask);
            Android.App.Application.Context.StartActivity(intent);
        }
        catch { }
#endif
    }

    private static void OpenChangePin()
    {
#if ANDROID
        try
        {
            var intent = new Android.Content.Intent(Android.Provider.Settings.ActionSecuritySettings);
            intent.SetFlags(Android.Content.ActivityFlags.NewTask);
            Android.App.Application.Context.StartActivity(intent);
        }
        catch { }
#endif
    }

    private async Task RequestLocationPermissionAsync()
    {
        try
        {
            var granted = await _permissionsService.EnsureLocationPermissionsAsync(requireBackground: false);
            AvailabilityStatusText = granted
                ? "Location permission granted."
                : "Location permission denied. Enable it in Android Settings → Apps → Alarma → Permissions.";
            LastActionText = AvailabilityStatusText;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HomeController] RequestLocationPermissionAsync failed: {ex}");
            LastActionText = "Could not request location permission.";
        }
    }

    private async Task RequestNotificationPermissionAsync()
    {
        try
        {
            var granted = await _permissionsService.EnsureNotificationPermissionAsync();
            LastActionText = granted
                ? "Notification permission granted."
                : "Notification permission denied. Enable it in Android Settings → Apps → Alarma → Permissions.";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HomeController] RequestNotificationPermissionAsync failed: {ex}");
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
                LastActionText = "Local SQLite database ready.";
            }
            else
            {
                LastActionText = "Local data refreshed.";
            }

            await LoadLocalDataAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HomeController] InitializeDatabaseAsync failed: {ex}");
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
            System.Diagnostics.Debug.WriteLine($"[HomeController] LoadEmergencyContactsAsync failed: {ex}");
            LastActionText = "Failed to load emergency contacts.";
        }
    }

    private async Task LoadSavedRoutesAsync()
    {
        try
        {
            var routes = await _databaseService.GetSavedRoutesAsync();
            var orderedRoutes = routes
                .OrderBy(route => route.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            ReplaceCollection(_savedRoutes, orderedRoutes);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HomeController] LoadSavedRoutesAsync failed: {ex}");
            LastActionText = "Failed to load saved routes.";
        }
    }

    private async Task LoadTripHistoryAsync()
    {
        try
        {
            var history = await _databaseService.GetTripHistoryAsync();
            var orderedHistory = history
                .OrderByDescending(entry => entry.StartedAt)
                .Take(TripHistoryLimit)
                .ToList();
            ReplaceCollection(_tripHistoryEntries, orderedHistory);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HomeController] LoadTripHistoryAsync failed: {ex}");
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
            System.Diagnostics.Debug.WriteLine($"[HomeController] ExportBackupAsync failed: {ex}");
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
            VehicleType = _preferencesService.VehicleType;
            UpdateBackupStatus();
            await LoadLocalDataAsync();
            BackupStatusText = $"Backup restored from {path}.";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HomeController] RestoreBackupAsync failed: {ex}");
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
            var results = await _geocodingService.SearchAsync(query, token);
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
            System.Diagnostics.Debug.WriteLine($"[HomeController] SearchDestinationAsync failed: {ex}");
            LastActionText = "Search failed. Check your internet connection and try again.";
            ClearDestination();
        }
        finally
        {
            IsSearchingDestination = false;
        }
    }

    private void SelectSearchResult(GeocodingResult result)
    {
        if (result is null) return;
        ReplaceCollection(_searchResults, Array.Empty<GeocodingResult>());
        var safeResult = result.DisplayName.Length > MaxDisplayNameLength
            ? result with { DisplayName = result.DisplayName[..MaxDisplayNameLength] }
            : result;
        SetDestination(safeResult, "Search result");
        LastActionText = $"Destination set: {safeResult.DisplayName}.";
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
            System.Diagnostics.Debug.WriteLine($"[HomeController] OpenMapsAsync failed: {ex}");
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

        // Use custom name if provided, otherwise derive from display name.
        // Clamp to 30 chars so long place names (e.g. universities) don't fail validation.
        var rawName = string.IsNullOrWhiteSpace(NewRouteName)
            ? _lastDestinationResult.DisplayName
            : NewRouteName.Trim();
        if (string.IsNullOrWhiteSpace(rawName))
            rawName = "Saved route";
        var routeName = rawName.Length > 30 ? rawName[..30].TrimEnd() : rawName;
        if (routeName.Length < 2)
            routeName = "Saved route";

        if (_savedRoutes.Any(route => string.Equals(route.Name, routeName, StringComparison.OrdinalIgnoreCase)))
        {
            // Append a short suffix to avoid collision while still saving.
            routeName = routeName.Length <= 27 ? routeName + " (2)" : routeName[..27] + " (2)";
        }

        var route = new SavedRoute
        {
            Name = routeName,
            DestinationLatitude = _lastDestinationResult.Latitude,
            DestinationLongitude = _lastDestinationResult.Longitude,
            Notes = $"Saved from search: {_lastDestinationResult.DisplayName}"
        };

        try
        {
            await _databaseService.SaveRouteAsync(route);
            NewRouteName = string.Empty;
            await LoadSavedRoutesAsync();
            LastActionText = $"Saved: {route.Name}.";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HomeController] SaveDestinationAsync failed: {ex}");
            LastActionText = "Failed to save route. Please try again.";
        }
    }

    private Task ApplySavedRouteAsync(SavedRoute route)
    {
        if (route is null)
        {
            return Task.CompletedTask;
        }

        var destination = new GeocodingResult(route.Name, route.DestinationLatitude, route.DestinationLongitude);
        SetDestination(destination, "Saved route");
        LastActionText = $"Loaded saved route: {route.Name}.";
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
            LastActionText = $"Removed saved route: {route.Name}.";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HomeController] RemoveSavedRouteAsync failed: {ex}");
            LastActionText = "Failed to remove route. Please try again.";
        }
    }

    private async Task AddEmergencyContactAsync()
    {
        var name = NewContactName?.Trim() ?? string.Empty;
        var number = NewContactNumber?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(number))
        {
            LastActionText = "Enter a contact name and phone number.";
            return;
        }

        if (name.Length > MaxContactNameLength)
        {
            LastActionText = $"Contact name must be {MaxContactNameLength} characters or fewer.";
            return;
        }

        if (_emergencyContacts.Count >= MaxEmergencyContacts)
        {
            LastActionText = $"Maximum {MaxEmergencyContacts} emergency contacts allowed. Remove one to add another.";
            return;
        }

        if (!IsValidPhilippineNumber(number))
        {
            LastActionText = "Phone number must be in format 09XXXXXXXXX or +639XXXXXXXXX.";
            return;
        }

        if (_emergencyContacts.Any(contact =>
                string.Equals(contact.PhoneNumber?.Trim(), number, StringComparison.OrdinalIgnoreCase)))
        {
            LastActionText = "A contact with this phone number already exists.";
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
            System.Diagnostics.Debug.WriteLine($"[HomeController] AddEmergencyContactAsync failed: {ex}");
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
            System.Diagnostics.Debug.WriteLine($"[HomeController] SetPrimaryContactAsync failed: {ex}");
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
            System.Diagnostics.Debug.WriteLine($"[HomeController] RemoveEmergencyContactAsync failed: {ex}");
            LastActionText = "Failed to remove contact. Please try again.";
        }
    }

    private async Task SendSosAsync()
    {
        if (_lastSosSentAt.HasValue && DateTimeOffset.UtcNow - _lastSosSentAt.Value < SosCooldown)
        {
            var remaining = (int)(SosCooldown - (DateTimeOffset.UtcNow - _lastSosSentAt.Value)).TotalSeconds;
            LastActionText = $"SOS sent recently. Wait {remaining}s before sending again.";
            return;
        }

        if (!await _permissionsService.EnsureSmsPermissionAsync())
        {
            LastActionText = "SMS permission is required to send SOS alerts.";
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

        var dndAccessGranted = await _permissionsService.EnsureDoNotDisturbAccessAsync();
        var location = await _locationService.GetLastKnownLocationAsync();
        string message;
        if (location is null)
        {
            message = "Alarma SOS: Location unavailable. Please check on me.";
        }
        else
        {
            var lat = location.Latitude.ToString("F5", System.Globalization.CultureInfo.InvariantCulture);
            var lon = location.Longitude.ToString("F5", System.Globalization.CultureInfo.InvariantCulture);
            message = $"Alarma SOS: I may need help. https://maps.google.com/?q={lat},{lon} — {location.Timestamp:t}";
        }

        try
        {
            await _alarmAudioService.TriggerAlarmAsync(AlarmStage.Stage3, AlarmSound, VibrationOnly, VibrationIntensity);
            await _smsService.SendEmergencySmsAsync(message, recipients);
            _lastSosSentAt = DateTimeOffset.UtcNow;
            LastActionText = dndAccessGranted
                ? $"SOS sent to {recipients.Count} contact(s)."
                : $"SOS sent to {recipients.Count} contact(s). Grant DND access for critical audio.";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HomeController] SendSosAsync failed: {ex}");
            LastActionText = "Failed to send SOS. Ensure SMS permission is granted and try again.";
        }
    }

    private async Task StartTrackingAsync()
    {
        if (IsTracking)
        {
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
        _routeDeviationAlerted = false;
        CurrentAlarmStage = AlarmStage.None;
        _lastSpeedMetersPerSecond = 0;
        _minDistanceToDestination = double.MaxValue;
        _snoozeCount = 0;
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
            System.Diagnostics.Debug.WriteLine($"[HomeController] StartTrackingAsync failed: {ex}");
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
            System.Diagnostics.Debug.WriteLine($"[HomeController] StopTrackingAsync failed: {ex}");
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
                System.Diagnostics.Debug.WriteLine($"[HomeController] StopTrackingAsync save history failed: {ex}");
                LastActionText = "Failed to save trip history.";
            }
        }

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
                System.Diagnostics.Debug.WriteLine($"[HomeController] HandleLocationUpdateAsync failed: {ex}");
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

        if (_lastTrackedLocation is not null)
        {
            var deltaSeconds = (snapshot.Timestamp - _lastTrackedLocation.Timestamp).TotalSeconds;
            var deltaMeters = CalculateDistanceMeters(_lastTrackedLocation, snapshot);
            if (deltaSeconds > 0)
            {
                _lastSpeedMetersPerSecond = deltaMeters / deltaSeconds;
            }

            _totalDistanceMeters += deltaMeters;
        }

        _lastTrackedLocation = snapshot;
        var distanceKm = _totalDistanceMeters / MetersPerKilometer;
        TrackingStatusText = $"Tracking active: {distanceKm:F2} km traveled.";

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
        var deviationThreshold = RouteDeviationBufferMeters + accuracyBuffer;
        var vehicleMinDistance = VehicleType switch
        {
            "City Bus" => MinAlarmDistanceCityBusMeters,
            "UV Express" => MinAlarmDistanceUvExpressMeters,
            _ => MinAlarmDistanceJeepneyMeters
        };
        var adaptiveLeadDistance = Math.Max(
            vehicleMinDistance,
            _lastSpeedMetersPerSecond * AlarmLeadMinutes * 60);
        var adaptiveLeadThreshold = adaptiveLeadDistance + accuracyBuffer;
        _minDistanceToDestination = Math.Min(_minDistanceToDestination, distanceToDestination);
        DistanceToDestinationText = $"Destination is {distanceToDestination / MetersPerKilometer:F2} km away.";

        if (_currentAlarmStage == AlarmStage.None && distanceToDestination <= adaptiveLeadThreshold)
        {
            await TriggerAlarmStageAsync(
                AlarmStage.Stage1,
                "Alarm stage 1",
                "Approaching destination. Prepare to disembark.",
                reroute: false,
                allowRepeat: false);
        }

        if (!_hasArrivedAtDestination && distanceToDestination <= ArrivalThresholdMeters)
        {
            _hasArrivedAtDestination = true;
            await TriggerAlarmStageAsync(
                AlarmStage.Stage2,
                "Alarm stage 2",
                "Arrived near destination. Stay alert.",
                reroute: false,
                allowRepeat: false);
            return;
        }

        if (_hasArrivedAtDestination && !_overshootAlerted && distanceToDestination >= overshootThreshold)
        {
            _overshootAlerted = true;
            await TriggerAlarmStageAsync(
                AlarmStage.Stage3,
                "Alarm stage 3",
                "Overshoot detected. Rerouting to destination.",
                reroute: true,
                allowRepeat: false);
            return;
        }

        if (!_hasArrivedAtDestination && !_routeDeviationAlerted
            && distanceToDestination > _minDistanceToDestination + deviationThreshold)
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
            System.Diagnostics.Debug.WriteLine($"[HomeController] TriggerAlarmStageAsync failed: {ex}");
            LastActionText = "Alarm alert failed.";
        }
    }

    private async Task SnoozeAlarmAsync()
    {
        if (CurrentAlarmStage == AlarmStage.None || CurrentAlarmStage == AlarmStage.Stage3)
            return;

        _snoozeCount++;
        if (_activeTrip is not null)
            _activeTrip.SnoozeCount = _snoozeCount;

        await _alarmAudioService.DisableCriticalAudioAsync();

        if (_snoozeCount >= MaxSnoozeCount)
        {
            await TriggerAlarmStageAsync(
                AlarmStage.Stage3,
                "Alarm Stage 3",
                "Max snoozes reached. You must disembark now!",
                reroute: false,
                allowRepeat: false);
        }
        else
        {
            LastActionText = $"Alarm snoozed ({_snoozeCount}/{MaxSnoozeCount}).";
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
        _routeDeviationAlerted = false;
        CurrentAlarmStage = AlarmStage.None;
        _lastSpeedMetersPerSecond = 0;
        _snoozeCount = 0;
        OnPropertyChanged(nameof(HasDestination));
        OnPropertyChanged(nameof(CanSaveRoute));
        OnPropertyChanged(nameof(ShowStartTripCard));
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
            System.Diagnostics.Debug.WriteLine($"[HomeController] CenterOnUserAsync failed: {ex}");
            LastActionText = "Could not get current location. Ensure GPS is enabled.";
        }
    }

    private void ClearDestination()
    {
        _lastDestinationResult = null;
        DestinationSummaryText = "No destination selected.";
        DistanceToDestinationText = string.Empty;
        ClearMapTile();
        _minDistanceToDestination = double.MaxValue;
        _hasArrivedAtDestination = false;
        _overshootAlerted = false;
        _routeDeviationAlerted = false;
        CurrentAlarmStage = AlarmStage.None;
        _lastSpeedMetersPerSecond = 0;
        OnPropertyChanged(nameof(HasDestination));
        OnPropertyChanged(nameof(CanSaveRoute));
        OnPropertyChanged(nameof(ShowStartTripCard));
        OnPropertyChanged(nameof(IsDestinationSaved));
    }

    private static void ReplaceCollection<T>(ObservableCollection<T> collection, IEnumerable<T> items)
    {
        collection.Clear();
        foreach (var item in items)
        {
            collection.Add(item);
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
        return Math.Abs(route.DestinationLatitude - destination.Latitude) <= tolerance
            && Math.Abs(route.DestinationLongitude - destination.Longitude) <= tolerance;
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

    private void ClearMapTile()
    {
        MapJsRequested?.Invoke(this, "clearDestination()");
    }

    // Security Considerations (OWASP Top 10)
    // A03 Injection: All dynamic values passed to JS functions (setDestination / updateUserLocation /
    //   centerOnUser) use InvariantCulture F6 numeric strings or JsonSerializer.Serialize — no raw
    //   user strings reach the eval context.
    // A05 Security Misconfiguration: CSP now explicitly allows CartoDB tile domains on connect-src
    //   (Leaflet 1.9.x may use XHR/fetch for retina-tile probing); img-src covers actual tile images.
    //   The map HTML is constructed once and never replaced — eliminating the full-WebView-reload
    //   that caused gray tiles when the user panned or tapped Center-on-me.
    private static HtmlWebViewSource BuildDefaultMapHtml()
    {
        var html = """
            <!DOCTYPE html>
            <html>
            <head>
              <meta charset="utf-8"/>
              <meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src https://unpkg.com 'unsafe-inline'; style-src https://unpkg.com 'unsafe-inline'; img-src https://*.cartocdn.com https://*.openstreetmap.org data: blob:; connect-src https://*.cartocdn.com https://unpkg.com"/>
              <meta name="viewport" content="width=device-width,initial-scale=1,maximum-scale=1,user-scalable=no"/>
              <link rel="stylesheet" href="https://unpkg.com/leaflet@1.9.4/dist/leaflet.css"/>
              <script src="https://unpkg.com/leaflet@1.9.4/dist/leaflet.js"></script>
              <style>
                html,body,#map{margin:0;padding:0;width:100%;height:100%}
                .leaflet-tile{filter:sepia(0.8) hue-rotate(250deg) saturate(2.5) brightness(0.75)}
              </style>
            </head>
            <body>
              <div id="map"></div>
              <script>
                var map = L.map('map',{zoomControl:false}).setView([14.5995,120.9842],12);
                L.tileLayer('https://{s}.basemaps.cartocdn.com/dark_all/{z}/{x}/{y}{r}.png',{attribution:'&copy; OSM &copy; CARTO',subdomains:'abcd',maxZoom:19}).addTo(map);
                var _userMarker=null;
                var _destMarker=null;
                function updateUserLocation(lat,lon){
                  var ll=[lat,lon];
                  if(_userMarker){_userMarker.setLatLng(ll);return;}
                  _userMarker=L.circleMarker(ll,{radius:8,color:'#fff',weight:2,fillColor:'#4A90D9',fillOpacity:0.9}).addTo(map).bindTooltip('You',{permanent:false,direction:'top'});
                }
                function centerOnUser(lat,lon){updateUserLocation(lat,lon);map.flyTo([lat,lon],16);}
                function setDestination(lat,lon,label){
                  clearDestination();
                  _destMarker=L.marker([lat,lon]).addTo(map).bindPopup(label).openPopup();
                  map.flyTo([lat,lon],15);
                }
                function clearDestination(){
                  if(_destMarker){map.removeLayer(_destMarker);_destMarker=null;}
                }
              </script>
            </body>
            </html>
            """;
        return new HtmlWebViewSource { Html = html, BaseUrl = "https://unpkg.com" };
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
