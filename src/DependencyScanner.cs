using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Modding;

namespace Sts2DependencyAssistant;

public enum DependencyStatus
{
    Satisfied,
    Missing,
    Disabled,
    VersionTooLow,
    LoadFailed,
    Duplicate,
    NeedsRestart,
    Unknown
}

public sealed record DependencyIssue(
    string RequiredById,
    string RequiredByName,
    string? RequiredMinVersion,
    DependencyStatus Status,
    string Detail,
    ulong WorkshopId,
    string SourceHint);

public sealed record ScanResult(
    IReadOnlyList<DependencyIssue> Issues,
    IReadOnlyList<string> ScannedModIds,
    IReadOnlyList<string> Errors);

public static class DependencyScanner
{
    private static readonly HashSet<string> SeverityOrder = new()
    {
        "Missing", "LoadFailed", "Duplicate", "Disabled", "VersionTooLow", "Unknown", "Satisfied"
    };

    public static ScanResult Scan()
    {
        var issues = new List<DependencyIssue>();
        var scanned = new List<string>();
        var errors = new List<string>();

        try
        {
            var mods = ModManager.Mods;
            if (mods is null)
            {
                errors.Add("ModManager.Mods 不可用（游戏可能尚未完成 mod 初始化）");
                return new ScanResult(issues, scanned, errors);
            }

            var byId = new Dictionary<string, Mod>(StringComparer.OrdinalIgnoreCase);
            foreach (var mod in mods)
            {
                var id = mod.manifest?.id;
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                if (!byId.TryAdd(id, mod))
                {
                    // 同一 id 出现多次（工坊版 + 本地版）本身就是问题
                    issues.Add(new DependencyIssue(
                        id,
                        mod.manifest?.name ?? id,
                        null,
                        DependencyStatus.Duplicate,
                        "同一 mod 同时从创意工坊与本地目录加载，游戏将不会加载它",
                        mod.workshopId ?? 0,
                        DescribeSource(mod)));
                }
            }

            var enabledMods = mods.Where(IsEnabled).ToList();
            foreach (var mod in enabledMods)
            {
                var ownerId = mod.manifest?.id;
                if (string.IsNullOrWhiteSpace(ownerId))
                {
                    continue;
                }
                scanned.Add(ownerId);

                var deps = mod.manifest?.dependencies;
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ownerId };

                // 检查工坊"必需物品"类隐藏前置（未写进游戏 manifest 的软依赖）
                CheckHiddenPrerequisites(ownerId, mod, byId, visited, issues);

                if (deps is null || deps.Count == 0)
                {
                    continue;
                }

                WalkDependencies(mod, deps, byId, visited, issues, errors, 0);
            }
        }
        catch (Exception ex)
        {
            errors.Add($"扫描过程异常：{ex.GetBaseException().Message}");
        }

        // 去重并按严重度排序
        var distinct = issues
            .GroupBy(i => $"{i.RequiredById}|{i.Status}")
            .Select(g => g.OrderBy(i => i.Detail, StringComparer.Ordinal).First())
            .OrderBy(i => SeverityRank(i.Status))
            .ThenBy(i => i.RequiredById, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ScanResult(distinct, scanned.Distinct().ToList(), errors);
    }

    private static void WalkDependencies(
        Mod owner,
        IReadOnlyList<ModDependency> deps,
        Dictionary<string, Mod> byId,
        HashSet<string> visited,
        List<DependencyIssue> issues,
        List<string> errors,
        int depth)
    {
        if (depth > 8)
        {
            errors.Add($"依赖层级超过 8 层，已截断：{owner.manifest?.id}");
            return;
        }

        foreach (var dep in deps)
        {
            if (string.IsNullOrWhiteSpace(dep.id))
            {
                continue;
            }

            if (!visited.Add(dep.id))
            {
                // 循环依赖：访问过即跳过，避免递归死循环
                continue;
            }

            if (!byId.TryGetValue(dep.id, out var target))
            {
                var fileId = WorkshopBridge.TryGetFileId(dep.id, out var mappedId) ? mappedId : 0;
                issues.Add(new DependencyIssue(
                    dep.id,
                    dep.id,
                    dep.minVersion,
                    DependencyStatus.Missing,
                    $"被 {owner.manifest?.name ?? owner.manifest?.id ?? "未知 mod"} 需要，但未安装",
                    fileId,
                    "创意工坊未订阅或未安装"));
                continue;
            }

            var status = Classify(target);
            if (status != DependencyStatus.Satisfied)
            {
                issues.Add(new DependencyIssue(
                    dep.id,
                    target.manifest?.name ?? dep.id,
                    dep.minVersion,
                    status,
                    $"被 {owner.manifest?.name ?? owner.manifest?.id ?? "未知 mod"} 需要；{DescribeStatus(status)}",
                    target.workshopId ?? 0,
                    DescribeSource(target)));
            }
            else if (!string.IsNullOrWhiteSpace(dep.minVersion))
            {
                var versionProblem = CheckVersion(target, dep.minVersion);
                if (versionProblem is not null)
                {
                    issues.Add(new DependencyIssue(
                        dep.id,
                        target.manifest?.name ?? dep.id,
                        dep.minVersion,
                        DependencyStatus.VersionTooLow,
                        $"被 {owner.manifest?.name ?? owner.manifest?.id ?? "未知 mod"} 需要；{versionProblem}",
                        target.workshopId ?? 0,
                        DescribeSource(target)));
                }
            }

            // 递归检查该前置自己的前置
            var childDeps = target.manifest?.dependencies;
            if (childDeps is not null && childDeps.Count > 0)
            {
                WalkDependencies(target, childDeps, byId, visited, issues, errors, depth + 1);
            }
        }
    }

