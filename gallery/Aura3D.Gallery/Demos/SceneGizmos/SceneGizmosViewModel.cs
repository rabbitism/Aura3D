using Aura3D.Core.Nodes;
using Aura3D.Core.Scenes;
using Aura3D.Gallery.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using Irihi.Lingua;
using System;
using System.Numerics;
using Color = System.Drawing.Color;

namespace Aura3D.Gallery.Demos;

/// <summary>
/// 场景辅助层页的界面状态。分工：
/// <list type="bullet">
///   <item><description>文案：AXAML 直接写 <c>{Translate}</c> 标记扩展取 Strings.Keys 键值；换语言走外壳既有的整页重挂。</description></item>
///   <item><description>参数：滑杆/开关双向绑定到这里的属性。滑杆写穿到场景建好后登记进来的
///     <see cref="Grid"/>/<see cref="AxisGizmo"/> 与 VM 持有的 <see cref="Spot"/>；
///     十个开关只改 VM 状态并经语义事件通知视图，由视图转写进宿主的
///     <c>PipelineSettings.Debug</c> 与 <c>Scene.ShowGrid/ShowAxisGizmo</c>——VM 不碰 DemoContext。</description></item>
///   <item><description>Readout：Lingua 1.1 的 format builder 组装，11 个占位符与
///     <c>SceneGizmos_DebugReadout</c> 模板一一对应，视图逐帧调 <see cref="Tick"/> 重读，
///     语言切换时模板本身也会自动换。</description></item>
///   <item><description>引擎模型：被锥角滑杆写穿的聚光灯住在这里，建场景时由视图挂进公转节点；
///     相机、平行光/点光与对照网格体是纯布景，仍留在视图组装。</description></item>
/// </list>
/// 视图桥是 <see cref="FrameRequested"/>（开关拨动/锥角变化后排一次重绘）与十个开关的
/// 语义事件（Debug 设置与 Scene 只有视图够得着，参照 Particles 的 ShowBoundsToggled 做法）。
/// </summary>
public sealed partial class SceneGizmosViewModel : ObservableObject
{
    /// <summary>
    /// 建好 VM：把聚光灯锥角按滑杆初值落一次（口径与 <see cref="SpotOuterAngle"/> 的写穿一致），
    /// 再装配 Readout 的模板与占位参数；滑杆/开关初值即字段默认值，等价于旧页面 XAML 里的那批字面量。
    /// </summary>
    public SceneGizmosViewModel()
    {
        // 内锥按外锥 0.6 倍跟随：旧 BuildScene 与旧 OnSpotConeChanged 都是这个比例。
        Spot.InnerConeAngleDegree = (float)(spotCone * 0.6);
        Spot.OuterConeAngleDegree = (float)spotCone;

        // Readout 模板 {0}..{10}：前六个占位读 VM 的 Debug 开关 backing——它与宿主设置
        // 经视图桥始终同步（初值对齐 + 后续写回都走视图）；后五个读网格/坐标轴对象本身，
        // 场景建出来前显示空值，与旧代码逐帧读 Context.Scene 的口径一致。
        ReadoutText = Strings.Keys.SceneGizmos_DebugReadout.CreateFormat()
            .Arg(() => debugEnable)
            .Arg(() => showBoundingBox)
            .Arg(() => showDirectionalLight)
            .Arg(() => showPointLight)
            .Arg(() => showSpotLight)
            .Arg(() => showCamera)
            .Arg(() => grid?.Enable == true)
            .Arg(() => grid?.Size)
            .Arg(() => grid?.Divisions)
            .Arg(() => axis?.AxisLength)
            .Arg(() => axis?.ArrowheadSize)
            .Build();
    }

    /// <summary>
    /// 场景建好后由视图登记网格与坐标轴对象（两者由 Scene 构造器创建，VM 此前拿不到引用），
    /// 滑杆从此写穿到它们上面；登记时先把滑杆初值落一次，等价于旧 BuildScene 末尾的
    /// ApplyGrid/ApplyAxis 调用。
    /// </summary>
    public void AttachGizmos(Grid sceneGrid, AxisGizmo sceneAxis)
    {
        grid = sceneGrid;
        axis = sceneAxis;

        ApplyGrid();
        ApplyAxis();
    }

    /// <summary>逐帧调用：重读 Readout 的占位参数（Debug 开关与网格/坐标轴当前值）。</summary>
    public void Tick() => ReadoutText.Refresh();

    /// <summary>开关拨动或锥角变化后请求一帧重绘；视图把它接到宿主的 InvalidateRender 上。</summary>
    public event Action? FrameRequested;

    /// <summary>「显示网格」开关变化；真值在 <c>Scene.ShowGrid</c> 上，只有视图够得着。</summary>
    public event Action<bool>? ShowGridToggled;

    /// <summary>「显示坐标轴」开关变化；真值在 <c>Scene.ShowAxisGizmo</c> 上。</summary>
    public event Action<bool>? ShowAxisToggled;

