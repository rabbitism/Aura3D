using Aura3D.Core.Nodes;
using Aura3D.Core.Resources;
using Aura3D.Gallery.Kit;
using Aura3D.Gallery.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using Irihi.Lingua;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Aura3D.Gallery.Demos;

/// <summary>
/// 图元页的界面状态，按 Particles 页的样板 MVVM 化。分工：
/// <list type="bullet">
///   <item><description>文案：AXAML 直接写 <c>{Translate}</c> 标记扩展取 Strings.Keys 键值；换语言走外壳既有的整页重挂。</description></item>
///   <item><description>参数：滑杆/下拉双向绑定到这里的属性。列/行/波幅没有引擎对象可写穿——
///     它们只在顶点缓冲重建时被消费——所以 setter 的「写穿」就是按新参数把七个网格的几何体
///     重灌一遍；点尺寸写穿到点材质的 <c>uPointSize</c> uniform。初值即旧 XAML 行控件上
///     写死的那一份（12/6/0.9/12），由绑定初始化取代。</description></item>
///   <item><description>Readout：Lingua 1.1 的 format builder 组装 {0}..{2}——聚焦图元的顶点数、
///     索引数与折算图元数只在聚焦/重建时变，所以读数在 <see cref="SelectedKind"/> 与
///     <see cref="Rebuild"/> 两个变异点重算（与旧 Report 同时机），语言切换时模板本身也会自动换。</description></item>
///   <item><description>引擎模型：七个网格（手写几何体 + NoLight 覆盖材质 + 聚焦摆位）住在这里。
///     本页没有外部资产，构造期即可建全；视图只负责生命周期——建场景时把它们挂进场景、
///     逐帧驱动读数。</description></item>
/// </list>
/// 视图桥只有一个事件：<see cref="FrameRequested"/>（聚焦或参数变化要排一次重绘，等价旧处理器
/// 末尾的 InvalidateRender）。本页锁死 NoLight 管线的原因见视图头注。
/// </summary>
public sealed partial class PrimitivesViewModel : ObservableObject
{
    private static readonly (PrimitiveType Type, LinguaKey Label)[] Kinds =
    [
        (PrimitiveType.Points, Strings.Keys.Primitives_KindPoints),
        (PrimitiveType.Lines, Strings.Keys.Primitives_KindLines),
        (PrimitiveType.LineStrip, Strings.Keys.Primitives_KindLineStrip),
        (PrimitiveType.LineLoop, Strings.Keys.Primitives_KindLineLoop),
        (PrimitiveType.Triangles, Strings.Keys.Primitives_KindTriangles),
        (PrimitiveType.TriangleStrip, Strings.Keys.Primitives_KindTriangleStrip),
        (PrimitiveType.TriangleFan, Strings.Keys.Primitives_KindTriangleFan),
    ];

    private readonly Dictionary<PrimitiveType, Mesh> meshes = new();

    private Mesh? selected;

    private List<Kit.ComboOption<PrimitiveType>> kindOptions = [];

    private Kit.ComboOption<PrimitiveType>? selectedKind;

    private double columns = 12;
    private double rows = 6;
    private double amplitude = 0.9;
    private double pointSize = 12;

    /// <summary>
    /// 建好 VM：按当前参数建出七种图元的网格并默认聚焦 Triangles（等价于旧构造函数里
    /// <c>FocusRow.SelectedItem = KindOptions[4]</c> 加 BuildScene 的组装），再把读数的
    /// 模板与计算参数装配起来。几何/材质是纯 CPU 声明，不碰 GPU，构造期建全没有问题。
    /// </summary>
    public PrimitivesViewModel()
    {
        // 选项的 Name 从 Lingua 键取 observable（切语言自动换），Value 是图元种类——
        // 稳定值直接索引 meshes 字典，选中比较回到选项实例，不再依赖翻译后的文案。
        kindOptions = Kinds.Select(k => Kit.ComboOption.ForKey(k.Label, k.Type)).ToList();

        foreach (var (type, label) in Kinds)
        {
            var mesh = new Mesh
            {
                Name = label.T(),
                Geometry = BuildGeometry(type),
                Material = type == PrimitiveType.Points
                    ? Shaders.Points("NoLightPass", (float)pointSize)
                    : Shaders.VertexColors("NoLightPass"),
            };

            meshes[type] = mesh;
        }

        selectedKind = kindOptions[4];
        selected = meshes[selectedKind.Value];

        Layout();

        // 下拉的顺序即 Kinds 的顺序，登记进场景时也按它来，聚焦摆位（Layout）与旧页面逐位一致。
        Meshes = Kinds.Select(k => meshes[k.Type]).ToList();

        // 读数模板 {0}..{2}：三个占位都是聚焦图元几何体上的计算参数。selected 在构造尾已就位，
        // 首帧 Tick 之前绑定也有值可显；null 兜底（0）在正常流程走不到，只是防几何体缺席的形状。
        ReadoutText = Strings.Keys.Primitives_CurrentReadout.CreateFormat()
            .Arg(() => selected?.Geometry?.VertexCount ?? 0)
            .Arg(() => selected?.Geometry?.IndicesCount ?? 0)
            .Arg(() => selected?.Geometry is { } geometry ? EstimatedPrimitives(geometry) : 0)
            .Build();
    }

