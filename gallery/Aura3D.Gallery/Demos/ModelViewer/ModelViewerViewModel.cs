using Aura3D.Core.Math;
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
using ModelNode = Aura3D.Core.Nodes.Model;

namespace Aura3D.Gallery.Demos;

/// <summary>
/// glTF 模型页的界面状态，按 Particles 页的样板 MVVM 化。分工：
/// <list type="bullet">
///   <item><description>文案：AXAML 直接写 <c>{Translate}</c> 标记扩展取 Strings.Keys 键值；换语言走外壳既有的整页重挂。</description></item>
///   <item><description>参数：模型/Clone 类型两个下拉与滑杆开关双向绑定到这里；包围盒的两个
///     属性写穿到当前模型节点，副本的创建、清账与共享情况统计也全在这里。</description></item>
///   <item><description>Readout：包围盒一行是纯模板读数，Lingua 1.1 的 format builder 组装，
///     逐帧 <see cref="Tick"/> 重读；装载记录、结构统计、共享情况三行都是「模板 + 回落句」的
///     多态行（有/没下载、有/没副本是两种句式）——format builder 换不了整句模板，所以按
///     非模板动态字符串走普通 string 属性，在各自的变化处重算（同 FbxAnimation 取样读数的口径）。
///     模型结构维持旧页的一行统计读数，原页没有 TreeView，不引入新的层级展示。</description></item>
///   <item><description>引擎模型：已装载的模型表、当前生效的那份与两份副本都住在这里；
///     视图只保留生命周期——进页即取档喂 <see cref="Preload"/>、建场景时调
///     <see cref="ActivateInitialModel"/>；页内按需档经 <see cref="ModelRequested"/> 请视图
///     下载解码（AssetBatch 在视图手里），完稿回灌 <see cref="SetModel"/> 再走同一条切换路径。</description></item>
/// </list>
/// 视图桥全是语义事件：模型取回、相机取景、副本入/出场景、调试线框的临时点亮与开关
/// （真值写在宿主设置上）——VM 全程不碰 DemoContext。
/// </summary>
public sealed partial class ModelViewerViewModel : ObservableObject
{
    // 三个进页即取（清单里合计约 3 MB），四个挂在页内按需取，选到才下载。
    // 后者不计入进页即取的下载量：视图的 AssetBatch 允许取本页集合之外的 Key（回落到全表）。
    // 表放在 VM 上公开：下拉选项、标签↔Key 对照和视图的资产循环共用这一份，不各抄一遍。
    public static readonly (string Key, LinguaKey Label, bool Preload)[] Library =
    [
        ("Stool", Strings.Keys.ModelViewer_OptionStool, true),
        ("CoffeeTable", Strings.Keys.ModelViewer_OptionCoffeeTable, true),
        ("LionHead", Strings.Keys.ModelViewer_OptionLionHead, true),
        ("Lightbulb", Strings.Keys.ModelViewer_OptionLightbulb, false),
        ("Stones", Strings.Keys.ModelViewer_OptionStones, false),
        ("Present", Strings.Keys.ModelViewer_OptionPresent, false),
        ("CelCharacter", Strings.Keys.ModelViewer_OptionCelCharacter, false),
    ];

    // 副本模式下拉的 (Lingua 键, CopyType) 对照表，键序沿用旧 CopyModeKeys（即枚举成员序）。
    // Value 直接携带引擎枚举，读数行的技术名由枚举 ToString() 给出，不再另备名字表。
    private static readonly (LinguaKey Key, CopyType Mode)[] CopyModes =
    [
        (Strings.Keys.ModelViewer_CopyOptionSharedResource, CopyType.SharedResource),
        (Strings.Keys.ModelViewer_CopyOptionSharedResourceData, CopyType.SharedResourceData),
        (Strings.Keys.ModelViewer_CopyOptionFullCopy, CopyType.FullCopy),
    ];

    private readonly Dictionary<string, ModelNode> loaded = [];
    private readonly List<ModelNode> clones = [];

    private ModelNode? current;
    private string selectedKey = Library[0].Key;

