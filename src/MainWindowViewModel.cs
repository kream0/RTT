using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
// Use explicit System.Windows references to avoid ambiguity
using System.Windows;
using System.Windows.Input;

namespace RepoToTxtGui
{
    public class MainWindowViewModel : INotifyPropertyChanged
    {
        // INotifyPropertyChanged Implementation
        public event PropertyChangedEventHandler? PropertyChanged;

        // Fields for debouncing
        private CancellationTokenSource? _debounceTokenSource;
        private readonly int _debounceDelay = 500; // 500ms delay

        // Token Counting Service
        private readonly TokenCounterService? _tokenCounterService;
        private bool _tokenCounterAvailable = false;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            Application.Current?.Dispatcher?.Invoke(CommandManager.InvalidateRequerySuggested);
        }        // Commands
        public ICommand SelectFolderCommand { get; private set; }
        public ICommand SelectOutputFileCommand { get; private set; }
        public ICommand CopyToClipboardCommand { get; private set; }
        public ICommand SaveToFileCommand { get; private set; }
        public ICommand RefreshCommand { get; private set; }

        // Backing fields
        private ObservableCollection<TreeNodeViewModel> _rootNodes = new ObservableCollection<TreeNodeViewModel>();
        private string _outputText = string.Empty;
        private string? _selectedFolderPath;
        private string? _outputFilePath;
        private bool _isBusy = false;
        private bool _isOutputToConsole = true; // Changed default value to true
        private bool _isDarkMode = false;
        private string? _statusText;
        private bool _useWebPreset = true;
        private string _userPrePrompt = string.Empty;
        private readonly HashSet<string> _detectedFileExtensions = new(StringComparer.OrdinalIgnoreCase);
        private string _concatenatedFileContents = string.Empty; // For pre-prompt optimization
        private CancellationTokenSource? _prePromptDebounceCts;

        // LLM Token Counting related fields
        private LlmModel _selectedLlmModel = LlmModel.Gpt4o; // Default model
        private string _tokenCountDisplay = "Tokens: (not available)";

        // Properties for data binding
        public ObservableCollection<TreeNodeViewModel> RootNodes
        {
            get => _rootNodes;
            set { _rootNodes = value; OnPropertyChanged(); }
        }

        public ObservableCollection<FileTypeFilterViewModel> FileTypeFilters { get; } = new(); public string UserPrePrompt
        {
            get => _userPrePrompt;
            set
            {
                if (_userPrePrompt != value)
                {
                    _userPrePrompt = value;
                    OnPropertyChanged();
                    DebouncePrePromptUpdate(); // Optimized update for pre-prompt
                }
            }
        }        // Debouncing method for pre-prompt changes
        private void DebounceUserPrePromptChange()
        {
            // This method is now replaced by DebouncePrePromptUpdate and UpdateOutputWithNewPrePrompt
            // for better performance and to avoid focus loss.
            // Kept for reference during transition or if needed elsewhere with original behavior.
            // For now, UserPrePrompt setter calls DebouncePrePromptUpdate.
        }

        private void DebouncePrePromptUpdate()
        {
            _prePromptDebounceCts?.Cancel();
            _prePromptDebounceCts = new CancellationTokenSource();
            var token = _prePromptDebounceCts.Token;

            Task.Delay(_debounceDelay, token).ContinueWith(t =>
            {
                if (t.IsCanceled || token.IsCancellationRequested) return;
                Application.Current?.Dispatcher?.Invoke(() =>
                {
                    if (token.IsCancellationRequested) return;
                    UpdateOutputWithNewPrePrompt();
                });
            }, TaskScheduler.Default);
        }

