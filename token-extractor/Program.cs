using System;
using System.Windows.Forms;

namespace WuwatappoTokenExtractor;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        TokenExtractor.CleanupStaleDumps();
        Application.Run(new MainForm());
    }
}
