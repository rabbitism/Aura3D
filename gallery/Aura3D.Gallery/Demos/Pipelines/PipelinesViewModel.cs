using Aura3D.Core.Nodes;
using Aura3D.Core.Renderers;
using Aura3D.Gallery.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using Irihi.Lingua;
using System;
using System.Linq;
using System.Numerics;
using Color = System.Drawing.Color;

namespace Aura3D.Gallery.Demos;

/// <summary>
/// 管线对照页的界面状态。分工：
/// <list type="bullet">
///   <item><description>文案：每个标签一个 Lingua observable，XAML 用 <c>{Binding Xxx^}</c> 订阅，
///     换语言时由绑定自动收到新值，不依赖外壳整页重挂。</description></item>
///   <item><description>参数：滑杆/开关双向绑定到这里的属性，setter 写穿到两个灯对象；
///     初值即灯上真正生效的值，旧 XAML 里写死的 <c>Value="-46"</c> / <c>IsChecked="True"</c>
///     由绑定初始化取代。</description></item>
///   <item><description>Readout：通道行是 8 占位符的 Lingua format（模板随语言换新），
///     pass 行是普通字符串属性；两者的原料——要求的管线种类、当前管线对象、背景是否
///     立方图——都在宿主手里，VM 不碰 DemoContext，由视图逐帧经 <see cref="Report"/> 喂进来。</description></item>
///   <item><description>引擎模型：两个灯住在这里，视图建场景时直接登记；地面与五个
///     各喂一种信息的对象是纯布景，仍留在视图的 BuildScene 里组装。</description></item>
/// </list>
/// 视图桥只有一个事件：<see cref="FrameRequested"/>（灯的参数变了要排一次重绘）。
/// 换管线不走这里：宿主状态栏的管线框调 <c>DemoContext.Reload</c>，整页连同本 VM 一起重建。
/// </summary>
public sealed partial class PipelinesViewModel : ObservableObject
{
    /// <summary>
    /// 建好 VM：对齐参数初值，再把通道读数的模板与计算参数装配起来。本页没有按钮，
    /// 无命令需要接线。
    /// </summary>
    public PipelinesViewModel()
    {
        // 参数初值取两个灯上真正生效的那一份（等价于旧 XAML 写死的行控件初值）。
        sunPitch = Sun.RotationDegrees.X;
        sunCastShadow = Sun.CastShadow;
        pointVisible = Point.Enable;

        // 通道读数模板 {0}..{7}：整行都是「当前管线是哪条」这一个事实的投影，逐帧 Report 后
        // Refresh 重读；构造期先按本页默认管线（BlinnPhong）算一遍，首帧 Report 之前绑定也有值可显。
        ChannelsText = Strings.Keys.Pipelines_ChannelsReadout.CreateFormat()
            .Arg(() => currentKind.DisplayName())
            .Arg(LitMark)
            .Arg(PbrMark)
            .Arg(PbrMark)
            .Arg(PbrMark)
            .Arg(LitMark)
            .Arg(CelMark)
            .Arg(EnvMark)
            .Build();
    }

    /// <summary>灯参数变化后请求一帧重绘；视图把它接到宿主的 InvalidateRender 上。</summary>
    public event Action? FrameRequested;

    /// <summary>页首说明。</summary>
    public IObservable<string?> IntroLabel => Strings.Instance.Pipelines_Intro;

    /// <summary>「灯」分组标题。</summary>
    public IObservable<string?> LightsSectionLabel => Strings.Instance.Pipelines_Lights;

    /// <summary>平行光俯角标签。</summary>
    public IObservable<string?> LightPitchLabel => Strings.Instance.Pipelines_LightPitch;

    /// <summary>平行光投影开关标签。</summary>
    public IObservable<string?> LightShadowLabel => Strings.Instance.Pipelines_LightShadow;

    /// <summary>点光可见开关标签。</summary>
    public IObservable<string?> PointLightVisibleLabel => Strings.Instance.Pipelines_PointLightVisible;

    /// <summary>「对照读数」分组标题。</summary>
    public IObservable<string?> ReadoutSectionLabel => Strings.Instance.Pipelines_Readout;

    /// <summary>通道读数行标题。</summary>
    public IObservable<string?> ChannelsLabel => Strings.Instance.Pipelines_Channels;

    /// <summary>pass 组成行标题。</summary>
    public IObservable<string?> PassesLabel => Strings.Instance.Pipelines_Passes;

