using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows.Input;
using LDI12.Actions;
using LDI12.Actions.Footprint;
using LDI12.Actions.Tools;
using LDI12.App.Mvvm;
using LDI12.Collectors;
using LDI12.Core.Logging;
using LDI12.Core.Runtime;
using LDI12.Engine.Profile;
using LDI12.Engine.Rules;

namespace LDI12.App.ViewModels
{
    /// <summary>Une ligne « étiquette / valeur » de l'écran, éventuellement suivie d'une précision.</summary>
    public sealed class AboutLine
    {
        public AboutLine(string label, string value, string? note = null)
        {
            Label = label;
            Value = value;
            Note = note;
        }

        public string Label { get; }

        public string Value { get; }

        public string? Note { get; }

        public bool HasNote => !string.IsNullOrWhiteSpace(Note);
    }

    /// <summary>
    /// Qui édite ce logiciel, ce qu'il est, et ce qu'il sait faire.
    /// </summary>
    /// <remarks>
    /// <b>Les chiffres sont comptés, pas recopiés.</b> Le nombre de sondes, de règles, d'actions,
    /// de consoles et de seuils vient des catalogues eux-mêmes : un écran qui annoncerait
    /// « 92 règles » six versions plus tard serait un mensonge poli, et personne ne penserait à le
    /// corriger. C'est la même raison qui met la liste des composants tiers dans le noyau, où un
    /// test la relit.
    /// </remarks>
    public sealed class AboutViewModel : ObservableObject
    {
        private const string Category = "About";

        private readonly ILdiLogger? _logger;

        public AboutViewModel(ILdiLogger? logger = null)
        {
            _logger = logger;

            OpenSiteCommand = new RelayCommand(() => Open(Vendor.Site));
            OpenMailCommand = new RelayCommand(() => Open("mailto:" + Vendor.Contact));

            Software = BuildSoftware();
            Coverage = BuildCoverage();
            Storage = BuildStorage();
        }

        // ---------------------------------------------------------------- l'éditeur

        public string Company { get; } = Attribute<AssemblyCompanyAttribute>(a => a.Company)
                                         ?? "Laguiole Dépannage Informatique";

        public string Product { get; } = Attribute<AssemblyProductAttribute>(a => a.Product) ?? "LDI12 Diagnostic";

        public string Activity => Vendor.Activity;

        public string Area => Vendor.Area;

        public string Site => Vendor.Site;

        public string Contact => Vendor.Contact;

        public ICommand OpenSiteCommand { get; }

        public ICommand OpenMailCommand { get; }

        // ---------------------------------------------------------------- le logiciel

        public string Version { get; } = ReadVersion();

        public string Copyright => Credits.Copyright;

        public string Terms => Credits.Terms;

        public IReadOnlyList<AboutLine> Software { get; }

        public IReadOnlyList<AboutLine> Coverage { get; }

        public IReadOnlyList<AboutLine> Storage { get; }

        public IReadOnlyList<Component> Components => Credits.Components;

        private static IReadOnlyList<AboutLine> BuildSoftware()
        {
            var assembly = Assembly.GetExecutingAssembly();

            return new[]
            {
                new AboutLine("Version", ReadVersion(),
                    "Le suffixe après le « + » est l'empreinte du code publié : c'est lui qui permet de " +
                    "savoir, six mois plus tard, quelle version exacte a produit un rapport."),
                new AboutLine("Fichier daté du", BuildDate(assembly),
                    "Date du fichier sur cette machine. L'horodatage inscrit dans l'exécutable n'en est " +
                    "plus une depuis que la compilation est déterministe : c'est une empreinte du contenu."),
                new AboutLine("Plateforme", ".NET Framework 4.6.2 : " + (Environment.Is64BitProcess ? "64 bits" : "32 bits"),
                    "Windows 7 SP1 à Windows 11, sans rien à installer."),
                new AboutLine("Fonctionnement", "Hors ligne",
                    "Aucune donnée ne quitte la machine. Les seuls accès réseau sont les tests de " +
                    "connectivité, qui sont eux-mêmes le sujet de la mesure."),
            };
        }

