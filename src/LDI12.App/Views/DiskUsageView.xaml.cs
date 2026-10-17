using System.Windows.Controls;

namespace LDI12.App.Views
{
    public partial class DiskUsageView : UserControl
    {
        public DiskUsageView() => InitializeComponent();

        /// <summary>Une sélection venue de la carte déplie l'arborescence : la ligne doit aussi venir à l'écran.</summary>
        private void OnFolderSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FolderList.SelectedItem != null) FolderList.ScrollIntoView(FolderList.SelectedItem);
        }
    }
}
