using FarArc.Model.Protocol;
using FarArc.Model.Protocol.Base;
using Newtonsoft.Json;

namespace FarArc.View.Editor.Forms
{
    public class RdpFormViewModel : ProtocolBaseWithAddressPortUserPwdFormViewModel
    {
        public new RDP New { get; }
        public RdpFormViewModel(RDP protocolBase) : base(protocolBase)
        {
            New = protocolBase;
        }
    }
}
