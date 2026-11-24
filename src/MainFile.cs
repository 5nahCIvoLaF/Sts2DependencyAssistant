using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;

namespace Sts2DependencyAssistant;

[ModInitializer(nameof(Initialize))]
public partial class MainFile : Node
{
    public const string ModId = "Sts2DependencyAssistant";

    public static void Initialize()
    {
        PluginLog.Info("初始化开始");

        var harmony = new Harmony(ModId);
        harmony.PatchAll();

        // 启动自动检测不在初始化阶段调度：此时主菜单尚未出现、
        // mod 加载状态未稳定，容易误判。改由主菜单 _Ready 补丁触发。

        PluginLog.Info("初始化完成");
    }
}

/// <summary>
/// 在主菜单 _Ready 之后注入浮动「前置检查」按钮。
/// 不占用主菜单按钮列表，独立锚定在屏幕右下角偏上位置。
/// </summary>
[HarmonyPatch(typeof(NMainMenu), "_Ready")]
internal static class MainMenuReadyPatch
{
    private const string ButtonName = "DependencyAssistantFloatButton";

    private static void Postfix(NMainMenu __instance)
    {
        InjectButton(__instance);

        // 主菜单已出现，此时 mod 加载状态稳定，才做启动检查
        CheckRunner.ScheduleStartupCheck();
    }

    /// <summary>
    /// 把按钮可见性绑定到「主菜单是否为当前活动屏幕」。
    /// 角色选择、图鉴等界面打开时主菜单通常不销毁、也不隐藏，
    /// 只有 ActiveScreenContext 能反映真实的当前界面。
    /// </summary>
    private static void BindVisibility(NMainMenu instance, Button button)
    {
        var context = ActiveScreenContext.Instance;

        void Sync()
        {
            if (!GodotObject.IsInstanceValid(button) || !GodotObject.IsInstanceValid(instance))
            {
                return;
            }
            button.Visible = context.IsCurrent(instance);
        }

        context.Updated += Sync;
        Sync();

        // 节点销毁时解除订阅，避免持有已释放节点
        button.TreeExiting += () =>
        {
            context.Updated -= Sync;
        };
    }

    private static void InjectButton(NMainMenu instance)
    {
        try
        {
            if (instance.GetNodeOrNull(ButtonName) is not null)
            {
                return;
            }

            var button = new Button();
            button.Name = ButtonName;
            button.Text = "前置检查";
            button.Theme = UiFont.CreateTheme(UiFont.HintFontSize + 3);
            button.CustomMinimumSize = new Vector2(120, 44);

            // 锚定右下角，向上偏移 150px、向左偏移 40px
            button.SetAnchorsPreset(Control.LayoutPreset.BottomRight);
            button.OffsetLeft = -40 - 120;
            button.OffsetTop = -150 - 44;
            button.OffsetRight = -40;
            button.OffsetBottom = -150;

            // 挂到主菜单根节点
            instance.AddChild(button);
            button.Pressed += CheckRunner.OpenWindow;

            // 只在主菜单是当前活动界面时显示。
            // 进入角色选择等界面时主菜单节点仍留在树里且可见，
            // 单靠父节点可见性无法隐藏，必须用游戏的屏幕上下文判断。
            BindVisibility(instance, button);

            PluginLog.Info("浮动前置检查按钮已注入（右下角）");
        }
        catch (Exception ex)
        {
            PluginLog.Error($"注入浮动按钮失败: {ex.GetBaseException().Message}");
        }
    }
}

internal static class CheckRunner
{
    private static bool _startupCheckDone;

    public static void ScheduleStartupCheck()
    {
        if (_startupCheckDone)
        {
            return;
        }
        _startupCheckDone = true;

        // 用 SceneTree 计时器延迟到主菜单就绪之后
        CallDeferredStartup();
    }

    private static void CallDeferredStartup()
    {
        SceneTree? tree = null;
        try
        {
            tree = Engine.GetMainLoop() as SceneTree;
        }
        catch (Exception ex)
        {
            PluginLog.Warn($"获取 SceneTree 失败: {ex.GetBaseException().Message}");
        }

        if (tree is null)
        {
            PluginLog.Info("SceneTree 尚不可用，跳过启动自动检测（可通过主菜单按钮手动检查）");
            return;
        }

        // 主菜单出现后再延迟 2 秒，等加载状态完全稳定
        var timer = new Godot.Timer { OneShot = true, WaitTime = 2.0 };
        timer.Timeout += () =>
        {
            var result = DependencyScanner.Scan();
            if (result.Issues.Count > 0)
            {
                PluginLog.Info($"启动检测发现 {result.Issues.Count} 项前置问题，提示用户（不自动下载）");
                // autoFix=false：只提示，下载由用户在窗口内手动点「一键下载」
                DependencyDialog.Show(result.Issues, autoFix: false);
            }
            else
            {
                // 无问题：保持静默，不弹窗
                PluginLog.Info("启动检测：前置依赖全部满足，不弹窗");
            }
            timer.QueueFree();
        };
        tree.Root.AddChild(timer);
        timer.Start();
    }

    public static void OpenWindow()
    {
        var result = DependencyScanner.Scan();
        DependencyDialog.Show(result.Issues, autoFix: false);
    }
}
