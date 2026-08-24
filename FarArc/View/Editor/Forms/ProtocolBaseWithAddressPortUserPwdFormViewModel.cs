using FarArc.Model.Protocol.Base;
using FarArc.View.Editor.Forms.AlternativeCredential;
using FarArc.View.Editor.Forms.Utils;

namespace FarArc.View.Editor.Forms
{
    public class ProtocolBaseWithAddressPortUserPwdFormViewModel : ProtocolBaseWithAddressPortFormViewModel
    {
        public new ProtocolBaseWithAddressPortUserPwd New { get; }
        public CredentialViewModel CredentialViewModel { get; }


        public ProtocolBaseWithAddressPortUserPwdFormViewModel(ProtocolBaseWithAddressPortUserPwd protocol) : base(protocol)
        {
            New = protocol;
            CredentialViewModel = new CredentialViewModel(protocol);
        }
    }
}