    // 一次按需装载未落地前挡掉新的选择（旧页 Choose 开头的 loading 早退原样搬来）。
    private bool isLoading;

    // 下拉选项统一走 ComboOption：强类型私表 + public IList 透出（XAML 绑定不变）。
    // Name 是 Lingua observable（模板 {Binding Name^} 流订阅，换语言自动换字），
    // Value 是语言无关的稳定身份——资产 Key / CopyType 枚举，选中比较回到选项实例，
    // 不再按翻译后的字符串 IndexOf 匹配。
    private List<Kit.ComboOption<string>> modelOptionList = [];
    private List<Kit.ComboOption<CopyType>> copyModeOptionList = [];
    private Kit.ComboOption<string>? selectedModel;
    private Kit.ComboOption<CopyType>? selectedCopyMode;

    private double fitPadding = 0.12;
    private double boxPadding;
    private bool customBox;
    private bool showBox = true;

    // 包围盒读数的逐帧缓存：Model.BoundingBox 的 getter 每次都遍历 Mesh 再合并，
    // 一帧只取一次，8 个占位共用（旧 ReportBox 也是先取一次再填）。
    private BoundingBox? box;

    /// <summary>
    /// 建 VM：装好两个下拉的选项与初选（对齐旧页构造函数里的两个 SelectedItem 赋值，
    /// 现在由绑定初始化替它完成），给三条普通读数立初值，再装配包围盒模板读数。
    /// </summary>
    public ModelViewerViewModel()
    {
        modelOptionList = Library.Select(i => Kit.ComboOption.ForKey(i.Label, i.Key)).ToList();
        copyModeOptionList = CopyModes.Select(m => Kit.ComboOption.ForKey(m.Key, m.Mode)).ToList();

        // 初选按 Value 对表：模型取选中键，副本模式取旧 copyModeIndex=0 对应的 SharedResource。
        selectedModel = modelOptionList.Single(o => o.Value == selectedKey);
        selectedCopyMode = copyModeOptionList.Single(o => o.Value == CopyType.SharedResource);

        // 装载记录初值是「还没取过」的提示句（旧 XAML 里 Text 直接绑这个键）；
        // 共享情况与结构统计旧页进页时不写（ReportClone/ReportStructure 只在交互后跑），
        // 这里立成行控件的缺省占位，展示口径与旧页一致。
        loadText = Strings.Keys.ModelViewer_LoadHint.T();
        cloneText = "—";
        structureText = "—";

        // 包围盒读数模板 {0}..{7}：六个坐标分量、外扩量、自定义盒有无（{7} 落到
        // 「无/有」两个短语键，同 Particles 贴图提示占位的做法）。资产到位前先显示一排
        // 零值，建好场景的当帧就被真值覆盖。
        BoxText = Strings.Keys.ModelViewer_BoxReadout.CreateFormat()
            .Arg(() => box?.Min.X ?? 0f)
            .Arg(() => box?.Min.Y ?? 0f)
            .Arg(() => box?.Min.Z ?? 0f)
            .Arg(() => box?.Max.X ?? 0f)
            .Arg(() => box?.Max.Y ?? 0f)
            .Arg(() => box?.Max.Z ?? 0f)
            .Arg(() => current?.BoundingBoxPadding ?? 0f)
            .Arg(() => current?.CustomBoundingBox == null
                ? Strings.Keys.ModelViewer_BoxNone.T()
                : Strings.Keys.ModelViewer_BoxSet.T())
            .Build();
    }

    /// <summary>资产阶段（进页即取档）由视图逐个喂入；命名照旧页在装载时就写好。</summary>
    public void Preload(string key, ModelNode model)
    {
        model.Name = key;

        loaded[key] = model;
    }

    /// <summary>
    /// 按需档开始下载前由视图调：先置 <c>isLoading</c> 挡掉装载中的新选择，再点亮进度文案。
    /// </summary>
    public void BeginLoad(string key, long kilobytes)
    {
        isLoading = true;

        LoadText = Strings.Keys.ModelViewer_LoadProgress.Format(key, kilobytes);
    }

