using System;
using System.Linq;
using LDI12.Core.Diagnostics;
using LDI12.Core.Model;
using LDI12.Platform.Gateways;
using LDI12.Platform.Native;
using Xunit;

namespace LDI12.Tests
{
    /// <summary>
    /// La lecture SMART sans privilèges : ce que la demande de prédiction de panne rapporte,
    /// et ce qu'elle ne rapporte pas.
    /// </summary>
    public class SmartTests
    {
        [Fact]
        public void La_table_d_attributs_se_lit_a_sa_place_dans_la_reponse_de_prediction()
        {
            // Disposition de STORAGE_PREDICT_FAILURE : un entier de verdict, puis les 512 octets
            // que le disque rend. Se tromper de quatre octets décalerait chaque attribut et
            // ferait lire des heures de fonctionnement dans un compteur d'erreurs.
            Assert.Equal(4, StorageNative.PredictFailureDataOffset);
            Assert.Equal(516, StorageNative.PredictFailureSize);
            Assert.Equal(
                StorageNative.PredictFailureSize,
                StorageNative.PredictFailureDataOffset + StorageNative.SmartDataSize);
        }

        [Fact]
        public void Les_heures_et_les_cycles_se_lisent_sans_les_seuils()
        {
            // C'est tout l'intérêt du chemin non privilégié : ces deux compteurs sont ceux qu'on
            // vient chercher sur un disque, et ils ne dépendent d'aucun seuil constructeur.
            var sector = Sector(
                Attribute(9, current: 96, raw: 9199),
                Attribute(12, current: 99, raw: 1896),
                Attribute(5, current: 98, raw: 48));

            var smart = SmartParser.Parse(sector, thresholdSector: null);

            Assert.Equal(9199, smart.PowerOnHours.Value);
            Assert.Equal(1896, smart.PowerCycles.Value);
            Assert.Equal(48, smart.ReallocatedSectors.Value);
        }

        [Fact]
        public void Sans_seuils_aucun_attribut_n_est_declare_au_dela_du_sien()
        {
            // Un seuil inconnu vaut zéro dans la table ; le prendre pour un seuil réel ferait
            // déclarer une panne sur le premier disque venu.
            var sector = Sector(Attribute(5, current: 1, raw: 4000));

            var smart = SmartParser.Parse(sector, thresholdSector: null);
            var reallocated = Assert.Single(smart.Attributes.Where(entry => entry.Id == 5));

            Assert.Equal(0, reallocated.Threshold);
            Assert.False(reallocated.ThresholdExceeded);
        }

        [Fact]
        public void Le_verdict_du_disque_remplace_la_comparaison_qu_on_ne_peut_pas_faire()
        {
            // Sans seuils, l'outil ne peut pas conclure à la panne lui-même. Le disque, lui,
            // a fait la comparaison : son verdict est repris tel quel.
            var sector = Sector(Attribute(5, current: 1, raw: 4000));

            var silent = SmartParser.Parse(sector, thresholdSector: null);
            var predicted = SmartParser.Parse(sector, thresholdSector: null, predictedFailure: true);

            Assert.Equal(SmartOverallStatus.Warning, silent.OverallStatus.Value);
            Assert.Equal(SmartOverallStatus.Failing, predicted.OverallStatus.Value);
        }

        [Fact]
        public void Un_secteur_vide_ne_decrit_pas_un_disque_neuf()
        {
            // Certains boîtiers répondent à la demande sans y joindre d'attributs. Lire zéro
            // heure et zéro cycle serait le pire des résultats : un disque de dix ans annoncé
            // comme sortant de l'usine.
            var smart = SmartParser.Parse(new byte[StorageNative.SmartDataSize], thresholdSector: null);

            Assert.Empty(smart.Attributes);
            Assert.False(smart.PowerOnHours.HasValue);
            Assert.False(smart.PowerCycles.HasValue);
        }

        [Fact]
        public void Les_seuils_restent_lus_quand_ils_sont_disponibles()
        {
            // Le chemin privilégié n'a rien perdu : quand les seuils sont là, la comparaison se
            // fait comme avant.
            var attributes = Sector(Attribute(5, current: 10, raw: 4000));
            var thresholds = Sector(Threshold(5, value: 36));

            var smart = SmartParser.Parse(attributes, thresholds);
            var reallocated = Assert.Single(smart.Attributes.Where(entry => entry.Id == 5));

            Assert.Equal(36, reallocated.Threshold);
            Assert.True(reallocated.ThresholdExceeded);
            Assert.Equal(SmartOverallStatus.Failing, smart.OverallStatus.Value);
        }

