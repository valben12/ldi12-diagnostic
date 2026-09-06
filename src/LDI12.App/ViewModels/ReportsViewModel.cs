using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using LDI12.App.Mvvm;
using LDI12.Actions.Journal;
using LDI12.Core.Diagnostics;
using LDI12.Core.Logging;
using LDI12.Core.Model;
using LDI12.Core.Publishing;
using LDI12.Reports;
using LDI12.Reports.Html;

namespace LDI12.App.ViewModels
{
    /// <summary>
    /// Écran d'export : trois documents, un seul diagnostic.
    /// </summary>
    /// <remarks>
    /// Le nom du technicien et la référence du dossier ne sont pas écrits dans l'instantané mais
    /// portés par un <see cref="ReportContext"/> au moment de l'export. Un instantané décrit une
    /// machine à un instant donné et doit rester intact : c'est ce qui permet de rouvrir un JSON
    /// archivé et de réémettre un rapport, à un autre nom, sans retourner chez le client.
    ///
    /// L'écran affiche ce que chaque document contient avant qu'on l'écrive. Un technicien qui
    /// remet un fichier à un client doit savoir ce qu'il lui donne : en particulier que le
    /// rapport technicien porte des numéros de série et des noms de périphériques, et le bilan
    /// client aucun des deux.
    /// </remarks>
    public sealed class ReportsViewModel : ObservableObject
    {
        private readonly ILdiLogger _logger;
        private readonly InterventionJournal _journal;
        private readonly IReportPublisher _publisher;

        private SystemSnapshot? _snapshot;
        private string _technician = string.Empty;
        private string _clientReference = string.Empty;
        private string _note = string.Empty;
        private string _directory;
        private string _status = "Lancez une analyse pour pouvoir exporter un rapport.";
        private bool _hasResult;
        private bool _isBusy;
        private string? _lastFolder;
        private PublicationRequest? _prepared;
        private string _publicationStatus = string.Empty;

