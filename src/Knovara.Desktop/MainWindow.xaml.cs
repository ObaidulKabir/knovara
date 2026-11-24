using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace Knovara.Desktop
{
    public class ContextDocItem
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("n");
        public string Title { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public int EstimatedTokens => Math.Max(1, Content.Length / 4);
        public string DisplayText => $"{Title} (~{EstimatedTokens} tokens) - {Source}";
    }

    public partial class MainWindow : Window
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };

        private readonly string _baseUrl;
        private readonly string _tenantId;
        private readonly string _jwtToken;
        private readonly HttpClient _http;
        private readonly ObservableCollection<ContextDocItem> _contextDocuments = new();

        public MainWindow(string baseUrl, string tenantId, string jwtToken)
        {
            InitializeComponent();

            _baseUrl = baseUrl.TrimEnd('/');
            _tenantId = tenantId;
            _jwtToken = jwtToken;

            _http = new HttpClient
            {
                BaseAddress = new Uri(_baseUrl + "/"),
                Timeout = TimeSpan.FromSeconds(30)
            };
            _http.DefaultRequestHeaders.Add("X-Tenant-Id", _tenantId);
            if (!string.IsNullOrWhiteSpace(_jwtToken))
            {
                _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _jwtToken);
            }

            var authMode = string.IsNullOrWhiteSpace(_jwtToken) ? "No JWT (Header Dev Mode)" : "Authenticated (JWT Bearer)";
            ConnectionInfoText.Text = $"Connected to {_baseUrl} | Tenant: {_tenantId} | Auth: {authMode}";

            ContextDocsListBox.ItemsSource = _contextDocuments;
            UpdateContextSummary();
        }

        #region Context Documents Management

        private void OnBrowseDocumentClicked(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Select Document to Add to Context",
                Filter = "Text & Markdown (*.txt;*.md;*.json;*.csv;*.cs;*.yaml;*.yml;*.xml;*.html)|*.txt;*.md;*.json;*.csv;*.cs;*.yaml;*.yml;*.xml;*.html|All Files (*.*)|*.*",
                Multiselect = true
            };

            if (dialog.ShowDialog() == true)
            {
                foreach (var fileName in dialog.FileNames)
                {
                    try
                    {
                        var content = File.ReadAllText(fileName);
                        var title = Path.GetFileName(fileName);
                        _contextDocuments.Add(new ContextDocItem
                        {
                            Title = title,
                            Content = content,
                            Source = fileName
                        });
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Could not read file '{fileName}': {ex.Message}", "File Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
                UpdateContextSummary();
            }
        }

        private void OnAddCustomDocClicked(object sender, RoutedEventArgs e)
        {
            var title = NewDocTitleBox.Text.Trim();
            var content = NewDocContentBox.Text.Trim();

            if (string.IsNullOrEmpty(content))
            {
                MessageBox.Show("Please enter document content or click 'Browse File...'.", "Content Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrEmpty(title))
            {
                title = $"Custom Doc {_contextDocuments.Count + 1}";
            }

            _contextDocuments.Add(new ContextDocItem
            {
                Title = title,
                Content = content,
                Source = "User Note / Snippet"
            });

            NewDocTitleBox.Clear();
            NewDocContentBox.Clear();
            UpdateContextSummary();
        }

        private void OnRemoveContextDocClicked(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string id)
            {
                var item = _contextDocuments.FirstOrDefault(d => d.Id == id);
                if (item != null)
                {
                    _contextDocuments.Remove(item);
                    UpdateContextSummary();
                }
            }
        }

        private void OnClearContextDocsClicked(object sender, RoutedEventArgs e)
        {
            if (_contextDocuments.Count > 0)
            {
                _contextDocuments.Clear();
                UpdateContextSummary();
            }
        }

        private void OnPinIngestedToContextClicked(object sender, RoutedEventArgs e)
        {
            var uri = IngestUriBox.Text.Trim();
            if (string.IsNullOrEmpty(uri))
            {
                MessageBox.Show("Please specify a URI or path first.", "Input Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var title = Path.GetFileName(uri);
            if (string.IsNullOrEmpty(title)) title = uri;

            _contextDocuments.Add(new ContextDocItem
            {
                Title = $"Resource: {title}",
                Content = $"Referenced ingestion resource: {uri}",
                Source = uri
            });
            UpdateContextSummary();
            MessageBox.Show($"Resource '{uri}' pinned to current context.", "Pinned to Context", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void OnPinResourceToContextClicked(object sender, RoutedEventArgs e)
        {
            var resourceId = StatusResourceIdBox.Text.Trim();
            if (string.IsNullOrEmpty(resourceId))
            {
                MessageBox.Show("Please specify a Resource ID first.", "Input Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var content = StatusResultBox.Text;
            if (string.IsNullOrWhiteSpace(content) || content.StartsWith("Resource status details"))
            {
                content = $"Resource ID: {resourceId}";
            }

            _contextDocuments.Add(new ContextDocItem
            {
                Title = $"Resource {resourceId}",
                Content = content,
                Source = $"Resource Store: {resourceId}"
            });
            UpdateContextSummary();
            MessageBox.Show($"Resource '{resourceId}' pinned to current context.", "Pinned to Context", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void UpdateContextSummary()
        {
            var totalTokens = _contextDocuments.Sum(d => d.EstimatedTokens);
            ContextSummaryText.Text = $" {_contextDocuments.Count} document(s) attached (~{totalTokens} tokens)";
            AskButton.Content = _contextDocuments.Count > 0 
                ? $"Ask Knovara ({_contextDocuments.Count} Doc{( _contextDocuments.Count > 1 ? "s" : "" )} in Context)"
                : "Ask Knovara (Corpus Search)";
        }

        #endregion

        #region Operations (Ask, Search, Ingest, Status)

        private async void OnAskClicked(object sender, RoutedEventArgs e)
        {
            var question = AskQuestionBox.Text.Trim();
            if (string.IsNullOrEmpty(question))
            {
                MessageBox.Show("Please enter a question.", "Input Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var sessionId = string.IsNullOrWhiteSpace(AskSessionBox.Text) ? "desktop-session-1" : AskSessionBox.Text.Trim();
            if (!int.TryParse(AskBudgetBox.Text, out var budget) || budget <= 0) budget = 2000;

            var docsPayload = _contextDocuments.Select(d => new
            {
                title = d.Title,
                content = d.Content,
                source = d.Source
            }).ToList();

            SetBusy($"Sending ask request with {_contextDocuments.Count} context document(s)...");
            try
            {
                var payload = new
                {
                    sessionId,
                    question,
                    tokenBudget = budget,
                    documents = docsPayload.Count > 0 ? docsPayload : null
                };

                var response = await _http.PostAsJsonAsync("api/retrieval/ask", payload);
                var content = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    var formatted = FormatJson(content);
                    AskResultBox.Text = $"=== EVIDENCE & CONTEXT PACKAGE RECEIVED ===\nStatus: {(int)response.StatusCode} {response.StatusCode}\nContext Docs Included: {_contextDocuments.Count}\n\n{formatted}";
                    SetStatus($"Ask completed successfully ({(int)response.StatusCode}).");
                }
                else
                {
                    AskResultBox.Text = $"=== ERROR RESPONSE ===\nStatus: {(int)response.StatusCode} {response.ReasonPhrase}\n\n{content}";
                    SetStatus($"API returned {(int)response.StatusCode} error.");
                }
            }
            catch (Exception ex)
            {
                AskResultBox.Text = $"=== EXCEPTION ===\nCould not reach API at {_baseUrl}\nError: {ex.Message}";
                SetStatus($"Request failed: {ex.Message}");
            }
        }

        private async void OnSearchClicked(object sender, RoutedEventArgs e)
        {
            var question = SearchQueryBox.Text.Trim();
            if (string.IsNullOrEmpty(question))
            {
                MessageBox.Show("Please enter a search query.", "Input Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var mode = (SearchModeCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Adaptive";
            if (!int.TryParse(SearchTopKBox.Text, out var topK) || topK <= 0) topK = 5;

            SetBusy("Running search query...");
            try
            {
                var payload = new { question, mode, topK };
                var response = await _http.PostAsJsonAsync("api/retrieval/search", payload);
                var content = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    var formatted = FormatJson(content);
                    SearchResultBox.Text = $"=== RETRIEVAL RESULTS ===\nStatus: {(int)response.StatusCode} {response.StatusCode}\n\n{formatted}";
                    SetStatus($"Search completed successfully.");
                }
                else
                {
                    SearchResultBox.Text = $"=== ERROR RESPONSE ===\nStatus: {(int)response.StatusCode} {response.ReasonPhrase}\n\n{content}";
                    SetStatus($"Search error: {(int)response.StatusCode}.");
                }
            }
            catch (Exception ex)
            {
                SearchResultBox.Text = $"=== EXCEPTION ===\nCould not reach API at {_baseUrl}\nError: {ex.Message}";
                SetStatus($"Search failed: {ex.Message}");
            }
        }

        private async void OnIngestClicked(object sender, RoutedEventArgs e)
        {
            var uri = IngestUriBox.Text.Trim();
            if (string.IsNullOrEmpty(uri))
            {
                MessageBox.Show("Please enter a URI or path.", "Input Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var type = (IngestTypeCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Document";

            SetBusy("Submitting ingestion job...");
            try
            {
                var payload = new { uri, type };
                var response = await _http.PostAsJsonAsync("api/resources/ingest", payload);
                var content = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    var formatted = FormatJson(content);
                    IngestResultBox.Text = $"=== INGESTION ACCEPTED ===\nStatus: {(int)response.StatusCode} {response.StatusCode}\n\n{formatted}";
                    SetStatus($"Ingestion job submitted successfully.");

                    // Auto-fill Resource Inspector
                    try
                    {
                        var doc = JsonDocument.Parse(content);
                        if (doc.RootElement.TryGetProperty("resourceId", out var idProp))
                        {
                            StatusResourceIdBox.Text = idProp.GetString() ?? "";
                        }
                    }
                    catch { }
                }
                else
                {
                    IngestResultBox.Text = $"=== ERROR RESPONSE ===\nStatus: {(int)response.StatusCode} {response.ReasonPhrase}\n\n{content}";
                    SetStatus($"Ingestion error: {(int)response.StatusCode}.");
                }
            }
            catch (Exception ex)
            {
                IngestResultBox.Text = $"=== EXCEPTION ===\nCould not reach API at {_baseUrl}\nError: {ex.Message}";
                SetStatus($"Ingest failed: {ex.Message}");
            }
        }

        private async void OnCheckStatusClicked(object sender, RoutedEventArgs e)
        {
            var resourceId = StatusResourceIdBox.Text.Trim();
            if (string.IsNullOrEmpty(resourceId))
            {
                MessageBox.Show("Please enter a Resource ID.", "Input Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SetBusy($"Querying status for {resourceId}...");
            try
            {
                var response = await _http.GetAsync($"api/resources/{resourceId}");
                var content = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    var formatted = FormatJson(content);
                    StatusResultBox.Text = $"=== RESOURCE DETAILS ===\nStatus: {(int)response.StatusCode} {response.StatusCode}\n\n{formatted}";
                    SetStatus($"Resource {resourceId} status retrieved.");
                }
                else
                {
                    StatusResultBox.Text = $"=== ERROR RESPONSE ===\nStatus: {(int)response.StatusCode} {response.ReasonPhrase}\n\n{content}";
                    SetStatus($"Resource check error: {(int)response.StatusCode}.");
                }
            }
            catch (Exception ex)
            {
                StatusResultBox.Text = $"=== EXCEPTION ===\nCould not reach API at {_baseUrl}\nError: {ex.Message}";
                SetStatus($"Status check failed: {ex.Message}");
            }
        }

        private void OnDisconnectClicked(object sender, RoutedEventArgs e)
        {
            var login = new LoginWindow();
            login.Show();
            Close();
        }

        #endregion

        private static string FormatJson(string json)
        {
            try
            {
                var doc = JsonDocument.Parse(json);
                return JsonSerializer.Serialize(doc, JsonOptions);
            }
            catch
            {
                return json;
            }
        }

        private void SetBusy(string message) => BottomStatusText.Text = $"[Working] {message}";
        private void SetStatus(string message) => BottomStatusText.Text = $"[Ready] {message}";

        protected override void OnClosed(EventArgs e)
        {
            _http.Dispose();
            base.OnClosed(e);
        }
    }
}
