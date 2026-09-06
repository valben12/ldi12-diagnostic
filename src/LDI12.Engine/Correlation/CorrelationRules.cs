using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Engine.Recommendations;

namespace LDI12.Engine.Correlation
{
    /// <summary>
    /// Règles de second niveau : elles lisent les constats, pas les mesures.
    /// </summary>
    /// <remarks>
    /// C'est ce qui transforme « voici quatorze problèmes » en « voici pourquoi ce PC est lent, et
    /// dans quel ordre agir ». Une corrélation n'ajoute jamais de pénalité : elle ne fait que
    /// relier des constats déjà comptés, et proposer un ordre de traitement.
    /// </remarks>
    public static class CorrelationRules
    {
        public static IReadOnlyList<Core.Model.Correlation> Evaluate(IReadOnlyList<Finding> findings)
        {
            var index = new FindingIndex(findings);
            var correlations = new List<Core.Model.Correlation>();

            Add(correlations, DyingDisk(index));
            Add(correlations, DegradedWindowsBlocksUpdates(index));
            Add(correlations, ConnectivityLayer(index));
            Add(correlations, MultifactorSlowness(index));
            Add(correlations, HardwareInstability(index));
            Add(correlations, UndersizedMachine(index));

            return correlations;
        }

        /// <summary>
        /// Le SMART seul peut être ignoré ; associé à des erreurs disque dans les journaux, il ne
        /// laisse plus de place au doute. C'est la corrélation la plus importante de l'outil :
        /// elle transforme un indicateur discutable en certitude actionnable.
        /// </summary>
        private static Core.Model.Correlation? DyingDisk(FindingIndex index)
        {
            var smart = index.Any("STO-003", "STO-004", "STO-005", "STO-006");
            var diskEvents = index.Any("EVT-003");
            if (!smart || !diskEvents) return null;

            return new Core.Model.Correlation
            {
                Id = "COR-001",
                Title = "Un disque est en train de tomber en panne",
                Severity = Severity.Critical,
                Narrative =
                    "Deux sources indépendantes concordent : le disque signale lui-même une dégradation par ses " +
                    "indicateurs SMART, et Windows enregistre en parallèle des erreurs de lecture ou d'écriture. " +
                    "Pris isolément, chacun de ces signes pourrait s'expliquer autrement ; ensemble, ils désignent " +
                    "une défaillance matérielle en cours. La sauvegarde passe avant toute autre intervention.",
                Contributors = index.KeysOf("STO-003", "STO-004", "STO-005", "STO-006", "EVT-003"),
                OrderedActions = new[] { Rec.BackupNow, Rec.ReplaceDisk, Rec.CheckDisk },
            };
        }

        /// <summary>
        /// L'ordre compte : réparer l'image avant de relancer Windows Update, sinon la mise à
        /// jour échouera à nouveau et le technicien tournera en rond.
        /// </summary>
        private static Core.Model.Correlation? DegradedWindowsBlocksUpdates(FindingIndex index)
        {
            var corruption = index.Any("WIN-001", "WIN-002");
            var updateDelay = index.Any("UPD-001", "UPD-002");
            if (!corruption || !updateDelay) return null;

            return new Core.Model.Correlation
            {
                Id = "COR-002",
                Title = "La corruption système explique le blocage des mises à jour",
                Severity = Severity.Problem,
                Narrative =
                    "Les fichiers système de Windows sont endommagés, et la machine a effectivement pris du retard " +
                    "sur les correctifs. Ce n'est pas une coïncidence : Windows Update s'appuie sur ces mêmes " +
                    "fichiers pour installer ses mises à jour. Relancer Windows Update maintenant échouera. " +
                    "Il faut réparer l'image système d'abord, puis relancer les mises à jour.",
                Contributors = index.KeysOf("WIN-001", "WIN-002", "UPD-001", "UPD-002"),
                OrderedActions = new[] { Rec.RepairComponentStore, Rec.RepairSystemFiles, Rec.RunWindowsUpdate },
            };
        }

