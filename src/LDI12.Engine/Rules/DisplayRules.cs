using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Engine.Recommendations;

namespace LDI12.Engine.Rules
{
    /// <summary>
    /// Ce qu'un client décrit par « c'est flou » ou « les caractères sont minuscules ».
    /// </summary>
    /// <remarks>
    /// <b>Trois plaintes très fréquentes, aucune mesure jusqu'ici.</b> Elles ont ceci de commun
    /// qu'elles ne sont pas des pannes : rien ne s'arrête, rien n'échoue, aucun journal n'en
    /// garde trace. La machine fait exactement ce qu'on lui a demandé, et personne ne se
    /// souvient de l'avoir demandé.
    /// </remarks>
    internal static class DisplayRules
    {
        private const DiagnosticCategory Cat = DiagnosticCategory.Hardware;

        /// <summary>Définition et fréquence décrivent le même réglage d'affichage vu de deux côtés.</summary>
        private const string Family = "display.mode";

        public static IEnumerable<IRule> All()
        {
            yield return new Rule("DSP-001", "Définition native de l'écran", Cat, NativeResolution, Family);
            yield return new Rule("DSP-002", "Fréquence de rafraîchissement", Cat, RefreshRate, Family);
            yield return new Rule("DSP-003", "Taille apparente du texte", Cat, TextSize);
        }

        /// <summary>
        /// L'écran n'affiche pas sa définition native.
        /// </summary>
        /// <remarks>
        /// Une dalle est nette à sa définition et à aucune autre : en dessous, elle étire chaque
        /// pixel sur plusieurs, et les contours du texte se brouillent. C'est littéralement le
        /// « c'est flou » du client, et c'est un réglage, pas une usure.
        /// <para>
        /// Quand le pilote graphique est le pilote générique de Windows, la cause est ailleurs et
        /// le constat le dit : ce pilote ne propose que quelques définitions basses, et changer le
        /// réglage n'y fera rien tant que le vrai pilote n'est pas installé.
        /// </para>
        /// </remarks>
        private static RuleResult NativeResolution(RuleContext c)
        {
            var monitors = c.Hardware.Displays.Monitors;
            if (monitors.Count == 0)
                return RuleResult.NotEvaluated("Aucun écran rattaché au bureau n'a été relevé.");

            var findings = new List<Finding>();
            var evaluated = false;

            foreach (var monitor in monitors)
            {
                if (!monitor.Current.HasValue || !monitor.Native.HasValue) continue;

                evaluated = true;
                if (monitor.AtNativeResolution) continue;

                var current = monitor.Current.Value;
                var native = monitor.Native.Value;
                var name = monitor.Model.Or(monitor.Output);

                findings.Add(c.Finding(Severity.Warning,
                    "L'écran n'affiche pas sa définition native",
                    name + " : " + current.Width + " × " + current.Height +
                    " alors que la dalle est en " + native.Width + " × " + native.Height + ".",
                    GenericDriver(c)
                        ? "Cet écran n'affiche pas la définition pour laquelle il est fait, et la carte " +
                          "graphique fonctionne avec le pilote générique de Windows. Ce pilote ne propose que " +
                          "quelques définitions basses : changer le réglage n'y changera rien tant que le pilote " +
                          "du constructeur n'est pas installé. C'est aussi ce qui explique une image floue et un " +
                          "bureau qui paraît trop grand."
                        : "Une dalle est nette à sa définition d'origine et à aucune autre : en dessous, elle " +
                          "étire chaque point sur plusieurs et les contours du texte se brouillent. C'est ce " +
                          "qu'on décrit par « c'est flou ». Le réglage se corrige en quelques secondes dans les " +
                          "paramètres d'affichage, et l'écran redevient net immédiatement.",
                    subject: name,
                    evidence: RuleContext.Ev(
                        Evidence.Of("Définition affichée", current.Width + " × " + current.Height,
                            DataSource.NativeApi, native.Width + " × " + native.Height),
                        Evidence.Of("Définition de la dalle", native.Width + " × " + native.Height,
                            DataSource.Registry)),
                    recommendations: GenericDriver(c)
                        ? RuleContext.Rec(Rec.InstallGpuDriver)
                        : RuleContext.Rec(Rec.SetNativeResolution)));
            }

            return evaluated
                ? RuleResult.Of(findings)
                : RuleResult.NotEvaluated(
                    "Aucun écran ne publie à la fois son mode courant et sa définition d'origine.");
        }

