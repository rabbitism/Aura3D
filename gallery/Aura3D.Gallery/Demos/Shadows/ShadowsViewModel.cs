using Aura3D.Core.Nodes;
using Aura3D.Core.Renderers;
using Aura3D.Core.Scenes;
using Aura3D.Gallery.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Irihi.Lingua;
using System;
using System.Numerics;
using Color = System.Drawing.Color;

namespace Aura3D.Gallery.Demos;

/// <summary>
/// 阴影页的界面状态，按 Particles/PostProcessing 页的样板 MVVM 化。分工：
/// <list type="bullet">
///   <item><description>文案：每个标签一个 Lingua observable，XAML 用 <c>{Binding Xxx^}</c> 订阅，
///     换语言时由绑定自动收到新值。</description></item>
///   <item><description>参数：滑杆/开关双向绑定到这里的属性。写灯（旋转/投影开关/ShadowConfig）与
///     相机裁剪面的直接写穿到 VM 持有的对象；写宿主 <see cref="PipelineSettings"/> 的（CSM 三样、
///     平行光上限、两个调试开关）走「backing + 值事件」，由视图把新值逐项转写进 Settings。</description></item>
///   <item><description>Readout：Lingua 1.1 的 format builder 组装 {0}..{8}——管线名与 SupportsCSM
///     是构造期快照，两个设置值读 backing（与宿主 Settings 始终同步），其余全是逐帧读的计算参数
///     （挂在主光上的 CsmShadowData 与单图 RenderTarget），视图逐帧调 <see cref="Tick"/> 重读。</description></item>
///   <item><description>引擎模型：两盏方向光住在这里，建场景时由视图挂进场景；场景本体经
///     <see cref="AttachScene"/> 登记——显式主光开关与相机 FarPlane 的写穿目标是场景成员。</description></item>
/// </list>
/// 视图桥只有事件：若干 Settings 转写事件、<see cref="FrameRequested"/>（参数变化要排一次重绘）与
/// <see cref="ReloadRequested"/>（级联数跨过 1↔2 的 ENABLE_CSM 着色器界线要整页重建）。
/// </summary>
public sealed partial class ShadowsViewModel : ObservableObject
{
    private readonly PipelineKind pipelineKind;

    // 建场景前为 null：显式主光与相机 FarPlane 的写穿按旧处理器的口径丢弃（旧代码判 Context.Scene != null）。
    private Scene? scene;

    /// <summary>
    /// 建好 VM：参数初值对齐真正生效的那一份——ShadowConfig 四项取引擎默认（与旧页面的字段初值同源），
    /// CSM 三样与平行光上限取宿主 Settings（等价于旧页面构造函数里那批「行控件写回」，现在由绑定
    /// 初始化替它完成）；同时记住当前管线，Readout 的管线名与 SupportsCSM 按它判。管线在页面生命
    /// 周期内不会变——换管线必经宿主整页重建。
    /// </summary>
    /// <param name="settings">宿主当前生效的管线设置，仅在构造期读初值，不保存引用。</param>
    /// <param name="pipeline">宿主当前要求的管线。</param>
    public ShadowsViewModel(PipelineSettings settings, PipelineKind pipeline)
    {
        pipelineKind = pipeline;

        shadowWidth = Main.ShadowConfig.Width;
        shadowHeight = Main.ShadowConfig.Height;
        shadowNear = Main.ShadowConfig.NearPlane;
        shadowFar = Main.ShadowConfig.FarPlane;

        csmCascadeCount = settings.CsmCascadeCount;
        csmSplitLambda = settings.CsmSplitLambda;
        csmShadowMapResolution = settings.CsmShadowMapResolution;
        directionalLightLimit = settings.DirectionalLightLimit;

        castShadow = Main.CastShadow;

        // 旧页面 XAML 里把两个主光开关画成勾选态；显式主光尤其微妙——勾选态只是「意图」，
        // 场景的 MainDirectionalLight 要等用户真的拨过开关才被写（构造期没人写），所以刚进页面
        // 时读数显示的还是「自动挑选」，与旧 Report() 读场景真值的行为一致。
        explicitKey = true;
        showDirectionalLight = true;
        showBoundingBox = true;

        // 旧代码在建场景末尾补一次 ApplyShadowConfig/主光旋转；现在灯住在这里，构造期就对齐，
        // 滑杆每动一次再同步一次，建场景时自然带着最新值。此刻视图还没订阅 FrameRequested，事件是空操作。
        ApplyMainRotation();
        ApplyShadowConfig();

        // Readout 模板 {0}..{8}：占位语义与旧 Report() 里的 string.Format 逐个对齐——{2} 读场景
        // 当前的显式主光；{3}/{4}/{8} 逐帧读挂在主光上的 CsmShadowData（管线还没跑时是 null，
        // 回落 0）；{5}/{6} 读 backing 并按视图转写的同一取整口径算，读数始终等于 Settings 里的真值；
        // {7} 读单图路径的 RenderTarget。
        ReadoutText = Strings.Keys.Shadows_Readout.CreateFormat()
            .Arg(() => pipelineKind.DisplayName())
            .Arg(() => SupportsCsm)
            .Arg(() => scene?.MainDirectionalLight?.Name ?? Strings.Keys.Shadows_AutoPicked.T())
            .Arg(() => Csm?.CascadeCount ?? 0)
            .Arg(() => Csm?.Resolution ?? 0)
            .Arg(() => (int)Math.Round(csmCascadeCount))
            .Arg(() => (int)Math.Round(csmShadowMapResolution / 256) * 256)
            .Arg(() => SingleMap == null
                ? Strings.Keys.Shadows_SingleNone.T()
                : $"{SingleMap.Width}×{SingleMap.Height}")
            .Arg(() => (Csm?.CascadeSplitDepths.Length ?? 0) - 1)
            .Build();
    }

