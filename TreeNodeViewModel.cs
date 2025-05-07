using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows; // Explicit import for Application

namespace RepoToTxtGui
{
    public class TreeNodeViewModel : INotifyPropertyChanged
    {
        private bool? _isChecked = false; // Use nullable bool for tri-state
        private bool _isUpdatingCheckState = false; // Prevent recursive loops

        public string Name { get; set; }
        public string FullPath { get; set; }
        public bool IsDirectory { get; set; }
        public TreeNodeViewModel? Parent { get; }
        public ObservableCollection<TreeNodeViewModel> Children { get; } = new ObservableCollection<TreeNodeViewModel>();

        public TreeNodeViewModel(string name, string fullPath, bool isDirectory, TreeNodeViewModel? parent = null)
        {
            Name = name;
            FullPath = fullPath;
            IsDirectory = isDirectory;
            Parent = parent;
        }

        public bool? IsChecked
        {
            get => _isChecked;
            set => SetIsChecked(value, true, true);
        }

        // Public setter for code-behind logic, allowing control over propagation
        public void SetIsChecked(bool? value, bool updateChildren, bool updateParent)
        {
            if (_isUpdatingCheckState) return; // Prevent re-entrancy

            if (value == _isChecked) return;

            _isUpdatingCheckState = true; // Mark as updating

            _isChecked = value;
            OnPropertyChanged(nameof(IsChecked)); // Notify UI

            if (updateChildren && _isChecked.HasValue && IsDirectory)
            {
                foreach (var child in Children)
                {
                    child.SetIsChecked(_isChecked, true, false); // Propagate change downwards
                }
            }

            if (updateParent && Parent != null)
            {
                Parent.UpdateCheckStateBasedOnChildren(); // Notify parent to re-evaluate its state
            }

            _isUpdatingCheckState = false; // Done updating
        }

        // Called by children when their state changes
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

        private bool? CalculateStateFromChildren()
        {
            if (!Children.Any()) return _isChecked; // Keep current state if no children

            bool anyChecked = false;
            bool allChecked = true; // Assume all are checked initially

            foreach (var child in Children)
            {
                if (child.IsChecked == true || child.IsChecked == null) // Treat null (indeterminate) as 'partially checked'
                {
                    anyChecked = true;
                }
                if (child.IsChecked == false || child.IsChecked == null) // Treat null (indeterminate) as 'not fully checked'
                {
                    allChecked = false;
                }
            }

            if (allChecked) return true;
            if (anyChecked) return null; // Indeterminate state
            return false; // None checked
        }

        // INotifyPropertyChanged Implementation
        public event PropertyChangedEventHandler? PropertyChanged;
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
    }
}