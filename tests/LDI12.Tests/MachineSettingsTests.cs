using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Actions;
using LDI12.Actions.Backup;
using LDI12.Core.Execution;
using Microsoft.Win32;
using Xunit;

namespace LDI12.Tests
{
    /// <summary>Imprimantes, lecteurs réseau et clé de Windows : relevés, notés, remis.</summary>
    public class MachineSettingsTests
    {
        private const string Backup = @"E:\LDI12-Sauvegarde-PC-2026-09-30-1400";

        [Theory]
        [InlineData("VK7JG-NPHTM-C97JM-9MPGT-3V66T")]
        [InlineData("W269N-WFGWX-YVC9B-4J6C9-T83GX")]
        [InlineData("NPPR9-FWDCX-D2C8J-H872K-2YT43")]
        public void La_cle_de_Windows_se_lit_dans_DigitalProductId(string key)
            => Assert.Equal(key, MachineSettings.Decode(Encode(key)));

        [Fact]
        public void Une_licence_numerique_sans_cle_n_est_pas_notee()
            => Assert.Null(MachineSettings.Decode(new byte[164]));

        [Fact]
        public void Un_REG_BINARY_de_reg_query_se_relit()
            => Assert.Equal(new byte[] { 0xA4, 0x00, 0x1F }, MachineSettings.Hex("A4001F"));

        [Fact]
        public void Les_imprimantes_virtuelles_ne_sont_pas_relevees()
        {
            var output =
                "P\tLocal\tMicrosoft Print to PDF\tMicrosoft Print To PDF\tPORTPROMPT:\t\t0\r\n" +
                "P\tLocal\tOneNote (Desktop)\tSend to Microsoft OneNote 16 Driver\tnul:\t\t0\r\n" +
                "P\tConnection\t\\\\SERVEUR\\Accueil\tKyocera Classic Universaldriver PCL6\t\\\\SERVEUR\\Accueil\t\t0\r\n" +
                "P\tLocal\tBrother MFC-L2710DW\tBrother MFC-L2710DW series\t192.168.1.20\t192.168.1.20\t1\r\n" +
                "P\tLocal\tCanon TS5100 d'accueil\tCanon TS5100 series\tUSB001\t\t0\r\n" +
                "K\tAAAAA-BBBBB-CCCCC-DDDDD-EEEEE\r\n";

            var record = MachineSettings.Parse(output);

            Assert.Equal(new[] { @"\\SERVEUR\Accueil", "Brother MFC-L2710DW", "Canon TS5100 d'accueil" }, record.Printers.Select(printer => printer.Name));
            Assert.Equal(PrinterKind.Connection, record.Printers[0].Kind);
            Assert.Equal(PrinterKind.Network, record.Printers[1].Kind);
            Assert.True(record.Printers[1].IsDefault);
            Assert.Equal(PrinterKind.Local, record.Printers[2].Kind);
            Assert.Equal("AAAAA-BBBBB-CCCCC-DDDDD-EEEEE", Assert.Single(record.Keys).Key);
        }

        [Fact]
        public void Le_fichier_de_la_sauvegarde_se_relit_a_l_identique()
        {
            var record = new MachineSettingsRecord
            {
                Printers = new[]
                {
                    new PrinterEntry { Kind = PrinterKind.Network, Name = "Brother", Driver = "Brother series", Port = "IP_1", Host = "192.168.1.20", IsDefault = true },
                    new PrinterEntry { Kind = PrinterKind.Connection, Name = @"\\SERVEUR\Accueil", Driver = "Kyocera", Port = @"\\SERVEUR\Accueil" },
                },
                Drives = new[] { new NetworkDrive { Letter = "Z:", Path = @"\\NAS\Compta", User = "marie" } },
                Keys = new[] { new ProductKey { Label = "Windows, clé installée", Key = "VK7JG-NPHTM-C97JM-9MPGT-3V66T" } },
            };

            var read = MachineSettings.Read("\uFEFF" + MachineSettings.Document(record));

            Assert.Equal("192.168.1.20", read.Printers[0].Host);
            Assert.True(read.Printers[0].IsDefault);
            Assert.Equal(PrinterKind.Connection, read.Printers[1].Kind);
            Assert.Equal(@"\\NAS\Compta", Assert.Single(read.Drives).Path);
            Assert.Equal("marie", read.Drives[0].User);
            Assert.Equal("VK7JG-NPHTM-C97JM-9MPGT-3V66T", Assert.Single(read.Keys).Key);
        }

