using System.Windows.Controls;

namespace LDI12.App.Views
{
    public partial class DiskUsageView : UserControl
    {
        public DiskUsageView() => InitializeComponent();

        private void OnZoomIn(object sender, System.Windows.RoutedEventArgs e) => Map.ZoomIn();

        private void OnZoomOut(object sender, System.Windows.RoutedEventArgs e) => Map.ZoomOut();

        private void OnZoomReset(object sender, System.Windows.RoutedEventArgs e) => Map.ResetZoom();

        /// <summary>Une sélection venue de la carte déplie l'arborescence : la ligne doit aussi venir à l'écran.</summary>
        private void OnFolderSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FolderList.SelectedItem != null) FolderList.ScrollIntoView(FolderList.SelectedItem);
        }
    }
}
