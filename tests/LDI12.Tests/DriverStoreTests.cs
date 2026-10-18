using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Actions.Backup;
using LDI12.Actions.Repairs;
using LDI12.Core.Drivers;
using Xunit;

namespace LDI12.Tests
{
    /// <summary>Le magasin de pilotes : qui est une ancienne version, et ce qu'on a le droit d'en retirer.</summary>
    public partial class NativeRepairTests
    {
        private static DriverPackage Package(string oem, string original, string version, int year, params string[] usedBy) => new DriverPackage
        {
            PublishedName = oem, OriginalName = original, Provider = "NVIDIA", DeviceClass = "Display", Version = version,
            Date = new DateTime(year, 6, 1), SizeBytes = 1_000_000, UsedBy = usedBy,
        };

        [Fact]
        public void Les_versions_remplacees_et_inutilisees_sont_seules_proposees()
        {
            var packages = new List<DriverPackage>
            {
                Package("oem41.inf", "nv_dispig.inf", "32.0.15.6094", 2024, "NVIDIA GeForce RTX 3060"),
                Package("oem33.inf", "nv_dispig.inf", "31.0.15.3623", 2023),
                Package("oem27.inf", "nv_dispig.inf", "31.0.15.2849", 2022),
                Package("oem7.inf", "hpcu255u.inf", "61.255.1.24923", 2021),      // imprimante débranchée : seule de son espèce
            };

            DriverStoreAnalyzer.Classify(packages);

            Assert.Equal(PackageStatus.InUse, packages[0].Status);
            Assert.Equal(PackageStatus.OldVersion, packages[1].Status);
            Assert.Equal(PackageStatus.OldVersion, packages[2].Status);
            Assert.Equal(PackageStatus.Latest, packages[3].Status);
        }

        [Fact]
        public void Une_version_plus_ancienne_reste_si_un_peripherique_s_en_sert_encore()
        {
            var packages = new List<DriverPackage>
            {
                Package("oem41.inf", "nv_dispig.inf", "32.0.15.6094", 2024),
                Package("oem33.inf", "nv_dispig.inf", "31.0.15.3623", 2023, "Ancienne carte"),
            };

            DriverStoreAnalyzer.Classify(packages);

            Assert.Equal(PackageStatus.Latest, packages[0].Status);
            Assert.Equal(PackageStatus.InUse, packages[1].Status);
        }

        [Fact]
        public void La_date_et_le_catalogue_du_pilote_se_lisent_dans_l_inf()
        {
            var summary = OfflineDrivers.Parse("[Version]\r\nSignature=\"$WINDOWS NT$\"\r\nClass=Display\r\nProvider=%NVIDIA%\r\nDriverVer=09/26/2024,32.0.15.6094\r\nCatalogFile=NV_DISP.CAT\r\n[Strings]\r\nNVIDIA=\"NVIDIA\"\r\n");

            Assert.Equal(new DateTime(2024, 9, 26), summary.Date);
            Assert.Equal("NV_DISP.CAT", summary.CatalogFile);
            Assert.Equal("NVIDIA", summary.Provider);
        }

        [Fact]
        public async Task Seuls_des_paquets_ajoutes_sont_retires_et_un_refus_est_dit()
        {
            var control = new FakeControl();
            control.PackagesInUse.Add("oem33.inf");
            var context = Context(control,
                (DriverPackagesDeleteAction.PackagesParameter, "oem27.inf|oem33.inf|nvlddmkm.inf|..\\oem1.inf"),
                (DriverPackagesDeleteAction.LabelsParameter, "NVIDIA 2022|NVIDIA 2023|piège|piège"));
            var action = new DriverPackagesDeleteAction();

            var outcome = await action.ExecuteAsync(context, await action.PreviewAsync(context, CancellationToken.None), null, CancellationToken.None);

            Assert.Equal(new[] { "delete-package oem27.inf", "delete-package oem33.inf" }, control.Calls);
            Assert.Equal(LDI12.Actions.ActionStatus.PartiallySucceeded, outcome.Status);
            Assert.Contains("utilise", outcome.Summary);
        }
    }
}
