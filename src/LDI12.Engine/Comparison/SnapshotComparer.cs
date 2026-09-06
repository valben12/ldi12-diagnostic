using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;

namespace LDI12.Engine.Comparison
{
    /// <summary>
    /// Compare deux diagnostics de la même machine.
    /// </summary>
    /// <remarks>
    /// Fonction pure, comme les règles : deux instantanés entrent, un <see cref="SnapshotDelta"/>
    /// sort. Aucun accès disque, aucune horloge : c'est ce qui permet de la tester sur des cas
    /// figés, et c'est indispensable pour un document que le client va lire comme la preuve que
    /// l'intervention a servi.
    ///
    /// Tout ce fichier tourne autour d'une seule tentation : celle de compter comme une réussite
    /// tout ce qui a disparu. Un constat absent du second diagnostic peut avoir trois causes très
    /// différentes : il est réglé, la règle n'a pas pu conclure, ou la sonde n'a pas tourné dans
    /// ce mode d'analyse. Une seule des trois est une réparation.
    /// </remarks>
    public static class SnapshotComparer
    {
        public static SnapshotDelta Compare(SystemSnapshot before, SystemSnapshot after)
        {
            if (before == null) throw new ArgumentNullException(nameof(before));
            if (after == null) throw new ArgumentNullException(nameof(after));

            var caveats = new List<string>();
            var scoreComparable = CheckComparability(before, after, caveats);

            return new SnapshotDelta
            {
                Before = Summarize(before),
                After = Summarize(after),
                SameMachine = CompareIdentity(before, after),
                ScoreComparable = scoreComparable,
                Caveats = caveats,
                Dimensions = CompareDimensions(before, after),
                Findings = CompareFindings(before, after),
                Measures = CompareMeasures(before, after),
            };
        }

        private static SnapshotSummary Summarize(SystemSnapshot snapshot) => new SnapshotSummary
        {
            SnapshotId = snapshot.Metadata.SnapshotId,
            CreatedAt = snapshot.Metadata.CreatedAt,
            RunMode = snapshot.Metadata.RunMode,
            ToolVersion = snapshot.Metadata.ToolVersion,
            SchemaVersion = snapshot.Metadata.SchemaVersion,
            ClientReference = snapshot.Metadata.ClientReference,
            MachineName = snapshot.Machine.MachineName,
            ProfileVersion = snapshot.Score?.ProfileVersion ?? string.Empty,
            Score = snapshot.Score?.Global,
        };

        // ============================================================ identité

        /// <summary>
        /// Les deux diagnostics viennent-ils de la même machine ?
        /// </summary>
        /// <remarks>
        /// L'empreinte est la seule réponse sûre. À défaut, le numéro de série de la machine, puis
        /// le nom du poste, mais ce dernier ne prouve rien : deux postes préparés depuis la même
        /// image portent souvent le même nom, et un même PC en change après réinstallation. Le
        /// rapprochement par le nom ressort donc <i>partiel</i>, avec sa réserve écrite.
        /// </remarks>
        private static Measured<bool> CompareIdentity(SystemSnapshot before, SystemSnapshot after)
        {
            var first = before.Machine;
            var second = after.Machine;

            if (first.Fingerprint.IsReliable && second.Fingerprint.IsReliable)
            {
                return Measured.Ok(
                    string.Equals(first.Fingerprint.Value, second.Fingerprint.Value, StringComparison.OrdinalIgnoreCase),
                    DataSource.Inferred);
            }

            if (first.SerialNumber.IsReliable && second.SerialNumber.IsReliable)
            {
                return Measured.Partial(
                    string.Equals(first.SerialNumber.Value, second.SerialNumber.Value, StringComparison.OrdinalIgnoreCase),
                    DataSource.Inferred,
                    "Rapprochement par le numéro de série de la machine, l'empreinte n'ayant pas été relevée.");
            }

            if (!string.IsNullOrWhiteSpace(first.MachineName) && !string.IsNullOrWhiteSpace(second.MachineName))
            {
                return Measured.Partial(
                    string.Equals(first.MachineName, second.MachineName, StringComparison.OrdinalIgnoreCase),
                    DataSource.Inferred,
                    "Rapprochement par le nom du poste seul : deux machines préparées depuis la même " +
                    "image le partagent souvent, et un même PC en change après réinstallation.");
            }

            return Measured.Missing<bool>(
                "Ni l'empreinte, ni le numéro de série, ni le nom du poste ne permettent de dire " +
                "si ces deux diagnostics viennent de la même machine.");
        }

