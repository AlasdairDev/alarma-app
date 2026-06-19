namespace AlarmaApp.Services;

// A tiny app-wide event aggregator — our lightweight stand-in for CommunityToolkit's
// WeakReferenceMessenger (we didn't want to pull a whole extra MVVM package into the trimmed/AOT
// release build just for one broadcast). The Bluetooth BroadcastReceiver path publishes earphone
// connect/disconnect changes here, and whoever cares — chiefly the Main Page (HomeView) — listens, so
// the transient "Earphones connected/disconnected" grey pill can pop globally no matter which screen
// the rider is on when their earbuds go in or out.
//
// Subscribers are responsible for unsubscribing if they're short-lived; our singletons (HomeController,
// HomeView) live for the whole process, so a plain static event is safe and leak-free for them.
public static class AppMessenger
{
    // Raised whenever the real audio-output state flips (earphones in / earphones out).
    public static event Action<EarphoneStatusChange>? EarphoneStatusChanged;

    public static void PublishEarphoneStatus(EarphoneStatusChange change)
        => EarphoneStatusChanged?.Invoke(change);
}

// What we hand subscribers: whether earphones are now connected, plus the ready-to-show pill text.
public readonly record struct EarphoneStatusChange(bool IsConnected, string Message);
