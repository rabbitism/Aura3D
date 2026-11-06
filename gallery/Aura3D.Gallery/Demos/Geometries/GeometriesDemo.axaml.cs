using Aura3D.Core.Geometries;
using Aura3D.Core.Nodes;
using Aura3D.Core.Resources;
using Aura3D.Gallery.Kit;
using System.Numerics;
using System.Threading.Tasks;

namespace Aura3D.Gallery.Demos;

/// <summary>
/// 几何体页的视图：文案、参数与读数全在 <see cref="GeometriesViewModel"/>，XAML 经绑定直连。
/// 四种内置几何体（含构造参数重建与整排旋转）的引擎侧配置随整排节点一起住在 VM；
/// 本类只保留生命周期——建场景（摆相机、铺地面、挂整排、落网格/坐标轴开关初值）、
/// 逐帧驱动旋转与读数。全页零外部资产：棋盘纹理由 <see cref="Procedural"/> 现算。
/// </summary>
public sealed partial class GeometriesDemo : Demo
{
    private readonly GeometriesViewModel viewModel = new();

    /// <summary>
    /// 建页：装配 XAML 并把 DataContext 换成 VM。仍须显式赋值——外壳继承下来的
    /// DataContext 优先于默认值；基类先设的「页面自身」在这里被 VM 顶替。
    /// </summary>
    /// <param name="context">宿主环境。</param>
    public GeometriesDemo(DemoContext context) : base(context)
    {
        InitializeComponent();

        // 顶替基类的 DataContext=this：页面绑定从此指向 VM。
        DataContext = viewModel;

        // 参数重建要排一帧重绘，等价旧 Rebuild 末尾的 InvalidateRender。
        viewModel.FrameRequested += () => Context.InvalidateRender();

        // 网格/坐标轴写在场景上，VM 不碰 DemoContext，经事件桥接；场景未建时跳过赋值
        // 但照旧排帧，逐行等价旧 OnShowGridToggled/OnShowAxisToggled。
        viewModel.ShowGridToggled += value =>
        {
            if (Context.Scene != null)
                Context.Scene.ShowGrid = value;

            Context.InvalidateRender();
        };

        viewModel.ShowAxesToggled += value =>
        {
            if (Context.Scene != null)
                Context.Scene.ShowAxisGizmo = value;

            Context.InvalidateRender();
        };
    }

    /// <inheritdoc />
    public override Task LoadAssetsAsync(AssetBatch assets) => Task.CompletedTask;

    /// <inheritdoc />
    public override void BuildScene()
    {
        var scene = Context.Scene!;

        // 开关初值落在 VM（true/false），等价旧代码「无条件 ShowGrid=true + 坐标轴走引擎默认」。
        scene.ShowGrid = viewModel.ShowGrid;
        scene.ShowAxisGizmo = viewModel.ShowAxes;

        scene.MainCamera.Position = new Vector3(0, 3.2f, 11f);
        scene.MainCamera.LookAt(new Vector3(0, 1.2f, 0));

        // 80×80 地面，默认 far 100 稍一拉远就撞墙；按本页尺度抬到 250。
        scene.MainCamera.FarPlane = 250f;

        var light = new DirectionalLight { LightColor = System.Drawing.Color.White };

        light.RotationDegrees = new Vector3(-45f, -25f, 0);

        scene.AddNode(light);

        // 地面是纯布景，留在视图；四个参数化几何体连同整排节点在 VM 构造期已备好，这里只挂进场景。
        var ground = new Mesh
        {
            Name = "Ground",
            Geometry = new PlaneGeometry(80f, 80f),
            Material = new Material(),
        };

        ground.Material.SetTexture("BaseColor", Procedural.Checker(256, 16));

        scene.AddNode(ground);

        scene.AddNode(viewModel.Row);
    }

    /// <inheritdoc />
    public override void Update(double deltaTime)
    {
        // 整排旋转与读数重读都在 Tick 里；渲染本身由视图的自动逐帧驱动，本页没有要追加的
        // 重绘请求（旧 Update 末尾的 RequestFrame 在连续渲染下本就是多余的）。
        viewModel.Tick(deltaTime);
    }
}