    /// <summary>
    /// 按需档下载完成由视图回灌（节点入场景由视图先做完）：命名、入表、装载记录、解除
    /// loading，然后走与已装载档同一条切换路径。
    /// </summary>
    public void SetModel(string key, ModelNode model, long kilobytes, long milliseconds, string path)
    {
        model.Name = key;

        loaded[key] = model;

        LoadText = Strings.Keys.ModelViewer_LoadDone.Format(key, kilobytes, milliseconds, path);

        isLoading = false;

        ApplySelection(key, model);
    }

    /// <summary>
    /// 建场景时由视图调：把初选那份立为当前模型并拨好 Enable（旧 BuildScene 的对应段）。
    /// 只做这两件事——旧页进页时不清副本、不出结构与共享读数（那两行保持占位到第一次
    /// 换模型），这里照抄口径；取景由视图随后自己调一次。
    /// </summary>
    public void ActivateInitialModel()
    {
        if (!loaded.TryGetValue(selectedKey, out var model))
            return;

        current = model;

        foreach (var pair in loaded)
            pair.Value.Enable = pair.Key == selectedKey;
    }

    /// <summary>逐帧调用：重读包围盒读数（旧页 Update 里的 ReportBox）。</summary>
    public void Tick() => RefreshBox();

    /// <summary>下拉选到未装载的模型：请视图走它的资产通道下载解码，完稿回灌 <see cref="SetModel"/>。</summary>
    public event Action<string>? ModelRequested;

    /// <summary>相机取景请求（Fit 按钮与换模型/拖留白都会发）；视图把它接到 MainCamera.FitToBoundingBox。</summary>
    public event Action? FitRequested;

    /// <summary>副本建好后要入场景；视图把它接到 Scene.AddNode。</summary>
    public event Action<ModelNode>? CloneAdded;

    /// <summary>副本清账时要撤下场景；视图把它接到 Scene.RemoveNode。</summary>
    public event Action<ModelNode>? CloneRemoved;

    /// <summary>改外扩量/自定义盒时旧页会顺手点亮调试线框（让效果立刻可见）；宿主设置只有视图够得着。</summary>
    public event Action? ShowBoxForced;

    /// <summary>「调试框」开关变化；视图把它写到宿主的 Settings.Debug.ShowBoundingBox。</summary>
    public event Action<bool>? ShowBoxToggled;

    /// <summary>包围盒读数。语言切换时模板换新，绑定自动收到重排后的整句。</summary>
    public LinguaFormatObservable BoxText { get; }

    /// <summary>装载记录（提示/进度/完成三态句），按需下载的两个节点处重算。</summary>
    public string LoadText
    {
        get => loadText;
        private set => SetProperty(ref loadText, value);
    }

    /// <summary>共享情况读数（模板 + 「没有副本」回落句），建/清副本处重算。</summary>
    public string CloneText
    {
        get => cloneText;
        private set => SetProperty(ref cloneText, value);
    }

    /// <summary>结构统计读数（单模板，但只在换模型时变），切换路径末尾重算。</summary>
    public string StructureText
    {
        get => structureText;
        private set => SetProperty(ref structureText, value);
    }

    /// <summary>当前模型下拉的选项：Name 跟随语言（流绑定自动换字），Value 是语言无关的资产 Key。</summary>
    public IList ModelOptions => modelOptionList;

    /// <summary>CopyType 下拉的选项：Name 跟随语言（流绑定自动换字），Value 是引擎的 CopyType 枚举。</summary>
    public IList CopyModeOptions => copyModeOptionList;

    /// <summary>已装载的模型表（进页即取与按需两档都在内）；建场景时视图按它登记节点。</summary>
    public IReadOnlyDictionary<string, ModelNode> Loaded => loaded;

    /// <summary>当前生效的模型键；建场景时视图按它决定哪个节点 Enable。</summary>
    public string SelectedKey => selectedKey;

    /// <summary>当前模型节点；视图的相机取景按它的包围盒收紧。</summary>
    public ModelNode? CurrentModel => current;

    /// <summary>相机按当前盒取景（Fit 与「按当前盒重新取景」两个按钮共用）；源生成 <c>FitCommand</c>。</summary>
    [RelayCommand]
    private void Fit() => FitRequested?.Invoke();