    /// <summary>
    /// 场景建好后由视图登记：显式主光开关与相机 FarPlane 的写穿目标是场景成员
    /// （MainDirectionalLight / MainCamera），登记前的写与读按旧处理器的口径丢弃。
    /// </summary>
    public void AttachScene(Scene attachedScene) => scene = attachedScene;

    /// <summary>逐帧调用：重读 Readout 的计算参数（GPU 状态与设置真值）。</summary>
    public void Tick() => ReadoutText.Refresh();

    /// <summary>参数（灯旋转、投影开关、ShadowConfig、CSM 设置、调试开关）变化后请求一帧重绘；视图把它接到宿主的 InvalidateRender 上。</summary>
    public event Action? FrameRequested;

    /// <summary>「Reload」按钮触发；视图把它接到宿主的整页重建上，VM 不碰 DemoContext。</summary>
    public event Action? ReloadRequested;

    /// <summary>逐帧读数。语言切换时模板换新，绑定自动收到重排后的整句。</summary>
    public LinguaFormatObservable ReadoutText { get; }

    /// <summary>
    /// 请求整页重建；源生成 <c>ReloadCommand</c> 供按钮绑定。级联数跨过 1↔2 界线要换
    /// ENABLE_CSM 着色器变体，只有重建才认（见 CsmLiveNote 文案）；重建后页面连同本 VM 一起换新。
    /// </summary>
    [RelayCommand]
    private void Reload() => ReloadRequested?.Invoke();

    /// <summary>主平行光：级联（CSM）路径的模型对象，俯角/方位/投影开关/显式主光的写穿目标，建场景时由视图挂进场景。</summary>
    public DirectionalLight Main { get; } = new()
    {
        Name = "Main",
        LightColor = Color.White,
        CastShadow = true,
        Irradiance = 60000,
    };

    /// <summary>第二盏也投影，但它不是主光，所以拿的是单张正交图那条路径。</summary>
    public DirectionalLight Second { get; } = new()
    {
        Name = "Second",
        LightColor = Color.Orange,
        CastShadow = true,
        Irradiance = 20000,
        RotationDegrees = new Vector3(-50f, 120f, 0),
    };

    private double pitch = -34;
    private double yaw = -22;
    private double shadowWidth;
    private double shadowHeight;
    private double shadowNear;
    private double shadowFar;
    private double csmCascadeCount;
    private double csmSplitLambda;
    private double csmShadowMapResolution;
    private double directionalLightLimit;

    // 相机 FarPlane 的初值是旧页面 XAML 里写死的 120（不来自 Settings），建场景时由视图带进裁剪面。
    private double cameraFar = 120;

