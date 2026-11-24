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
using Knovara.Common.Device;
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

        private bool IsFile => Source.Length > 2 && Path.IsPathRooted(Source);

        /// <summary>File name for files (the title of a folder-attached file is its relative path); the title otherwise.</summary>
        public string Name => IsFile ? Path.GetFileName(Source) : Title;

        /// <summary>The containing folder for files, so same-named files in different folders are distinguishable.</summary>
        public string Location => IsFile ? (Path.GetDirectoryName(Source) ?? Source) : Source;

        public string TokensText => EstimatedTokens >= 1000 ? $"~{EstimatedTokens / 1000.0:0.#}k tok" : $"~{EstimatedTokens} tok";

        public string PreviewText => Content.Length <= 400 ? Content : Content[..400] + "…";

        public string Icon => !IsFile ? "✏️" : Path.GetExtension(Source).ToLowerInvariant() switch
        {
            ".md" or ".markdown" or ".txt" or ".rst" or ".log" => "📝",
            ".json" or ".yaml" or ".yml" or ".xml" or ".toml" or ".ini" => "🧾",
            ".csv" or ".tsv" => "📊",
            ".html" or ".htm" => "🌐",
            ".cs" or ".py" or ".js" or ".ts" or ".java" or ".go" or ".sql" => "💻",
            _ => "📄"
        };
    }

    /// <summary>Mirrors the JSON shape of ResourceReference (GET/DELETE api/resources) for the Documents tab's list.</summary>
    public class ManagedResourceItem
    {
        public string ResourceId { get; set; } = string.Empty;
        public string Uri { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string? SourceVersion { get; set; }
        public string? Error { get; set; }
        public string ErrorText => string.IsNullOrWhiteSpace(Error) ? string.Empty : "⚠ " + Error;
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

        /// <summary>An enrolled device: the tenant and API address come from the control plane, and tokens refresh themselves.</summary>
        public MainWindow(DeviceTokenProvider tokens) : this(tokens.ApiBaseUrl, tokens.TenantId, string.Empty, tokens)
        {
        }

        public MainWindow(string baseUrl, string tenantId, string jwtToken, DeviceTokenProvider? tokens = null)
        {
            InitializeComponent();

            _baseUrl = baseUrl.TrimEnd('/');
            _tenantId = tenantId;
            _jwtToken = jwtToken;

            // An enrolled device sends its own short-lived token on every request; the handler refreshes it before it expires.
            _http = tokens is null
                ? new HttpClient()
                : new HttpClient(new BearerTokenHandler(tokens) { InnerHandler = new HttpClientHandler() });
            _http.BaseAddress = new Uri(_baseUrl + "/");
            // Ask scores every chunk of every attached document on CPU; an attached folder can take
            // well over the 30s that was enough for a single file.
            _http.Timeout = TimeSpan.FromSeconds(180);
            _http.DefaultRequestHeaders.Add("X-Tenant-Id", _tenantId);
            if (tokens is null && !string.IsNullOrWhiteSpace(_jwtToken))
            {
                _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _jwtToken);
            }

            var authMode = tokens is not null ? "Enrolled device (token refreshes automatically)"
                : string.IsNullOrWhiteSpace(_jwtToken) ? "No JWT (Header Dev Mode)" : "Authenticated (JWT Bearer)";
            ConnectionInfoText.Text = $"Connected to {_baseUrl} | Tenant: {_tenantId} | Auth: {authMode}";
            if (tokens is not null && AppInfo.IsNewerThanThisBuild(tokens.LatestVersion))
            {
                ConnectionInfoText.Text += $" | Update available: {tokens.LatestVersion} (you have {AppInfo.Version}) - download it from {tokens.ControlPlaneUrl}/Downloads";
            }

            ContextDocsListBox.ItemsSource = _contextDocuments;
            UpdateContextSummary();

            DocumentsListBox.ItemsSource = _managedResources;
            _ = RefreshDocumentsAsync(); // tenant-scoped, so it's safe to load as soon as we're connected
        }

        #region Context Documents Management

        private Action? _undoAction;

        private void OnBrowseDocumentClicked(object sender, RoutedEventArgs e)
        {
            var patterns = string.Join(";", FolderScanner.TextExtensions.Select(x => "*" + x));
            var dialog = new OpenFileDialog
            {
                Title = "Select Documents to Add to Context",
                Filter = $"Text, code & data files ({patterns})|{patterns}|All Files (*.*)|*.*",
                Multiselect = true
            };

            if (dialog.ShowDialog() == true) AttachFiles(dialog.FileNames);
        }

        private void OnAttachFolderClicked(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog { Title = "Select a Folder to Attach to Context" };
            if (dialog.ShowDialog() == true) AttachFolder(dialog.FolderName);
        }

        /// <summary>Attaches individually chosen or dropped files; reports (inline) anything it could not use.</summary>
        private void AttachFiles(IEnumerable<string> paths)
        {
            var known = _contextDocuments.Select(d => d.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var options = new FolderScanOptions();
            var added = new List<ContextDocItem>();
            var skipped = new Dictionary<string, int>();
            void Skip(string reason) => skipped[reason] = skipped.GetValueOrDefault(reason) + 1;

            foreach (var path in paths)
            {
                try
                {
                    if (known.Contains(path)) { Skip("already attached"); continue; }
                    if (new FileInfo(path).Length > options.MaxFileBytes) { Skip("file too large (over 1 MB)"); continue; }

                    var content = File.ReadAllText(path);
                    if (string.IsNullOrWhiteSpace(content)) { Skip("empty"); continue; }
                    if (content.Contains('\0')) { Skip("binary content"); continue; }

                    known.Add(path);
                    added.Add(new ContextDocItem { Title = Path.GetFileName(path), Content = content, Source = path });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Skip("unreadable");
                }
            }

            CommitAttached(added, added.Count == 1 ? $"Attached {added[0].Name}" : $"Attached {added.Count} files", skipped, hitLimit: false);
        }

        private void AttachFolder(string folder)
        {
            FolderScanResult result;
            try
            {
                result = FolderScanner.Scan(folder);
            }
            catch (Exception ex)
            {
                ShowAttachStatus($"Could not read folder '{folder}': {ex.Message}", warning: true);
                return;
            }

            var known = _contextDocuments.Select(d => d.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var skipped = result.Skipped.ToDictionary(s => s.Key, s => s.Value);
            var added = new List<ContextDocItem>();
            foreach (var file in result.Files)
            {
                if (!known.Add(file.FullPath))
                {
                    skipped["already attached"] = skipped.GetValueOrDefault("already attached") + 1;
                    continue;
                }
                added.Add(new ContextDocItem { Title = file.RelativePath, Content = file.Content, Source = file.FullPath });
            }

            var name = Path.GetFileName(folder.TrimEnd('\\', '/'));
            CommitAttached(added, $"Attached {added.Count} file(s) from '{name}'", skipped, result.HitLimit);
        }

        private void CommitAttached(List<ContextDocItem> added, string headline, Dictionary<string, int> skipped, bool hitLimit)
        {
            foreach (var item in added) _contextDocuments.Add(item);
            UpdateContextSummary();

            var message = added.Count == 0 ? "Nothing new was attached" : headline;
            if (skipped.Count > 0)
            {
                message += " - skipped: " + string.Join(", ", skipped.Select(s => $"{s.Value} {s.Key}"));
            }
            message += ".";

            ShowAttachStatus(message, warning: hitLimit || added.Count == 0,
                undo: added.Count == 0 ? null : () =>
                {
                    foreach (var item in added) _contextDocuments.Remove(item);
                    UpdateContextSummary();
                    ShowAttachStatus($"Removed the {added.Count} document(s) just attached.");
                });
        }

        private void ShowAttachStatus(string message, bool warning = false, Action? undo = null)
        {
            _undoAction = undo;
            AttachStatusText.Text = message;
            AttachStatusText.Foreground = warning ? System.Windows.Media.Brushes.SaddleBrown : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x14, 0x53, 0x2D));
            AttachStatusBorder.Background = warning ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFE, 0xF3, 0xC7)) : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xDC, 0xFC, 0xE7));
            AttachStatusBorder.BorderBrush = warning ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFC, 0xD3, 0x4D)) : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x86, 0xEF, 0xAC));
            UndoButton.Visibility = undo is null ? Visibility.Collapsed : Visibility.Visible;
            AttachStatusBorder.Visibility = Visibility.Visible;
        }

        private void OnUndoClicked(object sender, RoutedEventArgs e)
        {
            var undo = _undoAction;
            _undoAction = null;
            undo?.Invoke();
        }

        private void OnDismissStatusClicked(object sender, RoutedEventArgs e)
        {
            _undoAction = null;
            AttachStatusBorder.Visibility = Visibility.Collapsed;
        }

        #region Drag & drop of files and folders onto the context panel

        private static bool HasFiles(DragEventArgs e) => e.Data.GetDataPresent(DataFormats.FileDrop);

        private void OnContextDragOver(object sender, DragEventArgs e)
        {
            e.Effects = HasFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
            if (HasFiles(e))
            {
                ContextPanel.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x25, 0x63, 0xEB));
                ContextPanel.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xEA, 0xFF));
            }
        }

        private void OnContextDragLeave(object sender, DragEventArgs e) => ResetDropHighlight();

        private void ResetDropHighlight()
        {
            ContextPanel.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCB, 0xD5, 0xE1));
            ContextPanel.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF1, 0xF5, 0xF9));
        }

        private void OnContextDrop(object sender, DragEventArgs e)
        {
            ResetDropHighlight();
            e.Handled = true;
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] dropped) return;

            var files = dropped.Where(File.Exists).ToList();
            var folders = dropped.Where(Directory.Exists).ToList();
            if (folders.Count == 0) { AttachFiles(files); return; }

            // Folders and loose files can arrive together; scan each folder, then the loose files, and
            // report once so the status line is not overwritten between them.
            var before = _contextDocuments.Count;
            foreach (var folder in folders) AttachFolder(folder);
            if (files.Count > 0) AttachFiles(files);
            if (folders.Count + (files.Count > 0 ? 1 : 0) > 1)
            {
                ShowAttachStatus($"Dropped items added {_contextDocuments.Count - before} document(s).");
            }
        }

        #endregion

        private void OnAddCustomDocClicked(object sender, RoutedEventArgs e)
        {
            var title = NewDocTitleBox.Text.Trim();
            var content = NewDocContentBox.Text.Trim();

            if (string.IsNullOrEmpty(content))
            {
                ShowAttachStatus("Type the note's text first. To attach files, use Attach Files / Attach Folder or drag them here.", warning: true);
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
                    var index = _contextDocuments.IndexOf(item);
                    _contextDocuments.Remove(item);
                    UpdateContextSummary();
                    ShowAttachStatus($"Removed {item.Name}.", undo: () =>
                    {
                        _contextDocuments.Insert(Math.Min(index, _contextDocuments.Count), item);
                        UpdateContextSummary();
                        ShowAttachStatus($"Restored {item.Name}.");
                    });
                }
            }
        }

        private void OnClearContextDocsClicked(object sender, RoutedEventArgs e)
        {
            if (_contextDocuments.Count == 0) return;

            var removed = _contextDocuments.ToList();
            _contextDocuments.Clear();
            UpdateContextSummary();
            ShowAttachStatus($"Cleared {removed.Count} document(s).", undo: () =>
            {
                foreach (var item in removed) _contextDocuments.Add(item);
                UpdateContextSummary();
                ShowAttachStatus($"Restored {removed.Count} document(s).");
            });
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
                var response = await _http.GetAsync($"api/resources?limit={ListPageSize}");
                var content = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    var items = JsonSerializer.Deserialize<List<ManagedResourceItem>>(content, JsonOptions) ?? [];
                    var total = response.Headers.TryGetValues("X-Total-Count", out var v) && int.TryParse(v.FirstOrDefault(), out var t) ? t : items.Count;
                    _managedResources.Clear();
                    foreach (var item in items) _managedResources.Add(item);

                    var summary = total > items.Count
                        ? $" showing {items.Count} of {total} document(s) for tenant '{_tenantId}' (most recent first)"
                        : $" {total} document(s) for tenant '{_tenantId}'";
                    summary += await StatsSuffixAsync();
                    DocumentsSummaryText.Text = summary;
                    DeleteFailedButton.IsEnabled = _managedResources.Any(m => m.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase));
                    SetStatus($"Loaded {items.Count} document(s).");
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

        private const int ListPageSize = 500;

        /// <summary>The volume at a glance, from GET api/resources/stats. Optional: the list is useful without it.</summary>
        private async Task<string> StatsSuffixAsync()
        {
            try
            {
                var json = await _http.GetStringAsync("api/resources/stats");
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var failed = root.GetProperty("byStatus").TryGetProperty("Failed", out var f) ? f.GetInt32() : 0;
                return $" · {root.GetProperty("passages").GetInt32():N0} passages" + (failed > 0 ? $" · {failed} failed" : "");
            }
            catch
            {
                return string.Empty;
            }
        }

        private async void OnReingestDocumentClicked(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string resourceId) return;

            SetBusy($"Re-queuing {resourceId}...");
            try
            {
                var response = await _http.PostAsync($"api/resources/{resourceId}/reingest", null);
                SetStatus(response.IsSuccessStatusCode
                    ? $"Queued {resourceId} for processing again. Refresh in a moment to see its status."
                    : $"Re-ingest failed: {(int)response.StatusCode} {response.ReasonPhrase}.");
                if (response.IsSuccessStatusCode) await RefreshDocumentsAsync();
            }
            catch (Exception ex)
            {
                SetStatus($"Re-ingest failed: {ex.Message}");
            }
        }

        private async void OnDeleteFailedClicked(object sender, RoutedEventArgs e)
        {
            var failed = _managedResources.Where(m => m.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase)).ToList();
            if (failed.Count == 0) return;

            var confirm = MessageBox.Show(
                $"Delete {failed.Count} failed document(s)?\n\n{string.Join("\n", failed.Take(8).Select(f => "  " + f.Uri))}{(failed.Count > 8 ? "\n  …" : "")}\n\nThis cannot be undone.",
                "Confirm Delete Failed", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            SetBusy("Deleting failed documents...");
            try
            {
                var response = await _http.PostAsJsonAsync("api/resources/delete", new { status = "Failed" });
                if (response.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    SetStatus($"Deleted {doc.RootElement.GetProperty("deleted").GetArrayLength()} failed document(s).");
                    await RefreshDocumentsAsync();
                }
                else
                {
                    SetStatus($"Delete failed: {(int)response.StatusCode} {response.ReasonPhrase}.");
                }
            }
            catch (Exception ex)
            {
                SetStatus($"Delete failed: {ex.Message}");
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
                    DeleteFailedButton.IsEnabled = _managedResources.Any(m => m.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase));
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

        private void OnBudgetChanged(object sender, TextChangedEventArgs e)
        {
            if (IsLoaded) UpdateContextSummary();
        }

        private void UpdateContextSummary()
        {
            var count = _contextDocuments.Count;
            var totalTokens = _contextDocuments.Sum(d => d.EstimatedTokens);
            ContextEmptyHint.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ClearAllButton.IsEnabled = count > 0;

            if (count == 0)
            {
                ContextSummaryText.Text = "No documents attached";
                ContextSummaryText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x64, 0x74, 0x8B));
            }
            else
            {
                var text = $"{count} document{(count == 1 ? "" : "s")} · ~{totalTokens:N0} tokens (estimated)";
                var over = int.TryParse(AskBudgetBox.Text, out var budget) && budget > 0 && totalTokens > budget;
                if (over)
                {
                    text += $" · more than the {budget:N0}-token budget, so only the passages most relevant to your question will be used";
                }
                ContextSummaryText.Text = text;
                ContextSummaryText.Foreground = over
                    ? System.Windows.Media.Brushes.SaddleBrown
                    : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x64, 0x74, 0x8B));
            }

            AskButton.Content = count > 0
                ? $"Ask Knovara ({count} Doc{(count > 1 ? "s" : "")} in Context)"
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

        private const long MaxUploadBytes = 25L * 1024 * 1024;

        private async void OnUploadFileClicked(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Select a Document to Upload",
                Filter = "Documents (*.pdf;*.docx;*.pptx;*.html;*.htm;*.md;*.markdown;*.txt)|*.pdf;*.docx;*.pptx;*.html;*.htm;*.md;*.markdown;*.txt"
            };
            if (dialog.ShowDialog() != true) return;

            var info = new FileInfo(dialog.FileName);
            if (info.Length == 0 || info.Length > MaxUploadBytes)
            {
                MessageBox.Show(info.Length == 0 ? "That file is empty." : $"That file is {info.Length / (1024 * 1024)} MB; the limit is {MaxUploadBytes / (1024 * 1024)} MB.",
                    "Cannot Upload", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SetBusy($"Uploading {info.Name}...");
            try
            {
                var payload = new { fileName = info.Name, contentBase64 = Convert.ToBase64String(await File.ReadAllBytesAsync(dialog.FileName)) };
                var response = await _http.PostAsJsonAsync("api/resources/upload", payload);
                var content = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    IngestResultBox.Text = $"=== UPLOAD ACCEPTED ===\nStatus: {(int)response.StatusCode} {response.StatusCode}\n\n{FormatJson(content)}\n\n"
                        + "Processing is asynchronous: check the Documents tab (or Resource Inspector) until it shows Ready.";
                    SetStatus($"Uploaded {info.Name}.");
                    try { StatusResourceIdBox.Text = JsonDocument.Parse(content).RootElement.GetProperty("resourceId").GetString() ?? ""; } catch { }
                    await RefreshDocumentsAsync();
                }
                else
                {
                    IngestResultBox.Text = $"=== UPLOAD REJECTED ===\nStatus: {(int)response.StatusCode} {response.ReasonPhrase}\n\n{content}";
                    SetStatus($"Upload failed: {(int)response.StatusCode}.");
                }
            }
            catch (Exception ex)
            {
                IngestResultBox.Text = $"=== EXCEPTION ===\nCould not reach API at {_baseUrl}\nError: {ex.Message}";
                SetStatus($"Upload failed: {ex.Message}");
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
