using System;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LDI12.App.ViewModels;

namespace LDI12.App.Views
{
    public partial class LanView : UserControl
    {
        private LanViewModel? _model;
        private bool _scrollPending;

        public LanView()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (_model != null) _model.Packets.CollectionChanged -= OnPacketsChanged;
            _model = DataContext as LanViewModel;
            if (_model != null) _model.Packets.CollectionChanged += OnPacketsChanged;
        }

        /// <summary>
        /// La liste suit les derniers paquets quand « Suivre » est coché. Un lot ajoute des centaines
        /// de lignes d'un coup : un seul défilement par lot, une fois la liste à jour.
        /// </summary>
        private void OnPacketsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action != NotifyCollectionChangedAction.Add || _model == null || !_model.FollowPackets || _scrollPending) return;
            _scrollPending = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                _scrollPending = false;
                if (PacketList.Items.Count > 0) PacketList.ScrollIntoView(PacketList.Items[PacketList.Items.Count - 1]);
            }));
        }
    }
}
