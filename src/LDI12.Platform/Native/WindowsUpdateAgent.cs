using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using LDI12.Core.Execution;

namespace LDI12.Platform.Native
{
    /// <summary>
    /// Les pilotes de Windows Update, par l'agent Windows Update (COM, wuapi.dll).
    /// </summary>
    /// <remarks>
    /// <b>L'interface que Windows Update lui-même publie</b>, présente sur tout Windows depuis XP :
    /// pas de module PowerShell (PSWindowsUpdate n'est installé nulle part par défaut), pas
    /// d'outil à télécharger. Liaison tardive, sans assembly d'interopérabilité à embarquer.
    /// <para>
    /// Chaque appel tourne sur son propre fil STA, avec un délai : l'agent peut rester bloqué
    /// des minutes sur un poste dont le service est malade, et le logiciel ne doit pas l'être
    /// avec lui. Passé le délai, l'appel est abandonné et le dit.
    /// </para>
    /// <para>
    /// Un poste rattaché à un serveur WSUS, ou dont la stratégie exclut les pilotes de Windows
    /// Update, répond par une liste vide : c'est dit comme tel, sans conclure qu'il n'y a rien à
    /// mettre à jour.
    /// </para>
    /// </remarks>
    internal static class WindowsUpdateAgent
    {
        private const string Criteria = "IsInstalled=0 and Type='Driver' and IsHidden=0";

        internal static Task<DriverUpdateSearch> SearchAsync(CancellationToken cancellationToken)
            => RunAsync(() =>
            {
                var session = CreateSession();
                var searcher = Invoke(session, "CreateUpdateSearcher");
                var result = Invoke(searcher, "Search", Criteria);
                var updates = Get(result, "Updates");

                var list = new List<DriverUpdateInfo>();
                var count = Convert.ToInt32(Get(updates, "Count"));
                for (var index = 0; index < count; index++)
                {
                    var update = Get(updates, "Item", index);
                    list.Add(new DriverUpdateInfo
                    {
                        UpdateId = Convert.ToString(Get(Get(update, "Identity"), "UpdateID")) ?? string.Empty,
                        Title = Convert.ToString(Get(update, "Title")) ?? string.Empty,
                        DriverClass = Text(update, "DriverClass"),
                        Manufacturer = Text(update, "DriverManufacturer"),
                        Model = Text(update, "DriverModel"),
                        DriverDate = Date(update, "DriverVerDate"),
                        DownloadBytes = Size(update),
                    });
                }

                return new DriverUpdateSearch { Succeeded = true, Updates = list };
            }, TimeSpan.FromMinutes(6), error => new DriverUpdateSearch { Error = error }, cancellationToken);

        internal static Task<ControlResult> InstallAsync(
            IReadOnlyCollection<string> updateIds, IProgress<string>? progress, CancellationToken cancellationToken)
            => RunAsync(() =>
            {
                var wanted = new HashSet<string>(updateIds, StringComparer.OrdinalIgnoreCase);
                if (wanted.Count == 0) return ControlResult.Ok("Aucun pilote à installer.");

                progress?.Report("Windows Update : recherche des pilotes…");
                var session = CreateSession();
                var result = Invoke(Invoke(session, "CreateUpdateSearcher"), "Search", Criteria);
                var updates = Get(result, "Updates");

                var collection = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.UpdateColl", true)!)!;
                var titles = new List<string>();
                var count = Convert.ToInt32(Get(updates, "Count"));
                for (var index = 0; index < count; index++)
                {
                    var update = Get(updates, "Item", index);
                    var id = Convert.ToString(Get(Get(update, "Identity"), "UpdateID")) ?? string.Empty;
                    if (!wanted.Contains(id)) continue;
                    if (!Convert.ToBoolean(Get(update, "EulaAccepted"))) Invoke(update, "AcceptEula");
                    Invoke(collection, "Add", update);
                    titles.Add(Convert.ToString(Get(update, "Title")) ?? id);
                }

                if (titles.Count == 0)
                    return ControlResult.Fail("Windows Update ne propose plus ces pilotes : ils ont pu être installés entre-temps.");

                progress?.Report("Windows Update : téléchargement de " + titles.Count + " pilote(s)…");
                var downloader = Invoke(session, "CreateUpdateDownloader");
                Set(downloader, "Updates", collection);
                Invoke(downloader, "Download");

                progress?.Report("Windows Update : installation…");
                var installer = Invoke(session, "CreateUpdateInstaller");
                Set(installer, "Updates", collection);
                var installation = Invoke(installer, "Install");

                // OperationResultCode : 2 réussi, 3 réussi avec erreurs, 4 échec, 5 abandonné.
                var code = Convert.ToInt32(Get(installation, "ResultCode"));
                var reboot = Convert.ToBoolean(Get(installation, "RebootRequired"));
                return code switch
                {
                    2 => ControlResult.Ok(titles.Count + " pilote(s) installé(s) : " + string.Join(" ; ", titles), reboot),
                    3 => ControlResult.Ok("Installation partielle : certains pilotes n'ont pas pu être installés.", reboot),
                    5 => ControlResult.Fail("Installation interrompue par Windows."),
                    _ => ControlResult.Fail("Windows Update n'a pas pu installer ces pilotes (résultat " + code + ")."),
                };
            }, TimeSpan.FromMinutes(45), error => ControlResult.Fail(error), cancellationToken);

