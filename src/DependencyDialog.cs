using Godot;
using MegaCrit.Sts2.Core.Nodes.CommonUi;

namespace Sts2DependencyAssistant;

/// <summary>
/// 游戏内依赖检查窗口。
/// 本类不是 Godot 节点（不继承 Control），而是持有原生 Control 作为根节点。
/// 这是 MultiplayerModSync.SyncDialog 采用的模式——mod 程序集中的自定义 Godot
/// 脚本类在 AddChild 时无法被正确识别，必须用 Godot 内置节点类型作为根。
/// </summary>
public sealed class DependencyDialog
{
    private static DependencyDialog? _current;

    private readonly Control _dialog = new();
    private readonly VBoxContainer _rows = new();
    private readonly Label _summary = new();
    private readonly Button _subscribeAllButton = new();
    private readonly Button _refreshButton = new();
    private readonly Button _closeButton = new();
    private readonly Godot.Timer _pollTimer = new();
    private readonly ColorRect _backdrop = new();
    private readonly ProgressBar _progressBar = new();
    private readonly Label _progressLabel = new();

    private IReadOnlyList<DependencyIssue> _issues = Array.Empty<DependencyIssue>();
    private bool _autoFixTriggered;
    private SceneTree? _owningTree;
    private readonly Dictionary<ulong, WorkshopBridge.DownloadProgress> _progress = new();
    private readonly Dictionary<ulong, string> _downloadNames = new();

    private void OnTreeProcessFrame()
    {
        if (Godot.Input.IsActionJustPressed("ui_cancel"))
        {
            Close();
        }
    }

    public static bool IsOpen => _current is not null && GodotObject.IsInstanceValid(_current._dialog);

    public static void Show(IReadOnlyList<DependencyIssue> issues, bool autoFix = false)
    {
        if (IsOpen)
        {
            _current!._dialog.QueueFree();
            _current = null;
        }

        var tree = Engine.GetMainLoop() as SceneTree;
        if (tree?.Root is null)
        {
            PluginLog.Error("无法获取 SceneTree.Root，窗口未打开");
            return;
        }

        var dialog = new DependencyDialog();
        _current = dialog;
        dialog.SetIssues(issues);
        dialog._dialog.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        dialog._dialog.CustomMinimumSize = Vector2.Zero;

        var host = NModalContainer.Instance is { } modal && GodotObject.IsInstanceValid(modal)
            ? (Node)modal
            : tree.Root;
        host.AddChild(dialog._dialog);
        dialog._pollTimer.Start();

        // ESC 关闭：挂到场景树帧回调，关闭时断开
        dialog._owningTree = tree;
        tree.ProcessFrame += dialog.OnTreeProcessFrame;

        if (autoFix)
        {
            var delay = new Godot.Timer { OneShot = true, WaitTime = 0.5 };
            delay.Timeout += () =>
            {
                dialog.AutoSubscribeAll();
                delay.QueueFree();
            };
            dialog._dialog.AddChild(delay);
            delay.Start();
        }
    }

    public static void Close()
    {
        if (IsOpen)
        {
            var dialog = _current!;
            if (dialog._owningTree is not null)
            {
                dialog._owningTree.ProcessFrame -= dialog.OnTreeProcessFrame;
                dialog._owningTree = null;
            }
            dialog._dialog.QueueFree();
            _current = null;
        }
    }

    private DependencyDialog()
    {
        ApplyFont();
        BuildUi();
        _pollTimer.OneShot = false;
        _pollTimer.WaitTime = 2.0;
        _pollTimer.Timeout += OnPollTimeout;
        _dialog.AddChild(_pollTimer);
        // 注意：Start() 必须在 _dialog 挂入场景树之后调用，否则 Godot 报
        // "timer not inside scene tree"。实际启动延迟到 Show() 的 AddChild 之后。
    }

    /// <summary>
    /// 通过 Theme 为整个窗口统一设置字体。
    /// 优先微软雅黑，缺失时按列表顺序回退到其他中文字体。
    /// </summary>
    private void ApplyFont()
    {
        _dialog.Theme = UiFont.CreateTheme();
    }

