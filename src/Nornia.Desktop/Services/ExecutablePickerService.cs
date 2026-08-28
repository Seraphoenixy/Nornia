using Microsoft.Win32;

namespace Nornia.Desktop.Services;

public interface IExecutablePickerService
{
    string? PickExecutable();
}

public sealed class ExecutablePickerService : IExecutablePickerService
{
    public string? PickExecutable()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择项目编辑器",
            Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
