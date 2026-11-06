using Aura3D.Core.Geometries;
using Aura3D.Core.Nodes;
using Aura3D.Core.Resources;
using Aura3D.Core.Scenes;
using Aura3D.Gallery.Kit;
using System.Numerics;
using System.Threading.Tasks;
using Color = System.Drawing.Color;

namespace Aura3D.Gallery.Demos;

/// <summary>
/// <see cref="InstancedMeshGroup"/>（HISM）页的视图：八叉树把上万个实例切成若干
/// <see cref="InstancedMesh"/> 分组，剔除按组做，相机只看到一角时其余组不参与绘制；
/// 读数里的原地更新数与重建数是本页主角——跨叶更新会把整棵树重建一次。
/// MVVM 化后参数与命令全在 <see cref="HismViewModel"/>，XAML 经绑定直连，
/// 本类只保留生命周期——组装场景（相机、灯光、地面、HISM 分组）、逐帧驱动读数与渲染。
/// 实例分布、逐帧搬动与八叉树参数的写穿见 VM。
/// </summary>
public sealed partial class HismDemo : Demo
{
    private readonly HismViewModel viewModel = new();

    /// <summary>
    /// 建页：装配 XAML 并把 DataContext 换成 VM。仍须显式赋值——外壳继承下来的
    /// DataContext 优先于默认值；基类先设的「页面自身」在这里被 VM 顶替。
    /// </summary>
    /// <param name="context">宿主环境。</param>
    public HismDemo(DemoContext context) : base(context)
    {
        InitializeComponent();

        DataContext = viewModel;

        viewModel.FrameRequested += () => Context.RequestFrame();
    }

    /// <inheritdoc />
    public override Task LoadAssetsAsync(AssetBatch assets) => Task.CompletedTask;

    /// <inheritdoc />
    public override void BuildScene()
    {
        var scene = Context.Scene!;

        scene.MainCamera.Position = new Vector3(0, 26f, 62f);
        scene.MainCamera.LookAt(new Vector3(0, 2f, 0));
        // 默认 far 100 装不下 140×140 地面：初始机位到地边缘就近 132，远半片直接没画。抬到 400 给拉远也留余量。
        scene.MainCamera.FarPlane = 400f;

        var light = new DirectionalLight { LightColor = Color.White };

        light.RotationDegrees = new Vector3(-46f, -14f, 0);

        scene.AddNode(light);

        var ground = new Mesh
        {
            Name = "Ground",
            Geometry = new PlaneGeometry(140f, 140f),
            Material = new Material(),
        };

        ground.Material.SetTexture("BaseColor", Procedural.Checker(256, 32));

        scene.AddNode(ground);

        // HISM 分组节点与首批实例由 VM 建好，这里只挂进场景；建组时 VM 会经
        // FrameRequested 请求重绘，与本方法跑在渲染线程回调上的事实不冲突。
        scene.AddNode(viewModel.CreateGroup());
    }

    /// <inheritdoc />
    public override void Update(double deltaTime)
    {
        viewModel.Update(deltaTime);
        Context.RequestFrame();
    }
}
