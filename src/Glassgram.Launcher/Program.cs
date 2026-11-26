using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

const string applicationPath = @"C:\Glassgram\release\Glassgram.exe";

if (!File.Exists(applicationPath))
{
    MessageBox.Show(
        "Актуальная сборка не найдена. Выполните Release publish в C:\\Glassgram\\release.",
        "Glassgram",
        MessageBoxButtons.OK,
        MessageBoxIcon.Error);
    return;
}

Process.Start(new ProcessStartInfo(applicationPath)
{
    UseShellExecute = true,
    WorkingDirectory = Path.GetDirectoryName(applicationPath)!
});
