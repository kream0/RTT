Okay, Agent, your task is to implement several new features and improvements into the existing WPF "RepoToText" tool. Please follow these instructions meticulously.

**Overall Goal:** Enhance the tool with file type filtering, improved UI for TreeView and pre-prompt input, rename a header, and boost performance.

---
**Project Setup & General Notes:**
*   All file paths are relative to the project root directory (where the `.csproj` file is located).
*   Ensure all new C# classes are within the `RepoToTxtGui` namespace.
*   Pay close attention to `TODO LLM:` comments for specific implementation details or choices.

---

**Step 1: Create `FileTypeFilterViewModel.cs`**

*   **Action:** Create a new C# class file.
*   **File Path:** `FileTypeFilterViewModel.cs`
*   **Content:**
    ```csharp
    using System;
    using System.ComponentModel;
    using System.Runtime.CompilerServices;

    namespace RepoToTxtGui
    {
        public class FileTypeFilterViewModel : INotifyPropertyChanged
        {
            private bool _isSelected;
            private readonly Action<FileTypeFilterViewModel> _onSelectionChanged;

            public string Extension { get; }
            public string DisplayName => string.IsNullOrEmpty(Extension) ? "[No Extension]" : $"*.{Extension}";

            public bool IsSelected
            {
                get => _isSelected;
                set
                {
                    if (_isSelected != value)
                    {
                        _isSelected = value;
                        OnPropertyChanged();
                        _onSelectionChanged?.Invoke(this);
                    }
                }
            }

            public FileTypeFilterViewModel(string extension, bool initiallySelected, Action<FileTypeFilterViewModel> onSelectionChanged)
            {
                Extension = extension.StartsWith(".") ? extension.Substring(1) : extension;
                // Normalize empty or null to string.Empty for consistent [No Extension] display
                if (string.IsNullOrWhiteSpace(Extension)) 
                {
                    Extension = string.Empty;
                }
                _isSelected = initiallySelected;
                _onSelectionChanged = onSelectionChanged ?? throw new ArgumentNullException(nameof(onSelectionChanged));
            }

            public event PropertyChangedEventHandler? PropertyChanged;
            protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
        }
    }
    ```

---

**Step 2: Create `ZeroToCollapsedConverter.cs`**

*   **Action:** Create a new C# class file.
*   **File Path:** `ZeroToCollapsedConverter.cs`
*   **Content:**
    ```csharp
    using System;
    using System.Globalization;
    using System.Windows;
    using System.Windows.Data;

    namespace RepoToTxtGui
    {
        public class ZeroToCollapsedConverter : IValueConverter
        {
            public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            {
                if (value is int count)
                {
                    // Parameter "InverseZeroToCollapsed" means: if count is 0, then Collapsed. Otherwise Visible.
                    // Default behavior (no parameter or other string): if count is 0, Visible. Otherwise Collapsed.
                    bool inverse = parameter as string == "InverseZeroToCollapsed";
                    if (inverse)
                    {
                        return count == 0 ? Visibility.Collapsed : Visibility.Visible;
                    }
                    else
                    {
                        return count == 0 ? Visibility.Visible : Visibility.Collapsed;
                    }
                }
                return Visibility.Collapsed; // Default to collapsed if value is not an int
            }

            public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            {
                throw new NotImplementedException();
            }
        }
    }
    ```

---

**Step 3: Modify `TreeNodeViewModel.cs`**

*   **File Path:** `TreeNodeViewModel.cs`
*   **Changes:**
    1.  Ensure the `System.Windows` namespace is imported if not already for `Application.Current.Dispatcher`.
        ```csharp
        using System.Windows; // Add if not present
        ```
    2.  Modify the `SetIsChecked` method to only update children if the node `IsDirectory`.
        *   **Find:** `if (updateChildren && _isChecked.HasValue)`
        *   **Replace with:** `if (updateChildren && _isChecked.HasValue && IsDirectory)`
    3.  Refine `UpdateCheckStateBasedOnChildren` for more robust logic.
        *   **Replace the entire `UpdateCheckStateBasedOnChildren` method with:**
            ```csharp
            private void UpdateCheckStateBasedOnChildren()
            {
                if (_isUpdatingCheckState) return; 
                _isUpdatingCheckState = true;

                bool? newParentState;
                if (!Children.Any())
                {
                    // If it's a directory with no children, its state is its own explicitly set state.
                    // If it's a file, this method isn't typically called to determine state from children.
                    newParentState = _isChecked; 
                }
                else
                {
                    bool anyChecked = Children.Any(c => c.IsChecked == true);
                    bool allChecked = Children.All(c => c.IsChecked == true);
                    bool anyIndeterminate = Children.Any(c => c.IsChecked == null);

                    if (anyIndeterminate || (anyChecked && !allChecked)) // If any child is indeterminate, or if some are checked but not all
                    {
                        newParentState = null; // Indeterminate
                    }
                    else if (allChecked)
                    {
                        newParentState = true; // All children are true (none false or indeterminate)
                    }
                    else // No children are checked (all are false)
                    {
                        newParentState = false; 
                    }
                }
                
                if (_isChecked != newParentState)
                {
                    _isChecked = newParentState; 
                    OnPropertyChanged(nameof(IsChecked)); 

                    if (Parent != null)
                    {
                        Parent.UpdateCheckStateBasedOnChildren();
                    }
                }
                _isUpdatingCheckState = false;
            }
            ```
    4.  Modify `OnPropertyChanged` to ensure UI updates are dispatched to the UI thread.
        *   **Replace the entire `OnPropertyChanged` method with:**
            ```csharp
            protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            {
                var handler = PropertyChanged;
                if (handler != null)
                {
                    if (Application.Current?.Dispatcher?.CheckAccess() == false)
                    {
                        Application.Current.Dispatcher.Invoke(() => handler(this, new PropertyChangedEventArgs(propertyName)));
                    }
                    else
                    {
                        handler(this, new PropertyChangedEventArgs(propertyName));
                    }
                }
            }
            ```

