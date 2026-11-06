using Aura3D.Core.Nodes;
using Aura3D.Core.Resources;
using Aura3D.Gallery.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Irihi.Lingua;
using System;
using System.Collections.Generic;
using System.Numerics;
using ModelNode = Aura3D.Core.Nodes.Model;

namespace Aura3D.Gallery.Demos;

/// <summary>
/// 动画混合页的界面状态，按 Particles/FbxAnimation 的样板 MVVM 化。分工：
/// <list type="bullet">
///   <item><description>文案：AXAML 直接写 <c>{Translate}</c> 标记扩展取 Strings.Keys 键值；换语言走外壳既有的整页重挂。</description></item>
///   <item><description>参数：滑杆/开关双向绑定到这里的属性，setter 写穿状态机节点与混合空间；
///     初值即旧页字段的默认值，旧 AXAML 里那批 <c>Value=</c>/<c>IsChecked=</c> 字面量由绑定初始化取代。</description></item>
///   <item><description>Readout：混合空间一行是纯模板读数（5 个占位全是计算参数），format builder
///     组装、逐帧 <see cref="Tick"/> 重读；状态机一行是「动态前缀 + 模板」的双段拼接——前缀
///     （无状态机回落 / CurrentWeight 实时值）不是 resx 占位，format builder 承不住整句切换，
///     按 FbxAnimation 取样行的先例走 <see cref="GraphReadoutText"/> 普通 string 属性逐帧重算。</description></item>
///   <item><description>引擎模型：两份模型节点（原图 + SharedResource 克隆）、两个采样器
///     （状态机 / 混合空间）与剪辑字典都住在这里；视图只负责取资产喂 <see cref="SetWarrior"/>、
///     建场景时按旧顺序调 <see cref="BuildGraph"/> → <see cref="CreateBlendClone"/> →
///     <see cref="BuildBlendSpace"/> 并登记节点。</description></item>
///   <item><description>BlendSpacePad：编辑板本体与指针事件留在视图。用户拖动经
///     <see cref="SetAxisFromPad"/> 推进来；反过来扫掠/Reset 改的轴值由视图每帧读
///     <see cref="AxisX"/>/<see cref="AxisY"/> 调 <c>BlendSpacePad.SetAxis</c> 回显——那个方法
///     只挪滑块、不发事件，「扫掠驱动 UI」和「用户接管」两条路不会互相打架。</description></item>
/// </list>
/// 视图桥只有 <see cref="FrameRequested"/>（参数变化后排一次重绘，等价旧处理里的 InvalidateRender）；
/// 逐帧 RequestFrame 仍由视图的 Update 负责，VM 全程不碰 DemoContext。
/// </summary>
public sealed partial class AnimationMixViewModel : ObservableObject
{
    // 状态机的三个状态，按数组顺序循环：Idle → Walking → Running → Walking → Idle
    private static readonly string[] StateClips = ["Idle", "Walking_A", "Running_A"];

    // 混合空间的采样点摆成十字，两轴都是 [-1,1]：竖轴前后、横轴左右，原点静止。
    // 端点放跑、半程放走——KayKit 没有后退跑和侧移走，所以下端点只能用后退走顶上，
    // 左右半程也没有对应的走路剪辑，只能空着。
    // 建页时视图按它给 BlendSpacePad 摆灰点标签，建场景时这里按它加采样器，一份表两处共用。
    public static readonly (string Clip, Vector2 Point)[] BlendSamples =
    [
        ("Idle", new Vector2(0f, 0f)),
        ("Running_A", new Vector2(0f, 1f)),
        ("Walking_A", new Vector2(0f, 0.5f)),
        ("Walking_Backwards", new Vector2(0f, -1f)),
        ("Running_Strafe_Left", new Vector2(-1f, 0f)),
        ("Running_Strafe_Right", new Vector2(1f, 0f)),
    ];

