using System.IO;
using System.Windows.Forms;

namespace RedTune
{
    // the settings every part can reach, and where the files shipped next to the exe are
    static class Comp
    {
        public static Settings S; // set once in Program

        public static string AppDir { get { return Path.GetDirectoryName(Application.ExecutablePath); } }
        public static string BundledRam { get { return Path.Combine(AppDir, "ram"); } }
    }
}
