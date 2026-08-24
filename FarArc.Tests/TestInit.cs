using System.IO;
using FarArc.Service;

namespace FarArc.Tests
{
    public static class TestInit
    {
        public static AppPathHelper UseIsolatedAppPath(string rootDirectory)
        {
            Directory.CreateDirectory(rootDirectory);
            var previous = AppPathHelper.Instance;
            AppPathHelper.Instance = new AppPathHelper(rootDirectory, rootDirectory);
            return previous;
        }
    }
}
