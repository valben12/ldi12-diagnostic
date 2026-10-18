using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Actions.Repairs;
using LDI12.Core.Crashes;
using Xunit;

namespace LDI12.Tests
{
    /// <summary>
    /// L'écran Plantages : un minividage fabriqué octet par octet au format de Windows, le pilote
    /// désigné, et la conclusion tirée de plusieurs écrans bleus.
    /// </summary>
    public class CrashTests
    {
        private const ulong NtBase = 0xFFFFF80600000000;
        private const ulong NvBase = 0xFFFFF80612340000;
        private const ulong NetBase = 0xFFFFF80620000000;

        // ============================================================ lecture du vidage

        [Fact]
        public void Le_minividage_livre_code_parametres_et_pilotes()
        {
            var dump = CrashDumpReader.Read("060124-1.dmp", MiniDump(0xD1, rip: NtBase + 0x100, stack: new[] { NtBase + 0x50, NvBase + 0x1234 }),
                new DateTime(2024, 6, 1), 300_000);

            Assert.True(dump.IsReadable);
            Assert.True(dump.Is64Bit);
            Assert.Equal("mini", dump.Kind);
            Assert.Equal(0xD1u, dump.BugCheckCode);
            Assert.Equal(NvBase + 0x10, dump.Parameters[3]);
            Assert.Equal(new[] { "ntoskrnl.exe", "nvlddmkm.sys", "tcpip.sys" }, dump.Modules.Select(m => m.Name));
            Assert.Equal("ntoskrnl.exe", dump.CrashModule);
            Assert.Equal(new[] { "ntoskrnl.exe", "nvlddmkm.sys" }, dump.StackModules);
            Assert.Equal(new DateTime(2024, 5, 30, 8, 0, 0, DateTimeKind.Utc), dump.TimeUtc);
        }

        [Fact]
        public void Un_fichier_qui_n_est_pas_un_vidage_est_dit_illisible_sans_planter()
        {
            Assert.False(CrashDumpReader.Read("x.dmp", new byte[200], DateTime.UtcNow, 200).IsReadable);
            var program = new byte[0x2000];
            Encoding.ASCII.GetBytes("MDMP").CopyTo(program, 0);
            Assert.Contains("programme", CrashDumpReader.Read("x.dmp", program, DateTime.UtcNow, 0x2000).Error);
        }

        [Fact]
        public void Une_liste_de_pilotes_incoherente_est_ecartee()
        {
            var data = MiniDump(0x0A, rip: NtBase + 1, stack: Array.Empty<ulong>());
            BitConverter.GetBytes(0x7FFFFFF0u).CopyTo(data, DriverList); // nom hors du fichier

            var dump = CrashDumpReader.Read("x.dmp", data, DateTime.UtcNow, data.Length);

            Assert.True(dump.IsReadable);
            Assert.Empty(dump.Modules);
            Assert.Equal(0x0Au, dump.BugCheckCode);
        }

        [Fact]
        public void Un_vidage_de_la_memoire_ne_livre_que_son_en_tete()
        {
            var data = MiniDump(0x124, rip: 0, stack: Array.Empty<ulong>());
            BitConverter.GetBytes(2).CopyTo(data, 0xF98); // vidage du noyau

            var dump = CrashDumpReader.Read("MEMORY.DMP", data.Take(CrashDumpReader.HeaderBytes).ToArray(), DateTime.UtcNow, 0);

            Assert.Equal("noyau", dump.Kind);
            Assert.Equal(0x124u, dump.BugCheckCode);
            Assert.Empty(dump.Modules);
        }

        // ============================================================ pilote mis en cause

        [Fact]
        public void L_adresse_donnee_par_l_ecran_bleu_designe_le_pilote()
        {
            var dump = Read(0xD1, rip: NtBase + 0x100, stack: new[] { NetBase + 8 });
            CrashAnalyzer.AssignSuspect(dump, new Files());

            Assert.Equal("nvlddmkm.sys", dump.Suspect); // paramètre 4 de 0xD1, avant la pile
            Assert.Contains("adresse", dump.SuspectReason);
        }

        [Fact]
        public void Dans_la_pile_un_pilote_tiers_passe_avant_un_pilote_de_Windows()
        {
            var dump = Read(0x1A, rip: NtBase + 0x100, stack: new[] { NtBase + 8, NetBase + 8, NvBase + 8 });
            CrashAnalyzer.AssignSuspect(dump, new Files());

            Assert.Equal("nvlddmkm.sys", dump.Suspect);
        }

        [Fact]
        public void Le_noyau_n_est_retenu_qu_en_dernier_recours()
        {
            var dump = Read(0x1A, rip: NtBase + 0x100, stack: new[] { NtBase + 8 });
            CrashAnalyzer.AssignSuspect(dump, new Files());

            Assert.Equal("ntoskrnl.exe", dump.Suspect);
            Assert.Contains("noyau", dump.SuspectReason);
        }

