using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net.Http;
using System.Windows;
using Knovara.Common.Device;
using Knovara.Common.Runtime;

namespace Knovara.Desktop
{
    public partial class LoginWindow : Window
    {
        private const string ConfigPath = "knovara.config.json";
        private const string LocalApiUrl = "http://127.0.0.1:5299";
        private const string LocalTenantId = "local";

        // One client for the life of the app (sockets are not meant to be created per request).
        private static readonly HttpClient ControlPlaneHttp = new() { Timeout = TimeSpan.FromSeconds(30) };

        private readonly DeviceStore _store = new(DeviceStore.DefaultPath(), new DpapiProtector());
        private readonly ControlPlaneClient _control = new(ControlPlaneHttp);
        private DeviceIdentity? _identity;

        public LoginWindow()
        {
            InitializeComponent();
            LoadConfig();
            DeviceNameBox.Text = Environment.MachineName;
            RefreshEnrollmentState();
        }

        /// <summary>Shows either "this PC is registered" or the enrollment form, depending on what is saved on this PC.</summary>
        private void RefreshEnrollmentState()
        {
            _identity = _store.Load();
            var enrolled = _identity is not null;

            EnrolledPanel.Visibility = enrolled ? Visibility.Visible : Visibility.Collapsed;
            EnrollPanel.Visibility = enrolled ? Visibility.Collapsed : Visibility.Visible;
            // "Use Knovara on this PC" is the primary path (no account); a PC already enrolled with a team
            // server most likely wants that instead, so its panel opens expanded rather than collapsed.
            TeamExpander.IsExpanded = enrolled;

            if (_identity is not null)
            {
                EnrolledDetailText.Text = $"Tenant: {_identity.TenantId}\nControl plane: {_identity.ControlPlaneUrl}\nDevice id: {_identity.DeviceId}";
            }
        }

        /// <summary>Starts the local runtime (if it is not already running) and opens it directly, with no account and no server to set up.</summary>
        private async void OnUseLocalClicked(object sender, RoutedEventArgs e)
        {
            UseLocalButton.IsEnabled = false;
            UseLocalButton.Content = "Starting…";
            UseLocalStatusText.Visibility = Visibility.Visible;
            UseLocalStatusText.Text = "Starting the local runtime (the first run can take a few seconds)…";

            await LocalRuntimeLauncher.EnsureRunningAsync(LocalApiUrl, warn: message => Dispatcher.Invoke(() => UseLocalStatusText.Text = message));

            new MainWindow(LocalApiUrl, LocalTenantId, string.Empty).Show();
            Close();
        }

        private async void OnEnrollClicked(object sender, RoutedEventArgs e)
        {
            ShowEnrollError(null);

            var url = ControlPlaneClient.NormalizeUrl(ControlPlaneBox.Text);
            var code = CodeBox.Text.Trim();
            var name = DeviceNameBox.Text.Trim();
            if (url is null) { ShowEnrollError("Enter the control plane address as an http:// or https:// URL."); return; }
            if (code.Length == 0) { ShowEnrollError("Enter the enrollment code your administrator gave you."); return; }
            if (name.Length == 0) { ShowEnrollError("Give this PC a name so administrators can recognise it."); return; }

            EnrollButton.IsEnabled = false;
            EnrollButton.Content = "Enrolling...";
            try
            {
                var session = await _control.EnrollAsync(url, code, name, AppInfo.Version, AppInfo.Os);
                if (session.DeviceSecret is null) { ShowEnrollError("The control plane did not return a device credential."); return; }

                var identity = new DeviceIdentity(url, session.DeviceId, session.DeviceSecret, session.TenantId, session.ApiBaseUrl);
                try
                {
                    _store.Save(identity);
                }
                catch (Exception ex)
                {
                    // The device is registered on the server but this PC could not remember it; say so, since the slot is now used.
                    ShowEnrollError($"Enrolled, but this PC could not save the registration: {ex.Message}. Ask your administrator to revoke the device '{name}' and try again.");
                    return;
                }

                OpenMain(new DeviceTokenProvider(_control, identity, AppInfo.Version, AppInfo.Os, initial: session));
            }
            catch (ControlPlaneException ex)
            {
                ShowEnrollError(ex.Message);
            }
            finally
            {
                EnrollButton.IsEnabled = true;
                EnrollButton.Content = "Enroll this PC";
            }
        }