    private bool castShadow;
    private bool explicitKey;
    private bool showDirectionalLight;
    private bool showBoundingBox;

    // SupportsCSM 在管线实例上是只读属性，但取值只由管线种类决定（BlinnPhong 与两条 PBR 为真，
    // NoLight/CelShading 继承基类的假）；实例又由 RequestedPipeline 决定且页面生命周期内不变，
    // 所以按构造期快照判，VM 不必够到 Scene.RenderPipeline。
    private bool SupportsCsm => pipelineKind is PipelineKind.BlinnPhong or PipelineKind.PBRDeferred or PipelineKind.PBRForward;

    // 旧 Report() 逐帧从主光上取的两份 GPU 状态：级联数据与单图 RenderTarget，由 ShadowMapPass 挂上去。
    private CsmShadowData? Csm => Main.GetPipelineGpuState<CsmShadowData>(nameof(CsmShadowData));

    private RenderTarget? SingleMap => Main.GetPipelineGpuState<RenderTarget>("ShadowMapRenderTarget");

    /// <summary>页首说明。</summary>
    public IObservable<string?> IntroLabel => Strings.Instance.Shadows_Intro;

    /// <summary>「主光」分组标题。</summary>
    public IObservable<string?> KeyLightSectionLabel => Strings.Instance.Shadows_KeyLight;

    /// <summary>俯角滑杆标签。</summary>
    public IObservable<string?> PitchLabel => Strings.Instance.Shadows_Pitch;

    /// <summary>方位滑杆标签。</summary>
    public IObservable<string?> YawLabel => Strings.Instance.Shadows_Yaw;

    /// <summary>主光投影开关标签。</summary>
    public IObservable<string?> CastShadowLabel => Strings.Instance.Shadows_KeyCastShadow;

    /// <summary>显式主光开关标签。</summary>
    public IObservable<string?> ExplicitKeyLabel => Strings.Instance.Shadows_ExplicitKey;

    /// <summary>主光分组说明。</summary>
    public IObservable<string?> KeyNoteLabel => Strings.Instance.Shadows_KeyNote;

    /// <summary>「单张正交图」分组标题。</summary>
    public IObservable<string?> SingleMapSectionLabel => Strings.Instance.Shadows_SingleMap;

    /// <summary>正交框宽滑杆标签。</summary>
    public IObservable<string?> ConfigWidthLabel => Strings.Instance.Shadows_ConfigWidth;

    /// <summary>正交框高滑杆标签。</summary>
    public IObservable<string?> ConfigHeightLabel => Strings.Instance.Shadows_ConfigHeight;

    /// <summary>近裁剪面滑杆标签；原文是硬编码的设置项名，迁移时补的键，两语言同文。</summary>
    public IObservable<string?> NearPlaneLabel => Strings.Instance.Shadows_NearPlane;

    /// <summary>远裁剪面滑杆标签；同上，补键、两语言同文（相机 FarPlane 是另一个键）。</summary>
    public IObservable<string?> ConfigFarPlaneLabel => Strings.Instance.Shadows_ConfigFarPlane;

    /// <summary>单图分组说明。</summary>
    public IObservable<string?> ConfigNoteLabel => Strings.Instance.Shadows_ConfigNote;

    /// <summary>「级联」分组标题。</summary>
    public IObservable<string?> CascadesSectionLabel => Strings.Instance.Shadows_Cascades;

    /// <summary>级联数滑杆标签；补键、两语言同文。</summary>
    public IObservable<string?> CascadeCountLabel => Strings.Instance.Shadows_CsmCascadeCount;

    /// <summary>分割系数滑杆标签。</summary>
    public IObservable<string?> SplitLambdaLabel => Strings.Instance.Shadows_SplitLambda;

    /// <summary>阴影图分辨率滑杆标签；补键、两语言同文。</summary>
    public IObservable<string?> ShadowMapResolutionLabel => Strings.Instance.Shadows_CsmShadowMapResolution;

    /// <summary>相机远裁剪面滑杆标签。</summary>
    public IObservable<string?> FarPlaneLabel => Strings.Instance.Shadows_FarPlane;

    /// <summary>级联逐帧生效说明。</summary>
    public IObservable<string?> CsmLiveNoteLabel => Strings.Instance.Shadows_CsmLiveNote;

