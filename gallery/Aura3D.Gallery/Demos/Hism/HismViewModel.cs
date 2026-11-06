using Aura3D.Core.Geometries;
using Aura3D.Core.Nodes;
using Aura3D.Core.Resources;
using Aura3D.Gallery.Kit;
using Aura3D.Gallery.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Irihi.Lingua;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Aura3D.Gallery.Demos;

/// <summary>
/// HISM 页的界面状态（MVVM 化，照搬粒子页的分工）：
/// <list type="bullet">
///   <item><description>文案：AXAML 直接写 <c>{Translate}</c> 标记扩展取 Strings.Keys 键值；换语言走外壳既有的整页重挂。</description></item>
///   <item><description>参数：滑杆/开关/下拉双向绑定到这里的属性，setter 写穿到
///     <see cref="InstancedMeshGroup"/>（或先记字段、等建场景后生效）；
///     初值即原页面 XAML 里写死的那一份，由绑定初始化取代。</description></item>
///   <item><description>Readout：Lingua 1.1 的 format builder 组装，占位符全是八叉树状态，
///     只在树变化时重算——搬动步进、建树（重铺/清空/分组参数 Build）各自 Refresh，
///     与旧 Report 挂点一致（旧页清空后读数滞留到下次事件，这里顺带补上），语言切换时模板本身也会自动换。</description></item>
///   <item><description>引擎模型：实例变换表、随机分布（含种子复位）与 HISM 分组节点都住在这里，
///     视图只负责生命周期——建场景时取走分组节点挂进场景、逐帧驱动。</description></item>
/// </list>
/// 视图桥只有一个事件：<see cref="FrameRequested"/>——重铺/清空/剔除开关改完场景要排一次重绘，
/// VM 不碰 DemoContext，由视图把它接到宿主的 RequestFrame 上（原 InvalidateRender 与它同义）。
/// </summary>
public sealed partial class HismViewModel : ObservableObject
{
    private static readonly LinguaKey[] MoveModeKeys =
        [Strings.Keys.Hism_MoveNudge, Strings.Keys.Hism_MoveTeleport];

    private readonly List<Matrix4x4> transforms = [];

    private InstancedMeshGroup? group;

    // 固定种子的随机序列：重铺时复位成同一个，保证「重新铺满」得到与首铺完全相同的场地。
    private Random random = new(20260929);

    private int cursor;

    // moveModeIndex 与下拉选中项互为影子：比较/回写用下标，显示用字符串。
    private int moveModeIndex;
    private object? selectedMoveMode;

    // 滑杆是 double：数量/深度这几个整数参数在写穿时统一 (int) 换算，与旧事件处理一致。
    private double instanceCount = 12000;
    private double maxPerGroup = 512;
    private double maxDepth = 6;
    private double movesPerSecond = 30;

    private bool moveEveryFrame;
    private bool groupCulling = true;

    /// <summary>
    /// 建 VM：对齐下拉初值，再把 Readout 的模板与八叉树状态装配起来；命令经
    /// <c>[RelayCommand]</c> 源生成，无需在此接线。
    /// </summary>
    public HismViewModel()
    {
        MoveModeOptions = MoveModeKeys.Select(k => k.T()).ToList();
        selectedMoveMode = MoveModeOptions[moveModeIndex];

        // Readout 模板 {0}..{7}：实例数/组数/建树中/增量/重建/光标逐帧在读；
        // 每组上限与深度读字段，拖动滑杆时立刻反映，不必等树重建完。
        ReadoutText = Strings.Keys.Hism_OctreeReadout.CreateFormat()
            .Arg(() => group?.InstanceCount ?? 0)
            .Arg(() => group?.Groups.Count ?? 0)
            .Arg(() => group?.IsBuilding == true)
            .Arg(() => (int)maxPerGroup)
            .Arg(() => (int)maxDepth)
            .Arg(() => group?.InPlaceUpdateCount ?? 0)
            .Arg(() => group?.RebuildCount ?? 0)
            .Arg(() => cursor)
            .Build();
    }

    /// <summary>「移动方式」下拉的选项文案；当前语言的一份快照，随页面重建。</summary>
    public IList MoveModeOptions { get; }