    /// <summary>把两份副本撤下场景并清账；源生成 <c>ClearClonesCommand</c>。</summary>
    [RelayCommand]
    private void ClearClones()
    {
        foreach (var clone in clones)
            CloneRemoved?.Invoke(clone);

        clones.Clear();

        ReportClone();
    }

    /// <summary>
    /// 当前模型下拉的选中项。选到已装载的那档就地切换（只拨 Enable，不下载）；
    /// 选到按需那档发 <see cref="ModelRequested"/> 请视图取字节。装载进行中的新选择被
    /// <c>isLoading</c> 挡掉——旧页 Choose 的早退口径：下拉显示先行一步，落地后下次选择才追上。
    /// </summary>
    public Kit.ComboOption<string>? SelectedModel
    {
        get => selectedModel;
        set
        {
            // 选项实例即身份：绑定初始化把当前项再推一次时是同一实例，SetProperty 的相等
            // 判断挡掉，别让它凭空触发一次切换。
            if (value == null || !SetProperty(ref selectedModel, value))
                return;

            if (isLoading)
                return;

            SelectModel(value.Value);
        }
    }

    /// <summary>CopyType 下拉的选中项；换类型要整批重建副本——先清旧再按新模式建新。</summary>
    public Kit.ComboOption<CopyType>? SelectedCopyMode
    {
        get => selectedCopyMode;
        set
        {
            // 同上：初值推入是同一实例，SetProperty 挡掉，别让它凭空建出副本。
            if (value == null || !SetProperty(ref selectedCopyMode, value))
                return;

            ClearClones();
            AddClones();
        }
    }

    /// <summary>取景留白比例；拖动即时重新取景。</summary>
    public double FitPadding
    {
        get => fitPadding;
        set
        {
            if (SetProperty(ref fitPadding, value))
                FitRequested?.Invoke();
        }
    }

    /// <summary>模型级包围盒外扩量；写穿到当前模型并顺手点亮调试线框（旧页行为，便于立刻看到盒变了）。</summary>
    public double BoxPadding
    {
        get => boxPadding;
        set
        {
            if (!SetProperty(ref boxPadding, value))
                return;

            if (current != null)
                current.BoundingBoxPadding = (float)value;

            ShowBoxForced?.Invoke();

            // 旧处理器在改完就重排一次读数，不等下一帧；这里保持同样的即时性。
            RefreshBox();
        }
    }

    /// <summary>自定义盒开关；开=写一个比模型大的固定框，关=清掉回落到几何体自己的盒。</summary>
    public bool CustomBox
    {
        get => customBox;
        set
        {
            if (!SetProperty(ref customBox, value))
                return;

            if (current != null)
                current.CustomBoundingBox = value
                    ? new BoundingBox(new Vector3(-2f, -0.2f, -2f), new Vector3(2f, 3.4f, 2f))
                    : null;

            ShowBoxForced?.Invoke();

            RefreshBox();
        }
    }

    /// <summary>调试线框开关；真值在宿主设置上，经事件写过去。初值真——旧 XAML 画的就是勾选态。</summary>
    public bool ShowBox
    {
        get => showBox;
        set
        {
            if (SetProperty(ref showBox, value))
                ShowBoxToggled?.Invoke(value);
        }
    }

    private void SelectModel(string key)
    {
        if (loaded.TryGetValue(key, out var model))
        {
            // 已在场景里的那档（进页即取）：只切 Enable，不产生下载。
            ApplySelection(key, model);

            return;
        }

        // 未取过的那档（页内按需）：下载解码属于视图的资产通道，VM 只发语义请求。
        ModelRequested?.Invoke(key);
    }

    // 旧 Choose 收尾段的等价搬迁：换当前、全表拨 Enable、清副本、取景、出结构统计。
    private void ApplySelection(string key, ModelNode model)
    {
        selectedKey = key;
        current = model;

        foreach (var pair in loaded)
            pair.Value.Enable = pair.Key == key;

        ClearClones();

        FitRequested?.Invoke();

        ReportStructure();
    }