    /// <summary>「包围盒」开关变化；真值在宿主的 <c>Settings.Debug.ShowBoundingBox</c> 上。</summary>
    public event Action<bool>? ShowBoundingBoxToggled;

    /// <summary>「平行光体积」开关变化；真值在宿主的 <c>Settings.Debug.ShowDirectionalLight</c> 上。</summary>
    public event Action<bool>? ShowDirectionalLightToggled;

    /// <summary>「点光体积」开关变化；真值在宿主的 <c>Settings.Debug.ShowPointLight</c> 上。</summary>
    public event Action<bool>? ShowPointLightToggled;

    /// <summary>「聚光体积」开关变化；真值在宿主的 <c>Settings.Debug.ShowSpotLight</c> 上。</summary>
    public event Action<bool>? ShowSpotLightToggled;

    /// <summary>「相机视锥」开关变化；真值在宿主的 <c>Settings.Debug.ShowCamera</c> 上。</summary>
    public event Action<bool>? ShowCameraToggled;

    /// <summary>「粒子体积」开关变化；真值在宿主的 <c>Settings.Debug.ShowParticleBounds</c> 上。</summary>
    public event Action<bool>? ShowParticleBoundsToggled;

    /// <summary>「骨骼」开关变化；真值在宿主的 <c>Settings.Debug.ShowBone</c> 上。</summary>
    public event Action<bool>? ShowBoneToggled;

    /// <summary>总开关变化；关掉后 DebugDrawPass 一律早退，细分项全部无效。</summary>
    public event Action<bool>? DebugEnableToggled;

    /// <summary>逐帧读数。语言切换时模板换新，绑定自动收到重排后的整句。</summary>
    public LinguaFormatObservable ReadoutText { get; }

    /// <summary>
    /// 被锥角滑杆写穿的聚光灯：本体住在这里，建场景时由视图挂到公转节点下。
    /// 位置/朝向/颜色是布景常量，沿用旧 BuildScene 的取值；锥角初值在构造函数里按滑杆默认落一次。
    /// </summary>
    public SpotLight Spot { get; } = new()
    {
        LightColor = Color.PowderBlue,
        LuminousIntensity = 6000,
        AttenuationRadius = 12f,
        Position = new Vector3(3.4f, 6f, 3f),
        RotationDegrees = new Vector3(-108f, 12f, 0),
    };

    private Grid? grid;
    private AxisGizmo? axis;

    // 滑杆 backing：初值与旧 XAML 字面量一致（12/12/2.5/0.35/24/24）。
    private double gridHalfWidth = 12;
    private double gridDivisions = 12;
    private double axisLength = 2.5;
    private double arrowheadSize = 0.35;
    private double orbitSpeed = 24;
    private double spotCone = 24;

    // 开关 backing：初值即旧 XAML 的勾选态（网格/坐标轴/点光/聚光/总开关为真，其余为假）。
    private bool showGrid = true;
    private bool showAxes = true;
    private bool showBoundingBox;
    private bool showDirectionalLight;
    private bool showPointLight = true;
    private bool showSpotLight = true;
    private bool showCamera;
    private bool showParticleBounds;
    private bool showBone;
    private bool debugEnable = true;

    /// <summary>
    /// 网格边长（半宽，引擎属性叫 Size）。场景未建时先记在 backing 上，登记后由
    /// <see cref="AttachGizmos"/> 统一补落。与旧处理器一致，这里不排重绘——
    /// 公转动画默认在跑（每帧都在重绘），停转后旧版同样要等下一次重绘才见效。
    /// </summary>
    public double GridSize
    {
        get => gridHalfWidth;
        set
        {
            if (SetProperty(ref gridHalfWidth, value))
                ApplyGrid();
        }
    }

    /// <summary>网格分隔数；按整数生效。不排重绘的口径同 <see cref="GridSize"/>。</summary>
    public double GridDivisions
    {
        get => gridDivisions;
        set
        {
            if (SetProperty(ref gridDivisions, value))
                ApplyGrid();
        }
    }

    /// <summary>坐标轴长度。不排重绘的口径同 <see cref="GridSize"/>。</summary>
    public double AxisLength
    {
        get => axisLength;
        set
        {
            if (SetProperty(ref axisLength, value))
                ApplyAxis();
        }
    }

    /// <summary>箭头尺寸。不排重绘的口径同 <see cref="GridSize"/>。</summary>
    public double ArrowheadSize
    {
        get => arrowheadSize;
        set
        {
            if (SetProperty(ref arrowheadSize, value))
                ApplyAxis();
        }
    }

    /// <summary>两盏灯的公转角速度（度/秒）；只存值，旋转由视图逐帧驱动。</summary>
    public double LightOrbitSpeed
    {
        get => orbitSpeed;
        set => SetProperty(ref orbitSpeed, value);
    }