        [Fact]
        public void Adjoindre_la_sante_a_un_disque_ne_perd_aucune_de_ses_caracteristiques()
        {
            // Le défaut que ce test verrouille s'est produit : la sonde SMART recopiait le disque
            // champ par champ, et le premier champ ajouté après elle (la génération de liaison)
            // disparaissait sans erreur, entre la passerelle qui le lisait et l'écran qui devait
            // l'afficher. Une recopie manuelle ne prévient jamais de ce qu'elle oublie.
            var disk = new PhysicalDiskInfo
            {
                Index = 3,
                Model = Measured.Ok("MODELE", DataSource.NativeApi),
                Manufacturer = Measured.Ok("FABRICANT", DataSource.NativeApi),
                Firmware = Measured.Ok("FW1", DataSource.NativeApi),
                SerialNumber = Measured.Ok("SN1", DataSource.NativeApi),
                BusType = Measured.Ok(StorageBusType.Sata, DataSource.NativeApi),
                MediaType = Measured.Ok(StorageMediaType.Ssd, DataSource.NativeApi),
                CapacityBytes = Measured.Ok(1024L, DataSource.NativeApi),
                IsSystemDisk = Measured.Ok(true, DataSource.NativeApi),
                Identity = new AtaIdentity
                {
                    RotationRpm = Measured.Ok(7200, DataSource.NativeApi),
                    LinkGigabitsPerSecond = Measured.Ok(3.0, DataSource.NativeApi),
                    MaximumGigabitsPerSecond = Measured.Ok(6.0, DataSource.NativeApi),
                },
            };

            var carried = disk.WithSmart(new SmartData());

            foreach (var property in typeof(PhysicalDiskInfo).GetProperties())
            {
                if (property.Name == nameof(PhysicalDiskInfo.Smart)) continue;
                if (property.GetIndexParameters().Length > 0) continue;

                Assert.True(
                    Equals(property.GetValue(disk), property.GetValue(carried)),
                    "La propriété « " + property.Name + " » est perdue en adjoignant la santé du disque.");
            }
        }

        [Fact]
        public void La_commande_ata_a_la_taille_que_le_pilote_attend()
        {
            // Même garde-fou que pour les structures sans fil : une disposition fausse ne
            // provoque pas d'erreur, elle rend des valeurs plausibles et fausses. Le tampon de
            // données commence là où l'en-tête l'annonce, et l'en-tête change de taille entre
            // 32 et 64 bits à cause d'un ULONG_PTR.
            var size = System.Runtime.InteropServices.Marshal.SizeOf(
                typeof(StorageNative.ATA_PASS_THROUGH_EX));

            Assert.Equal(IntPtr.Size == 8 ? 48 : 40, size);
            Assert.Equal(512, StorageNative.IdentifySize);
            Assert.Equal(0xEC, StorageNative.ATA_COMMAND_IDENTIFY);
        }

        [Theory]
        [InlineData(7200, 7200)]     // disque à plateaux ordinaire
        [InlineData(5400, 5400)]
        [InlineData(0x0401, 0x0401)] // première valeur que la norme autorise
        public void La_vitesse_de_rotation_se_lit_telle_que_le_disque_la_declare(int word, int expected)
        {
            var rotation = AtaIdentityParser.Rotation(word);

            Assert.True(rotation.HasValue);
            Assert.Equal(expected, rotation.Value);
        }

        [Theory]
        [InlineData(0x0000)]   // champ jamais rempli
        [InlineData(0xFFFF)]   // contrôleur qui ne renseigne rien
        [InlineData(0x0400)]   // plage réservée par la norme, sous la première valeur valide
        public void Une_rotation_non_declaree_n_est_jamais_prise_pour_une_mesure(int word)
        {
            // Sans ce filtre, un disque rendrait « 0 tour par minute » ou « 65 535 », deux
            // chiffres qu'un rapport client ne pourrait pas porter.
            Assert.False(AtaIdentityParser.Rotation(word).HasValue);
        }

