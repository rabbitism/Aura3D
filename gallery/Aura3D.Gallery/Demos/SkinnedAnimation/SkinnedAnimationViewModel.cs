using Aura3D.Core.Geometries;
using Aura3D.Core.Nodes;
using Aura3D.Core.Particles;
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
using Color = System.Drawing.Color;
using ModelNode = Aura3D.Core.Nodes.Model;

namespace Aura3D.Gallery.Demos;

/// <summary>
/// 蒙皮动画页的界面状态，按 Particles/AnimationMix 的样板 MVVM 化。分工：
/// <list type="bullet">
///   <item><description>文案：AXAML 直接写 <c>{Translate}</c> 标记扩展取 Strings.Keys 键值；换语言走外壳既有的整页重挂。</description></item>
///   <item><description>参数：剪辑/循环模式下拉、倍率滑杆与三个开关双向绑定到这里的属性，
///     setter 写穿采样器与骨骼附件；剪辑下拉的选项要等 glb 的 animations 数组，由视图建场景时调
///     <see cref="InitializeClips"/> 回填并初选（初选写穿建第一个采样器挂上模型）。</description></item>
///   <item><description>Readout：两行都是纯模板读数（播放状态 7 占位、骨骼挑选 5 占位），
///     Lingua 1.1 的 format builder 组装，占位语义与旧 Report/ReportBones 里的 string.Format
///     逐个对齐，逐帧 <see cref="Tick"/> 重读。</description></item>
///   <item><description>引擎模型：士兵模型、采样器、骨骼附件与整支火把（四路发射器、点光源）
///     都住在这里；视图只负责取资产喂 <see cref="SetSoldier"/>/<see cref="SetFireSheet"/>、
///     建场景时按旧顺序登记模型与附件并调 <see cref="StartFlame"/> 开喷、逐帧驱动摇曳与读数。</description></item>
/// </list>
/// 视图桥两个事件：<see cref="FrameRequested"/>（参数变化后排一次重绘，等价旧处理里的
/// InvalidateRender）与 <see cref="ShowBoneToggled"/>（调试开关写在宿主的 PipelineSettings 上，
/// VM 不碰 DemoContext）；逐帧 RequestFrame 仍由视图的 Update 负责。
/// </summary>
public sealed partial class SkinnedAnimationViewModel : ObservableObject
{
    // 程序化贴图是纯 CPU 资源，构造期就能生成；火焰翻页图来自资产（ParticleFirePng，8×8 共 64 帧）。
    private static readonly Texture SoftDot = Procedural.SoftDot(128, 2.4f);
    private static readonly Texture Spark = Procedural.Spark(64, 64);

    // 与引擎 LoopMode 枚举同序（Once/Loop/PingPong）：下标即枚举值，ctor 的 ForKey 按它直转，
    // 选中比较从此走枚举 Value，不再按翻译后的字符串回查下标。
    private static readonly LinguaKey[] LoopModeKeys =
        [Strings.Keys.SkinnedAnimation_LoopModeOnce, Strings.Keys.SkinnedAnimation_LoopModeLoop, Strings.Keys.SkinnedAnimation_LoopModePingPong];

    /// <summary>火把点光源的基准亮度，<see cref="Tick"/> 里在这个值附近做三频叠加的轻微摇曳。</summary>
    private const float TorchLightIntensity = 600f;

    /// <summary>
    /// 火把根节点相对手骨的朝向（度）：(-90, 0, 0) 把柄轴（火把本地 +Y）扳到手骨的 -Z 上——
    /// 这就是掌心握持的方向。依据：glb 里食指根 (LeftHandIndex1) 与尾指根 (LeftHandPinky1)
    /// 相对手骨的偏移分别落在局部 Z 的两端（-2.9 / +5.6），拇指根偏向 -X，
    /// 所以拳心握棒沿"指节线"＝局部 Z 轴、掌心法线＝局部 X 轴；柄轴垂直于手指穿过掌心，
    /// 取朝上的一支（局部 -Z，世界仰角约 +23°，火口朝身体前上方）。
    /// 之前试过把柄轴对齐世界竖直方向，读起来像在手背上插了根旗杆，反而不像握着。
    /// </summary>
    private static readonly Vector3 TorchTiltDegrees = new(-90f, 0f, 0f);