        /// <summary>
        /// L'écran tourne bien en dessous de ce que le pilote lui propose.
        /// </summary>
        /// <remarks>
        /// Le cas classique : une dalle à cent quarante-quatre hertz branchée et laissée à
        /// soixante, parce que c'est ce que Windows applique par défaut sur beaucoup de câbles.
        /// Ce n'est une panne pour personne, et une différence que tout le monde voit dès qu'on
        /// la corrige. En information, donc, et sans pénalité.
        /// </remarks>
        private static RuleResult RefreshRate(RuleContext c)
        {
            var monitors = c.Hardware.Displays.Monitors;
            if (monitors.Count == 0)
                return RuleResult.NotEvaluated("Aucun écran rattaché au bureau n'a été relevé.");

            var findings = new List<Finding>();
            var evaluated = false;

            foreach (var monitor in monitors)
            {
                if (!monitor.Current.HasValue || !monitor.MaxRefreshHz.IsReliable) continue;

                var current = monitor.Current.Value.RefreshHz;
                var max = monitor.MaxRefreshHz.Value;
                if (current <= 0 || max <= 0) continue;

                evaluated = true;

                // Sous soixante-quinze hertz, il n'y a rien à gagner : c'est la fréquence de la
                // quasi-totalité des dalles, et l'écart avec elle-même n'existe pas.
                if (max < 75) continue;
                if (100d * current / max >= c.T.DisplayRefreshShortfallPercent) continue;

                var name = monitor.Model.Or(monitor.Output);

                findings.Add(c.Finding(Severity.Info,
                    "L'écran tourne bien en dessous de ce qu'il permet",
                    name + " : " + current + " Hz alors que " + max + " Hz sont proposés à cette définition.",
                    "Cet écran est capable d'afficher beaucoup plus d'images par seconde qu'il n'en affiche. " +
                    "Ce n'est pas une panne (Windows applique souvent la valeur la plus basse par défaut) " +
                    "mais la différence se voit immédiatement dès qu'on déplace une fenêtre ou le pointeur. " +
                    "Le réglage se change dans les paramètres d'affichage avancés.",
                    subject: name,
                    evidence: RuleContext.Ev(
                        Evidence.Of("Fréquence appliquée", current + " Hz", DataSource.NativeApi, max + " Hz"))));
            }

            return evaluated
                ? RuleResult.Of(findings)
                : RuleResult.NotEvaluated("Aucun écran ne publie les fréquences que son pilote propose.");
        }

        /// <summary>
        /// Le texte est nettement plus petit ou plus grand que ce qui se lit confortablement.
        /// </summary>
        /// <remarks>
        /// Ce qui décide de la taille du texte n'est ni la définition ni la taille de l'écran,
        /// mais leur rapport corrigé de la mise à l'échelle. Une dalle 4K de vingt-sept pouces
        /// laissée à cent pour cent affiche à cent soixante points par pouce : tout y est deux
        /// fois trop petit, et le client dit « je ne vois rien » sans savoir quoi demander.
        /// </remarks>
        private static RuleResult TextSize(RuleContext c)
        {
            var displays = c.Hardware.Displays;
            if (displays.Monitors.Count == 0)
                return RuleResult.NotEvaluated("Aucun écran rattaché au bureau n'a été relevé.");

            if (!displays.ScalingPercent.IsReliable)
                return RuleResult.NotEvaluated(
                    displays.ScalingPercent.Reason ?? "La mise à l'échelle du bureau n'a pas pu être lue.");

            var scaling = Math.Max(1, displays.ScalingPercent.Value);
            var findings = new List<Finding>();
            var evaluated = false;

            foreach (var monitor in displays.Monitors)
            {
                if (!monitor.PixelsPerInch.IsReliable) continue;

                evaluated = true;

                // Densité perçue : ce que la dalle affiche, ramené à ce que Windows agrandit.
                var effective = monitor.PixelsPerInch.Value * 100d / scaling;
                var tiny = effective >= c.T.DisplayEffectiveDpiTiny;
                var large = effective <= c.T.DisplayEffectiveDpiLarge;
                if (!tiny && !large) continue;

                var name = monitor.Model.Or(monitor.Output);

                findings.Add(c.Finding(Severity.Info,
                    tiny ? "Le texte est très petit sur cet écran" : "Le texte est très grand sur cet écran",
                    name + " : " + Math.Round(monitor.PixelsPerInch.Value) + " points par pouce, mise à " +
                    "l'échelle " + scaling + " %, soit " + Math.Round(effective) + " points par pouce perçus.",
                    tiny
                        ? "Cet écran affiche beaucoup de points sur peu de surface, et Windows ne compense pas. " +
                          "Tout y paraît minuscule (menus, texte, boutons) sans que rien ne soit en panne. " +
                          "Augmenter la mise à l'échelle dans les paramètres d'affichage rend la lecture " +
                          "confortable sans rien perdre de la finesse de l'image."
                        : "Tout paraît grand sur cet écran : la mise à l'échelle dépasse ce que sa finesse " +
                          "demande, et l'espace de travail s'en trouve réduit. La baisser rend de la place à " +
                          "l'écran sans rendre le texte illisible.",
                    subject: name,
                    evidence: RuleContext.Ev(
                        Evidence.Of("Densité perçue", Math.Round(effective) + " points par pouce",
                            DataSource.Inferred,
                            c.T.DisplayEffectiveDpiLarge + " à " + c.T.DisplayEffectiveDpiTiny),
                        Evidence.Of("Mise à l'échelle", scaling + " %", DataSource.Registry))));
            }

            return evaluated
                ? RuleResult.Of(findings)
                : RuleResult.NotEvaluated("Aucun écran ne déclare ses dimensions physiques.");
        }

        /// <summary>
        /// Vrai quand l'affichage repose sur le pilote générique de Windows.
        /// </summary>
        /// <remarks>
        /// Change la lecture d'une définition trop basse : ce n'est plus un réglage à corriger
        /// mais un pilote à installer, et proposer le premier ferait tourner en rond.
        /// </remarks>
        private static bool GenericDriver(RuleContext c)
        {
            foreach (var gpu in c.Hardware.Gpus)
                if (gpu.UsesGenericMicrosoftDriver.Or(false)) return true;

            return false;
        }
    }
}
