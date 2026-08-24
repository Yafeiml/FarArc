using System;
using System.Windows;
using System.Windows.Controls;
using FarArc.Controls.NoteDisplay;
using FarArc.Model;
using FarArc.View;
using FarArc.View.ServerView;
using Shawn.Utils;

namespace FarArc.Controls
{
    public partial class ServerCardItem : UserControl
    {
        public static readonly DependencyProperty ProtocolServerViewModelProperty =
            DependencyProperty.Register("ProtocolBaseViewModel", typeof(ProtocolBaseViewModel), typeof(ServerCardItem),
                new PropertyMetadata(null, new PropertyChangedCallback(OnServerDataChanged)));

        private static void OnServerDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var value = (ProtocolBaseViewModel)e.NewValue;
            ((ServerCardItem)d).DataContext = value;
            if (value?.HoverNoteDisplayControl != null)
            {
                value.HoverNoteDisplayControl.IsBriefNoteShown = false;
            }
        }

        public ProtocolBaseViewModel? ProtocolBaseViewModel
        {
            get => GetValue(ProtocolServerViewModelProperty) as ProtocolBaseViewModel;
            set => SetValue(ProtocolServerViewModelProperty, value);
        }

        public ServerCardItem()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            PopupCardSettingMenu.Closed += PopupCardSettingMenuOnClosed;
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            PopupCardSettingMenu.Closed -= PopupCardSettingMenuOnClosed;
        }

        private void PopupCardSettingMenuOnClosed(object? sender, EventArgs e)
        {
            ProtocolBaseViewModel?.ClearActions();
        }

        private void BtnSettingMenu_OnClick(object sender, RoutedEventArgs e)
        {
            ProtocolBaseViewModel?.BuildActions();
            PopupCardSettingMenu.IsOpen = true;
        }

        private void ServerMenuButton_OnClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button { CommandParameter: ProtocolAction afs })
            {
                afs.Run();
            }
            PopupCardSettingMenu.IsOpen = false;
        }

        private void ItemsCheckBox_OnClick(object sender, RoutedEventArgs e)
        {
            ServerListPageView.ItemsCheckBox_OnClick_Static(sender, e);
        }
    }
}