    /// <summary>
    /// 火把根节点沿手骨 +Y（模型局部单位，即厘米）的握点偏移，落在掌心而不是腕关节。
    /// 该偏移先于骨骼矩阵生效，所以用的是模型局部单位。
    /// </summary>
    private static readonly Vector3 TorchGripOffset = new(0f, 6f, 0f);

    // 火把各部件的配色：木柄、皮握把、铁件、炭杯与炭芯。
    private static readonly Color Wood = Color.FromArgb(255, 116, 78, 48);
    private static readonly Color Leather = Color.FromArgb(255, 72, 48, 32);
    private static readonly Color Iron = Color.FromArgb(255, 58, 56, 54);
    private static readonly Color Charcoal = Color.FromArgb(255, 38, 34, 32);
    private static readonly Color Ember = Color.FromArgb(255, 255, 148, 40);

    /// <summary>
    /// 建 VM：装好循环模式下拉的选项与初选，再把两条模板读数装配起来。剪辑下拉的选项要等
    /// glb 的动画数组，留到 <see cref="InitializeClips"/> 再填。命令经 <c>[RelayCommand]</c>
    /// 源生成，无需在此接线。
    /// </summary>
    public SkinnedAnimationViewModel()
    {
        // 循环模式选项：下标与引擎 LoopMode 枚举同序，ForKey 按下标直转枚举 Value；
        // Name 走 Lingua 流，换语言时下拉文字由流绑定自动换。
        var loopModeList = LoopModeKeys.Select((k, i) => Kit.ComboOption.ForKey(k, (LoopMode)i)).ToList();

        LoopModeOptions = loopModeList;

        // 初选按引擎当前值匹配：AnimationSampler.LoopMode 的默认就是 Loop（旧页 loopModeIndex=0
        // 同一个口径，此时还没有采样器实例可读）；由绑定初始化替旧页的 SelectedItem 赋值。
        selectedLoopMode = loopModeList.Single(o => o.Value == LoopMode.Loop);

        // 播放状态模板 {0}..{6}：剪辑名/时长、TimeScale、LoopMode、推进方、手动累计与蒙皮判定。
        // 占位语义与旧 Report() 逐个对齐——无剪辑回落 "-"、无采样器时浮点/枚举占位是空串、
        // 推进方与蒙皮判定按当前语言取键值，全是重读型参数。
        PlaybackReadoutText = Strings.Keys.SkinnedAnimation_Readout.CreateFormat()
            .Arg(() => CurrentClip?.Name ?? "-")
            .Arg(() => CurrentClip?.Duration.ToString("0.##") ?? "-")
            .Arg(() => sampler?.TimeScale)
            .Arg(() => sampler?.LoopMode)
            .Arg(() => externalUpdate
                       ? Strings.Keys.SkinnedAnimation_DriverManual.T()
                       : Strings.Keys.SkinnedAnimation_DriverAuto.T())
            .Arg(() => manualStepClock)
            .Arg(() => model?.IsSkinnedModel == true
                       ? Strings.Keys.SkinnedAnimation_Yes.T()
                       : Strings.Keys.SkinnedAnimation_No.T())
            .Build();

        // 骨骼挑选模板 {0}..{4}：骨骼数、挂载骨骼名（未挑中回落键值）、骨骼索引、骨骼矩阵条数与
        // 偏航角。GetBoneIndex 走缓存表，逐帧读不重建。旧 ReportBones 在骨架为空时早退、保留旧行
        // 文案；这里资产到位前会先显示一排零值，建好场景的当帧就被真值覆盖。
        BoneReadoutText = Strings.Keys.SkinnedAnimation_BoneReadout.CreateFormat()
            .Arg(() => model?.Skeleton?.Bones.Count ?? 0)
            .Arg(() => attachment?.BoneName ?? Strings.Keys.SkinnedAnimation_BoneNotPicked.T())
            .Arg(() => attachment != null && model?.Skeleton is { } skeleton
                       ? skeleton.GetBoneIndex(attachment.BoneName)
                       : -1)
            .Arg(() => sampler?.BonesTransform.Count ?? 0)
            .Arg(() => attachmentYaw)
            .Build();
    }

    /// <summary>参数变化后排一次重绘；视图把它接到宿主的 RequestFrame 上（InvalidateRender 与其同源）。</summary>
    public event Action? FrameRequested;

