using Aura3D.Core.Geometries;
using Aura3D.Core.Nodes;
using Aura3D.Core.Resources;
using Aura3D.Core.Scenes;
using Aura3D.Gallery.Kit;
using System.Numerics;
using System.Threading.Tasks;

namespace Aura3D.Gallery.Demos;

/// <summary>
/// 场景辅助层页的视图：文案、参数与读数全在 <see cref="SceneGizmosViewModel"/>，XAML 经绑定直连。
/// 本类只保留生命周期——组装场景（相机、平行光/点光、公转轨道与对照网格体；聚光灯本体住在 VM）、
/// 把 VM 的十个开关事件转写进宿主的 <c>PipelineSettings.Debug</c> 与 <c>Scene</c>
/// （VM 不碰 DemoContext，参照 Particles/PostProcessing 页的做法）、逐帧驱动两盏灯公转与 Readout。
/// 网格/坐标轴/DebugDrawPass 八个开关全部出自同一条 DebugDrawPass：总开关 <c>Debug.Enable</c>
/// 关掉后细分项一律无效，外壳默认已把它打开（DemoContext 构造时），本页只翻细分项。
/// </summary>
public sealed partial class SceneGizmosDemo : Demo
{
    private readonly SceneGizmosViewModel viewModel = new();

    private Node? lightOrbit;
    private double orbitDegrees;

    /// <summary>
    /// 建页：装配 XAML 后把 DataContext 换成 VM。仍须显式赋值——外壳继承下来的
    /// DataContext 优先于默认值；基类先设的「页面自身」在这里被 VM 顶替。
    /// </summary>
    /// <param name="context">宿主环境。</param>
    public SceneGizmosDemo(DemoContext context) : base(context)
    {
        InitializeComponent();

        // 顶替基类的 DataContext=this：页面绑定从此指向 VM。
        DataContext = viewModel;

        // 开关转写：DebugDrawPass 的细分开关住在宿主 PipelineSettings.Debug 上，只有视图够得着。
        viewModel.ShowBoundingBoxToggled += value => Context.Settings.Debug.ShowBoundingBox = value;
        viewModel.ShowDirectionalLightToggled += value => Context.Settings.Debug.ShowDirectionalLight = value;
        viewModel.ShowPointLightToggled += value => Context.Settings.Debug.ShowPointLight = value;
        viewModel.ShowSpotLightToggled += value => Context.Settings.Debug.ShowSpotLight = value;
        viewModel.ShowCameraToggled += value => Context.Settings.Debug.ShowCamera = value;
        viewModel.ShowParticleBoundsToggled += value => Context.Settings.Debug.ShowParticleBounds = value;
        viewModel.ShowBoneToggled += value => Context.Settings.Debug.ShowBone = value;
        viewModel.DebugEnableToggled += value => Context.Settings.Debug.Enable = value;

        // 网格/坐标轴开关的真值在 Scene 上；场景未建时跳过，与旧 SetGridEnabled 的守卫同口径。
        viewModel.ShowGridToggled += value => SetGridEnabled(value);
        viewModel.ShowAxisToggled += value => SetAxisEnabled(value);

        // 开关拨动与锥角变化都要排一帧重绘——等价于旧处理器里逐个调的 InvalidateRender。
        viewModel.FrameRequested += Context.InvalidateRender;

        // 初值对齐：把面板画出来的勾选态写回宿主设置，免得面板与真值不符（等价于旧构造函数里
        // 的三行赋值；其余细分项两边都是 false，一并写上让口径统一）。Debug.Enable 外壳
        // 本来就已打开，这里是显式重申。
        var debug = Context.Settings.Debug;

        debug.Enable = viewModel.DebugEnable;
        debug.ShowBoundingBox = viewModel.ShowBoundingBox;
        debug.ShowDirectionalLight = viewModel.ShowDirectionalLight;
        debug.ShowPointLight = viewModel.ShowPointLight;
        debug.ShowSpotLight = viewModel.ShowSpotLight;
        debug.ShowCamera = viewModel.ShowCamera;
        debug.ShowParticleBounds = viewModel.ShowParticleBounds;
        debug.ShowBone = viewModel.ShowBone;
    }

    /// <inheritdoc />
    public override Task LoadAssetsAsync(AssetBatch assets) => Task.CompletedTask;

    /// <inheritdoc />
    public override void BuildScene()
    {
        var scene = Context.Scene!;

        scene.MainCamera.Position = new Vector3(0, 4.2f, 12f);
        scene.MainCamera.LookAt(new Vector3(0, 1.6f, 0));

        // 网格/坐标轴场景默认全关，开关初值从 VM 对齐（等价于旧页写死的两个 true）；
        // 边长/分隔数/轴长/箭头这批参数由 AttachGizmos 登记后统一落一次。
        scene.ShowGrid = viewModel.ShowGrid;
        scene.ShowAxisGizmo = viewModel.ShowAxes;

        viewModel.AttachGizmos(scene.Grid, scene.AxisGizmo);

        var directional = new DirectionalLight { LightColor = System.Drawing.Color.White };

        directional.RotationDegrees = new Vector3(-52f, -34f, 0);

        scene.AddNode(directional);

        var point = new PointLight
        {
            LightColor = System.Drawing.Color.Orange,
            LuminousIntensity = 6000,
            AttenuationRadius = 9f,
        };

        point.Position = new Vector3(-3f, 3.4f, 2f);

        lightOrbit = new Node { Name = "LightOrbit" };

        scene.AddNode(lightOrbit);

        lightOrbit.AddChild(point, AttachToParentRule.KeepLocal);

        // 聚光灯本体住在 VM（锥角滑杆要写穿它），这里只负责挂进公转轨道。
        lightOrbit.AddChild(viewModel.Spot, AttachToParentRule.KeepLocal);

        AddMesh(scene, "Box", new BoxGeometry(1.6f, 1.6f, 1.6f), new Vector3(-2.6f, 1.1f, -0.6f));
        AddMesh(scene, "Sphere", new SphereGeometry(0.95f, 36, 20), new Vector3(0f, 1.1f, -0.6f));
        AddMesh(scene, "Cylinder", new CylinderGeometry(0.6f, 0.8f, 2.2f, 32), new Vector3(2.6f, 1.1f, -0.6f));

        var hidden = AddMesh(scene, "Hidden", new BoxGeometry(1f, 1f, 1f), new Vector3(0f, 3.6f, -3f));

        hidden.Enable = false;
    }

    /// <inheritdoc />
    public override void Update(double deltaTime)
    {
        // 公转：转速为 0 时既不转也不请求帧，保持旧页「按需渲染」的口径；转速读 VM 的滑杆值。
        if (lightOrbit != null && viewModel.LightOrbitSpeed > 0)
        {
            orbitDegrees += viewModel.LightOrbitSpeed * deltaTime;

            lightOrbit.RotationDegrees = new Vector3(0, (float)orbitDegrees, 0);

            Context.RequestFrame();
        }

        viewModel.Tick();
    }

    private void SetGridEnabled(bool on)
    {
        if (Context.Scene != null)
            Context.Scene.ShowGrid = on;
    }

    private void SetAxisEnabled(bool on)
    {
        if (Context.Scene != null)
            Context.Scene.ShowAxisGizmo = on;
    }

    private static Mesh AddMesh(Scene scene, string name, Geometry geometry, Vector3 position)
    {
        var material = new Material();

        material.SetTexture("BaseColor", Procedural.Checker(128, 3));

        var mesh = new Mesh { Name = name, Geometry = geometry, Material = material };

        mesh.Position = position;

        scene.AddNode(mesh);

        return mesh;
    }
}
