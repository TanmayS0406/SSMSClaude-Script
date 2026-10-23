using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using Microsoft.VisualStudio.PlatformUI;
using SsmsChatExtension.Database;

namespace SsmsChatExtension.UI
{
    /// <summary>Lets the user pick databases and options for the one-click ER diagram.</summary>
    public partial class DiagramDialog : DialogWindow
    {
        private readonly DiagramDialogModel _model;

        internal DiagramDialog(string serverName, IEnumerable<(string Name, bool IsCurrent)> databases)
        {
            InitializeComponent();
            _model = new DiagramDialogModel(serverName, databases);
            DataContext = _model;
            Loaded += (s, e) => FilterBox.Focus();
        }

        /// <summary>The chosen options; valid after <see cref="DialogWindow.ShowModal"/> returned true.</summary>
        internal DiagramOptions Options
        {
            get
            {
                return new DiagramOptions
                {
                    Databases = _model.Databases.Where(d => d.IsSelected).Select(d => d.Name).ToList(),
                    InferRelationships = _model.InferRelationships,
                    IncludeCodeReferences = _model.IncludeCodeReferences,
                    Schema = string.IsNullOrWhiteSpace(_model.Schema) ? null : _model.Schema.Trim()
                };
            }
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var database in _model.VisibleDatabases) database.IsSelected = true;
        }

        private void SelectNone_Click(object sender, RoutedEventArgs e)
        {
            foreach (var database in _model.VisibleDatabases) database.IsSelected = false;
        }
    }

    internal sealed class DiagramDialogModel : INotifyPropertyChanged
    {
        private string _filter = string.Empty;
        private bool _inferRelationships = true;
        private bool _includeCodeReferences = true;
        private string _schema = string.Empty;

        public DiagramDialogModel(string serverName, IEnumerable<(string Name, bool IsCurrent)> databases)
        {
            ServerName = serverName;
            Databases = databases.Select(d => new DatabaseChoice(d.Name, d.IsCurrent)).ToList();
            foreach (var database in Databases) database.PropertyChanged += (s, e) => SelectionChanged();
            VisibleDatabases = new ObservableCollection<DatabaseChoice>(Databases);
        }

        public string ServerName { get; }
        public IReadOnlyList<DatabaseChoice> Databases { get; }
        public ObservableCollection<DatabaseChoice> VisibleDatabases { get; }

        public string Filter
        {
            get { return _filter; }
            set
            {
                if (!Set(ref _filter, value ?? string.Empty)) return;
                VisibleDatabases.Clear();
                foreach (var database in Databases.Where(d => d.Name.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    VisibleDatabases.Add(database);
                }
            }
        }

        public bool InferRelationships { get { return _inferRelationships; } set { Set(ref _inferRelationships, value); } }
        public bool IncludeCodeReferences { get { return _includeCodeReferences; } set { Set(ref _includeCodeReferences, value); } }
        public string Schema { get { return _schema; } set { Set(ref _schema, value); } }

        public bool HasSelection
        {
            get { return Databases.Any(d => d.IsSelected); }
        }

        public string SelectionSummary
        {
            get
            {
                var count = Databases.Count(d => d.IsSelected);
                return count == 0 ? "Select at least one database."
                     : count == 1 ? "1 database selected."
                     : count + " databases selected - cross-database links are inferred from names, since SQL Server cannot declare them.";
            }
        }

        private void SelectionChanged()
        {
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(SelectionSummary));
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }

        private void OnPropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    internal sealed class DatabaseChoice : INotifyPropertyChanged
    {
        private bool _isSelected;

        public DatabaseChoice(string name, bool isCurrent)
        {
            Name = name;
            IsCurrent = isCurrent;
            _isSelected = isCurrent;
        }

        public string Name { get; }
        public bool IsCurrent { get; }

        public string CurrentMarker
        {
            get { return IsCurrent ? "  (current)" : string.Empty; }
        }

        public bool IsSelected
        {
            get { return _isSelected; }
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }
}