    /// <summary>
    /// 建 VM：装配混合空间读数的模板；状态机读数先按「无状态机」前缀算一遍初值，
    /// 资产到位前不空行，建好场景的当帧就被真值覆盖。命令经 <c>[RelayCommand]</c> 源生成，
    /// 无需在此接线。
    /// </summary>
    public AnimationMixViewModel()
    {
        // 混合空间读数模板 {0}..{4}：两轴值、IdwPower、采样点数与骨骼矩阵数——轴值随扫掠/拖动
        // 逐帧变，其余在装配与交互时才变，全按重读型占位组装。
        BlendReadoutText = Strings.Keys.AnimationMix_BlendReadout.CreateFormat()
            .Arg(() => axisX)
            .Arg(() => axisY)
            .Arg(() => idwPower)
            .Arg(() => blendPoints)
            .Arg(() => blendSpace?.BonesTransform.Count ?? 0)
            .Build();

        UpdateGraphReadout();
    }

    /// <summary>参数变化后排一次重绘；视图把它接到宿主的 RequestFrame 上（InvalidateRender 与其同源）。</summary>
    public event Action? FrameRequested;

    /// <summary>资产阶段取回的原图模型；视图建场景时摆位、登记并挂骨骼附件。</summary>
    public ModelNode? WarriorModel => idleRunModel;

    /// <summary>当前横轴值。不进绑定体系：视图每帧读它回显编辑板滑块。</summary>
    public float AxisX => axisX;

    /// <summary>当前纵轴值；扫掠、拖动与 Reset 都会改它。</summary>
    public float AxisY => axisY;

    /// <summary>
    /// 逐帧重算的状态机读数：前缀双态（无状态机回落键 / CurrentWeight 实时值）+ 分隔点 + 模板句，
    /// 与旧页 Update 里的拼接逐行等价。前缀不在 resx 占位里，所以不走 format builder；
    /// 相同字符串经 SetProperty 去重，不动绑定。
    /// </summary>
    public string GraphReadoutText
    {
        get => graphReadoutText;
        private set => SetProperty(ref graphReadoutText, value);
    }

    /// <summary>混合空间读数。语言切换时模板换新，绑定自动收到重排后的整句。</summary>
    public LinguaFormatObservable BlendReadoutText { get; }

    /// <summary>
    /// 逐帧调用：推进两个时钟（状态环自动切换、轴值自动扫掠）并重读两条读数。
    /// 注意扫掠分支与旧页保持一致——只更新轴值等视图回显编辑板，不调
    /// <c>blendSpace.SetAxis</c> 喂引擎（旧页就没喂，行为原样保留）。
    /// </summary>
    /// <param name="deltaTime">距上一帧的秒数。</param>
    public void Tick(double deltaTime)
    {
        clock += deltaTime;

        if (autoToggle && clock > toggleSeconds)
        {
            clock = 0;

            AdvanceState();
        }

        if (autoSweep)
        {
            sweepClock = (sweepClock + deltaTime) % sweepPeriod;

            // 沿竖轴余弦往返：0(静止) → 0.5(走路) → 1(前进跑) 再回来。
            // 引擎侧不另喂轴值——扫掠改的只有 UI 滑块与读数，姿势仍是最近一次拖动/Reset 的轴。
            axisY = (1f - (float)Math.Cos(2 * Math.PI * sweepClock / sweepPeriod)) / 2f;
        }

        UpdateGraphReadout();

        BlendReadoutText.Refresh();
    }

    /// <summary>
    /// 资产阶段由视图喂进取回的模型与剪辑表（旧 LoadAssetsAsync 的中段）：
    /// 命名、留作状态机的宿主，剪辑按名入字典供两套采样器查。
    /// </summary>
    public void SetWarrior(ModelNode model, IEnumerable<Animation> animations)
    {
        model.Name = "WarriorGraph";

        idleRunModel = model;

        foreach (var animation in animations)
            byName[animation.Name] = animation;
    }

