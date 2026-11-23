using System.Collections;
using System.Reflection;
using Godot;

namespace Sts2DependencyAssistant;

/// <summary>
/// 通过反射复用 AutoModSubscriber 的订阅队列与 Steamworks 下载接口。
/// 反射而非直接引用，目的是避免与 AutoModSubscriber 的具体程序集版本强绑定。
/// </summary>
internal static class WorkshopBridge
{
    private static readonly Type? MapType =
        Type.GetType("AutoModSubscriber.Protocol.ModWorkshopMap, AutoModSubscriber.Impl");

    private static readonly MethodInfo? TryGetMethod =
        MapType?.GetMethod("TryGet", BindingFlags.Public | BindingFlags.Static);

    private static readonly Type? SubscriberType =
        Type.GetType("AutoModSubscriber.Subscribe.WorkshopSubscriber, AutoModSubscriber.Impl");

    private static readonly PropertyInfo? InstanceProperty =
        SubscriberType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);

    private static readonly MethodInfo? SubmitMethod =
        SubscriberType?.GetMethod("Submit", BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance);

    private static readonly MethodInfo? PollMethod =
        SubscriberType?.GetMethod("Poll", BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance);

    private static readonly Type? SteamUgcType = Type.GetType("Steamworks.SteamUGC, Steamworks.NET");
    private static readonly Type? PublishedFileIdType = Type.GetType("Steamworks.PublishedFileId_t, Steamworks.NET");

    private static readonly MethodInfo? SubscribeItemMethod =
        SteamUgcType?.GetMethod("SubscribeItem", BindingFlags.Public | BindingFlags.Static);

    private static readonly MethodInfo? DownloadItemMethod =
        SteamUgcType?.GetMethod("DownloadItem", BindingFlags.Public | BindingFlags.Static);

    private static readonly MethodInfo? GetItemInstallInfoMethod =
        SteamUgcType?.GetMethod("GetItemInstallInfo", BindingFlags.Public | BindingFlags.Static);

    private static readonly MethodInfo? GetItemDownloadInfoMethod =
        SteamUgcType?.GetMethod("GetItemDownloadInfo", BindingFlags.Public | BindingFlags.Static);

    private static readonly MethodInfo? GetItemStateMethod =
        SteamUgcType?.GetMethod("GetItemState", BindingFlags.Public | BindingFlags.Static);

    /// <summary>Steam 项目下载状态查询（反射，避免编译期强引用程序集版本）。</summary>
    public sealed record DownloadProgress(
        bool IsDownloading,
        bool IsInstalled,
        bool NeedsUpdate,
        ulong BytesDownloaded,
        ulong BytesTotal,
        float Fraction)
    {
        public static readonly DownloadProgress None = new(false, false, false, 0, 0, 0f);
    }

    public static DownloadProgress GetProgress(ulong fileId)
    {
        if (fileId == 0 || PublishedFileIdType is null || GetItemDownloadInfoMethod is null)
        {
            return DownloadProgress.None;
        }

        try
        {
            var item = Activator.CreateInstance(PublishedFileIdType, fileId);
            if (item is null)
            {
                return DownloadProgress.None;
            }

            var stateArgs = new object[] { item };
            uint state = 0;
            if (GetItemStateMethod is not null)
            {
                var stateResult = GetItemStateMethod.Invoke(null, stateArgs);
                state = stateResult is uint u ? u : 0;
            }

            const uint installed = 4;
            const uint needsUpdate = 8;
            const uint downloading = 0x10;
            const uint pending = 0x20;
            var isDownloading = (state & (downloading | pending)) != 0;
            var isInstalled = (state & installed) != 0;
            var needsUpd = (state & needsUpdate) != 0;

            var args = new object?[] { item, 0UL, 0UL };
            var ok = GetItemDownloadInfoMethod.Invoke(null, args) as bool? ?? false;
            ulong downloaded = args[1] is ulong d ? d : 0;
            ulong total = args[2] is ulong t ? t : 0;
            var fraction = ok && total > 0 ? (float)downloaded / total : 0f;

            return new DownloadProgress(isDownloading, isInstalled, needsUpd, downloaded, total, fraction);
        }
        catch
        {
            return DownloadProgress.None;
        }
    }

    public static bool IsAvailable =>
        (SubscribeItemMethod is not null || DownloadItemMethod is not null) &&
        PublishedFileIdType is not null;

    public static string WorkshopPageUrl(ulong fileId) =>
        $"https://steamcommunity.com/sharedfiles/filedetails/?id={fileId}";

    public static string WorkshopSearchUrl(string manifestId) =>
        $"https://steamcommunity.com/workshop/browse/?appid=2868840&searchtext={Uri.EscapeDataString(manifestId)}";

    // 常见前置的已知工坊 ID：当 mod 被取消订阅、本地目录消失时，
    // 目录扫描无法反查，用此表兜底（ID 来自本机工坊订阅清单与已安装 manifest）。
    private static readonly Dictionary<string, ulong> KnownWorkshopIds =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["BaseLib"] = 3737335127UL,
            ["BaseLibCompatibilityHelper"] = 3737335127UL,
            ["AutoModSubscriber"] = 3750485606UL,
            ["LoadOrderManager"] = 3747605109UL,
            ["MultiplayerModSync"] = 3762348066UL,
            ["STS2-RitsuLib"] = 3747602295UL,
            ["RitsuLib"] = 3747602295UL,
            ["EnchantLib"] = 3827534573UL,
            ["ModConfig"] = 3747557003UL
        };

    public static bool TryGetFileId(string manifestId, out ulong fileId)
    {
        fileId = 0;

        // 常见前置的已知 ID 优先，避免依赖本地目录是否还在
        if (KnownWorkshopIds.TryGetValue(manifestId, out var knownId))
        {
            fileId = knownId;
            return true;
        }

        // 优先复用 AutoModSubscriber 的映射（若已安装）
        if (TryGetMethod is not null)
        {
            try
            {
                var args = new object?[] { manifestId, 0UL };
                var ok = TryGetMethod.Invoke(null, args) as bool? ?? false;
                if (ok && args[1] is ulong id && id != 0)
                {
                    fileId = id;
                    return true;
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warn($"AutoModSubscriber 映射失败，改用目录扫描: {ex.GetBaseException().Message}");
            }
        }

        // 兜底：直接扫描已订阅工坊目录的 manifest，不依赖任何第三方 mod
        return TryScanWorkshopDirectory(manifestId, out fileId);
    }

    private static readonly string[] CandidateManifestNames =
    {
        "mod_manifest.json",
        "manifest.json",
        "ShengZhuSts2Mod.json"
    };

    private static bool TryScanWorkshopDirectory(string manifestId, out ulong fileId)
    {
        fileId = 0;
        try
        {
            var workshopRoot = GetWorkshopRoot();
            if (string.IsNullOrEmpty(workshopRoot) || !Directory.Exists(workshopRoot))
            {
                return false;
            }

            foreach (var dir in Directory.EnumerateDirectories(workshopRoot))
            {
                var name = Path.GetFileName(dir);
                if (!ulong.TryParse(name, out var candidateId) || candidateId == 0)
                {
                    continue;
                }

                foreach (var manifestName in CandidateManifestNames)
                {
                    var manifestPath = Path.Combine(dir, manifestName);
                    if (!File.Exists(manifestPath))
                    {
                        continue;
                    }

                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifestPath));
                        var root = doc.RootElement;
                        if (root.TryGetProperty("id", out var idProp) &&
                            string.Equals(idProp.GetString(), manifestId, StringComparison.OrdinalIgnoreCase))
                        {
                            fileId = candidateId;
                            return true;
                        }
                    }
                    catch
                    {
                        // 单个 manifest 解析失败不影响其他目录
                    }
                }
            }
        }
        catch (Exception ex)
        {
            PluginLog.Warn($"扫描工坊目录失败: {ex.GetBaseException().Message}");
        }

        return false;
    }

    private static string GetWorkshopRoot()
    {
        try
        {
            var exePath = Godot.OS.GetExecutablePath();
            var gameDir = Path.GetDirectoryName(exePath);
            if (string.IsNullOrEmpty(gameDir))
            {
                return string.Empty;
            }

            // Steam 库布局：<library>/steamapps/common/Slay the Spire 2
            var steamApps = Path.GetDirectoryName(Path.GetDirectoryName(gameDir));
            if (string.IsNullOrEmpty(steamApps))
            {
                return string.Empty;
            }

            return Path.Combine(steamApps, "workshop", "content", "2868840");
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 订阅并优先下载一个工坊项目。SubscribeItem 使项目进入订阅列表，
    /// DownloadItem(itemId, true) 请求高优先级下载。
    /// </summary>
    public static bool SubscribeAndDownload(ulong fileId)
    {
        if (fileId == 0 || PublishedFileIdType is null)
        {
            return false;
        }

        var ok = true;
        try
        {
            var item = Activator.CreateInstance(PublishedFileIdType, fileId);
            if (item is null)
            {
                return false;
            }

            if (SubscribeItemMethod is not null)
            {
                SubscribeItemMethod.Invoke(null, new[] { item });
            }
            else
            {
                ok = false;
            }

            if (DownloadItemMethod is not null)
            {
                var result = DownloadItemMethod.Invoke(null, new object[] { item, true });
                ok &= result is bool b && b;
            }
            else
            {
                ok = false;
            }
        }
        catch (Exception ex)
        {
            PluginLog.Error($"SubscribeAndDownload({fileId}) 失败: {ex.GetBaseException().Message}");
            return false;
        }

        // 交由 AutoModSubscriber 的队列处理后续轮询与进度
        SubmitToSubscriber(fileId);
        return ok;
    }

    public static bool ForceDownload(ulong fileId)
    {
        if (fileId == 0 || PublishedFileIdType is null || DownloadItemMethod is null)
        {
            return false;
        }

        try
        {
            var item = Activator.CreateInstance(PublishedFileIdType, fileId);
            var result = DownloadItemMethod.Invoke(null, new object?[] { item, true });
            return result is bool b && b;
        }
        catch (Exception ex)
        {
            PluginLog.Error($"ForceDownload({fileId}) 失败: {ex.GetBaseException().Message}");
            return false;
        }
    }

    /// <summary>
    /// 复用 AutoModSubscriber 的订阅队列。Submit 需要一个包含 ManifestId 与 FileId 的元组集合，
    /// 这里用反射构造，避免编译期依赖其程序集版本。
    /// </summary>
    private static void SubmitToSubscriber(ulong fileId)
    {
        try
        {
            var instance = InstanceProperty?.GetValue(null);
            if (instance is null || SubmitMethod is null)
            {
                return;
            }

            // 构造 IEnumerable<(string, ulong)>：AutoModSubscriber.Submit 的参数类型
            var tupleType = typeof(ValueTuple<,>).MakeGenericType(typeof(string), typeof(ulong));
            var tuple = Activator.CreateInstance(tupleType, "", fileId);
            var listType = typeof(List<>).MakeGenericType(tupleType);
            var list = (IList)Activator.CreateInstance(listType)!;
            list.Add(tuple);
            SubmitMethod.Invoke(instance, new object[] { list });
        }
        catch (Exception ex)
        {
            // 队列不可用不影响 Steam 原生订阅本身
            PluginLog.Warn($"提交订阅队列失败（Steam 订阅仍有效）: {ex.GetBaseException().Message}");
        }
    }

    public static void PollSubscriber()
    {
        try
        {
            var instance = InstanceProperty?.GetValue(null);
            if (instance is not null && PollMethod is not null)
            {
                PollMethod.Invoke(instance, null);
            }
        }
        catch (Exception ex)
        {
            PluginLog.Warn($"Poll 失败: {ex.GetBaseException().Message}");
        }
    }
}

internal static class PluginLog
{
    private const string Prefix = "[DependencyAssistant]";

    public static void Info(string message) => GD.Print($"{Prefix} {message}");
    public static void Warn(string message) => GD.PushWarning($"{Prefix} {message}");
    public static void Error(string message) => GD.PushError($"{Prefix} {message}");
}
