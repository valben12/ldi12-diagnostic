using System;
using System.Collections.Generic;
using System.IO;

namespace LDI12.Core.DiskUsage
{
    /// <summary>Une entrée de dossier, telle que la lecture du disque la rend.</summary>
    public struct DiskEntry
    {
        public string Name;

        public bool IsDirectory;

        /// <summary>Jonction, lien symbolique : jamais suivi, sous peine de compter deux fois ou de tourner en rond.</summary>
        public bool IsReparsePoint;

        /// <summary>Fichier en ligne seulement (OneDrive) : il a une taille, mais n'occupe rien sur le disque.</summary>
        public bool IsCloudOnly;

        public long Size;

        public long LastWriteUtcTicks;
    }

    /// <summary>Lit le contenu immédiat d'un dossier.</summary>
    public interface IDirectoryLister
    {
        /// <summary>Ajoute les entrées de <paramref name="path"/> à <paramref name="into"/> ; faux si le dossier est illisible.</summary>
        bool TryList(string path, List<DiskEntry> into);
    }

    /// <summary>
    /// Lecture par le framework : portable, plus lente que la lecture native de la couche
    /// plateforme. Sert aux tests, et de secours.
    /// </summary>
    public sealed class ManagedDirectoryLister : IDirectoryLister
    {
        /// <summary>FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS : contenu resté dans le nuage.</summary>
        private const int RecallOnDataAccess = 0x400000;

        public static readonly ManagedDirectoryLister Instance = new ManagedDirectoryLister();

        public bool TryList(string path, List<DiskEntry> into)
        {
            try
            {
                foreach (var info in new DirectoryInfo(path).EnumerateFileSystemInfos())
                {
                    var attributes = info.Attributes;
                    var isDirectory = (attributes & FileAttributes.Directory) != 0;
                    into.Add(new DiskEntry
                    {
                        Name = info.Name,
                        IsDirectory = isDirectory,
                        IsReparsePoint = (attributes & FileAttributes.ReparsePoint) != 0,
                        IsCloudOnly = ((int)attributes & RecallOnDataAccess) != 0,
                        Size = isDirectory ? 0 : ((FileInfo)info).Length,
                        LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks,
                    });
                }
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                return false;
            }
        }
    }
}
