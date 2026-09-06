using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Engine.Recommendations;

namespace LDI12.Engine.Rules
{
    /// <summary>
    /// Les ports série, et les pannes qui n'en ont pas l'air.
    /// </summary>
    /// <remarks>
    /// <b>Un domaine que le grand public a oublié et qu'un atelier croise toutes les semaines.</b>
    /// Automate, caisse enregistreuse, balance, terminal de paiement, table traçante, appareil de
    /// mesure, carte électronique à programmer : tout cela parle encore en série, à travers un
    /// convertisseur USB. Et quand cela ne marche pas, le message du logiciel client est
    /// invariablement « port introuvable », ce qui n'aide personne.
    /// </remarks>
    internal static class SerialPortRules
    {
        private const DiagnosticCategory Cat = DiagnosticCategory.Hardware;

        private const string Family = "hardware.serial";

        public static IEnumerable<IRule> All()
        {
            yield return new Rule("SER-001", "Port série en défaut", Cat, Faulty, Family);
            yield return new Rule("SER-002", "Numéros de port retenus", Cat, Reserved, Family);
        }

        /// <summary>
        /// Un port série ou un convertisseur ne fonctionne pas.
        /// </summary>
        /// <remarks>
        /// Le cas le plus fréquent est le convertisseur sans pilote : Windows ne lui donne ni
        /// classe ni nom, il se perd parmi les périphériques inconnus, et le client dit « mon
        /// appareil n'est pas reconnu ». Le nommer par sa puce (FTDI, Prolific, CH340) dit
        /// immédiatement quel pilote aller chercher.
        /// </remarks>
        private static RuleResult Faulty(RuleContext c)
        {
            var ports = SerialPorts.Build(c.Snapshot);
            if (ports.Count == 0)
                return RuleResult.NotEvaluated("Aucun port série n'est présent sur cette machine.");

            var findings = new List<Finding>();

            foreach (var port in ports)
            {
                if (port.ProblemCode == 0 && port.PortName != null) continue;

                var missing = port.PortName == null;

                findings.Add(c.Finding(Severity.Warning,
                    missing
                        ? "Un appareil série est branché sans être utilisable"
                        : "Un port série est en défaut",
                    port.Name + (port.Manufacturer == null ? string.Empty : " : " + port.Manufacturer) +
                    (port.ProblemCode == 0
                        ? " : aucun numéro de port ne lui a été attribué."
                        : " : " + (port.ProblemLabel ?? "code " + port.ProblemCode) + "."),
                    missing
                        ? "Cet appareil est bien branché, mais Windows ne lui a donné aucun numéro de port : " +
                          "aucun logiciel ne peut donc lui parler. C'est presque toujours le pilote du " +
                          "convertisseur qui manque : celui de la puce, pas celui de l'appareil. Le message " +
                          "« port introuvable » affiché par le logiciel du client vient de là."
                        : "Ce port série ne fonctionne pas. Tant qu'il est dans cet état, l'appareil qui y est " +
                          "raccordé (automate, caisse, balance, appareil de mesure) reste injoignable, quel " +
                          "que soit le logiciel utilisé.",
                    subject: port.PortName ?? port.Name,
                    evidence: RuleContext.Ev(
                        Evidence.Of("Appareil", port.Name, DataSource.Wmi),
                        Evidence.Of("État",
                            port.ProblemCode == 0 ? "Aucun numéro attribué" : "Code " + port.ProblemCode,
                            DataSource.Wmi, "Port attribué et fonctionnel")),
                    recommendations: RuleContext.Rec(Rec.InstallMissingDrivers)));
            }

            return RuleResult.Of(findings);
        }

        /// <summary>
        /// Windows a retenu des numéros de port pour des appareils qui ne sont plus là.
        /// </summary>
        /// <remarks>
        /// La panne la plus déroutante de ce domaine, parce que rien n'est cassé. Windows ne rend
        /// jamais un numéro attribué : un convertisseur rebranché sur une autre prise prend le
        /// suivant, et après quelques manipulations l'appareil est sur COM13. Or les logiciels
        /// industriels et de programmation, souvent anciens, ne proposent que COM1 à COM9, voire
        /// COM1 à COM4. Tout fonctionne, et rien ne marche.
        /// </remarks>
        private static RuleResult Reserved(RuleContext c)
        {
            var count = c.System.SerialPorts.ReservedCount;
            if (!count.IsReliable)
                return RuleResult.NotEvaluated(
                    count.Reason ?? "La réserve de numéros de port série n'a pas pu être lue.");

            var orphaned = SerialPorts.Orphaned(c.Snapshot);
            if (orphaned.Count < c.T.ReservedSerialPortsWarning) return RuleResult.Clean;

            var highest = 0;
            foreach (var number in c.System.SerialPorts.Reserved)
                if (number > highest) highest = number;

            return RuleResult.Of(c.Finding(Severity.Info,
                "Des numéros de port série sont retenus par des appareils absents",
                orphaned.Count + " numéro(s) retenu(s) sans appareil correspondant, jusqu'à COM" + highest +
                " (seuil " + c.T.ReservedSerialPortsWarning + ").",
                "Windows ne rend jamais un numéro de port qu'il a attribué. À chaque convertisseur USB " +
                "rebranché sur une autre prise, le suivant est distribué, et le prochain appareil branché " +
                "ici recevra un numéro élevé. Beaucoup de logiciels industriels ou de programmation, souvent " +
                "anciens, ne proposent que COM1 à COM9 : l'appareil est alors reconnu par Windows et " +
                "introuvable pour le logiciel. La réserve se vide depuis le Gestionnaire de périphériques, " +
                "en affichant les périphériques absents.",
                evidence: RuleContext.Ev(
                    Evidence.Of("Numéros retenus sans appareil", orphaned.Count.ToString(),
                        DataSource.Registry, c.T.ReservedSerialPortsWarning.ToString()),
                    Evidence.Of("Numéro le plus élevé", "COM" + highest, DataSource.Registry))));
        }
    }
}
