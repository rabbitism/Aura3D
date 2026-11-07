using Aura3D.Core.Resources;
using Aura3D.Gallery.Kit;
using Aura3D.Gallery.Localization;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using Irihi.Lingua;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using ModelNode = Aura3D.Core.Nodes.Model;

namespace Aura3D.Gallery.Demos;

/// <summary>
/// FBX 动作库页的界面状态，按 Particles 页的样板 MVVM 化。分工：
/// <list type="bullet">
///   <item><description>文案：AXAML 直接写 <c>{Translate}</c> 标记扩展取 Strings.Keys 键值；换语言走外壳既有的整页重挂。</description></item>
///   <item><description>参数：剪辑/骨骼下拉与取样滑杆双向绑定到这里的属性；换剪辑写穿到模型节点的
///     AnimationSampler（第 0 项「不挂采样器」写 null），换骨骼与取样时刻只改取样读数。</description></item>
///   <item><description>Readout：骨架与剪辑表两行是纯模板读数，Lingua 1.1 的 format builder 组装，
///     逐帧 <see cref="Tick"/> 重读；取样读数是「模板 + 无剪辑回落」的双态行——format builder
///     换不了整句模板（占位格式串与回落句是两个键），所以按非模板动态字符串走
///     <see cref="SampleText"/> 普通 string 属性，在三个输入变化处重算。</description></item>
///   <item><description>引擎模型：模型节点、挂在它身上的采样器与取回的剪辑表都住在这里；
///     视图只负责取资产喂进来、建场景时把节点登记进场景并调 <see cref="PrepareBones"/> 回填骨骼名单。</description></item>
/// </list>
/// 视图桥一个事件都没有：换剪辑后的画面由模型节点逐帧自走（Model.Update 驱动采样器），
/// 重绘沿用旧页就有的逐帧 RequestFrame（在视图的 Update 里），VM 全程不碰 DemoContext。
/// </summary>
public sealed partial class FbxAnimationViewModel : ObservableObject
{
    // 标签与动作文件一一对应：ufbx 报的剪辑名是文件里的 take 名，这两个文件都存成 "Unreal Take"，
    // 当不了选项文字，所以标签按「哪个文件来的」给；剪辑表与采样读数仍照实读文件里的名与时长。
    // 视图取资产时按 AssetKey 循环这同一份表，避免清单在视图与 VM 里各抄一份。
    public static readonly (string Label, string AssetKey)[] MotionFiles =
    [
        ("Idle_Rifle_Hip", "FbxIdle"),
        ("Jog_Fwd_Rifle", "FbxJogFwd"),
    ];

    /// <summary>
    /// 建 VM：装好剪辑下拉的选项与初选（第 0 项是要翻译的「不挂采样器」，文件名标签不随语言走），
    /// 再把两条模板读数装配起来。取样读数的初值直接给「无剪辑」句——资产到位前这就是实情，
    /// 也省得等第一次交互才把空行填上。
    /// </summary>
    public FbxAnimationViewModel()
    {
        // 第 0 项「不挂采样器」走 Lingua 键（要翻译），其余是动作文件标签（语言无关的文件标识，
        // 字面量名直出）；Value 统一用下拉序号承载稳定身份——0 即不挂，i+1 即第 i 个动作文件，
        // 选中比较走选项实例，不再按显示字符串回查下标。
        var clipOptionList = MotionFiles.Select((file, i) => Kit.ComboOption.Literal(file.Label, i + 1))
            .Prepend(Kit.ComboOption.ForKey(Strings.Keys.FbxAnimation_NoSampler, 0))
            .ToList();

        ClipOptions = clipOptionList;

        // 初选对齐旧页面构造函数里的 ClipRow.SelectedItem = ClipOptions[1]（clipIndex 初值 1），
        // 现在由绑定初始化替它完成。
        selectedClip = clipOptionList[clipIndex];

        // 剪辑表模板 {0}..{3}：占位语义与旧 Report() 里的 string.Format 逐个对齐——当前项、
        // 条数、名字(时长)拼接（空表落到「无」）、采样器骨骼变换数。当前项与拼接都是重读型：
        // 换剪辑 / 模型就位两个变异点上 Refresh 带出新值。当前项不能捕获某个选项的 Name 流——
        // 那会把占位钉死在构造时的那一项上，所以按 clipIndex 现取显示名（第 0 项重取键值，其余取标签）。
        ClipsReadoutText = Strings.Keys.FbxAnimation_ClipTableReadout.CreateFormat()
            .Arg(() => clipIndex == 0
                           ? Strings.Keys.FbxAnimation_NoSampler.T()
                           : MotionFiles[clipIndex - 1].Label)
            .Arg(() => clips.Count)
            .Arg(() =>
            {
                var table = string.Join(
                    Strings.Keys.FbxAnimation_ListSeparator.T(),
                    clips.Select(c => $"{c.Name}({c.Duration:0.##}s)"));

                return table.Length > 0 ? table : Strings.Keys.FbxAnimation_None.T();
            })
            .Arg(() => sampler?.BonesTransform.Count ?? 0)
            .Build();

        // 骨架读数模板 {0}..{3}：骨骼数、根名、索引表项数（GetBoneIndexMap 有缓存，逐帧读不重建）、
        // 剪辑骨架是否同一实例。旧代码在骨架为空时早退、保留旧行文案；这里资产到位前会先显示一排
        // 零值，建好场景的当帧就被真值覆盖。
        SkeletonReadoutText = Strings.Keys.FbxAnimation_SkeletonReadout.CreateFormat()
            .Arg(() => model?.Skeleton?.Bones.Count ?? 0)
            .Arg(() => model?.Skeleton?.Root?.Name ?? "-")
            .Arg(() => model?.Skeleton?.GetBoneIndexMap().Count ?? 0)
            .Arg(() => clips.Count > 0
                       && model?.Skeleton is { } skeleton
                       && ReferenceEquals(clips[0].Skeleton, skeleton)
                ? Strings.Keys.FbxAnimation_SkeletonSame.T()
                : Strings.Keys.FbxAnimation_SkeletonDiff.T())
            .Build();

        sampleText = Strings.Keys.FbxAnimation_NoClip.T();
    }

