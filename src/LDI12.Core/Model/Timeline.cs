using System;
using System.Collections.Generic;

namespace LDI12.Core.Model
{
    /// <summary>Ce qu'une ligne de la chronologie raconte.</summary>
    public enum TimelineKind
    {
        Unknown = 0,

        // --- incidents : ce qui s'est mal passé
        ProgramCrash = 1,
        ProgramHang = 2,
        ServiceCrash = 3,
        BlueScreen = 4,
        HardwareError = 5,

        // --- changements : ce qui a été fait à la machine
        WindowsInstalled = 10,
        UpdateInstalled = 11,
        /// <summary>Un logiciel installé, ou mis à jour, ce que le registre ne distingue pas.</summary>
        /// <remarks>
        /// La date vient du champ que l'installeur écrit lui-même, et les logiciels qui se
        /// mettent à jour tout seuls le réécrivent à chaque fois : un navigateur y porte la date
        /// de sa dernière mise à jour, pas celle de son installation. Le libellé dit donc les
        /// deux plutôt que d'affirmer le mauvais.
        /// </remarks>
        SoftwareInstalled = 12,
    }

    /// <summary>Une ligne de la chronologie : une date, une nature, un sujet.</summary>
    public sealed class TimelineEntry
    {
        public DateTimeOffset Date { get; init; }

        public TimelineKind Kind { get; init; }

        public string Subject { get; init; } = string.Empty;

        /// <summary>Un incident subi, par opposition à un changement apporté.</summary>
        public bool IsIncident => Kind >= TimelineKind.ProgramCrash && Kind <= TimelineKind.HardwareError;
    }

    /// <summary>
    /// L'histoire récente de la machine : ce qu'elle a subi et ce qu'on lui a fait, mêlés.
    /// </summary>
    /// <remarks>
    /// <b>Les deux ne sont mêlés que pour être lus côte à côte, jamais pour conclure.</b> Voir
    /// qu'une mise à jour précède de deux jours le début d'une série de plantages est une piste,
    /// et une piste vaut d'être montrée à quelqu'un qui saura la vérifier. Ce n'est pas une
    /// cause, et rien dans ce logiciel ne l'écrira comme telle : c'est précisément parce que la
    /// tentation est forte que la règle est posée ici, dans le type lui-même.
    /// <para>
    /// Les entrées sont rangées de la plus récente à la plus ancienne, l'ordre dans lequel la
    /// question se pose.
    /// </para>
    /// </remarks>
    public sealed class Timeline
    {
        public static readonly Timeline Empty = new Timeline();

        /// <summary>Instant de référence : la date du relevé, jamais l'heure qu'il est.</summary>
        /// <remarks>
        /// Un diagnostic rouvert trois mois plus tard doit raconter la même histoire que le jour
        /// où il a été pris. Lire l'horloge ici ferait vieillir les constats archivés.
        /// </remarks>
        public DateTimeOffset ReferenceDate { get; init; }

        public IReadOnlyList<TimelineEntry> Entries { get; init; } = Array.Empty<TimelineEntry>();

        /// <summary>
        /// Début de la période réellement couverte, au-delà de laquelle l'absence d'incident ne
        /// prouve rien.
        /// </summary>
        public DateTimeOffset? CoveredSince { get; init; }

        public int Count(bool incidents, DateTimeOffset from, DateTimeOffset to)
        {
            var count = 0;
            foreach (var entry in Entries)
                if (entry.IsIncident == incidents && entry.Date >= from && entry.Date < to)
                    count++;
            return count;
        }

        /// <summary>Les changements survenus dans la fenêtre donnée, du plus récent au plus ancien.</summary>
        public IReadOnlyList<TimelineEntry> ChangesBetween(DateTimeOffset from, DateTimeOffset to)
        {
            var changes = new List<TimelineEntry>();
            foreach (var entry in Entries)
                if (!entry.IsIncident && entry.Date >= from && entry.Date < to)
                    changes.Add(entry);
            return changes;
        }
    }

    /// <summary>
    /// Assemble la chronologie à partir de ce qui a déjà été mesuré.
    /// </summary>
    /// <remarks>
    /// <b>Aucune mesure nouvelle : une projection, et rien d'autre.</b> Tout ce qui figure ici a
    /// été relevé par une sonde et porte déjà sa date. C'est la raison pour laquelle cette classe
    /// est dans le noyau plutôt que dans le moteur : le moteur de règles et la fiche de faits en
    /// ont besoin tous les deux, et deux assemblages parallèles finiraient par diverger : le
    /// technicien lirait alors une chronologie dans l'écran et une autre dans le rapport.
    /// <para>
    /// Les pilotes n'y figurent pas. La seule date que Windows en donne est celle de leur
    /// publication par l'éditeur, pas celle de leur installation sur cette machine : la porter
    /// dans une chronologie reviendrait à dater un changement de plusieurs années avant qu'il
    /// n'ait eu lieu.
    /// </para>
    /// </remarks>
    public static class TimelineBuilder
    {
        /// <summary>Au-delà, la chronologie cesse d'être lisible et le rapport, transportable.</summary>
        private const int MaxEntries = 400;