        private void UpdateOutputWithNewPrePrompt()
        {
            if (IsBusy) // If a full regeneration is in progress, skip this lightweight update.
            {
                // Optionally, queue this update to run after IsBusy is false.
                // For now, just skip to keep it simple. The full regeneration will include the latest pre-prompt.
                return;
            }

            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(UserPrePrompt))
            {
                sb.AppendLine(UserPrePrompt.Trim());
                sb.AppendLine();
            }
            sb.Append(_concatenatedFileContents);
            OutputText = sb.ToString(); // This will trigger token updates via its setter
        }

        public string OutputText
        {
            get => _outputText;
            set
            {
                if (_outputText != value)
                {
                    _outputText = value;
                    OnPropertyChanged();
                    // Update token count whenever OutputText changes
                    if (_tokenCounterAvailable)
                    {
                        Console.WriteLine($"DEBUG: Setting OutputText with {(string.IsNullOrEmpty(value) ? "EMPTY" : $"{value.Length} chars")} content. About to update token count.");
                        UpdateTokenCount();
                    }
                }
            }
        }

        // Token counting helper method
        private void UpdateTokenCount()
        {
            // Don't recalculate if the application is busy with other operations
            if (IsBusy || !_tokenCounterAvailable) return;

            // Start token counting operation asynchronously
            _ = UpdateTokenCountAsync(_outputText);
        }

        // LLM Model Selection Properties
        public LlmModel SelectedLlmModel
        {
            get => _selectedLlmModel;
            set
            {
                if (_selectedLlmModel != value)
                {
                    _selectedLlmModel = value;
                    OnPropertyChanged();
                    // Recalculate tokens for the current OutputText with the new model
                    if (_tokenCounterAvailable)
                    {
                        UpdateTokenCount();
                    }
                }
            }
        }

        public LlmModel[] AvailableLlmModels => (LlmModel[])Enum.GetValues(typeof(LlmModel));

        public string TokenCountDisplay
        {
            get => _tokenCountDisplay;
            private set // Private setter as it's updated internally
            {
                if (_tokenCountDisplay != value)
                {
                    _tokenCountDisplay = value;
                    OnPropertyChanged();
                }
            }
        }

        private async Task UpdateTokenCountAsync(string textToCount)
        {
            if (!_tokenCounterAvailable)
            {
                TokenCountDisplay = "Tokens: (not available)";
                return;
            }

            // Preserve the current display if it's already calculating, to avoid flicker
            string calculatingMsg = "Tokens: Calculating...";
            bool alreadyCalculating = TokenCountDisplay == calculatingMsg;
            if (!alreadyCalculating)
            {
                TokenCountDisplay = calculatingMsg;
            }

            LlmModel currentModel = _selectedLlmModel; // Capture current selection
            Console.WriteLine($"DEBUG: UpdateTokenCountAsync called with model: {currentModel}, text length: {(string.IsNullOrEmpty(textToCount) ? 0 : textToCount.Length)}");

            await Task.Run(() => // Perform calculation on a background thread
            {
                try
                {
                    var (count, encodingName, isProxy) = _tokenCounterService.CountTokens(textToCount, currentModel);
                    Console.WriteLine($"DEBUG: Token count result for {currentModel}: count={count}, encoding={encodingName}, isProxy={isProxy}");

                    string suffix = isProxy
                        ? $" (proxy: {encodingName})"
                        : $" (tokenizer: {encodingName})";
                    string newDisplay = $"Tokens: {count}{suffix}";

                    Application.Current?.Dispatcher?.Invoke(() => // Update UI on UI thread
                    {
                        TokenCountDisplay = newDisplay;
                        Console.WriteLine($"DEBUG: TokenCountDisplay updated to: {newDisplay}");
                    });
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"ERROR: Token counting error: {ex.Message}");
                    Application.Current?.Dispatcher?.Invoke(() =>
                    {
                        TokenCountDisplay = "Tokens: Error calculating";
                    });
                }
            });
        }

        public bool IsBusy
        {
            get => _isBusy;
            set { _isBusy = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsUiEnabled)); }
        }

        public bool IsUiEnabled => !IsBusy;
        public bool IsPrePromptTextBoxEnabled => true; // Pre-prompt textbox should remain enabled even during brief busy states from its own updates.
                                                       // If global IsBusy is true due to other long operations (like initial load),
                                                       // it might still be effectively non-interactive, but focus won't be lost.
                                                       // A more robust solution might bind to a combined state: IsUiEnabled || IsPrePromptUpdateInProgress

        public bool IsDarkMode
        {
            get => _isDarkMode;
            set
            {
                if (_isDarkMode != value)
                {
                    _isDarkMode = value;
                    OnPropertyChanged();
                    ThemeManager.ApplyTheme(_isDarkMode ? ThemeManager.AppTheme.Dark : ThemeManager.AppTheme.Light);
                }
            }
        }

        public string? StatusText
        {
            get => _statusText;
            set
            {
                if (_statusText != value)
                {
                    _statusText = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsOutputToConsole
        {
            get => _isOutputToConsole;
            set
            {
                if (_isOutputToConsole != value)
                {
                    _isOutputToConsole = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsOutputFileEnabled));
                }
            }
        }

        public bool IsOutputFileEnabled => !IsOutputToConsole;

        public string SelectedFolderPath
        {
            get => _selectedFolderPath ?? string.Empty;
            private set // Make setter private
            {
                if (_selectedFolderPath != value)
                {
                    _selectedFolderPath = value;
                    OnPropertyChanged();
                }
            }
        }

        public string OutputFilePath
        {
            get => _outputFilePath ?? string.Empty;
            set
            {
                if (_outputFilePath != value)
                {
                    _outputFilePath = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool UseWebPreset
        {
            get => _useWebPreset;
            set
            {
                if (_useWebPreset != value)
                {
                    _useWebPreset = value;
                    OnPropertyChanged();
                    UpdateExclusionsAndRefreshTree();
                }
            }
        }        // Commands

        // Constructor
        public MainWindowViewModel()
        {
            try
            {
                _tokenCounterService = new TokenCounterService();
                _tokenCounterAvailable = true;
                Console.WriteLine("DEBUG: Token counter service initialized successfully, _tokenCounterAvailable = true");
            }
            catch (Exception ex)
            {
                _tokenCounterAvailable = false;
                _tokenCounterService = null;
                Console.WriteLine($"DEBUG: Token counter initialization failed: {ex.Message}");
                MessageBox.Show($"Error initializing token counter: {ex.Message}\n\nThe application will continue without token counting functionality.",
                    "Token Counter Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            SelectFolderCommand = new RelayCommand(async _ => await SelectFolderAndProcessAsync(), _ => !IsBusy);
            SelectOutputFileCommand = new RelayCommand(_ => SelectOutputFile(), _ => !IsBusy && !IsOutputToConsole);
            CopyToClipboardCommand = new RelayCommand(CopyOutputToClipboard, _ => !IsBusy && !string.IsNullOrEmpty(OutputText));
            SaveToFileCommand = new RelayCommand(async _ => await SaveToFileAsync(), _ => !IsBusy && !IsOutputToConsole && !string.IsNullOrEmpty(OutputText));
            RefreshCommand = new RelayCommand(async _ => await RefreshFolderAsync(), _ => !IsBusy && !string.IsNullOrEmpty(SelectedFolderPath)); UpdateExclusionsBasedOnPresets();

            // Initialize dark mode from saved preference
            _isDarkMode = ThemeManager.LoadCurrentThemePreference() == ThemeManager.AppTheme.Dark;

            // Only initialize token counting if the service was successfully created
            if (_tokenCounterAvailable)
            {
                Console.WriteLine("DEBUG: Initial token count calculation on startup");
                _ = UpdateTokenCountAsync(_outputText);
            }
        }

        private void UpdateExclusionsBasedOnPresets()
        {
            EffectiveExclusions = UseWebPreset
                ? new List<string>(WebExclusionPreset)
                : new List<string>();
            PrepareExclusionMatchers();
        }

        private void UpdateExclusionsAndRefreshTree()
        {
            UpdateExclusionsBasedOnPresets(); // Call the new method
            if (!string.IsNullOrEmpty(SelectedFolderPath) && Directory.Exists(SelectedFolderPath))
            {
                _ = LoadFolderAsync();
            }
        }

        // Exclusion handling
        private List<string> EffectiveExclusions { get; set; } = new List<string>();
        private HashSet<string> ExcludedDirNames { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> ExcludedFileNames { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private List<string> ExcludedWildcards { get; set; } = new List<string>();

        private List<string> GetDefaultExclusions()
        {
            // Return a copy of the web exclusion preset
            return new List<string>(WebExclusionPreset);
        }

        private void PrepareExclusionMatchers()
        {
            ExcludedDirNames.Clear();
            ExcludedFileNames.Clear();
            ExcludedWildcards.Clear();

            foreach (var exclusion in EffectiveExclusions)
            {
                if (string.IsNullOrWhiteSpace(exclusion)) continue;

                string cleanedExclusion = exclusion.Replace('\\', '/');
                ExcludedWildcards.Add(cleanedExclusion); // Always add to wildcards

                if (!cleanedExclusion.Contains('*') && !cleanedExclusion.Contains('?') && !cleanedExclusion.Contains('/'))
                {
                    if (cleanedExclusion.Contains('.'))
                    {
                        ExcludedFileNames.Add(cleanedExclusion);
                        ExcludedDirNames.Add(cleanedExclusion); // Could be a dir like .git
                    }
                    else
                    {
                        ExcludedDirNames.Add(cleanedExclusion);
                    }
                }
            }
        }

        // UI Actions
        private async Task SelectFolderAndProcessAsync()
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog // Using WPF folder browser
            {
                Title = "Select the repository folder"
            };

            if (dialog.ShowDialog() == true)
            {
                SelectedFolderPath = dialog.FolderName; // Uses the private setter
                await LoadFolderAsync();
            }
        }

        private void SelectOutputFile()
        {
            var dialog = new Microsoft.Win32.SaveFileDialog // Using WPF save file dialog
            {
                Title = "Save Output As",
                Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
                DefaultExt = "txt"
            };

            if (dialog.ShowDialog() == true)
            {
                OutputFilePath = dialog.FileName;
            }
        }
        private async Task RefreshFolderAsync()
        {
            if (string.IsNullOrEmpty(SelectedFolderPath) || IsBusy) return;

            // Store the current pre-prompt and file content cache before refresh
            string currentPrePrompt = UserPrePrompt;
            string currentFileContentCache = _concatenatedFileContents;

            // 1. Store current selection
            var selectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var stack = new Stack<TreeNodeViewModel>(RootNodes);
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                if (node.IsChecked == true || node.IsChecked == null) // True or Indeterminate
                {
                    selectedPaths.Add(node.FullPath);
                }
                // For files, IsChecked == null isn't typical unless explicitly set,
                // but for directories, it means some children are checked.
                // If a directory is indeterminate, we want to re-evaluate it based on its children after refresh.
                // If a file was checked, it should remain checked.
                // If a directory was fully checked, it should remain fully checked (if all children still exist and are checked).
                // Storing FullPath for all IsChecked==true and IsChecked==null items is a good heuristic.
                // The actual check state will be re-calculated bottom-up after restoring individual node states.

                foreach (var child in node.Children.Reverse()) // Process in a way that mimics visual order if needed
                {
                    stack.Push(child);
                }
            }

            // Ensure root folder path is included if it was effectively selected
            if (RootNodes.Any(r => r.IsChecked == true || r.IsChecked == null) && !string.IsNullOrEmpty(SelectedFolderPath))
            {
                selectedPaths.Add(SelectedFolderPath);
            }

            await LoadFolderAsync(selectedPaths);

            // Restore the pre-prompt and update the UI after refresh is complete
            if (!string.IsNullOrEmpty(currentPrePrompt))
            {
                UserPrePrompt = currentPrePrompt;
                // Restore the concatenated file contents to maintain the preview
                _concatenatedFileContents = currentFileContentCache;
                UpdateOutputWithNewPrePrompt();
            }
        }

        private async Task LoadFolderAsync(HashSet<string>? selectionToRestore = null)
        {
            if (string.IsNullOrEmpty(SelectedFolderPath) || !Directory.Exists(SelectedFolderPath))
            {
                OutputText = "Selected folder is invalid.";
                StatusText = "Error: Invalid folder";
                return;
            }

            IsBusy = true;
            StatusText = selectionToRestore == null ? "Loading folder structure..." : "Refreshing folder structure...";
            OutputText = string.Empty; // Clear previous output

            Application.Current.Dispatcher.Invoke(() =>
            {
                RootNodes.Clear();
                FileTypeFilters.Clear();
            });
            _detectedFileExtensions.Clear();

            PrepareExclusionMatchers(); // Ensure exclusions are up-to-date

            try
            {
                var rootDirInfo = new DirectoryInfo(SelectedFolderPath);
                var rootNode = new TreeNodeViewModel(rootDirInfo.Name, rootDirInfo.FullName, true);                // Populate TreeView in background
                await Task.Run(() => BuildTree(rootDirInfo, rootNode, _detectedFileExtensions, selectionToRestore));

                var sortedExtensions = _detectedFileExtensions.OrderBy(ext => ext).ToList();
                var newFilters = new List<FileTypeFilterViewModel>();
                foreach (var ext in sortedExtensions)
                {
                    newFilters.Add(new FileTypeFilterViewModel(ext, true, HandleFileTypeFilterChanged)); // All true initially
                }

                Application.Current.Dispatcher.Invoke(() =>
                {
                    foreach (var filter in newFilters) FileTypeFilters.Add(filter);
                    RootNodes.Add(rootNode);

                    // Apply selection states based on whether this is a fresh load or a refresh
                    if (selectionToRestore != null && selectionToRestore.Count > 0)
                    {
                        // When refreshing, restore previously checked items
                        TraverseAndApplyRestoredSelection(rootNode, selectionToRestore);
                    }
                    else
                    {
                        // For initial load, check everything by default
                        rootNode.SetIsChecked(true, true, false);
                    }
                });

                // Generate initial output
                await GenerateOutputAsync();
                StatusText = "Ready";
            }
            catch (Exception ex)
            {
                OutputText = $"Error loading folder: {ex.Message}";
                StatusText = $"Error: {ex.Message}";
                Application.Current.Dispatcher.Invoke(() =>
                {
                    RootNodes.Clear();
                    FileTypeFilters.Clear();
                });
            }
            finally
            {
                IsBusy = false;
                CommandManager.InvalidateRequerySuggested();

                // Explicitly update token count now that IsBusy is set to false
                if (_tokenCounterAvailable && !string.IsNullOrEmpty(_outputText))
                {
                    Console.WriteLine("DEBUG: Explicitly updating token count after folder load is complete");
                    await UpdateTokenCountAsync(_outputText);
                }
            }
        }

        // Tree building and file handling
        private void BuildTree(DirectoryInfo currentDirInfo,
        TreeNodeViewModel parentNode,
        HashSet<string> detectedExtensions,
        HashSet<string>? selectionToRestore = null)
        {
            var childrenToAdd = new List<TreeNodeViewModel>();

            // Add Directories
            try
            {
                foreach (var dirInfo in currentDirInfo.EnumerateDirectories())
                {
                    // Check if directory should be excluded completely
                    bool shouldExclude = UseWebPreset && IsDirectoryHardExcluded(dirInfo.Name);
                    if (shouldExclude)
                    {
                        // Skip this directory entirely
                        continue;
                    }

                    var dirNode = new TreeNodeViewModel(dirInfo.Name, dirInfo.FullName, true, parentNode);

                    // Note: We don't set IsChecked here - it will be handled in LoadFolderAsync
                    // after the full tree structure is built and added to the UI.

                    // Add to collection for later sorted addition
                    childrenToAdd.Add(dirNode);

                    BuildTree(dirInfo, dirNode, detectedExtensions, selectionToRestore); // Recurse
                }
            }
            catch (UnauthorizedAccessException) { /* Skip inaccessible folders */ }
            catch (Exception ex)
            {
                Console.WriteLine($"Error enumerating directories in {currentDirInfo.FullName}: {ex.Message}");
            }

            // Add Files
            try
            {
                foreach (var fileInfo in currentDirInfo.EnumerateFiles())
                {
                    string fileExtension = Path.GetExtension(fileInfo.Name).TrimStart('.').ToLowerInvariant();
                    if (string.IsNullOrEmpty(fileExtension)) detectedExtensions.Add(string.Empty); // For files with no extension
                    else detectedExtensions.Add(fileExtension);

                    // Check exclusion for initial state setting
                    bool initiallyExcluded = IsPathInitiallyExcluded(fileInfo.Name, fileInfo.FullName, false);
                    var fileNode = new TreeNodeViewModel(fileInfo.Name, fileInfo.FullName, false, parentNode);
                    fileNode.SetIsChecked(!initiallyExcluded, false, false); // Set initial state without propagation

                    // Add to collection for later sorted addition
                    childrenToAdd.Add(fileNode);
                }
            }
            catch (UnauthorizedAccessException) { /* Skip inaccessible files */ }
            catch (Exception ex)
            {
                Console.WriteLine($"Error enumerating files in {currentDirInfo.FullName}: {ex.Message}");
            }

            if (childrenToAdd.Any())
            {
                // Sort children: directories first, then alphabetically
                var sortedChildren = childrenToAdd
                    .OrderByDescending(n => n.IsDirectory) // Directories first
                    .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // Batch updates to UI thread to reduce dispatcher calls
                if (Application.Current?.Dispatcher != null)
                {
                    Application.Current.Dispatcher.BeginInvoke(() =>
                    {
                        foreach (var childNode in sortedChildren)
                        {
                            parentNode.Children.Add(childNode);
                        }
                    }, System.Windows.Threading.DispatcherPriority.Background);
                }
            }
        }

        /// <summary>
        /// Traverses the tree and applies the restored selection states from a previous refresh
        /// </summary>
        /// <param name="node">The current node being processed</param>
        /// <param name="selectionToRestore">Set of paths to restore as checked</param>
        /// <returns>True if any child node was selected, false otherwise</returns>
        private bool TraverseAndApplyRestoredSelection(TreeNodeViewModel node, HashSet<string> selectionToRestore)
        {
            bool anyChildSelected = false;
            bool isCurrentNodeSelected = selectionToRestore.Contains(node.FullPath);

            // Process children first (bottom-up approach)
            foreach (var child in node.Children)
            {
                bool isChildSelected = TraverseAndApplyRestoredSelection(child, selectionToRestore);
                if (isChildSelected)
                {
                    anyChildSelected = true;
                }
            }

            // If this is a directory and any of its children are selected, or if it's explicitly selected
            if ((node.IsDirectory && anyChildSelected) || isCurrentNodeSelected)
            {
                // We use SetIsChecked(true, false, false) to avoid recursive propagation
                // which would override the carefully restored child states
                node.SetIsChecked(true, false, false);
                return true;
            }

            // For files, directly set the state based on selection
            if (!node.IsDirectory)
            {
                bool shouldCheck = isCurrentNodeSelected;
                node.SetIsChecked(shouldCheck, false, false);
                return shouldCheck;
            }

            return false;
        }

        // New method to determine if a directory should be completely excluded
        private bool IsDirectoryHardExcluded(string name)
        {
            // These are common, often large, and usually not wanted.
            return name.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals(".idea", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals(".vscode", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals(".next", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals(".nuxt", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("dist", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("build", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsPathInitiallyExcluded(string name, string fullPath, bool isDirectory)
        {
            if (EffectiveExclusions.Count == 0) return false;

            if (isDirectory && ExcludedDirNames.Contains(name)) return true;
            if (!isDirectory && ExcludedFileNames.Contains(name)) return true;

            if (MatchesWildcardInternal(name, ExcludedWildcards, false)) return true;

            if (!string.IsNullOrEmpty(_selectedFolderPath))
            {
                string relativePath = Path.GetRelativePath(_selectedFolderPath, fullPath).Replace(Path.DirectorySeparatorChar, '/');
                if (MatchesWildcardInternal(relativePath, ExcludedWildcards, true)) return true;
            }
            return false;
        }

        private async void HandleFileTypeFilterChanged(FileTypeFilterViewModel changedFilter)
        {
            if (IsBusy || RootNodes.Count == 0 || Application.Current == null) return;

            IsBusy = true;
            StatusText = $"Updating selection for {changedFilter.DisplayName}...";
            try
            {
                var nodesToProcess = new List<TreeNodeViewModel>();
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    foreach (var rootNode in RootNodes) nodesToProcess.Add(rootNode);
                }, System.Windows.Threading.DispatcherPriority.Background);

                var parentsOfChangedNodes = new HashSet<TreeNodeViewModel>();

                await Task.Run(async () =>
                {
                    var stack = new Stack<TreeNodeViewModel>(nodesToProcess);
                    while (stack.Count > 0)
                    {
                        var node = stack.Pop();
                        // Immutable properties can be read directly from background thread
                        // Name, IsDirectory, Parent are set at construction and don't change.
                        string nodeName = node.Name;
                        bool nodeIsDirectory = node.IsDirectory;

                        if (!nodeIsDirectory)
                        {
                            string fileExt = Path.GetExtension(nodeName).TrimStart('.').ToLowerInvariant();
                            bool shouldUpdate = (string.IsNullOrEmpty(changedFilter.Extension) && string.IsNullOrEmpty(fileExt)) ||
                                              (string.Equals(fileExt, changedFilter.Extension, StringComparison.OrdinalIgnoreCase));

                            if (shouldUpdate)
                            {
                                await Application.Current.Dispatcher.InvokeAsync(() =>
                                {
                                    // Check current state on UI thread before attempting to set, to avoid unnecessary work
                                    if (node.IsChecked != changedFilter.IsSelected)
                                    {
                                        node.SetIsChecked(changedFilter.IsSelected, false, false); // updateParent: false
                                        if (node.Parent != null)
                                        {
                                            parentsOfChangedNodes.Add(node.Parent);
                                        }
                                    }
                                }, System.Windows.Threading.DispatcherPriority.Background);
                            }
                        }
                        else // Is a directory
                        {
                            List<TreeNodeViewModel> childrenSnapshot = null;
                            await Application.Current.Dispatcher.InvokeAsync(() =>
                            {
                                childrenSnapshot = new List<TreeNodeViewModel>(node.Children.Reverse());
                            }, System.Windows.Threading.DispatcherPriority.Background);

                            if (childrenSnapshot != null)
                            {
                                foreach (var child in childrenSnapshot)
                                    stack.Push(child);
                            }
                        }
                    }
                });

                // After all individual nodes are updated (without parent propagation),
                // update the unique parents on the UI thread.
                if (parentsOfChangedNodes.Any())
                {
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        foreach (var parentNode in parentsOfChangedNodes)
                        {
                            parentNode.UpdateCheckStateBasedOnChildren();
                        }
                    }, System.Windows.Threading.DispatcherPriority.Background);
                }

                await Application.Current.Dispatcher.BeginInvoke(() => // Use BeginInvoke for lower priority UI update
                {
                    StatusText = "Selection updated.";
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                await Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    StatusText = $"Error updating for {changedFilter.DisplayName}: {ex.Message}";
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
            finally
            {
                await Application.Current.Dispatcher.InvokeAsync(async () => // Ensure IsBusy is set on UI thread and then regenerate
                {
                    IsBusy = false;
                    await RegenerateOutputAsync(); // Regenerate output after we've updated the file selections
                });
            }
        }

        // Output generation
        public async Task RegenerateOutputAsync()
        {
            if (IsBusy && StatusText != "Loading folder structure...") return; // Allow if initial load
            IsBusy = true;
            StatusText = "Generating output...";

            try
            {
                await Task.Run(async () =>
                {
                    if (string.IsNullOrEmpty(_selectedFolderPath)) return;

                    var filesToProcessMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var treeTraversalStack = new Stack<TreeNodeViewModel>();

                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        foreach (var rootNode in RootNodes.Reverse()) // Use RootNodes directly
                            treeTraversalStack.Push(rootNode);
                    }, System.Windows.Threading.DispatcherPriority.Background);

                    while (treeTraversalStack.Count > 0)
                    {
                        var node = treeTraversalStack.Pop();

                        // Immutable properties (Name, FullPath, IsDirectory) are read directly from the background thread.
                        // This assumes they are set at construction and not modified afterwards, which is true for TreeNodeViewModel.
                        bool nodeIsDirectory_directRead = node.IsDirectory;
                        string nodeFullPath_directRead = node.FullPath;

                        bool? nodeIsChecked = null; // Initialize to prevent CS0165
                        List<TreeNodeViewModel>? childrenSnapshot = null;

                        // Dispatch to UI thread only for IsChecked (mutable) and Children collection (UI-owned)
                        await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            nodeIsChecked = node.IsChecked;
                            if (nodeIsDirectory_directRead) // Use the directly read IsDirectory
                            {
                                childrenSnapshot = new List<TreeNodeViewModel>(node.Children.Reverse());
                            }
                        }, System.Windows.Threading.DispatcherPriority.Background);

                        if (nodeIsChecked == true)
                        {
                            if (!nodeIsDirectory_directRead)
                            {
                                string relativePath = Path.GetRelativePath(_selectedFolderPath, nodeFullPath_directRead).Replace(Path.DirectorySeparatorChar, '/');
                                if (!filesToProcessMap.ContainsKey(relativePath))
                                {
                                    filesToProcessMap.Add(relativePath, nodeFullPath_directRead);
                                }
                            }
                            else if (childrenSnapshot != null) // It's a checked directory
                            {
                                foreach (var child in childrenSnapshot) treeTraversalStack.Push(child);
                            }
                        }
                        else if (nodeIsChecked == null && nodeIsDirectory_directRead && childrenSnapshot != null) // Indeterminate directory
                        {
                            foreach (var child in childrenSnapshot) treeTraversalStack.Push(child);
                        }
                    }

                    var sortedRelativePaths = filesToProcessMap.Keys.ToList();
                    sortedRelativePaths.Sort(StringComparer.OrdinalIgnoreCase);

                    // Generate the files content without pre-prompt for caching
                    var fileContentBuilder = new StringBuilder();
                    if (!sortedRelativePaths.Any())
                    {
                        fileContentBuilder.AppendLine("No files selected or found based on current selection.");
                    }
                    else
                    {
                        fileContentBuilder.AppendLine("Directory Structure:");
                        fileContentBuilder.AppendLine();
                        BuildTreeStringInternal(sortedRelativePaths, fileContentBuilder);
                        fileContentBuilder.AppendLine();

                        fileContentBuilder.AppendLine("--- File Contents ---");
                        fileContentBuilder.AppendLine();

                        var fileContents = new ConcurrentDictionary<string, string>();
                        var fileReadErrors = new ConcurrentDictionary<string, string>();

                        var throttledTasks = new List<Task>();
                        var throttleSemaphore = new SemaphoreSlim(Math.Min(Environment.ProcessorCount * 2, 16));

                        foreach (var relativePath in sortedRelativePaths)
                        {
                            string fullPath = filesToProcessMap[relativePath];
                            throttledTasks.Add(Task.Run(async () =>
                            {
                                await throttleSemaphore.WaitAsync();
                                try
                                {
                                    var fileInfo = new FileInfo(fullPath);
                                    if (fileInfo.Length > 5 * 1024 * 1024)
                                    {
                                        fileReadErrors[relativePath] = $"Error: File '{Path.GetFileName(fullPath)}' is too large ({(double)fileInfo.Length / (1024 * 1024):F2}MB). Max 5MB.";
                                        return;
                                    }
                                    var content = await File.ReadAllTextAsync(fullPath);
                                    fileContents[relativePath] = content;
                                }
                                catch (IOException ex)
                                {
                                    fileReadErrors[relativePath] = $"Error reading file '{Path.GetFileName(fullPath)}': {ex.Message}";
                                }
                                catch (Exception ex)
                                {
                                    fileReadErrors[relativePath] = $"Error processing file '{Path.GetFileName(fullPath)}': {ex.Message}";
                                }
                                finally
                                {
                                    throttleSemaphore.Release();
                                }
                            }));
                        }

                        await Task.WhenAll(throttledTasks);

                        foreach (var relativePath in sortedRelativePaths)
                        {
                            fileContentBuilder.AppendLine($"\n---\nFile: /{relativePath}\n---");
                            if (fileReadErrors.TryGetValue(relativePath, out var error))
                            {
                                fileContentBuilder.AppendLine(error);
                            }
                            else if (fileContents.TryGetValue(relativePath, out var content))
                            {
                                fileContentBuilder.AppendLine(content);
                            }
                        }
                    }

                    // Store the file content without pre-prompt
                    _concatenatedFileContents = fileContentBuilder.ToString();

                    // Now create the full output with the pre-prompt
                    var outputBuilder = new StringBuilder();
                    string localUserPrePrompt = string.Empty;

                    // Get the current pre-prompt from the UI thread
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                        localUserPrePrompt = UserPrePrompt,
                        System.Windows.Threading.DispatcherPriority.Background);

                    if (!string.IsNullOrWhiteSpace(localUserPrePrompt))
                    {
                        outputBuilder.AppendLine(localUserPrePrompt.Trim());
                        outputBuilder.AppendLine();
                    }

                    // Append the file content
                    outputBuilder.Append(_concatenatedFileContents);

                    await Application.Current.Dispatcher.BeginInvoke(() => // Use BeginInvoke for lower priority UI update
                    {
                        Console.WriteLine($"DEBUG: About to set OutputText in RegenerateOutputAsync, length = {outputBuilder.Length}");
                        OutputText = outputBuilder.ToString();
                        Console.WriteLine("DEBUG: OutputText was set in RegenerateOutputAsync");
                    }, System.Windows.Threading.DispatcherPriority.Background);
                });
            }
            catch (Exception ex)
            {
                await Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    OutputText = $"Error generating output: {ex.Message}";
                    StatusText = $"Error: {ex.Message}";
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
            finally
            {
                bool shouldUpdateTokens = !string.IsNullOrEmpty(_outputText) && _tokenCounterAvailable;

                await Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    StatusText = "Ready";
                    IsBusy = false;
                    CommandManager.InvalidateRequerySuggested();
                }, System.Windows.Threading.DispatcherPriority.Background);

                // Ensure token count is updated after we're no longer busy
                if (shouldUpdateTokens)
                {
                    Console.WriteLine("DEBUG: Explicitly updating token count after RegenerateOutputAsync completes");
                    await UpdateTokenCountAsync(_outputText);
                }
            }
        }

        private async Task GenerateOutputAsync()
        {
            // Just call RegenerateOutputAsync which has the implementation
            await RegenerateOutputAsync();
        }

        private void CopyOutputToClipboard(object? parameter)
        {
            if (!string.IsNullOrEmpty(OutputText))
            {
                try
                {
                    // OutputText already includes the pre-prompt if it exists,
                    // as it's generated in RegenerateOutputAsync
                    System.Windows.Clipboard.SetText(OutputText);
                    StatusText = "Copied to clipboard";
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"Error copying to clipboard: {ex.Message}", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    StatusText = "Error copying to clipboard";
                }
            }
        }

        private async Task SaveToFileAsync()
        {
            if (string.IsNullOrEmpty(OutputFilePath))
            {
                // If no output file is selected, prompt user to select one
                SelectOutputFile();
                if (string.IsNullOrEmpty(OutputFilePath))
                {
                    return; // User canceled
                }
            }

            try
            {
                StatusText = "Saving file...";
                await File.WriteAllTextAsync(OutputFilePath, OutputText);
                System.Windows.MessageBox.Show($"File saved successfully to:\n{OutputFilePath}", "Success",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                StatusText = "File saved successfully";
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Error saving file: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                StatusText = "Error saving file";
            }
        }

        private static readonly List<string> WebExclusionPreset = new List<string>
        {
            "node_modules", ".git", "bin", "obj", "dist", "build", "vendor", ".next", ".nuxt",
            ".svelte-kit", "coverage", ".vscode", ".idea", ".cache", "__pycache__", ".pytest_cache",
            ".mypy_cache", ".venv", "venv", "env", ".yarn", ".angular", "bower_components",
            ".sass-cache", "uploads", "public/uploads", "out", ".parcel-cache", ".DS_Store",
            "Thumbs.db", "desktop.ini", ".directory", ".env", ".env.*", "*.config.js", "*.log",
            "*.tmp", "npm-debug.log*", "yarn-debug.log*", "yarn-error.log*", "*.ttf", "*.otf",
            "*.woff", "*.woff2", "*.eot", "*.jpg", "*.jpeg", "*.png", "*.gif", "*.bmp", "*.ico",
            "*.webp", "*.avif", "*.svg", "*.psd", "*.ai", "*.sketch", "*.fig", "*.xcf", "*.mp3",
            "*.wav", "*.ogg", "*.mp4", "*.webm", "*.avi", "*.mov", "*.mkv", "*.flac", "*.m4a",
            "*.aac", "*.pdf", "*.doc", "*.docx", "*.xls", "*.xlsx", "*.ppt", "*.pptx", "*.zip",
            "*.rar", "*.7z", "*.tar", "*.gz", "*.tgz", "*.bz2", "*.sqlite", "*.db", "*.mdb",
            "*.accdb", "*.dll", "*.exe", "*.so", "*.dylib", "*.class", "*.pyc", "*.pyo", "*.crx",
            "*.xpi", "*.app", "*.apk", "*.ipa", "yarn.lock", "package-lock.json", "composer.lock",
            "poetry.lock", "Pipfile.lock", "*.min.js", "*.min.css", "*.map", "*.bin", "*.dat",
            "*.bak", "Dockerfile.bak"
        };

        private static bool MatchesWildcardInternal(string nameOrPath, List<string> wildcards, bool isFullPathSegment)
        {
            string normalizedNameOrPath = nameOrPath.Replace('\\', '/');

            foreach (var wildcardPattern in wildcards)
            {
                if (normalizedNameOrPath.Equals(wildcardPattern, StringComparison.OrdinalIgnoreCase)) return true;

                if (wildcardPattern.StartsWith("*.") && normalizedNameOrPath.EndsWith(wildcardPattern.Substring(1), StringComparison.OrdinalIgnoreCase)) return true;
                if (wildcardPattern.EndsWith(".*") &&
                    normalizedNameOrPath.StartsWith(wildcardPattern.Substring(0, wildcardPattern.Length - 2), StringComparison.OrdinalIgnoreCase) &&
                    normalizedNameOrPath.LastIndexOf('.') > wildcardPattern.Length - 3)
                    return true;
                if (wildcardPattern.EndsWith("*") && !wildcardPattern.StartsWith("*") &&
                    normalizedNameOrPath.StartsWith(wildcardPattern.Substring(0, wildcardPattern.Length - 1), StringComparison.OrdinalIgnoreCase)) return true;
                if (wildcardPattern.StartsWith("*") && !wildcardPattern.EndsWith("*") &&
                    normalizedNameOrPath.EndsWith(wildcardPattern.Substring(1), StringComparison.OrdinalIgnoreCase)) return true;
                if (wildcardPattern.StartsWith("*") && wildcardPattern.EndsWith("*") && wildcardPattern.Length > 2 &&
                    normalizedNameOrPath.Contains(wildcardPattern.Substring(1, wildcardPattern.Length - 2), StringComparison.OrdinalIgnoreCase)) return true;

                if (isFullPathSegment && wildcardPattern.Contains("/"))
                {
                    if (normalizedNameOrPath.Contains(wildcardPattern, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            return false;
        }

        private static void BuildTreeStringInternal(List<string> relativePaths, StringBuilder builder)
        {
            var root = new Dictionary<string, object>();
            var sortedPaths = relativePaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();

            foreach (var path in sortedPaths)
            {
                var parts = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                var currentNode = root;
                for (int i = 0; i < parts.Length; i++)
                {
                    var part = parts[i];
                    bool isLastPart = (i == parts.Length - 1);

                    if (!currentNode.ContainsKey(part))
                    {
                        currentNode.Add(part, isLastPart ? null! : new Dictionary<string, object>());
                    }

                    if (!isLastPart)
                    {
                        if (!(currentNode[part] is Dictionary<string, object>))
                        {
                            currentNode[part] = new Dictionary<string, object>();
                        }
                        currentNode = (Dictionary<string, object>)currentNode[part];
                    }
                }
            }

            builder.AppendLine("./");
            PrintNode(root, "", true, builder);
        }

        private static void PrintNode(Dictionary<string, object> node, string indent, bool isRoot, StringBuilder builder)
        {
            var sortedKeys = node.Keys
                                .OrderBy(k => node[k] is null)
                                .ThenBy(k => k, StringComparer.OrdinalIgnoreCase)
                                .ToList();

            for (int i = 0; i < sortedKeys.Count; i++)
            {
                var key = sortedKeys[i];
                var value = node[key];
                bool isLastChild = (i == sortedKeys.Count - 1);

                builder.Append(indent);
                builder.Append(isLastChild ? "└── " : "├── ");
                builder.AppendLine(key);

                if (value is Dictionary<string, object> subDir)
                {
                    PrintNode(subDir, indent + (isLastChild ? "    " : "│   "), false, builder);
                }
            }
        }
    }
}