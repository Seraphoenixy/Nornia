using Nornia.Core.Interfaces;
using Nornia.Core.Models;
using System.Text.Json;

namespace Nornia.Storage;

/// <summary>环境修复步骤明细的文件存储(JSON Lines)。每个修复步骤执行完即追加一行 JSON 到
/// %APPDATA%\Nornia\environment-repair-logs.jsonl,回滚引擎按 correlation_id 读回步骤序列
/// 做逆向还原。读写经同一把信号量串行化,桌面规模(每次修复数条记录)下同步追加足够可靠。</summary>
public sealed class EnvironmentRepairLogStore : IEnvironmentRepairLogRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public EnvironmentRepairLogStore()
        : this(NorniaPaths.RepairLogPath)
    {
    }

    public EnvironmentRepairLogStore(string path) => _path = path;

    public async Task AppendAsync(EnvironmentRepairLogEntry entry, CancellationToken cancellationToken = default)
    {
        await Task.Run(async () =>
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                await File.AppendAllLinesAsync(_path, [JsonSerializer.Serialize(entry, JsonOptions)], cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<EnvironmentRepairLogEntry>> GetByCorrelationAsync(Guid correlationId, CancellationToken cancellationToken = default)
    {
        var all = await ReadAllAsync(cancellationToken);
        return all
            .Where(entry => entry.CorrelationId == correlationId)
            .OrderBy(entry => entry.Sequence)
            .ThenBy(entry => entry.Timestamp)
            .ToArray();
    }

    private async Task<IReadOnlyList<EnvironmentRepairLogEntry>> ReadAllAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var lines = await File.ReadAllLinesAsync(_path, cancellationToken);
            var entries = new List<EnvironmentRepairLogEntry>();
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }
                try
                {
                    entries.Add(JsonSerializer.Deserialize<EnvironmentRepairLogEntry>(line, JsonOptions)!);
                }
                catch (JsonException)
                {
                    // 单行损坏(如历史格式变化/写入中断)不阻断整份历史读取。
                }
            }
            return entries;
        }
        finally
        {
            _gate.Release();
        }
    }
}
