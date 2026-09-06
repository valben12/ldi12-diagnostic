using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Actions;
using LDI12.Actions.Elevation;
using LDI12.Actions.Journal;
using LDI12.Actions.Repairs;
using LDI12.Core.Execution;
using LDI12.Core.Logging;
using LDI12.Platform.Elevation;
using Xunit;

namespace LDI12.Tests
{
    /// <summary>
    /// Le canal élevé : ce qui passe entre l'application, restée en session utilisateur, et
    /// l'hôte administrateur.
    /// </summary>
    /// <remarks>
    /// <b>Ce chemin n'avait aucun test, et c'est ce qui l'a laissé cassé.</b> La couche
    /// plateforme décidait si une ligne était une progression en cherchant <c>"type"</c> dans son
    /// texte ; le sérialiseur écrivait <c>"Type"</c>, la recherche était sensible à la casse, et
    /// la première progression revenait donc à l'application comme si elle était la réponse
    /// finale. L'écran annonçait « Échec, l'hôte élevé n'a rendu aucun compte rendu exploitable »
    /// quatre secondes après le clic, pendant que <c>sfc.exe</c>, lui, réparait tranquillement
    /// les fichiers système en tâche de fond, sans que rien ne puisse plus l'interrompre.
    /// <para>
    /// Les tests portent donc sur les deux moitiés du défaut : le transport, qui doit continuer
    /// de lire tant que l'appelant reconnaît une notification, et l'appelant, qui doit
    /// reconnaître exactement ce que l'hôte écrit.
    /// </para>
    /// </remarks>
    public class ElevationTests
    {
        // ============================================================ le transport

