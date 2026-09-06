using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;

namespace LDI12.Core.Execution
{
    public sealed class MemoryStatus
    {
        public long TotalBytes { get; init; }
        public long AvailableBytes { get; init; }
        public long CommittedBytes { get; init; }
        public long CommitLimitBytes { get; init; }
        public double UsagePercent { get; init; }
    }

    /// <summary>
    /// Mesures système obtenues par API native.
    /// </summary>
    /// <remarks>
    /// Ces lectures sont préférées à WMI partout où c'est possible : elles coûtent quelques
    /// microsecondes, ne dépendent pas du dépôt WMI (souvent dégradé sur les machines à
    /// dépanner) et ne sont pas localisées.
    /// </remarks>
    public interface INativeSystemApi
    {
        Measured<MemoryStatus> ReadMemoryStatus();

        /// <summary>
        /// Charge processeur mesurée sur une fenêtre glissante, par différence de
        /// <c>GetSystemTimes</c>. Une lecture instantanée n'a aucun sens : il faut deux points.
        /// </summary>
        Task<Measured<double>> ReadCpuUsagePercentAsync(TimeSpan sampleWindow, CancellationToken cancellationToken);

        /// <summary>Temps écoulé depuis le démarrage, sans passer par WMI.</summary>
        Measured<TimeSpan> ReadUptime();

        /// <summary>
        /// Mode de démarrage UEFI ou BIOS hérité. Déterminé par la disponibilité des variables
        /// de firmware, ce qui fonctionne dès Windows 7.
        /// </summary>
        Measured<FirmwareMode> ReadFirmwareMode();

        /// <summary>
        /// Consommation des processus, échantillonnée sur une fenêtre.
        /// </summary>
        /// <remarks>
        /// Comme pour la charge processeur, une lecture instantanée n'a aucun sens : le temps
        /// processeur d'un processus est un cumul depuis son lancement, il faut deux points pour
        /// en tirer un pourcentage.
        /// </remarks>
        Task<Measured<IReadOnlyList<ProcessUsage>>> ReadProcessUsageAsync(
            TimeSpan sampleWindow, CancellationToken cancellationToken);

        /// <summary>
        /// Vrai quand la machine fonctionne sur sa batterie.
        /// </summary>
        /// <remarks>
        /// Lu par <c>GetSystemPowerStatus</c> plutôt que déduit de l'état d'une batterie : la
        /// question n'est pas « y a-t-il une batterie » mais « le secteur est-il branché », et
        /// c'est la seule source qui y réponde par oui ou par non. La réponse compte pour toute
        /// mesure de performance : Windows bride volontairement processeur et disque sur
        /// batterie, et un chiffre relevé dans cet état décrit un réglage, pas une machine.
        /// </remarks>
        Measured<bool> ReadRunningOnBattery();

        /// <summary>
        /// Comptes locaux de la machine et appartenance au groupe Administrateurs.
        /// </summary>
        /// <remarks>
        /// Par <c>NetUserEnum</c> et <c>NetLocalGroupGetMembers</c> plutôt que par WMI : ces
        /// appels répondent en quelques millisecondes et ne dépendent pas du dépôt WMI, souvent
        /// dégradé sur les machines à dépanner. Sur une machine jointe à un domaine, seuls les
        /// comptes locaux sont énumérés : c'est ce qu'on veut, et il faut le dire.
        /// </remarks>
        Measured<IReadOnlyList<LocalAccountInfo>> ReadLocalAccounts();
    }
}