    /// <summary>wasm 宿主说明。</summary>
    public IObservable<string?> WasmNoteLabel => Strings.Instance.Shadows_WasmNote;

    /// <summary>「Reload」按钮文案；原文是硬编码英文，补键、两语言同文。</summary>
    public IObservable<string?> ReloadLabel => Strings.Instance.Shadows_Reload;

    /// <summary>「平行光上限」分组标题。</summary>
    public IObservable<string?> LightLimitSectionLabel => Strings.Instance.Shadows_LightLimit;

    /// <summary>平行光上限滑杆标签；补键、两语言同文。</summary>
    public IObservable<string?> DirectionalLightLimitLabel => Strings.Instance.Shadows_DirectionalLightLimit;

    /// <summary>上限分组说明。</summary>
    public IObservable<string?> LimitNoteLabel => Strings.Instance.Shadows_LimitNote;

    /// <summary>「调试可视化」分组标题。</summary>
    public IObservable<string?> DebugSectionLabel => Strings.Instance.Shadows_Debug;

    /// <summary>画平行光体积开关标签。</summary>
    public IObservable<string?> DirectionalVolumeLabel => Strings.Instance.Shadows_DirectionalVolume;

    /// <summary>画包围盒开关标签。</summary>
    public IObservable<string?> BoundingBoxLabel => Strings.Instance.Shadows_BoundingBox;

    /// <summary>读数行标题。</summary>
    public IObservable<string?> ResourcesLabel => Strings.Instance.Shadows_Resources;

    /// <summary>主光俯角（度）；写穿到主光旋转，重绘请求经 FrameRequested 桥到视图。</summary>
    public double Pitch
    {
        get => pitch;
        set
        {
            if (!SetProperty(ref pitch, value))
                return;

            ApplyMainRotation();
            FrameRequested?.Invoke();
        }
    }

    /// <summary>主光方位角（度）；写穿路径同上。</summary>
    public double Yaw
    {
        get => yaw;
        set
        {
            if (!SetProperty(ref yaw, value))
                return;

            ApplyMainRotation();
            FrameRequested?.Invoke();
        }
    }

    /// <summary>主光投影开关；写穿目标就是 VM 持有的 <see cref="Main"/>，建场景前拨动也不会丢写。</summary>
    public bool CastShadow
    {
        get => castShadow;
        set
        {
            if (!SetProperty(ref castShadow, value))
                return;

            Main.CastShadow = value;
            FrameRequested?.Invoke();
        }
    }

    /// <summary>
    /// 显式把主光设为场景的 MainDirectionalLight。写穿目标是场景成员，场景未建时丢弃这一次写
    /// （与旧处理器判 Context.Scene != null 的口径一致）；读数里的主光名随逐帧 Tick 读场景真值。
    /// </summary>
    public bool ExplicitKey
    {
        get => explicitKey;
        set
        {
            if (!SetProperty(ref explicitKey, value))
                return;

            if (scene != null)
                scene.MainDirectionalLight = value ? Main : null;

            FrameRequested?.Invoke();
        }
    }

    /// <summary>正交框宽（ShadowConfig.Width，按整数生效）；两盏灯一起改，主光落到单图路径时效果不丢。</summary>
    public double ShadowWidth
    {
        get => shadowWidth;
        set
        {
            if (!SetProperty(ref shadowWidth, value))
                return;

            ApplyShadowConfig();
        }
    }

    /// <summary>正交框高（ShadowConfig.Height，按整数生效）；写穿路径同上。</summary>
    public double ShadowHeight
    {
        get => shadowHeight;
        set
        {
            if (!SetProperty(ref shadowHeight, value))
                return;

            ApplyShadowConfig();
        }
    }

    /// <summary>正交近裁剪面（ShadowConfig.NearPlane）。</summary>
    public double ShadowNear
    {
        get => shadowNear;
        set
        {
            if (!SetProperty(ref shadowNear, value))
                return;

            ApplyShadowConfig();
        }
    }

    /// <summary>正交远裁剪面（ShadowConfig.FarPlane）。</summary>
    public double ShadowFar
    {
        get => shadowFar;
        set
        {
            if (!SetProperty(ref shadowFar, value))
                return;

            ApplyShadowConfig();
        }
    }