        /// <summary>Ce que le logiciel sait faire, compté dans les catalogues au moment de l'affichage.</summary>
        private static IReadOnlyList<AboutLine> BuildCoverage()
        {
            var lines = new List<AboutLine>();

            Safe(lines, "Modules de collecte", () => CollectorCatalog.CreateAll().Count.ToString(CultureInfo.CurrentCulture),
                "Chacun est débrayable dans les réglages, et son absence apparaît dans le rapport.");

            Safe(lines, "Règles de diagnostic", () => RuleCatalog.All().Count.ToString(CultureInfo.CurrentCulture),
                "Une règle qui n'a pas pu conclure le dit, et ne retire aucun point.");

            Safe(lines, "Actions", () => ActionCatalog.CreateAll().Count.ToString(CultureInfo.CurrentCulture),
                "Réparation, maintenance et sauvegarde. Chacune montre ce qu'elle va faire avant de le faire.");

            Safe(lines, "Consoles Windows", () => WindowsToolCatalog.All.Count.ToString(CultureInfo.CurrentCulture),
                "Ouvertes telles quelles : le logiciel ne se substitue pas aux outils de Windows.");

            Safe(lines, "Seuils réglables", () => ThresholdCatalog.All.Count.ToString(CultureInfo.CurrentCulture),
                "Tous y figurent, sans exception : c'est le technicien qui répond au client.");

            return lines;
        }

        /// <summary>Où le logiciel écrit. La même réponse que l'écran des réglages, au même endroit.</summary>
        private static IReadOnlyList<AboutLine> BuildStorage()
        {
            string root;
            try
            {
                root = FootprintScanner.DefaultRoot();
            }
            catch (Exception)
            {
                return Array.Empty<AboutLine>();
            }

            return new[]
            {
                new AboutLine("Dossier de travail", root,
                    "Journaux, barème ajusté, diagnostics archivés et journal d'intervention. " +
                    "Rien dans la base de registre, aucun service, aucune tâche planifiée."),
                new AboutLine("Effacement", "Réglages → Traces de ce logiciel",
                    "Le relevé montre ce qui serait supprimé avant de supprimer quoi que ce soit."),
            };
        }

        private static void Safe(ICollection<AboutLine> lines, string label, Func<string> read, string note)
        {
            try
            {
                lines.Add(new AboutLine(label, read(), note));
            }
            catch (Exception)
            {
                // Un écran de présentation qui tombe serait le comble : une ligne manquante vaut
                // mieux qu'une page blanche.
            }
        }

        private static string ReadVersion()
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                var informational = assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

                if (!string.IsNullOrWhiteSpace(informational)) return informational!;

                var version = assembly.GetName().Version;
                return version == null ? "version inconnue" : version.ToString(3);
            }
            catch (Exception)
            {
                return "version inconnue";
            }
        }

        /// <summary>
        /// Date de compilation, lue dans l'en-tête PE.
        /// </summary>
        /// <remarks>
        /// L'horodatage de l'en-tête est déterministe depuis Roslyn : ce n'est plus une date mais
        /// une empreinte du contenu. La date du fichier sur le disque est donc plus honnête ici :
        /// elle dit quand cet exécutable-là est arrivé sur cette machine.
        /// </remarks>
        private static string BuildDate(Assembly assembly)
        {
            try
            {
                var path = assembly.Location;
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "inconnue";

                return File.GetLastWriteTime(path).ToString("d MMMM yyyy", CultureInfo.CurrentCulture);
            }
            catch (Exception)
            {
                return "inconnue";
            }
        }

        private static string? Attribute<T>(Func<T, string> read) where T : Attribute
        {
            try
            {
                var attribute = Assembly.GetExecutingAssembly().GetCustomAttribute<T>();
                return attribute == null ? null : read(attribute);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Ouvre le site ou le courrier dans l'application par défaut de la machine.
        /// </summary>
        /// <remarks>
        /// Sans passer par la passerelle de processus, contrairement à tout le reste : ce qui est
        /// lancé ici est une <b>constante du programme</b>, pas une valeur relevée sur la machine.
        /// La passerelle existe pour encadrer ce qui vient de l'extérieur ; il n'y a rien à
        /// encadrer dans une adresse écrite dans le code, et rien à consigner au journal
        /// d'intervention : ouvrir un site n'est pas une opération faite sur la machine du client.
        /// </remarks>
        private void Open(string target)
        {
            try
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _logger?.For(Category).Warn("Ouverture impossible de " + target + ".", ex);
            }
        }
    }
}
