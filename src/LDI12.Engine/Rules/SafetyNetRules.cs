using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Engine.Recommendations;

namespace LDI12.Engine.Rules
{
    /// <summary>
    /// Règles portant sur ce qui permettrait de revenir en arrière.
    /// </summary>
    /// <remarks>
    /// <b>Ces constats se lisent avant d'intervenir, pas après.</b> Ils ne décrivent pas une
    /// panne : ils décrivent ce qui se passera si l'intervention se passe mal. Une machine sans
    /// point de restauration, sans sauvegarde et dont les documents sont sur le disque qu'on
    /// s'apprête à reformater n'est pas en panne : elle est simplement une machine sur laquelle
    /// on ne travaille pas de la même façon.
    /// </remarks>
    internal static class SafetyNetRules
    {
        private const DiagnosticCategory Cat = DiagnosticCategory.Windows;
        private const string Family = "safety.net";

        public static IEnumerable<IRule> All()
        {
            yield return new Rule("SAF-001", "Points de restauration en échec", Cat, RestoreFailing, Family);
            yield return new Rule("SAF-002", "Aucun retour en arrière possible", Cat, NoSafetyNet, Family);
            yield return new Rule("SAF-003", "Dossier personnel introuvable", Cat, MissingFolder, Family);
            yield return new Rule("SAF-004", "Récupération de Windows indisponible", Cat, NoRecovery, Family);
        }