    /// <summary>资产阶段取回的模型节点；剪辑下拉从此写穿它上面的 AnimationSampler。</summary>
    public ModelNode? Model => model;

    /// <summary>「挂上的动作」下拉的选项：第 0 项的 Name 是 Lingua 流（随语言自动换），其余是
    /// 动作文件标签；Value 是下拉序号（0 = 不挂采样器）。</summary>
    public IList ClipOptions { get; }

    /// <summary>骨骼名单：资产到位前是空的，<see cref="PrepareBones"/> 里就地补齐。
    /// 仍用 AvaloniaList—— ComboRow 绑定它，补齐发生在渲染线程回调里，沿用旧页同一集合类型不打乱时序；
    /// 选项的 Name/Value 都是骨骼名这一引擎标识。</summary>
    public AvaloniaList<Kit.ComboOption<string>> BoneOptions { get; } = [];

    /// <summary>资产阶段由视图喂进取回的模型（旧页的 model 字段）。</summary>
    public void SetModel(ModelNode loadedModel) => model = loadedModel;

    /// <summary>
    /// 资产阶段按动作文件喂剪辑：同一文件可能带多条，旧代码全部入表且 byLabel 后写覆盖前写
    /// （一个文件多条时下拉只指到最后一条），这里保持同一口径。
    /// </summary>
    public void AddMotionClips(string label, IEnumerable<Animation> found)
    {
        foreach (var animation in found)
        {
            clips.Add(animation);

            byLabel[label] = animation;
        }
    }

    /// <summary>
    /// 建场景时由视图调（旧 BuildScene 中段的那一串）：挂初选剪辑、把骨骼名单补进
    /// <see cref="BoneOptions"/>、挑默认骨骼并经 <see cref="SelectedBone"/> 回填下拉初选，
    /// 最后算一遍取样读数。顺序上必须在相机的 FitToBoundingBox 之前——采样器构造函数
    /// 立即算第一帧姿势，包围盒跟着姿势走。
    /// </summary>
    public void PrepareBones()
    {
        ApplyClip();

        BoneOptions.Clear();

        foreach (var name in model?.Skeleton?.Bones.Select(b => b.Name) ?? [])
            BoneOptions.Add(Kit.ComboOption.Literal(name, name));

        // PickBone 给的是骨骼名，包装回选项实例再走 SelectedBone（选项实例即身份，不再按字符串匹配）；
        // 空骨架挑不出名字时回落 null，取样读数自然落到「没有可取样的剪辑」。
        var picked = PickBone();

        SelectedBone = BoneOptions.FirstOrDefault(o => o.Value == picked);

        // 旧 SetModel 尾部的 Report：两条模板读数的首个真值由这里带出。
        ClipsReadoutText.Refresh();
        SkeletonReadoutText.Refresh();

        UpdateSampleText();
    }

    /// <summary>
    /// 当前选中项对应的动作文件标签（byLabel 的键）：Value 即下拉序号。第 0 项「不挂采样器」
    /// 没有对应的动作文件，给空串让字典查不到、取数端走 clips[0] 回落（旧口径是拿「不挂采样器」
    /// 这句文案当键查，同样查不到任何剪辑，行为一致）。
    /// </summary>
    private string ClipLabel => clipIndex > 0 ? MotionFiles[clipIndex - 1].Label : string.Empty;

    private ModelNode? model;
    private readonly List<Animation> clips = [];
    private readonly Dictionary<string, Animation> byLabel = [];

    private AnimationSampler? sampler;

    // 选中项实例即身份：Value（下拉序号）写进 clipIndex 驱动引擎侧，显示交给选项自己的 Name 流。
    private int clipIndex = 1;
    private Kit.ComboOption<int>? selectedClip;