    private static DependencyStatus Classify(Mod mod)
    {
        return mod.state switch
        {
            ModLoadState.Loaded => DependencyStatus.Satisfied,
            ModLoadState.Failed => DependencyStatus.LoadFailed,
            ModLoadState.Disabled => DependencyStatus.Disabled,
            ModLoadState.DisabledDuplicate => DependencyStatus.Duplicate,
            // 游戏启动后才出现的 mod（如 Steam 刚下载完的订阅项）：已安装但需重启游戏
            ModLoadState.AddedAtRuntime => DependencyStatus.NeedsRestart,
            _ => DependencyStatus.Unknown
        };
    }

    private static string? CheckVersion(Mod target, string minVersion)
    {
        if (target.version is null)
        {
            return $"已安装版本无法解析（清单写 {target.manifest?.version ?? "空"}），无法确认是否满足 ≥ {minVersion}";
        }

        if (!SemanticVersion.TryFromString(minVersion, out var required) || required is null)
        {
            return $"前置要求版本 '{minVersion}' 不是合法的语义化版本";
        }

        return target.version.CompareTo(required) < 0
            ? $"已安装 {target.version}，低于要求的 {minVersion}"
            : null;
    }

    // 工坊"必需物品"不会写进游戏 manifest，游戏自身不做校验，
    // 但作者显式声明它们是运行前置。这里按已观察到的 mod 系列维护：
    // PPF 的 SpeedX / Skada / Rewind / QuickLink 都把 ModConfig 列为必需物品。
    private static readonly Dictionary<string, string[]> HiddenPrerequisites =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["SpeedX"] = new[] { "ModConfig" },
            ["Skada"] = new[] { "ModConfig" },
            ["Rewind"] = new[] { "ModConfig" },
            ["QuickLink"] = new[] { "ModConfig" }
        };

    private static void CheckHiddenPrerequisites(
        string ownerId,
        Mod owner,
        Dictionary<string, Mod> byId,
        HashSet<string> visited,
        List<DependencyIssue> issues)
    {
        if (!HiddenPrerequisites.TryGetValue(ownerId, out var requiredIds))
        {
            return;
        }

        foreach (var requiredId in requiredIds)
        {
            if (!visited.Add(requiredId))
            {
                continue;
            }

            if (byId.ContainsKey(requiredId))
            {
                continue; // 已安装（若被禁用，其自身的状态会在别处体现）
            }

            var fileId = WorkshopBridge.TryGetFileId(requiredId, out var mapped) ? mapped : 0;
            issues.Add(new DependencyIssue(
                requiredId,
                requiredId,
                null,
                DependencyStatus.Missing,
                $"被 {owner.manifest?.name ?? ownerId} 列为创意工坊必需物品，但未安装",
                fileId,
                "工坊必需物品（未写进游戏 manifest）"));
        }
    }

    private static bool IsEnabled(Mod mod)
    {
        if (mod.state == ModLoadState.Loaded)
        {
            return true;
        }

        // 加载失败但仍被启用的 mod，其依赖同样需要检查
        return mod.state == ModLoadState.Failed;
    }

    private static string DescribeStatus(DependencyStatus status) => status switch
    {
        DependencyStatus.Missing => "未安装",
        DependencyStatus.Disabled => "已在设置中禁用，需重新启用并重启",
        DependencyStatus.VersionTooLow => "版本过低，需更新",
        DependencyStatus.LoadFailed => "已安装但加载失败",
        DependencyStatus.Duplicate => "工坊版与本地版重复，游戏不会加载",
        DependencyStatus.NeedsRestart => "已安装/已下载，但需完全重启游戏后才会加载",
        DependencyStatus.Unknown => "状态未知",
        _ => "正常"
    };

    private static string DescribeSource(Mod mod) => mod.modSource switch
    {
        ModSource.SteamWorkshop => $"创意工坊项目 {mod.workshopId}",
        ModSource.ModsDirectory => "游戏本地 mods 目录（需手动安装）",
        _ => "来源未知"
    };

    private static int SeverityRank(DependencyStatus status) => status switch
    {
        DependencyStatus.Missing => 0,
        DependencyStatus.LoadFailed => 1,
        DependencyStatus.Duplicate => 2,
        DependencyStatus.Disabled => 3,
        DependencyStatus.VersionTooLow => 4,
        _ => 5
    };

}
