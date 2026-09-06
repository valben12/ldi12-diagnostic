using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Collectors.Internal;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;
using LDI12.Core.Model;
using LDI12.Core.Platform;
using LDI12.Core.Probes;

namespace LDI12.Collectors.Security
{
    /// <summary>
    /// Produits de sécurité déclarés à Windows, et état détaillé de Microsoft Defender.
    /// </summary>
    /// <remarks>
    /// Le logiciel n'est pas un antivirus et ne le devient pas ici : aucune recherche de menace,
    /// aucun fichier de l'utilisateur ouvert, aucun verdict sur la propreté de la machine. On
    /// relève ce que Windows expose de la configuration des protections (active ou non, à jour
    /// ou non), ce qui relève de l'audit et non de la détection.
    /// <para>
    /// Deux sources, parce qu'elles ne disent pas la même chose. Le Centre de sécurité liste tous
    /// les produits installés, y compris tiers, mais ne dit pas depuis quand leurs signatures
    /// datent. L'espace de noms Defender le dit, et sur une machine restée éteinte plusieurs
    /// mois, cas le plus fréquent en dépannage, la protection est « active » et pourtant aveugle
    /// aux menaces récentes. C'est exactement ce que le client ne peut pas voir seul.
    /// </para>
    /// </remarks>
    public sealed class SecurityProductsProbe : IDiagnosticProbe
    {
        public ProbeDescriptor Descriptor { get; } = new ProbeDescriptor
        {
            Id = ProbeIds.SecurityProducts,
            DisplayName = "Protections installées",
            Category = DiagnosticCategory.Security,
            EstimatedDuration = TimeSpan.FromSeconds(2),
            HardTimeout = TimeSpan.FromSeconds(30),
            Isolation = IsolationMode.SeparateProcess,
            Requirements = new ProbeRequirements
            {
                RequiredWmiNamespaces = new[] { WmiNamespaces.SecurityCenter2 },
            },
        };

        public async Task<ProbeOutcome> ExecuteAsync(ProbeContext context, CancellationToken cancellationToken)
        {
            var products = new List<SecurityProductInfo>();

            products.AddRange(await ReadAsync(
                context, "AntiVirusProduct", SecurityProductKind.Antivirus, cancellationToken).ConfigureAwait(false));
            products.AddRange(await ReadAsync(
                context, "AntiSpywareProduct", SecurityProductKind.Antispyware, cancellationToken).ConfigureAwait(false));
            products.AddRange(await ReadAsync(
                context, "FirewallProduct", SecurityProductKind.Firewall, cancellationToken).ConfigureAwait(false));

            var defender = await ReadDefenderAsync(context, cancellationToken).ConfigureAwait(false);

            context.Draft.SetSecurityProducts(products, defender);

            return Conclude(products, defender);
        }

        private static ProbeOutcome Conclude(IReadOnlyList<SecurityProductInfo> products, DefenderStatus defender)
        {
            var antivirus = 0;
            var active = 0;
            foreach (var product in products)
            {
                if (product.Kind != SecurityProductKind.Antivirus) continue;
                antivirus++;
                if (product.State.Or(ProtectionState.Unknown) == ProtectionState.Enabled) active++;
            }

            if (antivirus == 0)
                return ProbeOutcome.Partial(
                    "Aucun antivirus déclaré au Centre de sécurité Windows, à confirmer manuellement.");

            if (active == 0)
                return ProbeOutcome.Ok("Un antivirus est installé mais aucun n'est actif.");

            var age = defender.SignatureAgeDays;
            if (age.IsReliable && age.Value > 7)
                return ProbeOutcome.Ok(
                    "Protection active, mais les signatures datent de " +
                    age.Value.ToString(CultureInfo.CurrentCulture) + " jours.");

            return ProbeOutcome.Ok(
                active == 1 ? "Un antivirus actif." : active + " antivirus actifs simultanément.");
        }

