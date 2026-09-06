using System;
using LDI12.Core.Diagnostics;
using Xunit;

namespace LDI12.Tests
{
    public class MeasuredTests
    {
        [Fact]
        public void Une_mesure_non_initialisee_ne_se_fait_pas_passer_pour_disponible()
        {
            // Le piège que la structure doit rendre impossible : un champ oublié qui afficherait
            // « 0 °C » ou « 0 Go » comme s'il s'agissait d'une vraie mesure.
            var measured = default(Measured<int>);

            Assert.Equal(Availability.Unknown, measured.Availability);
            Assert.False(measured.HasValue);
            Assert.False(measured.IsReliable);
            Assert.Equal(0, measured.ValueOrDefault);
        }

        [Fact]
        public void Lire_la_valeur_d_une_mesure_absente_leve_plutot_que_de_mentir()
        {
            var measured = Measured.Missing<int>("SMART NVMe requiert Windows 10 1607.");

            var ex = Assert.Throws<InvalidOperationException>(() => measured.Value);
            Assert.Contains("SMART NVMe", ex.Message);
        }

        [Fact]
        public void Une_mesure_absente_doit_toujours_porter_sa_raison()
        {
            Assert.Throws<ArgumentException>(() => Measured.Missing<string>(string.Empty));
            Assert.Throws<ArgumentException>(() => Measured.Partial(1, DataSource.Wmi, "   "));
        }

        [Fact]
        public void Une_mesure_reussie_expose_sa_valeur_et_sa_provenance()
        {
            var measured = Measured.Ok(42, DataSource.NativeApi);

            Assert.True(measured.IsReliable);
            Assert.Equal(42, measured.Value);
            Assert.Equal(DataSource.NativeApi, measured.Source);
            Assert.Null(measured.Reason);
        }

        [Fact]
        public void Une_mesure_partielle_est_exploitable_mais_pas_fiable()
        {
            var measured = Measured.Partial(38, DataSource.Wmi, "Un seul capteur sur trois a répondu.");

            Assert.True(measured.HasValue);
            Assert.False(measured.IsReliable);   // une règle ne doit pas conclure là-dessus
            Assert.Equal(38, measured.Value);
        }

        [Fact]
        public void L_elevation_requise_est_un_etat_distinct_de_l_indisponibilite()
        {
            var measured = Measured.NeedsElevation<string>("lecture SMART du disque physique");

            Assert.Equal(Availability.RequiresElevation, measured.Availability);
            Assert.False(measured.HasValue);
            Assert.Contains("administrateur", measured.Reason!);
        }

        [Fact]
        public void Map_conserve_l_etat_et_la_raison()
        {
            var absent = Measured.Missing<int>("Compteur GPU indisponible avant Windows 10 1709.");
            var projected = absent.Map(v => v.ToString());

            Assert.Equal(Availability.Unavailable, projected.Availability);
            Assert.Equal(absent.Reason, projected.Reason);
            Assert.False(projected.HasValue);
        }

        [Fact]
        public void Or_fournit_un_repli_sans_masquer_l_etat()
        {
            var absent = Measured.Missing<int>("Non mesurable.");

            Assert.Equal(-1, absent.Or(-1));
            Assert.Equal(Availability.Unavailable, absent.Availability);
        }
    }
}
