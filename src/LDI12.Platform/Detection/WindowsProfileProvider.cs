using System;
using System.Globalization;
using System.Runtime.InteropServices;
using LDI12.Core.Execution;
using LDI12.Core.Logging;
using LDI12.Core.Platform;
using LDI12.Platform.Native;
using Microsoft.Win32;

namespace LDI12.Platform.Detection
{
    /// <summary>
    /// Construit le <see cref="WindowsProfile"/> de la machine courante.
    /// </summary>
    /// <remarks>
    /// Deux sources croisées : <c>ntdll!RtlGetVersion</c> pour la version réelle, et le registre
    /// pour ce que l'API ne donne pas (UBR, édition, nom d'affichage, version commerciale).
    /// <see cref="Environment.OSVersion"/> n'est jamais consulté.
    /// </remarks>
    public sealed class WindowsProfileProvider
    {
        private const string Category = "Platform.Detection";

        /// <summary>Windows 7 RTM. .NET 4.6.2 ne s'y installe pas : le SP1 est le plancher absolu.</summary>
        private const int Windows7RtmBuild = 7600;

        /// <summary>Windows 11 : ProductName affiche encore « Windows 10 », seul le build fait foi.</summary>
        private const int Windows11FirstBuild = 22000;

        /// <summary>Windows 10 1809, plancher du niveau de compatibilité complet.</summary>
        private const int Windows10Build1809 = 17763;

        private readonly IRegistryGateway _registry;
        private readonly IScopedLogger _log;

        public WindowsProfileProvider(IRegistryGateway registry, ILdiLogger logger)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _log = (logger ?? throw new ArgumentNullException(nameof(logger))).For(Category);
        }

        public WindowsProfile Detect()
        {
            var version = ReadRealVersion(out var servicePackMajor, out var isWorkstation);
            var build = version.Build;

            // L'UBR n'existe que sur Windows 10 et 11 ; ailleurs il vaut 0, ce qui est correct.
            var ubr = _registry.ReadInt32(RegistryHive.LocalMachine, RegistryPaths.CurrentVersion, "UBR") ?? 0;

            // ReleaseId reste figé à « 2009 » sur Windows 10 21H2 et 22H2 ; DisplayVersion n'existe
            // qu'à partir de 20H2. Il faut donc lire les deux et préférer DisplayVersion.
            var displayVersion = _registry.ReadString(RegistryHive.LocalMachine, RegistryPaths.CurrentVersion, "DisplayVersion");
            var releaseId = _registry.ReadString(RegistryHive.LocalMachine, RegistryPaths.CurrentVersion, "ReleaseId");
            var editionId = _registry.ReadString(RegistryHive.LocalMachine, RegistryPaths.CurrentVersion, "EditionID");
            var productName = _registry.ReadString(RegistryHive.LocalMachine, RegistryPaths.CurrentVersion, "ProductName");

            var family = DetermineFamily(version, build, isWorkstation);
            var (nativeArch, processArch, isWow64) = DetectArchitecture();
            var level = DetermineLevel(family, build, servicePackMajor, out var levelReason);

            var profile = new WindowsProfile
            {
                Family = family,
                Version = version,
                Build = build,
                UpdateBuildRevision = ubr,
                DisplayVersion = displayVersion ?? releaseId,
                ReleaseId = releaseId,
                EditionId = editionId,
                ProductName = productName,
                ServicePackMajor = servicePackMajor,
                IsServer = !isWorkstation,
                NativeArchitecture = nativeArch,
                ProcessArchitecture = processArch,
                IsWow64 = isWow64,
                PowerShellMajorVersion = DetectPowerShellMajorVersion(),
                SystemLocale = CultureInfo.InstalledUICulture.Name,
                IsDomainJoined = DetectDomainMembership(),
                Level = level,
                LevelReason = levelReason,
            };

            _log.Info("Plateforme détectée : " + profile.DisplayName + " : compatibilité " + level + ".");
            return profile;
        }

