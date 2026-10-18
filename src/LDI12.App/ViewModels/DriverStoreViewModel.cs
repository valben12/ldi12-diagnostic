using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using LDI12.Actions;
using LDI12.Actions.Repairs;
using LDI12.App.Mvvm;
using LDI12.App.Services;
using LDI12.Core.Diagnostics;
using LDI12.Core.Drivers;
using LDI12.Core.Logging;

namespace LDI12.App.ViewModels
{
    /// <summary>Un paquet du magasin, avec sa case.</summary>
    public sealed class DriverPackageRow : ObservableObject
    {
        private readonly DriverStoreViewModel _owner;
        private bool _isChecked;

        public DriverPackageRow(DriverPackage package, DriverStoreViewModel owner)
        {
            Package = package;
            _owner = owner;
            _isChecked = package.Status == PackageStatus.OldVersion;
        }

        public DriverPackage Package { get; }

        public string Title => Package.Device ?? Package.OriginalName ?? Package.PublishedName;

        public string Detail => string.Join(" · ", new[]
        {
            Package.Provider,
            DriverStoreAnalyzer.ClassLabel(Package.DeviceClass),
            Package.Version,
            Package.Date?.ToString("d MMM yyyy", Formats.French),
        }.Where(t => !string.IsNullOrWhiteSpace(t)));

        public string Names => Package.PublishedName + (Package.OriginalName != null ? " · " + Package.OriginalName : string.Empty);

        public string SizeText => Package.SizeBytes > 0 ? ValueFormat.Bytes(Package.SizeBytes) : string.Empty;

        public string StatusText => Package.Status switch
        {
            PackageStatus.InUse => "En service" + (Package.UsedBy.Count > 0 ? " · " + Package.UsedBy[0] + (Package.UsedBy.Count > 1 ? " +" + (Package.UsedBy.Count - 1) : string.Empty) : string.Empty),
            PackageStatus.OldVersion => "Ancienne version",
            _ => "Inutilisé, version la plus récente",
        };

        public string StatusKey => Package.Status.ToString();

