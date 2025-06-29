using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
// Use explicit System.Windows references to avoid ambiguity
using System.Windows;
using System.Windows.Input;
using LibGit2Sharp;

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

        // Git Commands
        public ICommand GenerateDiffCommand { get; private set; }
        public ICommand CopyDiffToClipboardCommand { get; private set; }

        // Backing fields
        private ObservableCollection<TreeNodeViewModel> _rootNodes = new ObservableCollection<TreeNodeViewModel>();
        private string _outputText = string.Empty;
        private string? _selectedFolderPath;
        private string? _outputFilePath;
        private bool _isBusy = false;
        private bool _isOutputToConsole = true; // Changed default value to true
        private bool _isDarkMode = true;
        private string? _statusText;
        private bool _useWebPreset = true;
        private string _userPrePrompt = string.Empty;
        private readonly HashSet<string> _detectedFileExtensions = new(StringComparer.OrdinalIgnoreCase);
        private string _concatenatedFileContents = string.Empty; // For pre-prompt optimization
        private CancellationTokenSource? _prePromptDebounceCts;

        // LLM Token Counting related fields
        private LlmModel _selectedLlmModel = LlmModel.Gpt4o; // Default model
        private string _tokenCountDisplay = "Tokens: (not available)";

        // Git-related fields
        private bool _isGitRepository = false;
        private bool _isDiffModePending = true;
        private bool _isDiffModeBranches = false;
        private bool _isDiffModeCommits = false;
        private string _selectedBranch1 = string.Empty;
        private string _selectedBranch2 = string.Empty;
        private string _commitHash1 = string.Empty;
        private string _commitHash2 = string.Empty;
        private string _generatedDiffText = string.Empty;
        private bool _includeDiffInPreview = false;

        // CHANGE 2: Property for the window title
        public string WindowTitle { get; }

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

            // Add Git diff if enabled
            if (IncludeDiffInPreview && !string.IsNullOrEmpty(GeneratedDiffText) && IsGitRepository)
            {
                sb.AppendLine();
                sb.AppendLine("---".PadRight(80, '-'));
                sb.AppendLine("--- GIT DIFF ---");
                sb.AppendLine("---".PadRight(80, '-'));
                sb.AppendLine();
                sb.AppendLine(GeneratedDiffText);
            }

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
                    CheckForGitRepository(); // Trigger git check on path change
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
            // CHANGE 2: Construct the window title using assembly version
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            WindowTitle = $"Repository To Text v{version?.Major}.{version?.Minor}.{version?.Build}";

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
            RefreshCommand = new RelayCommand(async _ => await RefreshFolderAsync(), _ => !IsBusy && !string.IsNullOrEmpty(SelectedFolderPath));

            // Git commands
            GenerateDiffCommand = new RelayCommand(_ => ExecuteGenerateDiff(), _ => CanExecuteGenerateDiff());
            CopyDiffToClipboardCommand = new RelayCommand(_ => ExecuteCopyDiffToClipboard(), _ => !string.IsNullOrEmpty(GeneratedDiffText)); UpdateExclusionsBasedOnPresets();

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

            // CHANGE 1 (FIX): Step 1 - Save the state of all currently checked files
            var checkedFilePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            GetCheckedFilePaths(RootNodes, checkedFilePaths);

            await LoadFolderAsync(checkedFilePaths);

            // The LoadFolderAsync method already calls GenerateOutputAsync() at the end,
            // so we don't need to call it again here to avoid double generation.
        }

        // CHANGE 1 (FIX): New helper method to get checked file paths (not directories)
        private void GetCheckedFilePaths(ObservableCollection<TreeNodeViewModel> nodes, HashSet<string> checkedPaths)
        {
            foreach (var node in nodes)
            {
                if (node.IsChecked == true && !node.IsDirectory)
                {
                    checkedPaths.Add(node.FullPath);
                }
                if (node.Children.Any())
                {
                    GetCheckedFilePaths(node.Children, checkedPaths);
                }
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

                    // Always expand the root node and its first level children
                    rootNode.IsExpanded = true;

                    // Expand first level children
                    foreach (var child in rootNode.Children)
                    {
                        if (child.IsDirectory)
                        {
                            child.IsExpanded = true;
                        }
                    }

                    // Always expand the root node and its first level
                    rootNode.IsExpanded = true;

                    // Expand first level children
                    foreach (var child in rootNode.Children)
                    {
                        if (child.IsDirectory)
                        {
                            child.IsExpanded = true;
                        }
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

                    var fileNode = new TreeNodeViewModel(fileInfo.Name, fileInfo.FullName, false, parentNode);

                    // CHANGE 1 (FIX): Only set initial state based on exclusions if this is NOT a refresh
                    if (selectionToRestore == null)
                    {
                        // For initial load, set state based on exclusions
                        bool initiallyExcluded = IsPathInitiallyExcluded(fileInfo.Name, fileInfo.FullName, false);
                        fileNode.SetIsChecked(!initiallyExcluded, false, false); // Set initial state without propagation
                    }
                    else
                    {
                        // For refresh, start with unchecked and let TraverseAndApplyRestoredSelection handle it
                        fileNode.SetIsChecked(false, false, false);
                    }

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
        /// CHANGE 1 (FIX): Updated method to restore checked state for files only
        /// </summary>
        /// <param name="node">The current node being processed</param>
        /// <param name="checkedFilePaths">Set of file paths to restore as checked</param>
        /// <returns>True if any child node was selected, false otherwise</returns>
        private bool TraverseAndApplyRestoredSelection(TreeNodeViewModel node, HashSet<string> checkedFilePaths)
        {
            bool anyChildSelected = false;

            // Process children first (bottom-up approach)
            foreach (var child in node.Children)
            {
                bool isChildSelected = TraverseAndApplyRestoredSelection(child, checkedFilePaths);
                if (isChildSelected)
                {
                    anyChildSelected = true;
                }
            }

            // For files, restore their checked state if they were previously checked
            if (!node.IsDirectory)
            {
                bool shouldCheck = checkedFilePaths.Contains(node.FullPath);
                node.SetIsChecked(shouldCheck, false, false);
                return shouldCheck;
            }

            // For directories, set checked state based on children
            if (anyChildSelected)
            {
                // Check if all children are checked to determine if parent should be fully checked or indeterminate
                bool allChildrenChecked = node.Children.All(c => c.IsChecked == true);
                node.SetIsChecked(allChildrenChecked ? true : null, false, false); // null means indeterminate
                return true;
            }
            else
            {
                node.SetIsChecked(false, false, false);
                return false;
            }
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
                            List<TreeNodeViewModel>? childrenSnapshot = null;
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

        // Git-related properties
        public bool IsGitRepository
        {
            get => _isGitRepository;
            set
            {
                if (_isGitRepository != value)
                {
                    _isGitRepository = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsDiffModePending
        {
            get => _isDiffModePending;
            set
            {
                if (_isDiffModePending != value)
                {
                    _isDiffModePending = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsDiffModeBranches
        {
            get => _isDiffModeBranches;
            set
            {
                if (_isDiffModeBranches != value)
                {
                    _isDiffModeBranches = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsDiffModeCommits
        {
            get => _isDiffModeCommits;
            set
            {
                if (_isDiffModeCommits != value)
                {
                    _isDiffModeCommits = value;
                    OnPropertyChanged();
                }
            }
        }

        public ObservableCollection<string> GitBranches { get; } = new ObservableCollection<string>();

        public string SelectedBranch1
        {
            get => _selectedBranch1;
            set
            {
                if (_selectedBranch1 != value)
                {
                    _selectedBranch1 = value;
                    OnPropertyChanged();
                }
            }
        }

        public string SelectedBranch2
        {
            get => _selectedBranch2;
            set
            {
                if (_selectedBranch2 != value)
                {
                    _selectedBranch2 = value;
                    OnPropertyChanged();
                }
            }
        }

        public string CommitHash1
        {
            get => _commitHash1;
            set
            {
                if (_commitHash1 != value)
                {
                    _commitHash1 = value;
                    OnPropertyChanged();
                }
            }
        }

        public string CommitHash2
        {
            get => _commitHash2;
            set
            {
                if (_commitHash2 != value)
                {
                    _commitHash2 = value;
                    OnPropertyChanged();
                }
            }
        }

        public string GeneratedDiffText
        {
            get => _generatedDiffText;
            set
            {
                if (_generatedDiffText != value)
                {
                    _generatedDiffText = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IncludeDiffInPreview
        {
            get => _includeDiffInPreview;
            set
            {
                if (_includeDiffInPreview != value)
                {
                    _includeDiffInPreview = value;
                    OnPropertyChanged();
                    if (IsGitRepository)
                    {
                        UpdatePreview();
                    }
                }
            }
        }

        private void UpdatePreview()
        {
            if (IsBusy) return; // Don't update if busy

            var sb = new StringBuilder();

            // Add user pre-prompt if exists
            if (!string.IsNullOrWhiteSpace(UserPrePrompt))
            {
                sb.AppendLine(UserPrePrompt.Trim());
                sb.AppendLine();
            }

            // Add concatenated file contents
            sb.Append(_concatenatedFileContents);

            // Add Git diff if enabled
            if (IncludeDiffInPreview && !string.IsNullOrEmpty(GeneratedDiffText) && IsGitRepository)
            {
                sb.AppendLine();
                sb.AppendLine("---".PadRight(80, '-'));
                sb.AppendLine("--- GIT DIFF ---");
                sb.AppendLine("---".PadRight(80, '-'));
                sb.AppendLine();
                sb.AppendLine(GeneratedDiffText);
            }

            OutputText = sb.ToString();
        }

        // Git-related methods
        private void CheckForGitRepository()
        {
            if (string.IsNullOrEmpty(SelectedFolderPath) || !Directory.Exists(SelectedFolderPath))
            {
                IsGitRepository = false;
                return;
            }

            // Repository.IsValid is a simple check for a .git directory
            IsGitRepository = Repository.IsValid(SelectedFolderPath);

            if (IsGitRepository)
            {
                LoadGitBranches();
            }
            else
            {
                GitBranches.Clear();
                GeneratedDiffText = string.Empty;
            }
        }

        private void LoadGitBranches()
        {
            GitBranches.Clear();
            try
            {
                using (var repo = new Repository(SelectedFolderPath))
                {
                    foreach (var branch in repo.Branches.Where(b => !b.IsRemote))
                    {
                        GitBranches.Add(branch.FriendlyName);
                    }
                }
            }
            catch (Exception ex)
            {
                // Handle exceptions, e.g., show a message to the user
                GeneratedDiffText = $"Error loading Git branches: {ex.Message}";
            }
        }

        private bool CanExecuteGenerateDiff()
        {
            if (!IsGitRepository) return false;

            if (IsDiffModeBranches)
            {
                return !string.IsNullOrEmpty(SelectedBranch1) && !string.IsNullOrEmpty(SelectedBranch2);
            }
            if (IsDiffModeCommits)
            {
                return !string.IsNullOrEmpty(CommitHash1) && !string.IsNullOrEmpty(CommitHash2);
            }
            // IsDiffModePending is always executable if it's a git repo
            return true;
        }

        private void ExecuteGenerateDiff()
        {
            GeneratedDiffText = "Generating diff...";
            try
            {
                using (var repo = new Repository(SelectedFolderPath))
                {
                    Patch? patch = null;

                    if (IsDiffModePending)
                    {
                        // Get the actual diff content for pending changes (working directory vs HEAD)
                        patch = repo.Diff.Compare<Patch>(repo.Head.Tip.Tree, DiffTargets.Index | DiffTargets.WorkingDirectory);
                    }
                    else if (IsDiffModeBranches)
                    {
                        var branch1 = repo.Branches[SelectedBranch1];
                        var branch2 = repo.Branches[SelectedBranch2];
                        if (branch1 != null && branch2 != null)
                        {
                            patch = repo.Diff.Compare<Patch>(branch1.Tip.Tree, branch2.Tip.Tree);
                        }
                    }
                    else if (IsDiffModeCommits)
                    {
                        var commit1 = repo.Lookup<Commit>(CommitHash1);
                        var commit2 = repo.Lookup<Commit>(CommitHash2);
                        if (commit1 != null && commit2 != null)
                        {
                            patch = repo.Diff.Compare<Patch>(commit1.Tree, commit2.Tree);
                        }
                        else
                        {
                            GeneratedDiffText = "Error: One or both commit hashes are invalid.";
                            return;
                        }
                    }

                    if (patch != null)
                    {
                        if (string.IsNullOrEmpty(patch.Content))
                        {
                            GeneratedDiffText = "No differences found.";
                        }
                        else
                        {
                            GeneratedDiffText = patch.Content;
                        }
                    }
                    else
                    {
                        GeneratedDiffText = "Unable to generate diff.";
                    }

                    // Update preview if needed
                    if (IncludeDiffInPreview)
                    {
                        UpdatePreview();
                    }
                }
            }
            catch (Exception ex)
            {
                GeneratedDiffText = $"An error occurred while generating the diff:\n{ex.Message}";
            }
        }

        private void ExecuteCopyDiffToClipboard()
        {
            if (!string.IsNullOrEmpty(GeneratedDiffText))
            {
                Clipboard.SetText(GeneratedDiffText);
            }
        }

        private async Task GenerateDiffAsync()
        {
            if (!IsGitRepository || string.IsNullOrEmpty(SelectedBranch1) || string.IsNullOrEmpty(SelectedBranch2))
            {
                GeneratedDiffText = "Select two branches or commits to compare.";
                return;
            }

            IsBusy = true;
            StatusText = "Generating diff...";

            try
            {
                await Task.Run(() =>
                {
                    // Simulate diff generation
                    Thread.Sleep(2000);

                    // Here you would access the Git repository and generate the diff
                    // For now, let's just set some dummy diff text
                    GeneratedDiffText = $@"--- a/File1.txt
+++ b/File1.txt
@@ -1,3 +1,3 @@
-Line 1
-Line 2
-Line 3
+Line 1 (modified)
+Line 2 (modified)
+Line 3 (modified)
";
                });
            }
            catch (Exception ex)
            {
                GeneratedDiffText = $"Error generating diff: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        // Placeholder implementations for missing methods (needed for existing codebase)
        private static readonly List<string> WebExclusionPreset = new List<string>
        {
            "node_modules/", ".git/", "bin/", "obj/", ".vs/", "*.min.js", "*.min.css"
        };

        private bool MatchesWildcardInternal(string input, List<string> patterns, bool isPath)
        {
            foreach (var pattern in patterns)
            {
                if (pattern.Contains("*"))
                {
                    var regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
                    if (Regex.IsMatch(input, regexPattern, RegexOptions.IgnoreCase))
                        return true;
                }
                else if (isPath && input.Contains(pattern))
                {
                    return true;
                }
                else if (!isPath && input.Equals(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private void BuildTreeStringInternal(List<string> paths, StringBuilder builder)
        {
            // Simple implementation for tree string building
            foreach (var path in paths)
            {
                builder.AppendLine(path);
            }
        }
    }
}