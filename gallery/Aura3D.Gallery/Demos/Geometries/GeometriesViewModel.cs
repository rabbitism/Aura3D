using Aura3D.Core.Geometries;
using Aura3D.Core.Nodes;
using Aura3D.Core.Resources;
using Aura3D.Gallery.Kit;
using Aura3D.Gallery.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using Irihi.Lingua;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;

namespace Aura3D.Gallery.Demos;

/// <summary>
/// 几何体页的界面状态，按 Particles/Primitives 页的样板 MVVM 化。分工：
/// <list type="bullet">
///   <item><description>文案：每个标签一个 Lingua observable，XAML 用 <c>{Binding XxxLabel^}</c> 订阅，
///     换语言时由绑定自动收到新值，不依赖外壳整页重挂。</description></item>
///   <item><description>参数：滑杆/开关双向绑定到这里的属性。几何构造参数没有引擎对象可写穿——
///     只在顶点重建时被消费——所以 setter 的「写穿」就是按新参数把那一个网格的几何体重灌一遍
///     （顶点是真的重新生成，不是靠缩放糊弄）；四段参数各管各的几何体，互不干扰。
///     初值即旧 XAML 行控件上写死的那一份，由绑定初始化取代。</description></item>
///   <item><description>下拉：选项就是四种几何体的种类名。它们指名道姓地对着 BoxGeometry 一族
///     API（兼作网格名与字典键），保持英文原文；换选只决定读数统计哪一个，不重建任何东西。</description></item>
///   <item><description>Readout：Lingua 1.1 的 format builder 组装 {0}..{2}——顶点数、索引数与
///     顶点缓冲折算 KB 全是读数对象上的计算参数，视图逐帧调 <see cref="Tick"/> 重读（旧 Report
///     在「换选/重建」两个时机各排一次，逐帧重读把它们都盖住），语言切换时模板本身也会自动换。</description></item>
///   <item><description>引擎模型：整排节点（四个网格 + 棋盘材质 + 摆位）住在这里。本页没有外部
///     资产，棋盘纹路由 <see cref="Procedural"/> 现算，构造期即可建全；视图只负责生命周期——
///     建场景时把整排挂进场景、逐帧驱动旋转与读数。</description></item>
/// </list>
/// 视图桥有三个事件：<see cref="FrameRequested"/>（参数重建要排一次重绘，等价旧 Rebuild 末尾的
/// InvalidateRender）与 <see cref="ShowGridToggled"/>/<see cref="ShowAxesToggled"/>（网格/坐标轴
/// 开关写在场景上，VM 不碰 DemoContext）。
/// </summary>
public sealed partial class GeometriesViewModel : ObservableObject
{
    /// <summary>种类名（下拉选项、网格名与字典键三位一体）；顺序即整排从左到右的摆位顺序。</summary>
    private static readonly string[] Kinds = ["Box", "Sphere", "Cylinder", "Plane"];

    private readonly Dictionary<string, Mesh> meshes = new(StringComparer.Ordinal);

    private string readoutKind = "Box";

    private object? selectedKind;

    private double boxWidth = 2, boxHeight = 2, boxDepth = 2;
    private double sphereRadius = 1.4, sphereStacks = 48, sphereSlices = 24, sphereLongitude = 360;
    private double cylinderTopRadius = 1, cylinderBottomRadius = 1, cylinderHeight = 2.4, cylinderSegments = 48;
    private bool cylinderOpen;
    private double planeWidth = 3, planeHeight = 3, planeSectionsX = 8, planeSectionsZ = 8;
    private double rotationSpeed;

    private bool showGrid = true;
    private bool showAxes;

