using System.Runtime.InteropServices;

namespace FarArc.Utils.WindowsApi.WindowsShortcutFactory
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct ShellLinkInst
    {
        public unsafe ShellLinkV* Vtbl;
    }
}
