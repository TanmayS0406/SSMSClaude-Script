using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.VisualStudio.PlatformUI;
using SsmsChatExtension.Database;

namespace SsmsChatExtension.UI
{
    /// <summary>
    /// The one-click "Tune procedures" picker: choose a database, then pick stored procedures from its drop-down (any number,
    /// or the slowest five) and hand them to Claude to optimize and benchmark.
    /// </summary>
    public partial class ProcedureTuneDialog : DialogWindow
    {
        private readonly ProcedureTuneModel _model;

        /// <param name="databases">The databases to choose from; the one marked current is selected first.</param>
        /// <param name="loadProcedures">Reads the procedures of a database. Runs on a background thread, once per database picked.</param>
        internal ProcedureTuneDialog(string serverName, IReadOnlyList<(string Name, bool IsCurrent)> databases, Func<string, ProcedureListing> loadProcedures)
        {
            InitializeComponent();
            _model = new ProcedureTuneModel(serverName, databases, loadProcedures);
            DataContext = _model;
            Loaded += (s, e) => { var ignored = _model.LoadProceduresAsync(); };
        }

        /// <summary>What was chosen; valid after <see cref="DialogWindow.ShowModal"/> returned true.</summary>
        internal ProcedureTuneRequest Request
        {
            get
            {
                return new ProcedureTuneRequest(_model.SelectedDatabase, _model.SelectedProcedures(), _model.Runs, _model.RollbackEachRun);
            }
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }

        private void ProcedureButton_Click(object sender, RoutedEventArgs e)
        {
            ProcedurePopup.IsOpen = true;
        }

        private void ProcedurePopup_Opened(object sender, EventArgs e)
        {
            // The popup is its own window: focus moves into it once it has been laid out.
            Dispatcher.BeginInvoke(new Action(() => FilterBox.Focus()), DispatcherPriority.Input);
        }

        private void ProcedurePopup_KeyDown(object sender, KeyEventArgs e)
        {
            // Close just the drop-down; left alone, Escape would cancel and Enter would accept the whole dialog.
            if (e.Key != Key.Escape && e.Key != Key.Enter) return;
            ProcedurePopup.IsOpen = false;
            e.Handled = true;
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            _model.SelectShown();
        }

        private void SelectSlowest_Click(object sender, RoutedEventArgs e)
        {
            _model.SelectSlowest(5);
        }

        private void SelectNone_Click(object sender, RoutedEventArgs e)
        {
            _model.SelectNone();
        }
    }

    internal sealed class ProcedureTuneModel : INotifyPropertyChanged
    {
        private static readonly int[] RunOptions = { 5, 10, 20, 50 };

        private readonly Func<string, ProcedureListing> _loadProcedures;
        private List<ProcedureChoice> _choices = new List<ProcedureChoice>();
        private ICollectionView _view;
        private string _selectedDatabase;
        private string _filter = string.Empty;
        private string _loadError = string.Empty;
        private bool _isLoading = true; // the dialog reads the first database as soon as it is shown
        private bool _hasStatistics;
        private int _loadVersion;
        private int _selectedCount;
        private int _runs = BenchmarkPlan.DefaultRuns;
        private bool _rollbackEachRun = true;

        public ProcedureTuneModel(string serverName, IReadOnlyList<(string Name, bool IsCurrent)> databases, Func<string, ProcedureListing> loadProcedures)
        {
            ServerName = serverName;
            Databases = databases.Select(d => d.Name).ToList();
            _loadProcedures = loadProcedures;
            _selectedDatabase = databases.Where(d => d.IsCurrent).Select(d => d.Name).FirstOrDefault() ?? Databases[0];

            ShowProcedures(new List<ProcedureInfo>(), false);
        }

        public string ServerName { get; }

        public IReadOnlyList<string> Databases { get; }

        /// <summary>The database whose procedures are listed. Picking another one reads its procedures and clears the selection.</summary>
        public string SelectedDatabase
        {
            get { return _selectedDatabase; }
            set
            {
                if (string.IsNullOrEmpty(value) || !Set(ref _selectedDatabase, value)) return;
                var ignored = LoadProceduresAsync();
            }
        }

