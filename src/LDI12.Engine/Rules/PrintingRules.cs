using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Engine.Recommendations;

namespace LDI12.Engine.Rules
{
    /// <summary>
    /// Règles portant sur l'impression.
    /// </summary>
    /// <remarks>
    /// <b>« Je ne peux plus imprimer » est une des trois pannes les plus racontées en atelier.</b>
    /// Trois causes en couvrent presque tous les cas, et chacune a son constat : le service
    /// d'impression est arrêté, l'imprimante est hors connexion, ou la file est encombrée devant
    /// une imprimante qui ne répond pas.
    /// </remarks>
    internal static class PrintingRules
    {
        private const DiagnosticCategory Cat = DiagnosticCategory.Windows;
        private const string Family = "printing";

        private const int MaxNamed = 4;

        public static IEnumerable<IRule> All()
        {
            yield return new Rule("PRN-001", "Service d'impression arrêté", Cat, Spooler, Family);
            yield return new Rule("PRN-002", "Imprimante hors connexion", Cat, Offline, Family);
            yield return new Rule("PRN-003", "Imprimante en erreur", Cat, Faulted, Family);
            yield return new Rule("PRN-004", "File d'attente encombrée", Cat, Queue, Family);
        }

        /// <summary>
        /// Le spouleur arrêté : rien ne s'imprime, quelle que soit l'imprimante.
        /// </summary>
        /// <remarks>
        /// La gravité dépend de ce qui est installé. Sur une machine sans imprimante, un spouleur
        /// arrêté est un choix courant (certains le coupent pour réduire la surface d'attaque)
        /// et l'annoncer comme une panne ferait perdre du temps.
        /// </remarks>
        private static RuleResult Spooler(RuleContext c)
        {
            var printing = c.System.Printing;
            if (!printing.SpoolerRunning.IsReliable)
                return RuleResult.NotEvaluated(
                    printing.SpoolerRunning.Reason ?? "L'état du service d'impression n'a pas pu être lu.");

            if (printing.SpoolerRunning.Value) return RuleResult.Clean;

            var installed = printing.Printers.Count;

            return RuleResult.Of(c.Finding(installed > 0 ? Severity.Problem : Severity.Info,
                "Le service d'impression est arrêté",
                "Spouleur : " + printing.SpoolerState.Or("arrêté") + ", pour " + installed +
                " imprimante(s) installée(s).",
                installed > 0
                    ? "Rien ne peut s'imprimer tant que ce service est arrêté, quelle que soit l'imprimante " +
                      "et quel que soit le programme. C'est la première chose à vérifier quand le client dit " +
                      "que « rien ne sort »."
                    : "Le service d'impression est arrêté, mais aucune imprimante n'est installée sur cette " +
                      "machine : rien n'en dépend aujourd'hui. À redémarrer le jour où une imprimante sera " +
                      "ajoutée.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Spouleur d'impression", printing.SpoolerState.Or("Arrêté"),
                        DataSource.NativeApi, "Démarré")),
                recommendations: RuleContext.Rec(Rec.StartEssentialService)));
        }

        /// <summary>
        /// Une imprimante que Windows ne joint pas, ou qu'il a reçu l'ordre de ne plus joindre.
        /// </summary>
        /// <remarks>
        /// La distinction est tout l'intérêt du constat. « Utiliser l'imprimante hors connexion »
        /// est une case cochée dans son menu, souvent par accident, et qui se décoche d'un clic ;
        /// une imprimante réellement injoignable demande de regarder le câble, le réseau ou
        /// l'appareil lui-même. Le client vit les deux de la même façon.
        /// </remarks>
        private static RuleResult Offline(RuleContext c)
        {
            var printers = c.System.Printing.Printers;
            if (printers.Count == 0) return NoPrinters(c);

            var chosen = new List<string>();
            var unreachable = new List<string>();

            foreach (var printer in printers)
            {
                if (printer.Availability != PrinterAvailability.Offline) continue;
                if (printer.OfflineByChoice) chosen.Add(printer.Name);
                else unreachable.Add(printer.Name);
            }

            if (chosen.Count == 0 && unreachable.Count == 0) return RuleResult.Clean;

            var findings = new List<Finding>();

            if (unreachable.Count > 0)
                findings.Add(c.Finding(Severity.Warning,
                    unreachable.Count == 1
                        ? "L'imprimante « " + unreachable[0] + " » est hors connexion"
                        : unreachable.Count + " imprimantes sont hors connexion",
                    Name(unreachable) + ".",
                    "Windows n'arrive pas à joindre cette imprimante : éteinte, débranchée, ou absente du " +
                    "réseau. Les impressions s'empilent en attente jusqu'à ce qu'elle réponde.",
                    subject: unreachable[0],
                    evidence: RuleContext.Ev(
                        Evidence.Of("Imprimantes hors connexion", unreachable.Count.ToString(),
                            DataSource.Wmi, "0"))));

            if (chosen.Count > 0)
                findings.Add(c.Finding(Severity.Warning,
                    chosen.Count == 1
                        ? "Le mode hors connexion est activé sur « " + chosen[0] + " »"
                        : chosen.Count + " imprimantes sont en mode hors connexion",
                    Name(chosen) + ".",
                    "Quelqu'un a coché « Utiliser l'imprimante hors connexion » dans son menu, souvent sans " +
                    "le vouloir. Windows n'essaie même plus de lui parler, et tout ce qu'on imprime reste en " +
                    "attente. Cela se décoche d'un clic, sans rien réparer d'autre.",
                    subject: chosen[0],
                    evidence: RuleContext.Ev(
                        Evidence.Of("Mode hors connexion demandé", chosen.Count.ToString(),
                            DataSource.Wmi, "0"))));

            return RuleResult.Of(findings);
        }

        /// <summary>Une imprimante qui demande une intervention physique.</summary>
        private static RuleResult Faulted(RuleContext c)
        {
            var printers = c.System.Printing.Printers;
            if (printers.Count == 0) return NoPrinters(c);

            var faulted = new List<string>();
            foreach (var printer in printers)
                if (printer.Availability == PrinterAvailability.Error ||
                    printer.Availability == PrinterAvailability.PaperOut ||
                    printer.Availability == PrinterAvailability.InkOut ||
                    printer.Availability == PrinterAvailability.Paused)
                    faulted.Add(printer.Name + " : " + Describe(printer.Availability));

            if (faulted.Count == 0) return RuleResult.Clean;

            return RuleResult.Of(c.Finding(Severity.Warning,
                faulted.Count == 1
                    ? "Une imprimante signale un problème"
                    : faulted.Count + " imprimantes signalent un problème",
                Name(faulted) + ".",
                "L'imprimante elle-même signale ce qui l'empêche d'imprimer. C'est presque toujours à " +
                "régler devant l'appareil (papier, encre, capot, bourrage) et non sur l'ordinateur.",
                subject: printers[0].Name,
                evidence: RuleContext.Ev(
                    Evidence.Of("Imprimantes en défaut", faulted.Count.ToString(), DataSource.Wmi, "0"))));
        }

        /// <summary>
        /// Des documents en attente devant une imprimante qui ne répond pas.
        /// </summary>
        /// <remarks>
        /// Une file qui n'avance pas est un symptôme, pas une cause : ce sont les constats
        /// précédents qui portent la cause. Elle vaut d'être dite parce qu'elle explique ce que
        /// le client décrit (« j'ai cliqué dix fois et rien ne sort ») et parce qu'un document
        /// bloqué en tête empêche les suivants de partir, même une fois la panne réglée.
        /// </remarks>
        private static RuleResult Queue(RuleContext c)
        {
            var printing = c.System.Printing;
            if (!printing.PendingJobs.IsReliable)
                return RuleResult.NotEvaluated(
                    printing.PendingJobs.Reason ?? "La file d'attente n'a pas pu être lue.");

            if (printing.PendingJobs.Value == 0) return RuleResult.Clean;

            var blocked = false;
            foreach (var printer in printing.Printers)
                if (printer.Availability != PrinterAvailability.Ready &&
                    printer.Availability != PrinterAvailability.Unknown)
                    blocked = true;

            if (!blocked) return RuleResult.Clean;

            return RuleResult.Of(c.Finding(Severity.Warning,
                "Des documents attendent devant une imprimante indisponible",
                printing.PendingJobs.Value + " document(s) en attente.",
                "Ces documents ne partiront pas tant que l'imprimante ne répondra pas. Une fois le problème " +
                "réglé, ils sortiront tous d'un coup, sauf si l'un d'eux est bloqué en tête de file, auquel " +
                "cas il faut vider la file avant de réessayer.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Documents en attente", printing.PendingJobs.Value.ToString(),
                        DataSource.Wmi, "0"))));
        }

        /// <summary>
        /// Aucune imprimante installée : il n'y a rien à juger.
        /// </summary>
        /// <remarks>
        /// « Non évalué » plutôt que « rien à signaler » : une machine sans imprimante n'est pas
        /// une machine dont les imprimantes vont bien, et le score ne doit pas compter un contrôle
        /// qui n'a rien contrôlé.
        /// </remarks>
        private static RuleResult NoPrinters(RuleContext c)
            => RuleResult.NotEvaluated("Aucune imprimante n'est installée sur cette machine.");

        internal static string Describe(PrinterAvailability availability) => availability switch
        {
            PrinterAvailability.Ready => "Prête",
            PrinterAvailability.Offline => "Hors connexion",
            PrinterAvailability.Error => "Bourrage, capot ouvert ou bac plein",
            PrinterAvailability.PaperOut => "Plus de papier",
            PrinterAvailability.InkOut => "Plus d'encre",
            PrinterAvailability.Paused => "Impression en pause",
            _ => "État indéterminé",
        };

        private static string Name(IReadOnlyList<string> names)
        {
            var parts = new List<string>();
            for (var i = 0; i < names.Count && i < MaxNamed; i++) parts.Add(names[i]);

            var text = string.Join(", ", parts.ToArray());
            if (names.Count > MaxNamed) text += " et " + (names.Count - MaxNamed) + " autre(s)";
            return text;
        }
    }
}
