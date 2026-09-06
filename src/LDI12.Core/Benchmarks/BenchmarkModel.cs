using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;

namespace LDI12.Core.Benchmarks
{
    public enum BenchmarkKind
    {
        SequentialWrite = 0,
        SequentialRead = 1,

        /// <summary>Accès isolés de quatre kilo-octets, tirés au hasard dans le fichier.</summary>
        RandomRead = 2,

        Cpu = 3,

        /// <summary>Le même travail réparti sur tous les cœurs logiques.</summary>
        CpuParallel = 4,

        Memory = 5,
    }

    /// <summary>Une grandeur mesurée, avec de quoi l'afficher sans la réinterpréter.</summary>
    public sealed class BenchmarkMeasure
    {
        public BenchmarkKind Kind { get; init; }

        public string Label { get; init; } = string.Empty;

        /// <summary>Ce que la mesure décrit, en une ligne. Un chiffre sans légende ne se lit pas.</summary>
        public string Meaning { get; init; } = string.Empty;

        public Measured<double> Value { get; init; }

        public string Display { get; init; } = string.Empty;
    }

    /// <summary>
    /// Ce qu'une mesure permet de dire de la machine.
    /// </summary>
    /// <remarks>
    /// Séparée de la mesure elle-même : un débit est un fait, « ce disque est mécanique » est une
    /// déduction, et le client doit pouvoir suivre le passage de l'un à l'autre.
    /// </remarks>
    public sealed class BenchmarkVerdict
    {
        public string Id { get; init; } = string.Empty;

        public Severity Severity { get; init; }

        public string Statement { get; init; } = string.Empty;

        public string Explanation { get; init; } = string.Empty;
    }

    /// <summary>
    /// Ce qui va être écrit sur le disque du client, avant de l'être.
    /// </summary>
    /// <remarks>
    /// La mesure d'un disque est la seule opération du logiciel qui écrive un fichier sur la
    /// machine du client sans qu'il s'agisse d'une réparation. Elle est donc traitée comme une
    /// action : le chemin exact, la taille exacte et l'espace disponible sont affichés avant, et
    /// le fichier est supprimé après, y compris si la mesure échoue ou si elle est annulée.
    /// </remarks>
    public sealed class StorageBenchmarkPlan
    {
        public string FilePath { get; init; } = string.Empty;

        /// <summary>« C: », tel qu'il s'affiche.</summary>
        public string Volume { get; init; } = string.Empty;

        public long Bytes { get; init; }

        public int BlockBytes { get; init; }

        /// <summary>Nombre d'accès isolés du test aléatoire.</summary>
        public int RandomReads { get; init; }

        public Measured<long> FreeBytes { get; init; }

        /// <summary>
        /// Renseigné si et seulement si la mesure ne doit pas être lancée.
        /// </summary>
        /// <remarks>
        /// Un refus expliqué, jamais un bouton grisé : « il ne reste pas assez de place » est en
        /// soi un résultat de diagnostic.
        /// </remarks>
        public string? Refusal { get; init; }

        public bool CanRun => Refusal == null;
    }

    public sealed class StorageThroughput
    {
        public Measured<double> WriteBytesPerSecond { get; init; }

        public Measured<double> ReadBytesPerSecond { get; init; }

        /// <summary>Temps moyen d'un accès isolé, la mesure qui explique « mon PC rame ».</summary>
        public Measured<double> RandomReadMilliseconds { get; init; }

        public Measured<double> RandomReadsPerSecond { get; init; }

        /// <summary>Ce que la mesure n'a pas pu établir, et pourquoi.</summary>
        public string? Limitation { get; init; }
    }

    /// <summary>
    /// Une campagne de mesures et ce qu'elle vaut.
    /// </summary>
    /// <remarks>
    /// <b>Un chiffre de performance ne vaut que par ses conditions.</b> Une machine déjà occupée,
    /// un portable sur batterie, un disque presque plein : chacun suffit à faire d'un mauvais
    /// résultat un artefact. Les réserves ne sont donc pas un ornement du rapport, elles sont
    /// relevées au moment de la mesure et voyagent avec elle.
    /// </remarks>
    public sealed class BenchmarkRun
    {
        public DateTimeOffset At { get; init; } = DateTimeOffset.Now;

        public TimeSpan Duration { get; init; }

        public IReadOnlyList<BenchmarkMeasure> Measures { get; init; } = Array.Empty<BenchmarkMeasure>();

        public IReadOnlyList<BenchmarkVerdict> Verdicts { get; init; } = Array.Empty<BenchmarkVerdict>();

        public IReadOnlyList<string> Caveats { get; init; } = Array.Empty<string>();

        public string Headline { get; init; } = string.Empty;
    }

    /// <summary>Mesure de débit d'un volume, par la couche plateforme.</summary>
    public interface IStorageBenchmarkGateway
    {
        /// <summary>
        /// Établit ce qui serait écrit, sans rien écrire.
        /// </summary>
        StorageBenchmarkPlan Plan(string volumeRoot, long bytes);

        /// <summary>
        /// Exécute la mesure décrite par le plan et supprime son fichier.
        /// </summary>
        StorageThroughput Measure(
            StorageBenchmarkPlan plan, IProgress<string>? progress, System.Threading.CancellationToken cancellationToken);
    }
}