---

**Step 4: Modify `MainWindowViewModel.cs`**

*   **File Path:** `MainWindowViewModel.cs`
*   **Changes:**
    1.  Add necessary `using` statements at the top:
        ```csharp
        using System.Collections.Concurrent; // For ConcurrentDictionary
        // Microsoft.Win32 is usually available via System.Windows.Forms or implicit usings if project targets Windows.
        // If not, ensure it's available for OpenFolderDialog and SaveFileDialog.
        ```
    2.  Add new private fields and public properties:
        ```csharp
        // Add near other backing fields
        private string _userPrePrompt = string.Empty;
        private readonly HashSet<string> _detectedFileExtensions = new(StringComparer.OrdinalIgnoreCase);

        // Add near other public properties
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
                    // Optional: Consider debounced regeneration if UserPrePrompt change should immediately reflect
                    // For now, it will be included in the next regeneration triggered by other actions.
                    // _ = RegenerateOutputAsync(); // Example of immediate regeneration
                }
            }
        }
        ```
    3.  Modify the `IsOutputToConsole` property:
        *   **Change default value:** `private bool _isOutputToConsole = true;` (was `false`)
    4.  Modify `SelectedFolderPath` property:
        *   **Change setter to private:**
            ```csharp
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
            ```
    5.  Modify the `UpdateExclusionsBasedOnPresets` method and add `UpdateExclusionsAndRefreshTree`:
        *   **Rename existing `UpdateExclusionsBasedOnPresets` to `UpdateExclusionsAndRefreshTree`.**
        *   **Modify `UpdateExclusionsAndRefreshTree` to be:**
            ```csharp
            private void UpdateExclusionsAndRefreshTree()
            {
                UpdateExclusionsBasedOnPresets(); // Call the new method
                if (!string.IsNullOrEmpty(SelectedFolderPath) && Directory.Exists(SelectedFolderPath))
                {
                    _ = LoadFolderAsync(); 
                }
            }
            ```
        *   **Create a new `UpdateExclusionsBasedOnPresets` method:**
            ```csharp
            private void UpdateExclusionsBasedOnPresets()
            {
                EffectiveExclusions = UseWebPreset
                    ? new List<string>(WebExclusionPreset)
                    : new List<string>();
                PrepareExclusionMatchers(); 
            }
            ```
        *   In the constructor `MainWindowViewModel()`, **replace** the call `UpdateExclusionsBasedOnPresets();` with `UpdateExclusionsBasedOnPresets(); // Initialize exclusions`. (No change, just confirm it's calling the new smaller method).
        *   In the `UseWebPreset` property setter, **change** the line `UpdateExclusionsBasedOnPresets();` to `UpdateExclusionsAndRefreshTree();`.
    6.  Modify `PrepareExclusionMatchers` to normalize separators:
        *   Inside the `foreach` loop, add: `string cleanedExclusion = exclusion.Replace('\\', '/');`
        *   Replace all uses of `exclusion` within the loop with `cleanedExclusion`.
            ```csharp
            // Example:
            // ExcludedWildcards.Add(exclusion); becomes:
            // ExcludedWildcards.Add(cleanedExclusion);
            // if (!exclusion.Contains('*') ... becomes:
            // if (!cleanedExclusion.Contains('*') ...
            // ExcludedDirNames.Add(exclusion); becomes:
            // ExcludedDirNames.Add(cleanedExclusion);
            // ExcludedFileNames.Add(exclusion); becomes:
            // ExcludedFileNames.Add(cleanedExclusion);
            ```
    7.  Modify `SelectFolderAndProcessAsync` to use `Microsoft.Win32.OpenFolderDialog`:
        *   **Replace the method content with:**
            ```csharp
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
            ```
    8.  Modify `SelectOutputFile` to use `Microsoft.Win32.SaveFileDialog`:
        *   **Replace the method content with:**
            ```csharp
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
            ```
    9.  Modify `LoadFolderAsync`:
        *   **At the beginning of the method, add clearing for new collections and status updates:**
            ```csharp
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
            ```
        *   **Modify the `Task.Run` call for `BuildTree` to pass `_detectedFileExtensions`:**
            *   Change: `await Task.Run(() => BuildTree(rootDirInfo, rootNode));`
            *   To: `await Task.Run(() => BuildTree(rootDirInfo, rootNode, _detectedFileExtensions));`
        *   **After `Task.Run(() => BuildTree(...))`, add logic to populate `FileTypeFilters`:**
            ```csharp
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
            ```
        *   **In the `catch` block, ensure UI collections are also cleared on error:**
            ```csharp
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
            ```
        *   Ensure `IsBusy = false;` is in a `finally` block. (Already present in original).
    10. Modify `BuildTree` method signature and logic:
        *   **Change signature to:** `private void BuildTree(DirectoryInfo currentDirInfo, TreeNodeViewModel parentNode, HashSet<string> detectedExtensions)`
        *   **Inside the `foreach (var dirInfo ...)` loop:**
            *   Change `IsDirectoryExcluded(dirInfo.Name, dirInfo.FullName)` to `IsDirectoryHardExcluded(dirInfo.Name)`
            *   Change `IsInitiallyExcluded(dirInfo.Name, dirInfo.FullName, true)` to `IsPathInitiallyExcluded(dirInfo.Name, dirInfo.FullName, true)`
        *   **Inside the `foreach (var fileInfo ...)` loop:**
            *   Add code to collect extensions:
                ```csharp
                string fileExtension = Path.GetExtension(fileInfo.Name).TrimStart('.').ToLowerInvariant();
                if (string.IsNullOrEmpty(fileExtension)) detectedExtensions.Add(string.Empty); // For files with no extension
                else detectedExtensions.Add(fileExtension);
                ```
            *   Change `IsInitiallyExcluded(fileInfo.Name, fileInfo.FullName, false)` to `IsPathInitiallyExcluded(fileInfo.Name, fileInfo.FullName, false)`
        *   **At the end of `BuildTree` (before closing `}`), replace the existing UI update logic for adding children with a batched and sorted version:**
            ```csharp
            if (childrenToAdd.Any())
            {
                // Sort children: directories first, then alphabetically
                var sortedChildren = childrenToAdd
                    .OrderByDescending(n => n.IsDirectory) // Directories first
                    .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                Application.Current.Dispatcher.Invoke(() =>
                {
                    foreach (var childNode in sortedChildren)
                    {
                        parentNode.Children.Add(childNode);
                    }
                });
            }
            ```
    11. Rename `IsDirectoryExcluded` to `IsDirectoryHardExcluded` and update its logic.
        *   **Rename method:** `private bool IsDirectoryExcluded(string name, string fullPath)` to `private bool IsDirectoryHardExcluded(string name)`
        *   **Replace its content with:**
            ```csharp
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
            ```
    12. Rename `IsInitiallyExcluded` to `IsPathInitiallyExcluded` and update its logic.
        *   **Rename method:** `private bool IsInitiallyExcluded(string name, string fullPath, bool isDirectory)` to `private bool IsPathInitiallyExcluded(string name, string fullPath, bool isDirectory)`
        *   **Replace its content with:**
            ```csharp
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
            ```
    13. Add the `HandleFileTypeFilterChanged` method:
        ```csharp
        private async void HandleFileTypeFilterChanged(FileTypeFilterViewModel changedFilter)
        {
            if (IsBusy || RootNodes.Count == 0 || Application.Current == null) return;

            IsBusy = true;
            StatusText = $"Updating selection for {changedFilter.DisplayName}...";
            try
            {
                // Use a copy of RootNodes for safe iteration if modifications could occur,
                // but here we are modifying node properties, not the collection structure itself.
                List<TreeNodeViewModel> nodesToProcess = new List<TreeNodeViewModel>();
                Application.Current.Dispatcher.Invoke(() => { // Access RootNodes on UI thread to build processing list
                    foreach(var rootNode in RootNodes) nodesToProcess.Add(rootNode);
                });

                await Task.Run(() => // Perform tree traversal on background thread
                {
                    var stack = new Stack<TreeNodeViewModel>(nodesToProcess);
                    while (stack.Count > 0)
                    {
                        var node = stack.Pop();
                        // Read node properties that might be accessed from UI thread.
                        // Here, Name and Children are set during build, IsDirectory is const.
                        string nodeName = node.Name; 
                        bool nodeIsDirectory = node.IsDirectory;
                        ObservableCollection<TreeNodeViewModel> nodeChildren = node.Children; // If Children could change, copy it too

                        if (!nodeIsDirectory)
                        {
                            string fileExt = Path.GetExtension(nodeName).TrimStart('.').ToLowerInvariant();
                            if (string.IsNullOrEmpty(changedFilter.Extension) && string.IsNullOrEmpty(fileExt)) // Match "[No Extension]"
                            {
                                Application.Current.Dispatcher.Invoke(() =>
                                {
                                    node.SetIsChecked(changedFilter.IsSelected, false, true);
                                });
                            }
                            else if (string.Equals(fileExt, changedFilter.Extension, StringComparison.OrdinalIgnoreCase))
                            {
                                Application.Current.Dispatcher.Invoke(() =>
                                {
                                    node.SetIsChecked(changedFilter.IsSelected, false, true);
                                });
                            }
                        }
                        else
                        {
                            // Iterate over a copy of children if the Children collection could be modified by another thread
                            // For now, assuming Children structure is stable during this operation.
                            var childrenToPush = nodeChildren.Reverse().ToList(); // ToList creates a copy
                            foreach (var child in childrenToPush) stack.Push(child);
                        }
                    }
                });
                StatusText = "Selection updated.";
            }
            catch (Exception ex)
            {
                StatusText = $"Error updating for {changedFilter.DisplayName}: {ex.Message}";
            }
            finally
            {
                IsBusy = false; 
                await RegenerateOutputAsync();
            }
        }
        ```
    14. Modify `RegenerateOutputAsync`:
        *   **At the start of the method:**
            *   Change: `if (IsBusy) return;` to `if (IsBusy && StatusText != "Loading folder structure...") return;`
            *   Wrap the entire method body (after `IsBusy = true; StatusText = "Generating output...";`) in `await Task.Run(async () => { ... });`
        *   **Inside the new `Task.Run` block:**
            *   Replace `List<string> filesToProcessRelative` and `List<string> filesToProcessFull` with:
                ```csharp
                var filesToProcessMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // RelativePath -> FullPath
                ```
            *   **Tree traversal logic to collect selected files (replace existing traversal):**
                ```csharp
                var treeTraversalStack = new Stack<TreeNodeViewModel>();
                Application.Current.Dispatcher.Invoke(() => { 
                    foreach(var rootNode in RootNodes.Reverse()) treeTraversalStack.Push(rootNode);
                });

                while (treeTraversalStack.Count > 0)
                {
                    var node = treeTraversalStack.Pop();
                    bool? nodeIsChecked = false; // Read on UI thread
                    bool nodeIsDirectory = false; // Read on UI thread
                    string nodeFullPath = string.Empty; // Read on UI thread
                    List<TreeNodeViewModel> childrenSnapshot = new List<TreeNodeViewModel>(); // Read on UI thread

                    Application.Current.Dispatcher.Invoke(() => {
                        nodeIsChecked = node.IsChecked;
                        nodeIsDirectory = node.IsDirectory;
                        nodeFullPath = node.FullPath;
                        if (node.IsDirectory) childrenSnapshot.AddRange(node.Children.Reverse()); // Reverse for stack processing
                    });

                    if (nodeIsChecked == true) 
                    {
                        if (!nodeIsDirectory)
                        {
                            string relativePath = Path.GetRelativePath(_selectedFolderPath, nodeFullPath).Replace(Path.DirectorySeparatorChar, '/');
                            if (!filesToProcessMap.ContainsKey(relativePath)) // Avoid duplicates if structure allows
                            {
                                filesToProcessMap.Add(relativePath, nodeFullPath);
                            }
                        }
                        else 
                        {
                            foreach (var child in childrenSnapshot) treeTraversalStack.Push(child);
                        }
                    }
                    else if (nodeIsChecked == null && nodeIsDirectory) // Indeterminate directory, process children
                    {
                        foreach (var child in childrenSnapshot) treeTraversalStack.Push(child);
                    }
                }
                var sortedRelativePaths = filesToProcessMap.Keys.ToList();
                sortedRelativePaths.Sort(StringComparer.OrdinalIgnoreCase);
                ```
            *   **Modify header generation:**
                *   Remove line: `outputBuilder.AppendLine($"Selected files from: {_selectedFolderPath}");`
                *   Change line: `outputBuilder.AppendLine("Directory Structure (Selected Files):");` to `outputBuilder.AppendLine("Directory Structure:");`
            *   **Add UserPrePrompt handling at the beginning of `outputBuilder` usage:**
                ```csharp
                string localUserPrePrompt = string.Empty;
                Application.Current.Dispatcher.Invoke(() => localUserPrePrompt = UserPrePrompt);

                if (!string.IsNullOrWhiteSpace(localUserPrePrompt))
                {
                    outputBuilder.AppendLine(localUserPrePrompt.Trim()); // Trim to remove accidental newlines
                    outputBuilder.AppendLine(); // Add a blank line after pre-prompt
                }
                ```
            *   **Replace file content reading loop with concurrent reading and error/size handling:**
                ```csharp
                if (sortedRelativePaths.Any())
                {
                    var fileContents = new ConcurrentDictionary<string, string>(); // path -> content
                    var fileReadErrors = new ConcurrentDictionary<string, string>(); // path -> error message
                    var readTasks = new List<Task>();

                    foreach (var relativePath in sortedRelativePaths)
                    {
                        string fullPath = filesToProcessMap[relativePath];
                        readTasks.Add(Task.Run(async () =>
                        {
                            try
                            {
                                var fileInfo = new FileInfo(fullPath);
                                if (fileInfo.Length > 5 * 1024 * 1024) // 5MB limit
                                {
                                     fileReadErrors[relativePath] = $"Error: File '{Path.GetFileName(fullPath)}' is too large ({(double)fileInfo.Length / (1024*1024):F2}MB). Max 5MB.";
                                     return;
                                }
                                var content = await File.ReadAllTextAsync(fullPath);
                                fileContents[relativePath] = content;
                            }
                            catch (IOException ex) // More specific for file I/O
                            {
                                fileReadErrors[relativePath] = $"Error reading file '{Path.GetFileName(fullPath)}': {ex.Message}";
                            }
                            catch (Exception ex) // General errors
                            {
                                fileReadErrors[relativePath] = $"Error processing file '{Path.GetFileName(fullPath)}': {ex.Message}";
                            }
                        }));
                    }
                    await Task.WhenAll(readTasks);

                    foreach (var relativePath in sortedRelativePaths) // Iterate in sorted order for output
                    {
                        outputBuilder.AppendLine($"\n---\nFile: /{relativePath}\n---"); // Keep leading newline for separation
                        if (fileReadErrors.TryGetValue(relativePath, out var error))
                        {
                            outputBuilder.AppendLine(error);
                        }
                        else if (fileContents.TryGetValue(relativePath, out var content))
                        {
                            outputBuilder.AppendLine(content);
                        }
                        // If neither, it means it was skipped (e.g. too large without explicit error added, though current logic adds error)
                    }
                }
                ```
            *   **Move `OutputText = outputBuilder.ToString();` inside `Application.Current.Dispatcher.Invoke(...)`:**
                ```csharp
                Application.Current.Dispatcher.Invoke(() => OutputText = outputBuilder.ToString());
                ```
            *   Ensure `StatusText = "Ready"; IsBusy = false;` are outside the `Task.Run` block but still within `RegenerateOutputAsync`.
    15. Modify `OnPropertyChanged` to dispatch `CommandManager.InvalidateRequerySuggested()`:
        *   **Replace:** `System.Windows.Application.Current.Dispatcher?.Invoke(() => CommandManager.InvalidateRequerySuggested());`
        *   **With:** (Already done this way in your provided original code, just confirm)
            ```csharp
            Application.Current?.Dispatcher?.Invoke(CommandManager.InvalidateRequerySuggested);
            ```
    16. Update `WebExclusionPreset` list with more common patterns (optional, but good practice):
        *   **Consider replacing the existing `WebExclusionPreset` list with this more comprehensive one (or merge carefully):**
            ```csharp
            private static readonly List<string> WebExclusionPreset = new List<string>
            {
                // Directories (often implicitly by name, but can be explicit paths)
                "node_modules", ".git", "bin", "obj", "dist", "build", "vendor", ".next", ".nuxt",
                ".svelte-kit", "coverage", ".vscode", ".idea", ".cache", "__pycache__", ".pytest_cache",
                ".mypy_cache", ".venv", "venv", "env", ".yarn", ".angular", "bower_components",
                ".sass-cache", "uploads", "public/uploads", "out", ".parcel-cache",
                // Specific Files / Patterns
                ".DS_Store", "Thumbs.db", "desktop.ini", ".directory",
                ".env", ".env.*", "*.local", // Common local environment files
                "*.log", "*.tmp", "npm-debug.log*", "yarn-debug.log*", "yarn-error.log*",
                // Binary/Asset Files (by extension)
                "*.ttf", "*.otf", "*.woff", "*.woff2", "*.eot",
                "*.jpg", "*.jpeg", "*.png", "*.gif", "*.bmp", "*.ico", "*.webp", "*.avif",
                "*.svg", // SVGs can be code, but often images. User can override with FileTypeFilter.
                "*.psd", "*.ai", "*.sketch", "*.fig", "*.xcf",
                "*.mp3", "*.wav", "*.ogg", "*.mp4", "*.webm", "*.avi", "*.mov", "*.mkv", "*.flac", "*.m4a", "*.aac",
                "*.pdf", "*.doc", "*.docx", "*.xls", "*.xlsx", "*.ppt", "*.pptx",
                "*.zip", "*.rar", "*.7z", "*.tar", "*.gz", "*.tgz", "*.bz2",
                "*.sqlite", "*.db", "*.mdb", "*.accdb",
                "*.dll", "*.exe", "*.so", "*.dylib", "*.class", "*.pyc", "*.pyo",
                "*.crx", "*.xpi", "*.app", "*.apk", "*.ipa",
                // Lock files
                "yarn.lock", "package-lock.json", "composer.lock", "poetry.lock", "Pipfile.lock",
                // Minified/Generated
                "*.min.js", "*.min.css",
                "*.map", // Source maps
                // Backup files
                "*.bak", "*.swp", "*.swo",
            };
            ```
    17. Refine `MatchesWildcardInternal`:
        *   **Replace the method content with this more robust version:**
            ```csharp
            private static bool MatchesWildcardInternal(string nameOrPath, List<string> wildcards, bool isFullPathSegment)
            {
                // Normalize the input path/name once
                string normalizedNameOrPath = nameOrPath.Replace('\\', '/');

                foreach (var wildcardPattern in wildcards) // wildcards are already normalized in PrepareExclusionMatchers
                {
                    // Exact match (case-insensitive)
                    if (normalizedNameOrPath.Equals(wildcardPattern, StringComparison.OrdinalIgnoreCase)) return true;

                    // *.ext
                    if (wildcardPattern.StartsWith("*.") && normalizedNameOrPath.EndsWith(wildcardPattern.Substring(1), StringComparison.OrdinalIgnoreCase)) return true;
                    // name.* (match "file.txt" with "file.*")
                    if (wildcardPattern.EndsWith(".*") && 
                        normalizedNameOrPath.StartsWith(wildcardPattern.Substring(0, wildcardPattern.Length - 2), StringComparison.OrdinalIgnoreCase) && 
                        normalizedNameOrPath.LastIndexOf('.') > wildcardPattern.Length - 3) // ensure dot is after prefix
                        return true;
                    // prefix* (match "prefixName" with "prefix*")
                    if (wildcardPattern.EndsWith("*") && !wildcardPattern.StartsWith("*") && 
                        normalizedNameOrPath.StartsWith(wildcardPattern.Substring(0, wildcardPattern.Length - 1), StringComparison.OrdinalIgnoreCase)) return true;
                    // *suffix (match "nameSuffix" with "*suffix")
                    if (wildcardPattern.StartsWith("*") && !wildcardPattern.EndsWith("*") && 
                        normalizedNameOrPath.EndsWith(wildcardPattern.Substring(1), StringComparison.OrdinalIgnoreCase)) return true;
                    // *contains* (match "nameContainsWord" with "*Contains*")
                    if (wildcardPattern.StartsWith("*") && wildcardPattern.EndsWith("*") && wildcardPattern.Length > 2 && 
                        normalizedNameOrPath.Contains(wildcardPattern.Substring(1, wildcardPattern.Length - 2), StringComparison.OrdinalIgnoreCase)) return true;
                
                    // Path segment matching (e.g., "docs/specific_file.md" or "**/tests/**")
                    if (isFullPathSegment && wildcardPattern.Contains("/"))
                    {
                        // This is a simplified glob. For full gitignore-style globbing, a library would be better.
                        // Example: "foo/bar" should match "some/path/foo/bar/file.txt"
                        // Example: "foo/*.js" should match "foo/script.js"
                        // For simplicity, let's stick to basic contains for path segments or direct match if no wildcards in path segment.
                        if (normalizedNameOrPath.Contains(wildcardPattern, StringComparison.OrdinalIgnoreCase)) // Basic contains
                            return true; 
                        // TODO LLM: This path wildcard matching can be significantly improved if needed,
                        // e.g., by splitting wildcard and path into segments and matching them.
                        // For now, 'contains' is a basic approximation.
                    }
                }
                return false;
            }
            ```
    18. Refine `BuildTreeStringInternal` and `PrintNode` for better tree output:
        *   **Replace the entire `BuildTreeStringInternal` method with:**
            ```csharp
            private static void BuildTreeStringInternal(List<string> relativePaths, StringBuilder builder)
            {
                var root = new Dictionary<string, object>(); // Using object to store sub-dictionaries or null for files
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
                            if (!(currentNode[part] is Dictionary<string, object>)) // Ensure it's a dictionary if a deeper path exists
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
            ```
        *   **Replace the entire `PrintNode` helper method (likely nested or static within `MainWindowViewModel`) with:**
            ```csharp
            private static void PrintNode(Dictionary<string, object> node, string indent, bool isRoot, StringBuilder builder)
            {
                var sortedKeys = node.Keys
                                    .OrderBy(k => node[k] is null) // Files (null value) after directories (Dictionary value)
                                    .ThenBy(k => k, StringComparer.OrdinalIgnoreCase) // Then alphabetically
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
            ```

---

**Step 5: Modify `MainWindow.xaml.cs`**

*   **File Path:** `MainWindow.xaml.cs`
*   **Changes:**
    1.  Ensure `System.Windows.Controls` is imported.
        ```csharp
        using System.Windows.Controls; // Add if not present
        ```
    2.  Remove event handlers for buttons if Commands are solely used (they are).
        *   **Comment out or delete:** `btnBrowseSource_Click` and `btnBrowseOutput_Click` methods.
    3.  Remove `chkOutputToConsole_CheckedChanged` if `IsOutputToConsole` binding is TwoWay and ViewModel handles logic (it does).
        *   **Comment out or delete:** `chkOutputToConsole_CheckedChanged` method.
    4.  Modify `HandleCheckChange` (rename to `HandleTreeNodeCheckChange` for clarity) to use `DispatcherPriority.Background` for regeneration, and ensure UI is enabled.
        *   **Rename:** `HandleCheckChange` to `HandleTreeNodeCheckChange`. Update callers (`CheckBox_Checked`, `CheckBox_Unchecked`).
        *   **Replace the content of `HandleTreeNodeCheckChange` with:**
            ```csharp
            private async Task HandleTreeNodeCheckChange(object sender)
            {
                if (DataContext is MainWindowViewModel vm &&
                    sender is CheckBox { DataContext: TreeNodeViewModel nodeVm } && // Modern pattern matching
                    vm.IsUiEnabled) 
                {
                    // The IsChecked property of TreeNodeViewModel is bound TwoWay.
                    // Its setter handles propagation and INotifyPropertyChanged.
                    // We just need to ensure that after this UI-driven change is processed,
                    // the output is regenerated.
                    await Application.Current.Dispatcher.BeginInvoke(async () =>
                    {
                        if (vm.IsUiEnabled) // Re-check, as state might change during dispatcher processing
                        {
                            await vm.RegenerateOutputAsync();
                        }
                    }, System.Windows.Threading.DispatcherPriority.Background); // Use Background priority
                }
            }
            ```

---

**Step 6: Modify `MainWindow.xaml`**

*   **File Path:** `MainWindow.xaml`
*   **Changes:**
    1.  Add local namespace for the converter if not already present at the top in `<Window ...>`:
        ```xml
        xmlns:local="clr-namespace:RepoToTxtGui"
        ```
    2.  Increase Window Height:
        *   Change `Height="600"` to `Height="750"` (or adjust as needed for new UI elements).
    3.  Modify `SelectedFolderPath` TextBox:
        *   Change `Mode=TwoWay` to `Mode=OneWay` (ViewModel sets this programmatically).
            ```xml
            <TextBox Grid.Column="0" Text="{Binding SelectedFolderPath, Mode=OneWay}" ... />
            ```
    4.  Remove `Click` handlers from buttons that use `Command`:
        *   `<Button Grid.Column="1" Content="Select Folder..." ... Click="btnBrowseSource_Click" .../>` -> remove `Click="btnBrowseSource_Click"`
        *   `<Button Grid.Column="1" Content="Browse..." ... Click="btnBrowseOutput_Click" .../>` -> remove `Click="btnBrowseOutput_Click"`
    5.  Remove `Checked` and `Unchecked` handlers from `IsOutputToConsole` CheckBox:
        *   `<CheckBox Content="Output to preview (instead of file)" ... IsChecked="{Binding IsOutputToConsole, Mode=TwoWay}" Checked="chkOutputToConsole_CheckedChanged" Unchecked="chkOutputToConsole_CheckedChanged"/>`
        *   -> remove `Checked="chkOutputToConsole_CheckedChanged"` and `Unchecked="chkOutputToConsole_CheckedChanged"`
    6.  **Add File Type Filters Panel:**
        *   Insert the following XAML *after* the `Grid` for "Output File Path" (the one with `OutputFilePath` TextBox) and *before* the "User Pre-Prompt Section" (which you'll add next).
            ```xml
            <!-- File Type Filters Panel -->
            <DockPanel DockPanel.Dock="Top" Margin="0,5,0,10" 
                       Visibility="{Binding FileTypeFilters.Count, Converter={StaticResource InverseZeroToCollapsedConverter}, FallbackValue=Collapsed, ConverterParameter=InverseZeroToCollapsed}">
                <TextBlock Text="Filter by File Type (Checked types are included):" VerticalAlignment="Center" Margin="0,0,10,0" DockPanel.Dock="Left"/>
                <ScrollViewer HorizontalScrollBarVisibility="Auto" VerticalScrollBarVisibility="Disabled">
                    <ItemsControl ItemsSource="{Binding FileTypeFilters}">
                        <ItemsControl.ItemsPanel>
                            <ItemsPanelTemplate>
                                <StackPanel Orientation="Horizontal"/>
                            </ItemsPanelTemplate>
                        </ItemsControl.ItemsPanel>
                        <ItemsControl.ItemTemplate>
                            <DataTemplate>
                                <CheckBox Content="{Binding DisplayName}" 
                                          IsChecked="{Binding IsSelected, Mode=TwoWay}" 
                                          Margin="5,2"
                                          IsEnabled="{Binding DataContext.IsUiEnabled, RelativeSource={RelativeSource AncestorType=Window}}"/>
                            </DataTemplate>
                        </ItemsControl.ItemTemplate>
                    </ItemsControl>
                </ScrollViewer>
            </DockPanel>
            ```
    7.  **Add User Pre-Prompt Section:**
        *   Insert the following XAML *after* the "File Type Filters Panel" (added above) and *before* the main `Grid` containing the TreeView and Preview (commented as "Main Content Area").
            ```xml
            <!-- User Pre-Prompt Section -->
            <DockPanel DockPanel.Dock="Top" Margin="0,0,0,10">
                <TextBlock Text="Custom Pre-Prompt (optional):" DockPanel.Dock="Top" Margin="0,0,0,5"/>
                <TextBox Text="{Binding UserPrePrompt, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"
                         AcceptsReturn="True"
                         TextWrapping="Wrap"
                         MinHeight="40"
                         MaxHeight="120" 
                         VerticalScrollBarVisibility="Auto"
                         BorderBrush="LightGray"
                         BorderThickness="1"
                         Padding="3"
                         IsEnabled="{Binding IsUiEnabled}"/>
            </DockPanel>
            ```
    8.  **Ensure TreeView Scrolling:**
        *   Wrap the existing `TreeView` inside a `ScrollViewer`.
            *   **Find:**
                ```xml
                <Border BorderBrush="LightGray" BorderThickness="1" Grid.Column="0">
                    <TreeView ItemsSource="{Binding RootNodes}" ... >
                        <!-- ... existing TreeView content ... -->
                    </TreeView>
                </Border>
                ```
            *   **Replace with:**
                ```xml
                <Border BorderBrush="LightGray" BorderThickness="1" Grid.Column="0">
                    <ScrollViewer VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Auto">
                        <TreeView ItemsSource="{Binding RootNodes}"
                                  VirtualizingPanel.IsVirtualizing="True"
                                  VirtualizingPanel.VirtualizationMode="Recycling">
                            <TreeView.ItemContainerStyle>
                                <Style TargetType="{x:Type TreeViewItem}">
                                    <Setter Property="IsEnabled"
                                            Value="{Binding DataContext.IsUiEnabled, RelativeSource={RelativeSource AncestorType=Window}}"/>
                                </Style>
                            </TreeView.ItemContainerStyle>
                            <TreeView.ItemTemplate>
                                <HierarchicalDataTemplate ItemsSource="{Binding Children}">
                                    <StackPanel Orientation="Horizontal">
                                        <CheckBox IsChecked="{Binding IsChecked, Mode=TwoWay}"
                                                  VerticalAlignment="Center"
                                                  Checked="CheckBox_Checked" 
                                                  Unchecked="CheckBox_Unchecked"/>
                                        <TextBlock Text="{Binding Name}"
                                                   Margin="5,0,0,0"
                                                   VerticalAlignment="Center"/>
                                    </StackPanel>
                                </HierarchicalDataTemplate>
                            </TreeView.ItemTemplate>
                        </TreeView>
                    </ScrollViewer>
                </Border>
                ```
    9.  **Correct StatusBar Placement and Main Content Grid Layout:**
        *   Locate the `Grid` commented as "Main Content Area".
        *   Add `Grid.RowDefinitions` to this grid to properly accommodate the content and the status bar.
        *   Ensure the `Border` (for TreeView) and the `Grid` (for Output Preview) are in `Grid.Row="0"`.
        *   Ensure the `StatusBar` is in `Grid.Row="1"`.
            *   **Find the start of the "Main Content Area" `Grid`:**
                ```xml
                <!-- Main Content Area -->
                <Grid>
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="250"/>
                        <ColumnDefinition Width="*"/>
                    </Grid.ColumnDefinitions>
                ```
            *   **Modify it to (add RowDefinitions, assign Grid.Row to children):**
                ```xml
                <!-- Main Content Area -->
                <Grid>
                    <Grid.RowDefinitions>
                        <RowDefinition Height="*"/> <!-- Content Row -->
                        <RowDefinition Height="Auto"/> <!-- StatusBar Row -->
                    </Grid.RowDefinitions>
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="250"/>
                        <ColumnDefinition Width="*"/>
                    </Grid.ColumnDefinitions>

                    <!-- Left Column: Tree View -->
                    <Border BorderBrush="LightGray" BorderThickness="1" Grid.Column="0" Grid.Row="0">
                        <!-- ... ScrollViewer and TreeView from previous step ... -->
                    </Border>

                    <!-- Right Column: Output Preview -->
                    <Grid Grid.Column="1" Grid.Row="0" Margin="10,0,0,0">
                        <!-- ... existing Preview Grid content ... -->
                    </Grid>
                    
                    <!-- Busy Overlay remains spanning all columns and rows of this grid -->
                    <Grid Grid.Column="0" Grid.Row="0" Grid.ColumnSpan="2" Grid.RowSpan="1" <!-- Ensure RowSpan is 1 if StatusBar is in Row 1 -->
                          Background="#AAFFFFFF"
                          Visibility="{Binding IsBusy, Converter={StaticResource BooleanToVisibilityConverter}}">
                        <TextBlock Text="Processing..." HorizontalAlignment="Center" VerticalAlignment="Center" FontSize="16" FontWeight="Bold"/>
                    </Grid>

                    <!-- Status Bar -->
                    <StatusBar Grid.Row="1" Grid.Column="0" Grid.ColumnSpan="2" VerticalAlignment="Bottom" Height="22">
                        <TextBlock Text="{Binding StatusText, Mode=OneWay, TargetNullValue=Ready}"/>
                        <ProgressBar Width="100" Height="15"
                                     Visibility="{Binding IsBusy, Converter={StaticResource BooleanToVisibilityConverter}}"
                                     Margin="10,0" IsIndeterminate="True"/>
                    </StatusBar>
                </Grid>
                ```
                *Self-correction: The Busy Overlay should cover the content area (Row 0). The status bar is in Row 1. So the Busy Overlay should be `Grid.Row="0"` and `Grid.RowSpan="1"` (or just not specify RowSpan if it's only for row 0).*
                *Corrected Busy Overlay placement within "Main Content Area" `Grid`:*
                ```xml
                    <!-- Busy Overlay -->
                    <Grid Grid.Row="0" Grid.Column="0" Grid.ColumnSpan="2" <!-- Covers only the content area -->
                          Background="#AAFFFFFF"
                          Visibility="{Binding IsBusy, Converter={StaticResource BooleanToVisibilityConverter}}">
                        <TextBlock Text="Processing..."
                                   HorizontalAlignment="Center"
                                   VerticalAlignment="Center"
                                   FontSize="16"
                                   FontWeight="Bold"/>
                    </Grid>
                ```

    10. **Add Resource for `ZeroToCollapsedConverter`:**
        *   Inside `<Window.Resources>` (or create it if it doesn't exist directly under `<Window>`), add:
            ```xml
            <Window.Resources>
                <BooleanToVisibilityConverter x:Key="BooleanToVisibilityConverter"/>
                <local:ZeroToCollapsedConverter x:Key="InverseZeroToCollapsedConverter"/>
            </Window.Resources>
            ```
            *(Note: If `<Window.Resources>` already exists with `BooleanToVisibilityConverter`, just add the `ZeroToCollapsedConverter` to it.)*

---
**Final Check:**
*   After applying all changes, build the project.
*   Test all new features:
    *   Folder selection and tree view population.
    *   TreeView scrolling.
    *   File type filter checkboxes appearing and affecting selected files in the output.
    *   User pre-prompt being included in the output.
    *   The output header for directory structure should be "Directory Structure:".
    *   Performance with a reasonably sized repository.
    *   Copy to Clipboard and Save to File functionalities.
    *   Web preset checkbox correctly applying/removing exclusions.

This concludes the instructions. Good luck, Agent!