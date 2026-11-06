using Aura3D.Core.Nodes;
using System.Numerics;
using Color = System.Drawing.Color;

namespace Aura3D.Gallery.Demos;

/// <summary>
/// 材质页的视图：文案、参数与读数全在 <see cref="MaterialShadersViewModel"/>，XAML 经绑定直连。
/// 材质的三件事（通道、按 pass 类名覆盖的 GLSL、按名绑定的材质参数）连同程序化贴图与整排网格
/// 随 VM 在构造期建全；本类只保留生命周期——建场景（摆相机、加灯、登记整排）、逐帧驱动。
/// 覆盖键写死为 LightPass，换管线就没有覆盖可言，所以本页锁管线（见 DemoRegistry 登记项）。
/// </summary>
public sealed partial class MaterialShadersDemo : Demo
{
    private readonly MaterialShadersViewModel viewModel = new();

    /// <summary>
    /// 建页：装配 XAML 并把 DataContext 换成 VM。仍须显式赋值——外壳继承下来的
    /// DataContext 优先于默认值；基类先设的「页面自身」在这里被 VM 顶替。
    /// </summary>
    /// <param name="context">宿主环境。</param>
    public MaterialShadersDemo(DemoContext context) : base(context)
    {
        InitializeComponent();

        // 顶替基类的 DataContext=this：页面绑定从此指向 VM。
        DataContext = viewModel;

        // 材质/参数变化要排一帧重绘；VM 不碰 DemoContext，经事件桥到宿主，
        // 等价于旧处理器末尾的 InvalidateRender。
        viewModel.FrameRequested += () => Context.InvalidateRender();

        // 地面网格挂在宿主的 Scene 上，VM 够不着；开关变化经事件桥写过去。
        // Scene 要到首帧回调才存在，开机窗口期在这里兜住（同旧处理器的判空）。
        viewModel.ShowGridToggled += value =>
        {
            if (Context.Scene != null)
                Context.Scene.ShowGrid = value;
        };
    }

    /// <inheritdoc />
    public override void BuildScene()
    {
        var scene = Context.Scene!;

        scene.MainCamera.Position = new Vector3(0, 2.4f, 11.5f);
        scene.MainCamera.LookAt(new Vector3(0, 2.1f, 0.4f));

        var light = new DirectionalLight { LightColor = Color.White };

        light.RotationDegrees = new Vector3(-48f, -28f, 0);

        scene.AddNode(light);

        // 贴图、材质与整排网格在 VM 构造期已备好，这里只把整排挂进场景。
        scene.AddNode(viewModel.Row);
    }

    /// <inheritdoc />
    public override void Update(double deltaTime)
    {
        // 自转、条纹时钟与读数都在 VM 里推进；渲染本身按旧路径逐帧请求。
        viewModel.Tick(deltaTime);

        Context.RequestFrame();
    }
}