    /// <summary>
    /// 建好 VM：按当前参数建出四种几何体的网格并整排挂到 <see cref="Row"/> 上（等价于旧
    /// BuildScene 里的组装循环），下拉选中项对齐读数当前盯着的 Box（等价于旧构造函数里的
    /// <c>KindRow.SelectedItem = readoutKind</c>），再把读数的模板与计算参数装配起来。
    /// 几何/材质/棋盘纹路是纯 CPU 声明，不碰 GPU，构造期建全没有问题。
    /// </summary>
    public GeometriesViewModel()
    {
        KindOptions = Kinds;
        selectedKind = readoutKind;

        for (int index = 0; index < Kinds.Length; index++)
        {
            var kind = Kinds[index];

            var mesh = new Mesh
            {
                Name = kind,
                Geometry = Build(kind),
                Material = new Material(),
            };

            mesh.Material.SetTexture("BaseColor", Procedural.Checker(128, 4));
            mesh.Position = new Vector3((index - 1.5f) * 4.2f, 1.5f, 0);

            Row.AddChild(mesh, AttachToParentRule.KeepLocal);
            meshes[kind] = mesh;
        }

        // 读数模板 {0}..{2}：三个占位都是读数对象几何体上的计算参数，KB 按「顶点 × (3+2+3) × float」
        // 折算，与旧 Report 同一口径。meshes 在构造期已建全，null 兜底在正常流程走不到，
        // 只是防几何体缺席的形状。
        ReadoutText = Strings.Keys.Geometries_VertexReadout.CreateFormat()
            .Arg(() => ReadoutGeometry?.VertexCount ?? 0)
            .Arg(() => ReadoutGeometry?.IndicesCount ?? 0)
            .Arg(() => (long)(ReadoutGeometry?.VertexCount ?? 0) * (3 + 2 + 3) * sizeof(float) / 1024.0)
            .Build();
    }

    /// <summary>参数重建后请求一帧重绘；视图把它接到宿主的 InvalidateRender 上。</summary>
    public event Action? FrameRequested;

    /// <summary>地面网格开关变化；场景只有视图够得着，VM 不碰 DemoContext。</summary>
    public event Action<bool>? ShowGridToggled;

    /// <summary>坐标轴 gizmo 开关变化；同上，经事件桥回视图。</summary>
    public event Action<bool>? ShowAxesToggled;

    /// <summary>「读数对象」下拉的选项；种类名本身，不随语言换（API 名保持英文原文）。</summary>
    public IList KindOptions { get; }

    /// <summary>整排节点；四个网格挂在其下，旋转速度转的就是它。建场景时由视图挂进场景。</summary>
    public Node Row { get; } = new() { Name = "Row" };

    /// <summary>逐帧读数。语言切换时模板换新，绑定自动收到重排后的整句。</summary>
    public LinguaFormatObservable ReadoutText { get; }

    /// <summary>
    /// 逐帧调用：按旋转速度累加整排的 Y 角——静止（≤0）时跳过，等价旧 Update 的早退；
    /// <see cref="Row"/> 在构造期已建好，旧的 null 判从此免了。读数不在这里重读：
    /// 顶点/索引数只在换选（<see cref="SelectedKind"/>）与重建（Rebuild）时变，
    /// 两个变异点各自 <c>Refresh</c>，与旧 Report 的挂点一致。
    /// </summary>
    /// <param name="deltaTime">距上一帧的秒数。</param>
    public void Tick(double deltaTime)
    {
        if (rotationSpeed > 0)
            Row.RotationDegrees = new Vector3(0, Row.RotationDegrees.Y + (float)rotationSpeed * (float)deltaTime, 0);
    }

    /// <summary>页首说明。</summary>
    public IObservable<string?> IntroLabel => Strings.Instance.Geometries_Intro;

    /// <summary>「读数对象」下拉标签。</summary>
    public IObservable<string?> ReadoutKindLabel => Strings.Instance.Geometries_ReadoutKind;

    /// <summary>「Box 立方体」分组标题。</summary>
    public IObservable<string?> BoxSectionLabel => Strings.Instance.Geometries_Box;

    /// <summary>立方体宽标签。</summary>
    public IObservable<string?> BoxWidthLabel => Strings.Instance.Geometries_BoxWidth;

    /// <summary>立方体高标签。</summary>
    public IObservable<string?> BoxHeightLabel => Strings.Instance.Geometries_BoxHeight;

