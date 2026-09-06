using System;

namespace LDI12.Core.Platform
{
    public enum WindowsFamily
    {
        Unknown = 0,

        /// <summary>Version antérieure à Windows 7 SP1, ou Windows 7 RTM : non prise en charge.</summary>
        Unsupported = 1,

        Windows7 = 2,
        Windows8 = 3,
        Windows81 = 4,
        Windows10 = 5,
        Windows11 = 6,
    }

    /// <summary>Résumé du niveau de service que l'application peut rendre sur cette machine.</summary>
    public enum CompatibilityLevel
    {
        /// <summary>L'application refuse de démarrer.</summary>
        Unsupported = 0,

        /// <summary>Windows 7 SP1, pas de MSFT_PhysicalDisk, pas de SMART NVMe, PowerShell 2.0.</summary>
        Minimal = 1,

        /// <summary>Windows 8.1 / Windows 10 antérieur à 1809, SMART NVMe et métriques GPU limitées.</summary>
        Reduced = 2,

        /// <summary>Windows 10 1809+ / Windows 11, toutes les fonctions disponibles.</summary>
        Full = 3,
    }

    public enum ProcessorArchitecture
    {
        Unknown = 0,
        X86 = 1,
        X64 = 2,
        Arm = 3,
        Arm64 = 4,
    }

    /// <summary>
    /// Identité complète de la plateforme Windows sous-jacente.
    /// </summary>
    /// <remarks>
    /// Construite par LDI12.Platform à partir de <c>ntdll!RtlGetVersion</c> croisé avec le registre.
    /// <see cref="Environment.OSVersion"/> n'est jamais utilisé : Windows ment aux applications
    /// via la couche de compatibilité, et renvoie 6.2 sur Windows 10 et 11.
    /// </remarks>
    public sealed class WindowsProfile
    {
        public WindowsFamily Family { get; init; }

        /// <summary>Version réelle rapportée par RtlGetVersion (ex. 10.0.22631).</summary>
        public Version Version { get; init; } = new Version(0, 0);

        public int Build { get; init; }

        /// <summary>Révision de build (UBR). 0 avant Windows 10.</summary>
        public int UpdateBuildRevision { get; init; }

        /// <summary>« 23H2 ». Absent avant Windows 10 20H2.</summary>
        public string? DisplayVersion { get; init; }

        /// <summary>« 1809 ». Figé à 2009 sur Windows 10 21H2 et 22H2, d'où le repli sur DisplayVersion.</summary>
        public string? ReleaseId { get; init; }

        /// <summary>« Professional », « Core », « Enterprise »…</summary>
        public string? EditionId { get; init; }

        /// <summary>Nom commercial tel qu'affiché par Windows. Vaut encore « Windows 10 » sur Windows 11.</summary>
        public string? ProductName { get; init; }

        public int ServicePackMajor { get; init; }

        public bool IsServer { get; init; }

        /// <summary>Architecture réelle de la machine.</summary>
        public ProcessorArchitecture NativeArchitecture { get; init; }

        /// <summary>Architecture du processus courant (peut différer : x86 sous WOW64, ou émulé sur ARM64).</summary>
        public ProcessorArchitecture ProcessArchitecture { get; init; }

        public bool IsWow64 { get; init; }

        /// <summary>Version majeure de PowerShell. 2 sur un Windows 7 nu : PowerShell n'est alors qu'un accélérateur optionnel.</summary>
        public int PowerShellMajorVersion { get; init; }

        public string? SystemLocale { get; init; }

        public bool IsDomainJoined { get; init; }

        public CompatibilityLevel Level { get; init; }

        /// <summary>Explication du niveau de compatibilité, affichée dans le bandeau d'en-tête.</summary>
        public string LevelReason { get; init; } = string.Empty;

        /// <summary>« 22631.3155 »</summary>
        public string BuildString => UpdateBuildRevision > 0
            ? Build.ToString(System.Globalization.CultureInfo.InvariantCulture) + "." +
              UpdateBuildRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : Build.ToString(System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>
        /// Édition en clair. Le registre stocke des identifiants techniques (« Core » pour
        /// l'édition Famille) qu'un rapport client ne peut pas afficher tels quels.
        /// </summary>
        public string? EditionLabel => EditionId switch
        {
            null or "" => null,
            "Core" => "Famille",
            "CoreN" => "Famille N",
            "CoreSingleLanguage" => "Famille Langue unique",
            "CoreCountrySpecific" => "Famille (édition pays)",
            "Professional" => "Professionnel",
            "ProfessionalN" => "Professionnel N",
            "ProfessionalEducation" => "Professionnel Éducation",
            "ProfessionalWorkstation" => "Professionnel pour stations de travail",
            "Enterprise" => "Entreprise",
            "EnterpriseN" => "Entreprise N",
            "EnterpriseS" => "Entreprise LTSC",
            "Education" => "Éducation",
            "IoTEnterprise" => "IoT Entreprise",
            "Ultimate" => "Édition Intégrale",
            "HomePremium" => "Familiale Premium",
            "HomeBasic" => "Familiale Basique",
            "Starter" => "Starter",
            "Business" => "Professionnel",
            "ServerStandard" => "Serveur Standard",
            "ServerDatacenter" => "Serveur Datacenter",
            _ => EditionId,
        };

        /// <summary>« Windows 11 Famille 25H2 (26200.9278) : x64 »</summary>
        public string DisplayName
            => ShortName + " (" + BuildString + ") : " + ArchitectureLabel(NativeArchitecture);

        /// <summary>
        /// « Windows 11 Famille 25H2 », la version, sans le numéro de compilation.
        /// </summary>
        /// <remarks>
        /// Pour la barre supérieure, qui dispose de la place qui reste une fois les boutons
        /// posés. Sur un écran de portable à 125 %, le nom complet s'y abrégeait en
        /// « Windows… » : une ellipse qui n'apprend rien vaut moins qu'une information plus
        /// courte mais entière. Le numéro de compilation et l'architecture restent affichés
        /// partout où la place ne manque pas : l'infobulle de cette même barre, la fiche
        /// Windows, et les deux rapports.
        /// </remarks>
        public string ShortName
        {
            get
            {
                var name = FamilyLabel(Family);
                var edition = EditionLabel;
                if (!string.IsNullOrEmpty(edition)) name += " " + edition;
                if (!string.IsNullOrEmpty(DisplayVersion)) name += " " + DisplayVersion;
                if (Family == WindowsFamily.Windows7 && ServicePackMajor > 0) name += " SP" + ServicePackMajor;
                return name;
            }
        }

        public static string FamilyLabel(WindowsFamily family) => family switch
        {
            WindowsFamily.Windows7 => "Windows 7",
            WindowsFamily.Windows8 => "Windows 8",
            WindowsFamily.Windows81 => "Windows 8.1",
            WindowsFamily.Windows10 => "Windows 10",
            WindowsFamily.Windows11 => "Windows 11",
            WindowsFamily.Unsupported => "Version de Windows non prise en charge",
            _ => "Windows (version indéterminée)",
        };

        public static string ArchitectureLabel(ProcessorArchitecture architecture) => architecture switch
        {
            ProcessorArchitecture.X86 => "x86",
            ProcessorArchitecture.X64 => "x64",
            ProcessorArchitecture.Arm => "ARM",
            ProcessorArchitecture.Arm64 => "ARM64",
            _ => "architecture inconnue",
        };

        public override string ToString() => DisplayName;
    }
}
