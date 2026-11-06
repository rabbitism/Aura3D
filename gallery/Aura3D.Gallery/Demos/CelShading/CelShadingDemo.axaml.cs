using Aura3D.Pipeline.CelShading;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

namespace Aura3D.Gallery.Demos;

/// <summary>
/// 卡通着色页的视图：参数与读数全在 <see cref="CelShadingViewModel"/>，XAML 经绑定直连。
/// 六个通道、下划线参数与三档染色随开关/滑杆写穿到 VM 里的身体/脸两个卡通材质，
/// 定向光、地面与角色模型也住在 VM；本类只保留生命周期——取角色资产、建场景
/// （摆相机、挂节点、首套参数落地）、逐帧驱动、离开时清描边引用。材质参数名与通道名
/// 只有 CelLightPass 认，换管线就一个都不生效，所以本页锁管线（见 DemoRegistry 登记项）。
/// </summary>
public sealed partial class CelShadingDemo : Demo
{
    private readonly CelShadingViewModel viewModel = new();

    /// <summary>
    /// 建页：装配 XAML 并把 DataContext 换成 VM。仍须显式赋值——外壳继承下来的
    /// DataContext 优先于默认值；基类先设的「页面自身」在这里被 VM 顶替。
    /// </summary>
    /// <param name="context">宿主环境。</param>
    public CelShadingDemo(DemoContext context) : base(context)
    {
        InitializeComponent();

        // 顶替基类的 DataContext=this：页面绑定从此指向 VM。
        DataContext = viewModel;

        // Settings.AmbientIntensity 读的是引擎默认，VM 够不着 Context，
        // 由这里喂初值（等价旧 ctor 里对 SettingsAmbientRow.Value 的补写）。
        viewModel.SetEngineAmbient(Context.Settings.AmbientIntensity);

        // 描边 pass 实例只能按类型从宿主管线里翻出来（它唯一的公开途径）；
        // VM 不碰 DemoContext，经定位器委托桥过去，场景没建好时返回 null。
        viewModel.FindOutlinePass = () =>
            Context.Scene?.RenderPipeline.EveryCameraRenderPasses.OfType<OutlinePass>().FirstOrDefault();

        // 材质/参数变化要排一帧重绘；等价旧处理器末尾的 InvalidateRender。
        viewModel.FrameRequested += () => Context.InvalidateRender();

        // 引擎环境光写在宿主的 Settings 上，只有这里够得着。
        viewModel.SettingsAmbientChanged += value => Context.Settings.AmbientIntensity = value;
    }

    /// <inheritdoc />
    public override async Task LoadAssetsAsync(AssetBatch assets)
    {
        viewModel.SetCharacter(await assets.ModelAsync("CelCharacter"));
    }

    /// <inheritdoc />
    public override void BuildScene()
    {
        var scene = Context.Scene!;

        scene.MainCamera.Position = new Vector3(0, 2.75f, 5.25f);
        scene.MainCamera.LookAt(new Vector3(0, 2.4f, 0));
        // 30×30 地面 + 拉远余量，抬过默认 far 100。
        scene.MainCamera.FarPlane = 120f;

        scene.AddNode(viewModel.Sun);
        scene.AddNode(viewModel.Ground);

        // 卡渲角色 glb 放中央：它的材质没设卡通参数也照常渲染——
        // pass 有参考材质兜底，正好当「真实模型」对照
        if (viewModel.Character != null)
            scene.AddNode(viewModel.Character);

        // 首次把通道/参数/染色整套落到材质上，并接上描边 pass 的环境光。
        viewModel.ApplyAll();
    }

    /// <inheritdoc />
    public override void Update(double deltaTime)
    {
        // 定向光自转与读数重读都在 VM 里推进；渲染本身按旧路径逐帧请求。
        viewModel.Tick(deltaTime);

        Context.RequestFrame();
    }

    /// <inheritdoc />
    public override void Unload()
    {
        // 描边那个 pass 属于本页创建过的管线，离开时不留悬挂引用。
        viewModel.ClearOutlinePass();
    }
}