        /// <summary>Un paquet en service ne se coche pas : Windows le refuserait, et c'est voulu.</summary>
        public bool CanCheck => Package.Status != PackageStatus.InUse;

        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                if (!CanCheck || !Set(ref _isChecked, value)) return;
                _owner.SelectionChanged();
            }
        }
    }

    /// <summary>
    /// Le magasin de pilotes, comme Driver Store Explorer : chaque paquet ajouté, ce qu'il pèse,
    /// qui s'en sert, et un nettoyage qui ne retire que les anciennes versions inutilisées.
    /// </summary>
    public sealed class DriverStoreViewModel : ObservableObject, IScreenActivation
    {
        private const string Category = "Magasin de pilotes";

        private readonly RepairsViewModel _repairs;
        private readonly ILdiLogger _logger;
        private readonly ListCollectionView _view;
        private bool _loaded, _isBusy, _demo;
        private string _filter = "All", _summary = "Les pilotes ajoutés à Windows, leurs anciennes versions, ce qu'ils pèsent.";
        private string _status = string.Empty, _selectionText = string.Empty;

        public DriverStoreViewModel(RepairsViewModel repairs, ILdiLogger logger)
        {
            _repairs = repairs ?? throw new ArgumentNullException(nameof(repairs));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _view = new ListCollectionView(Packages) { Filter = Keep };
            _view.SortDescriptions.Add(new SortDescription(nameof(DriverPackageRow.StatusKey), ListSortDirection.Descending));
            _view.SortDescriptions.Add(new SortDescription(nameof(DriverPackageRow.Title), ListSortDirection.Ascending));
            RefreshCommand = new AsyncRelayCommand(LoadAsync, () => !IsBusy);
            DeleteCommand = new AsyncRelayCommand(DeleteAsync, () => !IsBusy && Packages.Any(p => p.IsChecked));
        }

        public ObservableCollection<DriverPackageRow> Packages { get; } = new ObservableCollection<DriverPackageRow>();
        public ICollectionView View => _view;

        public AsyncRelayCommand RefreshCommand { get; }
        public AsyncRelayCommand DeleteCommand { get; }

        public string Summary { get => _summary; private set => Set(ref _summary, value); }
        public string Status { get => _status; private set => Set(ref _status, value); }
        public string SelectionText { get => _selectionText; private set => Set(ref _selectionText, value); }

        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (!Set(ref _isBusy, value)) return;
                RefreshCommand.RaiseCanExecuteChanged();
                DeleteCommand.RaiseCanExecuteChanged();
            }
        }

        public bool IsAll { get => _filter == "All"; set { if (value) Pick("All"); } }
        public bool IsOld { get => _filter == "Old"; set { if (value) Pick("Old"); } }
        public bool IsUnused { get => _filter == "Unused"; set { if (value) Pick("Unused"); } }
        public bool IsInUse { get => _filter == "InUse"; set { if (value) Pick("InUse"); } }

        public string AllLabel { get; private set; } = "Tout";
        public string OldLabel { get; private set; } = "Anciennes versions";
        public string UnusedLabel { get; private set; } = "Inutilisés";
        public string InUseLabel { get; private set; } = "En service";

        public bool HasPackages => Packages.Count > 0;

        private void Pick(string filter)
        {
            if (_filter == filter) return;
            _filter = filter;
            foreach (var name in new[] { nameof(IsAll), nameof(IsOld), nameof(IsUnused), nameof(IsInUse) }) Raise(name);
            _view.Refresh();
        }

        private bool Keep(object item)
        {
            var row = (DriverPackageRow)item;
            return _filter switch
            {
                "Old" => row.Package.Status == PackageStatus.OldVersion,
                "Unused" => !row.Package.InUse,
                "InUse" => row.Package.InUse,
                _ => true,
            };
        }

        public void Activated()
        {
            if (_loaded || _demo) return;
            _loaded = true;
            _ = LoadAsync();
        }

        public void Deactivated()
        {
        }

        public async Task LoadAsync()
        {
            if (IsBusy) return;
            IsBusy = true;
            Status = "Lecture du magasin de pilotes…";
            try
            {
                var packages = await Task.Run(DriverStoreReader.Read).ConfigureAwait(true);
                if (_demo) return;
                Show(packages);
                Status = string.Empty;
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                _logger.Error(Category, "Lecture du magasin impossible.", ex);
                Status = "Lecture impossible : " + ex.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void Show(IReadOnlyList<DriverPackage> packages)
        {
            Packages.Clear();
            foreach (var package in packages) Packages.Add(new DriverPackageRow(package, this));

            var old = packages.Where(p => p.Status == PackageStatus.OldVersion).ToList();
            AllLabel = "Tout " + packages.Count;
            OldLabel = "Anciennes versions " + old.Count;
            UnusedLabel = "Inutilisés " + packages.Count(p => !p.InUse);
            InUseLabel = "En service " + packages.Count(p => p.InUse);
            foreach (var name in new[] { nameof(AllLabel), nameof(OldLabel), nameof(UnusedLabel), nameof(InUseLabel), nameof(HasPackages) }) Raise(name);

            Summary = packages.Count == 0
                ? "Aucun pilote ajouté à Windows : le magasin ne contient que les pilotes d'origine."
                : packages.Count + " paquet(s), " + ValueFormat.Bytes(packages.Sum(p => p.SizeBytes))
                  + (old.Count > 0 ? " · " + old.Count + " ancienne(s) version(s) inutilisée(s), " + ValueFormat.Bytes(old.Sum(p => p.SizeBytes)) + " à regagner." : " · rien à nettoyer.");
            if (old.Count > 0 && _filter == "All") IsOld = true;
            _view.Refresh();
            SelectionChanged();
        }

        internal void SelectionChanged()
        {
            var selected = Packages.Where(p => p.IsChecked).ToList();
            SelectionText = selected.Count == 0
                ? "Cochez les paquets à retirer : les anciennes versions inutilisées le sont déjà."
                : selected.Count + " paquet(s) coché(s) · " + ValueFormat.Bytes(selected.Sum(p => p.Package.SizeBytes));
            DeleteCommand.RaiseCanExecuteChanged();
        }

        private async Task DeleteAsync()
        {
            var selected = Packages.Where(p => p.IsChecked).ToList();
            if (selected.Count == 0) return;
            var latest = selected.Count(p => p.Package.Status == PackageStatus.Latest);

            var question = "Retirer " + selected.Count + " paquet(s) du magasin de pilotes (" + ValueFormat.Bytes(selected.Sum(p => p.Package.SizeBytes)) + ") ?\n\n"
                           + "Un point de restauration est créé avant. Les pilotes en service ne sont jamais touchés."
                           + (latest > 0 ? "\n\nAttention : " + latest + " d'entre eux sont la dernière version d'un pilote inutilisé (matériel débranché ?). Il faudra le réinstaller au prochain branchement." : string.Empty);
            var owner = Application.Current?.MainWindow;
            var answer = owner == null
                ? MessageBox.Show(question, "LDI12 Diagnostic", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
                : MessageBox.Show(owner, question, "LDI12 Diagnostic", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;

            IsBusy = true;
            try
            {
                Status = "Point de restauration…";
                Status = await _repairs.EnsureRestorePointAsync().ConfigureAwait(true);
                var outcome = await _repairs.RunQuietAsync(ActionIds.DriverPackagesDelete, new Dictionary<string, string>
                {
                    [DriverPackagesDeleteAction.PackagesParameter] = string.Join("|", selected.Select(p => p.Package.PublishedName)),
                    [DriverPackagesDeleteAction.LabelsParameter] = string.Join("|", selected.Select(p => p.Title.Replace("|", "/"))),
                }, new Progress<Actions.ActionProgress>(p => Status = p.Text), CancellationToken.None).ConfigureAwait(true);
                _logger.Info(Category, outcome.Summary);
                IsBusy = false;
                await LoadAsync().ConfigureAwait(true);
                Status = outcome.Summary;
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>Un magasin d'exemple, pour les captures d'écran seulement.</summary>
        public void ShowDemo()
        {
            _demo = _loaded = true;
            DriverPackage P(int oem, string original, string provider, string @class, string version, int year, int month, long size, string? device, params string[] usedBy)
                => new DriverPackage
                {
                    PublishedName = "oem" + oem + ".inf", OriginalName = original, Provider = provider, DeviceClass = @class, Version = version,
                    Date = new DateTime(year, month, 12), SizeBytes = size, Device = device, UsedBy = usedBy,
                };
            var packages = new List<DriverPackage>
            {
                P(41, "nv_dispig.inf", "NVIDIA", "Display", "32.0.15.6094", 2024, 9, 1_210_000_000, "NVIDIA GeForce RTX 3060", "NVIDIA GeForce RTX 3060"),
                P(33, "nv_dispig.inf", "NVIDIA", "Display", "31.0.15.3623", 2023, 6, 1_150_000_000, "NVIDIA GeForce RTX 3060"),
                P(27, "nv_dispig.inf", "NVIDIA", "Display", "31.0.15.2849", 2023, 1, 1_090_000_000, "NVIDIA GeForce RTX 3060"),
                P(12, "hdxrt.inf", "Realtek Semiconductor Corp.", "MEDIA", "6.0.9549.1", 2023, 8, 64_000_000, "Realtek High Definition Audio", "Realtek High Definition Audio"),
                P(9, "hdxrt.inf", "Realtek Semiconductor Corp.", "MEDIA", "6.0.9285.1", 2022, 3, 61_000_000, "Realtek High Definition Audio"),
                P(18, "netwtw10.inf", "Intel", "Net", "23.60.1.2", 2024, 5, 38_000_000, "Intel(R) Wi-Fi 6 AX201 160MHz", "Intel(R) Wi-Fi 6 AX201 160MHz"),
                P(7, "hpcu255u.inf", "HP", "Printer", "61.255.1.24923", 2021, 11, 22_000_000, "HP LaserJet Pro M404"),
                P(5, "iaStorVD.inf", "Intel Corporation", "SCSIAdapter", "19.5.1.1040", 2022, 7, 6_000_000, "Intel RST VMD Controller", "Intel RST VMD Controller 9A0B"),
            };
            DriverStoreAnalyzer.Classify(packages);
            Show(packages);
        }
    }
}
