using Aura3D.Core.Nodes;
using System.Numerics;
using System.Threading.Tasks;
using Color = System.Drawing.Color;

namespace Aura3D.Gallery.Demos;

/// <summary>
/// <see cref="InstancedMesh"/>：一份几何 + 一组逐实例矩阵 = 一次 draw call。
/// 左片用引擎着色器（正常吃光照与贴图），右片用自定义的实例感知着色器对
/// （<see cref="Kit.Shaders.InstancedColorVertex"/> + 逐实例顶点色）——覆盖顶点阶段之后，
/// 槽位 8–11 的实例矩阵必须由自己的着色器乘进去，否则整片实例会叠在原点。
/// MVVM 化后文案、参数与命令全在 <see cref="InstancingViewModel"/>，XAML 经绑定直连，
/// 本类只保留生命周期——组装场景（相机、灯光）、逐帧驱动读数与渲染。
/// 两片实例场的建模与波动写法见 VM。
/// </summary>
public sealed partial class InstancingDemo : Demo
{
    private readonly InstancingViewModel viewModel = new();

    /// <summary>
    /// 建页：装配 XAML 并把 DataContext 换成 VM。仍须显式赋值——外壳继承下来的
    /// DataContext 优先于默认值；基类先设的「页面自身」在这里被 VM 顶替。
    /// </summary>
    /// <param name="context">宿主环境。</param>
    public InstancingDemo(DemoContext context) : base(context)
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

        scene.MainCamera.Position = new Vector3(0, 11f, 26f);
        scene.MainCamera.LookAt(new Vector3(0, 1.5f, 0));
        // 17×17 实例场，机位 ~28；抬过默认 far 100，留出滚轮拉远的余量。
        scene.MainCamera.FarPlane = 120f;

        var light = new DirectionalLight { LightColor = Color.White };

        light.RotationDegrees = new Vector3(-50f, -18f, 0);

        scene.AddNode(light);

        // 两片实例场由 VM 建好（建场时会经 FrameRequested 请求重绘），这里只挂进场景。
        var fields = viewModel.CreateFields();

        scene.AddNode(fields.LitField);
        scene.AddNode(fields.ColoredField);
    }

    /// <inheritdoc />
    public override void Update(double deltaTime)
    {
        viewModel.Update(deltaTime);
        viewModel.Tick();
    }
}
