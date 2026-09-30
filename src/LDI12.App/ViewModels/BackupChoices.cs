using System;
using System.Windows.Input;
using LDI12.Actions.Backup;
using LDI12.App.Mvvm;

namespace LDI12.App.ViewModels
{
    /// <summary>Un volume branché, tel qu'il s'affiche en tuile.</summary>
    public sealed class DriveTile : ObservableObject
    {
        private bool _isSelected;

        public DriveTile(DestinationDrive drive, Action<DriveTile> select)
        {
            Drive = drive ?? throw new ArgumentNullException(nameof(drive));
            if (select == null) throw new ArgumentNullException(nameof(select));
            SelectCommand = new RelayCommand(() => select(this));
        }

        public DestinationDrive Drive { get; }

        public string Root => Drive.Root;
        public string Title => Drive.Title;
        public string KindText => Drive.KindText;
        public string SpaceText => Drive.SpaceText;
        public string BackupsText => Drive.BackupsText;
        public string? ResumableText => Drive.ResumableText;
        public bool HasResumable => Drive.HasResumable;

        /// <summary>
        /// Ce qu'il faut savoir avant de choisir ce volume, ou nul.
        /// </summary>
        /// <remarks>
        /// Dit sur la tuile plutôt qu'au compte rendu : c'est au moment du choix que l'information
        /// sert, pas une heure plus tard.
        /// </remarks>
        public string? Warning => Drive.IsFat32
            ? "FAT32 : aucun fichier de 4 Go ou plus ne peut y être copié."
            : Drive.Kind == DestinationKind.Network
                ? "Lecteur réseau : l'export des pilotes, fait en administrateur, ne le voit pas."
                : null;

        public bool HasWarning => Warning != null;

        public ICommand SelectCommand { get; }

        public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

        private string? _speedText;
        private string? _speedAdvice;

        /// <summary>La vitesse mesurée de ce support, ou nul tant qu'il n'a pas été mesuré.</summary>
        public string? SpeedText
        {
            get => _speedText;
            set { if (Set(ref _speedText, value)) Raise(nameof(HasSpeed)); }
        }

        public bool HasSpeed => _speedText != null;

        /// <summary>Le conseil qui accompagne un support lent.</summary>
        public string? SpeedAdvice
        {
            get => _speedAdvice;
            set { if (Set(ref _speedAdvice, value)) Raise(nameof(HasSpeedAdvice)); }
        }

        public bool HasSpeedAdvice => _speedAdvice != null;
    }

    /// <summary>Un dossier ajouté à la main, et de quoi le retirer.</summary>
    public sealed class ExtraFolderItem
    {
        public ExtraFolderItem(string path, Action<ExtraFolderItem> remove)
        {
            Path = path;
            RemoveCommand = new RelayCommand(() => remove(this));
        }

        public string Path { get; }

        public ICommand RemoveCommand { get; }
    }

    /// <summary>Une case d'une liste : un pilote, une application.</summary>
    public sealed class ChoiceItem : ObservableObject
    {
        private readonly Action _changed;
        private bool _isChecked;

        public ChoiceItem(string key, string title, string? detail, bool isChecked, Action changed)
        {
            Key = key;
            Title = title;
            Detail = detail;
            _isChecked = isChecked;
            _changed = changed ?? throw new ArgumentNullException(nameof(changed));
        }

        public string Key { get; }
        public string Title { get; }
        public string? Detail { get; }
        public bool HasDetail => !string.IsNullOrEmpty(Detail);

        /// <summary>Le paquet de pilote qu'elle représente, pour les pilotes.</summary>
        public DriverChoice? Driver { get; init; }

        public bool IsChecked
        {
            get => _isChecked;
            set { if (Set(ref _isChecked, value)) _changed(); }
        }

        /// <summary>Coche ou décoche sans prévenir : pour « tout cocher », qui prévient une seule fois.</summary>
        internal void SetSilently(bool value) => Set(ref _isChecked, value, nameof(IsChecked));
    }
}
