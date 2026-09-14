using System;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Actions;
using LDI12.Actions.Journal;
using LDI12.Core.Diagnostics;
using LDI12.Core.Logging;
using Xunit;

namespace LDI12.Tests
{
    /// <summary>
    /// Sur quel fil tourne une action.
    /// </summary>
    /// <remarks>
    /// <b>Signalé depuis un poste client</b> : la sauvegarde détectait bien les données, puis
    /// le bouton de copie se grisait et rien ne se passait. Deux défauts se superposaient. Le
    /// premier, les chemins <c>\\?\</c> refusés par l'application, empêchait la copie de
    /// commencer. Le second n'est apparu qu'une fois le premier corrigé : la copie tournait sur
    /// le fil de l'interface, qui gelait jusqu'à la fin, soit une heure pour 87 Go.
    /// </remarks>
    public class ActionThreadTests
    {
        [Fact]
        public async Task Une_action_synchrone_rend_la_main_a_l_appelant_pendant_qu_elle_travaille()
        {
            using var feu = new ManualResetEventSlim(false);
            var action = new ActionDEssai((_, __) =>
            {
                feu.Wait(TimeSpan.FromSeconds(10));
                return ActionOutcome.Simple(ActionStatus.Succeeded, "Fait.");
            });
            var runner = new ActionRunner(ActionFakes.Context(), new InterventionJournal(), NullLogger.Instance);

            var preview = await runner.PreviewAsync(action, null, CancellationToken.None);
            var tache = runner.ExecuteAsync(action, preview, null, null, CancellationToken.None);

            // Avec l'ancien exécuteur, l'appel ne rendait la main qu'une fois le travail fini :
            // la tâche revenait déjà terminée, dix secondes plus tard.
            Assert.False(tache.IsCompleted, "L'action a tourné sur le fil de l'appelant.");

            feu.Set();
            var outcome = await tache;
            Assert.Equal(ActionStatus.Succeeded, outcome.Status);
        }

        [Fact]
        public async Task Une_action_tourne_sur_un_fil_a_elle_en_appartement_sta()
        {
            var appelant = Thread.CurrentThread.ManagedThreadId;
            int fil = 0;
            ApartmentState appartement = ApartmentState.Unknown;

            var action = new ActionDEssai((_, __) =>
            {
                fil = Thread.CurrentThread.ManagedThreadId;
                appartement = Thread.CurrentThread.GetApartmentState();
                return ActionOutcome.Simple(ActionStatus.Succeeded, "Fait.");
            });
            var runner = new ActionRunner(ActionFakes.Context(), new InterventionJournal(), NullLogger.Instance);

            var preview = await runner.PreviewAsync(action, null, CancellationToken.None);
            await runner.ExecuteAsync(action, preview, null, null, CancellationToken.None);

            Assert.NotEqual(appelant, fil);
            Assert.Equal(ApartmentState.STA, appartement);
        }

        [Fact]
        public async Task Une_erreur_dans_l_action_revient_en_compte_rendu_et_non_en_plantage()
        {
            var action = new ActionDEssai((_, __) => throw new InvalidOperationException("Disque débranché."));
            var runner = new ActionRunner(ActionFakes.Context(), new InterventionJournal(), NullLogger.Instance);

            var preview = await runner.PreviewAsync(action, null, CancellationToken.None);
            var outcome = await runner.ExecuteAsync(action, preview, null, null, CancellationToken.None);

            Assert.Equal(ActionStatus.Failed, outcome.Status);
            Assert.Contains("Disque débranché.", outcome.Summary, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Une_action_interrompue_le_dit()
        {
            using var annulation = new CancellationTokenSource();
            var action = new ActionDEssai((_, jeton) =>
            {
                annulation.Cancel();
                jeton.ThrowIfCancellationRequested();
                return ActionOutcome.Simple(ActionStatus.Succeeded, "Jamais atteint.");
            });
            var runner = new ActionRunner(ActionFakes.Context(), new InterventionJournal(), NullLogger.Instance);

            var preview = await runner.PreviewAsync(action, null, CancellationToken.None);
            var outcome = await runner.ExecuteAsync(action, preview, null, null, annulation.Token);

            Assert.Equal(ActionStatus.Cancelled, outcome.Status);
        }

        /// <summary>Une action dont le travail est une simple fonction, synchrone comme les vraies.</summary>
        private sealed class ActionDEssai : IRepairAction
        {
            private readonly Func<ActionContext, CancellationToken, ActionOutcome> _travail;

            public ActionDEssai(Func<ActionContext, CancellationToken, ActionOutcome> travail) => _travail = travail;

            public ActionDescriptor Descriptor { get; } = new ActionDescriptor
            {
                Id = "ESSAI-FIL",
                DisplayName = "Action d'essai",
                Kind = ActionKind.Maintenance,
                Category = DiagnosticCategory.Storage,
                Risk = ActionRisk.Low,
                Purpose = "Éprouve le fil sur lequel tourne une action.",
                PlainPurpose = "Rien.",
                TypicalDuration = TimeSpan.FromSeconds(1),
                HardTimeout = TimeSpan.FromSeconds(30),
            };

            public ActionReadiness CheckReadiness(ActionContext context) => ActionReadiness.Ready;

            public Task<ActionPreview> PreviewAsync(ActionContext context, CancellationToken cancellationToken)
                => Task.FromResult(new ActionPreview { Outcome = PreviewOutcome.Ready, Summary = "Prêt." });

            public Task<ActionOutcome> ExecuteAsync(
                ActionContext context, ActionPreview preview, IProgress<ActionProgress>? progress,
                CancellationToken cancellationToken)
                => Task.FromResult(_travail(context, cancellationToken));
        }
    }
}
