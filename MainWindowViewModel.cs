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

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            Application.Current?.Dispatcher?.Invoke(CommandManager.InvalidateRequerySuggested);
        }

        // Backing fields
        private ObservableCollection<TreeNodeViewModel> _rootNodes = new ObservableCollection<TreeNodeViewModel>();
        private string _outputText = string.Empty;
        private string? _selectedFolderPath;
        private string? _outputFilePath;
        private bool _isBusy = false;
        private bool _isOutputToConsole = true; // Changed default value to true
        private string? _statusText;
        private bool _useWebPreset = true;
        private string _userPrePrompt = string.Empty;
        private readonly HashSet<string> _detectedFileExtensions = new(StringComparer.OrdinalIgnoreCase);

        // Properties for data binding
        public ObservableCollection<TreeNodeViewModel> RootNodes
        {
            get => _rootNodes;
            set { _rootNodes = value; OnPropertyChanged(); }
        }

        public ObservableCollection<FileTypeFilterViewModel> FileTypeFilters { get; } = new();

        public string UserPrePrompt
        {
            get => _userPrePrompt;
            set
            {
                if (_userPrePrompt != value)
                {
                    _userPrePrompt = value;
                    OnPropertyChanged();

                    // Debounce the output regeneration to avoid lag when typing
                    DebounceUserPrePromptChange();
                }
            }
        }

        // Debouncing method for pre-prompt changes
        private void DebounceUserPrePromptChange()
        {
            if (!string.IsNullOrEmpty(SelectedFolderPath) && RootNodes.Count > 0 && !IsBusy)
            {
                // Cancel any previous pending operation
                _debounceTokenSource?.Cancel();
                _debounceTokenSource = new CancellationTokenSource();
                var token = _debounceTokenSource.Token;

                // Start a timer that will regenerate output after delay
                Task.Delay(_debounceDelay, token).ContinueWith(t =>
                {
                    if (!t.IsCanceled)
                    {
                        Application.Current?.Dispatcher?.InvokeAsync(async () =>
                        {
                            if (!IsBusy)
                            {
                                await RegenerateOutputAsync();
                            }
                        });
                    }
                }, TaskContinuationOptions.OnlyOnRanToCompletion);
            }
        }

        public string OutputText
        {
            get => _outputText;
            set { _outputText = value; OnPropertyChanged(); }
        }

        public bool IsBusy
        {
            get => _isBusy;
            set { _isBusy = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsUiEnabled)); }
        }

        public bool IsUiEnabled => !IsBusy;

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
        }

        // Commands
        public ICommand SelectFolderCommand { get; }
        public ICommand SelectOutputFileCommand { get; }
        public ICommand CopyToClipboardCommand { get; }
        public ICommand SaveToFileCommand { get; }

        // Constructor
        public MainWindowViewModel()
        {
            SelectFolderCommand = new RelayCommand(async _ => await SelectFolderAndProcessAsync(), _ => !IsBusy);
            SelectOutputFileCommand = new RelayCommand(_ => SelectOutputFile(), _ => !IsBusy && !IsOutputToConsole);
            CopyToClipboardCommand = new RelayCommand(CopyOutputToClipboard, _ => !IsBusy && !string.IsNullOrEmpty(OutputText));
            SaveToFileCommand = new RelayCommand(async _ => await SaveToFileAsync(), _ => !IsBusy && !IsOutputToConsole && !string.IsNullOrEmpty(OutputText));

            // Initialize with default exclusions
            UpdateExclusionsBasedOnPresets(); // Initialize exclusions
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

        private async Task LoadFolderAsync()
        {
            if (string.IsNullOrEmpty(_selectedFolderPath) || !Directory.Exists(_selectedFolderPath))
            {
                OutputText = "Selected folder is invalid.";
                StatusText = "Error: Invalid folder";
                return;
            }

            IsBusy = true;
            StatusText = "Loading folder structure...";
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
                var rootDirInfo = new DirectoryInfo(_selectedFolderPath);
                var rootNode = new TreeNodeViewModel(rootDirInfo.Name, rootDirInfo.FullName, true);

                // Populate TreeView in background
                await Task.Run(() => BuildTree(rootDirInfo, rootNode, _detectedFileExtensions));

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
                    rootNode.SetIsChecked(true, true, false);
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
            }
        }

        // Tree building and file handling
        private void BuildTree(DirectoryInfo currentDirInfo, TreeNodeViewModel parentNode, HashSet<string> detectedExtensions)
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

                    // Check exclusion for initial state setting
                    bool initiallyExcluded = IsPathInitiallyExcluded(dirInfo.Name, dirInfo.FullName, true);
                    var dirNode = new TreeNodeViewModel(dirInfo.Name, dirInfo.FullName, true, parentNode);
                    dirNode.SetIsChecked(!initiallyExcluded, false, false); // Set initial state without propagation yet

                    // Add to collection for later sorted addition
                    childrenToAdd.Add(dirNode);

                    BuildTree(dirInfo, dirNode, detectedExtensions); // Recurse
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
                List<TreeNodeViewModel> nodesToProcess = new List<TreeNodeViewModel>();

                // Use InvokeAsync to avoid blocking the UI thread
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    foreach (var rootNode in RootNodes) nodesToProcess.Add(rootNode);
                }, System.Windows.Threading.DispatcherPriority.Background);

                await Task.Run(async () =>
                {
                    var stack = new Stack<TreeNodeViewModel>(nodesToProcess);
                    while (stack.Count > 0)
                    {
                        var node = stack.Pop();
                        string nodeName = node.Name;
                        bool nodeIsDirectory = false;
                        ObservableCollection<TreeNodeViewModel> nodeChildren = null;

                        // Access node properties on a single dispatcher call to reduce UI thread contention
                        await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            nodeIsDirectory = node.IsDirectory;
                            nodeChildren = node.Children;
                        }, System.Windows.Threading.DispatcherPriority.Background);

                        if (!nodeIsDirectory)
                        {
                            string fileExt = Path.GetExtension(nodeName).TrimStart('.').ToLowerInvariant();
                            bool shouldUpdate = (string.IsNullOrEmpty(changedFilter.Extension) && string.IsNullOrEmpty(fileExt)) ||
                                              (string.Equals(fileExt, changedFilter.Extension, StringComparison.OrdinalIgnoreCase));

                            if (shouldUpdate)
                            {
                                // Use BeginInvoke to avoid waiting for UI thread
                                Application.Current.Dispatcher.BeginInvoke(() =>
                                {
                                    node.SetIsChecked(changedFilter.IsSelected, false, true);
                                }, System.Windows.Threading.DispatcherPriority.Background);
                            }
                        }
                        else if (nodeChildren != null)
                        {
                            var childrenToPush = new List<TreeNodeViewModel>();

                            // Access child nodes on UI thread
                            await Application.Current.Dispatcher.InvokeAsync(() =>
                            {
                                childrenToPush.AddRange(nodeChildren.Reverse());
                            }, System.Windows.Threading.DispatcherPriority.Background);

                            foreach (var child in childrenToPush)
                                stack.Push(child);
                        }
                    }
                });

                // Update UI on background priority
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    StatusText = "Selection updated.";
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    StatusText = $"Error updating for {changedFilter.DisplayName}: {ex.Message}";
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
            finally
            {
                // Make sure to reset IsBusy on the UI thread
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    IsBusy = false;
                }, System.Windows.Threading.DispatcherPriority.Background);

                // Regenerate output after we've updated the file selections
                await RegenerateOutputAsync();
            }
        }

        // Output generation
        public async Task RegenerateOutputAsync()
        {
            if (IsBusy && StatusText != "Loading folder structure...") return;
            IsBusy = true;
            StatusText = "Generating output...";

            try
            {
                await Task.Run(async () =>
                {
                    if (string.IsNullOrEmpty(_selectedFolderPath)) return;

                    var filesToProcessMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var treeTraversalStack = new Stack<TreeNodeViewModel>();

                    // Safely gather nodes from UI thread
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        foreach (var rootNode in RootNodes.Reverse())
                            treeTraversalStack.Push(rootNode);
                    }, System.Windows.Threading.DispatcherPriority.Background);

                    while (treeTraversalStack.Count > 0)
                    {
                        var node = treeTraversalStack.Pop();
                        bool? nodeIsChecked = false;
                        bool nodeIsDirectory = false;
                        string nodeFullPath = string.Empty;
                        List<TreeNodeViewModel> childrenSnapshot = new List<TreeNodeViewModel>();

                        // Use InvokeAsync with Background priority to prevent UI freezing
                        await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            nodeIsChecked = node.IsChecked;
                            nodeIsDirectory = node.IsDirectory;
                            nodeFullPath = node.FullPath;
                            if (node.IsDirectory) childrenSnapshot.AddRange(node.Children.Reverse());
                        }, System.Windows.Threading.DispatcherPriority.Background);

                        if (nodeIsChecked == true)
                        {
                            if (!nodeIsDirectory)
                            {
                                string relativePath = Path.GetRelativePath(_selectedFolderPath, nodeFullPath).Replace(Path.DirectorySeparatorChar, '/');
                                if (!filesToProcessMap.ContainsKey(relativePath))
                                {
                                    filesToProcessMap.Add(relativePath, nodeFullPath);
                                }
                            }
                            else
                            {
                                foreach (var child in childrenSnapshot) treeTraversalStack.Push(child);
                            }
                        }
                        else if (nodeIsChecked == null && nodeIsDirectory)
                        {
                            foreach (var child in childrenSnapshot) treeTraversalStack.Push(child);
                        }
                    }

                    var sortedRelativePaths = filesToProcessMap.Keys.ToList();
                    sortedRelativePaths.Sort(StringComparer.OrdinalIgnoreCase);

                    var outputBuilder = new StringBuilder();

                    string localUserPrePrompt = string.Empty;
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                        localUserPrePrompt = UserPrePrompt,
                        System.Windows.Threading.DispatcherPriority.Background);

                    if (!string.IsNullOrWhiteSpace(localUserPrePrompt))
                    {
                        outputBuilder.AppendLine(localUserPrePrompt.Trim());
                        outputBuilder.AppendLine();
                    }

                    if (!sortedRelativePaths.Any())
                    {
                        outputBuilder.AppendLine("No files selected or found based on current selection.");
                    }
                    else
                    {
                        outputBuilder.AppendLine("Directory Structure:");
                        outputBuilder.AppendLine();
                        BuildTreeStringInternal(sortedRelativePaths, outputBuilder);
                        outputBuilder.AppendLine();

                        outputBuilder.AppendLine("--- File Contents ---");
                        outputBuilder.AppendLine();

                        var fileContents = new ConcurrentDictionary<string, string>();
                        var fileReadErrors = new ConcurrentDictionary<string, string>();

                        // Use throttling to avoid too many parallel file reads which might overwhelm the system
                        var throttledTasks = new List<Task>();
                        var throttleSemaphore = new SemaphoreSlim(Math.Min(Environment.ProcessorCount * 2, 16)); // Limit concurrent reads

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
                            outputBuilder.AppendLine($"\n---\nFile: /{relativePath}\n---");
                            if (fileReadErrors.TryGetValue(relativePath, out var error))
                            {
                                outputBuilder.AppendLine(error);
                            }
                            else if (fileContents.TryGetValue(relativePath, out var content))
                            {
                                outputBuilder.AppendLine(content);
                            }
                        }
                    }

                    // Use BeginInvoke with Background priority to update UI without blocking
                    Application.Current.Dispatcher.BeginInvoke(() =>
                        OutputText = outputBuilder.ToString(),
                        System.Windows.Threading.DispatcherPriority.Background);
                });
            }
            catch (Exception ex)
            {
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    OutputText = $"Error generating output: {ex.Message}";
                    StatusText = $"Error: {ex.Message}";
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
            finally
            {
                // Update UI state
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    StatusText = "Ready";
                    IsBusy = false;
                    CommandManager.InvalidateRequerySuggested();
                }, System.Windows.Threading.DispatcherPriority.Background);
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