        // ============================================================ réserves

        /// <summary>
        /// Réserves à poser avant toute conclusion, et verdict sur la comparabilité des scores.
        /// </summary>
        /// <remarks>
        /// Deux scores calculés avec des barèmes différents ne se soustraient pas : le second
        /// chiffre mesurerait autant le réglage des seuils que l'état de la machine. C'est écrit
        /// depuis la première version dans le modèle de scoring, le respecter ici en est la
        /// conséquence.
        /// </remarks>
        private static bool CheckComparability(SystemSnapshot before, SystemSnapshot after, ICollection<string> caveats)
        {
            var comparable = before.Score != null && after.Score != null;

            if (before.Score == null || after.Score == null)
            {
                caveats.Add("L'un des deux diagnostics n'a pas de score : seuls les constats se comparent.");
            }
            else if (!string.Equals(before.Score.ProfileVersion, after.Score.ProfileVersion, StringComparison.Ordinal))
            {
                comparable = false;
                caveats.Add(
                    "Les seuils ont changé entre les deux diagnostics (barème " +
                    Describe(before.Score.ProfileVersion) + " puis " + Describe(after.Score.ProfileVersion) +
                    ") : les scores ne se soustraient pas, ils ne mesurent pas la même chose.");
            }

            if (before.Metadata.SchemaVersion != after.Metadata.SchemaVersion)
            {
                caveats.Add(
                    "Les deux diagnostics n'ont pas la même version de format (" +
                    before.Metadata.SchemaVersion + " puis " + after.Metadata.SchemaVersion +
                    ") : certaines mesures du plus ancien peuvent manquer.");
            }

            if (before.Metadata.RunMode != after.Metadata.RunMode)
            {
                caveats.Add(
                    "Les deux diagnostics n'ont pas été menés dans le même mode (" +
                    DescribeMode(before.Metadata.RunMode) + " puis " + DescribeMode(after.Metadata.RunMode) +
                    ") : les contrôles exécutés ne sont pas les mêmes.");
            }

            if (after.Score != null && after.Score.EvaluatedRuleIds.Count == 0)
            {
                caveats.Add(
                    "Le second diagnostic n'indique pas quels contrôles ont conclu : les constats " +
                    "disparus sont rangés comme non revérifiés, faute de pouvoir affirmer qu'ils sont réglés.");
            }

            return comparable;
        }

        private static string Describe(string version)
            => string.IsNullOrWhiteSpace(version) ? "non précisé" : version;

        private static string DescribeMode(RunMode mode) => mode switch
        {
            RunMode.Quick => "analyse rapide",
            RunMode.Full => "analyse complète",
            _ => "sélection manuelle",
        };

        // ============================================================ constats

        private static IReadOnlyList<FindingChange> CompareFindings(SystemSnapshot before, SystemSnapshot after)
        {
            var changes = new List<FindingChange>();

            var afterByKey = new Dictionary<string, Finding>(StringComparer.Ordinal);
            foreach (var finding in after.Findings) afterByKey[finding.Key] = finding;

            var rechecked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (after.Score != null)
                foreach (var ruleId in after.Score.EvaluatedRuleIds) rechecked.Add(ruleId);

            foreach (var finding in before.Findings)
            {
                if (afterByKey.TryGetValue(finding.Key, out var current))
                {
                    changes.Add(new FindingChange
                    {
                        Finding = current,
                        Kind = FindingChangeKind.Persisting,
                        PreviousSeverity = current.Severity != finding.Severity ? finding.Severity : (Severity?)null,
                    });
                    continue;
                }

                // Le constat n'est plus là. Reste à savoir si quelqu'un a regardé.
                if (rechecked.Contains(finding.RuleId))
                {
                    changes.Add(new FindingChange { Finding = finding, Kind = FindingChangeKind.Resolved });
                }
                else
                {
                    changes.Add(new FindingChange
                    {
                        Finding = finding,
                        Kind = FindingChangeKind.NotRechecked,
                        Note = "Le contrôle « " + finding.RuleId + " » n'a pas conclu lors du second " +
                               "diagnostic : ce constat n'est pas revérifié, il n'est pas réglé pour autant.",
                    });
                }
            }

            var beforeKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var finding in before.Findings) beforeKeys.Add(finding.Key);

            foreach (var finding in after.Findings)
                if (!beforeKeys.Contains(finding.Key))
                    changes.Add(new FindingChange { Finding = finding, Kind = FindingChangeKind.Appeared });

