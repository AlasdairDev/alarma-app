// =============================================================================
//  BluetoothUiSyncTests.cs
// -----------------------------------------------------------------------------
//  ISOLATED, MOCKED tests for the Bluetooth → ViewModel UI-sync logic.
//
//  The real native listener (AndroidBluetoothMonitor, a BroadcastReceiver) and
//  the real earphone probe (AndroidEarphoneService over AudioManager) can't run
//  on a build server. So we mock test-local interfaces with the SAME shape as the
//  production IBluetoothMonitor / IEarphoneService, raise their events with Moq to
//  simulate the hardware broadcasting, and assert the ViewModel updates its UI
//  state properties exactly as HomeController does.
//
//  The SUT (BluetoothSyncViewModel) mirrors HomeController's OnBluetoothStateChanged
//  / OnBluetoothDeviceConnectionChanged / UpdateEarphoneStatus decision logic. The
//  platform plumbing those handlers also do (Task.Delay settle + MainThread
//  marshaling) is intentionally omitted — it's not decision logic and would make
//  the test non-deterministic. Self-contained per project convention; no
//  production code referenced or modified.
// =============================================================================

using System.ComponentModel;
using System.Runtime.CompilerServices;
using Moq;
using Xunit;

namespace AlarmaApp.Tests;

// ── Test doubles mirroring the production interfaces ──────────────────────────
public interface ITestBluetoothMonitor
{
    bool IsEnabled { get; }
    event EventHandler<bool>? StateChanged;
    event EventHandler? DeviceConnectionChanged;
}

public interface ITestEarphoneService
{
    (bool IsConnected, string Details) GetConnectionStatus();
}

/// <summary>
/// Mirror of the relevant HomeController state + handlers: the Settings Bluetooth
/// toggle, its label, and the transient earphone pill — kept in sync with the
/// (mocked) hardware. Raises PropertyChanged so the bindings would update.
/// </summary>
public sealed class BluetoothSyncViewModel : INotifyPropertyChanged
{
    private readonly ITestEarphoneService _earphones;
    private bool? _lastEarphoneConnected;

    public event PropertyChangedEventHandler? PropertyChanged;

    public BluetoothSyncViewModel(ITestBluetoothMonitor monitor, ITestEarphoneService earphones)
    {
        _earphones = earphones;
        monitor.StateChanged += OnStateChanged;
        monitor.DeviceConnectionChanged += OnDeviceConnectionChanged;

        // Seed from the current hardware state, exactly like InitializeAsync.
        IsBluetoothOn = monitor.IsEnabled;
        BluetoothLabel = monitor.IsEnabled ? "On" : "Off";
        UpdateEarphoneStatus(announceTransitions: false); // baseline only — no pill on startup
    }

    private bool _isBluetoothOn;
    public bool IsBluetoothOn
    {
        get => _isBluetoothOn;
        private set => SetProperty(ref _isBluetoothOn, value);
    }

    private string _bluetoothLabel = "Off";
    public string BluetoothLabel
    {
        get => _bluetoothLabel;
        private set => SetProperty(ref _bluetoothLabel, value);
    }

    private bool _isEarphonePillVisible;
    public bool IsEarphonePillVisible
    {
        get => _isEarphonePillVisible;
        private set => SetProperty(ref _isEarphonePillVisible, value);
    }

    private string _earphonePillText = "Earphones Connected";
    public string EarphonePillText
    {
        get => _earphonePillText;
        private set => SetProperty(ref _earphonePillText, value);
    }

    // Adapter toggled on/off.
    private void OnStateChanged(object? sender, bool isOn)
    {
        IsBluetoothOn = isOn;
        BluetoothLabel = isOn ? "On" : "Off";
        if (!isOn)
        {
            UpdateEarphoneStatus(announceTransitions: false);
            IsEarphonePillVisible = false; // BT gone — force the pill down
        }
    }

    // A device linked/unlinked (ACL) — re-evaluate the real earphone state.
    private void OnDeviceConnectionChanged(object? sender, EventArgs e)
        => UpdateEarphoneStatus(announceTransitions: true);

    private void UpdateEarphoneStatus(bool announceTransitions)
    {
        var (isConnected, _) = _earphones.GetConnectionStatus();
        var changed = _lastEarphoneConnected != isConnected;
        _lastEarphoneConnected = isConnected;

        if (announceTransitions && changed)
        {
            EarphonePillText = isConnected ? "Earphones Connected" : "Earphones Disconnected";
            IsEarphonePillVisible = true;
            return;
        }

        if (!isConnected)
            IsEarphonePillVisible = false;
    }

    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

// ── Tests ────────────────────────────────────────────────────────────────────

public class BluetoothUiSyncTests
{
    private static Mock<ITestEarphoneService> Earphones(bool connected, string details = "Bluetooth audio connected.")
    {
        var mock = new Mock<ITestEarphoneService>();
        mock.Setup(e => e.GetConnectionStatus()).Returns((connected, details));
        return mock;
    }

