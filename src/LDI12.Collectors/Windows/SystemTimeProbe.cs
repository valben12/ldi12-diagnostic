using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Core.Probes;
using Microsoft.Win32;

namespace LDI12.Collectors.Windows
{
    /// <summary>
    /// L'heure de la machine, et de quoi juger si elle est juste.
    /// </summary>
    /// <remarks>
    /// <b>Une horloge fausse est le point de défaillance unique le plus trompeur de Windows.</b>
    /// Elle ne casse rien de visible : elle fait échouer toutes les connexions sécurisées à la
    /// fois, refuse les mises à jour, invalide l'activation et bloque l'ouverture de session sur
    /// un domaine. Le client dit « je n'ai plus Internet », le technicien cherche du côté du
    /// réseau, et personne ne regarde le coin de l'écran.
    /// <para>
    /// Le relevé porte aussi une <b>référence de temps indépendante de l'horloge</b> : la date
    /// des fichiers de Windows, qui vient de chez Microsoft et non de cette machine. Elle seule
    /// permet de démontrer qu'une horloge est fausse sans rien demander au réseau.
    /// </para>
    /// </remarks>
    public sealed class SystemTimeProbe : IDiagnosticProbe
    {
        private const string TimeZoneKey = @"SYSTEM\CurrentControlSet\Control\TimeZoneInformation";

        private const string W32TimeParameters = @"SYSTEM\CurrentControlSet\Services\W32Time\Parameters";

        private const string W32TimeService = @"SYSTEM\CurrentControlSet\Services\W32Time";

        /// <summary>Journal du service de temps. Absent ou vide sur les installations anciennes.</summary>
        private const string TimeLog = "Microsoft-Windows-Time-Service/Operational";

        /// <summary>
        /// « Le service W32time a défini l'heure du système sur… », une synchronisation appliquée.
        /// </summary>
        /// <remarks>
        /// L'identifiant vient du manifeste ; la description est traduite et n'est jamais lue.
        /// C'est la seule trace fiable d'une remise à l'heure réussie : le service peut être
        /// interrogé sans succès pendant des semaines sans que rien d'autre ne le dise.
        /// </remarks>
        private const int TimeSetEvent = 261;

        /// <summary>Même événement, dans le journal système des versions plus anciennes de Windows.</summary>
        private const int LegacySyncEvent = 35;

        /// <summary>
        /// Fichiers dont la date sert de plancher.
        /// </summary>
        /// <remarks>
        /// Trois fichiers du noyau, touchés par presque toutes les mises à jour cumulatives. Le
        /// but n'est pas de trouver le fichier le plus récent de Windows (il n'y a rien à gagner
        /// à en parcourir trois mille) mais d'obtenir une date qui soit à coup sûr inférieure ou
        /// égale à la dernière mise à jour installée.
        /// </remarks>
        private static readonly string[] Anchors = { "ntdll.dll", "kernel32.dll", "user32.dll" };

        public ProbeDescriptor Descriptor { get; } = new ProbeDescriptor
        {
            Id = ProbeIds.SystemTime,
            DisplayName = "Heure et synchronisation",
            Category = DiagnosticCategory.Windows,
            EstimatedDuration = TimeSpan.FromSeconds(1),
            HardTimeout = TimeSpan.FromSeconds(25),
        };

        public Task<ProbeOutcome> ExecuteAsync(ProbeContext context, CancellationToken cancellationToken)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            var now = DateTimeOffset.Now;
            var zone = ReadZone(context);

            var info = new SystemTimeInfo
            {
                SystemTime = Measured.Ok(now, DataSource.NativeApi),
                TimeZone = zone.Name,
                UtcOffset = zone.Offset,
                DaylightAdjustment = zone.Adjustment,
                InDaylightSaving = zone.InDaylight,
                NewestSystemFile = ReadAnchor(),
                Sync = ReadSync(context, cancellationToken),
            };

            context.Draft.SetSystemTime(info);

            var parts = new List<string> { "heure locale " + now.ToString("g", CultureInfo.CurrentCulture) };

            if (zone.Name.HasValue) parts.Add(zone.Name.Value);
            if (info.Sync.LastSynchronised.HasValue)
                parts.Add("dernière synchronisation le " + ValueFormat.Date(info.Sync.LastSynchronised.Value));
            if (info.Sync.ServiceDisabled.Or(false)) parts.Add("service de temps désactivé");

