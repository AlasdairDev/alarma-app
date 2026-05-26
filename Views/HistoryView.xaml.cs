// Security Considerations (OWASP Top 10)
// A01 Broken Access Control / IDOR: Displays read-only TripHistory records from the device-local
//   encrypted SQLite database. There is no cross-user or cross-device resource access surface —
//   every record is owned by the device and decrypted with the device-specific key from SecureStorage.
// A03 Injection: Trip data (destination names, summaries) was validated and length-capped at
//   write-time; no raw user-controlled strings are rendered as HTML or evaluated as code here.

using AlarmaApp.Controllers;

namespace AlarmaApp.Views;

public partial class HistoryView : ContentPage
{
    public HistoryView(HomeController controller)
    {
        BindingContext = controller;
        InitializeComponent();
    }

    protected override void OnAppearing()
    {
        Content.Opacity = 0;
        base.OnAppearing();
        Content.FadeTo(1, 220, Easing.CubicOut);
    }
}
