using System;
using System.Windows;
using FarArc.Model.Protocol.FileTransmit;
using FarArc.Service;

namespace FarArc.View.Settings.ProtocolConfig
{
    public partial class ExternalSshRunnerSettings : ExternalRunnerSettingsBase
    {
        public ExternalSshRunnerSettings()
        {
            InitializeComponent();
            base.InitBindableAvalonEditor(TextEditor);
            base.InitBindableAvalonEditor(TextEditorForSshWithPrivateKey);
        }
    }
}
