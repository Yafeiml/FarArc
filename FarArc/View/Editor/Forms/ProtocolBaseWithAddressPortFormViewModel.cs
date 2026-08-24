using FarArc.Model.Protocol.Base;
using FarArc.View.Editor.Forms.AlternativeCredential;
using FarArc.View.Editor.Forms.Utils;
using static System.Windows.Forms.VisualStyles.VisualStyleElement.StartPanel;

namespace FarArc.View.Editor.Forms
{
    public class ProtocolBaseWithAddressPortFormViewModel : ProtocolBaseFormViewModel
    {
        public new ProtocolBaseWithAddressPort New { get; }
        public HostViewModel HostViewModel { get; }
        public AlternativeCredentialListViewModel AlternativeCredentialListViewModel { get; }
        public ProtocolBaseWithAddressPortFormViewModel(ProtocolBaseWithAddressPort protocolBase) : base(protocolBase)
        {
            New = protocolBase;
            HostViewModel = new HostViewModel(protocolBase);
            AlternativeCredentialListViewModel = new AlternativeCredentialListViewModel(protocolBase);
        }



        public override bool CanSave()
        {
            if (!string.IsNullOrEmpty(New[nameof(New.Address)]))
                return false;
            return base.CanSave();
        }
    }
}