        public ReportsViewModel(ILdiLogger logger, InterventionJournal journal, IReportPublisher publisher)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _journal = journal ?? throw new ArgumentNullException(nameof(journal));
            _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));

            _directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LDI12 Diagnostic");

            ExportAllCommand = new AsyncRelayCommand(
                () => ExportAsync(new[] { ReportKind.Technician, ReportKind.Client, ReportKind.Json }), CanExport);
            ExportTechnicianCommand = new AsyncRelayCommand(
                () => ExportAsync(new[] { ReportKind.Technician }), CanExport);
            ExportClientCommand = new AsyncRelayCommand(
                () => ExportAsync(new[] { ReportKind.Client }), CanExport);
            ExportJsonCommand = new AsyncRelayCommand(
                () => ExportAsync(new[] { ReportKind.Json }), CanExport);
            OpenFolderCommand = new RelayCommand(OpenFolder, () => _lastFolder != null);
            PreparePublicationCommand = new AsyncRelayCommand(PreparePublicationAsync, CanPrepare);
            PublishCommand = new AsyncRelayCommand(PublishAsync, () => _prepared != null && !IsBusy);
            AbandonPublicationCommand = new RelayCommand(AbandonPublication, () => _prepared != null);

            _publicationStatus = _publisher.Configured.Or(false)
                ? "Rien n'est envoyé tant que vous ne l'avez pas demandé."
                : _publisher.Configured.Reason ?? "Aucun service de publication n'est configuré.";
        }

        public ICommand ExportAllCommand { get; }
        public ICommand ExportTechnicianCommand { get; }
        public ICommand ExportClientCommand { get; }
        public ICommand ExportJsonCommand { get; }
        public ICommand OpenFolderCommand { get; }

        /// <summary>Établit ce qui partirait, sans rien envoyer.</summary>
        public ICommand PreparePublicationCommand { get; }

        public ICommand PublishCommand { get; }

        public ICommand AbandonPublicationCommand { get; }

        /// <summary>
        /// Ce qui a été fait sur la machine pendant la session.
        /// </summary>
        /// <remarks>
        /// Affiché ici et non sur les écrans d'action : c'est au moment de remettre les documents
        /// qu'on veut relire l'intervention d'un seul tenant, et c'est là que le technicien
        /// vérifie que ce qu'il s'apprête à donner correspond à ce qu'il a fait.
        /// </remarks>
        public ObservableCollection<JournalItem> JournalEntries { get; } = new ObservableCollection<JournalItem>();

        public bool HasJournal => JournalEntries.Count > 0;

        /// <summary>
        /// Ce qui quitterait la machine, ligne par ligne.
        /// </summary>
        /// <remarks>
        /// Vide tant que rien n'a été préparé, et vidée dès que l'envoi est abandonné : une liste
        /// qui resterait affichée après coup finirait par décrire un envoi qui n'a pas eu lieu.
        /// </remarks>
        public ObservableCollection<string> PublicationLines { get; } = new ObservableCollection<string>();

        public bool HasPublicationPreview => PublicationLines.Count > 0;

        public string PublisherName => _publisher.DisplayName;

        public bool PublisherReady => _publisher.Configured.Or(false);

        public string PublicationStatus
        {
            get => _publicationStatus;
            private set => Set(ref _publicationStatus, value);
        }

        public string JournalPath => _journal.Path ?? "journal non écrit sur disque";

        /// <summary>Relit le journal, appelé quand une action vient de s'exécuter.</summary>
        public void RefreshJournal()
        {
            JournalEntries.Clear();
            foreach (var entry in _journal.Entries) JournalEntries.Add(new JournalItem(entry));

            Raise(nameof(HasJournal));
        }

        public string Technician
        {
            get => _technician;
            set => Set(ref _technician, value);
        }

        public string ClientReference
        {
            get => _clientReference;
            set => Set(ref _clientReference, value);
        }

        public string Note
        {
            get => _note;
            set => Set(ref _note, value);
        }

        public string Directory
        {
            get => _directory;
            set => Set(ref _directory, value);
        }

        public string Status { get => _status; private set => Set(ref _status, value); }
        public bool HasResult { get => _hasResult; private set => Set(ref _hasResult, value); }
        public bool IsBusy { get => _isBusy; private set { if (Set(ref _isBusy, value)) RaiseCommandStates(); } }

        public void Update(SystemSnapshot snapshot)
        {
            _snapshot = snapshot;
            HasResult = true;
            Status = "Diagnostic du " +
                     snapshot.Metadata.CreatedAt.ToString("d MMMM yyyy 'à' HH:mm", CultureInfo.CurrentCulture) +
                     " : " + snapshot.Findings.Count + " constat(s) prêts à être exportés.";
            RaiseCommandStates();
        }

        private bool CanExport() => _snapshot != null && !IsBusy;

        private async Task ExportAsync(IReadOnlyList<ReportKind> kinds)
        {
            var snapshot = _snapshot;
            if (snapshot == null || IsBusy) return;

            var directory = Directory;
            var context = Context();

            IsBusy = true;
            Status = "Génération en cours…";

            try
            {
                // Le rendu d'un rapport technicien complet dépasse la centaine de kilo-octets et
                // parcourt tout l'instantané : hors du fil d'interface, comme la collecte.
                var written = await Task.Run(() =>
                {
                    var paths = new List<string>();
                    foreach (var kind in kinds)
                        paths.Add(ReportExporter.Export(snapshot, kind, directory, context).Path);
                    return paths;
                }).ConfigureAwait(true);

                _lastFolder = directory;
                Status = written.Count == 1
                    ? "Rapport écrit : " + Path.GetFileName(written[0])
                    : written.Count + " fichiers écrits dans " + directory;

                foreach (var path in written) _logger.Info("Reports", "Rapport exporté : " + path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is ArgumentException || ex is NotSupportedException)
            {
                _logger.Error("Reports", "L'export a échoué.", ex);
                Status = "L'export n'a pas abouti : " + ex.Message;
            }
            finally
            {
                IsBusy = false;
                (OpenFolderCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }

        private IReadOnlyList<string> TechnicianLog()
        {
            var lines = new List<string>();
            foreach (var entry in _journal.Entries)
                lines.Add(entry.TimeLabel + " · " + entry.KindLabel + " · " + entry.Title +
                          " (" + entry.ActionId + ") : " + entry.Summary);
            return lines;
        }

        /// <summary>
        /// Version client : uniquement ce qui a réellement modifié la machine.
        /// </summary>
        /// <remarks>
        /// Une prévisualisation n'a rien changé, et l'ouverture d'une console Windows non plus.
        /// Les faire figurer gonflerait la liste de lignes qui donneraient l'impression d'un
        /// travail qui n'a pas eu lieu.
        /// </remarks>
        private IReadOnlyList<string> ClientLog()
        {
            var lines = new List<string>();
            foreach (var entry in _journal.Entries)
            {
                if (entry.Kind != InterventionKind.Execution && entry.Kind != InterventionKind.RestorePoint) continue;

                var line = entry.Title + " : " + entry.Summary;
                if (entry.FreedBytes > 0) line += " (" + ValueFormat.Bytes(entry.FreedBytes) + " libérés)";
                lines.Add(line);
            }
            return lines;
        }

        private bool CanPrepare() => _snapshot != null && !IsBusy && PublisherReady;

        /// <summary>
        /// Prépare l'envoi : rend le document et dresse la liste de ce qui partirait.
        /// </summary>
        /// <remarks>
        /// Deux gestes et non un seul. Le premier ne touche à rien et sert à lire ce qui va
        /// quitter la machine ; le second l'envoie. Un bouton unique demanderait au technicien de
        /// faire confiance à une phrase générale, là où il s'agit des données d'un client.
        /// </remarks>
        private async Task PreparePublicationAsync()
        {
            var snapshot = _snapshot;
            if (snapshot == null || IsBusy) return;

            IsBusy = true;

            try
            {
                var context = Context();

                // Le bilan client, et lui seul. Le rapport technicien porte les comptes Windows,
                // les réseaux sans fil relevés dans le logement et le détail du matériel : c'est
                // le document de l'atelier, il n'a pas à sortir de la machine.
                var html = await Task.Run(() => ReportExporter.Render(snapshot, ReportKind.Client, context))
                    .ConfigureAwait(true);

                _prepared = new PublicationRequest
                {
                    MachineName = snapshot.Machine.MachineName,
                    ClientReference = context.ClientReference,
                    Technician = context.Technician,
                    IssuedAt = DateTimeOffset.Now,
                    Fingerprint = snapshot.Machine.Fingerprint.HasValue ? snapshot.Machine.Fingerprint.Value : null,
                    // Un diagnostic sans note est possible : analyse interrompue, barème absent ;
                    // la publication porte alors le dossier sans chiffre plutôt que zéro.
                    Score = snapshot.Score?.Global,
                    Documents = new[]
                    {
                        new PublicationDocument
                        {
                            FileName = ReportExporter.SuggestFileName(snapshot, ReportKind.Client, context),
                            Title = "Bilan client",
                            MediaType = "text/html",
                            Content = Encoding.UTF8.GetBytes(html),
                        },
                    },
                };

                PublicationLines.Clear();
                foreach (var line in _publisher.DescribeWhatIsSent(_prepared)) PublicationLines.Add(line);

                Raise(nameof(HasPublicationPreview));
                PublicationStatus = "Voici ce qui partirait vers " + _publisher.DisplayName +
                                    ". Rien n'a encore été envoyé.";
            }
            catch (Exception ex)
            {
                _logger.Error("Reports", "La préparation de l'envoi a échoué.", ex);
                PublicationStatus = "L'envoi n'a pas pu être préparé : " + ex.Message;
            }
            finally
            {
                IsBusy = false;
                RaiseCommandStates();
            }
        }

        private async Task PublishAsync()
        {
            var request = _prepared;
            if (request == null || IsBusy) return;

            IsBusy = true;
            PublicationStatus = "Envoi en cours…";

            try
            {
                var progress = new Progress<string>(step => PublicationStatus = step);
                var outcome = await _publisher.PublishAsync(request, progress, CancellationToken.None)
                    .ConfigureAwait(true);

                PublicationStatus = outcome.Summary;

                // Un envoi réussi vide la préparation : la réafficher inviterait à renvoyer deux
                // fois le même dossier sans s'en apercevoir.
                if (outcome.Published) AbandonPublication();
            }
            catch (Exception ex)
            {
                _logger.Error("Reports", "L'envoi a échoué.", ex);
                PublicationStatus = "L'envoi n'a pas abouti : " + ex.Message;
            }
            finally
            {
                IsBusy = false;
                RaiseCommandStates();
            }
        }

        private void AbandonPublication()
        {
            _prepared = null;
            PublicationLines.Clear();
            Raise(nameof(HasPublicationPreview));
            RaiseCommandStates();
        }

        private ReportContext Context() => new ReportContext
        {
            Technician = string.IsNullOrWhiteSpace(Technician) ? null : Technician.Trim(),
            ClientReference = string.IsNullOrWhiteSpace(ClientReference) ? null : ClientReference.Trim(),
            Note = string.IsNullOrWhiteSpace(Note) ? null : Note.Trim(),
            TechnicianLog = TechnicianLog(),
            ClientLog = ClientLog(),
        };

        private void OpenFolder()
        {
            if (_lastFolder == null) return;

            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + _lastFolder + "\"")
                {
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                _logger.Error("Reports", "Le dossier n'a pas pu être ouvert.", ex);
                Status = "Le dossier n'a pas pu être ouvert : " + ex.Message;
            }
        }

        private void RaiseCommandStates()
        {
            (ExportAllCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (ExportTechnicianCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (ExportClientCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (ExportJsonCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (PreparePublicationCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (PublishCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (AbandonPublicationCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }
}
