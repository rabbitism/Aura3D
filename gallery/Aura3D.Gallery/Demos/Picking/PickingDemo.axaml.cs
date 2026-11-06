using Aura3D.Avalonia;
using Aura3D.Core.Nodes;
using Aura3D.Gallery.Localization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

namespace Aura3D.Gallery.Demos;

/// <summary>
/// 拾取页的视图：文案、参数与命令全在 <see cref="PickingViewModel"/>，XAML 经绑定直连。
/// 拾取是强视图交互，留在本类——<see cref="ViewAttached"/> 里订阅 ObjectPicked、自挂
/// PointerPressed 按点击位置现算全命中，Update 里按「中心射线」开关逐帧从视口正中拾取，
/// 结果都推进 VM 的报告方法；坐标是 DIP（视图内部才乘 RenderScaling），<c>e.GetPosition(view)</c>
/// 直接用。视口上的命中点标记画在 XAML 的 Overlay Canvas 上。五个目标球、挡板与实例
/// 方阵是 VM 持有的引擎模型；相机与灯是纯布景，仍在 BuildScene 里组装。
/// </summary>
public sealed partial class PickingDemo : Demo
{
    private readonly PickingViewModel viewModel = new();

    private Aura3DView? view;

    // 「中心射线」开关的视图侧影子：逐帧 Update 与 PointerPressed 都要读它判定走哪条拾取路径，
    // 经 CenterRayToggled 事件从 VM 转写过来，避免这两处每次去摸 VM 的属性。
    private bool centerMode;

    /// <summary>
    /// 建页：装配 XAML 并把 DataContext 换成 VM（顶替基类的 DataContext=this），再接好
    /// 三条事件桥——EnablePicking 写视图属性、中心射线进影子变量、结果/挡板/方阵变化排重绘。
    /// </summary>
    /// <param name="context">宿主环境。</param>
    public PickingDemo(DemoContext context) : base(context)
    {
        InitializeComponent();

        // 顶替基类的 DataContext=this：页面绑定从此指向 VM。
        DataContext = viewModel;

        // EnablePicking 是视图属性（Aura3DViewBase），VM 不碰 view 对象，经事件转写；
        // 视图尚未 attach 时先不落笔，ViewAttached 里会按 VM 当前值统一对齐。
        viewModel.EnablePickingToggled += value =>
        {
            if (view != null)
                view.EnablePicking = value;
        };

        viewModel.CenterRayToggled += value => centerMode = value;

        // 拾到结果、挡板可见与方阵边长变化都要排一帧重绘（等价旧处理器里的 InvalidateRender）。
        viewModel.FrameRequested += () => Context.InvalidateRender();
    }

    /// <inheritdoc />
    public override void ViewAttached(Aura3DView created)
    {
        view = created;

        // 对齐：构造到 attach 之间若有人拨过 EnablePicking，这里按 VM 的当前值补写一次
        // （同 Particles 页对 ShowBounds 的做法），旧页这一段会被静默丢写。
        created.EnablePicking = viewModel.EnablePicking;

        // ObjectPicked 只回最近一个命中，且事件参数里没有屏幕坐标；
        // 全命中列表要靠下面的 PointerPressed 按点击位置现算。
        created.ObjectPicked += (_, e) => viewModel.ReportObjectPicked(e.PickResult);

        created.PointerPressed += OnPointerPressed;
    }

    /// <inheritdoc />
    public override Task LoadAssetsAsync(AssetBatch assets) => Task.CompletedTask;

    /// <inheritdoc />
    public override void BuildScene()
    {
        var scene = Context.Scene!;

        scene.MainCamera.Position = new Vector3(0, 4.5f, 12f);
        scene.MainCamera.LookAt(new Vector3(0, 1.4f, 0));

        var light = new DirectionalLight { LightColor = System.Drawing.Color.White };

        light.RotationDegrees = new Vector3(-45f, -25f, 0);

        scene.AddNode(light);

        // 目标球、挡板与实例方阵都是 VM 持有的引擎模型（开关/滑杆的写穿目标），这里只做挂接。
        foreach (var target in viewModel.Targets)
            scene.AddNode(target);

        scene.AddNode(viewModel.Blocker);
        scene.AddNode(viewModel.Field);
    }

    /// <inheritdoc />
    public override void Update(double deltaTime)
    {
        if (centerMode)
        {
            var center = new Point(0, 0);

            if (view != null)
                center = new Point(view.Bounds.Width / 2, view.Bounds.Height / 2);

            var hits = view?.PickAt(center.X, center.Y) ?? [];
            var closest = hits.OrderBy(h => h.Distance).FirstOrDefault();

            PlaceMarker(center);

            // 顺序照旧：先报告后登记命中数——命中行里的「该点命中 N 个」落后一帧，与旧代码一致。
            if (closest != null)
                viewModel.ReportPick(closest, "PickAt");
            else
                viewModel.ReportCenterMiss();

            viewModel.NoteHitCount(hits.Count);
        }

        Context.RequestFrame();
    }

    /// <inheritdoc />
    public override void Unload()
    {
        if (view != null)
            view.PointerPressed -= OnPointerPressed;

        view = null;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (view == null || centerMode)
            return;

        var position = e.GetPosition(view);
        var hits = view.PickAt(position.X, position.Y);
        var closest = hits.OrderBy(h => h.Distance).FirstOrDefault();

        PlaceMarker(position);

        // 点击路径与中心射线相反：先登记命中数再报告，报告里的 N 即本次点击的真实值。
        viewModel.NoteHitCount(hits.Count);

        if (closest != null)
            viewModel.ReportPick(closest, Strings.Keys.Picking_SourceClick.T());
    }

    private void PlaceMarker(Point position)
    {
        Canvas.SetLeft(Marker, position.X - Marker.Width / 2);
        Canvas.SetTop(Marker, position.Y - Marker.Height / 2);
    }
}