    /// <summary>
    /// CSM 级联数；写宿主 Settings——ShadowMapPass 每帧比对级联数与分辨率、对不上就当场重建
    /// 纹理数组，所以写完要排一帧重绘。跨过 1↔2 界线例外（ENABLE_CSM 是着色器 define），要按 Reload。
    /// </summary>
    public double CsmCascadeCount
    {
        get => csmCascadeCount;
        set
        {
            if (!SetProperty(ref csmCascadeCount, value))
                return;

            CsmCascadeCountChanged?.Invoke(value);
            FrameRequested?.Invoke();
        }
    }

    /// <summary>CSM 对数/均匀分割系数；逐帧生效，写穿路径同上。</summary>
    public double CsmSplitLambda
    {
        get => csmSplitLambda;
        set
        {
            if (!SetProperty(ref csmSplitLambda, value))
                return;

            CsmSplitLambdaChanged?.Invoke(value);
            FrameRequested?.Invoke();
        }
    }

    /// <summary>CSM 阴影图分辨率；按 256 的倍数落到 Settings（取整口径在视图侧），逐帧生效。</summary>
    public double CsmShadowMapResolution
    {
        get => csmShadowMapResolution;
        set
        {
            if (!SetProperty(ref csmShadowMapResolution, value))
                return;

            CsmShadowMapResolutionChanged?.Invoke(value);
            FrameRequested?.Invoke();
        }
    }

    /// <summary>
    /// 平行光上限；ShadowMapPass 逐帧按它截断投影灯的枚举，所以与 PostProcessing 页把它归进
    /// 「必须重建」区不同，这里写完同样要排一帧重绘。
    /// </summary>
    public double DirectionalLightLimit
    {
        get => directionalLightLimit;
        set
        {
            if (!SetProperty(ref directionalLightLimit, value))
                return;

            DirectionalLightLimitChanged?.Invoke(value);
            FrameRequested?.Invoke();
        }
    }

    /// <summary>相机远裁剪面；决定级联分割范围，写穿到场景相机（近端 0.4 与旧处理器写死的一致）。</summary>
    public double CameraFar
    {
        get => cameraFar;
        set
        {
            if (!SetProperty(ref cameraFar, value))
                return;

            scene?.MainCamera.SetClippingPlanes(0.4f, (float)value);
            FrameRequested?.Invoke();
        }
    }

    /// <summary>画平行光体积；宿主的调试设置只有视图够得着，走值事件转写。</summary>
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

    /// <summary>画包围盒；转写路径同上。</summary>
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

    /// <summary>CSM 级联数变化；视图转写进宿主 Settings（四舍五入取 int）。</summary>
    public event Action<double>? CsmCascadeCountChanged;

    /// <summary>CSM 分割系数变化；视图转写（取 float）。</summary>
    public event Action<double>? CsmSplitLambdaChanged;

    /// <summary>CSM 阴影图分辨率变化；视图按 256 的倍数取整后转写。</summary>
    public event Action<double>? CsmShadowMapResolutionChanged;

    /// <summary>平行光上限变化；视图转写（四舍五入取 int）。</summary>
    public event Action<double>? DirectionalLightLimitChanged;

    /// <summary>画平行光体积开关变化；视图转写进 Settings.Debug。</summary>
    public event Action<bool>? ShowDirectionalLightToggled;

    /// <summary>画包围盒开关变化；视图转写进 Settings.Debug。</summary>
    public event Action<bool>? ShowBoundingBoxToggled;

    /// <summary>把俯角/方位合成主光旋转；初值与滑杆写穿共用这一条路径。</summary>
    private void ApplyMainRotation() => Main.RotationDegrees = new Vector3((float)pitch, (float)yaw, 0);

    /// <summary>
    /// 把四个 ShadowConfig 值写进两盏灯并请求重绘；旧 Report() 的读数刷新由逐帧 Tick 取代。
    /// </summary>
    private void ApplyShadowConfig()
    {
        foreach (var light in new[] { Main, Second })
        {
            light.ShadowConfig.Width = (int)Math.Round(shadowWidth);
            light.ShadowConfig.Height = (int)Math.Round(shadowHeight);
            light.ShadowConfig.NearPlane = (float)shadowNear;
            light.ShadowConfig.FarPlane = (float)shadowFar;
        }

        FrameRequested?.Invoke();
    }
}
