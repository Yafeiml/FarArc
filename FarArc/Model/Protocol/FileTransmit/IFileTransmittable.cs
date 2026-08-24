using FarArc.Model.Protocol.FileTransmit.Transmitters;

namespace FarArc.Model.Protocol.FileTransmit
{
    public interface IFileTransmittable
    {
        ITransmitter GeTransmitter();
        string GetStartupPath();
    }
}