        /// <summary>
        /// Désigne l'étage fautif plutôt que de laisser trois constats réseau côte à côte, ce qui
        /// évite de démonter une installation qui fonctionne.
        /// </summary>
        private static Core.Model.Correlation? ConnectivityLayer(FindingIndex index)
        {
            if (index.Any("NET-004") && !index.Any("NET-001", "NET-002", "NET-003"))
            {
                return new Core.Model.Correlation
                {
                    Id = "COR-003",
                    Title = "La panne se situe uniquement au niveau de la résolution de noms",
                    Severity = Severity.Problem,
                    Narrative =
                        "La carte réseau fonctionne, la box répond, la connexion vers Internet est établie : " +
                        "seule la traduction des noms de sites en adresses échoue. Pour l'utilisateur, tous les " +
                        "sites semblent inaccessibles alors que la ligne est parfaitement bonne. Le problème est " +
                        "la configuration DNS, et rien d'autre, inutile de toucher au matériel ou à la box.",
                    Contributors = index.KeysOf("NET-004"),
                    OrderedActions = new[] { Rec.FixDnsConfiguration },
                };
            }

            if (index.Any("NET-002") && index.Any("NET-003"))
            {
                return new Core.Model.Correlation
                {
                    Id = "COR-003",
                    Title = "La machine ne dialogue plus avec la box",
                    Severity = Severity.Problem,
                    Narrative =
                        "La carte réseau n'a obtenu aucune adresse et la box ne répond pas. Les deux constats " +
                        "pointent vers le même endroit : le lien physique entre la machine et le routeur. " +
                        "Câble, prise murale ou association Wi-Fi sont à vérifier avant tout le reste.",
                    Contributors = index.KeysOf("NET-002", "NET-003"),
                    OrderedActions = new[] { Rec.CheckNetworkLink, Rec.RestartRouter },
                };
            }

            return null;
        }

        /// <summary>
        /// Le cas le plus fréquent en intervention : « mon PC est lent » n'a presque jamais une
        /// cause unique. Nommer les facteurs et les ordonner vaut mieux que les lister.
        /// </summary>
        private static Core.Model.Correlation? MultifactorSlowness(FindingIndex index)
        {
            var factors = new List<string>();
            if (index.Any("STO-001")) factors.Add("le disque système est saturé");
            if (index.Any("STO-009")) factors.Add("Windows tourne sur un disque mécanique");
            if (index.Any("PRF-001")) factors.Add("de nombreux programmes se lancent au démarrage");
            if (index.Any("PRF-003")) factors.Add("la mémoire est fortement sollicitée");
            if (index.Any("MEM-001")) factors.Add("la mémoire installée est insuffisante");

            if (factors.Count < 2) return null;

            var actions = new List<string>();
            if (index.Any("STO-001")) actions.Add(Rec.FreeDiskSpace);
            if (index.Any("PRF-001")) actions.Add(Rec.ReduceStartupItems);
            if (index.Any("MEM-001") || index.Any("PRF-003")) actions.Add(Rec.AddMemory);
            if (index.Any("STO-009")) actions.Add(Rec.UpgradeToSsd);

            return new Core.Model.Correlation
            {
                Id = "COR-004",
                Title = "Plusieurs facteurs expliquent les ralentissements",
                Severity = Severity.Problem,
                Narrative =
                    "La lenteur constatée n'a pas une cause unique : " + Join(factors) + ". " +
                    "Chacun de ces facteurs pris seul ne suffirait pas à expliquer le ressenti, mais ils " +
                    "s'additionnent. Les actions ci-dessous sont classées du gain le plus immédiat au plus " +
                    "structurel : traiter les premières donne déjà un résultat perceptible.",
                Contributors = index.KeysOf("STO-001", "STO-009", "PRF-001", "PRF-003", "MEM-001"),
                OrderedActions = actions,
            };
        }

