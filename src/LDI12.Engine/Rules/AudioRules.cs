using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Engine.Recommendations;

namespace LDI12.Engine.Rules
{
    /// <summary>
    /// Règles portant sur le son.
    /// </summary>
    /// <remarks>
    /// <b>Ces règles disent ce qui est mesuré, et rien de plus.</b> Une machine peut avoir un
    /// service audio démarré, une sortie active, et rester muette parce que le volume est à zéro
    /// ou que le son part vers l'écran HDMI plutôt que vers les enceintes. Ni le volume ni la
    /// sortie par défaut ne se lisent sans deviner : les constats s'arrêtent donc là où s'arrête
    /// la mesure, et le technicien sait au moins que le reste est à regarder à l'écran.
    /// </remarks>
    internal static class AudioRules
    {
        private const DiagnosticCategory Cat = DiagnosticCategory.Windows;
        private const string Family = "audio";

        public static IEnumerable<IRule> All()
        {
            yield return new Rule("AUD-001", "Service audio arrêté", Cat, Service, Family);
            yield return new Rule("AUD-002", "Aucune sortie audio disponible", Cat, NoOutput, Family);
            yield return new Rule("AUD-003", "Sortie audio désactivée", Cat, Disabled, Family);
        }

        private static RuleResult Service(RuleContext c)
        {
            var audio = c.System.Audio;
            if (!audio.ServiceRunning.IsReliable)
                return RuleResult.NotEvaluated(
                    audio.ServiceRunning.Reason ?? "L'état du service audio n'a pas pu être lu.");

            var builderStopped = audio.EndpointBuilderRunning.IsReliable && !audio.EndpointBuilderRunning.Value;
            if (audio.ServiceRunning.Value && !builderStopped) return RuleResult.Clean;

            return RuleResult.Of(c.Finding(Severity.Problem,
                "Le service audio est arrêté",
                audio.ServiceRunning.Value
                    ? "Le constructeur de points de terminaison audio est arrêté."
                    : "Le service audio de Windows est arrêté.",
                "Aucun son ne peut sortir de cette machine tant que ce service est arrêté, et l'icône du " +
                "volume affiche une croix rouge. C'est la première chose à vérifier quand le client dit " +
                "qu'il n'a plus de son du tout.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Service audio", audio.ServiceRunning.Value ? "Démarré" : "Arrêté",
                        DataSource.NativeApi, "Démarré")),
                recommendations: RuleContext.Rec(Rec.StartEssentialService)));
        }

        /// <summary>
        /// Aucune sortie active.
        /// </summary>
        /// <remarks>
        /// Le détail dit pourquoi, et c'est ce qui oriente le travail : des sorties désactivées se
        /// réactivent en deux clics ; des sorties toutes débranchées désignent une prise ou un
        /// câble ; aucune sortie du tout désigne un pilote absent, que le module des périphériques
        /// aura déjà signalé de son côté.
        /// </remarks>
        private static RuleResult NoOutput(RuleContext c)
        {
            var audio = c.System.Audio;
            if (!audio.ActiveOutputs.IsReliable)
                return RuleResult.NotEvaluated(
                    audio.ActiveOutputs.Reason ?? "Les périphériques audio n'ont pas pu être énumérés.");

            if (audio.ActiveOutputs.Value > 0) return RuleResult.Clean;

            var disabled = 0;
            var unplugged = 0;
            foreach (var endpoint in audio.Endpoints)
            {
                if (endpoint.Direction != AudioDirection.Output) continue;
                if (endpoint.State == AudioEndpointState.Disabled) disabled++;
                else if (endpoint.State == AudioEndpointState.Unplugged) unplugged++;
            }

            var detail = disabled > 0
                ? disabled + " sortie(s) désactivée(s) dans les réglages de Windows."
                : unplugged > 0
                    ? unplugged + " sortie(s) présente(s), mais rien n'est branché dessus."
                    : "Aucun périphérique de sortie n'est déclaré sur cette machine.";

            var plain = disabled > 0
                ? "Le son ne peut aller nulle part : les sorties existent mais ont été désactivées dans les " +
                  "réglages de Windows. Elles se réactivent en deux clics, sans rien réparer d'autre."
                : unplugged > 0
                    ? "Les sorties existent mais Windows ne détecte rien de branché : enceintes ou casque " +
                      "débranchés, ou prise en panne. Le son n'a nulle part où aller."
                    : "Windows ne connaît aucun périphérique de sortie. Le pilote de la carte son est " +
                      "probablement absent ou en défaut, le module des périphériques le dira.";

            return RuleResult.Of(c.Finding(Severity.Problem,
                "Aucune sortie audio disponible",
                detail, plain,
                evidence: RuleContext.Ev(
                    Evidence.Of("Sorties actives", "0", DataSource.Registry, "au moins 1")),
                recommendations: RuleContext.Rec(Rec.InspectDeviceError)));
        }

        /// <summary>
        /// Des sorties désactivées alors qu'une autre fonctionne.
        /// </summary>
        /// <remarks>
        /// Ce n'est pas une panne : c'est l'explication d'un symptôme fréquent. « Le son sort de
        /// l'écran au lieu des enceintes » vient presque toujours de là, et le technicien qui
        /// voit la liste comprend en un regard ce que le client décrit depuis dix minutes.
        /// </remarks>
        private static RuleResult Disabled(RuleContext c)
        {
            var audio = c.System.Audio;
            if (!audio.ActiveOutputs.IsReliable)
                return RuleResult.NotEvaluated(
                    audio.ActiveOutputs.Reason ?? "Les périphériques audio n'ont pas pu être énumérés.");

            if (audio.ActiveOutputs.Value == 0) return RuleResult.Clean;

            var disabled = new List<string>();
            foreach (var endpoint in audio.Endpoints)
                if (endpoint.Direction == AudioDirection.Output && endpoint.State == AudioEndpointState.Disabled)
                    disabled.Add(endpoint.Name);

            if (disabled.Count == 0) return RuleResult.Clean;

            return RuleResult.Of(c.Finding(Severity.Info,
                disabled.Count == 1
                    ? "Une sortie audio est désactivée"
                    : disabled.Count + " sorties audio sont désactivées",
                string.Join(", ", disabled.ToArray()) + ".",
                "Ces sorties ont été désactivées dans les réglages de Windows. Le son fonctionne par " +
                "ailleurs, mais si le client dit qu'il ne sort pas du bon appareil, c'est ici qu'il faut " +
                "regarder d'abord.",
                subject: disabled[0],
                evidence: RuleContext.Ev(
                    Evidence.Of("Sorties désactivées", disabled.Count.ToString(), DataSource.Registry, "0"))));
        }
    }
}
