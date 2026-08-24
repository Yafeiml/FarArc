using System.ComponentModel;
using FarArc.Model.Protocol.Base;
using FarArc.Utils;
using Newtonsoft.Json;

namespace FarArc.View.Editor.Forms
{
    public class ProtocolBaseFormViewModel : NotifyPropertyChangedBaseScreen, IDataErrorInfo
    {
        public ProtocolBase New { get; }
        public ProtocolBaseFormViewModel(ProtocolBase protocolBase)
        {
            New = protocolBase;
        }

        ~ProtocolBaseFormViewModel()
        {
        }

        public virtual bool CanSave()
        {
            if (!string.IsNullOrEmpty(New[nameof(New.DisplayName)]))
                return false;
            return true;
        }

        #region IDataErrorInfo
        [JsonIgnore] public string Error => "";

        [JsonIgnore]
        public virtual string this[string columnName] => New[columnName];

        #endregion
    }
}