        private static Core.Model.Correlation? HardwareInstability(FindingIndex index)
        {
            var bsod = index.Any("EVT-001");
            var shutdowns = index.Any("EVT-002");
            if (!bsod && !shutdowns) return null;
            if (!bsod || !shutdowns) return null;

            return new Core.Model.Correlation
            {
                Id = "COR-005",
                Title = "La machine est instable au niveau matériel",
                Severity = Severity.Problem,
                Narrative =
                    "Les écrans bleus et les arrêts brutaux se cumulent sur la même période. Cette combinaison " +
                    "oriente vers une cause matérielle (alimentation, mémoire ou surchauffe) plutôt que vers " +
                    "un défaut logiciel. Les rapports de plantage désignent le composant fautif : les analyser " +
                    "évite de remplacer du matériel au hasard.",
                Contributors = index.KeysOf("EVT-001", "EVT-002"),
                OrderedActions = new[] { Rec.AnalyzeCrashDumps, Rec.CheckMemory, Rec.CheckPowerSupply },
            };
        }

        private static Core.Model.Correlation? UndersizedMachine(FindingIndex index)
        {
            var limits = 0;
            if (index.Any("MEM-001")) limits++;
            if (index.Any("CPU-002")) limits++;
            if (index.Any("STO-009")) limits++;
            if (limits < 2) return null;

            return new Core.Model.Correlation
            {
                Id = "COR-006",
                Title = "Le matériel lui-même est le facteur limitant",
                Severity = Severity.Warning,
                Narrative =
                    "Plusieurs composants essentiels sont en dessous de ce qu'exige la version de Windows " +
                    "installée. Aucune intervention logicielle ne compensera durablement ce décalage : le " +
                    "nettoyage donnera un résultat temporaire, la mise à niveau matérielle un résultat durable. " +
                    "C'est une information à donner au client avant d'engager des heures de main-d'œuvre.",
                Contributors = index.KeysOf("MEM-001", "CPU-002", "STO-009"),
                OrderedActions = new[] { Rec.AddMemory, Rec.UpgradeToSsd, Rec.PlanHardwareUpgrade },
            };
        }

        private static void Add(List<Core.Model.Correlation> list, Core.Model.Correlation? correlation)
        {
            if (correlation != null) list.Add(correlation);
        }

        private static string Join(List<string> parts)
        {
            if (parts.Count == 1) return parts[0];
            var head = string.Join(", ", parts.GetRange(0, parts.Count - 1).ToArray());
            return head + " et " + parts[parts.Count - 1];
        }

        /// <summary>Index des constats par identifiant de règle, pour des corrélations lisibles.</summary>
        private sealed class FindingIndex
        {
            private readonly Dictionary<string, List<Finding>> _byRule =
                new Dictionary<string, List<Finding>>(StringComparer.OrdinalIgnoreCase);

            public FindingIndex(IReadOnlyList<Finding> findings)
            {
                foreach (var finding in findings)
                {
                    // Un constat d'information ne déclenche pas de corrélation : les corrélations
                    // annoncent des problèmes, pas des observations.
                    if (finding.Severity == Severity.Info) continue;

                    if (!_byRule.TryGetValue(finding.RuleId, out var list))
                    {
                        list = new List<Finding>();
                        _byRule[finding.RuleId] = list;
                    }
                    list.Add(finding);
                }
            }

            public bool Any(params string[] ruleIds)
            {
                foreach (var id in ruleIds)
                    if (_byRule.ContainsKey(id)) return true;
                return false;
            }

            public IReadOnlyList<string> KeysOf(params string[] ruleIds)
            {
                var keys = new List<string>();
                foreach (var id in ruleIds)
                {
                    if (!_byRule.TryGetValue(id, out var list)) continue;
                    foreach (var finding in list) keys.Add(finding.Key);
                }
                return keys;
            }
        }
    }
}