    // Initial state is seeded from the hardware: adapter already on → toggle on, label "On".
    [Fact]
    public void InitialState_MirrorsAdapterEnabled()
    {
        var monitor = new Mock<ITestBluetoothMonitor>();
        monitor.SetupGet(m => m.IsEnabled).Returns(true);

        var vm = new BluetoothSyncViewModel(monitor.Object, Earphones(false).Object);

        Assert.True(vm.IsBluetoothOn);
        Assert.Equal("On", vm.BluetoothLabel);
        Assert.False(vm.IsEarphonePillVisible); // no pill on startup
    }

    // Hardware broadcasts "adapter ON" → toggle + label update, and PropertyChanged fires.
    [Fact]
    public void StateChangedOn_UpdatesToggleAndLabel_AndNotifies()
    {
        var monitor = new Mock<ITestBluetoothMonitor>();
        monitor.SetupGet(m => m.IsEnabled).Returns(false);
        var vm = new BluetoothSyncViewModel(monitor.Object, Earphones(false).Object);

        var notified = new List<string?>();
        vm.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        monitor.Raise(m => m.StateChanged += null, this, true);

        Assert.True(vm.IsBluetoothOn);
        Assert.Equal("On", vm.BluetoothLabel);
        Assert.Contains(nameof(BluetoothSyncViewModel.IsBluetoothOn), notified);
        Assert.Contains(nameof(BluetoothSyncViewModel.BluetoothLabel), notified);
    }

    // Hardware broadcasts "adapter OFF" → toggle off, label "Off", any earphone pill forced down.
    [Fact]
    public void StateChangedOff_TurnsToggleOff_AndHidesPill()
    {
        var monitor = new Mock<ITestBluetoothMonitor>();
        monitor.SetupGet(m => m.IsEnabled).Returns(true);
        var vm = new BluetoothSyncViewModel(monitor.Object, Earphones(true).Object);

        monitor.Raise(m => m.StateChanged += null, this, false);

        Assert.False(vm.IsBluetoothOn);
        Assert.Equal("Off", vm.BluetoothLabel);
        Assert.False(vm.IsEarphonePillVisible);
    }

    // The core requirement: a simulated "Connected" ACL broadcast flips the ViewModel's UI state
    // to show the "Earphones Connected" pill.
    [Fact]
    public void DeviceConnected_ShowsEarphonesConnectedPill()
    {
        var monitor = new Mock<ITestBluetoothMonitor>();
        monitor.SetupGet(m => m.IsEnabled).Returns(true);
        var earphones = Earphones(false); // disconnected at baseline
        var vm = new BluetoothSyncViewModel(monitor.Object, earphones.Object);

        // Now earbuds connect, then the hardware fires ACL_CONNECTED.
        earphones.Setup(e => e.GetConnectionStatus()).Returns((true, "Bluetooth audio connected."));
        monitor.Raise(m => m.DeviceConnectionChanged += null, this, EventArgs.Empty);

        Assert.True(vm.IsEarphonePillVisible);
        Assert.Equal("Earphones Connected", vm.EarphonePillText);
    }

    // A simulated "Disconnected" ACL broadcast shows the "Earphones Disconnected" pill.
    [Fact]
    public void DeviceDisconnected_ShowsEarphonesDisconnectedPill()
    {
        var monitor = new Mock<ITestBluetoothMonitor>();
        monitor.SetupGet(m => m.IsEnabled).Returns(true);
        var earphones = Earphones(true); // connected at baseline
        var vm = new BluetoothSyncViewModel(monitor.Object, earphones.Object);

        // Earbuds removed, then ACL_DISCONNECTED fires.
        earphones.Setup(e => e.GetConnectionStatus()).Returns((false, "No earphones detected."));
        monitor.Raise(m => m.DeviceConnectionChanged += null, this, EventArgs.Empty);

        Assert.True(vm.IsEarphonePillVisible);
        Assert.Equal("Earphones Disconnected", vm.EarphonePillText);
    }

    // Robustness: an ACL broadcast from a NON-audio device (state unchanged) must NOT flash a pill —
    // the transition guard prevents spurious notifications.
    [Fact]
    public void DeviceEvent_WithNoStateChange_DoesNotShowPill()
    {
        var monitor = new Mock<ITestBluetoothMonitor>();
        monitor.SetupGet(m => m.IsEnabled).Returns(true);
        var earphones = Earphones(false); // disconnected, stays disconnected
        var vm = new BluetoothSyncViewModel(monitor.Object, earphones.Object);

        monitor.Raise(m => m.DeviceConnectionChanged += null, this, EventArgs.Empty);

        Assert.False(vm.IsEarphonePillVisible);
    }

    // The ViewModel actually subscribes to the monitor's events (verifies the wiring contract).
    [Fact]
    public void ViewModel_SubscribesToHardwareEvents()
    {
        var monitor = new Mock<ITestBluetoothMonitor>();
        monitor.SetupGet(m => m.IsEnabled).Returns(false);

        _ = new BluetoothSyncViewModel(monitor.Object, Earphones(false).Object);

        monitor.VerifyAdd(m => m.StateChanged += It.IsAny<EventHandler<bool>>(), Times.Once);
        monitor.VerifyAdd(m => m.DeviceConnectionChanged += It.IsAny<EventHandler>(), Times.Once);
    }
}