        public static Timeline Build(SystemSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));

            var reference = snapshot.Metadata.CreatedAt;
            var stability = snapshot.Windows.Stability;

            // La fenêtre est celle de la lecture des journaux : au-delà, les changements seraient
            // affichés sans les incidents correspondants, ce qui se lirait comme une accalmie.
            var windowDays = stability.WindowDays.IsReliable ? stability.WindowDays.Value : 90;
            var floor = reference.AddDays(-windowDays);

            var entries = new List<TimelineEntry>();

            foreach (var incident in stability.Incidents)
                Add(entries, floor, reference, incident.Date, Map(incident.Kind), incident.Subject);

            foreach (var bsod in snapshot.Windows.Events.Bsods)
                Add(entries, floor, reference, bsod.Date, TimelineKind.BlueScreen, "Écran bleu");

            // Seules les erreurs non corrigées : une erreur corrigée est un incident pour la
            // machine, pas pour l'utilisateur, et elle noierait la chronologie par milliers.
            foreach (var group in snapshot.Hardware.Errors.Errors)
                if (group.Kind == HardwareErrorKind.Uncorrected)
                    Add(entries, floor, reference, group.LastSeen, TimelineKind.HardwareError,
                        group.Sample ?? "Erreur matérielle");

            foreach (var update in snapshot.Windows.Updates.Installed)
                Add(entries, floor, reference, update.InstalledOn, TimelineKind.UpdateInstalled, update.Identifier);

            foreach (var program in snapshot.Windows.Software.Programs)
                if (program.InstalledOn.IsReliable)
                    Add(entries, floor, reference, program.InstalledOn.Value,
                        TimelineKind.SoftwareInstalled, program.Name);

            var install = snapshot.Windows.Install.InstallDate;
            if (install.IsReliable)
                Add(entries, floor, reference, install.Value, TimelineKind.WindowsInstalled, "Installation de Windows");

            entries.Sort((a, b) => b.Date.CompareTo(a.Date));
            if (entries.Count > MaxEntries) entries.RemoveRange(MaxEntries, entries.Count - MaxEntries);

            return new Timeline
            {
                ReferenceDate = reference,
                Entries = entries,
                CoveredSince = Covered(stability, floor),
            };
        }

        /// <summary>
        /// Depuis quand les journaux disent réellement quelque chose.
        /// </summary>
        /// <remarks>
        /// Le plus ancien incident lisible, quand il est postérieur à la fenêtre demandée : le
        /// journal a alors été purgé, ou la machine est plus jeune que la fenêtre. Sans cette
        /// borne, « aucun incident jusqu'au 20 août » se lirait comme une période calme alors que
        /// c'est une période sans archive.
        /// </remarks>
        private static DateTimeOffset? Covered(StabilityInfo stability, DateTimeOffset floor)
        {
            if (!stability.OldestEntry.IsReliable) return null;
            return stability.OldestEntry.Value > floor ? stability.OldestEntry.Value : floor;
        }

        private static void Add(
            ICollection<TimelineEntry> entries, DateTimeOffset floor, DateTimeOffset reference,
            DateTimeOffset date, TimelineKind kind, string subject)
        {
            // Une date postérieure au relevé existe : horloge remise à l'heure, fuseau, ou date
            // d'installation écrite de travers par un installeur. Elle n'est pas corrigée, elle
            // est écartée : la placer dans le futur de sa propre chronologie ne dit rien.
            if (date < floor || date > reference) return;

            entries.Add(new TimelineEntry
            {
                Date = date,
                Kind = kind,
                Subject = string.IsNullOrWhiteSpace(subject) ? "(sans nom)" : subject,
            });
        }

        private static TimelineKind Map(IncidentKind kind) => kind switch
        {
            IncidentKind.ProgramCrash => TimelineKind.ProgramCrash,
            IncidentKind.ProgramHang => TimelineKind.ProgramHang,
            IncidentKind.ServiceCrash => TimelineKind.ServiceCrash,
            IncidentKind.BlueScreen => TimelineKind.BlueScreen,
            IncidentKind.HardwareError => TimelineKind.HardwareError,
            _ => TimelineKind.Unknown,
        };
    }
}