            changes.Sort(Compare);
            return changes;

            static int Compare(FindingChange left, FindingChange right)
            {
                var byKind = left.Kind.CompareTo(right.Kind);
                if (byKind != 0) return byKind;

                var bySeverity = right.Finding.Severity.CompareTo(left.Finding.Severity);
                return bySeverity != 0 ? bySeverity : string.CompareOrdinal(left.Finding.Key, right.Finding.Key);
            }
        }

        private static IReadOnlyList<DimensionDelta> CompareDimensions(SystemSnapshot before, SystemSnapshot after)
        {
            var deltas = new List<DimensionDelta>();
            var seen = new HashSet<DiagnosticCategory>();

            var first = before.Score?.Dimensions ?? Array.Empty<DimensionScore>();
            var second = after.Score?.Dimensions ?? Array.Empty<DimensionScore>();

            foreach (var dimension in first)
            {
                seen.Add(dimension.Dimension);
                deltas.Add(new DimensionDelta
                {
                    Dimension = dimension.Dimension,
                    Before = dimension.Score,
                    After = Find(second, dimension.Dimension),
                });
            }

            foreach (var dimension in second)
            {
                if (seen.Contains(dimension.Dimension)) continue;
                deltas.Add(new DimensionDelta
                {
                    Dimension = dimension.Dimension,
                    Before = null,
                    After = dimension.Score,
                });
            }

            return deltas;
        }

        private static int? Find(IReadOnlyList<DimensionScore> dimensions, DiagnosticCategory category)
        {
            foreach (var dimension in dimensions)
                if (dimension.Dimension == category) return dimension.Score;
            return null;
        }

        // ============================================================ mesures

        /// <summary>
        /// Une mesure suivie d'un diagnostic à l'autre.
        /// </summary>
        /// <remarks>
        /// La marge n'est pas un détail de présentation. Deux relevés de la même machine ne
        /// tombent jamais sur le même chiffre, et annoncer « la température a augmenté » pour un
        /// degré d'écart transformerait du bruit en diagnostic.
        /// </remarks>
        private sealed class MeasureSpec
        {
            public string Label { get; init; } = string.Empty;
            public string Unit { get; init; } = string.Empty;
            public bool HigherIsBetter { get; init; }
            public double Margin { get; init; }
            public Func<SystemSnapshot, Measured<double>> Read { get; init; } = _ => Measured.NotCollected<double>();
        }

        /// <summary>
        /// Les mesures retenues décrivent un <b>état</b>, pas une météo.
        /// </summary>
        /// <remarks>
        /// La charge processeur instantanée et l'occupation mémoire du moment ont été écartées
        /// volontairement : elles dépendent de ce qui était ouvert à la seconde du relevé. Les
        /// comparer produirait des « améliorations » et des « dégradations » qui ne décrivent que
        /// le hasard du moment : exactement le genre de chiffre qui décrédibilise le reste du
        /// document quand le client le remet en question.
        /// </remarks>
        private static readonly MeasureSpec[] Specs =
        {
            new MeasureSpec
            {
                Label = "Espace libre du volume système", Unit = "Go", HigherIsBetter = true, Margin = 0.5,
                Read = s => SystemVolume(s).FreeBytes.Map(bytes => bytes / 1024d / 1024 / 1024),
            },
            new MeasureSpec
            {
                Label = "Programmes lancés au démarrage", Unit = "", HigherIsBetter = false, Margin = 0,
                Read = s => Count(s.Windows.Startup, item => item.Enabled),
            },
            new MeasureSpec
            {
                Label = "Entrées de démarrage orphelines", Unit = "", HigherIsBetter = false, Margin = 0,
                Read = s => Count(s.Windows.Startup, item => item.TargetMissing),
            },
            new MeasureSpec
            {
                Label = "Durée de démarrage", Unit = "s", HigherIsBetter = false, Margin = 5,
                Read = s => s.Performance.Boot.Duration.Map(d => d.TotalSeconds),
            },
            new MeasureSpec
            {
                Label = "Secteurs réalloués du disque système", Unit = "", HigherIsBetter = false, Margin = 0,
                Read = s => SystemDisk(s).Smart.ReallocatedSectors.Map(v => (double)v),
            },
            new MeasureSpec
            {
                Label = "Secteurs en attente de réallocation", Unit = "", HigherIsBetter = false, Margin = 0,
                Read = s => SystemDisk(s).Smart.PendingSectors.Map(v => (double)v),
            },
            new MeasureSpec
            {
                Label = "Jours depuis la dernière mise à jour", Unit = "jours", HigherIsBetter = false, Margin = 0,
                Read = s => s.Windows.Updates.DaysSinceLastUpdate.Map(v => (double)v),
            },
            new MeasureSpec
            {
                Label = "Mémoire validée rapportée à la mémoire installée", Unit = "%", HigherIsBetter = false, Margin = 10,
                Read = s => s.Performance.Responsiveness.CommitRatioPercent,
            },
            new MeasureSpec
            {
                Label = "Température du processeur", Unit = "°C", HigherIsBetter = false, Margin = 5,
                Read = s => s.Hardware.Thermal.CpuPackageCelsius,
            },
        };