    /// <summary>
    /// 平行光：俯角滑杆与投影开关双向绑定写穿到这里。初值即旧页面行控件的初值（俯角 -46°、
    /// 投影开）；引擎默认 CastShadow=false，不显式给 true 的话开关显示已开而地面无影，
    /// 要等用户拨一次开关才对齐——旧页面就有这个首帧不一致，这里一并修掉。
    /// </summary>
    public DirectionalLight Sun { get; } = new()
    {
        LightColor = Color.White,
        CastShadow = true,
        RotationDegrees = new Vector3(-46f, -28f, 0),
    };

    /// <summary>点光：可见开关写穿到 Enable；初值与旧页面一致（引擎默认可见）。</summary>
    public PointLight Point { get; } = new()
    {
        LightColor = Color.Orange,
        LuminousIntensity = 5000,
        AttenuationRadius = 10f,
        Position = new Vector3(3.6f, 2.4f, 3.2f),
    };

    /// <summary>通道读数行。语言切换时模板换新，绑定自动收到重排后的整句。</summary>
    public LinguaFormatObservable ChannelsText { get; }

    /// <summary>pass 组成行。非模板的动态字符串，逐帧整串换新，SetProperty 自带去重。</summary>
    public string PassesText
    {
        get => passesText;
        private set => SetProperty(ref passesText, value);
    }

    private double sunPitch;
    private bool sunCastShadow;
    private bool pointVisible;
    private bool hasEnvironment;

    // 构造期到首帧 Report 之间的占位：本页默认管线，首帧即被真实值覆盖。
    private PipelineKind currentKind = PipelineKind.BlinnPhong;

    // pass 行的初值与 ReadoutRow 的控件默认一致，首帧 Report 之前显示同旧页面。
    private string passesText = "—";

    /// <summary>平行光俯角（度）；方位角 -28° 固定，与旧事件处理一致。</summary>
    public double SunPitch
    {
        get => sunPitch;
        set
        {
            if (!SetProperty(ref sunPitch, value))
                return;

            Sun.RotationDegrees = new Vector3((float)value, -28f, 0);
            FrameRequested?.Invoke();
        }
    }

    /// <summary>平行光是否投影。</summary>
    public bool SunCastShadow
    {
        get => sunCastShadow;
        set
        {
            if (!SetProperty(ref sunCastShadow, value))
                return;

            Sun.CastShadow = value;
            FrameRequested?.Invoke();
        }
    }

    /// <summary>点光是否可见（写穿到节点 Enable）。</summary>
    public bool PointVisible
    {
        get => pointVisible;
        set
        {
            if (!SetProperty(ref pointVisible, value))
                return;

            Point.Enable = value;
            FrameRequested?.Invoke();
        }
    }

    /// <summary>
    /// 逐帧由视图调用（本页版的 <c>Tick</c>）：把读数需要的三样事实喂进来——要求的管线
    /// 种类、当前管线对象、背景是否立方图。它们都得从宿主/场景上读，VM 不碰 DemoContext。
    /// 管线对象还没建出来（首帧之前）时维持上一条读数，与旧 Report 的早退一致。
    /// </summary>
    /// <param name="kind">宿主当前要求的管线。</param>
    /// <param name="pipeline">场景里实际生效的管线对象。</param>
    /// <param name="hasEnvironment">背景是否立方图（只有那一路才喂 IBL 的两个 pass）。</param>
    public void Report(PipelineKind kind, RenderPipeline? pipeline, bool hasEnvironment)
    {
        currentKind = kind;
        this.hasEnvironment = hasEnvironment;

        if (pipeline == null)
            return;

        var passes = pipeline.OnceRenderPasses.Select(p => p.GetType().Name).ToList();

        passes.Add("→");

        passes.AddRange(pipeline.EveryCameraRenderPasses.Select(p => p.GetType().Name));

        PassesText = string.Join(" ", passes);

        // 通道行是模板：重读一遍计算参数（管线名、各通道的对勾叉子、环境图状态）。
        ChannelsText.Refresh();
    }

    private bool UsesPbr => currentKind is PipelineKind.PBRDeferred or PipelineKind.PBRForward;

    // 通道行里的对勾/叉子只由管线种类决定，换管线（整页重建，VM 换新实例）后自然跟着变。
    private string LitMark => currentKind == PipelineKind.NoLight ? "×" : "✓";
    private string PbrMark => UsesPbr ? "✓" : "×";
    private string CelMark => currentKind == PipelineKind.CelShading ? "✓" : "×";

    /// <summary>IBL 环境图状态：只有 PBR 管线读它，其余管线显示「—」。</summary>
    private string EnvMark => UsesPbr
        ? hasEnvironment ? Strings.Keys.Pipelines_EnvSet.T() : Strings.Keys.Pipelines_EnvUnsetDefault.T()
        : "—";
}