    /// <summary>
    /// 建状态机（旧 BuildGraph 逐行照搬）：三个节点按 <c>stateIndex</c> 互连成环挂到原模型。
    /// 转移谓词读的是本 VM 的 <c>stateIndex</c>，自动/手动推进都在改同一个字段。
    /// </summary>
    public void BuildGraph()
    {
        if (idleRunModel?.Skeleton == null)
            return;

        if (!byName.TryGetValue(StateClips[0], out var idle) ||
            !byName.TryGetValue(StateClips[1], out var walk) ||
            !byName.TryGetValue(StateClips[2], out var run))
            return;

        var idleNode = new AnimationGraphNode(new AnimationSampler(idle)) { BlendTime = (float)graphBlendTime };
        var walkNode = new AnimationGraphNode(new AnimationSampler(walk)) { BlendTime = (float)graphBlendTime };
        var runNode = new AnimationGraphNode(new AnimationSampler(run)) { BlendTime = (float)graphBlendTime };

        // 状态环：Idle → Walking → Running → Walking → Idle。转移条件就是「目标态是不是自己」。
        idleNode.AddNextNode((_, _) => stateIndex == 1, walkNode);
        walkNode.AddNextNode((_, _) => stateIndex == 2, runNode);
        walkNode.AddNextNode((_, _) => stateIndex == 0, idleNode);
        runNode.AddNextNode((_, _) => stateIndex == 1, walkNode);

        graphNodes.Clear();
        graphNodes.Add(idleNode);
        graphNodes.Add(walkNode);
        graphNodes.Add(runNode);

        graph = new AnimationGraph(idleRunModel.Skeleton, idleNode);

        idleRunModel.AnimationSampler = graph;
    }

    /// <summary>
    /// 右半边的克隆模型（旧 BuildScene 的中段）：SharedResource 连骨骼都共用，
    /// 两个采样器各写自己的 BoneMatrixBuffer。时机与旧页一致——在 BuildGraph 之后、
    /// 挂骨骼附件之前（attach 要两份模型各自处理一遍）。
    /// </summary>
    public ModelNode? CreateBlendClone()
    {
        if (idleRunModel == null)
            return null;

        blendModel = idleRunModel.Clone(CopyType.SharedResource);
        blendModel.Name = "WarriorBlend";

        return blendModel;
    }

    /// <summary>建混合空间（旧 BuildBlendSpace 逐行照搬）：按 BlendSamples 摆采样点挂到克隆模型，收尾以当前轴值落一次位。</summary>
    public void BuildBlendSpace()
    {
        if (blendModel?.Skeleton == null)
            return;

        blendSpace = new AnimationBlendSpace(blendModel.Skeleton) { IdwPower = (float)idwPower };

        foreach (var (clip, point) in BlendSamples)
            AddPoint(clip, point);

        blendSpace.InitializePose();

        blendModel.AnimationSampler = blendSpace;

        ApplyAxis();
    }

    /// <summary>
    /// 编辑板拖动回调：用户接管轴值。开着扫掠时先经属性关掉——TwoWay 绑定会把界面开关拨回，
    /// 否则下一帧滑块就被扫掠值拽回去；随后把轴值写穿到混合空间。
    /// </summary>
    public void SetAxisFromPad(double x, double y)
    {
        axisX = (float)x;
        axisY = (float)y;

        if (autoSweep)
            AutoSweep = false;

        ApplyAxis();
    }

    /// <summary>切到下一个状态；源生成 <c>ManualSwitchCommand</c> 供按钮绑定。</summary>
    [RelayCommand]
    private void ManualSwitch()
    {
        AdvanceState();

        FrameRequested?.Invoke();
    }

    /// <summary>轴值与扫掠计时归零、混合空间自身 Reset；源生成 <c>ResetAxesCommand</c>。编辑板滑块由视图逐帧同步回显。</summary>
    [RelayCommand]
    private void ResetAxes()
    {
        blendSpace?.Reset();

        axisX = 0;
        axisY = 0;

        sweepClock = 0;

        FrameRequested?.Invoke();
    }