    /// <summary>「Debug.ShowBone」开关变化；宿主的调试设置只有视图够得着。</summary>
    public event Action<bool>? ShowBoneToggled;

    /// <summary>资产阶段取回的士兵模型；视图建场景时摆位（朝向、包围盒适配）并登记进场景。</summary>
    public ModelNode? SoldierModel => model;

    /// <summary>循环模式下拉的选项：Name 是 Lingua 流（随语言自动换），Value 直接是引擎的 LoopMode 枚举。</summary>
    public IList LoopModeOptions { get; }

    /// <summary>播放状态读数。语言切换时模板换新，绑定自动收到重排后的整句。</summary>
    public LinguaFormatObservable PlaybackReadoutText { get; }

    /// <summary>骨骼挑选读数。随语言自动换、在挂载建立（<see cref="StartFlame"/>）与偏航变化时重算。</summary>
    public LinguaFormatObservable BoneReadoutText { get; }

    /// <summary>
    /// 逐帧调用：摇曳火光（旧 Update 的中段——三个互质频率的正弦叠加，确定性、无随机数，
    /// 在基准亮度的 ±18% 内摆动），再重读播放状态读数（手动推进的累计时钟逐帧变，
    /// 旧 Update 本就每帧 Report）。骨骼读数不在这里重读：它只在挂载建立与偏航变化时变，
    /// 两个变异点各自 Refresh。
    /// </summary>
    /// <param name="deltaTime">距上一帧的秒数。</param>
    public void Tick(double deltaTime)
    {
        torchClock += (float)deltaTime;

        if (torchLight != null)
            torchLight.LuminousIntensity = TorchLightIntensity *
                (0.86f
                 + 0.09f * MathF.Sin(torchClock * 11.3f)
                 + 0.05f * MathF.Sin(torchClock * 27.7f + 1.9f)
                 + 0.04f * MathF.Sin(torchClock * 4.1f + 0.6f));

        PlaybackReadoutText.Refresh();
    }

    /// <summary>资产阶段由视图喂进取回的模型与剪辑表（旧 LoadAssetsAsync 的中段）：命名并留作采样器宿主。</summary>
    public void SetSoldier(ModelNode loaded, List<Animation> clips)
    {
        loaded.Name = "Soldier";

        model = loaded;
        animations = clips;
    }

    /// <summary>火把火焰用的翻页图（8×8 共 64 帧）；与粒子页共用同一份资产，构造期发射器先拿到空。</summary>
    public void SetFireSheet(Texture sheet) => fireSheet = sheet;

    /// <summary>
    /// 建场景时由视图调（旧 BuildScene 的中段）：把 glb 的 animations 数组排成剪辑下拉的选项
    /// （剪辑名是语言无关的引擎标识，字面量名直出），经 <see cref="SelectedClip"/> 回填初选，
    /// 再按旧页口径建第一个采样器挂上模型。必须在相机的 FitToBoundingBox 之前——采样器构造
    /// 立即算第一帧姿势，包围盒跟着姿势走。
    /// </summary>
    public void InitializeClips()
    {
        ClipOptions = animations.Select(a => Kit.ComboOption.Literal(a.Name, a.Name)).ToList();

        // 空表没有可选项可选，SelectedClip 保持 null（旧「—」占位只是视图层的空态，不再伪造选项）。
        SelectedClip = clipOptionList.Count > 0 ? clipOptionList[clipIndex] : null;

        // 旧代码在填完下拉后无条件 CreateSampler(clipIndex)（空表会在 animations[0] 越界崩掉）；
        // 这里按「有剪辑才建」收口，正常路径逐行等价。SelectedClip 的写穿分支要求采样器已存在，
        // 所以这第一个采样器由这里直接建，之后的换剪辑才走属性。
        if (clipOptionList.Count > 0 && model != null)
        {
            sampler = CreateSampler(clipIndex);

            model.AnimationSampler = sampler;
        }
    }

