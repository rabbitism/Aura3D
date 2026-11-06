using Aura3D.Core.Geometries;
using Aura3D.Core.Nodes;
using Aura3D.Core.Resources;
using Aura3D.Gallery.Kit;
using System.Numerics;
using System.Threading.Tasks;

namespace Aura3D.Gallery.Demos;

/// <summary>
/// 背景来源与天空盒页的视图：文案、参数与命令全在 <see cref="SkyboxBackgroundViewModel"/>，
/// XAML 经绑定直连。本类只保留生命周期——取三份背景素材喂 VM、在渲染线程回调里组装场景
/// （地面/方柱/反射球是纯布景，平行光住在 VM）、把两根 Settings 滑杆的值事件转写进宿主
/// （VM 不碰 DemoContext，参照 Particles/PostProcessing 页的做法）、逐帧转球、驱动 Readout 与渲染。
/// 背景的两条绘制分支（立方图按视线采样、平面图铺满窗口拉伸）与投影参数的不对称口径
/// 见检视面板里的说明行。
/// </summary>
public sealed partial class SkyboxBackgroundDemo : Demo
{
    private readonly SkyboxBackgroundViewModel viewModel;

    private Mesh? ball;

    /// <summary>
    /// 建页：先按宿主当前状态建 VM（两根 Settings 滑杆的初值取自宿主），装配 XAML 后把
    /// DataContext 换成 VM。仍须显式赋值——外壳继承下来的 DataContext 优先于默认值；
    /// 基类先设的「页面自身」在这里被 VM 顶替。
    /// </summary>
    /// <param name="context">宿主环境。</param>
    public SkyboxBackgroundDemo(DemoContext context) : base(context)
    {
        viewModel = new(context.Settings);

        InitializeComponent();

        // 顶替基类的 DataContext=this：页面绑定从此指向 VM。
        DataContext = viewModel;

        // 宿主的 PipelineSettings 只有视图够得着：两根滑杆的新值经事件逐项转写进去
        //（取 float 的口径与旧处理器一致），随后的重绘请求走下面的 FrameRequested。
        viewModel.IblAmbientIntensityChanged += value => Context.Settings.IblAmbientIntensity = (float)value;
        viewModel.ToneMappingExposureChanged += value => Context.Settings.ToneMappingExposure = (float)value;

        viewModel.FrameRequested += () => Context.InvalidateRender();
    }

    /// <inheritdoc />
    public override async Task LoadAssetsAsync(AssetBatch assets)
    {
        // 六面按 CubeMapFace 顺序取，helper 内部用的就是 AssetManifest.SkyboxKeys。
        viewModel.SetBackgroundAssets(
            await assets.CubeTextureAsync(),
            await assets.HdrTextureAsync("Hdr1k"),
            await assets.TextureAsync("BackgroundJpg"));
    }

    /// <inheritdoc />
    public override void BuildScene()
    {
        var scene = Context.Scene!;

        scene.MainCamera.Position = new Vector3(0, 3.2f, 13f);
        scene.MainCamera.LookAt(new Vector3(0, 1.4f, 0));

        // 相机四参数、背景与画背景开关在登记时统一写穿（旧 BuildScene 里的
        // ApplyCamera + ApplyBackground 挪进了 VM）。
        viewModel.AttachScene(scene);

        scene.AddNode(viewModel.Sun);

        var ground = new Mesh
        {
            Name = "Ground",
            Geometry = new PlaneGeometry(60f, 60f),
            Material = new Material(),
        };

        ground.Material.SetTexture("BaseColor", Procedural.Checker(256, 24));

        scene.AddNode(ground);

        // 一排等宽不等高的方柱：透视下侧棱向内收，正交下保持平行，投影分支一眼可辨。
        for (int i = 0; i < 4; i++)
        {
            float height = 1.6f + i;

            var pillar = new Mesh
            {
                Name = $"Pillar{i}",
                Geometry = new BoxGeometry(1.2f, height, 1.2f),
                Material = new Material(),
            };

            pillar.Material.SetTexture(
                "BaseColor",
                Texture.CreateFromColor(System.Drawing.Color.FromArgb(255, 200 - i * 30, 150, 90 + i * 40)));

            pillar.Position = new Vector3(-5.5f + i * 1.9f, height / 2f, -4f);

            scene.AddNode(pillar);
        }

        // 反射球是本页唯一能看见「哪一支在喂 IBL」的地方。
        var mirror = new Material();

        mirror.SetTexture("BaseColor", Texture.CreateFromColor(System.Drawing.Color.White));
        mirror.SetTexture("MetallicRoughness", Procedural.MetalRoughSolid(1f, 0.06f));

        ball = new Mesh
        {
            Name = "MirrorBall",
            Geometry = new SphereGeometry(1.6f, 48, 32),
            Material = mirror,
        };

        ball.Position = new Vector3(2.6f, 1.7f, 1.5f);

        scene.AddNode(ball);

        // 旧代码在这里还调一次 Report() 预填读数；现在读数是 VM 的 observable，
        // 同一回调里随后的 Update 会先调 Tick()，显示时机不变。
    }

    /// <inheritdoc />
    public override void Update(double deltaTime)
    {
        // 球体慢速自转：让画面持续有帧，也方便看反射横扫过天空内容。
        if (ball != null)
            ball.RotationDegrees = new Vector3(0, ball.RotationDegrees.Y + 10f * (float)deltaTime, 0);

        viewModel.Tick();

        Context.RequestFrame();
    }
}
