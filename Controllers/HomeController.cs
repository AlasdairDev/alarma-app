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
    private const int DefaultMapZoom = 12;
    private const double EarthRadiusMeters = 6_371_000;
    private const double MetersPerKilometer = 1000;
    private const double ArrivalThresholdMeters = 200;
    private const double OvershootBufferMeters = 250;
    private const double OvershootThresholdMeters = ArrivalThresholdMeters + OvershootBufferMeters;
    private const double RouteDeviationBufferMeters = OvershootBufferMeters;
    private const int TripHistoryLimit = 20;
    private const int MaxSavedRoutes = 5;

    private readonly DatabaseService _databaseService;
    private readonly PreferencesService _preferencesService;
    private readonly PermissionsService _permissionsService;
    private readonly GeocodingService _geocodingService;
    private readonly OpenStreetMapTileService _tileService;
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
    private ImageSource? _mapTileSource;
    private bool _hasMapTile;
    private bool _isTracking;
    private string _trackingStatusText = "Tracking inactive.";
    private LocationSnapshot? _lastTrackedLocation;
    private double _totalDistanceMeters;
    private TripHistory? _activeTrip;
    private bool _hasArrivedAtDestination;
    private bool _overshootAlerted;
    private bool _routeDeviationAlerted;
    private AlarmStage _currentAlarmStage = AlarmStage.None;
    private double _lastSpeedMetersPerSecond;
    private double _minDistanceToDestination = double.MaxValue;

    private string _newContactName = string.Empty;
    private string _newContactNumber = string.Empty;
    private string _newRouteName = string.Empty;
    private string _alarmSound;
    private double _alarmLeadMinutes;
    private bool _vibrationOnly;
    private bool _isOnboardingComplete;
    private bool _isDatabaseInitialized;
    private bool _hasInitialized;
    private readonly SemaphoreSlim _initializeSemaphore = new(1, 1);
    private readonly SemaphoreSlim _databaseInitSemaphore = new(1, 1);
    private readonly ObservableCollection<string> _alarmSoundOptions = new()
    {
        "Default",
        "Alarm",
        "Notification",
        "Ringtone"
    };
    private bool _wasOnline;
    private bool _availabilityChecked;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string ConnectivityText
    {
        get => _connectivityText;
        private set => SetProperty(ref _connectivityText, value);
    }

    public string LastActionText
    {
        get => _lastActionText;
        private set => SetProperty(ref _lastActionText, value);
    }

    public ImageSource? MapTileSource
    {
        get => _mapTileSource;
        private set => SetProperty(ref _mapTileSource, value);
    }

    public bool HasMapTile
    {
        get => _hasMapTile;
        private set => SetProperty(ref _hasMapTile, value);
    }

    public bool IsTracking
    {
        get => _isTracking;
        private set
        {
            if (SetProperty(ref _isTracking, value))
            {
                OnPropertyChanged(nameof(IsNotTracking));
            }
        }
    }

    public bool IsNotTracking => !IsTracking;

    public string TrackingStatusText
    {
        get => _trackingStatusText;
        private set => SetProperty(ref _trackingStatusText, value);
    }

    public string DestinationQuery
    {
        get => _destinationQuery;
        set => SetProperty(ref _destinationQuery, value);
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
        private set => SetProperty(ref _earphoneStatusText, value);
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

    public bool HasSavedRoutes => _savedRoutes.Count > 0;

    public bool HasTripHistory => _tripHistoryEntries.Count > 0;

    public bool CanSendSos => HasEmergencyContacts || !string.IsNullOrWhiteSpace(_preferencesService.EmergencyContactNumber);

    public bool CanSaveRoute => _lastDestinationResult is not null;

    public bool HasBackupAvailable => _preferencesService.LastBackupUtc.HasValue;

    public ObservableCollection<EmergencyContact> EmergencyContacts => _emergencyContacts;

    public ObservableCollection<SavedRoute> SavedRoutes => _savedRoutes;

    public ObservableCollection<TripHistory> TripHistoryEntries => _tripHistoryEntries;

    public ICommand InitializeCommand { get; }
    public ICommand SearchDestinationCommand { get; }
    public ICommand OpenMapsCommand { get; }
    public ICommand SendTestSmsCommand { get; }
    public ICommand StartTrackingCommand { get; }
    public ICommand StopTrackingCommand { get; }
    public ICommand AddEmergencyContactCommand { get; }
    public ICommand SetPrimaryContactCommand { get; }
    public ICommand RemoveEmergencyContactCommand { get; }
    public ICommand SaveDestinationCommand { get; }
    public ICommand ApplySavedRouteCommand { get; }
    public ICommand RemoveSavedRouteCommand { get; }
    public ICommand CompleteOnboardingCommand { get; }
    public ICommand RefreshAvailabilityCommand { get; }
    public ICommand ExportBackupCommand { get; }
    public ICommand RestoreBackupCommand { get; }
    public ICommand RefreshEarphoneStatusCommand { get; }
    public ICommand RequestBatteryOptimizationCommand { get; }

    public HomeController(
        DatabaseService databaseService,
        PreferencesService preferencesService,
        PermissionsService permissionsService,
        GeocodingService geocodingService,
        OpenStreetMapTileService tileService,
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
        _tileService = tileService;
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
        _isOnboardingComplete = _preferencesService.IsOnboardingComplete;

        InitializeCommand = new Command(async () => await InitializeDatabaseAsync());
        SearchDestinationCommand = new Command(async () => await SearchDestinationAsync());
        OpenMapsCommand = new Command(async () => await OpenMapsAsync());
        SendTestSmsCommand = new Command(async () => await SendTestSmsAsync());
        StartTrackingCommand = new Command(async () => await StartTrackingAsync());
        StopTrackingCommand = new Command(async () => await StopTrackingAsync());
        AddEmergencyContactCommand = new Command(async () => await AddEmergencyContactAsync());
        SetPrimaryContactCommand = new Command<EmergencyContact>(async contact => await SetPrimaryContactAsync(contact));
        RemoveEmergencyContactCommand = new Command<EmergencyContact>(async contact => await RemoveEmergencyContactAsync(contact));
        SaveDestinationCommand = new Command(async () => await SaveDestinationAsync());
        ApplySavedRouteCommand = new Command<SavedRoute>(async route => await ApplySavedRouteAsync(route));
        RemoveSavedRouteCommand = new Command<SavedRoute>(async route => await RemoveSavedRouteAsync(route));
        CompleteOnboardingCommand = new Command(CompleteOnboarding);
        RefreshAvailabilityCommand = new Command(async () => await RefreshAvailabilityAsync());
        ExportBackupCommand = new Command(async () => await ExportBackupAsync());
        RestoreBackupCommand = new Command(async () => await RestoreBackupAsync());
        RefreshEarphoneStatusCommand = new Command(UpdateEarphoneStatus);
        RequestBatteryOptimizationCommand = new Command(async () => await RequestBatteryOptimizationAsync());

        _locationService.LocationUpdated += OnLocationUpdated;
        _emergencyContacts.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasEmergencyContacts));
            OnPropertyChanged(nameof(CanSendSos));
        };
        _savedRoutes.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasSavedRoutes));
        _tripHistoryEntries.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasTripHistory));
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

            StatusText = "Authenticating...";
            var authenticated = await _biometricAuthService.AuthenticateAsync(
                "Unlock Alarma to continue.",
                CancellationToken.None);
            if (!authenticated)
            {
                StatusText = "Authentication required to continue.";
                return;
            }

            _hasInitialized = true;
            StatusText = "Ready to configure an offline-first trip.";
            await _notificationService.EnsureAlarmChannelAsync();
            await RefreshAvailabilityAsync();
            UpdateBatteryOptimizationStatus();
            UpdateEarphoneStatus();
            UpdateBackupStatus();
            await InitializeDatabaseAsync();
            TrackingStatusText = "Tracking inactive.";
        }
        catch (Exception ex)
        {
            StatusText = "Initialization encountered an error.";
            LastActionText = $"Initialization error: {ex.Message}";
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
            LastActionText = $"Database initialization failed: {ex.Message}";
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
            if (primary is not null)
            {
                _preferencesService.EmergencyContactNumber = primary.PhoneNumber;
            }
            else
            {
                _preferencesService.EmergencyContactNumber = string.Empty;
            }
        }
        catch (Exception ex)
        {
            LastActionText = $"Failed to load emergency contacts: {ex.Message}";
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
            LastActionText = $"Failed to load saved routes: {ex.Message}";
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
            LastActionText = $"Failed to load trip history: {ex.Message}";
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
            BackupStatusText = $"Backup export failed: {ex.Message}";
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
            UpdateBackupStatus();
            await LoadLocalDataAsync();
            BackupStatusText = $"Backup restored from {path}.";
        }
        catch (Exception ex)
        {
            BackupStatusText = $"Backup restore failed: {ex.Message}";
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

        try
        {
            DestinationQuery = query;
            var results = await _geocodingService.SearchAsync(query, CancellationToken.None);
            var first = results.FirstOrDefault();
            if (first is null)
            {
                LastActionText = "No destinations found.";
                ClearDestination();
                return;
            }

            SetDestination(first, "Search result");
            LastActionText = $"Sample result: {first.DisplayName}.";
        }
        catch (Exception ex)
        {
            LastActionText = $"Destination search failed: {ex.Message}";
            ClearDestination();
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
            LastActionText = $"Unable to open Google Maps: {ex.Message}";
        }
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

        var routeName = string.IsNullOrWhiteSpace(NewRouteName)
            ? _lastDestinationResult.DisplayName
            : NewRouteName.Trim();
        if (string.IsNullOrWhiteSpace(routeName))
        {
            routeName = "Saved route";
        }
        if (_savedRoutes.Any(route => string.Equals(route.Name, routeName, StringComparison.OrdinalIgnoreCase)))
        {
            LastActionText = "A saved route with this name already exists.";
            return;
        }

        if (_savedRoutes.Any(route => IsSameDestination(route, _lastDestinationResult)))
        {
            LastActionText = "This destination is already saved.";
            return;
        }
        var route = new SavedRoute
        {
            Name = routeName,
            DestinationLatitude = _lastDestinationResult.Latitude,
            DestinationLongitude = _lastDestinationResult.Longitude,
            Notes = string.IsNullOrWhiteSpace(NewRouteName)
                ? null
                : $"Saved from destination search: {_lastDestinationResult.DisplayName}"
        };

        try
        {
            await _databaseService.SaveRouteAsync(route);
            NewRouteName = string.Empty;
            await LoadSavedRoutesAsync();
            LastActionText = $"Saved route: {route.Name}.";
        }
        catch (Exception ex)
        {
            LastActionText = $"Failed to save route: {ex.Message}";
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
            LastActionText = $"Failed to remove route: {ex.Message}";
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
            LastActionText = $"Failed to save emergency contact: {ex.Message}";
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
            LastActionText = $"Failed to update primary contact: {ex.Message}";
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
            LastActionText = $"Failed to remove contact: {ex.Message}";
        }
    }

    private async Task SendTestSmsAsync()
    {
        if (!await _permissionsService.EnsureSmsPermissionAsync())
        {
            LastActionText = "SMS permission is required to send SOS alerts.";
            return;
        }

        var recipients = _emergencyContacts
            .Where(contact => contact.IsPrimary)
            .Select(contact => contact.PhoneNumber)
            .ToList();
        if (!recipients.Any())
        {
            var fallback = _preferencesService.EmergencyContactNumber;
            if (!string.IsNullOrWhiteSpace(fallback))
            {
                recipients.Add(fallback);
            }
        }

        recipients = recipients
            .Where(number => !string.IsNullOrWhiteSpace(number))
            .Select(number => number.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!recipients.Any())
        {
            LastActionText = "Configure an emergency contact before sending SMS.";
            return;
        }

        var dndAccessGranted = await _permissionsService.EnsureDoNotDisturbAccessAsync();
        var location = await _locationService.GetLastKnownLocationAsync();
        var message = location is null
            ? "Alarma SOS: Location unavailable."
            : $"Alarma SOS: Lat {location.Latitude:F5}, Lon {location.Longitude:F5} at {location.Timestamp:O}.";

        try
        {
            await _alarmAudioService.TriggerAlarmAsync(AlarmStage.Stage3, AlarmSound, VibrationOnly);
            await _smsService.SendEmergencySmsAsync(message, recipients);
            LastActionText = dndAccessGranted
                ? "Emergency SMS request sent to primary contact."
                : "Emergency SMS sent. Grant DND access to allow critical audio overrides.";
        }
        catch (Exception ex)
        {
            LastActionText = $"Failed to send SOS message: {ex.Message}";
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
        _currentAlarmStage = AlarmStage.None;
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
            await _alarmAudioService.DisableCriticalAudioAsync();
            LastActionText = $"Unable to start tracking: {ex.Message}";
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
            await _alarmAudioService.DisableCriticalAudioAsync();
        }
        catch (Exception ex)
        {
            LastActionText = $"Unable to stop tracking cleanly: {ex.Message}";
        }

        IsTracking = false;
        var distanceKm = _totalDistanceMeters / MetersPerKilometer;
        TrackingStatusText = $"Tracking stopped. Distance {distanceKm:F2} km.";
        _currentAlarmStage = AlarmStage.None;

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
                LastActionText = $"Failed to save trip history: {ex.Message}";
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
                LastActionText = $"Location update error: {ex.Message}";
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
        var adaptiveLeadDistance = Math.Max(
            ArrivalThresholdMeters,
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
            if (allowRepeat || stage > _currentAlarmStage)
            {
                if (stage > _currentAlarmStage)
                {
                    _currentAlarmStage = stage;
                }

                await _alarmAudioService.TriggerAlarmAsync(stage, AlarmSound, VibrationOnly);
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
            LastActionText = $"Alert failed: {ex.Message}";
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
        var tileUri = _tileService.GetTileUri(destination.Latitude, destination.Longitude, DefaultMapZoom);
        MapTileSource = ImageSource.FromUri(tileUri);
        HasMapTile = true;
        _minDistanceToDestination = double.MaxValue;
        _hasArrivedAtDestination = false;
        _overshootAlerted = false;
        _routeDeviationAlerted = false;
        _currentAlarmStage = AlarmStage.None;
        _lastSpeedMetersPerSecond = 0;
        OnPropertyChanged(nameof(HasDestination));
        OnPropertyChanged(nameof(CanSaveRoute));
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
        _currentAlarmStage = AlarmStage.None;
        _lastSpeedMetersPerSecond = 0;
        OnPropertyChanged(nameof(HasDestination));
        OnPropertyChanged(nameof(CanSaveRoute));
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
        MapTileSource = null;
        HasMapTile = false;
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
