using Microsoft.VisualBasic.FileIO;

namespace LDI12.App.Services
{
    /// <summary>
    /// La corbeille de Windows : la seule voie de suppression de l'analyse du disque.
    /// </summary>
    /// <remarks>
    /// Passe par l'opération de fichiers de l'Explorateur, celle d'un « Supprimer » au clavier :
    /// même corbeille, même restauration, même fenêtre si un fichier est ouvert ailleurs. Le
    /// technicien qui s'est trompé de dossier rattrape son geste, le client aussi.
    /// </remarks>
    public static class RecycleBin
    {
        /// <exception cref="System.OperationCanceledException">Le technicien a annulé dans la fenêtre de Windows.</exception>
        public static void Send(string path, bool isDirectory)
        {
            if (isDirectory)
                FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
            else
                FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
        }
    }
}
