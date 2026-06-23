// =============================================================================
//  SettingsBluetoothToggleTests.cs
// -----------------------------------------------------------------------------
//  Regression guard for the Settings-page crash: opening Settings hard-crashed the
//  app with a NullReferenceException during XAML inflation.
//
//  ROOT CAUSE: the BLUETOOTH <Switch> binds IsToggled OneWay to the live adapter
//  state and also wires Toggled="OnBluetoothToggled". When the adapter is already
//  ON, the binding flips IsToggled false→true DURING inflation, which fires the
//  Toggled handler — but the switch's x:Name field isn't assigned until inflation
//  finishes, so the handler's "revert the visual to hardware" line dereferenced a
//  null control and threw, taking the whole page down before it could open. (It was
//  not the Stage-1 / overshoot change — those sections were ruled out by bisection;
//  the crash reproduced with them removed and disappeared when the Bluetooth switch
//  was reduced to a bare element.)
//
//  FIX: OnBluetoothToggled bails out when the switch field is still null (the
//  inflation pass has no user intent; the OneWay binding already shows the right
//  state). This mirrors that handler so the guard is locked in by `dotnet test`.
//
//  Self-contained per project convention (the AlarmaApp.Tests net9.0 project can't
//  reference the android-targeted view/controller, so we mirror the exact logic).
// =============================================================================

using Xunit;

namespace AlarmaApp.Tests;

// Mirror of SettingsView.OnBluetoothToggled. `switchControl` stands in for the x:Name'd
// BluetoothSwitch field, which is null until XAML inflation finishes wiring it up.
internal sealed class BluetoothToggleMirror
{
    // Stand-in for the on-screen Switch (null during inflation, set afterwards).
    public sealed class FakeSwitch { public bool IsToggled; }

    public FakeSwitch? SwitchControl;     // the x:Name field
    public bool HardwareOn;               // live adapter state (IBluetoothMonitor.IsEnabled)
    public bool BoundIsOn;                // controller.IsBluetoothOn (the OneWay binding source)
    public bool? RequestedChange;         // non-null => RequestBluetoothChange(value) was called
    private bool _suppress;

    // Returns true if the toggle was handled, false if it bailed out early.
    public bool OnToggled(bool requested)
    {
        if (_suppress) return false;

        // The fix: ignore the inflation-time toggle, when the control field isn't wired up yet.
        if (SwitchControl is null) return false;

        if (BoundIsOn != HardwareOn)
            BoundIsOn = HardwareOn;

        _suppress = true;
        SwitchControl.IsToggled = HardwareOn; // would NRE if SwitchControl were null
        _suppress = false;

        if (requested != HardwareOn)
            RequestedChange = requested;

        return true;
    }
}

public class SettingsBluetoothToggleTests
{
    // The crash scenario: adapter ON, the OneWay binding flips IsToggled to true during inflation and
    // fires the handler before the switch field exists. The guard must make this a safe no-op (no throw).
    [Fact]
    public void ToggledDuringInflation_NullSwitch_DoesNotThrowAndNoOps()
    {
        var h = new BluetoothToggleMirror { SwitchControl = null, HardwareOn = true, BoundIsOn = false };

        var handled = h.OnToggled(requested: true); // the binding-driven false->true edge

        Assert.False(handled);                 // bailed out cleanly
        Assert.Null(h.RequestedChange);        // never asked the OS to change anything
    }

    // After inflation the field is wired up; a user tap that merely matches the hardware just reverts the
    // visual (no OS request) — the switch is a pure read-out of the adapter.
    [Fact]
    public void UserToggle_MatchingHardware_RevertsWithoutRequest()
    {
        var sw = new BluetoothToggleMirror.FakeSwitch { IsToggled = true };
        var h = new BluetoothToggleMirror { SwitchControl = sw, HardwareOn = true, BoundIsOn = true };

        var handled = h.OnToggled(requested: true);

        Assert.True(handled);
        Assert.True(sw.IsToggled);             // snapped back to the hardware truth
        Assert.Null(h.RequestedChange);        // no change requested
    }

    // A user tap that asks for a genuinely different state hands the request to the OS prompt, while the
    // visual still snaps back to the current hardware state until the OS confirms.
    [Fact]
    public void UserToggle_DifferentFromHardware_RequestsChange()
    {
        var sw = new BluetoothToggleMirror.FakeSwitch { IsToggled = false };
        var h = new BluetoothToggleMirror { SwitchControl = sw, HardwareOn = false, BoundIsOn = false };

        var handled = h.OnToggled(requested: true); // user wants ON, hardware is OFF

        Assert.True(handled);
        Assert.False(sw.IsToggled);            // visual stays on hardware truth (OFF) until OS confirms
        Assert.True(h.RequestedChange);        // OS change requested
    }
}
