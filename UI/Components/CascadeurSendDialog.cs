using Animanager.Core.Plugins;
using Animanager.UI.Shell;
using Godot;
using Quaternion = System.Numerics.Quaternion;
using Axis = System.Numerics.Vector3;
using static Animanager.UI.Controls.AppControls;

namespace Animanager.UI.Components;

internal sealed partial class CascadeurSendDialog : ConfirmationDialog
{
    private static readonly (string Label, Axis Direction)[] Axes =
    [
        ("+X", Axis.UnitX), ("−X", -Axis.UnitX),
        ("+Y", Axis.UnitY), ("−Y", -Axis.UnitY),
        ("+Z", Axis.UnitZ), ("−Z", -Axis.UnitZ)
    ];

    public Task<Quaternion?> Selection => _selection.Task;
    private readonly TaskCompletionSource<Quaternion?> _selection = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CascadeurSendDialog(CascadeurBindPose pose, string clip)
    {
        Name = "CascadeurSendDialog";
        Title = "发送到 Cascadeur";
        OkButtonText = "发送";
        CancelButtonText = "取消";
        DialogHideOnOk = false;
        GetOkButton().ThemeTypeVariation = "PrimaryButton";

        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, FollowFocus = true };
        AddChild(scroll);
        var content = Column(scroll);
        content.CustomMaximumSize = new(560, -1);
        content.PropagateMaximumSize = true;
        content.AddThemeConstantOverride("separation", 8);
        var clipLabel = Wrapped(clip, muted: false);
        content.AddChild(clipLabel);

        var mode = new OptionButton { Name = "CascadeurOrientationMode", FitToLongestItem = false };
        mode.AddItem("自动 · 使用 GLB 场景坐标");
        mode.AddItem("高级 · 手动指定轴");
        content.AddChild(mode);

        var axisRow = new HFlowContainer();
        axisRow.AddThemeConstantOverride("h_separation", 8);
        content.AddChild(axisRow);
        var up = AxisChoice(axisRow, "向上轴", "CascadeurUpAxis", 2);
        var forward = AxisChoice(axisRow, "朝前轴", "CascadeurForwardAxis", 4);

        var preview = new CascadeurBindPreview { Name = "CascadeurBindPreview", Pose = pose, CustomMinimumSize = new(0, 180) };
        content.AddChild(preview);
        var status = Wrapped("");
        content.AddChild(status);

        Quaternion? orientation = Quaternion.Identity;
        void Refresh()
        {
            bool manual = mode.Selected == 1;
            axisRow.Visible = manual;
            var upAxis = Axes[up.Selected].Direction;
            var forwardAxis = Axes[forward.Selected].Direction;
            bool valid = !manual || Axis.Dot(upAxis, forwardAxis) == 0;
            orientation = valid ? (manual ? CascadeurBindPose.MapAxes(upAxis, forwardAxis) : Quaternion.Identity) : null;
            preview.Orientation = orientation;
            preview.QueueRedraw();
            bool? upright = orientation is { } rotation ? pose.IsUpright(rotation) : null;
            GetOkButton().Disabled = !valid || upright == false;
            status.Text = !valid ? "向上轴和朝前轴必须垂直。"
                : upright == false ? "绑定姿态未沿 +Y 站立，请调整坐标轴。"
                : pose.Joints.Length == 0 ? "没有可预览的蒙皮绑定姿态，可以发送动画。"
                : "预览为发送后的绑定姿态。Cascadeur 的向上轴为 +Y。";
        }

        mode.ItemSelected += _ => Refresh();
        up.ItemSelected += _ => Refresh();
        forward.ItemSelected += _ => Refresh();
        Confirmed += () => { if (!GetOkButton().Disabled) _selection.TrySetResult(orientation); };
        Canceled += () => _selection.TrySetResult(null);
        CloseRequested += () => _selection.TrySetResult(null);
        Refresh();
    }

    private static OptionButton AxisChoice(Container row, string label, string name, int selected)
    {
        var group = Row(row);
        group.AddChild(Label(label));
        var choice = new OptionButton { Name = name };
        foreach (var axis in Axes) choice.AddItem(axis.Label);
        choice.Select(selected);
        group.AddChild(choice);
        return choice;
    }
}

internal sealed partial class CascadeurBindPreview : Control
{
    public CascadeurBindPose Pose { get; init; } = CascadeurBindPose.Empty;
    public Quaternion? Orientation { get; set; } = Quaternion.Identity;

    public override void _Ready() => Resized += QueueRedraw;

    public override void _Draw()
    {
        float width = (Size.X - 8) / 2;
        if (width < 48 || Size.Y < 48) return;
        DrawView(new Rect2(0, 0, width, Size.Y), "正面  +Z", side: false);
        DrawView(new Rect2(width + 8, 0, width, Size.Y), "侧面  +X", side: true);
    }

    private void DrawView(Rect2 bounds, string label, bool side)
    {
        DrawRect(bounds, Tokens.Raised);
        DrawString(GetThemeDefaultFont(), bounds.Position + new Vector2(10, 19), label, fontSize: 12, modulate: Tokens.Text2);
        var axisStart = new Vector2(bounds.Position.X + 15, bounds.End.Y - 13);
        DrawLine(axisStart, axisStart + new Vector2(0, -18), Tokens.Text3);
        DrawString(GetThemeDefaultFont(), axisStart + new Vector2(-6, -21), "+Y", fontSize: 10, modulate: Tokens.Text3);
        if (Pose.Joints.Length == 0 || Orientation is not { } rotation) return;

        var points = Pose.Joints.Select(joint =>
        {
            var p = Axis.Transform(joint.Position, rotation);
            return new Vector2(side ? -p.Z : p.X, -p.Y);
        }).ToArray();
        var min = new Vector2(points.Min(p => p.X), points.Min(p => p.Y));
        var max = new Vector2(points.Max(p => p.X), points.Max(p => p.Y));
        var center = bounds.GetCenter() + new Vector2(0, 12);
        float scale = Mathf.Min((bounds.Size.X - 40) / Mathf.Max(.1f, max.X - min.X),
            (bounds.Size.Y - 43) / Mathf.Max(.1f, max.Y - min.Y));
        for (int i = 0; i < points.Length; i++) points[i] = center + (points[i] - (min + max) / 2) * scale;

        for (int i = 0; i < points.Length; i++)
        {
            int parent = Pose.Joints[i].Parent;
            if (parent >= 0) DrawLine(points[parent], points[i], Tokens.Text2, 1.5f, true);
            DrawCircle(points[i], 2.25f, Tokens.Accent, antialiased: true);
        }
    }
}
