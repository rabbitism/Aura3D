using System.Numerics;

namespace Aura3D.Gallery.Demos;

/// <summary>
/// 图元页的视图：文案、参数与读数全在 <see cref="PrimitivesViewModel"/>，XAML 经绑定直连。
/// 手写 <see cref="Aura3D.Core.Resources.Geometry"/> 顶点缓冲与七种
/// <see cref="Aura3D.Core.Resources.PrimitiveType"/> 连线规则的细节随网格一起住在 VM；
/// 本类只保留生命周期——建场景（摆相机、开网格、登记 VM 的七个网格）、逐帧驱动读数。
/// 顶点色与点精灵的着色器只覆盖 <c>NoLightPass</c>，所以本页锁死在 NoLight 管线上
/// （XAML 的 <c>Pipeline="NoLight"</c>）——换到需要法线的管线会得到误导的结果。
/// </summary>
public sealed partial class PrimitivesDemo : Demo
{
    private readonly PrimitivesViewModel viewModel = new();

    /// <summary>
    /// 建页：装配 XAML 并把 DataContext 换成 VM。仍须显式赋值——外壳继承下来的
    /// DataContext 优先于默认值；基类先设的「页面自身」在这里被 VM 顶替。
    /// </summary>
    /// <param name="context">宿主环境。</param>
    public PrimitivesDemo(DemoContext context) : base(context)
    {
        InitializeComponent();

        // 顶替基类的 DataContext=this：页面绑定从此指向 VM。
        DataContext = viewModel;

        // 聚焦或参数变化要排一帧重绘；VM 不碰 DemoContext，经事件桥到宿主，
        // 等价于旧处理器末尾的 InvalidateRender。
        viewModel.FrameRequested += () => Context.InvalidateRender();
    }

    /// <inheritdoc />
    public override void BuildScene()
    {
        var scene = Context.Scene!;

        scene.ShowGrid = true;
        scene.MainCamera.Position = new Vector3(0, 6f, 14f);
        scene.MainCamera.LookAt(new Vector3(0, 0.5f, 0));

        // 七个网格连同聚焦摆位在 VM 构造期已备好，这里只登记进场景。
        foreach (var mesh in viewModel.Meshes)
            scene.AddNode(mesh);
    }

    /// <inheritdoc />
    public override void Update(double deltaTime)
    {
        // 读数原料只在交互时变，但按模板读数的统一路径逐帧重读（语言切换也走这条）；
        // 渲染本身由视图的自动逐帧驱动，本页没有要追加的重绘请求。
        viewModel.Tick();
    }
}