    private ModelNode? idleRunModel;
    private ModelNode? blendModel;
    private AnimationGraph? graph;
    private AnimationBlendSpace? blendSpace;

    private readonly Dictionary<string, Animation> byName = [];
    private readonly List<AnimationGraphNode> graphNodes = [];

    private int blendPoints;

    private int stateIndex;
    private bool autoToggle = true;
    private double toggleSeconds = 2.5;
    private double clock;
    private double graphBlendTime = 0.6;

    private float axisX;
    private float axisY;
    private double idwPower = 2;

    // 速度轴自动扫掠：X 在 0(静止)→1(跑步)→0 之间余弦往返，直接演示连续过渡
    private bool autoSweep = true;
    private double sweepPeriod = 8;
    private double sweepClock;

    private string graphReadoutText = string.Empty;

    /// <summary>沿状态环自动转圈。</summary>
    public bool AutoToggle
    {
        get => autoToggle;
        set => SetProperty(ref autoToggle, value);
    }

    /// <summary>每个状态停留的秒数。</summary>
    public double ToggleSeconds
    {
        get => toggleSeconds;
        set => SetProperty(ref toggleSeconds, value);
    }

    /// <summary>状态机的交叉淡化时长（秒）；写穿到三个节点，转移判定立即读新值。</summary>
    public double BlendTime
    {
        get => graphBlendTime;
        set
        {
            if (!SetProperty(ref graphBlendTime, value))
                return;

            foreach (var node in graphNodes)
                node.BlendTime = (float)value;

            FrameRequested?.Invoke();
        }
    }

    /// <summary>反距离权重的幂；写穿到混合空间。</summary>
    public double IdwPower
    {
        get => idwPower;
        set
        {
            if (!SetProperty(ref idwPower, value))
                return;

            if (blendSpace != null)
                blendSpace.IdwPower = (float)value;

            FrameRequested?.Invoke();
        }
    }

    /// <summary>
    /// 自动扫掠前进。重新打开时把扫掠时钟清零，从静止开始一个完整的 静止→跑→静止 循环；
    /// 关闭既可以来自界面开关、也可以来自拖动编辑板（见 <see cref="SetAxisFromPad"/>）。
    /// </summary>
    public bool AutoSweep
    {
        get => autoSweep;
        set
        {
            if (!SetProperty(ref autoSweep, value))
                return;

            if (autoSweep)
                sweepClock = 0;
        }
    }

    /// <summary>扫掠一个来回的秒数；钳到 ≥1，防余弦相位算出除零。</summary>
    public double SweepPeriod
    {
        get => sweepPeriod;
        set => SetProperty(ref sweepPeriod, Math.Max(1, value));
    }

    // 沿 Idle → Walking → Running → Idle 的环推进一格：转移谓词读的就是这个字段
    private void AdvanceState() => stateIndex = (stateIndex + 1) % StateClips.Length;

    private void AddPoint(string name, Vector2 at)
    {
        if (blendSpace == null || !byName.TryGetValue(name, out var animation))
            return;

        blendSpace.AddAnimationSampler(at, new AnimationSampler(animation));

        blendPoints++;
    }

    private void ApplyAxis()
    {
        blendSpace?.SetAxis(axisX, axisY);

        FrameRequested?.Invoke();
    }

    // 状态机读数的整句：前缀双态 + 模板句（目标剪辑与 BlendTime），逐帧重排。
    private void UpdateGraphReadout()
    {
        GraphReadoutText =
            $"{(graph == null ? Strings.Keys.AnimationMix_NoGraph.T() : $"CurrentWeight={graph.CurrentWeight:0.###}")} · " +
            Strings.Keys.AnimationMix_GraphReadout.Format(StateClips[stateIndex], graphBlendTime);
    }
}
