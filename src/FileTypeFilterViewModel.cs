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