        /// <summary>True while the procedures of the selected database are being read.</summary>
        public bool IsLoading
        {
            get { return _isLoading; }
            private set
            {
                if (Set(ref _isLoading, value)) OnProcedureStateChanged();
            }
        }

        /// <summary>Why the procedures could not be read (no access to the database, a timeout, ...); empty when they could.</summary>
        private string LoadError
        {
            get { return _loadError; }
            set
            {
                if (Set(ref _loadError, value)) OnProcedureStateChanged();
            }
        }

        /// <summary>False when the login cannot read the plan cache statistics (or nothing has run yet).</summary>
        public bool HasStatistics
        {
            get { return _hasStatistics; }
        }

        /// <summary>The filtered list the drop-down shows. Filtering refreshes a view instead of rebuilding the list, so thousands of procedures stay fast.</summary>
        public ICollectionView View
        {
            get { return _view; }
        }

        public IReadOnlyList<int> RunChoices
        {
            get { return RunOptions; }
        }

        public int Runs
        {
            get { return _runs; }
            set
            {
                if (Set(ref _runs, value)) OnPropertyChanged(nameof(SelectionSummary));
            }
        }

        public bool RollbackEachRun
        {
            get { return _rollbackEachRun; }
            set { Set(ref _rollbackEachRun, value); }
        }

        public string Filter
        {
            get { return _filter; }
            set
            {
                if (!Set(ref _filter, value ?? string.Empty)) return;
                View.Refresh();
            }
        }

        /// <summary>The drop-down can be opened once there is something to pick.</summary>
        public bool CanPickProcedures
        {
            get { return !_isLoading && _choices.Count > 0; }
        }

        /// <summary>What the closed drop-down shows: the loading state, the pick, or how many procedures there are to pick from.</summary>
        public string ProcedureButtonText
        {
            get
            {
                if (_isLoading) return "Loading stored procedures...";
                if (_loadError.Length > 0) return "Could not read the stored procedures";
                if (_choices.Count == 0) return "No stored procedures in this database";
                if (_selectedCount == 0) return "Choose stored procedures (" + _choices.Count + " available)";

                var names = _choices.Where(c => c.IsSelected).Select(c => c.DisplayName).ToList();
                return names.Count == 1 ? names[0] : names.Count + " selected: " + string.Join(", ", names);
            }
        }

        public bool CanTune
        {
            get { return !_isLoading && _selectedCount >= 1 && _selectedCount <= BenchmarkPlan.MaxProcedures; }
        }

        public string SelectionSummary
        {
            get
            {
                if (_isLoading) return "Reading the stored procedures of " + _selectedDatabase + "...";
                if (_loadError.Length > 0) return "Could not read the stored procedures of " + _selectedDatabase + ": " + _loadError;
                if (_choices.Count == 0) return _selectedDatabase + " has no stored procedures.";
                if (_selectedCount == 0)
                {
                    return HasStatistics
                        ? "Select at least one procedure. \"Slowest 5\" picks the ones that have used the most time since their plans were cached."
                        : "Select at least one procedure. (Run statistics are not available: the login lacks VIEW SERVER STATE, or nothing has run yet.)";
                }
                if (_selectedCount > BenchmarkPlan.MaxProcedures)
                {
                    return _selectedCount + " selected - tune at most " + BenchmarkPlan.MaxProcedures + " at a time, then repeat for the rest.";
                }
                return _selectedCount + (_selectedCount == 1 ? " procedure selected." : " procedures selected.")
                       + " The benchmark runs each one " + (Runs + BenchmarkPlan.DefaultWarmupRuns) + " times before and after.";
            }
        }