        private static object CreateSession()
        {
            var type = Type.GetTypeFromProgID("Microsoft.Update.Session", false)
                       ?? throw new COMException("L'agent Windows Update n'est pas enregistré sur ce PC.");
            var session = Activator.CreateInstance(type)!;
            try { Set(session, "ClientApplicationID", "LDI12 Diagnostic"); }
            catch (TargetInvocationException) { }
            return session;
        }

        /// <summary>Sur un fil STA dédié, avec délai : un agent bloqué n'immobilise pas le logiciel.</summary>
        private static Task<T> RunAsync<T>(Func<T> work, TimeSpan timeout, Func<string, T> failed, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<T>();
            var thread = new Thread(() =>
            {
                // Tout est rattrapé : une exception sortie d'un fil d'arrière-plan arrêterait le logiciel.
                try { completion.TrySetResult(work()); }
                catch (Exception ex) { completion.TrySetResult(failed(Describe(Unwrap(ex)))); }
            })
            { IsBackground = true, Name = "LDI12 Windows Update" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            return WaitAsync(completion.Task, timeout, failed, cancellationToken);
        }

        private static async Task<T> WaitAsync<T>(Task<T> work, TimeSpan timeout, Func<string, T> failed, CancellationToken cancellationToken)
        {
            var finished = await Task.WhenAny(work, Task.Delay(timeout, cancellationToken)).ConfigureAwait(false);
            if (finished == work) return await work.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return failed("Windows Update ne répond pas (plus de " + (int)timeout.TotalMinutes + " minutes) : le service est peut-être bloqué.");
        }

        private static Exception Unwrap(Exception ex) => ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;

        private static string Describe(Exception ex)
        {
            if (ex is UnauthorizedAccessException) return "Windows Update demande les privilèges administrateur.";
            if (!(ex is COMException com)) return "Windows Update a répondu de façon inattendue : " + ex.Message;
            return unchecked((uint)com.HResult) switch
            {
                0x8024402C or 0x80072EE7 or 0x80072EFD => "Windows Update est injoignable : vérifiez la connexion à Internet.",
                0x8024001E => "Le service Windows Update s'est arrêté pendant l'opération.",
                0x80240438 or 0x8024500C => "Une stratégie bloque l'accès à Windows Update sur ce PC.",
                0x80070422 => "Le service Windows Update est désactivé.",
                _ => "Windows Update a renvoyé l'erreur 0x" + com.HResult.ToString("X8") + ".",
            };
        }

        private static object Invoke(object target, string method, params object[] arguments)
            => target.GetType().InvokeMember(method, BindingFlags.InvokeMethod, null, target, arguments)!;

        private static object Get(object target, string property, params object[] arguments)
            => target.GetType().InvokeMember(property, BindingFlags.GetProperty, null, target, arguments)!;

        private static void Set(object target, string property, object value)
            => target.GetType().InvokeMember(property, BindingFlags.SetProperty, null, target, new[] { value });

        private static string? Text(object update, string property)
        {
            try { return Convert.ToString(Get(update, property)) is { Length: > 0 } text ? text : null; }
            catch (Exception ex) when (Unwrap(ex) is COMException || Unwrap(ex) is MissingMemberException) { return null; }
        }

        private static DateTime? Date(object update, string property)
        {
            try { return Get(update, property) is DateTime date ? date : (DateTime?)null; }
            catch (Exception ex) when (Unwrap(ex) is COMException || Unwrap(ex) is MissingMemberException) { return null; }
        }

        private static long Size(object update)
        {
            try { return Convert.ToInt64(Get(update, "MaxDownloadSize")); }
            catch (Exception ex) when (Unwrap(ex) is COMException || Unwrap(ex) is MissingMemberException || ex is OverflowException) { return 0; }
        }
    }
}
