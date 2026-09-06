using System;
using System.Collections.Generic;
using System.Globalization;
using LDI12.Core.Platform;

namespace LDI12.Core.Model
{
    /// <summary>Ce que le calendrier de Microsoft dit de la version installée.</summary>
    public enum WindowsSupportState
    {
        /// <summary>Version absente du calendrier embarqué : rien n'est affirmé.</summary>
        Unknown = 0,

        Supported = 1,

        Ended = 2,
    }

    /// <summary>État de support de la version de Windows installée, à la date du relevé.</summary>
    public sealed class SupportStatus
    {
        public WindowsSupportState State { get; init; }

        /// <summary>Ce qui est installé, tel qu'on le dirait au client : « Windows 10 22H2 ».</summary>
        public string VersionLabel { get; init; } = string.Empty;

        public DateTimeOffset? EndOfSupport { get; init; }

        /// <summary>Jours restants à la date du relevé. Négatif une fois la date passée.</summary>
        public int? DaysRemaining { get; init; }

        /// <summary>Renseigné quand la date n'est pas connue, ou quand elle mérite une précision.</summary>
        public string? Reason { get; init; }
    }

    /// <summary>
    /// Calendrier de fin de support des versions de Windows.
    /// </summary>
    /// <remarks>
    /// <b>Une table figée, relue à une date qui est écrite ici.</b> Les dates de fin de support
    /// sont publiées par Microsoft et ne changent plus une fois passées ; les embarquer permet de
    /// répondre sans réseau, ce qui est la règle de ce logiciel. Le prix à payer est qu'une
    /// version sortie après la relecture n'y figure pas : elle est alors rapportée comme
    /// <see cref="WindowsSupportState.Unknown"/>, avec la date de relecture dans le motif. Deviner
    /// serait pire : un « encore suivi » inventé rassurerait à tort le technicien, et un « fini »
    /// inventé ferait réinstaller une machine qui n'en avait pas besoin.
    /// <para>
    /// Les éditions Entreprise et Éducation ont leurs propres dates, plus tardives d'un an sur
    /// Windows 11. Les confondre ferait annoncer un support terminé sur des machines qui reçoivent
    /// encore des correctifs.
    /// </para>
    /// </remarks>
    public static class WindowsLifecycle
    {
        /// <summary>Date à laquelle cette table a été relue. Citée telle quelle quand une version manque.</summary>
        public static readonly DateTime CheckedOn = new DateTime(2026, 9, 6);

        /// <summary>Fin de support de la dernière branche de Windows 10.</summary>
        public static readonly DateTime Windows10End = new DateTime(2025, 10, 14);

        public static SupportStatus For(WindowsProfile windows, DateTimeOffset reference)
        {
            if (windows == null) throw new ArgumentNullException(nameof(windows));

            var label = Label(windows);

            switch (windows.Family)
            {
                case WindowsFamily.Unsupported:
                    return new SupportStatus
                    {
                        State = WindowsSupportState.Ended,
                        VersionLabel = label,
                        Reason = "Cette version est antérieure à Windows 7 SP1 : son support est terminé depuis " +
                                 "des années.",
                    };

                case WindowsFamily.Windows7:
                    return At(label, new DateTime(2020, 1, 14), reference);

                case WindowsFamily.Windows8:
                    return At(label, new DateTime(2016, 1, 12), reference,
                        "Windows 8 a cessé d'être suivi dès la sortie de 8.1, qui était une mise à jour gratuite.");

                case WindowsFamily.Windows81:
                    return At(label, new DateTime(2023, 1, 10), reference);

                case WindowsFamily.Windows10:
                    return Ten(windows, label, reference);

                case WindowsFamily.Windows11:
                    return Eleven(windows, label, reference);

                default:
                    return new SupportStatus
                    {
                        State = WindowsSupportState.Unknown,
                        VersionLabel = label,
                        Reason = "La version de Windows n'a pas pu être déterminée.",
                    };
            }
        }

        /// <summary>Vrai pour les éditions dont le cycle de vie est plus long d'un an.</summary>
        public static bool IsLongCycleEdition(string? edition)
        {
            if (string.IsNullOrWhiteSpace(edition)) return false;

            var text = edition!.ToUpperInvariant();
            return text.Contains("ENTERPRISE") || text.Contains("EDUCATION");
        }

        private static SupportStatus Ten(WindowsProfile windows, string label, DateTimeOffset reference)
        {
            // 22H2 est la dernière branche de Windows 10 ; toutes les autres se sont arrêtées avant
            // elle, quelle que soit l'édition. Nommer la date propre à chacune demanderait une table
            // de douze lignes pour une précision dont personne n'a l'usage : ce qui compte est que
            // le support est fini, et depuis quand au plus tard.
            var version = Normalize(windows.DisplayVersion) ?? Normalize(windows.ReleaseId);
            if (string.Equals(version, "22H2", StringComparison.Ordinal))
                return At(label, Windows10End, reference);

            return new SupportStatus
            {
                State = WindowsSupportState.Ended,
                VersionLabel = label,
                EndOfSupport = new DateTimeOffset(Windows10End, reference.Offset),
                DaysRemaining = Days(Windows10End, reference),
                Reason = "Cette branche de Windows 10 a cessé d'être suivie avant le 14 octobre 2025, date à " +
                         "laquelle la dernière (22H2) s'est arrêtée à son tour.",
            };
        }

