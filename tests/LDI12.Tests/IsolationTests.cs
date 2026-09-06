using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Diagnostics;
using LDI12.Core.Logging;
using LDI12.Core.Model;
using LDI12.Core.Probes;
using LDI12.Engine.Orchestration;
using Xunit;

namespace LDI12.Tests
{
    /// <summary>
    /// L'exécution d'une sonde dans un processus séparé, et ce qu'il ne faut pas y perdre.
    /// </summary>
    /// <remarks>
    /// Le risque de cette mécanique n'est pas qu'elle échoue bruyamment : c'est qu'elle perde
    /// une section en silence. Une sonde s'exécuterait, rendrait un compte rendu vert, et son
    /// résultat n'arriverait jamais dans le relevé. Ces tests parcourent donc le catalogue des
    /// sections plutôt que d'en vérifier quelques-unes à la main.
    /// </remarks>
    public class IsolationTests
    {
        // ============================================================ sections du relevé

        [Fact]
        public void Chaque_ecriture_du_releve_est_declaree_au_catalogue_des_sections()
        {
            // Une section écrite mais absente du catalogue traverserait la frontière de processus
            // sans type pour la reconstruire : elle serait rejetée à l'arrivée, sans erreur.
            var draft = new SnapshotDraft();
            InvokeEverySetter(draft);

            var unknown = new List<string>();
            foreach (var write in draft.Writes)
                if (DraftSections.TypeOf(write.Name) == null) unknown.Add(write.Name);

            Assert.True(unknown.Count == 0,
                "Sections écrites et absentes du catalogue : " + string.Join(", ", unknown));
        }

        [Fact]
        public void Chaque_methode_de_depot_laisse_une_trace()
        {
            // Un « SetX » qui oublierait de s'enregistrer ferait disparaître le résultat d'une
            // sonde isolée : elle écrirait chez elle, et le parent ne recevrait rien.
            var silent = new List<string>();

            foreach (var setter in Setters())
            {
                var draft = new SnapshotDraft();
                setter.Invoke(draft, DefaultArguments(setter));

                if (draft.Writes.Count == 0) silent.Add(setter.Name);
            }

            Assert.True(silent.Count == 0,
                "Méthodes de dépôt qui n'enregistrent rien : " + string.Join(", ", silent));
        }

        [Fact]
        public void Chaque_section_du_catalogue_sait_se_rejouer()
        {
            // Le pendant du test précédent : une section qu'on sait écrire mais pas rejouer
            // arriverait chez le parent pour y être ignorée.
            var draft = new SnapshotDraft();
            var refused = new List<string>();

            foreach (var name in DraftSections.Names)
            {
                var type = DraftSections.TypeOf(name);
                if (type == null) { refused.Add(name + " (sans type)"); continue; }

                if (!draft.Apply(name, Activator.CreateInstance(type)!)) refused.Add(name);
            }

            Assert.True(refused.Count == 0,
                "Sections que le relevé ne sait pas rejouer : " + string.Join(", ", refused));
        }

        [Fact]
        public void Un_rejeu_conserve_la_raison_d_une_mesure_absente()
        {
            // La promesse du logiciel s'arrête là où une mesure absente devient un zéro : elle
            // doit traverser la frontière de processus avec la raison de son absence.
            var origin = new SnapshotDraft();
            origin.SetHardwareErrors(new HardwareErrorsInfo
            {
                WindowDays = Measured.Missing<int>("Le journal Système n'a pas pu être lu."),
            });

            var replayed = new SnapshotDraft();
            foreach (var write in origin.Writes) Assert.True(replayed.Apply(write.Name, write.Value));

            var errors = replayed.BuildHardware().Errors;
            Assert.Equal(Availability.Unavailable, errors.WindowDays.Availability);
            Assert.Equal("Le journal Système n'a pas pu être lu.", errors.WindowDays.Reason);
        }

        [Fact]
        public void Un_rejeu_compte_comme_une_ecriture_pour_la_sonde_suivante()
        {
            // La sonde SMART s'exécute elle aussi dans le processus isolé et y cherche la liste
            // des disques produite juste avant. Un rejeu muet la lui cacherait.
            var draft = new SnapshotDraft();
            draft.Apply(DraftSections.PhysicalDisks, new List<PhysicalDiskInfo> { new PhysicalDiskInfo() });

            var write = Assert.Single(draft.Writes);
            Assert.Equal(DraftSections.PhysicalDisks, write.Name);
        }

