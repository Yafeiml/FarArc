using Stylet;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Media;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using FarArc.Utils;
using Shawn.Utils.Wpf;
using FarArc.Service;
using Shawn.Utils.Wpf.Controls;

namespace FarArc.View.Utils
{
    /// <summary>
    /// Default implementation of IMessageBoxViewModel, and is therefore the ViewModel shown by default by ShowMessageBox
    /// </summary>
    public class BreakingChangeUpdateViewModel : NotifyPropertyChangedBaseScreen
    {
        public AboutPageViewModel AboutPageViewModel => IoC.Get<AboutPageViewModel>();


        private RelayCommand? _cmdUpdate;
        public RelayCommand CmdUpdate
        {
            get
            {
                return _cmdUpdate ??= new RelayCommand((o) =>
                {
                    HyperlinkHelper.OpenUriBySystem(AboutPageViewModel.NewVersionUrl);
                });
            }
        }
        private RelayCommand? _cmdClose;
        public RelayCommand CmdClose
        {
            get
            {
                return _cmdClose ??= new RelayCommand((o) =>
                {
                    IoC.Get<ConfigurationService>().Engagement.BreakingChangeAlertVersionString = AboutPageViewModel.NewVersion;
                    IoC.Get<ConfigurationService>().Save();
                    this.RequestClose(true);
                });
            }
        }
    }
}
