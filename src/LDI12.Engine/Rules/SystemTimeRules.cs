using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Engine.Recommendations;

namespace LDI12.Engine.Rules
{
    /// <summary>
    /// L'horloge, le point de défaillance unique le plus trompeur de Windows.
    /// </summary>
    /// <remarks>
    /// <b>Une horloge fausse ne casse rien de visible.</b> Elle fait échouer toutes les
    /// connexions sécurisées à la fois, refuse les mises à jour, invalide l'activation et bloque
    /// l'ouverture de session sur un domaine. Le client dit « je n'ai plus Internet », le
    /// technicien cherche du côté du réseau, et personne ne regarde le coin de l'écran.
    /// </remarks>
    internal static class SystemTimeRules
    {
        private const DiagnosticCategory Cat = DiagnosticCategory.Windows;

        /// <summary>Une horloge fausse et une horloge qui ne se remet plus à l'heure sont le même problème.</summary>
        private const string Family = "windows.clock";

        public static IEnumerable<IRule> All()
        {
            yield return new Rule("TIM-001", "Justesse de l'horloge", Cat, ClockBehind, Family);
            yield return new Rule("TIM-002", "Service de temps désactivé", Cat, ServiceDisabled, Family);
            yield return new Rule("TIM-003", "Ancienneté de la synchronisation", Cat, StaleSync, Family);
            yield return new Rule("TIM-004", "Passage à l'heure d'été", Cat, DaylightAdjustment);
            yield return new Rule("TIM-005", "Source de temps d'un poste de domaine", Cat, DomainSource);
        }

