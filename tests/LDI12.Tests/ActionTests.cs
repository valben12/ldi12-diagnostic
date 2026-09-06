using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Actions;
using LDI12.Actions.Elevation;
using LDI12.Actions.Journal;
using LDI12.Actions.Maintenance;
using LDI12.Actions.Repairs;
using LDI12.Actions.Tools;
using LDI12.Core.Execution;
using LDI12.Core.Logging;
using LDI12.Engine.Recommendations;
using Xunit;

namespace LDI12.Tests
{
    /// <summary>
    /// Ce que la couche d'actions doit garantir.
    /// </summary>
    /// <remarks>
    /// Les tests portent d'abord sur les promesses faites au client (rien n'est supprimé sans
    /// avoir été montré, rien n'est affirmé qui n'ait été constaté) et ensuite seulement sur le
    /// fonctionnement. Une erreur de collecte se corrige ; une suppression de trop, non.
    /// </remarks>
    public class ActionTests
    {
        // ---------- Catalogue ----------

        [Fact]
        public void Toutes_les_actions_du_catalogue_se_construisent()
        {
            var actions = ActionCatalog.CreateAll(NullLogger.Instance);

            Assert.NotEmpty(actions);
            foreach (var action in actions)
            {
                var descriptor = action.Descriptor;
                Assert.False(string.IsNullOrWhiteSpace(descriptor.Id), "Action sans identifiant.");
                Assert.False(string.IsNullOrWhiteSpace(descriptor.DisplayName), descriptor.Id + " : libellé manquant.");
                Assert.False(string.IsNullOrWhiteSpace(descriptor.Purpose), descriptor.Id + " : objet manquant.");
                Assert.True(descriptor.HardTimeout > TimeSpan.Zero, descriptor.Id + " : délai maximal non défini.");
            }
        }

        [Fact]
        public void Chaque_action_dit_au_client_ce_qu_elle_fait()
        {
            // Le double libellé des règles n'a de valeur que s'il existe aussi côté actions :
            // sans lui, le bilan client ne pourrait pas dire ce qui a été fait sur la machine
            // autrement qu'en recopiant une ligne de commande.
            foreach (var action in ActionCatalog.CreateAll(NullLogger.Instance))
            {
                Assert.False(string.IsNullOrWhiteSpace(action.Descriptor.PlainPurpose),
                    action.Descriptor.Id + " : aucune formulation destinée au client.");
                Assert.NotEqual(action.Descriptor.Purpose, action.Descriptor.PlainPurpose);
            }
        }

