using Aura3D.Core.Geometries;
using Aura3D.Core.Nodes;
using Aura3D.Core.Resources;
using Aura3D.Gallery.Kit;
using System;
using System.Numerics;
using System.Threading.Tasks;

namespace Aura3D.Gallery.Demos;

/// <summary>
/// 阴影页的视图：参数与命令全在 <see cref="ShadowsViewModel"/>，XAML 经绑定直连。
/// 本类只保留生命周期——在渲染线程回调里组装场景（相机/地面/立柱/球；两盏方向光本体住在 VM）、
/// 把 VM 的 Settings 值事件逐项转写进宿主的 <c>PipelineSettings</c>（VM 不碰 DemoContext，照抄
/// PostProcessing 页的做法）、逐帧驱动 Readout 与渲染。主光级联（CSM）与单张正交图两条路径的
/// 参数分界见 VM 的属性注释。
/// </summary>
public sealed partial class ShadowsDemo : Demo
{
    private readonly ShadowsViewModel viewModel;

    private Node? casters;

    /// <summary>
    /// 建页：先按宿主当前状态建 VM（参数初值对齐 Settings 与引擎默认、Readout 记住当前管线），
    /// 装配 XAML 后把 DataContext 换成 VM。仍须显式赋值——外壳继承下来的 DataContext
    /// 优先于默认值；基类先设的「页面自身」在这里被 VM 顶替。
    /// </summary>
    /// <param name="context">宿主环境。</param>
    public ShadowsDemo(DemoContext context) : base(context)
    {
        viewModel = new(context.Settings, context.RequestedPipeline);

        InitializeComponent();

        // 顶替基类的 DataContext=this：页面绑定从此指向 VM。
        DataContext = viewModel;

        // 参数转写：宿主的 PipelineSettings 只有视图够得着。VM 的每个写穿属性把新值经事件
        // 送过来，这里逐项写进 Settings——与旧的 ValueChanged/Toggled 处理器逐行等价
        // （含取 float/int 与按 256 取整的口径）。
        viewModel.CsmCascadeCountChanged += value => Context.Settings.CsmCascadeCount = (int)Math.Round(value);
        viewModel.CsmSplitLambdaChanged += value => Context.Settings.CsmSplitLambda = (float)value;
        viewModel.CsmShadowMapResolutionChanged += value =>
            Context.Settings.CsmShadowMapResolution = (int)Math.Round(value / 256) * 256;
        viewModel.DirectionalLightLimitChanged += value =>
            Context.Settings.DirectionalLightLimit = (int)Math.Round(value);
        viewModel.ShowDirectionalLightToggled += value => Context.Settings.Debug.ShowDirectionalLight = value;
        viewModel.ShowBoundingBoxToggled += value => Context.Settings.Debug.ShowBoundingBox = value;

        // 面板里这两个开关画的是勾选态，这里把设置同步成一样的，免得面板与真值不符
        // （旧构造函数里那两句硬编码 true 的等价物，初值改从 VM 读）。
        Context.Settings.Debug.ShowDirectionalLight = viewModel.ShowDirectionalLight;
        Context.Settings.Debug.ShowBoundingBox = viewModel.ShowBoundingBox;

        // 本页的 Settings 参数（含光源上限）都被 ShadowMapPass 逐帧读，写完要排一帧重绘。
        viewModel.FrameRequested += Context.InvalidateRender;

        // 整页重建只有宿主干得了：跨过 ENABLE_CSM 的着色器界线要重走「建视图 → 取资产 → 建场景」。
        viewModel.ReloadRequested += () => Context.Reload(Context.RequestedPipeline);
    }

    /// <inheritdoc />
    public override Task LoadAssetsAsync(AssetBatch assets) => Task.CompletedTask;

    /// <inheritdoc />
    public override void BuildScene()
    {
        var scene = Context.Scene!;

        // 场景交给 VM：显式主光开关与相机 FarPlane 的写穿目标是场景成员。
        // 旧代码在这里调的 ApplyShadowConfig/主光旋转已在 VM 构造期对齐，滑杆的后续写也
        // 落在 VM 持有的灯对象上，建场景时自然带着最新值。
        viewModel.AttachScene(scene);

        scene.MainCamera.Position = new Vector3(0, 6f, 26f);
        scene.MainCamera.LookAt(new Vector3(0, 0.6f, -6f));
        scene.MainCamera.SetClippingPlanes(0.4f, (float)viewModel.CameraFar);

        scene.AddNode(viewModel.Main);

        scene.AddNode(viewModel.Second);

        var ground = new Mesh
        {
            Name = "Ground",
            Geometry = new PlaneGeometry(160f, 160f),
            Material = new Material(),
        };

        ground.Material.SetTexture("BaseColor", Procedural.Checker(512, 40));

        scene.AddNode(ground);

        casters = new Node { Name = "Casters" };

        scene.AddNode(casters);

        // 三组不同距离的立柱：近处、中景、地平线附近，级联边界会在这三段上表现得很清楚。
        AddPillars(6f, 1, new Vector3(-3f, 0, 6f));
        AddPillars(14f, 3, new Vector3(-6f, 0, -8f));
        AddPillars(26f, 6, new Vector3(-14f, 0, -34f));

        var sphere = new Mesh
        {
            Name = "Ball",
            Geometry = new SphereGeometry(1.6f, 44, 24),
            Material = new Material(),
        };

        sphere.Material.SetTexture("BaseColor", Procedural.VerticalGradient(
            new Procedural.Rgb(230, 230, 240), new Procedural.Rgb(120, 130, 160)));
        sphere.Position = new Vector3(4f, 1.6f, 2f);

        casters.AddChild(sphere, AttachToParentRule.KeepLocal);

        // 旧代码在这里还调一次 Report() 预填读数；现在读数是 VM 的 observable，
        // 同一帧的 Update 会先调 Tick()，显示时机不变。
    }

    /// <inheritdoc />
    public override void Update(double deltaTime)
    {
        viewModel.Tick();

        Context.RequestFrame();
    }

    private void AddPillars(float spacing, int count, Vector3 origin)
    {
        if (casters == null)
            return;

        for (int i = 0; i < count; i++)
        {
            var height = 1.6f + (i % 3) * 0.9f;

            var pillar = new Mesh
            {
                Name = $"P{count}-{i}",
                Geometry = new BoxGeometry(0.9f, height, 0.9f),
                Material = new Material(),
            };

            pillar.Material.SetTexture("BaseColor", Procedural.Checker(64, 2));
            pillar.Position = origin + new Vector3(i * spacing, height / 2f, 0);

            casters.AddChild(pillar, AttachToParentRule.KeepLocal);
        }
    }
}