    private void AddClones()
    {
        if (current == null)
            return;

        for (int i = 0; i < 2; i++)
        {
            var clone = current.Clone(ModeOf());

            clone.Name = $"{current.Name}-clone{i}";
            clone.Enable = true;
            clone.Position = new Vector3(2.6f + i * 2.6f, 0, 0);

            // 第二份换掉自己的 BaseColor：材质若与原模型同源，原模型会跟着换图，
            // 这是判断共享边界最直白的办法——BaseColor 是 LightPass/PBR 都真读的通道。
            if (i == 1)
            {
                foreach (var mesh in clone.Meshes)
                {
                    mesh.Material?.SetTexture("BaseColor", Procedural.Checker(128, 6,
                        new Procedural.Rgb(240, 90, 40), new Procedural.Rgb(40, 200, 235)));
                }
            }

            clones.Add(clone);

            CloneAdded?.Invoke(clone);
        }

        ReportClone();
    }

    // 选中项的 Value 即引擎枚举，旧 copyModeIndex→CopyType 的换算随之退役；
    // 建副本时必已选中，null 回落只是给编译器一个确定性出口。
    private CopyType ModeOf() => selectedCopyMode?.Value ?? CopyType.SharedResource;

    private string LabelOf(string key) => Library.First(i => i.Key == key).Label.T();

    // 旧 ReportStructure 逐行照搬：子节点/Mesh/顶点合计/通道清单/首材质的 alpha 三件套。
    private void ReportStructure()
    {
        if (current == null)
            return;

        var meshes = current.Meshes;

        var channels = meshes
            .Where(m => m.Material != null)
            .SelectMany(m => m.Material!.Channels)
            .Select(c => $"{c.Name}{(c.Texture == null ? "∅" : $"({c.Texture.Width}×{c.Texture.Height})")}")
            .Distinct()
            .ToList();

        var first = meshes.FirstOrDefault(m => m.Material != null)?.Material;

        StructureText = Strings.Keys.ModelViewer_StructureReadout.Format(
            LabelOf(selectedKey),
            current.Children.Count,
            meshes.Count,
            meshes.Sum(m => m.Geometry?.VertexCount ?? 0),
            string.Join(" ", channels),
            first?.BlendMode,
            first?.DoubleSided,
            first?.AlphaCutoff);
    }

    // 旧 ReportClone 逐行照搬：没副本时整句回落；有副本时用引用相等探几何/材质是否同源，
    // 再看原模型的 BaseColor 有没有跟着副本换图。
    private void ReportClone()
    {
        if (clones.Count == 0 || current == null)
        {
            CloneText = Strings.Keys.ModelViewer_NoClones.T();

            return;
        }

        var sharedGeometry = clones
            .SelectMany(c => c.Meshes)
            .Zip(current.Meshes, (a, b) => ReferenceEquals(a.Geometry, b.Geometry))
            .All(x => x);

        var sharedMaterial = clones
            .SelectMany(c => c.Meshes)
            .Zip(current.Meshes, (a, b) => ReferenceEquals(a.Material, b.Material))
            .All(x => x);

        var originalTint = current.Meshes
            .FirstOrDefault(m => m.Material != null)?
            .Material!.GetTexture("BaseColor");

        var followed = originalTint != null && clones
            .SelectMany(c => c.Meshes)
            .Any(m => m.Material != null && ReferenceEquals(m.Material.GetTexture("BaseColor"), originalTint));

        CloneText = Strings.Keys.ModelViewer_CloneReadout.Format(
            ModeOf(),
            sharedGeometry ? Strings.Keys.ModelViewer_SameInstance.T() : Strings.Keys.ModelViewer_OwnCopies.T(),
            sharedMaterial ? Strings.Keys.ModelViewer_SameInstance.T() : Strings.Keys.ModelViewer_OwnCopies.T(),
            followed
                ? Strings.Keys.ModelViewer_TintFollowed.T()
                : Strings.Keys.ModelViewer_TintIndependent.T());
    }

    private void RefreshBox()
    {
        box = current?.BoundingBox;

        BoxText.Refresh();
    }

    private string loadText;
    private string cloneText;
    private string structureText;
}