    private Kit.ComboOption<string>? selectedBone;
    private double sampleTime;

    private string sampleText;

    /// <summary>「挂上的动作」下拉的选中项；选项实例即身份，Value（下拉序号）直接驱动换剪辑
    /// ——换模型节点上的采样器，并重算取样读数。</summary>
    public Kit.ComboOption<int>? SelectedClip
    {
        get => selectedClip;
        set
        {
            if (value == null || !SetProperty(ref selectedClip, value))
                return;

            clipIndex = value.Value;

            ApplyClip();

            // 旧换剪辑处理器的 Report：当前项与采样器骨骼变换数在这里跟着换。
            ClipsReadoutText.Refresh();
            SkeletonReadoutText.Refresh();

            UpdateSampleText();
        }
    }

    /// <summary>骨骼下拉的选中项；选项实例即身份，Value 即骨骼名，只影响取样读数在哪根骨骼上取样。</summary>
    public Kit.ComboOption<string>? SelectedBone
    {
        get => selectedBone;
        set
        {
            if (value == null || !SetProperty(ref selectedBone, value))
                return;

            UpdateSampleText();
        }
    }

    /// <summary>取样时刻（秒）；滑杆把时间钉住，读数用它直接调 Animation.Sample。</summary>
    public double SampleTime
    {
        get => sampleTime;
        set
        {
            if (SetProperty(ref sampleTime, value))
                UpdateSampleText();
        }
    }

    /// <summary>
    /// 取样读数。这行是双态：有剪辑且选了骨骼时是 7 占位模板（剪辑名、时刻、骨骼名、位移三分量、
    /// 通道数），否则整句换成「没有可取样的剪辑」——两个键、两种句式，Lingua 的 format builder
    /// 承不住整句切换，所以按「非模板动态字符串」走普通 string + SetProperty，在换剪辑、
    /// 换骨骼、拖时刻三处重算。语言切换由外壳重挂整页（连同本 VM 一起换新）自然换句。
    /// </summary>
    public string SampleText
    {
        get => sampleText;
        private set => SetProperty(ref sampleText, value);
    }

    /// <summary>剪辑表读数。语言切换时模板换新，绑定自动收到重排后的整句。</summary>
    public LinguaFormatObservable ClipsReadoutText { get; }

    /// <summary>骨架读数。同上，随语言自动换、在模型就位与换剪辑时重算。</summary>
    public LinguaFormatObservable SkeletonReadoutText { get; }

    /// <summary>
    /// 把剪辑换到模型节点上（旧 ApplyClip 逐行照搬）：第 0 项清空采样器回绑定姿势；
    /// 其余按标签找剪辑，每次都新建 AnimationSampler（时间从 0 起、循环播放）。
    /// </summary>
    private void ApplyClip()
    {
        if (model == null)
            return;

        if (clipIndex == 0)
        {
            model.AnimationSampler = null;
            sampler = null;

            return;
        }

        if (!byLabel.TryGetValue(ClipLabel, out var animation))
            return;

        sampler = new AnimationSampler(animation) { TimeScale = 1f, LoopMode = LoopMode.Loop };

        model.AnimationSampler = sampler;
    }

    /// <summary>默认骨骼的挑法：先找头、再找脊柱、退而求第一根（旧 PickBone 照搬）。</summary>
    private string PickBone()
    {
        var names = model?.Skeleton?.Bones.Select(b => b.Name).ToList() ?? [];

        return names.FirstOrDefault(n => n.Contains("Head", StringComparison.OrdinalIgnoreCase))
               ?? names.FirstOrDefault(n => n.Contains("Spine", StringComparison.OrdinalIgnoreCase))
               ?? names.FirstOrDefault()
               ?? string.Empty;
    }

    /// <summary>
    /// 重算取样读数（旧 ReportSample 逐行照搬）：无剪辑或未选骨骼时整行回落到「没有可取样的剪辑」；
    /// 否则直接调 Animation.Sample 拿那根骨骼在该时刻的矩阵，按 7 占位模板排出。
    /// </summary>
    private void UpdateSampleText()
    {
        var animation = clips.Count > 0
            ? byLabel.GetValueOrDefault(ClipLabel) ?? clips[0]
            : null;

        // 消费端从选中项的 Value 取骨骼名，不再单独养一个字符串影子字段。
        var boneName = SelectedBone?.Value;

        if (animation == null || string.IsNullOrEmpty(boneName))
        {
            SampleText = Strings.Keys.FbxAnimation_NoClip.T();

            return;
        }

        var matrix = animation.Sample(boneName, (float)sampleTime);

        SampleText = Strings.Keys.FbxAnimation_SampleReadout.Format(
            animation.Name,
            sampleTime,
            boneName,
            matrix.Translation.X,
            matrix.Translation.Y,
            matrix.Translation.Z,
            animation.Channels.Count);
    }
}
