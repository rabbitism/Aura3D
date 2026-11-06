using Aura3D.Core.Geometries;
using Aura3D.Core.Nodes;
using Aura3D.Core.Resources;
using Aura3D.Gallery.Kit;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Color = System.Drawing.Color;

namespace Aura3D.Gallery.Demos;

/// <summary>
/// glTF/GLB 模型页的视图，MVVM 化：参数、副本与三条读数全在 <see cref="ModelViewerViewModel"/>，
/// XAML 经绑定直连；本类只保留生命周期——进页即取档的资产装载、在渲染线程回调里组装相机/灯光/
/// 地面并登记已装载模型，以及页内按需档那条异步通道（VM 发 ModelRequested，这里取字节解码后
/// 回灌 VM）。库里的模型分两档取：进页即取的算进该页的下载量，页内按需的不算——这是 web 端
/// 控制首屏字节的办法；两档的模型表与「谁在生效」的状态都在 VM 上，视图不落一份。
/// </summary>
public sealed partial class ModelViewerDemo : Demo
{
    private readonly ModelViewerViewModel viewModel = new();

    // 按需档的下载解码走这个批处理（进度上报与按 Key 缓存都在里面），LoadAssetsAsync 时留下引用。
    private AssetBatch batch = null!;

    /// <summary>
    /// 建页：装配 XAML 并把 DataContext 换成 VM。仍须显式赋值——外壳继承下来的
    /// DataContext 优先于默认值；基类先设的「页面自身」在这里被 VM 顶替。
    /// </summary>
    /// <param name="context">宿主环境。</param>
    public ModelViewerDemo(DemoContext context) : base(context)
    {
        InitializeComponent();

        // 顶替基类的 DataContext=this：XAML 的绑定从此指向 VM。
        DataContext = viewModel;

        // XAML 里这个开关画的是勾选态，而引擎默认是关的：不进设置同步一次，
        // 一进页就看不到线框，面板与真值也对不上。
        Context.Settings.Debug.ShowBoundingBox = true;

        // 语义事件桥：VM 不碰 DemoContext，场景/相机/宿主设置的落点全在视图这边接。
        viewModel.ModelRequested += key => FetchModel(key);
        viewModel.CloneAdded += node => Context.Scene!.AddNode(node);
        viewModel.CloneRemoved += node => Context.Scene?.RemoveNode(node);
        viewModel.FitRequested += FitCamera;
        viewModel.ShowBoxForced += () => Context.Settings.Debug.ShowBoundingBox = true;
        viewModel.ShowBoxToggled += value => Context.Settings.Debug.ShowBoundingBox = value;
    }

    /// <inheritdoc />
    public override async Task LoadAssetsAsync(AssetBatch assets)
    {
        batch = assets;

        // 进页即取档：一次全取完（约 3 MB），页内换它们只切 Enable 不再下载。
        foreach (var (key, _, preload) in ModelViewerViewModel.Library.Where(i => i.Preload))
            viewModel.Preload(key, await assets.ModelAsync(key));
    }

    /// <inheritdoc />
    public override void BuildScene()
    {
        var scene = Context.Scene!;

        scene.ShowGrid = true;
        scene.MainCamera.Position = new Vector3(3.4f, 2.6f, 5.2f);
        scene.MainCamera.LookAt(new Vector3(0, 0.9f, 0));
        // 40×40 地面 + 拉远余量，抬过默认 far 100（页面自己的 Fit 按钮仍按模型盒子收紧）。
        scene.MainCamera.FarPlane = 150f;

        var light = new DirectionalLight
        {
            Name = "Key",
            LightColor = Color.White,
        };

        light.RotationDegrees = new Vector3(-42f, -28f, 0);

        scene.AddNode(light);

        var ground = new Mesh
        {
            Name = "Ground",
            Geometry = new PlaneGeometry(40f, 40f),
            Material = new Material(),
        };

        ground.Material.SetTexture("BaseColor", Procedural.Checker(256, 24));

        scene.AddNode(ground);

        // 全部已装载模型都入场景，用 Enable 挑当前那份——之后页内换模型不再动场景结构。
        foreach (var (key, model) in viewModel.Loaded)
        {
            model.Enable = key == viewModel.SelectedKey;

            scene.AddNode(model);
        }

        // 立初选为当前模型（拨 Enable、立 CurrentModel），再按它的盒子取一次景。
        viewModel.ActivateInitialModel();

        FitCamera();
    }

    /// <inheritdoc />
    public override void Update(double deltaTime)
    {
        viewModel.Tick();

        Context.RequestFrame();
    }

    /// <summary>
    /// 按需档的下载解码（旧 Choose 的 await 段等价搬迁）：字节流走 LoadAssetsAsync 留下的
    /// 批处理，进度与耗时文案由 VM 记账；解码完先把节点入场景，再回灌 VM 走同一条切换路径。
    /// 重入由 VM 的 loading 状态挡（装载中新选择根本不会发请求）。
    /// </summary>
    private async void FetchModel(string key)
    {
        var asset = Assets.AssetManifest.All.First(a => a.Key == key);

        viewModel.BeginLoad(key, asset.Bytes / 1024);

        var stopwatch = Stopwatch.StartNew();

        var model = await batch.ModelAsync(key);

        stopwatch.Stop();

        Context.Scene!.AddNode(model);

        viewModel.SetModel(key, model, asset.Bytes / 1024, stopwatch.ElapsedMilliseconds, asset.Path);
    }

    /// <summary>
    /// 相机取景（旧 Fit 等价搬迁）：按当前模型的盒子收紧视角，far 放大几倍留拉远余量。
    /// 旧页每次 Fit 都乘一次 far——远平面只变大不碍事，照抄。
    /// </summary>
    private void FitCamera()
    {
        var camera = Context.Scene?.MainCamera;

        if (camera == null || viewModel.CurrentModel == null)
            return;

        camera.FitToBoundingBox(viewModel.CurrentModel.BoundingBox, (float)viewModel.FitPadding);

        // fit 收紧后的 far 恰好贴着模型背面，滚轮拉远一点就撞远平面；放大几倍留出拉远余量
        camera.FarPlane *= 3f;
    }
}