    /// <summary>立方体深标签。</summary>
    public IObservable<string?> BoxDepthLabel => Strings.Instance.Geometries_BoxDepth;

    /// <summary>「Sphere 球体」分组标题。</summary>
    public IObservable<string?> SphereSectionLabel => Strings.Instance.Geometries_Sphere;

    /// <summary>球体半径标签。</summary>
    public IObservable<string?> SphereRadiusLabel => Strings.Instance.Geometries_SphereRadius;

    /// <summary>球体水平分段标签。</summary>
    public IObservable<string?> SphereStacksLabel => Strings.Instance.Geometries_SphereStacks;

    /// <summary>球体垂直分段标签。</summary>
    public IObservable<string?> SphereSlicesLabel => Strings.Instance.Geometries_SphereSlices;

    /// <summary>球体经度范围标签。</summary>
    public IObservable<string?> SphereLongitudeLabel => Strings.Instance.Geometries_SphereLongitude;

    /// <summary>「Cylinder 圆柱/圆台」分组标题。</summary>
    public IObservable<string?> CylinderSectionLabel => Strings.Instance.Geometries_Cylinder;

    /// <summary>圆柱顶半径标签。</summary>
    public IObservable<string?> CylinderTopRadiusLabel => Strings.Instance.Geometries_CylinderTopRadius;

    /// <summary>圆柱底半径标签。</summary>
    public IObservable<string?> CylinderBottomRadiusLabel => Strings.Instance.Geometries_CylinderBottomRadius;

    /// <summary>圆柱高标签。</summary>
    public IObservable<string?> CylinderHeightLabel => Strings.Instance.Geometries_CylinderHeight;

    /// <summary>圆柱周向分段标签。</summary>
    public IObservable<string?> CylinderSegmentsLabel => Strings.Instance.Geometries_CylinderSegments;

    /// <summary>圆柱开口开关标签。</summary>
    public IObservable<string?> CylinderOpenLabel => Strings.Instance.Geometries_CylinderOpen;

    /// <summary>「Plane 平面」分组标题。</summary>
    public IObservable<string?> PlaneSectionLabel => Strings.Instance.Geometries_Plane;

    /// <summary>平面宽标签。</summary>
    public IObservable<string?> PlaneWidthLabel => Strings.Instance.Geometries_PlaneWidth;

    /// <summary>平面高标签。</summary>
    public IObservable<string?> PlaneHeightLabel => Strings.Instance.Geometries_PlaneHeight;

    /// <summary>平面横向分段标签。</summary>
    public IObservable<string?> PlaneSectionsXLabel => Strings.Instance.Geometries_PlaneSectionsX;

    /// <summary>平面纵向分段标签。</summary>
    public IObservable<string?> PlaneSectionsZLabel => Strings.Instance.Geometries_PlaneSectionsZ;

    /// <summary>「场景」分组标题。</summary>
    public IObservable<string?> SceneSectionLabel => Strings.Instance.Geometries_Scene;

    /// <summary>整排旋转速度滑杆标签。</summary>
    public IObservable<string?> RotationSpeedLabel => Strings.Instance.Geometries_RotationSpeed;

    /// <summary>地面网格开关标签。</summary>
    public IObservable<string?> GridLabel => Strings.Instance.Geometries_Grid;

    /// <summary>坐标轴 gizmo 开关标签。</summary>
    public IObservable<string?> AxesLabel => Strings.Instance.Geometries_Axes;

    /// <summary>读数行标题。</summary>
    public IObservable<string?> VertexDataLabel => Strings.Instance.Geometries_VertexData;

    /// <summary>
    /// 「读数对象」下拉的选中项；换一种几何体只把读数挪过去并立即重读——等价于旧
    /// OnKindChanged 里的「readoutKind 赋值 + Report」，不重建任何几何体。
    /// </summary>
    public object? SelectedKind
    {
        get => selectedKind;
        set
        {
            if (value == null || !SetProperty(ref selectedKind, value))
                return;

            readoutKind = (string)value;

            ReadoutText.Refresh();
        }
    }