    /// <summary>
    /// 建骨骼附件与整支火把（旧 SetupAttachment 的建段）：按「含 hand 且非 end → hand → head」
    /// 挑手骨、挑蒙皮网格，组装 <see cref="BoneAttachment"/> 并把火把整体挂上去。
    /// 登记进场景由视图做，登记完调 <see cref="StartFlame"/> 开喷。
    /// </summary>
    public BoneAttachment? BuildAttachment()
    {
        if (model == null || model.Skeleton == null)
            return null;

        var names = model.Skeleton.Bones.Select(b => b.Name).ToList();

        var boneName = names.FirstOrDefault(n => n.Contains("hand", StringComparison.OrdinalIgnoreCase)
                                                 && !n.Contains("end", StringComparison.OrdinalIgnoreCase))
                       ?? names.FirstOrDefault(n => n.Contains("hand", StringComparison.OrdinalIgnoreCase))
                       ?? names.FirstOrDefault(n => n.Contains("head", StringComparison.OrdinalIgnoreCase));

        if (boneName == null)
            return null;

        var targetMesh = model.Meshes.FirstOrDefault(m => m.IsSkinnedMesh) ?? model.Meshes.FirstOrDefault();

        if (targetMesh == null)
            return null;

        attachment = new BoneAttachment
        {
            Name = "HandAttachment",
            Mesh = targetMesh,
            BoneName = boneName,
            LocalOffset = GripOffset((float)attachmentYaw),

            // glTF 的场景根节点带着 0.01 的整体缩放（模型按厘米建模）。不归一化的话，
            // 按米建模的火把会被缩小一百倍，在手里只剩几个像素。
            NormalizeScale = true,
            Enable = showAttachment,
        };

        BuildTorch();

        return attachment;
    }

    /// <summary>
    /// 附件登记进场景后由视图调一次（保持旧页 AddNode→Play 的顺序）。发射原点取
    /// <see cref="ParticleSystem"/> 的世界变换，Play 之后每帧都会跟着骨骼矩阵走。
    /// 本页没有粒子暂停开关，VM 是这套火焰播放态的唯一改写者，不存在暂停后 Play 早退的问题。
    /// </summary>
    public void StartFlame()
    {
        flame?.Play();

        // 旧页在附件登记、开喷之后调 ReportBones——骨骼读数的首个真值由这里带出。
        BoneReadoutText.Refresh();
    }

    /// <summary>采样器时间归零（回到 0 秒）；源生成 <c>ResetCommand</c> 供按钮绑定。</summary>
    [RelayCommand]
    private void Reset()
    {
        sampler?.Reset();

        FrameRequested?.Invoke();
    }

    /// <summary>手动喂采样器 1/30 秒；源生成 <c>AdvanceStepCommand</c>。累计时长进播放状态读数。</summary>
    [RelayCommand]
    private void AdvanceStep()
    {
        sampler?.Update(1.0 / 30.0);

        manualStepClock += 1.0 / 30.0;

        FrameRequested?.Invoke();
    }

    private ModelNode? model;
    private List<Animation> animations = [];

    private AnimationSampler? sampler;
    private BoneAttachment? attachment;
    private Node? torch;
    private PointLight? torchLight;
    private ParticleSystem? flame;

    // 火焰翻页图（8×8 共 64 帧），资产阶段由视图喂进来。
    private Texture? fireSheet;

    // 播放状态读数里的「当前剪辑」：旧 Report() 里那个 current 局部变量，空表/越界回落 null。
    private Animation? CurrentClip => clipIndex >= 0 && clipIndex < animations.Count ? animations[clipIndex] : null;

    private int clipIndex;
    private Kit.ComboOption<string>? selectedClip;
    private double timeScale = 1;
    private Kit.ComboOption<LoopMode>? selectedLoopMode;
    private bool externalUpdate;
    private bool showBone = true;
    private bool showAttachment = true;
    private double attachmentYaw;
    private List<Kit.ComboOption<string>> clipOptionList = [];

    private float torchClock;
    private double manualStepClock;

    /// <summary>
    /// 剪辑下拉的选项（Name/Value 都是剪辑名这一引擎标识）；资产到位前是空的，建场景时由
    /// <see cref="InitializeClips"/> 换上新列表并经此发出通知，所以是可通知属性而不是构造期常量。
    /// </summary>
    public IList ClipOptions
    {
        get => clipOptionList;
        private set => SetProperty(ref clipOptionList, (List<Kit.ComboOption<string>>)value);
    }

