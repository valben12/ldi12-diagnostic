using System;
using LDI12.Core.Diagnostics;

namespace LDI12.Core.Model
{
    /// <summary>D'où la machine tient son heure.</summary>
    public enum TimeSource
    {
        Unknown = 0,

        /// <summary>Un serveur de temps sur Internet ou sur le réseau local.</summary>
        Ntp = 1,

        /// <summary>La hiérarchie du domaine Active Directory. Le bon réglage d'un poste joint.</summary>
        Domain = 2,

        /// <summary>Aucune synchronisation : l'horloge dérive librement.</summary>
        None = 3,
    }

    /// <summary>Ce qui remet l'horloge à l'heure, et quand cela s'est produit pour la dernière fois.</summary>
    public sealed class TimeSyncInfo
    {
        public Measured<DateTimeOffset> LastSynchronised { get; init; }

        public Measured<string> Server { get; init; }

        public Measured<TimeSource> Source { get; init; }

        /// <summary>
        /// Service de temps désactivé.
        /// </summary>
        /// <remarks>
        /// <b>Désactivé, et non arrêté.</b> Depuis Windows 10, W32Time démarre à la demande et
        /// s'arrête aussitôt son travail fait : le trouver arrêté est l'état normal de presque
        /// toutes les machines. Signaler cet état ferait sonner l'alerte partout, ce qui revient
        /// à ne plus la faire sonner nulle part.
        /// </remarks>
        public Measured<bool> ServiceDisabled { get; init; }
    }

    /// <summary>
    /// L'heure de la machine, et de quoi juger si elle est juste.
    /// </summary>
    /// <remarks>
    /// <b>Une horloge fausse est le point de défaillance unique le plus trompeur de Windows.</b>
    /// Elle ne casse rien de visible : elle fait échouer toutes les connexions sécurisées à la
    /// fois, refuse les mises à jour, invalide l'activation et bloque l'ouverture de session sur
    /// un domaine. Le client dit « je n'ai plus Internet », le technicien cherche du côté du
    /// réseau, et personne ne regarde le coin de l'écran.
    /// </remarks>
    public sealed class SystemTimeInfo
    {
        public Measured<DateTimeOffset> SystemTime { get; init; }

        public Measured<string> TimeZone { get; init; }

        public Measured<TimeSpan> UtcOffset { get; init; }

        /// <summary>Passage automatique à l'heure d'été. Désactivé, l'horloge est fausse d'une heure la moitié de l'année.</summary>
        public Measured<bool> DaylightAdjustment { get; init; }

        public Measured<bool> InDaylightSaving { get; init; }

        /// <summary>
        /// Date la plus récente portée par un fichier du système.
        /// </summary>
        /// <remarks>
        /// <b>La seule référence de temps qui ne vienne pas de l'horloge elle-même.</b> Les
        /// fichiers de Windows portent la date à laquelle Microsoft les a compilés, recopiée
        /// telle quelle à l'installation d'une mise à jour. La machine ne peut donc pas être
        /// antérieure à eux, et si son horloge l'affirme, c'est l'horloge qui a tort.
        /// </remarks>
        public Measured<DateTimeOffset> NewestSystemFile { get; init; }

        public TimeSyncInfo Sync { get; init; } = new TimeSyncInfo();
    }
}