        [Fact]
        public async Task Une_progression_ne_termine_pas_l_echange()
        {
            var lignes = Flux(
                Progression("Analyse en cours…", 0.10),
                Progression("Analyse en cours…", 0.60),
                Reponse("Aucune anomalie détectée."));

            var vues = new List<string>();
            var reponse = await ElevatedChannel.ReadReplyAsync(
                lignes, ligne => Notification(ligne, vues), CancellationToken.None);

            Assert.Equal(2, vues.Count);
            Assert.Contains("outcome", reponse, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Sans_lecteur_de_notification_la_premiere_ligne_fait_reponse()
        {
            // Le comportement attendu pour un échange qui n'en attend pas : la prévisualisation
            // et le point de restauration n'annoncent rien en chemin.
            var reponse = await ElevatedChannel.ReadReplyAsync(
                Flux(Reponse("Fait.")), null, CancellationToken.None);

            Assert.Contains("Fait.", reponse, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Une_ligne_vide_est_ignoree()
        {
            var reponse = await ElevatedChannel.ReadReplyAsync(
                new StringReader("\n\n" + Reponse("Fait.") + "\n"), null, CancellationToken.None);

            Assert.Contains("Fait.", reponse, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Un_hote_qui_disparait_le_dit()
        {
            // Le canal se referme sans réponse : c'est un incident, pas une réponse vide.
            await Assert.ThrowsAsync<EndOfStreamException>(() => ElevatedChannel.ReadReplyAsync(
                Flux(Progression("À mi-chemin…", 0.5)), ligne => true, CancellationToken.None));
        }

        // ============================================================ ce que l'hôte écrit

        [Fact]
        public void La_progression_ecrite_par_l_hote_porte_bien_ce_type()
        {
            // Test de non-régression sur la forme exacte du message, casse comprise : c'est
            // elle qui avait trompé la recherche de texte.
            var ligne = ElevationProtocol.Write(new ElevatedMessage
            {
                Type = ElevatedMessage.TypeProgress,
                Text = "Analyse en cours…",
                Fraction = 0.42,
            });

            var relu = ElevationProtocol.Read<ElevatedMessage>(ligne);

            Assert.NotNull(relu);
            Assert.Equal(ElevatedMessage.TypeProgress, relu!.Type);
            Assert.Equal("Analyse en cours…", relu.Text);
        }

        // ============================================================ de bout en bout

        [Fact]
        public async Task Une_action_privilegiee_rend_son_compte_rendu_apres_ses_progressions()
        {
            var canal = new CanalDEssai();
            canal.Repond(ElevationProtocol.Write(new ElevatedMessage
            {
                Type = ElevatedMessage.TypePreview,
                Preview = PreviewPayload.From(
                    new ActionPreview
                    {
                        Outcome = PreviewOutcome.Ready,
                        Summary = "La correspondance entre adresses IP et cartes va être oubliée.",
                    },
                    "jeton-1", 200),
            }));
            canal.Repond(
                Progression("Vidage en cours…", 0.5),
                ElevationProtocol.Write(new ElevatedMessage
                {
                    Type = ElevatedMessage.TypeOutcome,
                    Outcome = OutcomePayload.From(ActionOutcome.Simple(
                        ActionStatus.Succeeded, "Le cache des adresses physiques a été vidé.")),
                }));

            var runner = new ActionRunner(
                ActionFakes.Context(new FakePlatformInfo(elevated: false)),
                new InterventionJournal(), NullLogger.Instance, canal);
            var action = new FlushArpCacheAction();

            var preview = await runner.PreviewAsync(action, null, CancellationToken.None);
            Assert.Equal("jeton-1", preview.RemoteToken);

            var etapes = new List<string>();
            var outcome = await runner.ExecuteAsync(
                action, preview, null, new Rapporteur(etapes), CancellationToken.None);

            Assert.Equal(ActionStatus.Succeeded, outcome.Status);
            Assert.Equal("Le cache des adresses physiques a été vidé.", outcome.Summary);
            Assert.Equal(new[] { "Vidage en cours…" }, etapes);
        }

        [Fact]
        public async Task Un_hote_qui_refuse_le_dit_avec_ses_mots()
        {
            // Un jeton périmé, une action qui ne correspond pas : l'hôte rend une erreur, et
            // c'est sa phrase qui doit remonter, pas une phrase de repli.
            var canal = new CanalDEssai();
            canal.Repond(ElevationProtocol.Write(new ElevatedMessage
            {
                Type = ElevatedMessage.TypePreview,
                Preview = PreviewPayload.From(
                    new ActionPreview { Outcome = PreviewOutcome.Ready, Summary = "Prêt." }, "jeton-2", 200),
            }));
            canal.Repond(ElevationProtocol.Write(new ElevatedMessage
            {
                Type = ElevatedMessage.TypeError,
                Message = "Aucun relevé ne correspond : rien n'a été exécuté.",
            }));

            var runner = new ActionRunner(
                ActionFakes.Context(new FakePlatformInfo(elevated: false)),
                new InterventionJournal(), NullLogger.Instance, canal);
            var action = new FlushArpCacheAction();

            var preview = await runner.PreviewAsync(action, null, CancellationToken.None);
            var outcome = await runner.ExecuteAsync(action, preview, null, null, CancellationToken.None);

            Assert.Equal(ActionStatus.Failed, outcome.Status);
            Assert.Contains("Aucun relevé ne correspond", outcome.Summary, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Une_elevation_refusee_ne_fait_rien_et_le_dit()
        {
            // Le troisième cas que le plan déclarait non éprouvé : quelqu'un répond « Non » à
            // l'invite de Windows. Rien ne doit partir, et l'écran doit le dire sans code
            // d'erreur ni jargon.
            var canal = new CanalDEssai { Etat = ElevatedChannelState.Refused };
            var runner = new ActionRunner(
                ActionFakes.Context(new FakePlatformInfo(elevated: false)),
                new InterventionJournal(), NullLogger.Instance, canal);
            var action = new FlushArpCacheAction();

            var preview = await runner.PreviewAsync(action, null, CancellationToken.None);

            Assert.Equal(PreviewOutcome.Blocked, preview.Outcome);
            Assert.False(preview.CanExecute);
            Assert.Contains("refusée", preview.Summary, StringComparison.Ordinal);
            Assert.Empty(canal.Envoyes);

            // Et le contrat tient jusqu'au bout : une prévisualisation bloquée n'exécute rien.
            var outcome = await runner.ExecuteAsync(action, preview, null, null, CancellationToken.None);

            Assert.Equal(ActionStatus.Failed, outcome.Status);
            Assert.Empty(canal.Envoyes);
        }

        // ============================================================ mise en place

        private static StringReader Flux(params string[] lignes)
            => new StringReader(string.Join("\n", lignes) + "\n");

        private static string Progression(string texte, double fraction)
            => ElevationProtocol.Write(new ElevatedMessage
            {
                Type = ElevatedMessage.TypeProgress,
                Text = texte,
                Fraction = fraction,
            });

        private static string Reponse(string resume)
            => ElevationProtocol.Write(new ElevatedMessage
            {
                Type = ElevatedMessage.TypeOutcome,
                Outcome = OutcomePayload.From(ActionOutcome.Simple(ActionStatus.Succeeded, resume)),
            });

        /// <summary>Le prédicat que l'appelant fournit au transport, tel qu'il l'écrit vraiment.</summary>
        private static bool Notification(string ligne, List<string> vues)
        {
            var message = ElevationProtocol.Read<ElevatedMessage>(ligne);
            if (message == null || message.Type != ElevatedMessage.TypeProgress) return false;

            vues.Add(message.Text ?? string.Empty);
            return true;
        }

        /// <summary>
        /// Un canal qui joue une suite de lignes préparées, en tenant le contrat du vrai : les
        /// notifications sont remises à l'appelant, la première ligne qu'il ne réclame pas est
        /// la réponse.
        /// </summary>
        private sealed class CanalDEssai : IElevatedChannel
        {
            private readonly Queue<string[]> _echanges = new Queue<string[]>();

            public ElevatedChannelState State { get; private set; } = ElevatedChannelState.NotRequested;

            /// <summary>Ce que l'ouverture du canal rendra : disponible, ou refusée par l'invite.</summary>
            public ElevatedChannelState Etat { get; set; } = ElevatedChannelState.Available;

            public string? Reason => null;

            /// <summary>Les requêtes reçues : ce qui est parti pour de bon.</summary>
            public List<string> Envoyes { get; } = new List<string>();

            public void Repond(params string[] lignes) => _echanges.Enqueue(lignes);

            public Task<ElevatedChannelState> EnsureAsync(CancellationToken cancellationToken)
            {
                State = Etat;
                return Task.FromResult(State);
            }

            public Task<string> SendAsync(
                string request, Func<string, bool>? onNotification, TimeSpan timeout,
                CancellationToken cancellationToken)
            {
                Envoyes.Add(request);
                if (_echanges.Count == 0) throw new InvalidOperationException("Échange non prévu.");

                foreach (var ligne in _echanges.Dequeue())
                {
                    if (onNotification != null && onNotification(ligne)) continue;
                    return Task.FromResult(ligne);
                }

                throw new IOException("L'hôte élevé a fermé le canal.");
            }
        }

        /// <summary>Rapporte sur le fil appelant : un <c>Progress&lt;T&gt;</c> passerait par le pool.</summary>
        private sealed class Rapporteur : IProgress<ActionProgress>
        {
            private readonly List<string> _etapes;

            public Rapporteur(List<string> etapes) => _etapes = etapes;

            public void Report(ActionProgress value) => _etapes.Add(value.Text);
        }
    }
}
