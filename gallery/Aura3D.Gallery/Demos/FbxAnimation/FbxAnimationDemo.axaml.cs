using Aura3D.Core.Geometries;
using Aura3D.Core.Nodes;
using Aura3D.Core.Resources;
using Aura3D.Gallery.Kit;
using System.Numerics;
using System.Threading.Tasks;
using Color = System.Drawing.Color;

namespace Aura3D.Gallery.Demos;

/// <summary>
/// FBX 动作库页的视图，MVVM 化：文案、剪辑/骨骼/取样参数与三条读数全在
/// <see cref="FbxAnimationViewModel"/>，XAML 经绑定直连；本类只保留生命周期——
/// 取回模型与动作文件喂给 VM（同一 Skeleton 实例挂外接剪辑，ufbx 路的标准用法）、
/// 在渲染线程回调里组装相机/灯光/地面并登记模型节点。加载器是纯托管的 FbxLoader，
/// 没有原生库，所以这页在浏览器端也成立。
/// </summary>
public sealed partial class FbxAnimationDemo : Demo
{
    private readonly FbxAnimationViewModel viewModel = new();

    /// <summary>
    /// 建页：装配 XAML 并把 DataContext 换成 VM。仍须显式赋值——外壳继承下来的
    /// DataContext 优先于默认值；基类先设的「页面自身」在这里被 VM 顶替。
    /// </summary>
    /// <param name="context">宿主环境。</param>
    public FbxAnimationDemo(DemoContext context) : base(context)
    {
        InitializeComponent();

        DataContext = viewModel;
    }

    /// <inheritdoc />
    public override async Task LoadAssetsAsync(AssetBatch assets)
    {
        var loaded = await assets.FbxModelAsync("FbxMannequin");

        loaded.Name = "SK_Mannequin";

        viewModel.SetModel(loaded);

        // 两个动作文件都传同一个 Skeleton：返回的剪辑直接就是这副骨架的，不需要重定向。
        // 标签与键的对照表在 VM 上，这里只按它循环。
        foreach (var (label, key) in FbxAnimationViewModel.MotionFiles)
            viewModel.AddMotionClips(label, await assets.FbxAnimationsAsync(key, loaded.Skeleton));
    }

    /// <inheritdoc />
    public override void BuildScene()
    {
        var scene = Context.Scene!;

        if (viewModel.Model == null)
            return;

        scene.ShowGrid = true;
        scene.MainCamera.Position = new Vector3(38f, 42f, 62f);
        scene.MainCamera.LookAt(new Vector3(0, 40f, 0));

        var light = new DirectionalLight { LightColor = Color.White };

        light.RotationDegrees = new Vector3(-42f, -24f, 0);

        scene.AddNode(light);

        var ground = new Mesh
        {
            Name = "Ground",
            Geometry = new PlaneGeometry(600f, 600f),
            Material = new Material(),
        };

        ground.Material.SetTexture("BaseColor", Procedural.Checker(256, 20));

        scene.AddNode(ground);

        scene.AddNode(viewModel.Model);

        // 挂初选剪辑、补骨骼名单并回填初选（旧 BuildScene 的中段）。必须在 fit 之前——
        // 采样器构造时立即算第一帧姿势，模型的包围盒跟着姿势走。
        viewModel.PrepareBones();

        scene.MainCamera.FitToBoundingBox(viewModel.Model.BoundingBox, 0.25f);

        // fit 只框到模型，收紧的 far 会把 600×600 地面裁掉一大半；抬到 1000 盖住初始视角能看到的地。
        scene.MainCamera.FarPlane = 1000f;
    }

    /// <inheritdoc />
    public override void Update(double deltaTime)
    {
        viewModel.Tick();

        Context.RequestFrame();
    }
}
