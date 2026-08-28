using Microsoft.Win32;
using System.IO;

namespace Nornia.Desktop.Services;

public interface IFolderPickerService
{
    string? PickFolder(string? initialDirectory = null);
}

public sealed class FolderPickerService : IFolderPickerService
{
    public string? PickFolder(string? initialDirectory = null)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择项目文件夹",
            Multiselect = false,
            InitialDirectory = !string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory)
                ? initialDirectory
                : Environment.CurrentDirectory
        };

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