        [Fact]
        public void Une_section_inconnue_est_refusee_sans_rien_casser()
        {
            var draft = new SnapshotDraft();

            Assert.False(draft.Apply("section-qui-n-existe-pas", new CpuInfo()));
            Assert.False(draft.Apply(DraftSections.Cpu, "ceci n'est pas un processeur"));
        }

        // ============================================================ politique d'exécution

        [Fact]
        public async Task Une_sonde_isolee_passe_par_l_hote_et_rapporte_sa_duree()
        {
            var host = new FakeIsolationHost(new IsolatedOutcome
            {
                Status = ProbeStatus.Ok,
                Message = "fait ailleurs",
                DurationMs = 42,
            });

            var probe = new IsolatedFakeProbe();
            var report = await Execute(probe, host);

            Assert.Equal(ProbeStatus.Ok, report.Status);
            Assert.Equal("fait ailleurs", report.Message);
            Assert.False(probe.RanHere);

            // La durée est celle du travail, pas celle du tour d'attente : les sondes isolées se
            // suivent, et rapporter l'attente ferait passer un module rapide pour un module lent.
            Assert.Equal(42, report.DurationMs);
        }

        [Fact]
        public async Task Un_hote_indisponible_fait_executer_la_sonde_sur_place()
        {
            // Une isolation impossible (hôte absent, lancement refusé) ne doit priver personne
            // d'un module qui fonctionne très bien dans le processus courant.
            var probe = new IsolatedFakeProbe();
            var report = await Execute(probe, new FakeIsolationHost(null));

            Assert.Equal(ProbeStatus.Ok, report.Status);
            Assert.True(probe.RanHere);
        }

        [Fact]
        public async Task Une_sonde_ordinaire_ne_passe_jamais_par_l_hote()
        {
            var host = new FakeIsolationHost(new IsolatedOutcome { Status = ProbeStatus.Ok });
            var probe = new IsolatedFakeProbe { Isolated = false };

            await Execute(probe, host);

            Assert.Equal(0, host.Calls);
            Assert.True(probe.RanHere);
        }

        [Fact]
        public async Task Ce_que_l_hote_rend_est_rejoue_dans_le_releve()
        {
            var host = new FakeIsolationHost(new IsolatedOutcome
            {
                Status = ProbeStatus.Ok,
                Writes = new[]
                {
                    new DraftSection(DraftSections.Events, new EventsInfo
                    {
                        WindowDays = Measured.Ok(14, DataSource.EventLog),
                    }),
                },
            });

            var draft = new SnapshotDraft();
            await Execute(new IsolatedFakeProbe(), host, draft);

            Assert.Equal(14, draft.BuildWindows().Events.WindowDays.Value);
        }

        [Fact]
        public async Task Le_releve_deja_collecte_accompagne_la_demande()
        {
            // Sans cela, la lecture SMART ne trouverait pas la liste des disques et l'isolation
            // transformerait une dépendance déclarée en donnée manquante.
            var draft = new SnapshotDraft();
            draft.SetPhysicalDisks(new[] { new PhysicalDiskInfo { Index = 3 } });

            var host = new FakeIsolationHost(new IsolatedOutcome { Status = ProbeStatus.Ok });
            await Execute(new IsolatedFakeProbe(), host, draft);

            var sent = Assert.Single(host.LastInput!);
            Assert.Equal(DraftSections.PhysicalDisks, sent.Name);
        }

        [Fact]
        public async Task Une_sonde_qui_se_bloque_sans_isolation_est_quand_meme_abandonnee()
        {
            // Une sonde qui se bloque sur un appel synchrone ne rend pas la main avant sa
            // première attente : appelée directement, elle emportait le délai maximal avec elle.
            // Constaté sur la machine de développement, le diagnostic ne se terminait plus.
            var report = await Execute(new BlockingFakeProbe(), new FakeIsolationHost(null));

            Assert.Equal(ProbeStatus.TimedOut, report.Status);
        }

