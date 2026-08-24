using FarArc.Model.Protocol.Base;

namespace FarArc.Service.DataSource.DAO
{
    public interface IDataBaseServer
    {
        /// <summary>
        /// ULID since FarArc
        /// </summary>
        string GetId();

        string GetProtocol();

        string GetClassVersion();

        string GetJson();

        ProtocolBase? ToProtocolServerBase();
    }
}