    /// <summary>立方体宽；改了就重灌 Box 的顶点。</summary>
    public double BoxWidth
    {
        get => boxWidth;
        set
        {
            if (!SetProperty(ref boxWidth, value))
                return;

            Rebuild("Box");
        }
    }

    /// <summary>立方体高；口径同 <see cref="BoxWidth"/>。</summary>
    public double BoxHeight
    {
        get => boxHeight;
        set
        {
            if (!SetProperty(ref boxHeight, value))
                return;

            Rebuild("Box");
        }
    }

    /// <summary>立方体深；口径同 <see cref="BoxWidth"/>。</summary>
    public double BoxDepth
    {
        get => boxDepth;
        set
        {
            if (!SetProperty(ref boxDepth, value))
                return;

            Rebuild("Box");
        }
    }

    /// <summary>球体半径；改了就重灌 Sphere 的顶点。滑杆送来未取整的 double，
    /// 分段数一类 int 参数在消费点（<see cref="Build"/>）按 (int) 截断——与旧处理器同一口径。</summary>
    public double SphereRadius
    {
        get => sphereRadius;
        set
        {
            if (!SetProperty(ref sphereRadius, value))
                return;

            Rebuild("Sphere");
        }
    }

    /// <summary>球体水平分段；取整口径见 <see cref="SphereRadius"/>。</summary>
    public double SphereStacks
    {
        get => sphereStacks;
        set
        {
            if (!SetProperty(ref sphereStacks, value))
                return;

            Rebuild("Sphere");
        }
    }

    /// <summary>球体垂直分段；取整口径见 <see cref="SphereRadius"/>。</summary>
    public double SphereSlices
    {
        get => sphereSlices;
        set
        {
            if (!SetProperty(ref sphereSlices, value))
                return;

            Rebuild("Sphere");
        }
    }

    /// <summary>球体经度范围（度）；进构造参数时才换算成弧度。</summary>
    public double SphereLongitude
    {
        get => sphereLongitude;
        set
        {
            if (!SetProperty(ref sphereLongitude, value))
                return;

            Rebuild("Sphere");
        }
    }

    /// <summary>圆柱顶半径；改了就重灌 Cylinder 的顶点。上下半径分开调即可在圆柱/圆台间过渡。</summary>
    public double CylinderTopRadius
    {
        get => cylinderTopRadius;
        set
        {
            if (!SetProperty(ref cylinderTopRadius, value))
                return;

            Rebuild("Cylinder");
        }
    }

    /// <summary>圆柱底半径；口径同 <see cref="CylinderTopRadius"/>。</summary>
    public double CylinderBottomRadius
    {
        get => cylinderBottomRadius;
        set
        {
            if (!SetProperty(ref cylinderBottomRadius, value))
                return;

            Rebuild("Cylinder");
        }
    }

    /// <summary>圆柱高；口径同 <see cref="CylinderTopRadius"/>。</summary>
    public double CylinderHeight
    {
        get => cylinderHeight;
        set
        {
            if (!SetProperty(ref cylinderHeight, value))
                return;

            Rebuild("Cylinder");
        }
    }

    /// <summary>圆柱周向分段；取整口径见 <see cref="SphereRadius"/>。</summary>
    public double CylinderSegments
    {
        get => cylinderSegments;
        set
        {
            if (!SetProperty(ref cylinderSegments, value))
                return;

            Rebuild("Cylinder");
        }
    }

    /// <summary>圆柱开口（去掉端盖）；开关只会重建 Cylinder 自己。</summary>
    public bool CylinderOpen
    {
        get => cylinderOpen;
        set
        {
            if (!SetProperty(ref cylinderOpen, value))
                return;

            Rebuild("Cylinder");
        }
    }

    /// <summary>平面宽；改了就重灌 Plane 的顶点。</summary>
    public double PlaneWidth
    {
        get => planeWidth;
        set
        {
            if (!SetProperty(ref planeWidth, value))
                return;

            Rebuild("Plane");
        }
    }

