namespace Nornia.Storage;

public static class NorniaPaths
{
    /// <summary>用户数据根目录:机器本地(<c>%LOCALAPPDATA%\Nornia</c>)。</summary>
    /// <remarks>
    /// 这里存放的是<strong>本机环境资产清单</strong>——已安装的 Runtime 与软件包快照、
    /// 已登记项目的绝对路径与环境绑定、环境修复记录。这些都不是可漫游的用户资料:
    /// 放在 Roaming 会让域环境下把 A 机的软件清单同步到 B 机(得到完全错误的结论),
    /// 且多台机器同时打开同一个 SQLite 文件有写冲突与损坏风险。
    /// 历史版本曾落在 Roaming,见 <see cref="MigrateFromRoamingIfNeeded"/>。
    /// </remarks>
    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nornia");

    public static string DatabasePath => Path.Combine(DataDirectory, "nornia.db");

    /// <summary>环境修复步骤明细文件(JSON Lines)。修复记录是回滚功能的数据,与数据库解耦,
    /// 与日志文件同属数据目录下的纯文件资产。</summary>
    public static string RepairLogPath => Path.Combine(DataDirectory, "environment-repair-logs.jsonl");

    /// <summary>历史版本使用的数据目录(Roaming)。仅迁移检查时使用。</summary>
    public static string LegacyRoamingDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Nornia");

    /// <summary>
    /// 把历史 Roaming 位置的数据一次性搬到 Local 位置。幂等且绝不阻塞启动:
    /// 任一步失败(文件被占用、权限不足)都保持原状,下次启动再试。
    /// </summary>
    public static void MigrateFromRoamingIfNeeded()
    {
        var legacy = LegacyRoamingDataDirectory;
        var current = DataDirectory;
        if (string.Equals(legacy, current, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(legacy))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(current);

            // 数据库本体连同 -wal/-shm 一并搬过去,不和剩余的 WAL 内容分家。
            foreach (var file in Directory.EnumerateFiles(legacy))
            {
                var destination = Path.Combine(current, Path.GetFileName(file));
                if (!File.Exists(destination))
                {
                    File.Move(file, destination);
                }
            }

            // 清空后删掉旧目录,避免留下第二个「看起来像数据目录」的位置。
            if (!Directory.EnumerateFileSystemEntries(legacy).Any())
            {
                Directory.Delete(legacy, recursive: false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 旧目录仍在,下次启动会重试;宁可延迟迁移也不要让用户数据处于半搬状态。
        }
    }
}