    /// <summary>
    /// 剪辑下拉的选中项；换剪辑即换模型节点上的采样器（每次新建，时间与循环模式带入当前值）。
    /// 选项实例即身份，Value 给出剪辑名，按它在强类型表里定位下标（FindIndex 落空只记下标、
    /// 不动采样器——旧 OnClipChanged 的口径）。
    /// </summary>
    public Kit.ComboOption<string>? SelectedClip
    {
        get => selectedClip;
        set
        {
            if (value == null || !SetProperty(ref selectedClip, value))
                return;

            clipIndex = clipOptionList.FindIndex(o => o.Value == value.Value);

            if (sampler != null && model != null && clipIndex >= 0)
            {
                sampler = CreateSampler(clipIndex);

                model.AnimationSampler = sampler;
            }
        }
    }

    /// <summary>采样器时间倍率；写穿当前采样器。</summary>
    public double TimeScale
    {
        get => timeScale;
        set
        {
            if (!SetProperty(ref timeScale, value))
                return;

            if (sampler != null)
                sampler.TimeScale = (float)value;

            FrameRequested?.Invoke();
        }
    }

    /// <summary>循环模式下拉的选中项；选项的 Value 即引擎枚举，直接写穿当前采样器的 LoopMode。</summary>
    public Kit.ComboOption<LoopMode>? SelectedLoopMode
    {
        get => selectedLoopMode;
        set
        {
            if (value == null || !SetProperty(ref selectedLoopMode, value))
                return;

            if (sampler != null)
                sampler.LoopMode = value.Value;

            FrameRequested?.Invoke();
        }
    }

    /// <summary>把推进权交回本页：勾上后 Model.Update 不再推进采样器，靠「手动推进」按钮喂固定步长。</summary>
    public bool ExternalUpdate
    {
        get => externalUpdate;
        set
        {
            if (!SetProperty(ref externalUpdate, value))
                return;

            if (sampler != null)
                sampler.ExternalUpdate = value;

            FrameRequested?.Invoke();
        }
    }

    /// <summary>骨骼线调试开关；宿主的调试设置只有视图够得着，经 <see cref="ShowBoneToggled"/> 桥接。</summary>
    public bool ShowBone
    {
        get => showBone;
        set
        {
            if (SetProperty(ref showBone, value))
                ShowBoneToggled?.Invoke(value);
        }
    }

    /// <summary>骨骼附件总开关；写穿 <see cref="BoneAttachment.Enable"/>。</summary>
    public bool ShowAttachment
    {
        get => showAttachment;
        set
        {
            if (!SetProperty(ref showAttachment, value))
                return;

            if (attachment != null)
                attachment.Enable = value;

            FrameRequested?.Invoke();
        }
    }

    /// <summary>握点偏移绕手骨 Y 轴的偏航（度）；写穿 <see cref="BoneAttachment.LocalOffset"/>。</summary>
    public double AttachmentYaw
    {
        get => attachmentYaw;
        set
        {
            if (!SetProperty(ref attachmentYaw, value))
                return;

            if (attachment != null)
                attachment.LocalOffset = GripOffset((float)value);

            // 旧偏航处理器的 ReportBones：读数里的偏航角与挂载信息跟着变。
            BoneReadoutText.Refresh();

            FrameRequested?.Invoke();
        }
    }

    // 换剪辑每次都新建采样器（旧 CreateSampler 照搬）：时间倍率与循环模式带入当前值——循环模式
    // 直接取选中项的枚举 Value（ctor 保证已按引擎默认初选，旧 ModeOf 的下标翻译随之删掉）；
    // ExternalUpdate 故意不带——旧页就是这个口径（开关只写当时的采样器，换剪辑即回到引擎默认的自动）。
    private AnimationSampler CreateSampler(int index)
    {
        var animation = animations[index];

        return new AnimationSampler(animation)
        {
            TimeScale = (float)timeScale,
            LoopMode = selectedLoopMode!.Value,
        };
    }

    /// <summary>握点偏移：绕手骨 Y 轴偏航一圈，再沿手骨推到掌心。</summary>
    private static Matrix4x4 GripOffset(float yawDegrees) =>
        Matrix4x4.CreateRotationY(yawDegrees * MathF.PI / 180f)
        * Matrix4x4.CreateTranslation(TorchGripOffset);

