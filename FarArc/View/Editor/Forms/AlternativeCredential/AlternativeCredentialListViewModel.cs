using FarArc.Model.Protocol.Base;
using FarArc.Utils;

namespace FarArc.View.Editor.Forms.AlternativeCredential;

public class AlternativeCredentialListViewModel : NotifyPropertyChangedBaseScreen
{
    public ProtocolBaseWithAddressPort New { get; }
    public AlternativeCredentialListViewModel(ProtocolBaseWithAddressPort protocol)
    {
        New = protocol;
    }
}