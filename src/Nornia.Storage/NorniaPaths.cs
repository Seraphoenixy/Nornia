namespace Nornia.Storage;

public static class NorniaPaths
{
    /// <summary>用户数据根目录:Roaming(APPDATA\Nornia)。设置/状态/数据库/日志均属应跟随
    /// 漫游用户资料的用户数据,而非机器相关的 Local 目录。</summary>
    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Nornia");

    public static string DatabasePath => Path.Combine(DataDirectory, "nornia.db");

    /// <summary>环境修复步骤明细文件(JSON Lines)。修复记录是回滚功能的数据,与数据库解耦,
    /// 与日志文件同属数据目录下的纯文件资产。</summary>
    public static string RepairLogPath => Path.Combine(DataDirectory, "environment-repair-logs.jsonl");
}