        // ============================================================ conclusion

        [Fact]
        public void Un_meme_pilote_dans_la_plupart_des_vidages_est_designe_avec_son_editeur()
        {
            var history = new CrashHistory
            {
                Dumps = new[]
                {
                    Read(0x116, rip: NvBase + 4, stack: new[] { NvBase + 8 }, daysAgo: 1),
                    Read(0x3B, rip: NvBase + 4, stack: new[] { NvBase + 8 }, daysAgo: 5),
                    Read(0x1A, rip: NtBase + 4, stack: new[] { NtBase + 8 }, daysAgo: 9),
                },
            };

            var verdict = CrashAnalyzer.Conclude(history, new Files());

            Assert.Equal("Bad", verdict.Tone);
            Assert.Equal("nvlddmkm.sys", verdict.Suspect);
            Assert.Contains("NVIDIA", verdict.Title);
            Assert.Contains("2 sur 3", verdict.Detail);
            Assert.Contains(verdict.Advice, a => a.Contains("NVIDIA Corporation"));
            Assert.Equal("DRIVERS-WINDOWS-UPDATE", verdict.ActionId);
        }

        [Fact]
        public void Des_codes_qui_changent_sans_pilote_qui_revient_accusent_la_memoire()
        {
            var history = new CrashHistory
            {
                BugChecks = new[]
                {
                    new BugCheckEvent { TimeUtc = DateTime.UtcNow.AddDays(-1), BugCheckCode = 0x1A },
                    new BugCheckEvent { TimeUtc = DateTime.UtcNow.AddDays(-4), BugCheckCode = 0x50 },
                    new BugCheckEvent { TimeUtc = DateTime.UtcNow.AddDays(-8), BugCheckCode = 0x3B },
                },
            };

            var verdict = CrashAnalyzer.Conclude(history, new Files());

            Assert.Equal(CrashCause.Memory, verdict.Cause);
            Assert.Equal(CrashAnalyzer.MemoryTest, verdict.ActionId);
            Assert.Equal(3, CrashAnalyzer.CountBlueScreens(history));
        }

        [Fact]
        public void Un_ecran_bleu_note_au_journal_et_lu_dans_son_vidage_ne_compte_qu_une_fois()
        {
            var dump = Read(0x124, rip: NtBase + 4, stack: Array.Empty<ulong>(), daysAgo: 2);
            var history = new CrashHistory
            {
                Dumps = new[] { dump },
                BugChecks = new[] { new BugCheckEvent { TimeUtc = dump.TimeUtc.AddMinutes(2), BugCheckCode = 0x124 } },
            };

            Assert.Equal(1, CrashAnalyzer.CountBlueScreens(history));
            Assert.Equal(CrashCause.Hardware, CrashAnalyzer.Conclude(history, new Files()).Cause);
        }

        [Fact]
        public void Sans_ecran_bleu_les_arrets_brutaux_orientent_vers_l_alimentation()
        {
            var verdict = CrashAnalyzer.Conclude(new CrashHistory { UnexpectedShutdowns = 3 }, new Files());

            Assert.Equal("Warning", verdict.Tone);
            Assert.Contains("3 arrêts brutaux", verdict.Title);
        }

        [Fact]
        public void Une_machine_sans_plantage_qui_n_enregistre_rien_le_dit()
        {
            var verdict = CrashAnalyzer.Conclude(new CrashHistory { Settings = new DumpSettings { Mode = 0 } }, new Files());

            Assert.Equal("Warning", verdict.Tone);
            Assert.Contains(verdict.Advice, a => a.Contains("minividages"));
        }

        // ============================================================ transfert et opération

        [Fact]
        public void Les_adresses_du_noyau_traversent_le_canal_sans_perte()
        {
            var dump = Read(0xD1, rip: NtBase + 0x100, stack: new[] { NvBase + 8 });

            var back = Assert.Single(CrashDumpTransfer.Read(CrashDumpTransfer.Write(new[] { dump })));

            Assert.Equal(dump.Parameters, back.Parameters);
            Assert.Equal(dump.CrashAddress, back.CrashAddress);
            Assert.Equal(dump.Modules.Select(m => m.Base), back.Modules.Select(m => m.Base));
            Assert.Equal(dump.StackModules, back.StackModules);
            Assert.Equal(dump.TimeUtc, back.TimeUtc);
        }

