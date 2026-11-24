using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace Knovara.Desktop
{
    public partial class LoginWindow : Window
    {
        private const string ConfigPath = "knovara.config.json";

        public LoginWindow()
        {
            InitializeComponent();
            LoadConfig();
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
