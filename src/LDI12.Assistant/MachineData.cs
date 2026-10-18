using System;
using System.Collections;
using System.Collections.Generic;
using LDI12.Core.Model;
using LDI12.Reports.Json;
using Newtonsoft.Json;

namespace LDI12.Assistant
{
    /// <summary>
    /// Ce que Claude peut lire de la machine : les relevés du diagnostic, section par section.
    /// </summary>
    /// <remarks>
    /// <b>Lecture seule, et rien que le diagnostic.</b> Claude ne lance aucune commande, ne lit
    /// aucun fichier du client : il consulte le relevé que LDI12 a déjà fait, dans le même format
    /// que l'export JSON. Ce qui part chez Anthropic est ce que ce relevé contient, ni plus ni
    /// moins : matériel, pilotes, journaux d'événements, services. Aucun document, aucun nom de
    /// fichier personnel.
    /// <para>
    /// Section par section plutôt que d'un bloc : le relevé complet pèse plusieurs centaines de
    /// milliers de caractères, et Claude n'en lit que ce qui sert au problème posé.
    /// </para>
    /// </remarks>
    public static class MachineData
    {
        /// <summary>Au-delà, la section est tronquée et Claude est invité à filtrer.</summary>
        public const int MaxCharacters = 20_000;

        public static readonly IReadOnlyDictionary<string, string> Sections = new Dictionary<string, string>
        {
            ["machine"] = "Fabricant, modèle, version de Windows, droits, fonctions présentes",
            ["hardware"] = "Processeur, mémoire, cartes graphiques et leurs pilotes, carte mère, BIOS, batteries, températures",
            ["storage"] = "Disques physiques (SMART, usure), volumes, place système, maintenance",
            ["devices"] = "Périphériques du Gestionnaire de périphériques, avec code d'erreur et identifiant matériel",
            ["drivers"] = "Pilotes installés : périphérique, classe, éditeur, version, date, INF, signature, générique Microsoft",
            ["events"] = "Journaux : erreurs critiques, erreurs récurrentes, erreurs disque, écrans bleus, arrêts brutaux",
            ["services"] = "Services Windows : état, démarrage",
            ["startup"] = "Programmes au démarrage et tâches planifiées",
            ["windows"] = "Installation de Windows, mises à jour, intégrité des fichiers système, profils, heure",
            ["network"] = "Cartes réseau, tests de connectivité, DNS, proxy, Wi-Fi",
            ["security"] = "Antivirus, Defender, pare-feu, UAC, comptes, accès à distance",
            ["performance"] = "Réactivité, démarrage, processus les plus gourmands, durée de fonctionnement",
            ["findings"] = "Constats du diagnostic LDI12, corrélations et recommandations, avec leurs règles",
        };

        /// <summary>La section demandée, en JSON compact, filtrée sur <paramref name="query"/> pour les listes.</summary>
        public static string Read(SystemSnapshot snapshot, string section, string? query)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            object? content = (section ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "machine" => new { snapshot.Machine, snapshot.Platform },
                "hardware" => snapshot.Hardware,
                "storage" => snapshot.Storage,
                "devices" => Lines(Filter(snapshot.Windows.Devices, query), d => DeviceLine((DeviceInfo)d)),
                "drivers" => Lines(Filter(snapshot.Windows.Drivers, query), d => DriverLine((DriverInfo)d)),
                "events" => snapshot.Windows.Events,
                "services" => Lines(Filter(snapshot.Windows.Services, query), s => ServiceLine((ServiceInfo)s)),
                "startup" => Lines(Filter(snapshot.Windows.Startup, query), s => StartupLine((StartupItem)s)),
                "windows" => new
                {
                    snapshot.Windows.Install,
                    snapshot.Windows.Updates,
                    snapshot.Windows.SystemFiles,
                    snapshot.Windows.Profiles,
                    snapshot.Windows.Time,
                },
                "network" => snapshot.Network,
                "security" => snapshot.Security,
                "performance" => snapshot.Performance,
                "findings" => new { snapshot.Findings, snapshot.Correlations, snapshot.Recommendations, snapshot.Score },
                _ => null,
            };

            if (content == null)
                return "Section inconnue. Sections disponibles : " + string.Join(", ", Sections.Keys) + ".";

            var json = content as string ?? JsonConvert.SerializeObject(content, SnapshotSerializer.CreateSettings(indented: false));
            if (json.Length <= MaxCharacters) return json;
            return json.Substring(0, MaxCharacters)
                   + " … [tronqué : " + json.Length + " caractères au total. Relancez avec un filtre « query » pour cibler.]";
        }

        /// <summary>Une liste en lignes de texte : trois à cinq fois moins de jetons que le même contenu en JSON.</summary>
        private static string Lines(object items, Func<object, string> line)
        {
            var text = new System.Text.StringBuilder();
            var count = 0;
            foreach (var item in (IEnumerable)items)
            {
                if (item == null) continue;
                text.AppendLine(line(item));
                count++;
            }
            return count == 0 ? "aucun élément" : count + " élément(s)\n" + text;
        }

        internal static string DeviceLine(DeviceInfo device)
            => device.Name + " | classe " + (device.DeviceClass ?? "?") + " | " + (device.Manufacturer ?? "?")
               + " | code " + device.ProblemCode + (device.ProblemLabel != null ? " (" + device.ProblemLabel + ")" : string.Empty)
               + (device.IsDisabled ? " | désactivé" : string.Empty) + " | id " + (device.HardwareId ?? "?");

        internal static string DriverLine(DriverInfo driver)
            => driver.DeviceName + " | " + (driver.DeviceClass ?? "?") + " | " + (driver.Manufacturer ?? "?")
               + " | v" + (driver.Version ?? "?")
               + (driver.Date.HasValue ? " | " + driver.Date.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) : string.Empty)
               + (driver.InfName != null ? " | " + driver.InfName : string.Empty)
               + (driver.IsMicrosoftGeneric ? " | GÉNÉRIQUE MICROSOFT" : string.Empty)
               + (driver.IsSigned == false ? " | NON SIGNÉ" : string.Empty);

        internal static string ServiceLine(ServiceInfo service)
            => service.Name + " (" + service.DisplayName + ") | " + service.State + " | " + service.StartMode
               + (service.Deviation != null ? " | ANOMALIE : " + service.Deviation : string.Empty);

        internal static string StartupLine(StartupItem item)
            => item.Name + " | " + item.Location + (item.Publisher != null ? " | " + item.Publisher : string.Empty)
               + (item.TargetMissing ? " | CIBLE ABSENTE" : string.Empty) + " | " + MachineBrief.Short(item.Command, 90);

        /// <summary>Les éléments d'une liste dont la sérialisation contient le texte cherché, sans tenir compte de la casse.</summary>
        private static object Filter(IEnumerable items, string? query)
        {
            if (string.IsNullOrWhiteSpace(query)) return items;
            var settings = SnapshotSerializer.CreateSettings(indented: false);
            var kept = new List<object>();
            foreach (var item in items)
            {
                if (item == null) continue;
                if (JsonConvert.SerializeObject(item, settings).IndexOf(query!.Trim(), StringComparison.OrdinalIgnoreCase) >= 0)
                    kept.Add(item);
            }
            return kept;
        }
    }
}