            return Task.FromResult(ProbeOutcome.Ok(string.Join(", ", parts)));
        }

        // ================================================================= fuseau

        private readonly struct Zone
        {
            public Zone(Measured<string> name, Measured<TimeSpan> offset,
                        Measured<bool> adjustment, Measured<bool> inDaylight)
            {
                Name = name;
                Offset = offset;
                Adjustment = adjustment;
                InDaylight = inDaylight;
            }

            public Measured<string> Name { get; }
            public Measured<TimeSpan> Offset { get; }
            public Measured<bool> Adjustment { get; }
            public Measured<bool> InDaylight { get; }
        }

        /// <summary>
        /// Le fuseau et le réglage d'heure d'été.
        /// </summary>
        /// <remarks>
        /// Le fuseau vient du cadre .NET, qui donne son nom traduit ; le réglage d'heure d'été
        /// vient du registre, parce que le cadre dit ce que le fuseau <i>permet</i> et non ce que
        /// la machine <i>applique</i>. Un fuseau qui connaît l'heure d'été sur une machine où
        /// l'ajustement est coupé donne une horloge fausse d'une heure la moitié de l'année.
        /// </remarks>
        private static Zone ReadZone(ProbeContext context)
        {
            Measured<string> name;
            Measured<TimeSpan> offset;
            Measured<bool> inDaylight;
            var supportsDaylight = false;

            try
            {
                var local = TimeZoneInfo.Local;
                name = Measured.Ok(local.DisplayName, DataSource.NativeApi);
                offset = Measured.Ok(local.GetUtcOffset(DateTimeOffset.Now), DataSource.NativeApi);
                supportsDaylight = local.SupportsDaylightSavingTime;
                inDaylight = Measured.Ok(local.IsDaylightSavingTime(DateTimeOffset.Now), DataSource.NativeApi);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException || ex is InvalidTimeZoneException ||
                                       ex is System.Security.SecurityException)
            {
                name = Measured.Missing<string>("Le fuseau horaire de la machine n'a pas pu être lu.");
                offset = Measured.Missing<TimeSpan>("Le décalage horaire n'a pas pu être lu.");
                inDaylight = Measured.Missing<bool>("L'état d'heure d'été n'a pas pu être lu.");
            }

            var disabled = context.Registry.ReadInt32(
                RegistryHive.LocalMachine, TimeZoneKey, "DynamicDaylightTimeDisabled");

            var adjustment = !supportsDaylight
                ? Measured.Missing<bool>("Ce fuseau horaire ne pratique pas l'heure d'été.")
                : Measured.Ok(disabled == null || disabled.Value == 0, DataSource.Registry);

            return new Zone(name, offset, adjustment, inDaylight);
        }

        // ================================================================= synchronisation

        private static TimeSyncInfo ReadSync(ProbeContext context, CancellationToken cancellationToken)
        {
            var type = context.Registry.ReadString(RegistryHive.LocalMachine, W32TimeParameters, "Type");
            var server = context.Registry.ReadString(RegistryHive.LocalMachine, W32TimeParameters, "NtpServer");
            var start = context.Registry.ReadInt32(RegistryHive.LocalMachine, W32TimeService, "Start");

            return new TimeSyncInfo
            {
                Source = Translate(type),
                Server = string.IsNullOrWhiteSpace(server)
                    ? Measured.Missing<string>("Aucun serveur de temps n'est déclaré.")
                    // Le serveur porte des drapeaux après une virgule (« ,0x9 ») qui ne
                    // regardent que le service : le technicien veut le nom.
                    : Measured.Ok(server!.Split(',')[0].Trim(), DataSource.Registry),

                // 4 signifie « désactivé » dans le mode de démarrage d'un service. Les autres
                // valeurs (dont 3, « manuel », qui est l'état normal depuis Windows 10) ne
                // disent rien de fâcheux.
                ServiceDisabled = start == null
                    ? Measured.Missing<bool>("Le mode de démarrage du service de temps n'a pas pu être lu.")
                    : Measured.Ok(start.Value == 4, DataSource.Registry),

                LastSynchronised = ReadLastSync(cancellationToken),
            };
        }