        private Version ReadRealVersion(out int servicePackMajor, out bool isWorkstation)
        {
            var info = new NativeMethods.OSVERSIONINFOEXW
            {
                dwOSVersionInfoSize = (uint)Marshal.SizeOf(typeof(NativeMethods.OSVERSIONINFOEXW)),
                szCSDVersion = string.Empty,
            };

            if (NativeMethods.RtlGetVersion(ref info) == 0)
            {
                servicePackMajor = info.wServicePackMajor;
                isWorkstation = info.wProductType == NativeMethods.VER_NT_WORKSTATION;
                return new Version((int)info.dwMajorVersion, (int)info.dwMinorVersion, (int)info.dwBuildNumber);
            }

            // Repli : le registre. Environment.OSVersion reste écarté : il ment.
            _log.Warn("RtlGetVersion a échoué, repli sur le registre.");
            servicePackMajor = 0;
            isWorkstation = true;

            var buildText = _registry.ReadString(RegistryHive.LocalMachine, RegistryPaths.CurrentVersion, "CurrentBuildNumber")
                         ?? _registry.ReadString(RegistryHive.LocalMachine, RegistryPaths.CurrentVersion, "CurrentBuild");
            var major = _registry.ReadInt32(RegistryHive.LocalMachine, RegistryPaths.CurrentVersion, "CurrentMajorVersionNumber");
            var minor = _registry.ReadInt32(RegistryHive.LocalMachine, RegistryPaths.CurrentVersion, "CurrentMinorVersionNumber");

            int.TryParse(buildText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var build);
            return new Version(major ?? 0, minor ?? 0, build);
        }

        private static WindowsFamily DetermineFamily(Version version, int build, bool isWorkstation)
        {
            if (version.Major == 6)
            {
                return version.Minor switch
                {
                    1 => build <= Windows7RtmBuild ? WindowsFamily.Unsupported : WindowsFamily.Windows7,
                    2 => WindowsFamily.Windows8,
                    3 => WindowsFamily.Windows81,
                    _ => WindowsFamily.Unsupported,
                };
            }

            if (version.Major == 10)
            {
                // Windows 11 ne se distingue QUE par son build : ProductName, CurrentVersion et
                // l'API renvoient tous « 10.0 ». C'est le piège de détection le plus courant.
                return build >= Windows11FirstBuild ? WindowsFamily.Windows11 : WindowsFamily.Windows10;
            }

            return WindowsFamily.Unsupported;
        }

        private static CompatibilityLevel DetermineLevel(
            WindowsFamily family, int build, int servicePackMajor, out string reason)
        {
            switch (family)
            {
                case WindowsFamily.Windows11:
                    reason = "Toutes les fonctions de diagnostic sont disponibles.";
                    return CompatibilityLevel.Full;

                case WindowsFamily.Windows10 when build >= Windows10Build1809:
                    reason = "Toutes les fonctions de diagnostic sont disponibles.";
                    return CompatibilityLevel.Full;

                case WindowsFamily.Windows10:
                    reason = "Windows 10 antérieur à 1809 : certaines métriques GPU et de stockage sont limitées.";
                    return CompatibilityLevel.Reduced;

                case WindowsFamily.Windows81:
                case WindowsFamily.Windows8:
                    reason = "Windows 8.x : SMART NVMe et compteurs GPU indisponibles.";
                    return CompatibilityLevel.Reduced;

                case WindowsFamily.Windows7 when servicePackMajor >= 1:
                    reason = "Windows 7 SP1 : gestion du stockage moderne, SMART NVMe et PowerShell 3 absents. "
                           + "Les modules concernés le signalent individuellement.";
                    return CompatibilityLevel.Minimal;

                case WindowsFamily.Windows7:
                    reason = "Windows 7 sans Service Pack 1 : le Service Pack 1 est requis (.NET 4.6.2 ne s'installe pas sans lui).";
                    return CompatibilityLevel.Unsupported;

                default:
                    reason = "Version de Windows antérieure à Windows 7 SP1 : non prise en charge.";
                    return CompatibilityLevel.Unsupported;
            }
        }