        private async void OnConnectEnrolledClicked(object sender, RoutedEventArgs e)
        {
            if (_identity is null) return;

            var provider = new DeviceTokenProvider(_control, _identity, AppInfo.Version, AppInfo.Os);
            ConnectEnrolledButton.IsEnabled = false;
            try
            {
                // Fetch a token now, so a revoked device or an unreachable control plane is reported here, not as errors inside the app.
                await provider.GetAccessTokenAsync();
                OpenMain(provider);
            }
            catch (ControlPlaneException ex)
            {
                var hint = ex.Failure is ControlPlaneFailure.Revoked or ControlPlaneFailure.BadCredentials
                    ? "\n\nUse \"Forget this device\" and enroll again with a new code if you still need access."
                    : "\n\nCheck your network connection and try again.";
                MessageBox.Show(ex.Message + hint, "Cannot Connect", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                ConnectEnrolledButton.IsEnabled = true;
            }
        }

        private void OnForgetClicked(object sender, RoutedEventArgs e)
        {
            var confirm = MessageBox.Show(
                "Forget this device on this PC?\n\nYou will need a new enrollment code to register again. "
                + "To free the device slot, an administrator should also revoke it in the dashboard.",
                "Forget Device", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            _store.Delete();
            RefreshEnrollmentState();
        }

        private void OpenMain(DeviceTokenProvider tokens)
        {
            new MainWindow(tokens).Show();
            Close();
        }

        private void ShowEnrollError(string? message)
        {
            EnrollErrorText.Text = message ?? string.Empty;
            EnrollErrorText.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
        }

        private void LoadConfig()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var text = File.ReadAllText(ConfigPath);
                    var node = JsonNode.Parse(text);
                    if (node != null)
                    {
                        var url = node["BaseUrl"]?.ToString();
                        var tenant = node["TenantId"]?.ToString();
                        var token = node["JwtToken"]?.ToString();
                        var controlPlane = node["ControlPlaneUrl"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(controlPlane)) ControlPlaneBox.Text = controlPlane;

                        if (!string.IsNullOrWhiteSpace(url)) BaseUrlBox.Text = url;
                        if (!string.IsNullOrWhiteSpace(tenant)) TenantBox.Text = tenant;
                        if (!string.IsNullOrWhiteSpace(token)) TokenBox.Text = token;
                    }
                }
            }
            catch
            {
                // Ignore load errors and use defaults
            }
        }

        private void OnLoginClicked(object sender, RoutedEventArgs e)
        {
            var baseUrl = BaseUrlBox.Text.Trim();
            var tenant = TenantBox.Text.Trim();
            var token = TokenBox.Text.Trim();

            if (string.IsNullOrEmpty(baseUrl))
            {
                MessageBox.Show("Please enter an API Base URL.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrEmpty(tenant))
            {
                MessageBox.Show("Please enter a Tenant ID.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SaveConfig(baseUrl, tenant, token);

            var mainWindow = new MainWindow(baseUrl, tenant, token);
            mainWindow.Show();
            Close();
        }

        private static void SaveConfig(string baseUrl, string tenant, string token)
        {
            try
            {
                JsonObject obj;
                if (File.Exists(ConfigPath))
                {
                    var existing = File.ReadAllText(ConfigPath);
                    obj = JsonNode.Parse(existing) as JsonObject ?? new JsonObject();
                }
                else
                {
                    obj = new JsonObject();
                }

                obj["BaseUrl"] = baseUrl;
                obj["TenantId"] = tenant;
                obj["JwtToken"] = token;

                var json = JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ConfigPath, json);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Warning: Could not save config file: {ex.Message}", "Config Warning", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }
}
