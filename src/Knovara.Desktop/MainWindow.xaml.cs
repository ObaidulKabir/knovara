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
using Knovara.Common;
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

    /// <summary>Mirrors the JSON shape of ResourceReference (GET/DELETE api/resources) for the Documents tab's list.</summary>
    public class ManagedResourceItem
    {
        public string ResourceId { get; set; } = string.Empty;
        public string Uri { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string? SourceVersion { get; set; }
        public string DisplayText => $"{Uri}   [{Type}] - {Status}{(SourceVersion is null ? "" : $"  (v:{SourceVersion[..Math.Min(19, SourceVersion.Length)]}…)")}";
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
        private readonly ObservableCollection<ManagedResourceItem> _managedResources = new();

        public MainWindow(string baseUrl, string tenantId, string jwtToken)
        {
            InitializeComponent();

            _baseUrl = baseUrl.TrimEnd('/');
            _tenantId = tenantId;
            _jwtToken = jwtToken;

            _http = new HttpClient
            {
                BaseAddress = new Uri(_baseUrl + "/"),
                // Ask scores every chunk of every attached document on CPU; an attached folder can take
                // well over the 30s that was enough for a single file.
                Timeout = TimeSpan.FromSeconds(180)
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

            DocumentsListBox.ItemsSource = _managedResources;
            _ = RefreshDocumentsAsync(); // tenant-scoped, so it's safe to load as soon as we're connected
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
                var attached = new List<string>();
                foreach (var fileName in dialog.FileNames)
                {
                    try
                    {
                        var content = File.ReadAllText(fileName);
                        var title = Path.GetFileName(fileName);
                        if (string.IsNullOrWhiteSpace(content))
                        {
                            MessageBox.Show($"'{title}' is empty, so there is nothing to attach.", "Empty File", MessageBoxButton.OK, MessageBoxImage.Warning);
                            continue;
                        }

                        _contextDocuments.Add(new ContextDocItem
                        {
                            Title = title,
                            Content = content,
                            Source = fileName
                        });
                        attached.Add(title);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Could not read file '{fileName}': {ex.Message}", "File Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }

                UpdateContextSummary();
                if (attached.Count > 0)
                {
                    // The other pin actions confirm; without this it looked like nothing had happened,
                    // and "Add Typed Note" cannot attach a browsed file because it only reads the text boxes.
                    MessageBox.Show($"Attached to context: {string.Join(", ", attached)}", "Attached to Context",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }

        private void OnAttachFolderClicked(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog { Title = "Select a Folder to Attach to Context" };
            if (dialog.ShowDialog() != true) return;

            FolderScanResult result;
            try
            {
                result = Knovara.Common.FolderScanner.Scan(dialog.FolderName);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not read folder '{dialog.FolderName}': {ex.Message}", "Folder Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var already = _contextDocuments.Select(d => d.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var added = 0;
            foreach (var file in result.Files)
            {
                if (already.Contains(file.FullPath)) continue;
                _contextDocuments.Add(new ContextDocItem { Title = file.RelativePath, Content = file.Content, Source = file.FullPath });
                added++;
            }
            UpdateContextSummary();

            var message = $"Attached {added} file(s) from '{Path.GetFileName(dialog.FolderName.TrimEnd('\\', '/'))}'.";
            if (result.Files.Count - added > 0) message += $"\n{result.Files.Count - added} already attached were left as they were.";
            if (result.Skipped.Count > 0)
            {
                message += "\n\nSkipped:\n" + string.Join("\n", result.Skipped.Select(s => $"  {s.Value} - {s.Key}"));
            }
            MessageBox.Show(message, "Folder Attached", MessageBoxButton.OK,
                result.HitLimit ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }

        private void OnAddCustomDocClicked(object sender, RoutedEventArgs e)
        {
            var title = NewDocTitleBox.Text.Trim();
            var content = NewDocContentBox.Text.Trim();

            if (string.IsNullOrEmpty(content))
            {
                MessageBox.Show(
                    "Type the note's text in the content box next to this button.\n\n"
                    + "To attach a file instead, use \"Attach File(s)\" above — that attaches it straight away "
                    + "and does not fill these boxes.",
                    "Content Required", MessageBoxButton.OK, MessageBoxImage.Warning);
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

        #endregion // Context Documents Management

        #region Document Management (tenant-scoped resource list)

        private async void OnRefreshDocumentsClicked(object sender, RoutedEventArgs e) => await RefreshDocumentsAsync();

        private async Task RefreshDocumentsAsync()
        {
            SetBusy("Loading documents for this tenant...");
            try
            {
                var response = await _http.GetAsync("api/resources");
                var content = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    var items = JsonSerializer.Deserialize<List<ManagedResourceItem>>(content, JsonOptions) ?? [];
                    _managedResources.Clear();
                    foreach (var item in items) _managedResources.Add(item);
                    DocumentsSummaryText.Text = $" {_managedResources.Count} document(s) for tenant '{_tenantId}'";
                    SetStatus($"Loaded {_managedResources.Count} document(s).");
                }
                else
                {
                    DocumentsSummaryText.Text = $" Could not load: {(int)response.StatusCode} {response.ReasonPhrase}";
                    SetStatus($"Document list error: {(int)response.StatusCode}.");
                }
            }
            catch (Exception ex)
            {
                DocumentsSummaryText.Text = $" Could not reach API: {ex.Message}";
                SetStatus($"Document list failed: {ex.Message}");
            }
        }

        private async void OnDeleteDocumentClicked(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string resourceId) return;
            var item = _managedResources.FirstOrDefault(d => d.ResourceId == resourceId);

            var confirm = MessageBox.Show(
                $"Delete '{item?.Uri ?? resourceId}' and everything extracted from it?\n\nThis removes its passages, embeddings and relationships. It cannot be undone.",
                "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            SetBusy($"Deleting {resourceId}...");
            try
            {
                var response = await _http.DeleteAsync($"api/resources/{resourceId}");
                if (response.IsSuccessStatusCode)
                {
                    if (item is not null) _managedResources.Remove(item);
                    DocumentsSummaryText.Text = $" {_managedResources.Count} document(s) for tenant '{_tenantId}'";
                    SetStatus($"Deleted {resourceId}.");
                }
                else if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // Already gone - e.g. deleted from another session. Drop it locally and move on.
                    if (item is not null) _managedResources.Remove(item);
                    SetStatus($"{resourceId} was already deleted.");
                }
                else
                {
                    var content = await response.Content.ReadAsStringAsync();
                    MessageBox.Show($"Delete failed ({(int)response.StatusCode}): {content}", "Delete Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    SetStatus($"Delete failed: {(int)response.StatusCode}.");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not reach API: {ex.Message}", "Delete Error", MessageBoxButton.OK, MessageBoxImage.Error);
                SetStatus($"Delete failed: {ex.Message}");
            }
        }

        #endregion // Document Management

        private void UpdateContextSummary()
        {
            var totalTokens = _contextDocuments.Sum(d => d.EstimatedTokens);
            ContextSummaryText.Text = $" {_contextDocuments.Count} document(s) attached (~{totalTokens} tokens)";
            AskButton.Content = _contextDocuments.Count > 0
                ? $"Ask Knovara ({_contextDocuments.Count} Doc{( _contextDocuments.Count > 1 ? "s" : "" )} in Context)"
                : "Ask Knovara (Corpus Search)";
        }

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
