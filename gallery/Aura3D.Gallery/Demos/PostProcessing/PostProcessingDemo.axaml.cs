using Aura3D.Core.Geometries;
using Aura3D.Core.Nodes;
using Aura3D.Core.Resources;
using Aura3D.Gallery.Kit;
using System.Numerics;
using System.Threading.Tasks;

namespace Aura3D.Gallery.Demos;

/// <summary>
/// 管线设置页的视图：参数与命令全在 <see cref="PostProcessingViewModel"/>，XAML 经绑定直连。
/// 本类只保留生命周期——建场景（相机/地面/亮度球组/点光；平行光本体住在 VM）、把 VM 的参数
/// 事件转写进宿主的 <c>PipelineSettings</c>（VM 不碰 DemoContext，参照 Particles 页的做法）、
/// 逐帧驱动 Readout 与渲染。页面要演示的「逐帧生效 vs 必须重建」分界见 VM 的参数注释。
/// </summary>
public sealed partial class PostProcessingDemo : Demo
{
    private readonly PostProcessingViewModel viewModel;

    private Node? brightRow;

    /// <summary>
    /// 建页：先按宿主当前状态建 VM（参数初值对齐 Settings、Readout 记住当前管线），
    /// 装配 XAML 后把 DataContext 换成 VM。仍须显式赋值——外壳继承下来的 DataContext
    /// 优先于默认值；基类先设的「页面自身」在这里被 VM 顶替。
    /// </summary>
    /// <param name="context">宿主环境。</param>
    public PostProcessingDemo(DemoContext context) : base(context)
    {
        viewModel = new(context.Settings, context.RequestedPipeline);

        InitializeComponent();

        // 顶替基类的 DataContext=this：页面绑定从此指向 VM。
        DataContext = viewModel;

        // 参数转写：宿主的 PipelineSettings 只有视图够得着。VM 的每个写穿属性把新值经事件
        // 送过来，这里逐项写进 Settings——与旧的 ValueChanged/Toggled 处理器逐行等价
        // （含取 float/int 的口径；逐帧区随后的重绘请求走下面的 FrameRequested）。
        viewModel.ToneMappingExposureChanged += value => Context.Settings.ToneMappingExposure = (float)value;
        viewModel.BrightnessClampChanged += value => Context.Settings.BrightnessClamp = (float)value;
        viewModel.AmbientIntensityChanged += value => Context.Settings.AmbientIntensity = (float)value;
        viewModel.IblAmbientIntensityChanged += value => Context.Settings.IblAmbientIntensity = (float)value;
        viewModel.FxaaToggled += value => Context.Settings.EnableFxaa = value;
        viewModel.FrustumCullingToggled += value => Context.Settings.EnableFrustumCulling = value;
        viewModel.DirectionalLightLimitChanged += value => Context.Settings.DirectionalLightLimit = (int)value;
        viewModel.PointLightLimitChanged += value => Context.Settings.PointLightLimit = (int)value;
        viewModel.SpotLightLimitChanged += value => Context.Settings.SpotLightLimit = (int)value;
        viewModel.CsmCascadeCountChanged += value => Context.Settings.CsmCascadeCount = (int)value;
        viewModel.CsmSplitLambdaChanged += value => Context.Settings.CsmSplitLambda = (float)value;
        viewModel.CsmShadowMapResolutionChanged += value => Context.Settings.CsmShadowMapResolution = (int)value;

        // 逐帧区参数（含平行光投影开关）写完要排一帧重绘；下半区参数只在重建时被读，不必重绘。
        viewModel.FrameRequested += ApplyLive;

        // 整页重建只有宿主干得了：换着色器变体与纹理尺寸要重走「建视图 → 取资产 → 建场景」。
        viewModel.ReloadRequested += () => Context.Reload(Context.RequestedPipeline);
    }

    /// <inheritdoc />
    public override Task LoadAssetsAsync(AssetBatch assets) => Task.CompletedTask;

    /// <inheritdoc />
    public override void BuildScene()
    {
        var scene = Context.Scene!;

        scene.MainCamera.Position = new Vector3(0, 3.4f, 13f);
        scene.MainCamera.LookAt(new Vector3(0, 1.6f, 0));
        // 70×70 地面 + 拉远余量，默认 far 100 不够；按本页尺度抬到 220。
        scene.MainCamera.FarPlane = 220f;

        scene.AddNode(viewModel.Sun);

        var ground = new Mesh
        {
            Name = "Ground",
            Geometry = new PlaneGeometry(70f, 70f),
            Material = new Material(),
        };

        ground.Material.SetTexture("BaseColor", Procedural.Checker(256, 24));

        scene.AddNode(ground);

        brightRow = new Node { Name = "Bright" };

        scene.AddNode(brightRow);

        // 三档亮度递增的球，用来观察曝光与亮度上限各自截在哪里。
        for (int i = 0; i < 3; i++)
        {
            var mesh = new Mesh
            {
                Name = $"Ball{i}",
                Geometry = new SphereGeometry(1f, 40, 24),
                Material = new Material(),
            };

            mesh.Material.SetTexture("BaseColor", Procedural.Checker(128, 2 + i * 3));
            mesh.Position = new Vector3((i - 1) * 2.8f, 1.4f, -1.4f);

            brightRow.AddChild(mesh, AttachToParentRule.KeepLocal);
        }

        // 两盏点光，验证点光上限与实际可画的光数：上限低于场景里的灯数时后面的灯会被丢掉。
        for (int i = 0; i < 2; i++)
        {
            var point = new PointLight
            {
                LightColor = i == 0 ? System.Drawing.Color.DeepSkyBlue : System.Drawing.Color.OrangeRed,
                LuminousIntensity = 9000,
                AttenuationRadius = 12f,
            };

            point.Position = new Vector3(i == 0 ? -4.5f : 4.5f, 2.6f, 2.2f);

            scene.AddNode(point);
        }

        // 旧代码在这里还调一次 Report() 预填读数；现在读数是 VM 的 observable，
        // 同一帧的 Update 会先调 Tick()，显示时机不变。
        ApplyLive();
    }

    /// <inheritdoc />
    public override void Update(double deltaTime)
    {
        if (brightRow != null)
            brightRow.RotationDegrees = new Vector3(0, (float)(deltaTime * 6) + brightRow.RotationDegrees.Y, 0);

        viewModel.Tick();

        Context.RequestFrame();
    }

    // Settings 这个实例在 CreateView 时就交给了视图与管线，改字段即改配置，只需要排一帧重绘。
    private void ApplyLive() => Context.InvalidateRender();
}