        /// <summary>
        /// L'horloge est antérieure à ce que la machine contient.
        /// </summary>
        /// <remarks>
        /// <b>Une démonstration, pas une estimation.</b> Les fichiers du noyau de Windows portent
        /// la date à laquelle Microsoft les a compilés, et le micrologiciel celle de sa
        /// publication : deux dates qui viennent de l'extérieur de la machine et que son horloge
        /// ne peut pas avoir influencées. La machine ne peut pas être antérieure à elles.
        /// <para>
        /// La date d'installation de Windows n'est volontairement pas utilisée : elle a été
        /// écrite par cette machine, avec cette horloge. Un poste installé avec une pile morte
        /// porte une date d'installation aberrante, et s'en servir comme référence ferait
        /// déclarer fausse une horloge parfaitement juste.
        /// </para>
        /// </remarks>
        private static RuleResult ClockBehind(RuleContext c)
        {
            var time = c.System.Time;
            if (!time.SystemTime.IsReliable)
                return RuleResult.NotEvaluated("L'heure de la machine n'a pas pu être lue.");

            DateTimeOffset? floor = null;
            string? source = null;

            if (time.NewestSystemFile.IsReliable)
            {
                floor = time.NewestSystemFile.Value;
                source = "les fichiers du noyau de Windows";
            }

            var bios = c.Hardware.Motherboard.Bios.ReleaseDate;
            if (bios.IsReliable && (floor == null || bios.Value > floor.Value))
            {
                floor = bios.Value;
                source = "la date de publication du micrologiciel";
            }

            if (floor == null)
                return RuleResult.NotEvaluated(
                    "Aucune référence extérieure à l'horloge n'a pu être lue : sa justesse ne peut pas " +
                    "être démontrée sans réseau.");

            var behind = (floor.Value - time.SystemTime.Value).TotalDays;
            if (behind < c.T.ClockBehindDaysProblem) return RuleResult.Clean;

            return RuleResult.Of(c.Finding(Severity.Problem,
                "L'horloge de la machine est fausse",
                "Horloge au " + Fmt.Date(time.SystemTime.Value) + ", alors que " + source + " datent du " +
                Fmt.Date(floor.Value) + " : " + Math.Round(behind) + " jour(s) de retard.",
                "Cette machine contient des fichiers plus récents que la date qu'elle affiche : son horloge " +
                "est donc en retard, et ce n'est pas une question d'appréciation. Une horloge fausse fait " +
                "échouer toutes les connexions sécurisées à la fois (le navigateur annonce que les " +
                "certificats des sites ne sont pas valides), bloque les mises à jour de Windows et peut " +
                "invalider l'activation. C'est souvent la seule cause d'un « je n'ai plus Internet » où tout " +
                "le reste fonctionne. Sur une machine de bureau, une pile de carte mère usée la remet à zéro " +
                "à chaque extinction.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Horloge de la machine", Fmt.Date(time.SystemTime.Value), DataSource.NativeApi),
                    Evidence.Of("Référence indépendante", Fmt.Date(floor.Value), DataSource.FileSystem),
                    Evidence.Of("Retard", Math.Round(behind) + " jour(s)", DataSource.Inferred,
                        c.T.ClockBehindDaysProblem + " jour(s)")),
                recommendations: RuleContext.Rec(Rec.FixSystemClock)));
        }

        /// <summary>
        /// Le service de temps est désactivé.
        /// </summary>
        /// <remarks>
        /// <b>Désactivé, et non arrêté.</b> Depuis Windows 10, W32Time démarre à la demande et
        /// s'arrête aussitôt : le trouver arrêté est l'état normal de presque toutes les machines,
        /// et le signaler ferait sonner l'alerte partout, ce qui revient à ne plus la faire
        /// sonner nulle part. Désactivé, en revanche, est un choix que quelqu'un a fait, et
        /// l'horloge dérive alors librement.
        /// </remarks>
        private static RuleResult ServiceDisabled(RuleContext c)
        {
            var disabled = c.System.Time.Sync.ServiceDisabled;
            if (!disabled.IsReliable)
                return RuleResult.NotEvaluated(
                    disabled.Reason ?? "L'état du service de temps n'a pas pu être lu.");

            if (!disabled.Value) return RuleResult.Clean;

            return RuleResult.Of(c.Finding(Severity.Warning,
                "Le service qui remet l'horloge à l'heure est désactivé",
                "Service W32Time désactivé au démarrage.",
                "Sans ce service, l'horloge de la machine n'est jamais corrigée et dérive de quelques " +
                "secondes par jour, davantage sur un poste ancien. Au bout de quelques mois l'écart " +
                "devient suffisant pour faire échouer des connexions sécurisées, et personne ne fait le " +
                "lien. Ce service n'est pas censé tourner en permanence : il se réveille, corrige, et " +
                "s'arrête. Le trouver désactivé est en revanche un choix, rarement volontaire.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Démarrage du service", "Désactivé", DataSource.Registry, "Manuel ou automatique")),
                recommendations: RuleContext.Rec(Rec.FixSystemClock)));
        }

        /// <summary>
        /// L'horloge n'a pas été remise à l'heure depuis longtemps.
        /// </summary>
        /// <remarks>
        /// L'absence de trace ne prouve rien (un journal purgé, une machine réinstallée la
        /// veille) et le constat ne se déclenche que sur une date réellement lue. Là où d'autres
        /// règles concluent d'un silence, celle-ci s'en abstient : un « jamais synchronisée »
        /// prononcé à tort enverrait chercher une panne qui n'existe pas.
        /// </remarks>
        private static RuleResult StaleSync(RuleContext c)
        {
            var last = c.System.Time.Sync.LastSynchronised;
            if (!last.IsReliable)
                return RuleResult.NotEvaluated(
                    last.Reason ?? "Aucune remise à l'heure n'apparaît dans les journaux.");

            var days = (c.Snapshot.Metadata.CreatedAt - last.Value).TotalDays;
            if (days < c.T.ClockSyncStaleDaysWarning) return RuleResult.Clean;

            return RuleResult.Of(c.Finding(Severity.Info,
                "L'horloge n'a pas été remise à l'heure depuis longtemps",
                "Dernière synchronisation le " + Fmt.Date(last.Value) + ", soit il y a " +
                Math.Round(days) + " jours (seuil " + c.T.ClockSyncStaleDaysWarning + ").",
                "Windows corrige l'horloge de temps en temps auprès d'un serveur de temps. Cela ne s'est pas " +
                "produit depuis longtemps ici : soit la machine ne joint pas ce serveur, soit elle est restée " +
                "éteinte. L'horloge n'est pas forcément fausse pour autant, mais plus rien ne la surveille.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Dernière synchronisation", Fmt.Date(last.Value), DataSource.EventLog,
                        c.T.ClockSyncStaleDaysWarning + " jours"))));
        }

        /// <summary>
        /// Le passage automatique à l'heure d'été est coupé.
        /// </summary>
        /// <remarks>
        /// Un réglage que personne ne coche par hasard, et dont l'effet est invisible pendant six
        /// mois : l'horloge est juste l'hiver et fausse d'une heure l'été. La plainte arrive alors
        /// sans rapport apparent avec quoi que ce soit : des rendez-vous décalés, un pointage
        /// faux, des courriels horodatés de travers.
        /// </remarks>
        private static RuleResult DaylightAdjustment(RuleContext c)
        {
            var adjustment = c.System.Time.DaylightAdjustment;
            if (!adjustment.IsReliable)
                return RuleResult.NotEvaluated(
                    adjustment.Reason ?? "Le réglage d'heure d'été n'a pas pu être lu.");

            if (adjustment.Value) return RuleResult.Clean;

            return RuleResult.Of(c.Finding(Severity.Info,
                "Le passage automatique à l'heure d'été est désactivé",
                "Fuseau : " + c.System.Time.TimeZone.Or("inconnu") + ", ajustement automatique coupé.",
                "Ce fuseau horaire pratique l'heure d'été, mais la machine ne l'applique pas. Son horloge " +
                "sera donc juste une moitié de l'année et fausse d'une heure l'autre moitié. L'effet est " +
                "invisible jusqu'au changement d'heure, puis se manifeste par des rendez-vous décalés et des " +
                "courriels horodatés de travers, sans rapport apparent avec l'ordinateur.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Ajustement automatique", "Désactivé", DataSource.Registry, "Activé"))));
        }

        /// <summary>
        /// Un poste de domaine qui prend son heure ailleurs que dans le domaine.
        /// </summary>
        /// <remarks>
        /// Kerberos refuse toute authentification au-delà de cinq minutes d'écart avec le
        /// contrôleur. Un poste réglé sur un serveur de temps public dérive indépendamment du
        /// domaine, et le jour où l'écart dépasse la tolérance, plus aucune session ne s'ouvre,
        /// avec un message qui ne parle jamais de l'heure.
        /// </remarks>
        private static RuleResult DomainSource(RuleContext c)
        {
            if (!c.Windows.IsDomainJoined)
                return RuleResult.NotEvaluated(
                    "Cette machine n'appartient à aucun domaine : la question ne se pose pas.");

            var source = c.System.Time.Sync.Source;
            if (!source.IsReliable)
                return RuleResult.NotEvaluated(source.Reason ?? "La source de temps n'a pas pu être lue.");

            if (source.Value == TimeSource.Domain) return RuleResult.Clean;

            var external = source.Value == TimeSource.Ntp;

            return RuleResult.Of(c.Finding(Severity.Warning,
                external
                    ? "Ce poste du domaine prend son heure hors du domaine"
                    : "Ce poste du domaine ne synchronise pas son horloge",
                "Source déclarée : " + (external ? "serveur de temps " + c.System.Time.Sync.Server.Or("non nommé")
                    : "aucune synchronisation") + ", au lieu de la hiérarchie du domaine.",
                "Sur une machine jointe à un domaine, l'heure doit venir du domaine lui-même. " +
                "L'authentification refuse toute session dès que l'écart avec le contrôleur dépasse cinq " +
                "minutes, et le message affiché à l'utilisateur ne parle jamais de l'heure. Une machine " +
                "réglée sur un serveur extérieur dérive indépendamment du domaine : elle fonctionne jusqu'au " +
                "jour où elle ne fonctionne plus, sans que rien n'ait été touché.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Source de temps",
                        external ? "Serveur extérieur" : "Aucune", DataSource.Registry, "Domaine")),
                recommendations: RuleContext.Rec(Rec.FixSystemClock)));
        }
    }
}