        private static Measured<TimeSource> Translate(string? type)
        {
            if (string.IsNullOrWhiteSpace(type))
                return Measured.Missing<TimeSource>("La source de temps n'est pas déclarée.");

            switch (type!.Trim().ToUpperInvariant())
            {
                case "NTP": return Measured.Ok(TimeSource.Ntp, DataSource.Registry);
                case "NT5DS": return Measured.Ok(TimeSource.Domain, DataSource.Registry);
                case "NOSYNC": return Measured.Ok(TimeSource.None, DataSource.Registry);

                // « AllSync » interroge le domaine et les serveurs déclarés : c'est bien une
                // synchronisation par le domaine, avec un secours.
                case "ALLSYNC": return Measured.Ok(TimeSource.Domain, DataSource.Registry);
                default: return Measured.Missing<TimeSource>("Source de temps inconnue : " + type);
            }
        }

        /// <summary>
        /// La dernière remise à l'heure réussie.
        /// </summary>
        /// <remarks>
        /// Cherchée dans le journal du service de temps, puis dans le journal système des
        /// versions plus anciennes. Les deux peuvent être vides (un journal purgé, une machine
        /// réinstallée la veille) et l'absence est alors dite comme telle : elle ne prouve pas
        /// qu'aucune synchronisation n'a eu lieu.
        /// </remarks>
        private static Measured<DateTimeOffset> ReadLastSync(CancellationToken cancellationToken)
        {
            var operational = Newest(TimeLog, TimeSetEvent, cancellationToken);
            if (operational != null) return Measured.Ok(operational.Value, DataSource.EventLog);

            var system = Newest("System", LegacySyncEvent, cancellationToken);
            if (system != null) return Measured.Ok(system.Value, DataSource.EventLog);

            return Measured.Missing<DateTimeOffset>(
                "Aucune remise à l'heure n'apparaît dans les journaux. Ils peuvent avoir été purgés : " +
                "cela ne prouve pas qu'il n'y en a jamais eu.");
        }

        private static DateTimeOffset? Newest(string log, int eventId, CancellationToken cancellationToken)
        {
            var xpath = "*[System[EventID=" + eventId.ToString(CultureInfo.InvariantCulture) + "]]";

            try
            {
                var query = new EventLogQuery(log, PathType.LogName, xpath) { ReverseDirection = true };
                using var reader = new EventLogReader(query);

                for (var read = 0; read < 8; read++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    EventRecord? record;
                    try { record = reader.ReadEvent(); }
                    catch (EventLogException) { return null; }
                    if (record == null) return null;

                    using (record)
                        if (record.TimeCreated.HasValue) return new DateTimeOffset(record.TimeCreated.Value);
                }
            }
            catch (Exception ex) when (ex is EventLogNotFoundException || ex is EventLogException ||
                                       ex is UnauthorizedAccessException)
            {
                return null;
            }

            return null;
        }

        // ================================================================= référence indépendante

        /// <summary>
        /// La date la plus récente portée par un fichier du noyau de Windows.
        /// </summary>
        /// <remarks>
        /// <b>La seule référence de temps qui ne vienne pas de l'horloge de la machine.</b> Ces
        /// fichiers portent la date à laquelle Microsoft les a compilés, recopiée telle quelle à
        /// l'installation d'une mise à jour : elle ne dépend ni de l'heure locale ni du fuseau.
        /// La machine ne peut donc pas être antérieure à eux, et si son horloge l'affirme, c'est
        /// l'horloge qui a tort.
        /// </remarks>
        private static Measured<DateTimeOffset> ReadAnchor()
        {
            string system;
            try
            {
                system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            }
            catch (Exception)
            {
                return Measured.Missing<DateTimeOffset>("Le dossier système n'a pas pu être localisé.");
            }

            if (string.IsNullOrEmpty(system))
                return Measured.Missing<DateTimeOffset>("Le dossier système n'a pas pu être localisé.");

            DateTimeOffset? newest = null;

            foreach (var name in Anchors)
            {
                try
                {
                    var file = new FileInfo(Path.Combine(system, name));
                    if (!file.Exists) continue;

                    var written = new DateTimeOffset(file.LastWriteTime);
                    if (newest == null || written > newest.Value) newest = written;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                           ex is ArgumentException)
                {
                    // Un fichier illisible ne rend pas les autres inutilisables.
                }
            }

            return newest == null
                ? Measured.Missing<DateTimeOffset>(
                    "Aucun fichier du noyau n'a pu être daté : la justesse de l'horloge ne peut pas être " +
                    "démontrée sans référence extérieure.")
                : Measured.Ok(newest.Value, DataSource.FileSystem);
        }
    }
}