        /// <summary>
        /// Reads the procedures of the selected database off the UI thread. The previous database's list (and the ticks in it)
        /// is dropped at once, and a result that arrives after another database was picked is ignored.
        /// </summary>
        public async Task LoadProceduresAsync()
        {
            var version = ++_loadVersion;
            var database = _selectedDatabase;

            ShowProcedures(new List<ProcedureInfo>(), false);
            LoadError = string.Empty;
            IsLoading = true;

            ProcedureListing listing = null;
            string error = null;
            try
            {
                listing = await Task.Run(() => _loadProcedures(database));
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            if (version != _loadVersion) return;

            if (listing != null) ShowProcedures(listing.Procedures, listing.HasStatistics);
            LoadError = error ?? string.Empty;
            IsLoading = false;
        }

        public IReadOnlyList<ProcedureInfo> SelectedProcedures()
        {
            return _choices.Where(c => c.IsSelected).Select(c => c.Info).ToList();
        }

        /// <summary>Ticks the procedures the filter shows, up to the limit per run.</summary>
        public void SelectShown()
        {
            var room = BenchmarkPlan.MaxProcedures - _selectedCount;
            foreach (var choice in View.Cast<ProcedureChoice>().Where(c => c.CanSelect && !c.IsSelected).Take(Math.Max(0, room)))
            {
                choice.IsSelected = true;
            }
        }

        /// <summary>Replaces the selection with the procedures that used the most total time.</summary>
        public void SelectSlowest(int count)
        {
            var slowest = _choices
                .Where(c => c.CanSelect && c.Info.TotalElapsedMs > 0)
                .OrderByDescending(c => c.Info.TotalElapsedMs)
                .Take(count)
                .ToList();
            SelectNone();
            foreach (var choice in slowest) choice.IsSelected = true;
        }

        public void SelectNone()
        {
            foreach (var choice in _choices.Where(c => c.IsSelected).ToList()) choice.IsSelected = false;
        }

        /// <summary>Swaps in the procedures of another database: nothing ticked, no filter, a fresh view.</summary>
        private void ShowProcedures(IReadOnlyList<ProcedureInfo> procedures, bool hasStatistics)
        {
            foreach (var choice in _choices) choice.PropertyChanged -= OnChoiceChanged;
            _choices = procedures.Select(p => new ProcedureChoice(p)).ToList();
            foreach (var choice in _choices) choice.PropertyChanged += OnChoiceChanged;

            _selectedCount = 0;
            _filter = string.Empty;
            _hasStatistics = hasStatistics && procedures.Any(p => p.Executions > 0);

            _view = CollectionViewSource.GetDefaultView(_choices);
            _view.Filter = item => Matches((ProcedureChoice)item);

            OnPropertyChanged(nameof(View));
            OnPropertyChanged(nameof(Filter));
            OnPropertyChanged(nameof(HasStatistics));
            OnProcedureStateChanged();
        }

        private bool Matches(ProcedureChoice choice)
        {
            return _filter.Length == 0 || choice.DisplayName.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void OnChoiceChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(ProcedureChoice.IsSelected)) return;

            // Counting the change keeps ticking a box O(1) however long the list is.
            _selectedCount += ((ProcedureChoice)sender).IsSelected ? 1 : -1;
            OnProcedureStateChanged();
        }

        /// <summary>Everything that depends on the loading state, the list or the ticks.</summary>
        private void OnProcedureStateChanged()
        {
            OnPropertyChanged(nameof(CanPickProcedures));
            OnPropertyChanged(nameof(ProcedureButtonText));
            OnPropertyChanged(nameof(CanTune));
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

    internal sealed class ProcedureChoice : INotifyPropertyChanged
    {
        private bool _isSelected;

        public ProcedureChoice(ProcedureInfo info)
        {
            Info = info;
        }

        public ProcedureInfo Info { get; }

        public string DisplayName
        {
            get { return Info.DisplayName; }
        }

        public string StatsText
        {
            get { return Info.StatsText; }
        }

        /// <summary>Encrypted procedures cannot be read, so they cannot be tuned.</summary>
        public bool CanSelect
        {
            get { return !Info.IsEncrypted; }
        }

        public string ToolTip
        {
            get { return Info.IsEncrypted ? "Created WITH ENCRYPTION: the definition cannot be read, so it cannot be tuned." : Info.Name.Quoted; }
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
