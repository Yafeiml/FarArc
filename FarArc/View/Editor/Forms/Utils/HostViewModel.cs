using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using FarArc.Model.Protocol.Base;
using FarArc.Service;
using FarArc.Service.DataSource;
using FarArc.Utils;
using Shawn.Utils.Wpf.FileSystem;

namespace FarArc.View.Editor.Forms.Utils;

public class HostViewModel : NotifyPropertyChangedBaseScreen
{
    public ProtocolBaseWithAddressPort New { get; }
    public HostViewModel(ProtocolBaseWithAddressPort protocol)
    {
        New = protocol;
    }
}