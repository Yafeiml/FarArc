using System.Runtime.InteropServices;

namespace FarArc.Utils.WindowsApi.WindowsShortcutFactory
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct PersistFileInst
    {
        public unsafe PersistFileV* Vtbl;
    }
}
