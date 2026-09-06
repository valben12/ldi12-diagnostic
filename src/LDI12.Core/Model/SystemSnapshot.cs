using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;
using LDI12.Core.Platform;
using LDI12.Core.Probes;

namespace LDI12.Core.Model
{
    public enum RunMode
    {
        /// <summary>Lecture pure, aucun processus externe. Environ 15 secondes.</summary>
        Quick = 0,

        /// <summary>Inclut les outils système en lecture seule et les tests réseau. 2 à 4 minutes.</summary>
        Full = 1,

        /// <summary>Sélection manuelle du technicien.</summary>
        Custom = 2,
    }

    public sealed class SnapshotMetadata
    {
        public int SchemaVersion { get; init; } = SystemSnapshot.CurrentSchemaVersion;

        public Guid SnapshotId { get; init; } = Guid.NewGuid();

        public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

        public string ToolVersion { get; init; } = string.Empty;

        public RunMode RunMode { get; init; }

        public long DurationMs { get; init; }

        /// <summary>Renseigné par le technicien avant l'export du rapport. Facultatif.</summary>
        public string? Technician { get; init; }

        /// <summary>Référence du dossier client (future clé de rapprochement Klarvi). Facultatif.</summary>
        public string? ClientReference { get; init; }
    }

    public sealed class MachineIdentity
    {
        public string MachineName { get; init; } = string.Empty;

        public string UserName { get; init; } = string.Empty;

        /// <summary>
        /// Empreinte stable de la machine, destinée à rapprocher deux diagnostics du même PC
        /// même après réinstallation de Windows. Complétée en phase 1 avec l'UUID de carte mère
        /// et les numéros de série des disques.
        /// </summary>
        public Measured<string> Fingerprint { get; init; } = Measured.NotCollected<string>();

        public Measured<string> Manufacturer { get; init; } = Measured.NotCollected<string>();

        public Measured<string> Model { get; init; } = Measured.NotCollected<string>();

        public Measured<string> SerialNumber { get; init; } = Measured.NotCollected<string>();
    }

    public sealed class PlatformSnapshot
    {
        public WindowsProfile Windows { get; init; } = new WindowsProfile();

        public ElevationState Elevation { get; init; }

        public IReadOnlyList<FeatureState> Features { get; init; } = Array.Empty<FeatureState>();
    }

    /// <summary>
    /// Compte rendu d'exécution d'une sonde. Présent dans le rapport même en cas d'échec :
    /// « pourquoi cette information manque » est une information.
    /// </summary>
    public sealed class ModuleReport
    {
        public string ProbeId { get; init; } = string.Empty;

        public string DisplayName { get; init; } = string.Empty;

        public DiagnosticCategory Category { get; init; }

        public ProbeStatus Status { get; init; }

        public long DurationMs { get; init; }

        public string? Message { get; init; }

        /// <summary>Type et message de l'exception. La trace complète va dans le journal, pas dans le rapport.</summary>
        public string? ExceptionSummary { get; init; }
    }

    /// <summary>
    /// Agrégat racine, immuable et sérialisable. Contrat unique entre la collecte, l'analyse,
    /// l'interface, les rapports et la future intégration Klarvi.
    /// </summary>
    /// <remarks>
    /// Un snapshot peut être relu des mois plus tard pour comparer un avant / après réparation,
    /// d'où <see cref="CurrentSchemaVersion"/> dès la première version.
    /// </remarks>
    public sealed class SystemSnapshot
    {
        public const int CurrentSchemaVersion = 1;

        public SnapshotMetadata Metadata { get; init; } = new SnapshotMetadata();

        public MachineIdentity Machine { get; init; } = new MachineIdentity();

        public PlatformSnapshot Platform { get; init; } = new PlatformSnapshot();

        public HardwareSnapshot Hardware { get; init; } = new HardwareSnapshot();

        public StorageSnapshot Storage { get; init; } = new StorageSnapshot();

        public WindowsSnapshot Windows { get; init; } = new WindowsSnapshot();

        public NetworkSnapshot Network { get; init; } = new NetworkSnapshot();

        public SecuritySnapshot Security { get; init; } = new SecuritySnapshot();

        public PerformanceSnapshot Performance { get; init; } = new PerformanceSnapshot();

        public IReadOnlyList<ModuleReport> ModuleReports { get; init; } = Array.Empty<ModuleReport>();

        /// <summary>Constats produits par le moteur de règles. Vide tant que l'analyse n'a pas tourné.</summary>
        public IReadOnlyList<Finding> Findings { get; init; } = Array.Empty<Finding>();

        /// <summary>Conclusions de second niveau, reliant plusieurs constats.</summary>
        public IReadOnlyList<Correlation> Correlations { get; init; } = Array.Empty<Correlation>();

        /// <summary>Actions recommandées, dédoublonnées et triées par priorité puis par rapport impact/effort.</summary>
        public IReadOnlyList<Recommendation> Recommendations { get; init; } = Array.Empty<Recommendation>();

        /// <summary>Score global et par dimension, avec le grand livre des pénalités.</summary>
        public DiagnosticScore? Score { get; init; }

        /// <summary>
        /// Le même instantané, augmenté du résultat de l'analyse.
        /// </summary>
        /// <remarks>
        /// Existe pour fermer un piège rencontré en vrai : le moteur d'analyse reconstruisait
        /// l'instantané en énumérant ses sections, si bien qu'une section ajoutée plus tard 
        /// (Sécurité, Performances) était collectée correctement puis perdue au passage suivant,
        /// sans erreur, sans avertissement, avec pour seul symptôme un rapport vide.
        /// <para>
        /// La copie est ici, à côté de la déclaration des sections : ajouter une section et
        /// oublier de la recopier demande maintenant de modifier deux lignes voisines plutôt que
        /// deux fichiers éloignés. Un test le vérifie par réflexion, pour le cas où.
        /// </para>
        /// </remarks>
        public SystemSnapshot WithAnalysis(
            IReadOnlyList<Finding> findings,
            IReadOnlyList<Correlation> correlations,
            IReadOnlyList<Recommendation> recommendations,
            DiagnosticScore? score)
            => new SystemSnapshot
            {
                Metadata = Metadata,
                Machine = Machine,
                Platform = Platform,
                Hardware = Hardware,
                Storage = Storage,
                Windows = Windows,
                Network = Network,
                Security = Security,
                Performance = Performance,
                ModuleReports = ModuleReports,

                Findings = findings,
                Correlations = correlations,
                Recommendations = recommendations,
                Score = score,
            };
    }
}