    private void BuildUi()
    {
        // 全屏背板：拦截面板外的点击并关闭窗口（轻量半透明蒙层）
        _backdrop.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _backdrop.Color = new Color(0f, 0f, 0f, 0f);
        _backdrop.MouseFilter = Control.MouseFilterEnum.Stop;
        _backdrop.GuiInput += OnBackdropGuiInput;
        _dialog.AddChild(_backdrop);

        var panel = new PanelContainer();
        // 锚定右边缘：窗口靠右，右边距 60px；垂直方向偏上，不贴底。
        panel.SetAnchorsPreset(Control.LayoutPreset.Center);
        panel.AnchorLeft = 1f;
        panel.AnchorRight = 1f;
        panel.OffsetLeft = -860;   // 宽 800，右边距 60
        panel.OffsetRight = -60;
        panel.OffsetTop = -300;    // 高 560，中心偏上约 20px
        panel.OffsetBottom = 260;
        panel.CustomMinimumSize = new Vector2(760, 460);
        panel.MouseFilter = Control.MouseFilterEnum.Stop;
        _dialog.AddChild(panel);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 24);
        margin.AddThemeConstantOverride("margin_right", 24);
        margin.AddThemeConstantOverride("margin_top", 20);
        margin.AddThemeConstantOverride("margin_bottom", 20);
        panel.AddChild(margin);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 12);
        margin.AddChild(root);

        var title = new Label();
        title.Text = "前置检查助手";
        title.AddThemeFontSizeOverride("font_size", UiFont.TitleFontSize);
        root.AddChild(title);

        var subtitle = new Label();
        subtitle.Text = "检查已启用 mod 的依赖完整性";
        subtitle.AddThemeFontSizeOverride("font_size", UiFont.HintFontSize);
        subtitle.AddThemeColorOverride("font_color", UiFont.ColorMuted);
        root.AddChild(subtitle);

        var separator = new HSeparator();
        root.AddChild(separator);

        _summary.Text = "";
        _summary.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        root.AddChild(_summary);

        // 下载进度：初始隐藏，点击一键下载后显示
        _progressLabel.Text = "";
        _progressLabel.AddThemeFontSizeOverride("font_size", UiFont.HintFontSize);
        root.AddChild(_progressLabel);

        _progressBar.MinValue = 0;
        _progressBar.MaxValue = 1;
        _progressBar.Value = 0;
        _progressBar.CustomMinimumSize = new Vector2(0, 24);
        _progressBar.ShowPercentage = true;
        root.AddChild(_progressBar);
        _progressLabel.Visible = false;
        _progressBar.Visible = false;

        var scroll = new ScrollContainer();
        scroll.CustomMinimumSize = new Vector2(0, 200);
        scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        scroll.VerticalScrollMode = ScrollContainer.ScrollMode.ShowAlways;
        root.AddChild(scroll);

        _rows.AddThemeConstantOverride("separation", 8);
        scroll.AddChild(_rows);

        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 12);
        root.AddChild(buttons);

        _subscribeAllButton.Text = "订阅并下载缺失前置";
        _subscribeAllButton.Pressed += AutoSubscribeAll;
        buttons.AddChild(_subscribeAllButton);

        _refreshButton.Text = "重新检查";
        _refreshButton.Pressed += OnRefreshPressed;
        buttons.AddChild(_refreshButton);

        _closeButton.Text = "关闭";
        _closeButton.Pressed += Close;
        buttons.AddChild(_closeButton);
    }

    private void SetIssues(IReadOnlyList<DependencyIssue> issues)
    {
        _issues = issues;
        RefreshRows();
    }

    private void RefreshRows()
    {
        foreach (var child in _rows.GetChildren())
        {
            if (child is Node node)
            {
                node.QueueFree();
            }
        }

        var actionable = _issues.Count(i => i.Status is DependencyStatus.Missing or DependencyStatus.VersionTooLow);
        var needRestart = _issues.Count(i => i.Status == DependencyStatus.NeedsRestart);
        _summary.Text = _issues.Count == 0
            ? "所有已启用 mod 的前置依赖均已满足。"
            : $"发现 {_issues.Count} 项前置问题，其中 {actionable} 项可通过创意工坊订阅/更新处理。";
        _summary.AddThemeColorOverride("font_color",
            _issues.Count == 0 ? UiFont.ColorOk : (actionable > 0 ? UiFont.ColorMissing : UiFont.ColorWarning));
        _subscribeAllButton.Disabled = actionable == 0 || !WorkshopBridge.IsAvailable;

        if (needRestart > 0)
        {
            _summary.Text += $"\n{needRestart} 项已下载完成，请完全退出游戏后重新启动即可加载。";
        }

        if (!WorkshopBridge.IsAvailable)
        {
            _summary.Text += "\n警告：当前无法访问 Steamworks 订阅接口，只能打开工坊页面手动处理。";
        }

        if (_issues.Count == 0)
        {
            var ok = new Label();
            ok.Text = "未发现缺失或版本不满足的前置依赖。";
            _rows.AddChild(ok);
            return;
        }

        foreach (var issue in _issues)
        {
            _rows.AddChild(BuildRow(issue));
        }
    }

    private static Control BuildRow(DependencyIssue issue)
    {
        var row = new PanelContainer();
        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 12);
        margin.AddThemeConstantOverride("margin_right", 12);
        margin.AddThemeConstantOverride("margin_top", 8);
        margin.AddThemeConstantOverride("margin_bottom", 8);
        row.AddChild(margin);

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 4);
        margin.AddChild(box);

        var head = new Label();
        head.Text = $"{StatusIcon(issue.Status)} {issue.RequiredByName} ({issue.RequiredById})";
        head.AddThemeFontSizeOverride("font_size", UiFont.RowTitleFontSize);
        head.AddThemeColorOverride("font_color", StatusColor(issue.Status));
        box.AddChild(head);

        var detail = new Label();
        detail.Text = issue.Detail;
        detail.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        box.AddChild(detail);

        var hint = new Label();
        hint.Text = $"来源：{issue.SourceHint}" +
                    (issue.RequiredMinVersion is null ? "" : $"　要求版本：≥ {issue.RequiredMinVersion}");
        hint.AddThemeFontSizeOverride("font_size", UiFont.HintFontSize);
        hint.AddThemeColorOverride("font_color", UiFont.ColorMuted);
        box.AddChild(hint);

        // 每行只显示工坊链接；下载统一由窗口底部的「一键下载」按钮触发
        var link = new LinkButton();
        link.Text = issue.WorkshopId != 0 ? "打开创意工坊页面" : "在创意工坊中搜索";
        link.Uri = issue.WorkshopId != 0
            ? WorkshopBridge.WorkshopPageUrl(issue.WorkshopId)
            : WorkshopBridge.WorkshopSearchUrl(issue.RequiredById);
        box.AddChild(link);

        return row;
    }

    private static Color StatusColor(DependencyStatus status) => status switch
    {
        DependencyStatus.Missing => UiFont.ColorMissing,
        DependencyStatus.LoadFailed => UiFont.ColorMissing,
        DependencyStatus.Satisfied => UiFont.ColorOk,
        _ => UiFont.ColorWarning
    };

    private static string StatusIcon(DependencyStatus status) => status switch
    {
        DependencyStatus.Satisfied => "OK",
        DependencyStatus.Missing => "MISSING",
        DependencyStatus.Disabled => "DISABLED",
        DependencyStatus.VersionTooLow => "OLD VERSION",
        DependencyStatus.LoadFailed => "LOAD FAILED",
        DependencyStatus.Duplicate => "DUPLICATE",
        DependencyStatus.NeedsRestart => "需重启游戏",
        _ => "UNKNOWN"
    };

    private void AutoSubscribeAll()
    {
        if (_autoFixTriggered)
        {
            return;
        }
        _autoFixTriggered = true;
        _subscribeAllButton.Disabled = true;

        var targets = _issues
            .Where(i => i.Status is DependencyStatus.Missing or DependencyStatus.VersionTooLow)
            .Where(i => i.WorkshopId != 0)
            .DistinctBy(i => i.WorkshopId)
            .ToList();

        if (targets.Count == 0)
        {
            _summary.Text += "\n没有可自动订阅的工坊项目（缺失前置未映射到工坊 ID）。";
            return;
        }

        var okCount = 0;
        foreach (var target in targets)
        {
            if (WorkshopBridge.SubscribeAndDownload(target.WorkshopId))
            {
                okCount++;
                _downloadNames[target.WorkshopId] = target.RequiredByName;
                PluginLog.Info($"已提交订阅：{target.RequiredById} ({target.WorkshopId})");
            }
            else
            {
                PluginLog.Error($"订阅失败：{target.RequiredById} ({target.WorkshopId})");
            }
        }

        _summary.Text += $"\n已提交 {okCount}/{targets.Count} 项订阅，下载完成后请重启游戏。";
        if (okCount > 0)
        {
            _progressLabel.Visible = true;
            _progressBar.Visible = true;
        }
    }

    private void OnBackdropGuiInput(InputEvent @event)
    {
        // 点击面板外的任意位置关闭；面板本身 MouseFilter=Stop 不会触发此事件
        if (@event is InputEventMouseButton mouse &&
            mouse.Pressed &&
            mouse.ButtonIndex == MouseButton.Left)
        {
            Close();
        }
    }

    private void OnRefreshPressed()
    {
        var result = DependencyScanner.Scan();
        SetIssues(result.Issues);
    }

    private void OnPollTimeout()
    {
        if (!IsOpen)
        {
            return;
        }

        WorkshopBridge.PollSubscriber();
        if (_downloadNames.Count == 0)
        {
            return;
        }

        // 查询所有下载项，取整体平均进度；显示当前正在下载的名称
        double totalFraction = 0;
        int count = 0;
        string? activeName = null;
        var allInstalled = true;

        foreach (var fileId in _downloadNames.Keys)
        {
            var p = WorkshopBridge.GetProgress(fileId);
            _progress[fileId] = p;
            totalFraction += p.Fraction;
            count++;

            if (p.IsDownloading || !p.IsInstalled)
            {
                activeName = _downloadNames[fileId];
            }
            if (!p.IsInstalled || p.NeedsUpdate)
            {
                allInstalled = false;
            }
        }

        var avg = count > 0 ? (float)(totalFraction / count) : 0f;
        _progressBar.Value = avg;

        if (allInstalled)
        {
            _progressLabel.Text = $"下载完成，请完全重启游戏以加载（{count} 项）";
            _progressBar.Value = 1f;
        }
        else if (activeName is not null)
        {
            var pct = (int)(avg * 100);
            _progressLabel.Text = $"正在下载：{activeName}  {pct}%";
        }
        else
        {
            _progressLabel.Text = "等待 Steam 开始下载...";
        }
    }
}
