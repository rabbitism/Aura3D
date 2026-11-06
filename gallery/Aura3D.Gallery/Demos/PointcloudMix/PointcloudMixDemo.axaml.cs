using Aura3D.Core.Nodes;
using Aura3D.Core.Resources;
using System.Numerics;
using System.Threading.Tasks;
using ModelNode = Aura3D.Core.Nodes.Model;

namespace Aura3D.Gallery.Demos;

/// <summary>
/// 点云与模型混载页的视图，MVVM 化：三个材质参数（uPointSize / uUnlit /
/// uUseVertexColor）全在 <see cref="PointcloudMixViewModel"/>，XAML 经绑定直连；
/// 点云材质与网格也由 VM 持有（引擎默认路径，无任何着色器覆盖，所以页面不锁管线，
/// 切到 PBR 前向/延迟或卡通风，同一份场景照常出图）。本类只保留生命周期——
/// 取桌子资产、在渲染线程回调里组装场景；参数经 VM 写穿材质后由
/// <see cref="PointcloudMixViewModel.FrameRequested"/> 回来请求重绘。
/// </summary>
public sealed partial class PointcloudMixDemo : Demo
{
    private readonly PointcloudMixViewModel viewModel = new();

    private ModelNode? table;

    /// <summary>
    /// 建页：装配 XAML 并把 DataContext 换成 VM。仍须显式赋值——外壳继承下来的
    /// DataContext 优先于默认值；基类先设的「页面自身」在这里被 VM 顶替。
    /// </summary>
    /// <param name="context">宿主环境。</param>
    public PointcloudMixDemo(DemoContext context) : base(context)
    {
        InitializeComponent();

        DataContext = viewModel;

        viewModel.FrameRequested += () => Context.InvalidateRender();
    }

    /// <inheritdoc />
    public override async Task LoadAssetsAsync(AssetBatch assets)
    {
        table = await assets.ModelAsync("CoffeeTable");

        table.Name = "CoffeeTable";
    }

    /// <inheritdoc />
    public override void BuildScene()
    {
        var scene = Context.Scene!;

        scene.ShowGrid = true;
        scene.MainCamera.Position = new Vector3(3.4f, 2.6f, 5.2f);
        scene.MainCamera.LookAt(new Vector3(0, 1.1f, 0));

        var light = new DirectionalLight
        {
            Name = "Key",
            LightColor = System.Drawing.Color.White,
        };

        light.RotationDegrees = new Vector3(-42f, -28f, 0);

        scene.AddNode(light);

        if (table != null)
            scene.AddNode(table);

        // 点云网格与材质由 VM 建好；悬浮高度属场景布局，仍在这里摆。
        viewModel.Pointcloud.Position = new Vector3(0f, 2.2f, 0f);

        scene.AddNode(viewModel.Pointcloud);
    }
}