        [Fact]
        public async Task L_operation_lit_le_dossier_et_rend_l_analyse()
        {
            var files = new FakeFileSystemGateway().WithFile(@"C:\Windows\Minidump", @"C:\Windows\Minidump\060124-1.dmp", 300_000);
            files.Bytes[@"C:\Windows\Minidump\060124-1.dmp"] = MiniDump(0x116, rip: NvBase + 4, stack: new[] { NvBase + 8 });
            var context = ActionFakes.Context(files: files, parameters: new Dictionary<string, string>
            {
                [CrashDumpsReadAction.FolderParameter] = @"C:\Windows\Minidump",
                [CrashDumpsReadAction.MemoryParameter] = @"C:\Windows\MEMORY.DMP",
            });
            var action = new CrashDumpsReadAction();

            var preview = await action.PreviewAsync(context, CancellationToken.None);
            var outcome = await action.ExecuteAsync(context, preview, null, CancellationToken.None);

            var dump = Assert.Single(CrashDumpTransfer.Read(outcome.RawOutput));
            Assert.Equal(0x116u, dump.BugCheckCode);
            Assert.Equal("nvlddmkm.sys", dump.CrashModule);
        }

        // ============================================================ fabrication d'un minividage

        private const int Triage = 0x2000;
        private const int Context = 0x2100;
        private const int DriverList = 0x2400;
        private const int StringPool = 0x2800;
        private const int CallStack = 0x3000;

        private static CrashDump Read(uint code, ulong rip, ulong[] stack, int daysAgo = 0)
        {
            var dump = CrashDumpReader.Read("x.dmp", MiniDump(code, rip, stack), DateTime.UtcNow, 1);
            return daysAgo == 0 ? dump : new CrashDump
            {
                File = dump.File, TimeUtc = DateTime.UtcNow.AddDays(-daysAgo), BugCheckCode = dump.BugCheckCode, Parameters = dump.Parameters,
                Is64Bit = true, CrashAddress = dump.CrashAddress, CrashModule = dump.CrashModule, Modules = dump.Modules, StackModules = dump.StackModules,
            };
        }

        /// <summary>Un minividage 64 bits : en-tête, structure de triage, trois pilotes, une pile.</summary>
        private static byte[] MiniDump(uint code, ulong rip, ulong[] stack)
        {
            var data = new byte[0x3400];
            Encoding.ASCII.GetBytes("PAGEDU64").CopyTo(data, 0);
            BitConverter.GetBytes(code).CopyTo(data, 0x38);
            ulong[] parameters = { 0x10, 2, 0, NvBase + 0x10 };
            for (var index = 0; index < 4; index++) BitConverter.GetBytes(parameters[index]).CopyTo(data, 0x40 + index * 8);
            BitConverter.GetBytes(4).CopyTo(data, 0xF98);
            BitConverter.GetBytes(new DateTime(2024, 5, 30, 8, 0, 0, DateTimeKind.Utc).ToFileTimeUtc()).CopyTo(data, 0xFA8);

            BitConverter.GetBytes(Context).CopyTo(data, Triage + 0x0C);
            BitConverter.GetBytes(CallStack).CopyTo(data, Triage + 0x28);
            BitConverter.GetBytes(stack.Length * 8).CopyTo(data, Triage + 0x2C);
            BitConverter.GetBytes(DriverList).CopyTo(data, Triage + 0x30);
            BitConverter.GetBytes(3).CopyTo(data, Triage + 0x34);
            BitConverter.GetBytes(StringPool).CopyTo(data, Triage + 0x38);
            BitConverter.GetBytes(rip).CopyTo(data, Context + 0xF8);

            var names = new[] { ("ntoskrnl.exe", NtBase, 0x1000000u), ("nvlddmkm.sys", NvBase, 0x2000000u), ("tcpip.sys", NetBase, 0x300000u) };
            var pool = StringPool;
            for (var index = 0; index < names.Length; index++)
            {
                var entry = DriverList + index * 0x90;
                BitConverter.GetBytes(pool).CopyTo(data, entry);
                BitConverter.GetBytes(names[index].Item2).CopyTo(data, entry + 8 + 0x30);
                BitConverter.GetBytes(names[index].Item3).CopyTo(data, entry + 8 + 0x40);
                BitConverter.GetBytes(1_600_000_000u).CopyTo(data, entry + 8 + 0x80);

                BitConverter.GetBytes(names[index].Item1.Length).CopyTo(data, pool);
                Encoding.Unicode.GetBytes(names[index].Item1).CopyTo(data, pool + 4);
                pool += 4 + names[index].Item1.Length * 2 + 2;
            }

            for (var index = 0; index < stack.Length; index++) BitConverter.GetBytes(stack[index]).CopyTo(data, CallStack + index * 8);
            return data;
        }

        private sealed class Files : IModuleFileSource
        {
            public ModuleFile? Find(string moduleName) => moduleName switch
            {
                "nvlddmkm.sys" => new ModuleFile { Company = "NVIDIA Corporation", Description = "NVIDIA Windows Kernel Mode Driver", Version = "31.0.15.3623", FileDateUtc = new DateTime(2023, 6, 1) },
                "tcpip.sys" => new ModuleFile { Company = "Microsoft Corporation", Description = "TCP/IP Driver" },
                "ntoskrnl.exe" => new ModuleFile { Company = "Microsoft Corporation", Description = "NT Kernel & System" },
                _ => null,
            };
        }
    }
}