    /// <summary>逐帧读数。语言切换时模板换新，绑定自动收到重排后的整句。</summary>
    public LinguaFormatObservable ReadoutText { get; }

    /// <summary>重铺、清空或剔除开关变化后请求一帧重绘；视图把它接到宿主的 RequestFrame 上。</summary>
    public event Action? FrameRequested;

    /// <summary>
    /// 建 HISM 分组节点（含茎秆源网格），铺满首批实例后交视图挂进场景——等价于旧
    /// BuildScene 尾部的组装。节点构造是纯 CPU，安全落在渲染线程回调里；返回前不碰场景，
    /// 所以 VM 不需要 DemoContext。建好之前参数 setter 只改字段，与旧页面 group==null 的行为一致。
    /// </summary>
    public InstancedMeshGroup CreateGroup()
    {
        group = new InstancedMeshGroup(CreateStalkMesh())
        {
            Name = "HISM",
            MaxInstancesPerGroup = (int)maxPerGroup,
            MaxDepth = (int)maxDepth,
        };

        RebuildField();

        return group;
    }

    /// <summary>
    /// 逐帧搬动实例（原 Update 的仿真部分）：光标按固定步长推进，落在哪个实例上与帧率无关；
    /// 增量模式只改本组缓冲，瞬移模式跨叶触发整树重建——两种代价差正是本页要演示的。
    /// 其余帧（未开搬动、树还没建或已清空）这里直接早退，重绘由视图统一请求。
    /// </summary>
    public void Update(double deltaTime)
    {
        if (!moveEveryFrame || group == null || group.InstanceCount == 0)
            return;

        var steps = Math.Max(1, (int)Math.Round(movesPerSecond * deltaTime));

        for (int s = 0; s < steps; s++)
        {
            cursor = (cursor + 977) % group.InstanceCount;

            group.UpdateInstance(cursor, moveModeIndex == 0 ? Nudge(cursor) : Teleport(cursor));
        }

        // 旧 Update 的 Report 位置：只有真的搬动了实例才重读八叉树统计，空闲帧不做事。
        ReadoutText.Refresh();
    }

    /// <summary>实例总数；改一次整场重铺（树按新集合重建）。</summary>
    public double InstanceCount
    {
        get => instanceCount;
        set
        {
            if (SetProperty(ref instanceCount, value))
                RebuildField();
        }
    }

    /// <summary>
    /// 每组实例上限；树已建时写穿并显式 Build 一次——写属性本身会作废在建任务，
    /// 但不再排队（引擎只在节点 Update 里按需重建），手动 Build 保持旧处理器的即时反馈。
    /// </summary>
    public double MaxPerGroup
    {
        get => maxPerGroup;
        set
        {
            if (!SetProperty(ref maxPerGroup, value))
                return;

            if (group != null)
            {
                group.MaxInstancesPerGroup = (int)value;
                group.Build();

                ReadoutText.Refresh();
            }
        }
    }

    /// <summary>八叉树深度上限；写穿与手动 Build 的口径同 <see cref="MaxPerGroup"/>。</summary>
    public double MaxDepth
    {
        get => maxDepth;
        set
        {
            if (!SetProperty(ref maxDepth, value))
                return;

            if (group != null)
            {
                group.MaxDepth = (int)value;
                group.Build();

                ReadoutText.Refresh();
            }
        }
    }

    /// <summary>移动方式下拉的选中项；只记下标，真正消费它的是逐帧搬动。</summary>
    public object? SelectedMoveMode
    {
        get => selectedMoveMode;
        set
        {
            if (value == null || !SetProperty(ref selectedMoveMode, value))
                return;

            moveModeIndex = Math.Max(0, MoveModeOptions.IndexOf(value));
        }
    }

    /// <summary>是否逐帧搬动实例；只改这里，Update 每帧来读。</summary>
    public bool MoveEveryFrame
    {
        get => moveEveryFrame;
        set => SetProperty(ref moveEveryFrame, value);
    }

    /// <summary>每秒搬动次数；换算成每帧步数在 Update 里做。</summary>
    public double MovesPerSecond
    {
        get => movesPerSecond;
        set => SetProperty(ref movesPerSecond, value);
    }