        // ============================================================ outillage

        private static async Task<ModuleReport> Execute(
            IDiagnosticProbe probe, IProbeIsolationHost host, SnapshotDraft? draft = null)
        {
            var context = new ProbeContext(
                new FakePlatformInfo(), new FakeProcessRunner(), new FakeWmiGateway(),
                new FakeRegistryGateway(), NullLogger.Instance, null!, null!, null!, null!, null!, null!, null!,
                draft ?? new SnapshotDraft(), RunMode.Full);

            return await new ProbeExecutionPolicy(NullLogger.Instance, host)
                .ExecuteAsync(probe, context, CancellationToken.None);
        }

        private static IEnumerable<MethodInfo> Setters()
            => typeof(SnapshotDraft)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(method => method.Name.StartsWith("Set", StringComparison.Ordinal));

        private static void InvokeEverySetter(SnapshotDraft draft)
        {
            foreach (var setter in Setters()) setter.Invoke(draft, DefaultArguments(setter));
        }

        /// <summary>
        /// Une valeur neutre pour chaque paramètre : listes vides, objets par défaut, mesures
        /// non collectées. Le contenu n'importe pas, seul importe qu'une écriture ait lieu.
        /// </summary>
        private static object[] DefaultArguments(MethodInfo setter)
        {
            var parameters = setter.GetParameters();
            var arguments = new object[parameters.Length];

            for (var index = 0; index < parameters.Length; index++)
            {
                var type = parameters[index].ParameterType;

                arguments[index] = type.IsInterface && type.IsGenericType
                    ? Array.CreateInstance(type.GetGenericArguments()[0], 0)
                    : Activator.CreateInstance(type)!;
            }

            return arguments;
        }

        private sealed class FakeIsolationHost : IProbeIsolationHost
        {
            private readonly IsolatedOutcome? _outcome;

            public FakeIsolationHost(IsolatedOutcome? outcome) => _outcome = outcome;

            public int Calls { get; private set; }

            public IReadOnlyList<DraftSection>? LastInput { get; private set; }

            public Task<IsolatedOutcome?> RunAsync(
                ProbeDescriptor descriptor, RunMode mode, IReadOnlyList<DraftSection> input,
                CancellationToken cancellationToken)
            {
                Calls++;
                LastInput = input;
                return Task.FromResult(_outcome);
            }
        }

        /// <summary>Sonde qui bloque son fil, comme le ferait une requête système sans retour.</summary>
        private sealed class BlockingFakeProbe : IDiagnosticProbe
        {
            public ProbeDescriptor Descriptor => new ProbeDescriptor
            {
                Id = "TEST-BLOCK",
                DisplayName = "Module bloquant",
                Category = DiagnosticCategory.Windows,
                EstimatedDuration = TimeSpan.FromMilliseconds(1),
                HardTimeout = TimeSpan.FromMilliseconds(150),
            };

            public Task<ProbeOutcome> ExecuteAsync(ProbeContext context, CancellationToken cancellationToken)
            {
                // Bornée pour ne pas laisser un fil derrière elle : le délai de la sonde est
                // trente fois plus court, c'est lui qui décide de l'issue du test.
                Thread.Sleep(TimeSpan.FromSeconds(5));
                return Task.FromResult(ProbeOutcome.Ok("jamais atteint"));
            }
        }

        private sealed class IsolatedFakeProbe : IDiagnosticProbe
        {
            public bool Isolated { get; init; } = true;

            public bool RanHere { get; private set; }

            public ProbeDescriptor Descriptor => new ProbeDescriptor
            {
                Id = "TEST-ISO",
                DisplayName = "Module de test",
                Category = DiagnosticCategory.Windows,
                Isolation = Isolated ? IsolationMode.SeparateProcess : IsolationMode.InProcess,
                EstimatedDuration = TimeSpan.FromMilliseconds(1),
                HardTimeout = TimeSpan.FromSeconds(5),
            };

            public Task<ProbeOutcome> ExecuteAsync(ProbeContext context, CancellationToken cancellationToken)
            {
                RanHere = true;
                return Task.FromResult(ProbeOutcome.Ok("fait ici"));
            }
        }
    }
}
