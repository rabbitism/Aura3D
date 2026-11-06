using Aura3D.Core.Nodes;
using Aura3D.Core.Renderers;
using Aura3D.Gallery.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Irihi.Lingua;
using System;
using System.Numerics;
using Color = System.Drawing.Color;

namespace Aura3D.Gallery.Demos;

/// <summary>
/// 管线设置页的界面状态，按 Particles 页的样板 MVVM 化。分工：
/// <list type="bullet">
///   <item><description>文案：AXAML 直接写 <c>{Translate}</c> 标记扩展取 Strings.Keys 键值；换语言走外壳既有的整页重挂。</description></item>
///   <item><description>参数：滑杆/开关双向绑定到这里的属性。宿主的 <see cref="PipelineSettings"/>
///     归 DemoContext 所有、VM 够不着（同 Particles 页的宿主调试设置），所以 setter 走
///     「backing + 值事件」，由视图把新值逐项转写进 Settings；逐帧区参数写完再发
///     <see cref="FrameRequested"/>，等价于旧处理器的 ApplyLive。</description></item>
///   <item><description>Readout：Lingua 1.1 的 format builder 组装 {0}..{8}——管线种类、三项重建
///     参数与尾部提示语全是计算参数，视图逐帧调 <see cref="Tick"/> 重读，语言切换时模板自动换。</description></item>
///   <item><description>引擎模型：平行光住在这里（CastShadow 开关的写穿目标），建场景时由视图挂进场景。</description></item>
/// </list>
/// 视图桥只有两个杂项事件：<see cref="FrameRequested"/>（逐帧区变化要排一次重绘）与
/// <see cref="ReloadRequested"/>（下半区改的是着色器变体与纹理尺寸，只有整页重建才认）。
/// </summary>
public sealed partial class PostProcessingViewModel : ObservableObject
{
    private readonly PipelineKind pipelineKind;

    private bool needsRebuildHint;

    /// <summary>
    /// 建好 VM：参数初值对齐宿主 Settings 里真正生效的那一份（等价于旧页面构造函数里那批
    /// 「行控件写回」，只是方向反过来——绑定初始化替它完成）；同时记住当前管线，Readout 的
    /// 「谁读谁」矩阵按它判。管线在页面生命周期内不会变——换管线必经宿主整页重建。
    /// </summary>
    /// <param name="settings">宿主当前生效的管线设置，仅在构造期读初值，不保存引用。</param>
    /// <param name="pipeline">宿主当前要求的管线。</param>
    public PostProcessingViewModel(PipelineSettings settings, PipelineKind pipeline)
    {
        pipelineKind = pipeline;

        toneMappingExposure = settings.ToneMappingExposure;
        brightnessClamp = settings.BrightnessClamp;
        ambientIntensity = settings.AmbientIntensity;
        iblAmbientIntensity = settings.IblAmbientIntensity;
        enableFxaa = settings.EnableFxaa;
        enableFrustumCulling = settings.EnableFrustumCulling;
        directionalLightLimit = settings.DirectionalLightLimit;
        pointLightLimit = settings.PointLightLimit;
        spotLightLimit = settings.SpotLightLimit;
        csmCascadeCount = settings.CsmCascadeCount;
        csmSplitLambda = settings.CsmSplitLambda;
        csmShadowMapResolution = settings.CsmShadowMapResolution;

        castShadow = Sun.CastShadow;

        // Readout 模板 {0}..{8}：占位语义与旧 Report() 里的 string.Format 逐个对齐——
        // {1}/{3} 看 PBR、{2} 看 BlinnPhong/Cel、{4} 看是否有阴影 pass；✓/× 与两段
        // 「无某 pass」文案都走原键。三项重建参数读 VM 的 backing（它始终与宿主 Settings
        // 同步：初值取自它、后续写回也经视图落到它），尾部提示语随 Reload 按钮翻转。
        ReadoutText = Strings.Keys.PostProcessing_MatrixReadout.CreateFormat()
            .Arg(() => pipelineKind)
            .Arg(() => Pbr ? "✓" : Strings.Keys.PostProcessing_ExposureNoTone.T())
            .Arg(() => Ambient ? "✓" : "×")
            .Arg(() => Pbr ? "✓" : "×")
            .Arg(() => Shadow ? "✓" : Strings.Keys.PostProcessing_CsmNoShadowMap.T())
            .Arg(() => (int)pointLightLimit)
            .Arg(() => (int)csmCascadeCount)
            .Arg(() => (int)csmShadowMapResolution)
            .Arg(() => needsRebuildHint
                ? Strings.Keys.PostProcessing_HintRebuilt.T()
                : Strings.Keys.PostProcessing_HintNeedsReload.T())
            .Build();
    }