    /// <summary>
    /// 按组视锥剔除；只写穿到当前已建出的叶子分组——重建树会产生带引擎默认值的新叶子，
    /// 旧处理器同样不回头补写，这里保持同一口径（要再看效果就再拨一次开关）。
    /// </summary>
    public bool GroupCulling
    {
        get => groupCulling;
        set
        {
            if (!SetProperty(ref groupCulling, value))
                return;

            foreach (var leaf in group?.Groups ?? [])
                leaf.EnableFrustumCulling = value;

            FrameRequested?.Invoke();
        }
    }

    /// <summary>清空全部实例（读数归零、组卸载）；源生成 <c>ClearInstancesCommand</c> 供按钮绑定。</summary>
    [RelayCommand]
    private void ClearInstances()
    {
        group?.ClearInstances();

        // 旧页清空不刷新读数（要等下次搬动/重铺才归零），这里在变异点上直接重算。
        ReadoutText.Refresh();
        FrameRequested?.Invoke();
    }

    /// <summary>固定种子重铺整场；源生成 <c>RefillCommand</c>。</summary>
    [RelayCommand]
    private void Refill() => RebuildField();

    /// <summary>按当前实例数重新生成变换集合并提交建树；复位种子保证逐次结果一致。</summary>
    private void RebuildField()
    {
        transforms.Clear();

        random = new Random(20260929);

        for (int i = 0; i < (int)instanceCount; i++)
            transforms.Add(Scatter(i));

        group?.SetInstances(transforms);
        group?.Build();

        ReadoutText.Refresh();
        FrameRequested?.Invoke();
    }

    private Matrix4x4 Scatter(int index)
    {
        var plan = Ground(index);
        var position = new Vector3(plan.X, Terrain(plan.X, plan.Z), plan.Z);

        var height = 0.55f + (float)random.NextDouble() * 1.35f;
        var lean = (float)(random.NextDouble() - 0.5) * 0.5f;
        var twist = (float)(random.NextDouble() - 0.5) * 3.2f;

        return Matrix4x4.CreateScale(1f, height, 1f)
               * Matrix4x4.CreateRotationZ(lean)
               * Matrix4x4.CreateRotationY(twist)
               * Matrix4x4.CreateTranslation(position);
    }

    private static Vector3 Ground(int index)
    {
        // 分布只由下标决定，这样增量与瞬移两种模式改的是同一个实例。
        var angle = index * 2.399963f;
        var radius = 6f + 44f * MathF.Sqrt((index % 9973) / 9973f);

        return new Vector3(MathF.Cos(angle) * radius, 0f, MathF.Sin(angle) * radius);
    }

    private static float Terrain(float x, float z) =>
        1.3f * MathF.Sin(x * 0.045f) * MathF.Cos(z * 0.037f) + 0.6f;

    private Matrix4x4 Nudge(int index)
    {
        var position = Ground(index);

        position.X += 0.25f;
        position.Z += 0.15f;

        return Matrix4x4.CreateScale(1f, 1.1f, 1f)
               * Matrix4x4.CreateTranslation(new Vector3(position.X, Terrain(position.X, position.Z), position.Z));
    }

    private Matrix4x4 Teleport(int index)
    {
        var position = Ground(index);

        position.X = -position.X * 0.7f;
        position.Z = -position.Z * 0.7f;

        return Matrix4x4.CreateScale(1f, 1.6f, 1f)
               * Matrix4x4.CreateTranslation(new Vector3(position.X, Terrain(position.X, position.Z), position.Z));
    }

    private static Mesh CreateStalkMesh()
    {
        // 茎秆源网格：竖向渐变贴图让分组后的密度差异肉眼可辨。
        var mesh = new Mesh
        {
            Name = "Stalk",
            Geometry = new BoxGeometry(0.32f, 2.6f, 0.32f),
            Material = new Material(),
        };

        mesh.Material.SetTexture("BaseColor", Procedural.VerticalGradient(
            new Procedural.Rgb(120, 190, 110),
            new Procedural.Rgb(36, 74, 44),
            width: 4,
            height: 64));

        return mesh;
    }
}