        private static async Task<IReadOnlyList<SecurityProductInfo>> ReadAsync(
            ProbeContext context, string className, SecurityProductKind kind, CancellationToken cancellationToken)
        {
            var result = await context.Wmi.QueryAsync(
                    WmiNamespaces.SecurityCenter2, "SELECT * FROM " + className,
                    TimeSpan.FromSeconds(8), cancellationToken)
                .ConfigureAwait(false);

            if (!result.Succeeded) return Array.Empty<SecurityProductInfo>();

            var products = new List<SecurityProductInfo>(result.Records.Count);
            foreach (var record in result.Records)
            {
                var name = Measure.Clean(record.GetString("displayName"));
                if (name == null) continue;

                var state = record.GetInt32("productState");
                var decoded = Decode(state);

                products.Add(new SecurityProductInfo
                {
                    Name = name,
                    Kind = kind,
                    State = decoded.State,
                    UpToDate = decoded.UpToDate,
                    IsBuiltIn = name.IndexOf("Defender", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                name.IndexOf("Pare-feu Windows", StringComparison.OrdinalIgnoreCase) >= 0,
                });
            }

            return products;
        }

        /// <summary>
        /// Décode le champ <c>productState</c> du Centre de sécurité.
        /// </summary>
        /// <remarks>
        /// Ce champ n'est pas documenté par Microsoft. Son encodage est connu et stable depuis
        /// Windows 7 (six chiffres hexadécimaux dont le deuxième octet porte l'activation et le
        /// troisième la fraîcheur des signatures) mais « connu » n'est pas « garanti ». Un motif
        /// que l'on ne sait pas lire rend donc une mesure <b>absente</b> avec sa raison, et non
        /// un « désactivé » qui ferait conclure à une machine sans protection.
        /// </remarks>
        internal static (Measured<ProtectionState> State, Measured<bool> UpToDate) Decode(int? productState)
        {
            if (productState == null)
                return (Measured.Missing<ProtectionState>(
                            "Ce produit ne déclare pas son état au Centre de sécurité.", DataSource.Wmi),
                        Measured.Missing<bool>(
                            "Ce produit ne déclare pas la fraîcheur de ses signatures.", DataSource.Wmi));

            var hex = productState.Value.ToString("X6", CultureInfo.InvariantCulture);
            var enabledByte = hex.Substring(2, 2);
            var signatureByte = hex.Substring(4, 2);

            var state = enabledByte switch
            {
                "00" => Measured.Ok(ProtectionState.Disabled, DataSource.Wmi),
                "01" => Measured.Ok(ProtectionState.Expired, DataSource.Wmi),
                "10" => Measured.Ok(ProtectionState.Enabled, DataSource.Wmi),
                "11" => Measured.Ok(ProtectionState.Enabled, DataSource.Wmi),
                _ => Measured.Missing<ProtectionState>(
                    "État rapporté sous une forme inconnue (0x" + hex + ") : il n'est pas interprété.",
                    DataSource.Wmi),
            };

            var upToDate = signatureByte switch
            {
                "00" => Measured.Ok(true, DataSource.Wmi),
                "10" => Measured.Ok(false, DataSource.Wmi),
                _ => Measured.Missing<bool>(
                    "Fraîcheur des signatures rapportée sous une forme inconnue (0x" + hex + ").", DataSource.Wmi),
            };

            return (state, upToDate);
        }

        private static async Task<DefenderStatus> ReadDefenderAsync(
            ProbeContext context, CancellationToken cancellationToken)
        {
            var feature = context.Platform.Features.Get(FeatureId.DefenderWmi);
            if (feature.Availability != Availability.Available)
            {
                var reason = feature.Reason ?? "L'espace de noms Defender n'est pas disponible sur cette machine.";
                return new DefenderStatus
                {
                    AntivirusEnabled = Measured.Missing<bool>(reason, DataSource.Wmi),
                    RealTimeProtectionEnabled = Measured.Missing<bool>(reason, DataSource.Wmi),
                    SignatureDate = Measured.Missing<DateTimeOffset>(reason, DataSource.Wmi),
                    SignatureAgeDays = Measured.Missing<int>(reason, DataSource.Wmi),
                    LastScan = Measured.Missing<DateTimeOffset>(reason, DataSource.Wmi),
                    TamperProtectionEnabled = Measured.Missing<bool>(reason, DataSource.Wmi),
                };
            }

            var result = await context.Wmi.QueryAsync(
                    WmiNamespaces.Defender, "SELECT * FROM MSFT_MpComputerStatus",
                    TimeSpan.FromSeconds(10), cancellationToken)
                .ConfigureAwait(false);

            if (!result.Succeeded || result.Records.Count == 0)
            {
                var reason = result.Reason ?? "Microsoft Defender n'a pas rendu son état.";
                return new DefenderStatus
                {
                    AntivirusEnabled = Measured.Missing<bool>(reason, DataSource.Wmi),
                    RealTimeProtectionEnabled = Measured.Missing<bool>(reason, DataSource.Wmi),
                    SignatureDate = Measured.Missing<DateTimeOffset>(reason, DataSource.Wmi),
                    SignatureAgeDays = Measured.Missing<int>(reason, DataSource.Wmi),
                    LastScan = Measured.Missing<DateTimeOffset>(reason, DataSource.Wmi),
                    TamperProtectionEnabled = Measured.Missing<bool>(reason, DataSource.Wmi),
                };
            }

            var record = result.Records[0];
            return new DefenderStatus
            {
                AntivirusEnabled = Measure.Bool(record, "AntivirusEnabled", "L'activation de l'antivirus"),
                RealTimeProtectionEnabled = Measure.Bool(
                    record, "RealTimeProtectionEnabled", "La protection en temps réel"),
                SignatureDate = Measure.DmtfDate(
                    record, "AntivirusSignatureLastUpdated", "La date des signatures"),
                SignatureAgeDays = Measure.Int32(
                    record, "AntivirusSignatureAge", "L'ancienneté des signatures"),
                LastScan = Measure.DmtfDate(record, "QuickScanEndTime", "La date du dernier examen"),
                TamperProtectionEnabled = Measure.Bool(
                    record, "IsTamperProtected", "La protection contre les modifications"),
            };
        }
    }
}
