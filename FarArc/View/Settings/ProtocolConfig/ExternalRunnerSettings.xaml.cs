using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ICSharpCode.AvalonEdit.CodeCompletion;
using FarArc.Model.Protocol.FileTransmit;
using FarArc.Service;

namespace FarArc.View.Settings.ProtocolConfig
{
    public partial class ExternalRunnerSettings : ExternalRunnerSettingsBase
    {
        public ExternalRunnerSettings()
        {
            InitializeComponent();
            base.InitBindableAvalonEditor(TextEditor);
        }
    }
}