        [Fact]
        public void Un_disque_a_memoire_flash_declare_qu_il_ne_tourne_pas()
        {
            // Ce n'est pas une lacune, c'est une confirmation : le libellé doit le dire.
            var rotation = AtaIdentityParser.Rotation(0x0001);

            Assert.False(rotation.HasValue);
            Assert.Contains("mémoire flash", rotation.Reason);
        }

        [Theory]
        [InlineData(0x0002, 1.5)]   // bit 1 : première génération
        [InlineData(0x0004, 3.0)]   // bit 2 : deuxième
        [InlineData(0x0006, 3.0)]   // les bits sont cumulatifs, le plus haut compte
        [InlineData(0x000E, 6.0)]   // troisième génération, qui sait parler aux deux autres
        public void La_generation_maximale_est_la_plus_haute_que_le_disque_annonce(
            int capabilities, double expected)
        {
            var supported = AtaIdentityParser.Supported(capabilities);

            Assert.True(supported.HasValue);
            Assert.Equal(expected, supported.Value);
        }

        [Theory]
        [InlineData(0x0002, 1.5)]
        [InlineData(0x0004, 3.0)]
        [InlineData(0x0006, 6.0)]
        public void La_generation_negociee_se_lit_a_partir_du_bit_1(int status, double expected)
        {
            // Le bit 0 du mot 77 est réservé : oublier de décaler diviserait chaque débit par
            // deux, et ferait passer un port de sixième génération pour un port de troisième.
            var negotiated = AtaIdentityParser.Negotiated(capabilities: 0x000E, status: status);

            Assert.True(negotiated.HasValue);
            Assert.Equal(expected, negotiated.Value);
        }

        [Theory]
        [InlineData(0x0000)]
        [InlineData(0xFFFF)]
        public void Un_disque_qui_n_est_pas_en_sata_ne_se_voit_attribuer_aucune_generation(int capabilities)
        {
            // Un NVMe ou un boîtier USB laisse ce mot vide. Lui inventer une génération de
            // liaison ferait apparaître un bridage là où la notion n'existe pas.
            Assert.False(AtaIdentityParser.Supported(capabilities).HasValue);
            Assert.False(AtaIdentityParser.Negotiated(capabilities, status: 0x0006).HasValue);
        }

        [Fact]
        public void Un_disque_de_sixieme_generation_sur_un_port_de_troisieme_se_voit()
        {
            // Le cas d'atelier : le disque sait tenir 6 Gb/s, le port lui en donne 3.
            var identify = new byte[512];
            Word(identify, 76, 0x000E);   // le disque sait faire les trois générations
            Word(identify, 77, 0x0004);   // la liaison a été négociée en deuxième
            Word(identify, 217, 0x0001);  // et c'est une mémoire flash

            var identity = AtaIdentityParser.Parse(identify);

            Assert.Equal(6.0, identity.MaximumGigabitsPerSecond.Value);
            Assert.Equal(3.0, identity.LinkGigabitsPerSecond.Value);
            Assert.False(identity.RotationRpm.HasValue);
        }

        private static void Word(byte[] identify, int index, int value)
        {
            identify[index * 2] = (byte)(value & 0xFF);
            identify[(index * 2) + 1] = (byte)((value >> 8) & 0xFF);
        }

        /// <summary>Secteur SMART : deux octets de révision, puis trente entrées de douze octets.</summary>
        private static byte[] Sector(params byte[][] entries)
        {
            var sector = new byte[StorageNative.SmartDataSize];
            sector[0] = 0x10;

            for (var index = 0; index < entries.Length; index++)
            {
                var offset = StorageNative.SmartAttributeTableOffset +
                             (index * StorageNative.SmartAttributeSize);
                Buffer.BlockCopy(entries[index], 0, sector, offset, entries[index].Length);
            }

            return sector;
        }

        private static byte[] Attribute(byte id, byte current, long raw)
        {
            var entry = new byte[StorageNative.SmartAttributeSize];
            entry[0] = id;
            entry[1] = 0x0B;
            entry[3] = current;
            entry[4] = current;
            for (var b = 0; b < 6; b++) entry[5 + b] = (byte)((raw >> (8 * b)) & 0xFF);
            return entry;
        }

        /// <summary>Dans la table des seuils, le second octet porte la valeur constructeur.</summary>
        private static byte[] Threshold(byte id, byte value)
        {
            var entry = new byte[StorageNative.SmartAttributeSize];
            entry[0] = id;
            entry[1] = value;
            return entry;
        }
    }
}
