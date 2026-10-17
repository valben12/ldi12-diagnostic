using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using LDI12.Assistant;
using LDI12.Core.Model;
using Xunit;

namespace LDI12.Tests
{
    /// <summary>L'assistant : la clé rangée sur la clé USB, et ce que Claude lit et rend.</summary>
    public class AssistantTests
    {
        private const string Key = "sk-ant-api03-exemple-de-cle-pour-les-tests-4f2a";

        [Fact]
        public void La_cle_se_relit_sur_le_meme_support()
        {
            var data = ApiKeyVault.EncryptPortable(Key, "1A2B3C4D");

            Assert.Equal(Key, ApiKeyVault.DecryptPortable(data, "1A2B3C4D"));
            Assert.DoesNotContain("api03", System.Text.Encoding.UTF8.GetString(data));
        }

        [Fact]
        public void Copiee_sur_un_autre_support_la_cle_ne_se_lit_plus()
        {
            var data = ApiKeyVault.EncryptPortable(Key, "1A2B3C4D");

            Assert.Null(ApiKeyVault.DecryptPortable(data, "99999999"));
            Assert.Null(ApiKeyVault.DecryptPortable(data, null));
        }

        [Fact]
        public void Un_fichier_retouche_est_refuse()
        {
            var data = ApiKeyVault.EncryptPortable(Key, "1A2B3C4D");
            data[data.Length / 2] ^= 0x01;

            Assert.Null(ApiKeyVault.DecryptPortable(data, "1A2B3C4D"));
        }

        [Fact]
        public void La_cle_se_range_a_cote_du_logiciel_et_s_oublie()
        {
            var root = Path.Combine(Path.GetTempPath(), "LDI12-cle-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var vault = new ApiKeyVault(root, Path.Combine(root, "profil"), _ => "CAFE0001");

                Assert.Equal(ApiKeyLocation.Portable, vault.Save("  " + Key + "  "));
                Assert.True(File.Exists(Path.Combine(root, ApiKeyVault.FileName)));
                Assert.Equal(Key, vault.Read());

                // Le même fichier, lu depuis un autre support : rien.
                Assert.Null(new ApiKeyVault(root, Path.Combine(root, "profil"), _ => "CAFE0002").Read());

                vault.Forget();
                Assert.Equal(ApiKeyLocation.None, vault.Location);
                Assert.Null(vault.Read());
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void Le_masque_ne_montre_que_la_fin()
            => Assert.Equal("sk-ant-…4f2a", ApiKeyVault.Mask(Key));

        [Fact]
        public void Une_operation_inconnue_du_plan_ne_s_execute_jamais()
        {
            var input = Parse(@"{
                ""summary"": ""Le Wi-Fi décroche."",
                ""confidence"": ""forte"",
                ""findings"": [ { ""title"": ""Pilote générique"", ""detail"": ""Realtek 2019"", ""severity"": ""problème"" } ],
                ""steps"": [
                    { ""title"": ""Réinitialiser le réseau"", ""why"": ""Réglages laissés par l'ancien pilote."", ""action_id"": ""repair-network-stack"" },
                    { ""title"": ""Formater"", ""why"": ""?"", ""action_id"": ""FORMAT-C"" },
                    { ""title"": ""Redémarrer le spouleur"", ""why"": ""Bloqué."", ""action_id"": ""REPAIR-RESTART-SERVICE"", ""parameters"": { ""service"": ""Spooler"" } },
                    { ""title"": ""Pilote du constructeur"", ""why"": ""Plus stable."", ""action_id"": """", ""manual"": ""Site du fabricant."" }
                ]
            }");
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "REPAIR-NETWORK-STACK", "REPAIR-RESTART-SERVICE" };

            var plan = ClaudeAnalyst.ParsePlan(input, known);

            Assert.Equal("forte", plan.Confidence);
            Assert.Single(plan.Findings);
            Assert.Equal("REPAIR-NETWORK-STACK", plan.Steps[0].ActionId);
            Assert.Null(plan.Steps[1].ActionId);
            Assert.Contains("FORMAT-C", plan.Steps[1].Manual);
            Assert.Equal("Spooler", plan.Steps[2].Parameters["service"]);
            Assert.False(plan.Steps[3].IsAutomatic);
            Assert.Equal("Site du fabricant.", plan.Steps[3].Manual);
        }

        [Fact]
        public void Claude_lit_les_pilotes_filtres_et_rien_d_autre()
        {
            var snapshot = new SystemSnapshot
            {
                Windows = new WindowsSnapshot
                {
                    Drivers = new[]
                    {
                        new DriverInfo { DeviceName = "Realtek RTL8821CE Wireless LAN", Manufacturer = "Microsoft", Version = "2019.6.12", IsMicrosoftGeneric = true },
                        new DriverInfo { DeviceName = "Intel UHD Graphics 620", Manufacturer = "Intel", Version = "26.20.100.7262" },
                    },
                },
            };

            var json = MachineData.Read(snapshot, "drivers", "realtek");

            Assert.Contains("RTL8821CE", json);
            Assert.DoesNotContain("UHD", json);
            Assert.StartsWith("Section inconnue", MachineData.Read(snapshot, "documents", null));
        }

        private static IReadOnlyDictionary<string, JsonElement> Parse(string json)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        }
    }
}