    /// <summary>逐帧调用：重读 Readout 的计算参数（重建参数与提示语）。</summary>
    public void Tick() => ReadoutText.Refresh();

    /// <summary>逐帧区参数（含平行光投影开关）变化后请求一帧重绘；视图把它接到宿主的 ApplyLive 上。</summary>
    public event Action? FrameRequested;

    /// <summary>「Reload」按钮触发；视图把它接到宿主的整页重建上，VM 不碰 DemoContext。</summary>
    public event Action? ReloadRequested;

    /// <summary>逐帧读数。语言切换时模板换新，绑定自动收到重排后的整句。</summary>
    public LinguaFormatObservable ReadoutText { get; }

    /// <summary>
    /// 请求整页重建；源生成 <c>ReloadCommand</c> 供按钮绑定。重建后页面连同本 VM 一起换新，
    /// 所以 <c>needsRebuildHint</c> 只在当前生命周期内把读数尾巴翻成「已重建生效」。
    /// </summary>
    [RelayCommand]
    private void Reload()
    {
        needsRebuildHint = true;

        ReloadRequested?.Invoke();
    }

    /// <summary>平行光：CastShadow 开关的写穿目标，建场景时由视图挂进场景。</summary>
    public DirectionalLight Sun { get; } = new()
    {
        LightColor = Color.White,
        CastShadow = true,
        Irradiance = 120000,
        RotationDegrees = new Vector3(-42f, -28f, 0),
    };

    private double toneMappingExposure;
    private double brightnessClamp;
    private double ambientIntensity;
    private double iblAmbientIntensity;
    private double directionalLightLimit;
    private double pointLightLimit;
    private double spotLightLimit;
    private double csmCascadeCount;
    private double csmSplitLambda;
    private double csmShadowMapResolution;

    private bool enableFxaa;
    private bool enableFrustumCulling;
    private bool castShadow;

    // 与旧 Report() 里的三个局部布尔同源：按当前管线判断各 pass 是否存在。
    private bool Pbr => pipelineKind is PipelineKind.PBRDeferred or PipelineKind.PBRForward;

    private bool Ambient => pipelineKind is PipelineKind.BlinnPhong or PipelineKind.CelShading;

    private bool Shadow => pipelineKind is not PipelineKind.NoLight;

    /// <summary>
    /// 色调映射曝光；逐帧区参数——新值经事件由视图写进宿主 Settings，随后请求一帧重绘。
    /// </summary>
    public double ToneMappingExposure
    {
        get => toneMappingExposure;
        set
        {
            if (!SetProperty(ref toneMappingExposure, value))
                return;

            ToneMappingExposureChanged?.Invoke(value);
            FrameRequested?.Invoke();
        }
    }

    /// <summary>亮度上限；逐帧区参数，写穿路径同上。</summary>
    public double BrightnessClamp
    {
        get => brightnessClamp;
        set
        {
            if (!SetProperty(ref brightnessClamp, value))
                return;

            BrightnessClampChanged?.Invoke(value);
            FrameRequested?.Invoke();
        }
    }

    /// <summary>环境光强度（BlinnPhong / Cel 读）；逐帧区参数。</summary>
    public double AmbientIntensity
    {
        get => ambientIntensity;
        set
        {
            if (!SetProperty(ref ambientIntensity, value))
                return;

            AmbientIntensityChanged?.Invoke(value);
            FrameRequested?.Invoke();
        }
    }

    /// <summary>IBL 环境光强度（PBR 读）；逐帧区参数。</summary>
    public double IblAmbientIntensity
    {
        get => iblAmbientIntensity;
        set
        {
            if (!SetProperty(ref iblAmbientIntensity, value))
                return;

            IblAmbientIntensityChanged?.Invoke(value);
            FrameRequested?.Invoke();
        }
    }

    /// <summary>FXAA；逐帧区开关。</summary>
    public bool EnableFxaa
    {
        get => enableFxaa;
        set
        {
            if (!SetProperty(ref enableFxaa, value))
                return;

            FxaaToggled?.Invoke(value);
            FrameRequested?.Invoke();
        }
    }

