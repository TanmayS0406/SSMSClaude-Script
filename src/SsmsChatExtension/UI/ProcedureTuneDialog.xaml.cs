using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using Microsoft.VisualStudio.PlatformUI;
using SsmsChatExtension.Database;

namespace SsmsChatExtension.UI
{
    /// <summary>
    /// The one-click "Tune procedures" picker: tick any number of stored procedures (or take the slowest five), then hand
    /// them to Claude to optimize and benchmark.
    /// </summary>
    public partial class ProcedureTuneDialog : DialogWindow
    {
        private readonly ProcedureTuneModel _model;

        internal ProcedureTuneDialog(string serverName, string databaseName, IReadOnlyList<ProcedureInfo> procedures, bool hasStatistics)
        {
            InitializeComponent();
            _model = new ProcedureTuneModel(serverName, databaseName, procedures, hasStatistics);
            DataContext = _model;
            Loaded += (s, e) => FilterBox.Focus();
        }

        /// <summary>What was chosen; valid after <see cref="DialogWindow.ShowModal"/> returned true.</summary>
        internal ProcedureTuneRequest Request
        {
            get
            {
                return new ProcedureTuneRequest(_model.DatabaseName, _model.SelectedProcedures(), _model.Runs, _model.RollbackEachRun);
            }
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
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

        private readonly List<ProcedureChoice> _choices;
        private string _filter = string.Empty;
        private int _selectedCount;
        private int _runs = BenchmarkPlan.DefaultRuns;
        private bool _rollbackEachRun = true;

        public ProcedureTuneModel(string serverName, string databaseName, IReadOnlyList<ProcedureInfo> procedures, bool hasStatistics)
        {
            ServerName = serverName;
            DatabaseName = databaseName;
            HasStatistics = hasStatistics && procedures.Any(p => p.Executions > 0);

            _choices = procedures.Select(p => new ProcedureChoice(p)).ToList();
            foreach (var choice in _choices) choice.PropertyChanged += OnChoiceChanged;

            View = CollectionViewSource.GetDefaultView(_choices);
            View.Filter = item => Matches((ProcedureChoice)item);
        }

        public string ServerName { get; }

        public string DatabaseName { get; }

        /// <summary>False when the login cannot read the plan cache statistics (or nothing has run yet).</summary>
        public bool HasStatistics { get; }

        /// <summary>The filtered list the dialog shows. Filtering refreshes a view instead of rebuilding the list, so thousands of procedures stay fast.</summary>
        public ICollectionView View { get; }

        public IReadOnlyList<int> RunChoices
        {
            get { return RunOptions; }
        }

        public int Runs
        {
            get { return _runs; }
            set { Set(ref _runs, value); }
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

        public bool CanTune
        {
            get { return _selectedCount >= 1 && _selectedCount <= BenchmarkPlan.MaxProcedures; }
        }

        public string SelectionSummary
        {
            get
            {
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

        private bool Matches(ProcedureChoice choice)
        {
            return _filter.Length == 0 || choice.DisplayName.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void OnChoiceChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(ProcedureChoice.IsSelected)) return;

            // Counting the change keeps ticking a box O(1) however long the list is.
            _selectedCount += ((ProcedureChoice)sender).IsSelected ? 1 : -1;
            OnPropertyChanged(nameof(CanTune));
            OnPropertyChanged(nameof(SelectionSummary));
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            if (name == nameof(Runs)) OnPropertyChanged(nameof(SelectionSummary));
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