        [Fact]
        public void Les_lecteurs_reseau_se_lisent_dans_le_registre_du_compte()
        {
            var registry = new NetworkRegistry();
            registry.Drives["z"] = (@"\\NAS\Compta", "marie");
            registry.Drives["Y"] = (@"\\SERVEUR\Commun", "");

            var drives = MachineSettings.Drives(registry);

            Assert.Equal(new[] { "Y:", "Z:" }, drives.Select(drive => drive.Letter));
            Assert.Null(drives[0].User);
            Assert.Equal("marie", drives[1].User);
        }

        [Fact]
        public async Task La_restauration_reconnecte_les_lecteurs_et_inscrit_ceux_qui_ne_repondent_pas()
        {
            var files = new FakeFileSystemGateway();
            files.Directories.Add(Backup);
            files.Directories.Add(@"C:\");
            files.Files.Add(Backup + @"\manifeste.csv");
            files.Files.Add(Backup + @"\" + MachineSettings.FileName);
            files.Texts[Backup + @"\" + MachineSettings.FileName] = MachineSettings.Document(new MachineSettingsRecord
            {
                Drives = new[]
                {
                    new NetworkDrive { Letter = "Z:", Path = @"\\NAS\Compta" },
                    new NetworkDrive { Letter = "Y:", Path = @"\\SERVEUR\Eteint" },
                    new NetworkDrive { Letter = "C:", Path = @"\\SERVEUR\Pris" },
                },
            });

            var processes = new ScriptedProcessRunner(request =>
                request.FileName == "net.exe" && request.Arguments.Contains("Eteint") ? ActionFakes.Result(exitCode: 2) : ActionFakes.Result());

            var context = ActionFakes.Context(files: files, processes: processes, parameters: new Dictionary<string, string>
            {
                [RestoreUserDataAction.SourceParameter] = Backup,
            });

            var action = new RestoreUserDataAction(new RestoreTargets { UserProfile = @"C:\Users\Nouveau", Documents = @"C:\Users\Nouveau\Documents" });
            var preview = await action.PreviewAsync(context, CancellationToken.None);

            Assert.Contains(preview.WillDo, line => line.Contains(@"Z: à \\NAS\Compta"));
            Assert.Contains(preview.WillNotDo, line => line.Contains("C:") && line.Contains("déjà prise"));

            var outcome = await action.ExecuteAsync(context, preview, null, CancellationToken.None);

            Assert.Contains(outcome.Details, line => line.Contains("Z: reconnecté") && line.Contains("Y: inscrit"));
            Assert.Contains(processes.Requests, request => request.FileName == "reg.exe" && request.Arguments.Contains(@"HKCU\Network\Y") &&
                                                            request.Arguments.Contains("RemotePath"));
            Assert.DoesNotContain(processes.Requests, request => request.Arguments.Contains("Pris"));
        }

        [Fact]
        public async Task Les_imprimantes_reseau_et_partagees_sont_recreees_les_USB_attendent_leur_branchement()
        {
            var files = new FakeFileSystemGateway();
            files.Directories.Add(Backup);
            files.Files.Add(Backup + @"\manifeste.csv");
            files.Texts[Backup + @"\" + MachineSettings.FileName] = MachineSettings.Document(new MachineSettingsRecord
            {
                Printers = new[]
                {
                    new PrinterEntry { Kind = PrinterKind.Network, Name = "Brother d'accueil", Driver = "Brother series", Port = "192.168.1.20", Host = "192.168.1.20", IsDefault = true },
                    new PrinterEntry { Kind = PrinterKind.Connection, Name = @"\\SERVEUR\Accueil", Driver = "Kyocera", Port = @"\\SERVEUR\Accueil" },
                    new PrinterEntry { Kind = PrinterKind.Local, Name = "Canon TS5100", Driver = "Canon", Port = "USB001" },
                },
            });

            var processes = new ScriptedProcessRunner(request => ActionFakes.Result(output: "OK\r\n"));
            var context = ActionFakes.Context(files: files, processes: processes, parameters: new Dictionary<string, string>
            {
                [RestorePrintersAction.SourceParameter] = Backup,
            });

            var action = new RestorePrintersAction();
            var preview = await action.PreviewAsync(context, CancellationToken.None);
            var plan = Assert.IsType<PrinterRestorePlan>(preview.Plan);

            Assert.Equal(2, plan.Printers.Count);
            Assert.Equal("Brother d'accueil", plan.Default);
            Assert.Contains(preview.WillNotDo, line => line.Contains("Canon TS5100"));

            // L'apostrophe d'un nom ne casse pas le script : elle est doublée.
            Assert.Contains("'Brother d''accueil'", RestorePrintersAction.Script(plan.Printers[0]));

            var outcome = await action.ExecuteAsync(context, preview, null, CancellationToken.None);
            Assert.Equal(ActionStatus.Succeeded, outcome.Status);
            Assert.Contains(outcome.Details, line => line.Contains("par défaut"));
        }

        /// <summary>L'inverse du décodage : une clé, rangée comme Windows 8 et suivants la rangent.</summary>
        private static byte[] Encode(string key)
        {
            const string chars = "BCDFGHJKMPQRTVWXY2346789";
            var plain = key.Replace("-", string.Empty);
            var position = plain.IndexOf('N');
            var rest = plain.Remove(position, 1);

            var digits = new List<int> { position };
            digits.AddRange(rest.Select(character => chars.IndexOf(character)));

            var number = new byte[15];
            foreach (var digit in digits)
            {
                var carry = digit;
                for (var index = 0; index < number.Length; index++)
                {
                    var value = number[index] * 24 + carry;
                    number[index] = (byte)(value & 0xFF);
                    carry = value >> 8;
                }
            }

            var id = new byte[164];
            Array.Copy(number, 0, id, 52, number.Length);
            id[66] |= 0x08;
            return id;
        }

        private sealed class NetworkRegistry : IRegistryGateway
        {
            public Dictionary<string, (string Path, string User)> Drives { get; } = new Dictionary<string, (string, string)>();

            public string? ReadString(RegistryHive hive, string subKey, string valueName, RegistryView view = RegistryView.Default)
            {
                foreach (var pair in Drives)
                    if (subKey == @"Network\" + pair.Key)
                        return valueName == "RemotePath" ? pair.Value.Path : valueName == "UserName" ? pair.Value.User : null;
                return null;
            }

            public int? ReadInt32(RegistryHive hive, string subKey, string valueName, RegistryView view = RegistryView.Default) => null;
            public long? ReadInt64(RegistryHive hive, string subKey, string valueName, RegistryView view = RegistryView.Default) => null;
            public string[]? ReadMultiString(RegistryHive hive, string subKey, string valueName, RegistryView view = RegistryView.Default) => null;
            public byte[]? ReadBinary(RegistryHive hive, string subKey, string valueName, RegistryView view = RegistryView.Default) => null;
            public bool KeyExists(RegistryHive hive, string subKey, RegistryView view = RegistryView.Default) => true;

            public IReadOnlyList<string> GetSubKeyNames(RegistryHive hive, string subKey, RegistryView view = RegistryView.Default)
                => hive == RegistryHive.CurrentUser && subKey == "Network" ? Drives.Keys.ToList() : (IReadOnlyList<string>)Array.Empty<string>();

            public IReadOnlyList<string> GetValueNames(RegistryHive hive, string subKey, RegistryView view = RegistryView.Default)
                => Array.Empty<string>();
        }
    }
}