    /// <summary>聚光外锥角（度）；内锥按 0.6 倍跟随，维持旧处理器的比例。变化后排一次重绘。</summary>
    public double SpotOuterAngle
    {
        get => spotCone;
        set
        {
            if (!SetProperty(ref spotCone, value))
                return;

            Spot.OuterConeAngleDegree = (float)value;
            Spot.InnerConeAngleDegree = (float)(value * 0.6);

            FrameRequested?.Invoke();
        }
    }

    /// <summary>「显示网格」；经事件由视图转写到 <c>Scene.ShowGrid</c>。</summary>
    public bool ShowGrid
    {
        get => showGrid;
        set
        {
            if (!SetProperty(ref showGrid, value))
                return;

            ShowGridToggled?.Invoke(value);
            FrameRequested?.Invoke();
        }
    }

    /// <summary>「显示坐标轴」；经事件由视图转写到 <c>Scene.ShowAxisGizmo</c>。</summary>
    public bool ShowAxes
    {
        get => showAxes;
        set
        {
            if (!SetProperty(ref showAxes, value))
                return;

            ShowAxisToggled?.Invoke(value);
            FrameRequested?.Invoke();
        }
    }

    /// <summary>画包围盒；经事件由视图转写到 <c>Settings.Debug.ShowBoundingBox</c>。</summary>
    public bool ShowBoundingBox
    {
        get => showBoundingBox;
        set
        {
            if (!SetProperty(ref showBoundingBox, value))
                return;

            ShowBoundingBoxToggled?.Invoke(value);
            FrameRequested?.Invoke();
        }
    }

    /// <summary>画平行光体积；经事件由视图转写到 <c>Settings.Debug.ShowDirectionalLight</c>。</summary>
    public bool ShowDirectionalLight
    {
        get => showDirectionalLight;
        set
        {
            if (!SetProperty(ref showDirectionalLight, value))
                return;

            ShowDirectionalLightToggled?.Invoke(value);
            FrameRequested?.Invoke();
        }
    }

    /// <summary>画点光体积；经事件由视图转写到 <c>Settings.Debug.ShowPointLight</c>。</summary>
    public bool ShowPointLight
    {
        get => showPointLight;
        set
        {
            if (!SetProperty(ref showPointLight, value))
                return;

            ShowPointLightToggled?.Invoke(value);
            FrameRequested?.Invoke();
        }
    }

    /// <summary>画聚光体积；经事件由视图转写到 <c>Settings.Debug.ShowSpotLight</c>。</summary>
    public bool ShowSpotLight
    {
        get => showSpotLight;
        set
        {
            if (!SetProperty(ref showSpotLight, value))
                return;

            ShowSpotLightToggled?.Invoke(value);
            FrameRequested?.Invoke();
        }
    }

    /// <summary>画相机视锥；经事件由视图转写到 <c>Settings.Debug.ShowCamera</c>。</summary>
    public bool ShowCamera
    {
        get => showCamera;
        set
        {
            if (!SetProperty(ref showCamera, value))
                return;

            ShowCameraToggled?.Invoke(value);
            FrameRequested?.Invoke();
        }
    }

    /// <summary>画粒子包围体积；经事件由视图转写到 <c>Settings.Debug.ShowParticleBounds</c>。</summary>
    public bool ShowParticleBounds
    {
        get => showParticleBounds;
        set
        {
            if (!SetProperty(ref showParticleBounds, value))
                return;

            ShowParticleBoundsToggled?.Invoke(value);
            FrameRequested?.Invoke();
        }
    }

    /// <summary>画骨骼；经事件由视图转写到 <c>Settings.Debug.ShowBone</c>。</summary>
    public bool ShowBone
    {
        get => showBone;
        set
        {
            if (!SetProperty(ref showBone, value))
                return;

            ShowBoneToggled?.Invoke(value);
            FrameRequested?.Invoke();
        }
    }

    /// <summary>
    /// DebugDrawPass 总开关。外壳的 DemoContext 构造时已把它打开，本页默认也画勾；
    /// 拨到关后细分项一律无效（那个 pass 直接早退）。
    /// </summary>
    public bool DebugEnable
    {
        get => debugEnable;
        set
        {
            if (!SetProperty(ref debugEnable, value))
                return;

            DebugEnableToggled?.Invoke(value);
            FrameRequested?.Invoke();
        }
    }

    /// <summary>把边长/分隔数落到场景网格上；对象未登记时只改 backing，登记时统一补落。</summary>
    private void ApplyGrid()
    {
        if (grid == null)
            return;

        grid.Size = (float)gridHalfWidth;
        grid.Divisions = (int)gridDivisions;
    }

    /// <summary>把轴长/箭头尺寸落到坐标轴上；对象未登记时只改 backing。</summary>
    private void ApplyAxis()
    {
        if (axis == null)
            return;

        axis.AxisLength = (float)axisLength;
        axis.ArrowheadSize = (float)arrowheadSize;
    }
}