    /// <summary>
    /// 用方块拼一支拿得出手的火把：木柄（手握的那段缠两圈皮条）、铁箍、燃料方杯、
    /// 顶端炭芯与外撇八度的四根护笼柱；杯口一路粒子系统分四路发射器——
    /// 翻页火焰、柔光辉光、上蹿下落的火星、慢烟——最后挂一盏会摇曳的点光源。
    /// 全部子节点挂在 torch 根下，随 BoneAttachment 一起被骨骼矩阵拽着走。
    /// </summary>
    private void BuildTorch()
    {
        torch = new Node
        {
            Name = "Torch",
            RotationDegrees = TorchTiltDegrees,
        };

        // 手握段：从掌心往下伸一截，缠两圈皮条防滑
        AddBox("TorchGrip", 0.058f, 0.26f, 0.058f, new Vector3(0, -0.13f, 0), Wood);
        AddBox("TorchWrapA", 0.078f, 0.034f, 0.078f, new Vector3(0, -0.19f, 0), Leather);
        AddBox("TorchWrapB", 0.078f, 0.034f, 0.078f, new Vector3(0, -0.07f, 0), Leather);

        // 上半段略细，套一圈铁箍过渡到燃料杯
        AddBox("TorchShaft", 0.05f, 0.1f, 0.05f, new Vector3(0, 0.05f, 0), Wood);
        AddBox("TorchCollar", 0.088f, 0.036f, 0.088f, new Vector3(0, 0.115f, 0), Iron);

        // 燃料方杯与顶端炭芯：炭芯颜色给足饱和度，让点光源把它烧亮
        AddBox("TorchCup", 0.108f, 0.09f, 0.108f, new Vector3(0, 0.195f, 0), Charcoal);
        AddBox("TorchCoal", 0.062f, 0.045f, 0.062f, new Vector3(0, 0.255f, 0), Ember);

        // 护笼：四根细方柱各向外撇 8°，加两根十字横箍
        var barSpecs = new (float X, float Z, float Pitch, float Roll)[]
        {
            ( 1f,  1f,  8f,  0f),
            (-1f,  1f, -8f,  0f),
            ( 1f, -1f,  0f,  8f),
            (-1f, -1f,  0f, -8f),
        };

        for (var i = 0; i < barSpecs.Length; i++)
        {
            var (x, z, pitch, roll) = barSpecs[i];

            var bar = AddBox($"TorchBar{i}", 0.016f, 0.15f, 0.016f,
                new Vector3(0.054f * x, 0.275f, 0.054f * z), Iron);

            bar.RotationDegrees = new Vector3(pitch, 0, roll);
        }

        AddBox("TorchBandX", 0.152f, 0.018f, 0.018f, new Vector3(0, 0.215f, 0), Iron);
        AddBox("TorchBandZ", 0.018f, 0.018f, 0.152f, new Vector3(0, 0.215f, 0), Iron);

        // 杯口火焰：四路发射器共用一个系统，原点取本节点的世界变换，跟着手跑
        flame = new ParticleSystem
        {
            Name = "TorchFlame",
            MaxParticles = 512,
            Position = new Vector3(0, 0.3f, 0),
        };

        flame.Emitters.Add(FlameCoreEmitter());
        flame.Emitters.Add(FlameGlowEmitter());
        flame.Emitters.Add(EmberEmitter());
        flame.Emitters.Add(SmokeEmitter());

        torch.AddChild(flame, AttachToParentRule.KeepLocal);

        // 点光源挂在杯口正上方，火光主要喂给持火把的手臂和半个身体
        torchLight = new PointLight
        {
            Name = "TorchLight",
            LightColor = Color.Orange,
            LuminousIntensity = TorchLightIntensity,
            AttenuationRadius = 6f,
            Position = new Vector3(0, 0.33f, 0),
        };

        torch!.AddChild(torchLight, AttachToParentRule.KeepLocal);

        attachment!.AddChild(torch, AttachToParentRule.KeepLocal);
    }

    private Mesh AddBox(string name, float width, float height, float depth, Vector3 position, Color color)
    {
        var mesh = new Mesh
        {
            Name = name,
            Geometry = new BoxGeometry(width, height, depth),
            Material = new Material(),
            Position = position,
        };

        mesh.Material.SetTexture("BaseColor", Texture.CreateFromColor(color));

        torch!.AddChild(mesh, AttachToParentRule.KeepLocal);

        return mesh;
    }