    /// <summary>聚焦或参数变化后请求一帧重绘；视图把它接到宿主的 InvalidateRender 上。</summary>
    public event Action? FrameRequested;

    /// <summary>「聚焦图元」下拉的选项：Name 跟随语言（Lingua 流绑定），Value 是图元种类（直接索引 meshes 字典）。</summary>
    public IList KindOptions => kindOptions;

    /// <summary>七种图元的网格（顺序同 Kinds）；建场景时由视图挂进场景。</summary>
    public IReadOnlyList<Mesh> Meshes { get; }

    /// <summary>聚焦/重建时重算的读数。语言切换时模板换新，绑定自动收到重排后的整句。</summary>
    public LinguaFormatObservable ReadoutText { get; }

    /// <summary>
    /// 「聚焦图元」下拉的选中项；选项实例即身份，Value 直接给出图元种类（旧实现按翻译后的
    /// 字符串 IndexOf 反查下标，换语言即脱钩），换一种图元就把聚焦挪过去、其余摆回相机背后。
    /// 旧处理器里「场景没建先放过」的早退不再需要：网格在 VM 构造期已建全，任何时刻改选
    /// 都落得到 Mesh 上，建场景时摆位一并生效。
    /// </summary>
    public Kit.ComboOption<PrimitiveType>? SelectedKind
    {
        get => selectedKind;
        set
        {
            if (value == null || !SetProperty(ref selectedKind, value))
                return;

            selected = meshes[value.Value];

            Layout();
            ReadoutText.Refresh();
            FrameRequested?.Invoke();
        }
    }

    /// <summary>列数（每行顶点数）；滑杆送来未取整的 double，重建几何体时按 (int) 截断——与旧处理器同一口径。</summary>
    public double Columns
    {
        get => columns;
        set
        {
            if (!SetProperty(ref columns, value))
                return;

            Rebuild();
        }
    }

    /// <summary>行数；取整口径同 <see cref="Columns"/>。</summary>
    public double Rows
    {
        get => rows;
        set
        {
            if (!SetProperty(ref rows, value))
                return;

            Rebuild();
        }
    }

    /// <summary>Z 向波幅；连续值，直接按 (float) 进几何体。</summary>
    public double Amplitude
    {
        get => amplitude;
        set
        {
            if (!SetProperty(ref amplitude, value))
                return;

            Rebuild();
        }
    }

    /// <summary>
    /// 点尺寸（像素）；写穿到点材质的 <c>uPointSize</c> uniform。建场景前改它也只改 VM 里的材质，
    /// 场景建好后连续生效，与旧处理器「有网格才写」的差别只是省了一次判空。
    /// </summary>
    public double PointSize
    {
        get => pointSize;
        set
        {
            if (!SetProperty(ref pointSize, value))
                return;

            if (meshes.TryGetValue(PrimitiveType.Points, out var points))
                points.Material?.SetParameterValue("uPointSize", (float)value);

            FrameRequested?.Invoke();
        }
    }

