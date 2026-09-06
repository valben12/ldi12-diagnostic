using System;
using System.Globalization;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Execution;
using LDI12.Core.Logging;
using LDI12.Core.Platform;

namespace LDI12.Platform.Gateways
{
    /// <summary>
    /// Point de restauration système, par la classe WMI <c>SystemRestore</c> de
    /// <c>root\default</c>.
    /// </summary>
    /// <remarks>
    /// Trois réponses de Windows méritent d'être distinguées, parce qu'elles n'ont pas du tout
    /// le même sens pour le technicien : le point a été créé ; la protection du système est
    /// désactivée, donc il n'y a aucun filet ; un point récent existe déjà et Windows refuse
    /// d'en créer un second dans les 24 heures : le filet est là quand même. Les confondre sous
    /// un « échec » ferait renoncer à une intervention parfaitement couverte.
    /// </remarks>
    public sealed class SystemRestoreGateway : ISystemRestoreGateway
    {
        private const string Category = "Platform.Restore";
        private const string Namespace = @"root\default";

        /// <summary>Type de modification : APPLICATION_INSTALL, le plus large des points applicatifs.</summary>
        private const uint RestorePointTypeApplicationInstall = 0;

        /// <summary>BEGIN_SYSTEM_CHANGE : le point couvre ce qui suit sa création.</summary>
        private const uint EventTypeBeginSystemChange = 100;

        private readonly IScopedLogger _log;
        private readonly IPlatformInfo _platform;

        public SystemRestoreGateway(IPlatformInfo platform, ILdiLogger logger)
        {
            _platform = platform ?? throw new ArgumentNullException(nameof(platform));
            _log = (logger ?? throw new ArgumentNullException(nameof(logger))).For(Category);
        }

        public Task<RestorePointResult> CreateAsync(string description, CancellationToken cancellationToken)
        {
            if (!_platform.IsElevated)
                return Task.FromResult(new RestorePointResult
                {
                    State = RestorePointState.RequiresElevation,
                    Message = "Un point de restauration ne peut être créé qu'avec des privilèges administrateur.",
                });

            if (_platform.Profile.IsServer)
                return Task.FromResult(new RestorePointResult
                {
                    State = RestorePointState.Unsupported,
                    Message = "La restauration du système n'existe pas sur les éditions serveur de Windows.",
                });

            // L'appel est synchrone et peut durer une minute : il part sur le pool de threads
            // plutôt que de figer l'écran qui l'a demandé.
            return Task.Run(() => Create(description), cancellationToken);
        }

        private RestorePointResult Create(string description)
        {
            try
            {
                using var management = new ManagementClass(new ManagementPath(Namespace + ":SystemRestore"));
                using var parameters = management.GetMethodParameters("CreateRestorePoint");

                parameters["Description"] = Trim(description);
                parameters["RestorePointType"] = RestorePointTypeApplicationInstall;
                parameters["EventType"] = EventTypeBeginSystemChange;

                using var result = management.InvokeMethod("CreateRestorePoint", parameters, null);
                var code = Convert.ToUInt32(result?["ReturnValue"] ?? 0u, CultureInfo.InvariantCulture);

                return Interpret(code);
            }
            catch (ManagementException ex)
            {
                _log.Warn("La création du point de restauration a échoué.", ex);
                return new RestorePointResult
                {
                    State = RestorePointState.Failed,
                    Message = "Windows a refusé la création du point de restauration : " + ex.Message,
                };
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is System.Runtime.InteropServices.COMException)
            {
                _log.Warn("La création du point de restauration a été refusée.", ex);
                return new RestorePointResult
                {
                    State = RestorePointState.Failed,
                    Message = "La création du point de restauration a été refusée par le système.",
                };
            }
        }

        internal static RestorePointResult Interpret(uint code) => code switch
        {
            0 => new RestorePointResult
            {
                State = RestorePointState.Created,
                Message = "Point de restauration créé. La machine peut revenir à cet état.",
            },

            // ERROR_SERVICE_DISABLED : la protection du système est coupée sur le volume Windows.
            1058 => new RestorePointResult
            {
                State = RestorePointState.Disabled,
                Message = "La protection du système est désactivée : aucun point de restauration ne peut " +
                          "être créé. Elle s'active dans les propriétés système, onglet Protection du système.",
            },

            // Windows refuse un second point dans les 24 heures : un point récent existe déjà.
            1359 => new RestorePointResult
            {
                State = RestorePointState.Throttled,
                Message = "Windows a refusé un second point dans les 24 heures : un point récent existe " +
                          "déjà et protège tout autant.",
            },

            _ => new RestorePointResult
            {
                State = RestorePointState.Failed,
                Message = "La création du point de restauration a échoué (code " +
                          code.ToString(CultureInfo.InvariantCulture) + ").",
            },
        };

        /// <summary>La description d'un point de restauration est limitée à 256 caractères.</summary>
        private static string Trim(string description)
        {
            var text = string.IsNullOrWhiteSpace(description) ? "LDI12 Diagnostic" : description.Trim();
            return text.Length <= 256 ? text : text.Substring(0, 256);
        }
    }
}