    /// <summary>平面高；口径同 <see cref="PlaneWidth"/>。</summary>
    public double PlaneHeight
    {
        get => planeHeight;
        set
        {
            if (!SetProperty(ref planeHeight, value))
                return;

            Rebuild("Plane");
        }
    }

    /// <summary>平面横向分段；取整口径见 <see cref="SphereRadius"/>。</summary>
    public double PlaneSectionsX
    {
        get => planeSectionsX;
        set
        {
            if (!SetProperty(ref planeSectionsX, value))
                return;

            Rebuild("Plane");
        }
    }

    /// <summary>平面纵向分段；取整口径见 <see cref="SphereRadius"/>。</summary>
    public double PlaneSectionsZ
    {
        get => planeSectionsZ;
        set
        {
            if (!SetProperty(ref planeSectionsZ, value))
                return;

            Rebuild("Plane");
        }
    }

    /// <summary>整排旋转速度（度/秒）；只喂给逐帧的 <see cref="Tick"/> 累加角度，不触发任何
    /// 重建或排帧——画面本就由视图的连续渲染驱动着，这与旧 OnSpinChanged 只赋值不排帧一致。</summary>
    public double RotationSpeed
    {
        get => rotationSpeed;
        set => SetProperty(ref rotationSpeed, value);
    }

    /// <summary>地面网格开关；初值 true 对齐旧 XAML 写死的 IsChecked="True" 与旧 BuildScene
    /// 里的无条件 ShowGrid=true。场景只有视图够得着，经 <see cref="ShowGridToggled"/> 桥过去。</summary>
    public bool ShowGrid
    {
        get => showGrid;
        set
        {
            if (SetProperty(ref showGrid, value))
                ShowGridToggled?.Invoke(value);
        }
    }

    /// <summary>坐标轴 gizmo 开关；初值 false 对齐旧 XAML 写死的 IsChecked="False" 与引擎默认。</summary>
    public bool ShowAxes
    {
        get => showAxes;
        set
        {
            if (SetProperty(ref showAxes, value))
                ShowAxesToggled?.Invoke(value);
        }
    }

    /// <summary>读数对象当前的几何体；正常流程恒非空（meshes 构造期建全），缺席时读数按 0 显示。</summary>
    private Geometry? ReadoutGeometry => meshes.TryGetValue(readoutKind, out var mesh) ? mesh.Geometry : null;

    /// <summary>
    /// 按当前参数构造指定种类的几何体；与旧页面的 Build 逐行相同，仅字段名随属性对齐，
    /// int 参数的截断仍在同一消费点。
    /// </summary>
    private Geometry Build(string kind) => kind switch
    {
        "Box" => new BoxGeometry((float)boxWidth, (float)boxHeight, (float)boxDepth),
        "Sphere" => new SphereGeometry(
            (float)sphereRadius,
            (int)sphereStacks,
            (int)sphereSlices,
            phiLength: (float)(sphereLongitude * MathF.PI / 180f)),
        "Cylinder" => new CylinderGeometry(
            (float)cylinderTopRadius,
            (float)cylinderBottomRadius,
            (float)cylinderHeight,
            (int)cylinderSegments,
            4,
            cylinderOpen),
        _ => new PlaneGeometry((float)planeWidth, (float)planeHeight, (int)planeSectionsX, (int)planeSectionsZ),
    };

    /// <summary>
    /// 用当前参数重建指定那一种几何体：重灌顶点，读数对象被重建时顺手重读，再请求一帧重绘
    /// ——逐行等价旧 Rebuild（Geometry 赋值 + 按需 Report + InvalidateRender）。下拉只决定
    /// 读数读哪一个，所以改了参数不一定要重读读数。
    /// </summary>
    private void Rebuild(string kind)
    {
        if (meshes.TryGetValue(kind, out var mesh))
            mesh.Geometry = Build(kind);

        if (kind == readoutKind)
            ReadoutText.Refresh();

        FrameRequested?.Invoke();
    }
}