    /// <summary>火焰主体：翻页火焰图按寿命走完 64 帧，亮黄起、透红收。</summary>
    private ParticleEmitter FlameCoreEmitter() => new()
    {
        Shape = EmissionShape.Cone,
        ConeAngle = 11f,
        ShapeSize = new Vector3(0.035f, 0.02f, 0.035f),
        EmissionRate = 170f,
        Lifetime = new RangeFloat(0.16f, 0.3f),
        Velocity = new RangeVector3(new Vector3(-0.12f, 0.55f, -0.12f), new Vector3(0.12f, 0.95f, 0.12f)),
        StartSize = new RangeFloat(0.13f, 0.19f),
        EndSize = new RangeFloat(0.01f, 0.03f),
        StartColor = Color.FromArgb(255, 255, 236, 160),
        EndColor = Color.FromArgb(0, 255, 90, 20),
        Rotation = new RangeFloat(0f, MathF.PI * 2f),
        AngularVelocity = new RangeFloat(-3f, 3f),
        Gravity = new Vector3(0, 1.1f, 0),
        Damping = 1.6f,
        Texture = fireSheet,
        FlipbookTiles = new Vector2(8, 8),
        BlendMode = BlendMode.Translucent,
        MaxParticles = 256,
    };

    /// <summary>柔光辉光：无纹理感的光团，负责把杯口糊成一片亮，遮住方块拼接的棱角。</summary>
    private ParticleEmitter FlameGlowEmitter() => new()
    {
        Shape = EmissionShape.Sphere,
        ShapeSize = new Vector3(0.03f, 0.03f, 0.03f),
        EmissionRate = 90f,
        Lifetime = new RangeFloat(0.12f, 0.2f),
        Velocity = new RangeVector3(new Vector3(-0.06f, 0.4f, -0.06f), new Vector3(0.06f, 0.7f, 0.06f)),
        StartSize = new RangeFloat(0.1f, 0.16f),
        EndSize = new RangeFloat(0.01f, 0.03f),
        StartColor = Color.FromArgb(230, 255, 190, 90),
        EndColor = Color.FromArgb(0, 255, 120, 30),
        Gravity = new Vector3(0, 0.8f, 0),
        Texture = SoftDot,
        BlendMode = BlendMode.Translucent,
        MaxParticles = 128,
    };

    /// <summary>火星：小火点上蹿、被负重力拉回抛物线，冷却成暗红后消失。</summary>
    private ParticleEmitter EmberEmitter() => new()
    {
        Shape = EmissionShape.Sphere,
        ShapeSize = new Vector3(0.03f, 0.03f, 0.03f),
        EmissionRate = 22f,
        Lifetime = new RangeFloat(0.5f, 1f),
        Velocity = new RangeVector3(new Vector3(-0.3f, 1.2f, -0.3f), new Vector3(0.3f, 2.2f, 0.3f)),
        StartSize = new RangeFloat(0.012f, 0.022f),
        EndSize = new RangeFloat(0f, 0.004f),
        StartColor = Color.FromArgb(255, 255, 214, 130),
        EndColor = Color.FromArgb(0, 255, 70, 10),
        Gravity = new Vector3(0, -2.2f, 0),
        Damping = 0.4f,
        Texture = Spark,
        BlendMode = BlendMode.Translucent,
        MaxParticles = 128,
    };

    /// <summary>慢烟：给火焰上方一点体积感，尺寸随寿命涨大、透明度归零。</summary>
    private ParticleEmitter SmokeEmitter() => new()
    {
        Shape = EmissionShape.Cone,
        ConeAngle = 16f,
        ShapeSize = new Vector3(0.03f, 0.01f, 0.03f),
        EmissionRate = 9f,
        Lifetime = new RangeFloat(0.9f, 1.6f),
        Velocity = new RangeVector3(new Vector3(-0.08f, 0.5f, -0.08f), new Vector3(0.08f, 0.85f, 0.08f)),
        StartSize = new RangeFloat(0.05f, 0.08f),
        EndSize = new RangeFloat(0.24f, 0.34f),
        StartColor = Color.FromArgb(70, 120, 118, 116),
        EndColor = Color.FromArgb(0, 120, 118, 116),
        Rotation = new RangeFloat(0f, MathF.PI * 2f),
        AngularVelocity = new RangeFloat(-1f, 1f),
        Gravity = new Vector3(0, 0.5f, 0),
        Texture = SoftDot,
        BlendMode = BlendMode.Translucent,
        MaxParticles = 64,
    };
}