        private (ProcessorArchitecture Native, ProcessorArchitecture Process, bool IsWow64) DetectArchitecture()
        {
            var processArch = IntPtr.Size == 8 ? ProcessorArchitecture.X64 : ProcessorArchitecture.X86;

            // IsWow64Process2 (Windows 10 1511+) est le seul moyen de voir un ARM64 sous les
            // pieds d'un processus x86 ou x64 émulé. Sur les versions antérieures, ARM64 n'existe
            // pas, donc GetNativeSystemInfo suffit et reste exact.
            if (NativeMethods.TryGetWow64Machines(out var processMachine, out var nativeMachine))
            {
                var native = FromImageFileMachine(nativeMachine);
                var current = processMachine == NativeMethods.IMAGE_FILE_MACHINE_UNKNOWN
                    ? native            // pas sous WOW64 : le processus tourne en natif
                    : FromImageFileMachine(processMachine);

                if (native != ProcessorArchitecture.Unknown)
                    return (native, current, processMachine != NativeMethods.IMAGE_FILE_MACHINE_UNKNOWN);
            }

            var info = default(NativeMethods.SYSTEM_INFO);
            NativeMethods.GetNativeSystemInfo(ref info);
            var fallbackNative = info.wProcessorArchitecture switch
            {
                NativeMethods.PROCESSOR_ARCHITECTURE_AMD64 => ProcessorArchitecture.X64,
                NativeMethods.PROCESSOR_ARCHITECTURE_INTEL => ProcessorArchitecture.X86,
                NativeMethods.PROCESSOR_ARCHITECTURE_ARM64 => ProcessorArchitecture.Arm64,
                NativeMethods.PROCESSOR_ARCHITECTURE_ARM => ProcessorArchitecture.Arm,
                _ => ProcessorArchitecture.Unknown,
            };

            var wow64 = false;
            try { NativeMethods.IsWow64Process(NativeMethods.GetCurrentProcess(), out wow64); }
            catch (EntryPointNotFoundException) { /* antérieur à XP SP2 : hors cible */ }

            return (fallbackNative, processArch, wow64);
        }

        private static ProcessorArchitecture FromImageFileMachine(ushort machine) => machine switch
        {
            NativeMethods.IMAGE_FILE_MACHINE_AMD64 => ProcessorArchitecture.X64,
            NativeMethods.IMAGE_FILE_MACHINE_I386 => ProcessorArchitecture.X86,
            NativeMethods.IMAGE_FILE_MACHINE_ARM64 => ProcessorArchitecture.Arm64,
            NativeMethods.IMAGE_FILE_MACHINE_ARMNT => ProcessorArchitecture.Arm,
            _ => ProcessorArchitecture.Unknown,
        };

        private int DetectPowerShellMajorVersion()
        {
            // La clé « 3 » existe dès PowerShell 3.0 et porte la version réelle (3, 4, 5…).
            // Sur un Windows 7 nu, seule la clé « 1 » existe, avec PowerShell 2.0.
            var version = _registry.ReadString(RegistryHive.LocalMachine, RegistryPaths.PowerShellEngine, "PowerShellVersion")
                       ?? _registry.ReadString(RegistryHive.LocalMachine, RegistryPaths.PowerShellEngineV1, "PowerShellVersion");

            if (version == null) return 0;
            var dot = version.IndexOf('.');
            var majorText = dot > 0 ? version.Substring(0, dot) : version;
            return int.TryParse(majorText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var major) ? major : 0;
        }

        private bool DetectDomainMembership()
        {
            var buffer = IntPtr.Zero;
            try
            {
                if (NativeMethods.NetGetJoinInformation(null, out buffer, out var joinType) != 0) return false;
                return joinType == NativeMethods.NetSetupDomainName;
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                _log.Debug("netapi32 indisponible, appartenance au domaine non déterminée.");
                return false;
            }
            finally
            {
                if (buffer != IntPtr.Zero) NativeMethods.NetApiBufferFree(buffer);
            }
        }
    }
}