        [Fact]
        public void Les_identifiants_d_action_sont_uniques()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var action in ActionCatalog.CreateAll(NullLogger.Instance))
                Assert.True(seen.Add(action.Descriptor.Id), "Identifiant en double : " + action.Descriptor.Id);
        }

        [Fact]
        public void Chaque_recommandation_qui_annonce_une_action_en_designe_une_qui_existe()
        {
            // Une recommandation qui renvoie vers un bouton inexistant est pire qu'une
            // recommandation sans bouton : elle fait chercher au technicien quelque chose qui
            // n'a jamais existé.
            var orphans = new List<string>();

            foreach (var id in RecommendationCatalog.AllIds)
            {
                if (!RecommendationCatalog.TryGet(id, out var recommendation)) continue;
                if (recommendation.LinkedAction == null) continue;

                if (!ActionCatalog.IsKnown(recommendation.LinkedAction))
                    orphans.Add(recommendation.Id + " → " + recommendation.LinkedAction);
            }

            Assert.True(orphans.Count == 0,
                "Recommandations qui désignent une action inconnue : " + string.Join(", ", orphans));
        }

        // ---------- La prévisualisation est obligatoire ----------

        [Fact]
        public async Task Aucune_action_ne_s_execute_sans_previsualisation_concluante()
        {
            var runner = new ActionRunner(ActionFakes.Context(), new InterventionJournal(), NullLogger.Instance);
            var action = ActionCatalog.Find(ActionIds.FlushDns, NullLogger.Instance)!;

            var refused = new ActionPreview
            {
                Outcome = PreviewOutcome.Blocked,
                Summary = "Rien à faire.",
                Blocker = "Motif de blocage.",
            };

            var outcome = await runner.ExecuteAsync(action, refused, null, null, CancellationToken.None);

            Assert.Equal(ActionStatus.Failed, outcome.Status);
            Assert.Equal("Motif de blocage.", outcome.Summary);
        }

        [Fact]
        public async Task Une_action_indisponible_rend_une_previsualisation_bloquee_qui_dit_pourquoi()
        {
            // Windows 7 : DISM /RestoreHealth n'existe pas. L'écran doit dire pourquoi, et ce
            // qu'il faut faire à la place.
            var runner = new ActionRunner(
                ActionFakes.Context(new FakePlatformInfo(build: 7601, elevated: true)),
                new InterventionJournal(), NullLogger.Instance);

            var action = ActionCatalog.Find(ActionIds.Dism, NullLogger.Instance)!;
            var preview = await runner.PreviewAsync(action, null, CancellationToken.None);

            Assert.Equal(PreviewOutcome.Blocked, preview.Outcome);
            Assert.False(preview.CanExecute);
            Assert.False(string.IsNullOrWhiteSpace(preview.Blocker));
            Assert.Contains(preview.WillDo, line => line.IndexOf("KB947821", StringComparison.Ordinal) >= 0);
        }

        [Fact]
        public async Task Sans_privileges_ni_hote_eleve_une_action_privilegiee_ne_pretend_pas_pouvoir_s_executer()
        {
            var runner = new ActionRunner(
                ActionFakes.Context(new FakePlatformInfo(elevated: false)),
                new InterventionJournal(), NullLogger.Instance);

            var action = ActionCatalog.Find(ActionIds.Sfc, NullLogger.Instance)!;

            var readiness = runner.Readiness(action);
            Assert.Equal(ActionAvailability.Unavailable, readiness.Availability);
            Assert.False(string.IsNullOrWhiteSpace(readiness.Reason));

            var preview = await runner.PreviewAsync(action, null, CancellationToken.None);
            Assert.Equal(PreviewOutcome.Blocked, preview.Outcome);
        }

        [Fact]
        public async Task Chaque_previsualisation_dit_aussi_ce_qu_elle_ne_touchera_pas()
        {
            // C'est la question que pose le client, et la seule à laquelle le technicien doit
            // pouvoir répondre sans supposer.
            var runner = new ActionRunner(
                ActionFakes.Context(new FakePlatformInfo(elevated: true)),
                new InterventionJournal(), NullLogger.Instance);

            foreach (var action in ActionCatalog.CreateAll(NullLogger.Instance))
            {
                if (action.Descriptor.Id == ActionIds.RestartService) continue;
                if (action.Descriptor.Id == ActionIds.Cleanup) continue;

                var preview = await runner.PreviewAsync(action, null, CancellationToken.None);
                if (preview.Outcome != PreviewOutcome.Ready) continue;

                Assert.True(preview.WillNotDo.Count > 0,
                    action.Descriptor.Id + " : la prévisualisation ne dit pas ce qui n'est pas touché.");
            }
        }

        // ---------- Garde-fou du nettoyage ----------

        [Theory]
        [InlineData(@"C:\")]
        [InlineData(@"C:")]
        [InlineData("")]
        public void Le_nettoyage_refuse_une_racine_de_volume(string root)
        {
            Assert.False(CleanupGuard.IsAcceptableRoot(root, out var reason));
            Assert.False(string.IsNullOrWhiteSpace(reason));
        }

        [Fact]
        public void Le_nettoyage_refuse_un_dossier_qui_contient_des_donnees_personnelles()
        {
            var forbidden = new[] { @"C:\Users\Paul\Documents", @"C:\Windows", @"C:\Users\Paul" };

            Assert.False(CleanupGuard.IsAcceptableRoot(@"C:\Users\Paul", forbidden, out _));
            Assert.False(CleanupGuard.IsAcceptableRoot(@"C:\Users\Paul\Documents", forbidden, out _));

            // Un dossier situé sous un dossier interdit reste nettoyable : c'est le cas de
            // C:\Windows\Temp, qui ne contient rien de personnel.
            Assert.True(CleanupGuard.IsAcceptableRoot(@"C:\Windows\Temp", forbidden, out _));
        }

        [Fact]
        public void Le_perimetre_se_compare_par_segments_et_non_par_prefixe()
        {
            // « C:\Temp2 » commence par « C:\Temp » sans être dedans. Comparer des chaînes plutôt
            // que des chemins ferait sortir une suppression de son périmètre annoncé.
            Assert.True(CleanupGuard.IsInside(@"C:\Temp", @"C:\Temp\a.tmp"));
            Assert.False(CleanupGuard.IsInside(@"C:\Temp", @"C:\Temp2\a.tmp"));
            Assert.False(CleanupGuard.IsInside(@"C:\Temp", @"C:\Temp"));
        }

        [Fact]
        public void Toutes_les_racines_du_catalogue_de_nettoyage_passent_le_garde_fou()
        {
            foreach (var provider in CleanupCatalog.Create())
                foreach (var root in provider.Roots)
                {
                    // Les racines à joker sont vérifiées une fois développées ; on contrôle ici
                    // que le préfixe fixe est déjà hors des dossiers interdits.
                    var fixedPart = root.Split('*')[0].TrimEnd(Path.DirectorySeparatorChar);
                    Assert.True(CleanupGuard.IsAcceptableRoot(fixedPart, out var reason),
                        provider.Id + ", racine refusée : " + reason);
                }
        }

        // ---------- Sélection ----------

        [Fact]
        public void La_corbeille_n_est_jamais_selectionnee_d_office()
        {
            // Une case pré-cochée dans un écran qu'on valide vite est une suppression
            // automatique déguisée, ce que le cahier des charges interdit.
            var catalog = CleanupCatalog.Create();
            var recycleBin = catalog.Single(provider => provider.Kind == CleanupGroupKind.RecycleBin);

            Assert.True(recycleBin.ContainsUserData);
            Assert.False(recycleBin.SelectedByDefault);
            Assert.DoesNotContain(CleanupAction.Selected(catalog, null), provider => provider.ContainsUserData);
        }

        [Fact]
        public void Aucune_source_selectionnee_d_office_ne_contient_de_donnees_personnelles()
        {
            foreach (var provider in CleanupAction.Selected(CleanupCatalog.Create(), null))
                Assert.False(provider.ContainsUserData, provider.Id + " est coché d'office.");
        }

        // ---------- Ce qui est supprimé est exactement ce qui a été montré ----------

        [Fact]
        public void Le_nettoyage_ne_supprime_que_les_fichiers_du_releve()
        {
            var files = new FakeFileSystemGateway()
                .WithFile(@"C:\Temp", @"C:\Temp\a.tmp")
                .WithFile(@"C:\Temp", @"C:\Temp\b.tmp");

            // Un fichier présent sur le disque mais absent du relevé : apparu après coup, il n'a
            // été montré à personne et ne doit pas partir.
            files.WithFile(@"C:\Autre", @"C:\Temp\c.tmp");

            var plan = new CleanupPlan
            {
                Groups = new[]
                {
                    new CleanupGroup
                    {
                        ProviderId = "TEMP-USER",
                        Title = "Temporaires",
                        Roots = new[] { @"C:\Temp" },
                        Files = new[]
                        {
                            new FileEntry { Path = @"C:\Temp\a.tmp", SizeBytes = 1024, LastWriteUtc = new DateTime(2024, 1, 1) },
                            new FileEntry { Path = @"C:\Temp\b.tmp", SizeBytes = 1024, LastWriteUtc = new DateTime(2024, 1, 1) },
                        },
                        ItemCount = 2,
                        Bytes = 2048,
                    },
                },
            };

            var outcome = CleanupAction.Execute(files, plan, null, CancellationToken.None);

            Assert.Equal(ActionStatus.Succeeded, outcome.Status);
            Assert.Equal(new[] { @"C:\Temp\a.tmp", @"C:\Temp\b.tmp" }, files.Deleted.ToArray());
            Assert.True(files.StillExists(@"C:\Temp\c.tmp"), "Un fichier hors relevé a été supprimé.");
            Assert.Equal(2048, outcome.FreedBytes);
        }

        [Fact]
        public void Un_fichier_verrouille_est_annonce_et_non_compte_comme_supprime()
        {
            var files = new FakeFileSystemGateway()
                .WithFile(@"C:\Temp", @"C:\Temp\a.tmp")
                .WithFile(@"C:\Temp", @"C:\Temp\ouvert.tmp");
            files.LockedFiles.Add(@"C:\Temp\ouvert.tmp");

            var plan = Plan(@"C:\Temp", @"C:\Temp\a.tmp", @"C:\Temp\ouvert.tmp");
            var outcome = CleanupAction.Execute(files, plan, null, CancellationToken.None);

            Assert.Equal(ActionStatus.PartiallySucceeded, outcome.Status);
            Assert.Single(files.Deleted);
            Assert.Contains(outcome.Details, detail => detail.IndexOf("ouverts", StringComparison.Ordinal) >= 0);
        }

        [Fact]
        public void Un_plan_vide_ne_rend_pas_un_echec()
        {
            // « Rien à supprimer » est un résultat, pas une panne.
            var outcome = CleanupAction.Execute(
                new FakeFileSystemGateway(), new CleanupPlan(), null, CancellationToken.None);

            Assert.Equal(ActionStatus.NothingToDo, outcome.Status);
        }

        [Fact]
        public void La_previsualisation_annonce_explicitement_la_corbeille_quand_elle_est_comprise()
        {
            var plan = new CleanupPlan
            {
                Groups = new[]
                {
                    new CleanupGroup
                    {
                        ProviderId = "RECYCLE-BIN",
                        Title = "Corbeille",
                        Kind = CleanupGroupKind.RecycleBin,
                        ContainsUserData = true,
                        ItemCount = 12,
                        Bytes = 5_000_000,
                    },
                },
            };

            var preview = CleanupAction.Describe(plan);

            Assert.Equal(PreviewOutcome.Ready, preview.Outcome);
            Assert.Contains("récupérable", preview.Summary, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Une_source_illisible_est_dite_et_non_presentee_comme_vide()
        {
            // Un dossier qu'on n'a pas pu lire n'est pas un dossier vide. Les confondre ferait
            // annoncer « rien à nettoyer » sur une machine qui en aurait besoin.
            var plan = new CleanupPlan
            {
                Groups = new[]
                {
                    new CleanupGroup
                    {
                        ProviderId = "TEMP-SYSTEM",
                        Title = "Temporaires de Windows",
                        Unavailable = "Ce dossier n'est lisible qu'avec des privilèges administrateur.",
                    },
                },
            };

            var preview = CleanupAction.Describe(plan);

            Assert.Equal(PreviewOutcome.NothingToDo, preview.Outcome);
            Assert.Contains(preview.Measurements,
                line => line.Value.IndexOf("privilèges administrateur", StringComparison.Ordinal) >= 0);
        }

        // ---------- Interprétation des outils ----------

        [Fact]
        public void Sfc_sans_corruption_n_est_pas_presente_comme_une_reparation()
        {
            var outcome = SfcScanAction.Interpret(
                ActionFakes.Result(output: "Windows Resource Protection did not find any integrity violations."),
                TimeSpan.FromMinutes(6));

            Assert.Equal(ActionStatus.NothingToDo, outcome.Status);
            Assert.False(outcome.RestartRequired);
        }

        [Fact]
        public void Sfc_qui_a_repare_demande_un_redemarrage()
        {
            var outcome = SfcScanAction.Interpret(
                ActionFakes.Result(output: "Windows Resource Protection found corrupt files and successfully repaired them."),
                TimeSpan.FromMinutes(9));

            Assert.Equal(ActionStatus.Succeeded, outcome.Status);
            Assert.True(outcome.RestartRequired);
        }

        [Fact]
        public void Sfc_lu_dans_la_langue_de_Windows_est_compris_aussi()
        {
            var outcome = SfcScanAction.Interpret(
                ActionFakes.Result(output: "La protection des ressources Windows n'a trouvé aucune violation d'intégrité."),
                TimeSpan.FromMinutes(6));

            Assert.Equal(ActionStatus.NothingToDo, outcome.Status);
        }

        [Fact]
        public void Dism_3010_est_une_reussite_et_non_un_echec()
        {
            // DISM a ses propres conventions : traiter « non nul » comme un échec ferait passer
            // une réparation réussie pour un plantage.
            var outcome = DismRestoreHealthAction.Interpret(ActionFakes.Result(3010), TimeSpan.FromMinutes(12));

            Assert.Equal(ActionStatus.Succeeded, outcome.Status);
            Assert.True(outcome.RestartRequired);
        }

        [Fact]
        public void Dism_sans_source_explique_ce_qui_manque()
        {
            var outcome = DismRestoreHealthAction.Interpret(
                ActionFakes.Result(unchecked((int)0x800F081F)), TimeSpan.FromMinutes(4));

            Assert.Equal(ActionStatus.Failed, outcome.Status);
            Assert.Contains("source", outcome.Summary, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData(0, ActionStatus.NothingToDo)]
        [InlineData(1, ActionStatus.Succeeded)]
        [InlineData(3, ActionStatus.PartiallySucceeded)]
        public void Chkdsk_traduit_son_code_de_sortie(int exitCode, ActionStatus expected)
        {
            var outcome = CheckDiskAction.Interpret("C:", ActionFakes.Result(exitCode), TimeSpan.FromMinutes(3));
            Assert.Equal(expected, outcome.Status);
        }

        [Fact]
        public void Chkdsk_qui_trouve_des_erreurs_ne_pretend_pas_les_avoir_corrigees()
        {
            var outcome = CheckDiskAction.Interpret("C:", ActionFakes.Result(1), TimeSpan.FromMinutes(3));

            Assert.Contains("n'ont pas été corrigées", outcome.Summary, StringComparison.Ordinal);
        }

        [Fact]
        public void Un_outil_qui_n_a_pas_pu_etre_lance_n_est_jamais_annonce_comme_reussi()
        {
            foreach (var outcome in new[]
            {
                SfcScanAction.Interpret(ActionFakes.Result(launchFailed: true), TimeSpan.Zero),
                DismRestoreHealthAction.Interpret(ActionFakes.Result(launchFailed: true), TimeSpan.Zero),
                CheckDiskAction.Interpret("C:", ActionFakes.Result(launchFailed: true), TimeSpan.Zero),
            })
            {
                Assert.Equal(ActionStatus.Failed, outcome.Status);
                Assert.False(outcome.Succeeded);
            }
        }

        [Fact]
        public void Le_vidage_dns_distingue_un_refus_de_privileges_d_un_echec()
        {
            var refused = FlushDnsAction.Interpret(
                ActionFakes.Result(1, "The requested operation requires elevation."), TimeSpan.Zero);

            Assert.Equal(ActionStatus.ElevationRequired, refused.Status);
        }

        [Theory]
        [InlineData("        STATE              : 4  RUNNING", 4)]
        [InlineData("        ÉTAT               : 1  STOPPED", 1)]
        [InlineData("rien d'exploitable", null)]
        public void L_etat_d_un_service_se_lit_sur_son_code_numerique(string output, int? expected)
        {
            // La sortie de sc est traduite ; son code d'état, non. Un outil de dépannage qui ne
            // fonctionne que sur un Windows français n'est pas un outil de dépannage.
            Assert.Equal(expected, RestartServiceAction.ReadState(output));
        }

        // ---------- Consoles Windows ----------

        [Fact]
        public void Le_catalogue_d_outils_couvre_les_consoles_attendues()
        {
            Assert.True(WindowsToolCatalog.All.Count >= 15,
                "Le plan prévoit au moins quinze consoles ; il y en a " + WindowsToolCatalog.All.Count + ".");

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var tool in WindowsToolCatalog.All)
            {
                Assert.StartsWith(ActionIds.ToolPrefix, tool.Id, StringComparison.Ordinal);
                Assert.True(seen.Add(tool.Id), "Identifiant d'outil en double : " + tool.Id);
                Assert.False(string.IsNullOrWhiteSpace(tool.Purpose), tool.Id + " : sans description.");
            }
        }

        [Fact]
        public void Une_console_absente_de_l_edition_Famille_est_expliquee_et_non_seulement_grisee()
        {
            // Un outil grisé sans phrase laisse croire au client que sa machine a un problème
            // de plus. C'est une décision d'édition de Microsoft, et il faut le dire.
            var platform = new FakePlatformInfo(elevated: false, editionId: "Core");
            var files = new FakeFileSystemGateway();

            var states = WindowsToolCatalog.Inspect(platform, files);
            var policy = states.Single(state => state.Tool.Id == "TOOL-GROUP-POLICY");

            Assert.False(policy.Available);
            Assert.NotNull(policy.Reason);
            Assert.Contains("édition", policy.Reason!, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Une_console_livree_mais_bridee_par_l_edition_n_est_pas_proposee()
        {
            // lusrmgr.msc est bien présent dans System32 sur une édition Famille, où il refuse
            // ensuite de s'ouvrir. Se fier au fichier proposerait un bouton qui ne peut
            // qu'échouer, et laisserait croire au client à un défaut de sa machine.
            var files = new FakeFileSystemGateway();
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "lusrmgr.msc");
            files.Files.Add(path);

            var home = WindowsToolCatalog
                .Inspect(new FakePlatformInfo(editionId: "Core"), files)
                .Single(state => state.Tool.Id == "TOOL-LOCAL-USERS");

            Assert.False(home.Available);
            Assert.Contains("édition", home.Reason!, StringComparison.OrdinalIgnoreCase);

            var professional = WindowsToolCatalog
                .Inspect(new FakePlatformInfo(editionId: "Professional"), files)
                .Single(state => state.Tool.Id == "TOOL-LOCAL-USERS");

            Assert.True(professional.Available);
        }

        [Fact]
        public void Une_console_indisponible_porte_toujours_sa_raison()
        {
            var states = WindowsToolCatalog.Inspect(new FakePlatformInfo(), new FakeFileSystemGateway());

            foreach (var state in states)
                if (!state.Available)
                    Assert.False(string.IsNullOrWhiteSpace(state.Reason), state.Tool.Id + " : grisé sans raison.");
        }

        [Fact]
        public void Ouvrir_une_console_indisponible_ne_lance_rien()
        {
            var launcher = new FakeProcessLauncher();
            var runner = new ActionRunner(
                ActionFakes.Context(launcher: launcher), new InterventionJournal(), NullLogger.Instance);

            var state = new WindowsToolState
            {
                Tool = WindowsToolCatalog.Find("TOOL-SERVICES")!,
                Available = false,
                Reason = "Absente de cette installation.",
            };

            var result = runner.LaunchTool(state);

            Assert.False(result.Started);
            Assert.Empty(launcher.Launched);
        }

        // ---------- Journal d'intervention ----------

        [Fact]
        public void Le_journal_conserve_l_ordre_et_la_nature_des_operations()
        {
            var journal = new InterventionJournal(NullLogger.Instance, path: null);

            journal.Record(InterventionKind.Preview, ActionIds.Sfc, "Réparer", "Ready", "Ce qui va se passer.");
            journal.Record(InterventionKind.Execution, ActionIds.Sfc, "Réparer", "Succeeded", "Ce qui s'est passé.",
                elevated: true, duration: TimeSpan.FromMinutes(4));

            var entries = journal.Entries;
            Assert.Equal(2, entries.Count);
            Assert.Equal(InterventionKind.Preview, entries[0].Kind);
            Assert.Equal(InterventionKind.Execution, entries[1].Kind);

            var rendered = journal.Render();
            Assert.Contains("Prévisualisation", rendered, StringComparison.Ordinal);
            Assert.Contains("administrateur", rendered, StringComparison.Ordinal);
        }

        [Fact]
        public void Un_journal_vide_le_dit_plutot_que_de_rester_muet()
        {
            var journal = new InterventionJournal(NullLogger.Instance, path: null);

            Assert.True(journal.IsEmpty);
            Assert.Contains("n'a été que lue", journal.Render(), StringComparison.Ordinal);
        }

        [Fact]
        public async Task Toute_execution_laisse_une_trace_dans_le_journal()
        {
            var journal = new InterventionJournal(NullLogger.Instance, path: null);
            var runner = new ActionRunner(
                ActionFakes.Context(processes: new ScriptedProcessRunner(_ => ActionFakes.Result())),
                journal, NullLogger.Instance);

            var action = ActionCatalog.Find(ActionIds.FlushDns, NullLogger.Instance)!;
            var preview = await runner.PreviewAsync(action, null, CancellationToken.None);
            await runner.ExecuteAsync(action, preview, null, null, CancellationToken.None);

            Assert.Contains(journal.Entries, entry => entry.Kind == InterventionKind.Preview);
            Assert.Contains(journal.Entries, entry => entry.Kind == InterventionKind.Execution);
        }

        // ---------- Point de restauration ----------

        [Fact]
        public async Task Un_point_de_restauration_refuse_faute_de_privileges_le_dit_clairement()
        {
            var runner = new ActionRunner(
                ActionFakes.Context(new FakePlatformInfo(elevated: false)),
                new InterventionJournal(NullLogger.Instance, path: null), NullLogger.Instance);

            var result = await runner.CreateRestorePointAsync("Test", CancellationToken.None);

            Assert.Equal(RestorePointState.RequiresElevation, result.State);
            Assert.False(result.ProtectionInPlace);
        }

        [Fact]
        public void Un_point_recent_deja_present_n_est_pas_un_echec()
        {
            // Windows refuse un second point dans les 24 heures. Le présenter comme un échec
            // ferait croire au technicien qu'il intervient sans filet, alors qu'il en a un.
            var throttled = new RestorePointResult
            {
                State = RestorePointState.Throttled,
                Message = "Un point récent existe déjà.",
            };

            Assert.True(throttled.ProtectionInPlace);
        }

        // ---------- Protocole d'élévation ----------

        [Fact]
        public void Le_plan_de_nettoyage_ne_traverse_le_canal_qu_alleger()
        {
            // Le relevé complet d'un dossier temporaire dépasse couramment cinquante mille
            // entrées : seul son résumé traverse, et le jeton désigne le relevé authentique,
            // resté du côté qui l'a établi.
            var files = new List<FileEntry>();
            for (var i = 0; i < 1000; i++)
                files.Add(new FileEntry { Path = @"C:\Temp\f" + i + ".tmp", SizeBytes = 10 });

            var plan = new CleanupPlan
            {
                Groups = new[]
                {
                    new CleanupGroup
                    {
                        ProviderId = "TEMP-USER", Title = "Temporaires",
                        Files = files, ItemCount = files.Count, Bytes = 10_000,
                    },
                },
            };

            var payload = PreviewPayload.From(CleanupAction.Describe(plan), "jeton", maxFilesPerGroup: 400);

            Assert.Equal(400, payload.Cleanup!.Groups[0].Files.Count);
            Assert.Equal(1000, payload.Cleanup.Groups[0].ItemCount);
            Assert.True(payload.Cleanup.Groups[0].Truncated);
        }

        [Fact]
        public void Une_previsualisation_venue_de_l_hote_eleve_impose_l_execution_a_distance()
        {
            var payload = PreviewPayload.From(
                new ActionPreview { Outcome = PreviewOutcome.Ready, Summary = "Prêt." }, "jeton", 400);

            var round = ElevationProtocol.Read<PreviewPayload>(ElevationProtocol.Write(payload))!.ToPreview();

            Assert.Equal("jeton", round.RemoteToken);
            Assert.True(round.CanExecute);
            Assert.Equal("Prêt.", round.Summary);
        }

        [Fact]
        public void Un_compte_rendu_traverse_le_canal_sans_rien_perdre()
        {
            var outcome = new ActionOutcome
            {
                Status = ActionStatus.PartiallySucceeded,
                Summary = "Partiellement terminé.",
                Details = new[] { "Une précision." },
                Duration = TimeSpan.FromSeconds(42),
                RestartRequired = true,
                FreedBytes = 4096,
            };

            var round = ElevationProtocol
                .Read<OutcomePayload>(ElevationProtocol.Write(OutcomePayload.From(outcome)))!
                .ToOutcome();

            Assert.Equal(outcome.Status, round.Status);
            Assert.Equal(outcome.Summary, round.Summary);
            Assert.Equal(outcome.RestartRequired, round.RestartRequired);
            Assert.Equal(outcome.FreedBytes, round.FreedBytes);
            Assert.Equal(42, Math.Round(round.Duration.TotalSeconds));
        }

        [Fact]
        public void Une_requete_illisible_ne_devient_pas_une_requete_vide()
        {
            // Un JSON invalide doit rendre null, et non un ordre par défaut que l'hôte élevé
            // exécuterait.
            Assert.Null(ElevationProtocol.Read<ElevatedRequest>("{ ceci n'est pas du JSON"));
        }

        private static CleanupPlan Plan(string root, params string[] paths)
        {
            var files = new List<FileEntry>();
            foreach (var path in paths)
                files.Add(new FileEntry { Path = path, SizeBytes = 1024, LastWriteUtc = new DateTime(2024, 1, 1) });

            return new CleanupPlan
            {
                Groups = new[]
                {
                    new CleanupGroup
                    {
                        ProviderId = "TEST", Title = "Test", Roots = new[] { root },
                        Files = files, ItemCount = files.Count, Bytes = files.Count * 1024,
                    },
                },
            };
        }
    }
}