        /// <summary>
        /// La protection est activée mais ne crée plus rien.
        /// </summary>
        /// <remarks>
        /// Le pire des cas, parce qu'il est invisible : la case reste cochée dans les réglages de
        /// Windows, le technicien croit être couvert, et c'est le point de restauration créé par
        /// ce logiciel avant une réparation qui échouera, au moment le plus tardif possible.
        /// </remarks>
        private static RuleResult RestoreFailing(RuleContext c)
        {
            var restore = c.System.SafetyNet.Restore;
            if (!restore.Failures.IsReliable)
                return RuleResult.NotEvaluated(
                    restore.Failures.Reason ?? "L'activité de la protection du système n'a pas pu être lue.");

            if (restore.Failures.Value == 0) return RuleResult.Clean;

            var severity = restore.PointsCreated.Or(0) == 0 ? Severity.Problem : Severity.Warning;

            return RuleResult.Of(c.Finding(severity,
                "La création de points de restauration échoue",
                restore.Failures.Value + " échec(s) sur les " + restore.WindowDays.Or(90) + " derniers jours, pour " +
                restore.PointsCreated.Or(0) + " point(s) créé(s).",
                restore.PointsCreated.Or(0) == 0
                    ? "La protection du système est présentée comme active, mais elle n'arrive plus à créer le " +
                      "moindre point de restauration. Le filet de sécurité de Windows n'existe donc plus sur cette " +
                      "machine, alors que ses réglages affirment le contraire. C'est à corriger avant toute " +
                      "intervention qui modifie le système."
                    : "Certaines créations de points de restauration échouent. Le filet fonctionne encore par " +
                      "moments, mais on ne peut pas compter dessus au moment où l'on en aurait besoin.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Échecs de création", restore.Failures.Value.ToString(), DataSource.EventLog, "0")),
                recommendations: RuleContext.Rec(Rec.EnableSystemRestore)));
        }

        /// <summary>
        /// Rien ne permettrait de revenir en arrière.
        /// </summary>
        /// <remarks>
        /// Trois absences simultanées, et une seule d'entre elles ne dirait rien : beaucoup de
        /// machines vivent très bien sans point de restauration parce que les documents sont
        /// ailleurs, et beaucoup d'autres n'ont pas de sauvegarde mais gardent un filet système
        /// intact. C'est la conjonction qui compte, et c'est pourquoi ce constat existe en plus
        /// de celui qui signale la protection désactivée.
        /// </remarks>
        private static RuleResult NoSafetyNet(RuleContext c)
        {
            var net = c.System.SafetyNet;

            if (!net.Restore.PointsCreated.IsReliable)
                return RuleResult.NotEvaluated(
                    net.Restore.PointsCreated.Reason ?? "L'activité de la protection du système n'a pas pu être lue.");

            if (net.Folders.Count == 0)
                return RuleResult.NotEvaluated("L'emplacement des dossiers personnels n'a pas pu être lu.");

            var points = net.Restore.PointsCreated.Value > 0 && net.Restore.Enabled.Or(true);
            if (points || net.Backups.Count > 0) return RuleResult.Clean;

            var exposed = new List<string>();
            foreach (var folder in net.Folders)
                if (folder.OnSystemVolume && !folder.InCloud)
                    exposed.Add(Label(folder.Kind));

            if (exposed.Count == 0) return RuleResult.Clean;

            return RuleResult.Of(c.Finding(Severity.Warning,
                "Rien ne permettrait de revenir en arrière",
                "Aucun point de restauration créé sur les " + net.Restore.WindowDays.Or(90) + " derniers jours, " +
                "aucun moyen de sauvegarde repéré, et " + exposed.Count + " dossier(s) personnel(s) sur le disque " +
                "système : " + string.Join(", ", exposed.ToArray()) + ".",
                "Cette machine n'a ni point de restauration récent, ni sauvegarde, et les affaires du client sont " +
                "sur le disque de Windows. Rien n'est en panne pour autant, mais si une réparation tourne mal ou " +
                "si le disque lâche, il n'y a rien pour revenir en arrière. Une copie des données avant " +
                "d'intervenir est le premier geste à faire.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Points de restauration créés", "0", DataSource.EventLog, "au moins 1"),
                    Evidence.Of("Moyens de sauvegarde repérés", "0", DataSource.Registry, "au moins 1")),
                recommendations: RuleContext.Rec(Rec.BackupNow, Rec.EnableSystemRestore),
                confidence: ConfidenceLevel.Medium));
        }

        /// <summary>
        /// Un dossier personnel qui pointe vers un emplacement qui n'existe pas.
        /// </summary>
        /// <remarks>
        /// Le cas classique du disque secondaire débranché ou tombé en panne : Windows continue
        /// d'afficher « Documents », le client continue de croire que ses fichiers y sont, et le
        /// dossier ne pointe plus sur rien. C'est un constat grave parce qu'il désigne des
        /// données déjà inaccessibles, pas un risque à venir.
        /// </remarks>
        private static RuleResult MissingFolder(RuleContext c)
        {
            var folders = c.System.SafetyNet.Folders;
            if (folders.Count == 0)
                return RuleResult.NotEvaluated("L'emplacement des dossiers personnels n'a pas pu être lu.");

            var missing = new List<PersonalFolder>();
            foreach (var folder in folders)
                if (!folder.Exists)
                    missing.Add(folder);

            if (missing.Count == 0) return RuleResult.Clean;

            var names = new List<string>();
            foreach (var folder in missing) names.Add(Label(folder.Kind));

            return RuleResult.Of(c.Finding(Severity.Problem,
                missing.Count == 1
                    ? "Le dossier « " + names[0] + " » pointe vers un emplacement introuvable"
                    : missing.Count + " dossiers personnels pointent vers un emplacement introuvable",
                string.Join(", ", names.ToArray()) + " : " + missing[0].Path + ".",
                "Ces dossiers ont été déplacés vers un autre disque, et cet emplacement n'existe plus : disque " +
                "débranché, en panne, ou lettre de lecteur changée. Les fichiers qui s'y trouvaient ne sont pas " +
                "perdus pour autant, mais Windows ne sait plus où les chercher, et tout ce qui est enregistré " +
                "depuis part ailleurs.",
                subject: missing[0].Path,
                evidence: RuleContext.Ev(
                    Evidence.Of(names[0], missing[0].Path, DataSource.Registry, "un dossier accessible")),
                recommendations: RuleContext.Rec(Rec.BackupNow)));
        }

        /// <summary>
        /// La machine ne sait plus démarrer en mode réparation.
        /// </summary>
        /// <remarks>
        /// Sans environnement de récupération, une machine qui ne démarre plus n'a plus qu'une clé
        /// d'installation pour espoir, et l'on ne s'en aperçoit qu'au moment où elle ne démarre
        /// plus. C'est aussi ce qui rend impossible une restauration depuis un point, l'écran de
        /// dépannage étant l'un des chemins pour y accéder.
        /// </remarks>
        private static RuleResult NoRecovery(RuleContext c)
        {
            var state = c.System.SafetyNet.Recovery.State;
            if (!state.IsReliable)
                return RuleResult.NotEvaluated(
                    state.Reason ?? "L'état de l'environnement de récupération n'a pas pu être lu.");

            if (state.Value == RecoveryEnvironmentState.Installed) return RuleResult.Clean;

            var missing = state.Value == RecoveryEnvironmentState.Missing;

            return RuleResult.Of(c.Finding(Severity.Warning,
                missing
                    ? "Aucun environnement de récupération sur cette machine"
                    : "L'environnement de récupération est désactivé",
                missing
                    ? "Windows ne déclare aucun environnement de récupération."
                    : "L'environnement de récupération est présent mais désactivé.",
                "C'est l'écran bleu de dépannage qui s'ouvre quand Windows ne démarre pas : réparation du " +
                "démarrage, retour à un point de restauration, invite de commandes. Sur cette machine, il ne " +
                "s'ouvrira pas : un démarrage qui échoue demandera alors une clé d'installation et une " +
                "intervention en atelier.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Environnement de récupération",
                        missing ? "Absent" : "Désactivé", state.Source, "Installé")),
                recommendations: RuleContext.Rec(Rec.EnableSystemRestore)));
        }

        private static string Label(PersonalFolderKind kind) => kind switch
        {
            PersonalFolderKind.Desktop => "Bureau",
            PersonalFolderKind.Documents => "Documents",
            PersonalFolderKind.Pictures => "Images",
            PersonalFolderKind.Music => "Musique",
            PersonalFolderKind.Videos => "Vidéos",
            PersonalFolderKind.Downloads => "Téléchargements",
            _ => "Dossier personnel",
        };
    }
}