    /// <summary>视锥剔除；逐帧区开关。</summary>
    public bool EnableFrustumCulling
    {
        get => enableFrustumCulling;
        set
        {
            if (!SetProperty(ref enableFrustumCulling, value))
                return;

            FrustumCullingToggled?.Invoke(value);
            FrameRequested?.Invoke();
        }
    }

    /// <summary>
    /// 平行光上限；重建区参数——着色器变体按它编译，管线建好后改字段没人读，
    /// 写完不必重绘，等 Reload。
    /// </summary>
    public double DirectionalLightLimit
    {
        get => directionalLightLimit;
        set
        {
            if (SetProperty(ref directionalLightLimit, value))
                DirectionalLightLimitChanged?.Invoke(value);
        }
    }

    /// <summary>点光上限；重建区参数，同上。</summary>
    public double PointLightLimit
    {
        get => pointLightLimit;
        set
        {
            if (SetProperty(ref pointLightLimit, value))
                PointLightLimitChanged?.Invoke(value);
        }
    }

    /// <summary>聚光灯上限；重建区参数。</summary>
    public double SpotLightLimit
    {
        get => spotLightLimit;
        set
        {
            if (SetProperty(ref spotLightLimit, value))
                SpotLightLimitChanged?.Invoke(value);
        }
    }

    /// <summary>CSM 级联数；重建区参数。</summary>
    public double CsmCascadeCount
    {
        get => csmCascadeCount;
        set
        {
            if (SetProperty(ref csmCascadeCount, value))
                CsmCascadeCountChanged?.Invoke(value);
        }
    }

    /// <summary>CSM 对数/均匀分割系数；重建区参数。</summary>
    public double CsmSplitLambda
    {
        get => csmSplitLambda;
        set
        {
            if (SetProperty(ref csmSplitLambda, value))
                CsmSplitLambdaChanged?.Invoke(value);
        }
    }

    /// <summary>CSM 阴影图分辨率；重建区参数。</summary>
    public double CsmShadowMapResolution
    {
        get => csmShadowMapResolution;
        set
        {
            if (SetProperty(ref csmShadowMapResolution, value))
                CsmShadowMapResolutionChanged?.Invoke(value);
        }
    }

    /// <summary>
    /// 平行光投影开关；写穿目标就是 VM 持有的 <see cref="Sun"/>（旧代码建场景前拨开关会丢写，
    /// 现在开关状态先落在光对象上，建场景时一并生效），重绘请求仍经 FrameRequested 桥到视图。
    /// </summary>
    public bool CastShadow
    {
        get => castShadow;
        set
        {
            if (!SetProperty(ref castShadow, value))
                return;

            Sun.CastShadow = value;
            FrameRequested?.Invoke();
        }
    }

    /// <summary>曝光变化；视图转写进宿主 Settings（取 float 的口径在视图侧，与旧处理器一致）。</summary>
    public event Action<double>? ToneMappingExposureChanged;

    /// <summary>亮度上限变化；视图转写。</summary>
    public event Action<double>? BrightnessClampChanged;

    /// <summary>环境光强度变化；视图转写。</summary>
    public event Action<double>? AmbientIntensityChanged;

    /// <summary>IBL 环境光强度变化；视图转写。</summary>
    public event Action<double>? IblAmbientIntensityChanged;

    /// <summary>FXAA 开关变化；视图转写。</summary>
    public event Action<bool>? FxaaToggled;

    /// <summary>视锥剔除开关变化；视图转写。</summary>
    public event Action<bool>? FrustumCullingToggled;

    /// <summary>平行光上限变化；视图转写（取 int）。</summary>
    public event Action<double>? DirectionalLightLimitChanged;

    /// <summary>点光上限变化；视图转写（取 int）。</summary>
    public event Action<double>? PointLightLimitChanged;

    /// <summary>聚光灯上限变化；视图转写（取 int）。</summary>
    public event Action<double>? SpotLightLimitChanged;

    /// <summary>CSM 级联数变化；视图转写（取 int）。</summary>
    public event Action<double>? CsmCascadeCountChanged;

    /// <summary>CSM 分割系数变化；视图转写（取 float）。</summary>
    public event Action<double>? CsmSplitLambdaChanged;

    /// <summary>CSM 阴影图分辨率变化；视图转写（取 int）。</summary>
    public event Action<double>? CsmShadowMapResolutionChanged;
}