        private static IReadOnlyList<MeasureChange> CompareMeasures(SystemSnapshot before, SystemSnapshot after)
        {
            var changes = new List<MeasureChange>();

            foreach (var spec in Specs)
            {
                var first = spec.Read(before);
                var second = spec.Read(after);

                // Une mesure absente des deux côtés n'a rien à faire dans un comparatif : elle
                // ajouterait une ligne « non mesuré / non mesuré » qui n'apprend rien.
                if (!first.HasValue && !second.HasValue) continue;

                changes.Add(Build(spec, first, second));
            }

            return changes;
        }

        private static MeasureChange Build(MeasureSpec spec, Measured<double> before, Measured<double> after)
        {
            if (!before.IsReliable || !after.IsReliable)
            {
                return new MeasureChange
                {
                    Label = spec.Label,
                    Unit = spec.Unit,
                    Before = before,
                    After = after,
                    HigherIsBetter = spec.HigherIsBetter,
                    Direction = ChangeDirection.Unknown,
                    Note = Explain(before, after),
                };
            }

            var delta = after.Value - before.Value;
            var direction = Math.Abs(delta) <= spec.Margin
                ? ChangeDirection.Unchanged
                : delta > 0 == spec.HigherIsBetter
                    ? ChangeDirection.Improved
                    : ChangeDirection.Worsened;

            return new MeasureChange
            {
                Label = spec.Label,
                Unit = spec.Unit,
                Before = before,
                After = after,
                HigherIsBetter = spec.HigherIsBetter,
                Direction = direction,
                Delta = delta,
            };
        }

        /// <summary>
        /// Pourquoi l'évolution ne peut pas être établie, et de quel côté manque la mesure.
        /// </summary>
        private static string Explain(Measured<double> before, Measured<double> after)
        {
            if (before.IsReliable && !after.IsReliable)
            {
                return "Mesurée au premier diagnostic, plus au second" +
                       (after.Reason == null ? "." : " : " + after.Reason);
            }

            if (!before.IsReliable && after.IsReliable)
            {
                return "Non mesurée au premier diagnostic" +
                       (before.Reason == null ? "." : " : " + before.Reason) +
                       " L'évolution ne peut donc pas être établie.";
            }

            return "Mesure absente des deux diagnostics.";
        }

        // ============================================================ extraction

        private static VolumeInfo SystemVolume(SystemSnapshot snapshot)
        {
            foreach (var volume in snapshot.Storage.Volumes)
                if (volume.IsSystemVolume.Or(false)) return volume;
            return new VolumeInfo();
        }

        private static PhysicalDiskInfo SystemDisk(SystemSnapshot snapshot)
        {
            foreach (var disk in snapshot.Storage.PhysicalDisks)
                if (disk.IsSystemDisk.Or(false)) return disk;
            return new PhysicalDiskInfo();
        }

        /// <summary>
        /// Compte d'éléments d'une liste collectée.
        /// </summary>
        /// <remarks>
        /// Une liste vide est ambiguë : la machine n'a rien au démarrage, ou la sonde n'a pas
        /// tourné. Un zéro comparé à un zéro dirait « rien n'a changé » dans les deux cas. La
        /// mesure est donc absente quand la liste l'est.
        /// </remarks>
        private static Measured<double> Count<T>(IReadOnlyList<T> items, Func<T, bool> predicate)
        {
            if (items.Count == 0)
                return Measured.Missing<double>("Aucun élément relevé : la sonde correspondante n'a pas tourné.");

            var count = 0;
            foreach (var item in items)
                if (predicate(item)) count++;

            return Measured.Ok((double)count, DataSource.Inferred);
        }
    }
}
