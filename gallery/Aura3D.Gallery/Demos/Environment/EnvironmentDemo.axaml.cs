using Aura3D.Core.Geometries;
using Aura3D.Core.Nodes;
using Aura3D.Core.Resources;
using Aura3D.Gallery.Kit;
using System.Numerics;
using System.Threading.Tasks;

namespace Aura3D.Gallery.Demos;

/// <summary>
/// 环境图与 IBL 页的视图：参数与命令全在 <see cref="EnvironmentViewModel"/>，XAML 经绑定
/// 直连。本类只保留生命周期——资产阶段取 HDRI 全景与六面天空盒喂 VM、在渲染线程回调里组装
/// 场景（相机/地面；平行光与五颗组合球本体住在 VM）、把 VM 的三项 Settings 值事件转写进宿主
/// （VM 不碰 DemoContext，照 PostProcessing 页的做法）、逐帧驱动自转与 Readout。
/// 换全景图/面边长直接写穿到 VM 持有的场景；引擎侧的环境来源与 IBL 缓存语义见 VM 注释。
/// </summary>
public sealed partial class EnvironmentDemo : Demo
{
    private readonly EnvironmentViewModel viewModel;

    /// <summary>
    /// 建页：先按宿主当前状态建 VM（参数初值对齐 Settings），装配 XAML 后把 DataContext
    /// 换成 VM。仍须显式赋值——外壳继承下来的 DataContext 优先于默认值；
    /// 基类先设的「页面自身」在这里被 VM 顶替。
    /// </summary>
    /// <param name="context">宿主环境。</param>
    public EnvironmentDemo(DemoContext context) : base(context)
    {
        viewModel = new(context.Settings);

        InitializeComponent();

        // 顶替基类的 DataContext=this：页面绑定从此指向 VM。
        DataContext = viewModel;

        // 参数转写：宿主的 PipelineSettings 只有视图够得着。VM 的三项写穿属性把新值经事件
        // 送过来，这里逐项写进 Settings——与旧的 ValueChanged 处理器逐行等价（取 float）。
        viewModel.IblAmbientIntensityChanged += value => Context.Settings.IblAmbientIntensity = (float)value;
        viewModel.ToneMappingExposureChanged += value => Context.Settings.ToneMappingExposure = (float)value;
        viewModel.BrightnessClampChanged += value => Context.Settings.BrightnessClamp = (float)value;

        // 换图/换面边长/重烘/参数写完都要排一帧重绘。
        viewModel.FrameRequested += () => Context.InvalidateRender();

        // 整页重建只有宿主干得了：换管线要重走「建视图 → 取资产 → 建场景」。
        viewModel.ReloadRequested += () => Context.Reload(Context.RequestedPipeline);
    }

    /// <inheritdoc />
    public override async Task LoadAssetsAsync(AssetBatch assets)
    {
        // 等距柱状全景：烘之前先整张解出来，一次烘完就是立方图。
        var panorama = await assets.HdrTextureAsync("Hdr1k");

        // 六面天空盒：helper 内部按 AssetManifest.SkyboxKeys 的顺序取六张 jpg。
        var cube = await assets.CubeTextureAsync();

        // 取完喂 VM（PbrMaterials 页 SetTextures 的先例）；建场景时按当前选择落成 Background。
        viewModel.SetEnvironmentAssets(panorama, cube);
    }

    /// <inheritdoc />
    public override void BuildScene()
    {
        var scene = Context.Scene!;

        scene.MainCamera.Position = new Vector3(0, 2.4f, 9.5f);
        scene.MainCamera.LookAt(new Vector3(0, 1.2f, 0));
        // 40×40 地面 + 拉远余量，抬过默认 far 100。
        scene.MainCamera.FarPlane = 150f;

        // 场景递给 VM：先把当前全景选择落成 Background（旧 ApplyEnvironment 的初落地），
        // 之后换图/背景开关/重烘都直接作用在它上面。
        viewModel.AttachScene(scene);

        scene.AddNode(viewModel.Sun);

        var ground = new Mesh
        {
            Name = "Ground",
            Geometry = new PlaneGeometry(40f, 40f),
            Material = new Material(),
        };

        ground.Material.SetTexture("BaseColor", Procedural.Checker(256, 18));

        scene.AddNode(ground);

        scene.AddNode(viewModel.BallRow);

        // 旧代码在这里还调一次 Report() 预填读数；现在读数是 VM 的 observable，
        // 同一帧的 Update 会先调 Tick()，显示时机不变。
    }

    /// <inheritdoc />
    public override void Update(double deltaTime)
    {
        // 组合球排的展示自转（8 度/秒），让反射随角度流动；球排是 VM 持有的引擎对象，
        // 构造起就在，不需要旧代码那层空调试。
        var row = viewModel.BallRow;

        row.RotationDegrees = new Vector3(0, row.RotationDegrees.Y + 8f * (float)deltaTime, 0);

        viewModel.Tick();

        Context.RequestFrame();
    }
}