        private static SupportStatus Eleven(WindowsProfile windows, string label, DateTimeOffset reference)
        {
            var version = Normalize(windows.DisplayVersion);
            var longCycle = IsLongCycleEdition(windows.EditionId);

            DateTime? end;
            switch (version)
            {
                case "21H2": end = longCycle ? new DateTime(2024, 10, 8) : new DateTime(2023, 10, 10); break;
                case "22H2": end = longCycle ? new DateTime(2025, 10, 14) : new DateTime(2024, 10, 8); break;
                case "23H2": end = longCycle ? new DateTime(2026, 11, 10) : new DateTime(2025, 11, 11); break;
                case "24H2": end = longCycle ? new DateTime(2027, 10, 12) : new DateTime(2026, 10, 13); break;
                case "25H2": end = longCycle ? new DateTime(2028, 10, 10) : new DateTime(2027, 10, 12); break;
                default: end = null; break;
            }

            if (end == null)
                return new SupportStatus
                {
                    State = WindowsSupportState.Unknown,
                    VersionLabel = label,
                    Reason = "La version « " + (version ?? "inconnue") + " » ne figure pas au calendrier embarqué " +
                             "dans cette version du logiciel, relu le " + Fr(CheckedOn) + ". Sa date de fin de " +
                             "support se vérifie chez Microsoft.",
                };

            return At(label, end.Value, reference);
        }

        private static SupportStatus At(string label, DateTime end, DateTimeOffset reference, string? reason = null)
        {
            var days = Days(end, reference);

            return new SupportStatus
            {
                // Le jour même, la version est encore suivie : le support se termine à la fin de
                // cette journée-là, pas à son début.
                State = days < 0 ? WindowsSupportState.Ended : WindowsSupportState.Supported,
                VersionLabel = label,
                EndOfSupport = new DateTimeOffset(end, reference.Offset),
                DaysRemaining = days,
                Reason = reason,
            };
        }

        /// <summary>Jours séparant le relevé de la fin de support. L'heure du relevé ne compte pas.</summary>
        private static int Days(DateTime end, DateTimeOffset reference)
            => (int)(end.Date - reference.Date).TotalDays;

        private static string Label(WindowsProfile windows)
        {
            var family = WindowsProfile.FamilyLabel(windows.Family);
            var version = Normalize(windows.DisplayVersion) ?? Normalize(windows.ReleaseId);
            return version == null ? family : family + " " + version;
        }

        private static string? Normalize(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            return value!.Trim().ToUpperInvariant();
        }

        private static string Fr(DateTime value) => value.ToString("d MMMM yyyy", new CultureInfo("fr-FR"));
    }

    /// <summary>Ce qu'on peut dire d'une exigence, une fois la machine mesurée.</summary>
    public enum RequirementOutcome
    {
        /// <summary>La mesure n'a pas pu être faite : rien n'est conclu.</summary>
        Unknown = 0,

        Met = 1,

        /// <summary>Le matériel en est capable ; un réglage l'en empêche.</summary>
        Fixable = 2,

        /// <summary>Le matériel ne suffit pas : aucun réglage n'y changera rien.</summary>
        NotMet = 3,

        /// <summary>Mesure faite, mais elle ne permet pas de trancher.</summary>
        Undetermined = 4,
    }

    /// <summary>Une exigence de Windows 11, et ce que cette machine en dit.</summary>
    public sealed class UpgradeRequirement
    {
        public string Id { get; init; } = string.Empty;

        public string Label { get; init; } = string.Empty;

        /// <summary>Ce que Microsoft demande, en clair.</summary>
        public string Expectation { get; init; } = string.Empty;

        public RequirementOutcome Outcome { get; init; }

        /// <summary>Ce qui a été relevé, en quelques mots, ou, faute de mesure, pourquoi on ne sait pas.</summary>
        public string Observation { get; init; } = string.Empty;

        /// <summary>
        /// Ce qu'il faut savoir en plus : ce qui lèverait le blocage, ou la limite de ce qui est
        /// affirmé. Absent quand le constat se suffit à lui-même.
        /// </summary>
        public string? Note { get; init; }
    }

    /// <summary>
    /// Ce que la génération du processeur laisse penser de la liste officielle.
    /// </summary>
    /// <remarks>
    /// Une indication, jamais un verdict : la liste de Microsoft comporte des exceptions dans les
    /// deux sens, et elle n'est pas embarquable, plus de mille références, révisées au fil des
    /// sorties.
    /// </remarks>
    public enum CpuListIndication
    {
        Unknown = 0,
        LikelyListed = 1,
        LikelyNotListed = 2,
    }

    public enum UpgradeVerdict
    {
        Unknown = 0,

        /// <summary>La question ne se pose pas : édition serveur.</summary>
        NotApplicable = 1,

        AlreadyThere = 2,

        Eligible = 3,

        /// <summary>Rien à changer dans la machine : un ou plusieurs réglages suffisent.</summary>
        EligibleAfterSetting = 4,

        Ineligible = 5,

        /// <summary>Une exigence au moins n'a pas pu être mesurée.</summary>
        Undetermined = 6,
    }

    /// <summary>Réponse à « cette machine peut-elle passer à Windows 11 ? », exigence par exigence.</summary>
    public sealed class UpgradeAssessment
    {
        public UpgradeVerdict Verdict { get; init; }

        public IReadOnlyList<UpgradeRequirement> Requirements { get; init; } = Array.Empty<UpgradeRequirement>();

        public CpuListIndication CpuIndication { get; init; }

        /// <summary>Pourquoi il n'y a rien à évaluer, quand c'est le cas.</summary>
        public string? Note { get; init; }

        public IReadOnlyList<UpgradeRequirement> With(RequirementOutcome outcome)
        {
            var kept = new List<UpgradeRequirement>();
            foreach (var requirement in Requirements)
                if (requirement.Outcome == outcome) kept.Add(requirement);
            return kept;
        }
    }
}
