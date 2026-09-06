using System;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Diagnostics;
using LDI12.Core.Probes;

namespace LDI12.Collectors.Security
{
    /// <summary>
    /// Comptes locaux de la machine : lesquels sont actifs, lesquels sont administrateurs,
    /// lesquels s'ouvrent sans mot de passe.
    /// </summary>
    /// <remarks>
    /// Uniquement des noms et des indicateurs de configuration. Aucun mot de passe n'est lu, ni
    /// testé, ni deviné : le logiciel ne cherche pas à entrer dans les comptes, il constate leur
    /// réglage. Un compte administrateur sans mot de passe est un constat de configuration, pas
    /// le résultat d'une tentative.
    /// <para>
    /// Sur une machine jointe à un domaine, seuls les comptes locaux sont énumérés : les comptes
    /// du domaine ne sont pas gérés ici, et le rapport le dit plutôt que de laisser croire à un
    /// inventaire complet.
    /// </para>
    /// </remarks>
    public sealed class LocalAccountsProbe : IDiagnosticProbe
    {
        public ProbeDescriptor Descriptor { get; } = new ProbeDescriptor
        {
            Id = ProbeIds.LocalAccounts,
            DisplayName = "Comptes locaux",
            Category = DiagnosticCategory.Security,
            EstimatedDuration = TimeSpan.FromMilliseconds(120),
            HardTimeout = TimeSpan.FromSeconds(20),
        };

        public Task<ProbeOutcome> ExecuteAsync(ProbeContext context, CancellationToken cancellationToken)
        {
            var accounts = context.Native.ReadLocalAccounts();

            if (!accounts.HasValue)
            {
                context.Draft.SetLocalAccounts(Array.Empty<Core.Model.LocalAccountInfo>());
                return Task.FromResult(ProbeOutcome.Partial(
                    accounts.Reason ?? "Les comptes locaux n'ont pas pu être énumérés."));
            }

            context.Draft.SetLocalAccounts(accounts.Value);

            var enabled = 0;
            var administrators = 0;
            var passwordless = 0;

            foreach (var account in accounts.Value)
            {
                if (!account.Enabled.Or(false)) continue;
                enabled++;
                if (account.IsAdministrator.Or(false)) administrators++;
                if (account.NoPasswordRequired.Or(false)) passwordless++;
            }

            if (passwordless > 0)
                return Task.FromResult(ProbeOutcome.Ok(
                    passwordless + " compte(s) actif(s) peuvent être ouverts sans mot de passe."));

            return Task.FromResult(ProbeOutcome.Ok(
                enabled + " compte(s) local(aux) actif(s), dont " + administrators + " administrateur(s)."));
        }
    }
}
