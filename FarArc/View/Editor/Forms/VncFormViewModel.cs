using FarArc.Model.Protocol;

namespace FarArc.View.Editor.Forms
{
    public class VncFormViewModel : ProtocolBaseWithAddressPortUserPwdFormViewModel
    {
        public new VNC New { get; }
        public VncFormViewModel(VNC protocolBase) : base(protocolBase)
        {
            New = protocolBase;
        }
    }
}