    /// <summary>
    /// 手写几何体：顶点位置按列/行铺成网格，Y 分量叠一圈正弦波（波幅 <c>amplitude</c>），
    /// 顶点色按 UV 渐变走槽位 2；索引按图元种类另算（见 <see cref="AppendIndices"/>）。
    /// 与旧页面的 BuildGeometry 逐行相同，仅把 int 参数的截断挪到了消费点。
    /// </summary>
    private Geometry BuildGeometry(PrimitiveType type)
    {
        // 滑杆是 double 绑定，旧事件处理器在赋值字段时就截断成 int；这里在同一消费点截断，
        // 几何体拿到的数与旧页面一致。
        int columnCount = (int)columns;
        int rowCount = (int)rows;

        var positions = new List<float>();
        var colors = new List<float>();
        var indices = new List<uint>();

        for (int row = 0; row < rowCount; row++)
        {
            for (int column = 0; column < columnCount; column++)
            {
                var u = columnCount == 1 ? 0f : column / (float)(columnCount - 1);
                var v = rowCount == 1 ? 0f : row / (float)(rowCount - 1);

                positions.Add((u - 0.5f) * 8f);
                positions.Add(0.6f + MathF.Sin(u * MathF.PI * 2f) * (float)amplitude * MathF.Cos(v * MathF.PI));
                positions.Add((v - 0.5f) * 6f);

                var color = new Vector4(0.25f + 0.7f * u, 0.35f + 0.5f * v, 0.85f - 0.5f * u, 1f);

                colors.Add(color.X);
                colors.Add(color.Y);
                colors.Add(color.Z);
                colors.Add(color.W);
            }
        }

        AppendIndices(type, columnCount, rowCount, indices);

        var geometry = new Geometry { PrimitiveType = type };

        geometry.SetVertexAttribute(BuildInVertexAttribute.Position, 3, positions);
        geometry.SetVertexAttribute(BuildInVertexAttribute.Color_0, 4, colors);
        geometry.SetIndices(indices);

        return geometry;
    }

    /// <summary>
    /// 各图元的索引写法差别：点与线只数顶点，三角形族才需要真正的三角形拓扑。
    /// </summary>
    private static void AppendIndices(PrimitiveType type, int columns, int rows, List<uint> indices)
    {
        int stride = columns;

        switch (type)
        {
            case PrimitiveType.Triangles:
                for (int row = 0; row + 1 < rows; row++)
                {
                    for (int column = 0; column + 1 < columns; column++)
                    {
                        int upper = row * stride + column;
                        int lower = upper + stride;

                        indices.Add((uint)upper);
                        indices.Add((uint)lower);
                        indices.Add((uint)(upper + 1));

                        indices.Add((uint)(upper + 1));
                        indices.Add((uint)lower);
                        indices.Add((uint)(lower + 1));
                    }
                }

                break;

            case PrimitiveType.TriangleStrip:
            case PrimitiveType.TriangleFan:
                for (int row = 0; row + 1 < rows; row++)
                {
                    for (int column = 0; column < columns; column++)
                    {
                        indices.Add((uint)(row * stride + column));
                        indices.Add((uint)((row + 1) * stride + column));
                    }
                }

                break;

            default:
                for (int i = 0; i < rows * columns; i++)
                    indices.Add((uint)i);

                break;
        }
    }

    /// <summary>按当前参数重灌七个几何体并请求重绘；读数不用手动排——逐帧 Tick 会重读。</summary>
    private void Rebuild()
    {
        foreach (var (type, _) in Kinds)
            meshes[type].Geometry = BuildGeometry(type);

        Layout();
        ReadoutText.Refresh();
        FrameRequested?.Invoke();
    }

    private void Layout()
    {
        for (int i = 0; i < Kinds.Length; i++)
        {
            var mesh = meshes[Kinds[i].Type];

            // 聚焦某一种图元时，其余挪到相机背后：留在原地会互相遮挡，看不出连线规则。
            mesh.Position = ReferenceEquals(mesh, selected)
                ? new Vector3(0, 0, 0)
                : new Vector3(0, -50f - i, 0);
        }
    }

    private static double EstimatedPrimitives(Geometry geometry) => geometry.PrimitiveType switch
    {
        PrimitiveType.Points => geometry.VertexCount,
        PrimitiveType.Lines => geometry.IndicesCount / 2.0,
        PrimitiveType.LineStrip => Math.Max(geometry.IndicesCount - 1, 0),
        PrimitiveType.LineLoop => geometry.IndicesCount,
        PrimitiveType.Triangles => geometry.IndicesCount / 3.0,
        PrimitiveType.TriangleStrip => Math.Max(geometry.IndicesCount - 2, 0),
        _ => Math.Max(geometry.IndicesCount - 2, 0),
    };
}
