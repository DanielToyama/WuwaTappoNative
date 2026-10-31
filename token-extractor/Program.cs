using System;
using System.Windows.Forms;
using WuwatappoNative.Net;

namespace WuwatappoTokenExtractor;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        OfficialTokenReader.CleanupStaleDumps();
        Application.Run(new MainForm());
    }
}
