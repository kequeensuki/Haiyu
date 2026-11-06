using ABI.Models;
using Haiyu.Services.Contracts;
using Waves.Settings;

namespace Haiyu.Services;

/// <summary>持久化模块选择；null 表示首次使用，空列表表示不启用捕获。</summary>
public sealed class GameDataExportSelectionService : IGameDataExportSelectionService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<CancellationToken, Task<List<string>?>> _read;
    private readonly Func<List<string>, Task> _write;
    private readonly IGameDataExportCatalogService _catalog;

    public GameDataExportSelectionService(AppSettings settings, IGameDataExportCatalogService catalog)
        : this(settings.GetGameDataExportModuleIdsAsync, ids => settings.SetGameDataExportModuleIdsAsync(ids), catalog) { }

    internal GameDataExportSelectionService(Func<CancellationToken, Task<List<string>?>> read,
        Func<List<string>, Task> write, IGameDataExportCatalogService catalog)
    { _read = read; _write = write; _catalog = catalog; }

    public async Task<string[]?> GetAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try { return (await _read(token).ConfigureAwait(false))?.ToArray(); }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(string[] moduleIds)
    {
        var snapshot = moduleIds.Distinct(StringComparer.Ordinal).ToList();
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await _write(snapshot).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task<GameDataExportRequest?> CreateRequestAsync(CancellationToken token = default)
    {
        var selected = await GetAsync(token).ConfigureAwait(false);
        // 用户关闭所有模块时不查询高权限目录，避免无意义的 UAC 和捕获启动。
        if (selected is { Length: 0 }) return null;
        var modules = await _catalog.GetAsync(token).ConfigureAwait(false);
        var ids = modules.Where(module => selected is null ? module.DefaultSelected
            : selected.Contains(module.Id, StringComparer.Ordinal)).Select(module => module.Id).ToArray();
        return ids.Length == 0 ? null : new(GameDataExportScope.None, ids);
    }
}
