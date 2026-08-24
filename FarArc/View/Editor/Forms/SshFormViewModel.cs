using FarArc.Model.Protocol;

namespace FarArc.View.Editor.Forms
{
    public class SshFormViewModel : ProtocolBaseWithAddressPortUserPwdFormViewModel
    {
        public new SSH New { get; }
        public SshFormViewModel(SSH protocolBase) : base(protocolBase)
        {
            New = protocolBase;
        }
    }
}
