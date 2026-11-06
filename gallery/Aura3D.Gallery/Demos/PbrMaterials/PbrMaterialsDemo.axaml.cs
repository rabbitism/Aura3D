using Aura3D.Core.Geometries;
using Aura3D.Core.Nodes;
using Aura3D.Core.Resources;
using Aura3D.Gallery.Kit;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;

namespace Aura3D.Gallery.Demos;

/// <summary>
/// PBR 材质页的视图，MVVM 化（模式照抄 Particles）：面板文案与三颗材质球住在
/// <see cref="PbrMaterialsViewModel"/>（选材口径与贴图接线的说明也见那边），本类只留生命周期——
/// 资产阶段取天空盒与九张贴图喂 VM，渲染线程回调里组场景（天空盒/定向光/地面/相机），逐帧自转并请求重绘。
/// 环境挂六面天空盒：金属反射没有 IBL 就是一坨死灰，环境立方图是这页能不能看的前提。
/// </summary>
public sealed partial class PbrMaterialsDemo : Demo
{
    private readonly PbrMaterialsViewModel viewModel = new();

    private CubeTexture environment = null!;

    /// <summary>
    /// 建页：装配 XAML 并把 DataContext 换成 VM。仍须显式赋值——外壳继承下来的
    /// DataContext 优先于默认值；基类先设的「页面自身」在这里被 VM 顶替。
    /// </summary>
    /// <param name="context">宿主环境。</param>
    public PbrMaterialsDemo(DemoContext context) : base(context)
    {
        InitializeComponent();

        DataContext = viewModel;
    }

    /// <inheritdoc />
    public override async Task LoadAssetsAsync(AssetBatch assets)
    {
        environment = await assets.CubeTextureAsync();

        // 按 VM 展开的键序取九张贴图（每套 base/normal/arm），喂回去由 VM 组材质与球体。
        var textures = new Dictionary<string, Texture>();

        foreach (var key in viewModel.TextureKeys)
            textures[key] = await assets.TextureAsync(key);

        viewModel.SetTextures(textures);
    }

    /// <inheritdoc />
    public override void BuildScene()
    {
        var scene = Context.Scene!;

        // 默认 IBL 强度是按白天空调的，介质球会被烘成一片白蒙蒙；
        // 压到 0.45 让定向光的高光与阴影把三档材质拉开。
        Context.Settings.IblAmbientIntensity = 0.45f;

        scene.ShowGrid = true;
        // 取景中心略偏右：把三颗球整体挪离右侧参数面板的遮挡区。
        scene.MainCamera.Position = new Vector3(0.5f, 2.2f, 9.2f);
        scene.MainCamera.LookAt(new Vector3(0.5f, 1.4f, 0));
        // 30×30 地面 + 拉远余量，抬过默认 far 100。
        scene.MainCamera.FarPlane = 150f;

        // 环境立方图：IBL 只认 Scene.Background 的立方图分支。挂上就行——
        // 渲染先于本回调（Update → Render → SceneUpdated），首帧烘的其实是引擎那张默认纯白立方图，
        // 而 Scene.Background 的 setter 会自动把两张烘焙缓存作废，下一帧就用这个天空盒重烘。
        scene.Background = environment;

        var sun = new DirectionalLight
        {
            LightColor = System.Drawing.Color.White,
            CastShadow = true,
        };

        sun.RotationDegrees = new Vector3(-40f, -30f, 0);

        scene.AddNode(sun);

        var ground = new Mesh
        {
            Name = "Ground",
            Geometry = new PlaneGeometry(30f, 30f),
            Material = new Material(),
        };

        ground.Material.SetTexture("BaseColor", Procedural.Checker(256, 16));

        scene.AddNode(ground);

        foreach (var sphere in viewModel.Spheres)
            scene.AddNode(sphere);
    }

    /// <inheritdoc />
    public override void Update(double deltaTime)
    {
        foreach (var sphere in viewModel.Spheres)
            sphere.RotationDegrees = new Vector3(0, sphere.RotationDegrees.Y + 12f * (float)deltaTime, 0);

        Context.RequestFrame();
    }
}
