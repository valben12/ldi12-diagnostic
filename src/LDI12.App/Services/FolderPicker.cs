using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace LDI12.App.Services
{
    /// <summary>
    /// La fenêtre « Sélectionner un dossier » de l'Explorateur.
    /// </summary>
    /// <remarks>
    /// WPF n'en fournit pas, et celle de Windows Forms est l'ancienne arborescence, sans barre
    /// d'adresse ni favoris. Celle-ci est la fenêtre d'ouverture de l'Explorateur, en mode dossiers :
    /// celle que le technicien connaît, avec la saisie d'un chemin et la sélection de plusieurs
    /// dossiers d'un coup. Disponible depuis Windows Vista.
    /// </remarks>
    public static class FolderPicker
    {
        /// <summary>Les dossiers choisis, ou une liste vide si la fenêtre a été fermée.</summary>
        public static IReadOnlyList<string> Pick(IntPtr owner, string title)
        {
            var result = new List<string>();
            IFileOpenDialog? dialog = null;

            try
            {
                dialog = (IFileOpenDialog)new FileOpenDialog();
                dialog.GetOptions(out var options);
                dialog.SetOptions(options | PickFolders | ForceFileSystem | AllowMultiSelect | PathMustExist);
                dialog.SetTitle(title);

                if (dialog.Show(owner) != 0) return result;

                dialog.GetResults(out var items);
                items.GetCount(out var count);
                for (uint index = 0; index < count; index++)
                {
                    items.GetItemAt(index, out var item);
                    item.GetDisplayName(FileSystemPath, out var path);
                    if (!string.IsNullOrWhiteSpace(path)) result.Add(path);
                    Marshal.ReleaseComObject(item);
                }

                Marshal.ReleaseComObject(items);
            }
            catch (COMException)
            {
                // Fenêtre refusée par la machine (session très restreinte) : rien de choisi, le
                // champ de saisie du chemin reste là.
            }
            finally
            {
                if (dialog != null) Marshal.ReleaseComObject(dialog);
            }

            return result;
        }

        private const uint PickFolders = 0x00000020;
        private const uint ForceFileSystem = 0x00000040;
        private const uint AllowMultiSelect = 0x00000200;
        private const uint PathMustExist = 0x00000800;
        private const uint FileSystemPath = 0x80058000;

        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        private class FileOpenDialog
        {
        }

        [ComImport, Guid("d57c7288-d4ad-4768-be02-9d969532d960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOpenDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint count, IntPtr types);
            void SetFileTypeIndex(uint index);
            void GetFileTypeIndex(out uint index);
            void Advise(IntPtr events, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(uint options);
            void GetOptions(out uint options);
            void SetDefaultFolder(IShellItem item);
            void SetFolder(IShellItem item);
            void GetFolder(out IShellItem item);
            void GetCurrentSelection(out IShellItem item);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void GetResult(out IShellItem item);
            void AddPlace(IShellItem item, int placement);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
            void Close(int result);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr filter);
            void GetResults(out IShellItemArray items);
            void GetSelectedItems(out IShellItemArray items);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr context, ref Guid handler, ref Guid interfaceId, out IntPtr result);
            void GetParent(out IShellItem parent);
            void GetDisplayName(uint type, [MarshalAs(UnmanagedType.LPWStr)] out string name);
            void GetAttributes(uint mask, out uint attributes);
            void Compare(IShellItem other, uint hint, out int order);
        }

        [ComImport, Guid("B63EA76D-1F85-456F-A19C-48159EFA858B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemArray
        {
            void BindToHandler(IntPtr context, ref Guid handler, ref Guid interfaceId, out IntPtr result);
            void GetPropertyStore(int flags, ref Guid interfaceId, out IntPtr store);
            void GetPropertyDescriptionList(IntPtr key, ref Guid interfaceId, out IntPtr list);
            void GetAttributes(int flags, uint mask, out uint attributes);
            void GetCount(out uint count);
            void GetItemAt(uint index, out IShellItem item);
            void EnumItems(out IntPtr items);
        }
    }
}
