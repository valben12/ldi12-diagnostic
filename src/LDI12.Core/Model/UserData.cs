using System;
using System.Collections.Generic;
using LDI12.Core.Diagnostics;

namespace LDI12.Core.Model
{
    /// <summary>Nature d'un dossier de données personnelles.</summary>
    public enum UserDataKind
    {
        Desktop = 0,
        Documents = 1,
        Pictures = 2,
        Music = 3,
        Videos = 4,
        Downloads = 5,

        /// <summary>Dossier synchronisé avec un espace en ligne.</summary>
        Cloud = 6,

        /// <summary>
        /// Tout ce que le profil contient d'autre.
        /// </summary>
        /// <remarks>
        /// Ce n'est pas un fourre-tout : c'est là que vivent les messageries locales, les profils
        /// de navigateur, les favoris et les licences de logiciels. Le dossier que tout le monde
        /// oublie, et dont le client s'aperçoit trois jours après la réinstallation.
        /// </remarks>
        RestOfProfile = 7,
    }

    /// <summary>Un dossier pesé, et ce que la pesée n'a pas pu voir.</summary>
    public sealed class UserDataFolder
    {
        public UserDataKind Kind { get; init; }

        public string Label { get; init; } = string.Empty;

        public string Path { get; init; } = string.Empty;

        /// <summary>Taille annoncée, fichiers en ligne compris.</summary>
        public Measured<long> TotalBytes { get; init; }

        /// <summary>Ce qui est réellement sur ce disque, et qu'une copie emporterait.</summary>
        public Measured<long> OnDiskBytes { get; init; }

        public long CloudOnlyBytes { get; init; }

        public int FileCount { get; init; }

        public int CloudOnlyFileCount { get; init; }

        /// <summary>Le relevé s'est arrêté sur son budget : le total est un minimum.</summary>
        public bool Truncated { get; init; }

        public bool HasCloudOnly => CloudOnlyFileCount > 0;
    }

    /// <summary>Un autre compte ayant un profil sur cette machine.</summary>
    public sealed class ForeignProfile
    {
        public string Name { get; init; } = string.Empty;

        public string Path { get; init; } = string.Empty;
    }

    /// <summary>
    /// Ce qu'il y aurait à sauvegarder avant de réinstaller.
    /// </summary>
    /// <remarks>
    /// <b>Le pendant en lecture du nettoyage.</b> L'un montre ce qui peut disparaître, l'autre ce
    /// qu'il ne faut pas perdre, et les deux répondent à la même exigence du cahier des charges :
    /// rien concernant les données personnelles ne se décide sans que le technicien ait vu de quoi
    /// il s'agit.
    /// <para>
    /// Aucun nom de fichier n'y figure, à aucun moment. Ce relevé compte des octets et des
    /// fichiers ; il ne dresse pas la liste de ce que le client garde chez lui.
    /// </para>
    /// </remarks>
    public sealed class UserDataSurvey
    {
        public DateTimeOffset At { get; init; } = DateTimeOffset.Now;

        public TimeSpan Duration { get; init; }

        public string Account { get; init; } = string.Empty;

        public IReadOnlyList<UserDataFolder> Folders { get; init; } = Array.Empty<UserDataFolder>();

        public Measured<long> TotalBytes { get; init; }

        public Measured<long> OnDiskBytes { get; init; }

        public long CloudOnlyBytes { get; init; }

        public int FileCount { get; init; }

        /// <summary>Profils d'autres comptes présents sur la machine, jamais mesurés.</summary>
        public IReadOnlyList<ForeignProfile> OtherProfiles { get; init; } = Array.Empty<ForeignProfile>();

        /// <summary>Ce que ce relevé ne dit pas, et il en dit beaucoup.</summary>
        public IReadOnlyList<string> Caveats { get; init; } = Array.Empty<string>();

        public string Headline { get; init; } = string.Empty;

        public bool HasCloudOnly => CloudOnlyBytes > 0;
    }
}
