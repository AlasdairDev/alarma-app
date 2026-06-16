namespace AlarmaApp.Views;

public partial class TermsAndPrivacyView : ContentPage
{
    private bool _showingTc;

    // ── Terms & Conditions (source: Alarma_TC_and_PrivacyPolicy.pdf) ──────────────
    private static readonly (string Heading, string Body)[] TcSections =
    [
        ("Effective Date: May 25, 2026",
         "ALARMA — Adaptive Anti-Oversleep Destination Alarm and Emergency Safety System"),

        ("1. Acceptance of Terms",
         "By downloading, installing, or using the Alarma mobile application (the \"App\"), you agree to be bound by these Terms and Conditions. If you do not agree to these terms, please do not use the App. These Terms apply to all users of the App, including commuters and their designated emergency contacts."),

        ("2. Description of the App",
         "Alarma is an Android mobile application designed to help commuters using jeepneys, buses, and UV Express vehicles in the Philippines. The App provides the following features:\n\n• Adaptive destination-based alarm that triggers when the user is near their stop\n• Real-time GPS tracking with offline fallback functionality\n• Behavioral learning that adjusts alarm distance and intensity over time\n• SOS emergency button that sends the user's GPS coordinates via SMS to a designated emergency contact\n• Emergency contact notification system using Native Android SMS\n\nNote: The fake call feature has been removed from the current version of the App."),

        ("3. Eligibility",
         "The App is intended for use by individuals who are at least 13 years of age. By using the App, you confirm that you meet this age requirement. Users under 18 years of age should have parental or guardian consent before using the App."),

        ("4. User Responsibilities",
         "As a user of Alarma, you agree to the following:\n\n• You will provide accurate destination and location information when using the App.\n• You will configure your emergency contact information correctly so that SOS messages are sent to a trusted person.\n• You are responsible for ensuring that your emergency contact is aware of and has consented to receiving location-based SMS alerts from the App.\n• You will not use the App for any unlawful or unauthorized purposes.\n• You understand that the App is a supplementary safety tool and does not guarantee your safety or the accuracy of alarm timing in all circumstances.\n• You will not attempt to reverse-engineer, decompile, or modify the App."),

        ("5. GPS and Location Services",
         "The App relies on your device's built-in GPS sensor for core functionality. By using Alarma, you grant the App permission to access your device's location at all times while the App is active. You acknowledge that:\n\n• GPS accuracy may vary depending on your device, signal strength, and environmental conditions such as tunnels, buildings, and areas with poor mobile signal coverage.\n• The App is designed with offline GPS fallback capabilities; however, location accuracy may be reduced in areas with no mobile connectivity.\n• Continuous GPS usage may result in increased battery consumption on your device."),

        ("6. Emergency SOS Feature",
         "The SOS feature sends your GPS coordinates to your designated emergency contact via Native Android SMS. You acknowledge and agree that:\n\n• The App requires a SIM card and SMS capability to use the SOS feature.\n• SMS delivery is dependent on your carrier's network availability and is not guaranteed by Alarma.\n• Alarma is not a substitute for official emergency services. In life-threatening situations, you should contact your local emergency hotline (e.g., 911 in the Philippines) directly.\n• The developers of Alarma are not liable for any failure of the SOS message to be delivered or received."),

        ("7. Disclaimer of Warranties",
         "Alarma is provided on an \"as is\" and \"as available\" basis. The developers make no warranties, expressed or implied, regarding the App, including but not limited to:\n\n• The accuracy, reliability, or completeness of GPS location data or alarm timing.\n• Uninterrupted or error-free operation of the App.\n• The App's suitability for any particular purpose or commute route.\n\nYou use the App at your own risk."),

        ("8. Limitation of Liability",
         "To the fullest extent permitted by applicable law, the developers of Alarma shall not be liable for any direct, indirect, incidental, special, or consequential damages arising from your use of or inability to use the App, including but not limited to missed stops, physical harm, loss of property, or any other damages resulting from reliance on the App's features."),

        ("9. Modifications to the App and Terms",
         "The Alarma development team reserves the right to modify, update, or discontinue any feature of the App at any time without prior notice. We also reserve the right to update these Terms and Conditions. Continued use of the App after any changes constitutes your acceptance of the revised Terms."),

        ("10. Intellectual Property",
         "All content, features, and functionality of the App — including but not limited to source code, design, graphics, and text — are owned by the Alarma development team and are protected by applicable intellectual property laws. You may not reproduce, distribute, or create derivative works without explicit written permission."),

        ("11. Governing Law",
         "These Terms and Conditions shall be governed by and construed in accordance with the laws of the Republic of the Philippines. Any disputes arising from the use of the App shall be subject to the jurisdiction of the appropriate courts in the Philippines."),

        ("12. Contact Information",
         "If you have any questions or concerns about these Terms and Conditions, please contact the Alarma development team through your institution or project supervisors at Polytechnic University of the Philippines, Sta. Mesa, Manila."),
    ];

