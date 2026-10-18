using LDI12.Reports.Facts;
using Xunit;

namespace LDI12.Tests
{
    /// <summary>Le réglage du fichier d'échange, lu tel que Windows l'écrit, s'affiche en clair.</summary>
    public class PageFileTests
    {
        [Fact]
        public void Deux_tailles_nulles_veulent_dire_taille_automatique()
            => Assert.Equal(@"D:\pagefile.sys, taille automatique", FactSheetBuilder.DescribePageFileSetting(@"D:\pagefile.sys 0 0"));

        [Fact]
        public void Le_reglage_sur_chaque_disque_se_dit_en_francais()
            => Assert.Equal("Chaque disque, taille automatique", FactSheetBuilder.DescribePageFileSetting(@"?:\pagefile.sys"));

        [Fact]
        public void Des_tailles_imposees_sont_donnees_en_mo()
        {
            var text = FactSheetBuilder.DescribePageFileSetting(@"C:\pagefile.sys 4096 8192 · E:\pagefile.sys 0 0");

            Assert.StartsWith(@"C:\pagefile.sys, 4", text);
            Assert.Contains("192 Mo", text);
            Assert.EndsWith(@"E:\pagefile.sys, taille automatique", text);
        }
    }
}
