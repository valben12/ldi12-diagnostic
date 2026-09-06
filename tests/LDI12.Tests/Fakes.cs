using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Diagnostics;
using LDI12.Core.Execution;
using LDI12.Core.Logging;
using LDI12.Core.Platform;
using LDI12.Core.Probes;
using Microsoft.Win32;

namespace LDI12.Tests
{
    internal sealed class FakeFeatureRegistry : IFeatureRegistry
    {
        private readonly Dictionary<FeatureId, FeatureState> _states = new Dictionary<FeatureId, FeatureState>();

        public FakeFeatureRegistry Set(FeatureId id, Availability availability, string reason = "Test.")
        {
            _states[id] = new FeatureState
            {
                Id = id,
                DisplayName = id.ToString(),
                Availability = availability,
                Reason = reason,
            };
            return this;
        }

        public IReadOnlyList<FeatureState> All => new List<FeatureState>(_states.Values);

        public FeatureState Get(FeatureId id) => _states.TryGetValue(id, out var state)
            ? state
            : new FeatureState { Id = id, Availability = Availability.Available, Reason = "Disponible par défaut." };

        public bool IsAvailable(FeatureId id) => Get(id).Availability == Availability.Available;
    }

    internal sealed class FakePlatformInfo : IPlatformInfo
    {
        public FakePlatformInfo(
            int build = 22631, bool elevated = false, IFeatureRegistry? features = null, string? editionId = null)
        {
            Profile = new WindowsProfile
            {
                Family = build >= 22000 ? WindowsFamily.Windows11 : WindowsFamily.Windows10,
                Version = new Version(10, 0, build),
                Build = build,
                EditionId = editionId,
                Level = CompatibilityLevel.Full,
                NativeArchitecture = ProcessorArchitecture.X64,
            };
            Features = features ?? new FakeFeatureRegistry();
            Elevation = elevated ? ElevationState.Elevated : ElevationState.NotElevated;
        }

        public WindowsProfile Profile { get; }
        public IFeatureRegistry Features { get; }
        public ElevationState Elevation { get; }
        public bool IsElevated => Elevation == ElevationState.Elevated || Elevation == ElevationState.ElevatedByDefault;
    }

    internal sealed class FakeWmiGateway : IWmiGateway
    {
        private readonly Dictionary<string, WmiNamespaceState> _namespaces =
            new Dictionary<string, WmiNamespaceState>(StringComparer.OrdinalIgnoreCase);

        public FakeWmiGateway Set(string @namespace, WmiNamespaceState state)
        {
            _namespaces[@namespace] = state;
            return this;
        }

        public Task<WmiQueryResult> QueryAsync(string @namespace, string query, TimeSpan timeout, CancellationToken cancellationToken)
            => Task.FromResult(WmiQueryResult.Ok(Array.Empty<WmiRecord>(), TimeSpan.Zero));

        public Task<WmiNamespaceState> ProbeNamespaceAsync(string @namespace, CancellationToken cancellationToken)
            => Task.FromResult(_namespaces.TryGetValue(@namespace, out var state) ? state : WmiNamespaceState.Present);

        public async Task<bool> NamespaceExistsAsync(string @namespace, CancellationToken cancellationToken)
            => await ProbeNamespaceAsync(@namespace, cancellationToken) == WmiNamespaceState.Present;

        public Task<bool> ClassExistsAsync(string @namespace, string className, CancellationToken cancellationToken)
            => Task.FromResult(true);
    }

    internal sealed class FakeProcessRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new ProcessResult { FileName = request.FileName, Arguments = request.Arguments });
    }

    internal sealed class FakeRegistryGateway : IRegistryGateway
    {
        public string? ReadString(RegistryHive hive, string subKey, string valueName, RegistryView view = RegistryView.Default) => null;
        public int? ReadInt32(RegistryHive hive, string subKey, string valueName, RegistryView view = RegistryView.Default) => null;
        public long? ReadInt64(RegistryHive hive, string subKey, string valueName, RegistryView view = RegistryView.Default) => null;
        public string[]? ReadMultiString(RegistryHive hive, string subKey, string valueName, RegistryView view = RegistryView.Default) => null;
        public byte[]? ReadBinary(RegistryHive hive, string subKey, string valueName, RegistryView view = RegistryView.Default) => null;
        public bool KeyExists(RegistryHive hive, string subKey, RegistryView view = RegistryView.Default) => false;
        public IReadOnlyList<string> GetSubKeyNames(RegistryHive hive, string subKey, RegistryView view = RegistryView.Default) => Array.Empty<string>();
        public IReadOnlyList<string> GetValueNames(RegistryHive hive, string subKey, RegistryView view = RegistryView.Default) => Array.Empty<string>();
    }

    internal static class Fakes
    {
        public static ProbeContext Context(IPlatformInfo? platform = null, IWmiGateway? wmi = null)
            => new ProbeContext(
                platform ?? new FakePlatformInfo(),
                new FakeProcessRunner(),
                wmi ?? new FakeWmiGateway(),
                new FakeRegistryGateway(),
                NullLogger.Instance);
    }

    /// <summary>Sondes de test représentant les comportements que la politique doit absorber.</summary>
    internal sealed class DelegateProbe : IDiagnosticProbe
    {
        private readonly Func<ProbeContext, CancellationToken, Task<ProbeOutcome>> _body;

        public DelegateProbe(ProbeDescriptor descriptor, Func<ProbeContext, CancellationToken, Task<ProbeOutcome>> body)
        {
            Descriptor = descriptor;
            _body = body;
        }

        public ProbeDescriptor Descriptor { get; }

        public Task<ProbeOutcome> ExecuteAsync(ProbeContext context, CancellationToken cancellationToken)
            => _body(context, cancellationToken);

        public static ProbeDescriptor Descriptor_(
            string id = "TEST-001",
            ProbeRequirements? requirements = null,
            TimeSpan? hardTimeout = null,
            string[]? dependsOn = null,
            bool fullScanOnly = false)
            => new ProbeDescriptor
            {
                Id = id,
                DisplayName = "Sonde de test",
                Category = DiagnosticCategory.Windows,
                Requirements = requirements ?? ProbeRequirements.None,
                HardTimeout = hardTimeout ?? TimeSpan.FromSeconds(5),
                DependsOn = dependsOn ?? Array.Empty<string>(),
                FullScanOnly = fullScanOnly,
            };
    }
}