    // ── Privacy Policy (source: Alarma_TC_and_PrivacyPolicy.pdf) ─────────────────
    private static readonly (string Heading, string Body)[] PrivacySections =
    [
        ("Effective Date: May 25, 2026",
         "Alarma is committed to protecting your privacy. This Privacy Policy explains how we collect, use, store, and share your personal information when you use the Alarma mobile application. By using the App, you consent to the practices described in this policy."),

        ("1. Information We Collect",
         "a. Information You Provide\n\n• Emergency Contact Details: The name and mobile number of your designated emergency contact, which you manually enter in the App.\n• Destination Information: The destination coordinates or address you set before starting a journey.\n\nb. Information Collected Automatically\n\n• GPS Location Data: The App continuously accesses your device's GPS sensor to track your real-time location during an active journey. This data is used solely to trigger destination alarms and send SOS location messages.\n• Device Information: Basic device data such as Android version and device model may be collected for compatibility and performance purposes.\n• Usage Behavior Data: The App records how quickly you respond to alarms (e.g., wake-up response time) to adapt future alarm distances and vibration intensity. This data is stored locally on your device."),

        ("2. How We Use Your Information",
         "The information we collect is used for the following purposes:\n\n• To trigger destination-based alarms when you are approaching your stop.\n• To send your GPS coordinates to your emergency contact via SMS when the SOS button is activated.\n• To improve alarm timing and intensity based on your past behavior.\n• To ensure the App functions correctly in offline or low-signal environments using local GPS data.\n• To maintain and improve the App's performance and reliability."),

        ("3. Data Storage",
         "Alarma stores the following data locally on your device:\n\n• Your emergency contact information\n• Your alarm behavior history (response time)\n• Recently used destinations\n\nThe App does not operate a centralized cloud database. All personal data is stored on your device and is not transmitted to any external servers, except for SMS messages sent through your carrier's network when the SOS feature is activated."),

        ("4. Data Sharing",
         "We do not sell, rent, or share your personal information with third parties, except in the following limited circumstances:\n\n• Emergency SMS: When you activate the SOS button, your GPS coordinates are sent to your designated emergency contact via Native Android SMS through your mobile carrier's network.\n• Legal Compliance: We may disclose information if required by law or in response to valid legal processes.\n\nNo advertising networks, analytics companies, or marketing partners receive your data through this App."),

        ("5. Permissions Required",
         "Alarma requires the following device permissions to function:\n\n• Location (Fine and Background): Required for GPS-based alarm triggering and SOS location sharing.\n• SMS: Required to send emergency location messages to your designated contact.\n• Vibration: Required to trigger physical alarm alerts.\n• Boot Completed: Required to restore active alarms after the device restarts.\n\nYou may revoke these permissions at any time through your device settings, but doing so will limit or disable core App functionality."),

        ("6. Data Retention",
         "Alarma retains your locally stored data (emergency contact, behavior history, destinations) for as long as you have the App installed. You may delete this data at any time by clearing the App's data through your device settings or uninstalling the App."),

        ("7. Children's Privacy",
         "Alarma is not directed at children under 13 years of age. We do not knowingly collect personal information from children under 13. If you believe a child under 13 has provided us with personal information, please contact us so we can take appropriate action."),

        ("8. Security",
         "We take reasonable measures to protect the information stored by the App on your device. However, no method of electronic storage is 100% secure. We encourage you to protect your device with a PIN, password, or biometric lock to prevent unauthorized access to the App and its data."),

        ("9. Your Rights",
         "As a user, you have the right to:\n\n• Access the personal data stored by the App on your device.\n• Delete your data by clearing the App's storage or uninstalling the App.\n• Withdraw consent to location tracking by revoking the App's location permission in your device settings."),

        ("10. Changes to This Privacy Policy",
         "We may update this Privacy Policy from time to time to reflect changes in the App or applicable laws. We will notify users of any significant changes by updating the effective date at the top of this policy. Continued use of the App after any changes constitutes your acceptance of the revised policy."),

        ("11. Contact Us",
         "If you have any questions, concerns, or requests regarding this Privacy Policy, please reach out to the Alarma development team through your institution at Polytechnic University of the Philippines, Sta. Mesa, Manila."),
    ];

    public TermsAndPrivacyView(int initialTab = 0)
    {
        InitializeComponent();
        ShowTab(showTc: initialTab == 0);
    }

    private void ShowTab(bool showTc)
    {
        _showingTc = showTc;
        TitleLabel.Text = showTc ? "Terms & Conditions" : "Privacy Policy";
        TcTabBorder.BackgroundColor = showTc ? Color.FromArgb("#7B3FA0") : Color.FromArgb("#3D2B5E");
        PrivacyTabBorder.BackgroundColor = showTc ? Color.FromArgb("#3D2B5E") : Color.FromArgb("#7B3FA0");

        ((Label)TcTabBorder.Content!).TextColor = showTc ? Colors.White : Color.FromArgb("#C8B8E8");
        ((Label)PrivacyTabBorder.Content!).TextColor = showTc ? Color.FromArgb("#C8B8E8") : Colors.White;

        ContentLayout.Children.Clear();
        _ = ContentScroll.ScrollToAsync(0, 0, false);

        var sections = showTc ? TcSections : PrivacySections;
        foreach (var (heading, body) in sections)
        {
            ContentLayout.Add(new Label
            {
                Text = heading,
                FontSize = 15,
                FontAttributes = FontAttributes.Bold,
                TextColor = Color.FromArgb("#9B6FBF"),
                Margin = new Thickness(0, 20, 0, 4),
            });
            ContentLayout.Add(new Label
            {
                Text = body,
                FontSize = 13,
                TextColor = Color.FromArgb("#C8B8E8"),
                LineBreakMode = LineBreakMode.WordWrap,
                Margin = new Thickness(0, 0, 0, 4),
            });
            ContentLayout.Add(new BoxView
            {
                HeightRequest = 1,
                Color = Color.FromArgb("#2A1F4E"),
                Margin = new Thickness(0, 8, 0, 0),
            });
        }
    }

    private void OnTcTabTapped(object? sender, TappedEventArgs e)
    {
        if (!_showingTc) ShowTab(showTc: true);
    }

    private void OnPrivacyTabTapped(object? sender, TappedEventArgs e)
    {
        if (_showingTc) ShowTab(showTc: false);
    }

    private async void OnCloseTapped(object? sender, TappedEventArgs e)
        => await Navigation.PopModalAsync(animated: false);
}
