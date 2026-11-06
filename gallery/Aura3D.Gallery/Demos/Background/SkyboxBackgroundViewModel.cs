using Aura3D.Core.Nodes;
using Aura3D.Core.Renderers;
using Aura3D.Core.Resources;
using Aura3D.Core.Scenes;
using Aura3D.Gallery.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Irihi.Lingua;
using System;
using System.Collections;
using System.Linq;
using System.Numerics;
using Color = System.Drawing.Color;

namespace Aura3D.Gallery.Demos;

/// <summary>
/// 背景来源与天空盒页的界面状态，按 Particles/PostProcessing 页的样板 MVVM 化。分工：
/// <list type="bullet">
///   <item><description>文案：每个标签一个 Lingua observable，XAML 用 <c>{Binding Xxx^}</c> 订阅，
///     换语言时由绑定自动收到新值。</description></item>
///   <item><description>参数：滑杆/开关/下拉双向绑定到这里的属性。相机四参数与背景来源写穿到
///     <see cref="AttachScene"/> 登记的场景对象上——场景建好前的拨动先落在 backing 字段里，
///     登记时统一补写（旧代码此时会丢写）。宿主 <see cref="PipelineSettings"/> 上的两根滑杆
///     （IblAmbientIntensity / ToneMappingExposure）VM 够不着（同 PostProcessing 页），
///     setter 走「backing + 值事件」，由视图把新值转写进 Settings。</description></item>
///   <item><description>Readout：Lingua 1.1 的 format builder 组装 {0}..{6}——来源名、背景 OneOf
///     分支、投影变体与两张 IBL 缓存的 fbo 号。分支与 fbo 号只有场景/相机知道，逐帧读；
///     变体、FarPlane 与画背景开关读 VM 的 backing（VM 是这些相机参数的唯一改写者，
///     登记时同步过一次）。视图逐帧调 <see cref="Tick"/> 重读，语言切换时模板自动换。</description></item>
///   <item><description>引擎模型：平行光（可见性开关的写穿目标）与三份背景素材住在这里，
///     视图只负责生命周期——取资产喂 <see cref="SetBackgroundAssets"/>、建场景后登记场景。</description></item>
/// </list>
/// 视图桥是一组杂项事件：<see cref="FrameRequested"/>（任何参数变化后要排一次重绘）与两个
/// Settings 转写事件；重烘按钮直接命令化，作废缓存调的也是相机节点自己的 API，VM 不碰 DemoContext。
/// </summary>
public sealed partial class SkyboxBackgroundViewModel : ObservableObject
{
    // 引擎在相机节点上缓存 IBL 两件套用的键，登记/读数/作废三处共用。
    private const string IrradianceKey = "IrradianceMap";
    private const string PrefilterKey = "PrefilteredEnvironmentMap";

    private static readonly LinguaKey[] SourceKeys =
    [
        Strings.Keys.Background_SourceEngineDefault,
        Strings.Keys.Background_SourceCubeMap,
        Strings.Keys.Background_SourceHdr,
        Strings.Keys.Background_SourceFlat,
    ];

    /// <summary>
    /// 建好 VM：参数初值对齐真正生效的那一份——两根 Settings 滑杆取宿主当前值（等价于旧页面
    /// 构造函数里那批「行控件写回」，只是方向反过来——绑定初始化替它完成）；相机四参数沿用
    /// 旧页面的常量初值，相机要到建场景才存在，这些值随后由 <see cref="AttachScene"/> 写过去。
    /// Readout 的模板与计算参数也在这里装配；命令经 <c>[RelayCommand]</c> 源生成，无需接线。
    /// </summary>
    /// <param name="settings">宿主当前生效的管线设置，仅在构造期读初值，不保存引用。</param>
    public SkyboxBackgroundViewModel(PipelineSettings settings)
    {
        SourceOptions = SourceKeys.Select(k => k.T()).ToList();

        iblAmbientIntensity = settings.IblAmbientIntensity;
        toneMappingExposure = settings.ToneMappingExposure;

        // 默认停在六面立方图那一支（sourceIndex=1），与旧页面一致。
        sourceIndex = 1;
        selectedSource = SourceOptions[sourceIndex];

        // Readout 模板 {0}..{6}：占位语义与旧 Report() 里的 string.Format 逐个对齐——{0} 来源名；
        // {1} 背景 OneOf 分支（尺寸只有场景知道）；{2} 投影变体、{3} FarPlane 与 {4} 画背景开关
        // 读 VM 的 backing（VM 是这些相机参数的唯一改写者，登记时同步过一次，同 PostProcessing
        // 页重建参数读 backing 的做法）；尾部 {5}/{6} 是两张 IBL 缓存的 fbo 号，只能逐帧问相机节点。
        ReadoutText = Strings.Keys.Background_Readout.CreateFormat()
            .Arg(() => (string)SourceOptions[sourceIndex]!)
            .Arg(() => BranchText())
            .Arg(() => orthographic
                ? Strings.Keys.Background_VariantOrtho.Format(orthoSize)
                : Strings.Keys.Background_VariantPersp.Format(fieldOfView))
            .Arg(() => (float)farPlane)
            .Arg(() => renderBackground
                ? Strings.Keys.Background_RenderOn.T()
                : Strings.Keys.Background_RenderOff.T())
            .Arg(() => scene?.MainCamera.GetPipelineGpuState<CubeRenderTarget>(IrradianceKey)?.FrameBufferId ?? 0)
            .Arg(() => scene?.MainCamera.GetPipelineGpuState<CubeRenderTarget>(PrefilterKey)?.FrameBufferId ?? 0)
            .Build();
    }

    /// <summary>
    /// 场景建好后由视图登记：相机四参数、背景与画背景开关从此直接写穿到真对象上
    /// （等价旧 BuildScene 里的 ApplyCamera + ApplyBackground；画背景开关相机默认开、
    /// 与初值一致，补写一次让早于建场景的拨动也不丢）。
    /// </summary>
    public void AttachScene(Scene attached)
    {
        scene = attached;

        ApplyCamera(scene.MainCamera);
        ApplyBackground(scene);
        scene.MainCamera.IsRenderBackground = renderBackground;
    }

    /// <summary>
    /// 资产阶段取到的三份背景素材：六面立方图、HDRI 全景图与单张平面图；
    /// 在此之前 <see cref="ApplyBackground"/> 还没有可喂的资产（旧代码同样是资产后才填）。
    /// </summary>
    public void SetBackgroundAssets(CubeTexture cube, Texture hdr, Texture flat)
    {
        fileCube = cube;
        hdrPanorama = hdr;
        flatImage = flat;
    }

    /// <summary>逐帧调用：重读 Readout 的计算参数（背景分支与 IBL 缓存号）。</summary>
    public void Tick() => ReadoutText.Refresh();

    /// <summary>任何参数变化后请求一帧重绘；视图把它接到宿主的 InvalidateRender 上。</summary>
    public event Action? FrameRequested;

    /// <summary>IBL 环境光强度变化；宿主的 PipelineSettings 只有视图够得着，由视图转写（取 float）。</summary>
    public event Action<double>? IblAmbientIntensityChanged;

    /// <summary>曝光变化；视图转写（取 float）。</summary>
    public event Action<double>? ToneMappingExposureChanged;

    /// <summary>逐帧读数。语言切换时模板换新，绑定自动收到重排后的整句。</summary>
    public LinguaFormatObservable ReadoutText { get; }

    /// <summary>来源下拉的选项文案；当前语言的一份快照，随页面重建。</summary>
    public IList SourceOptions { get; }

    /// <summary>
    /// 重烘 IBL：把缓存在相机节点上的辐照度图与预滤波图作废，下一帧管线重烘；
    /// 源生成 <c>RebakeIblCommand</c> 供按钮绑定。换背景时管线自己会作废缓存，
    /// 这个按钮服务的是「背景资源内容被原地改过」的场景。
    /// </summary>
    [RelayCommand]
    private void RebakeIbl()
    {
        var camera = scene?.MainCamera;

        if (camera == null)
            return;

        foreach (var key in new[] { IrradianceKey, PrefilterKey })
        {
            if (camera.GetPipelineGpuState<CubeRenderTarget>(key) is { } target)
                target.Invalidate();
        }

        FrameRequested?.Invoke();
    }

    /// <summary>平行光：可见性开关的写穿目标，建场景时由视图挂进场景。</summary>
    public DirectionalLight Sun { get; } = new()
    {
        LightColor = Color.White,
        Irradiance = 60000,
        RotationDegrees = new Vector3(-42f, -18f, 0),
    };

    private Scene? scene;

    // 三份背景素材，LoadAssetsAsync 之后由 SetBackgroundAssets 填上。
    private CubeTexture? fileCube;
    private Texture? hdrPanorama;
    private Texture? flatImage;

    private int sourceIndex;
    private object? selectedSource;

    private double cubeFaceSize = 128;
    private double orthoSize = 12;
    private double fieldOfView = 75;
    private double farPlane = 100;
    private double iblAmbientIntensity;
    private double toneMappingExposure;

    private bool orthographic;
    private bool renderBackground = true;
    private bool sunVisible = true;

    /// <summary>页首说明。</summary>
    public IObservable<string?> IntroLabel => Strings.Instance.Background_Intro;

    /// <summary>「背景来源」分组标题。</summary>
    public IObservable<string?> SourceSectionLabel => Strings.Instance.Background_Source;

    /// <summary>来源下拉标签；原文是硬编码的属性名，迁移时补的键，两语言同文。</summary>
    public IObservable<string?> SourceLabel => Strings.Instance.Background_SceneBackground;

    /// <summary>HDRI 转立方图边长滑杆标签。</summary>
    public IObservable<string?> CubeSizeLabel => Strings.Instance.Background_CubeSize;

    /// <summary>画背景开关标签。</summary>
    public IObservable<string?> RenderBackgroundLabel => Strings.Instance.Background_RenderBackground;

    /// <summary>clear 行为说明。</summary>
    public IObservable<string?> ClearNoteLabel => Strings.Instance.Background_ClearNote;

    /// <summary>「两个着色器变体」分组标题。</summary>
    public IObservable<string?> ShaderVariantsSectionLabel => Strings.Instance.Background_ShaderVariants;

    /// <summary>正交投影开关标签。</summary>
    public IObservable<string?> OrthographicLabel => Strings.Instance.Background_Orthographic;

    /// <summary>OrthographicSize 滑杆标签。</summary>
    public IObservable<string?> OrthoSizeLabel => Strings.Instance.Background_OrthoSize;

    /// <summary>FOV 滑杆标签。</summary>
    public IObservable<string?> FieldOfViewLabel => Strings.Instance.Background_FieldOfView;

    /// <summary>FarPlane 滑杆标签。</summary>
    public IObservable<string?> FarPlaneLabel => Strings.Instance.Background_FarPlane;

    /// <summary>透视/正交两分支表现不对称的说明。</summary>
    public IObservable<string?> ProjectionNoteLabel => Strings.Instance.Background_ProjectionNote;

    /// <summary>「只有立方图喂 IBL」分组标题。</summary>
    public IObservable<string?> CubemapIblSectionLabel => Strings.Instance.Background_CubemapFeedsIbl;

    /// <summary>IBL 环境光强度滑杆标签；原文是硬编码的设置项名，迁移时补的键，两语言同文。</summary>
    public IObservable<string?> AmbientLabel => Strings.Instance.Background_IblAmbientIntensity;

    /// <summary>曝光滑杆标签；同上，补键、两语言同文。</summary>
    public IObservable<string?> ExposureLabel => Strings.Instance.Background_ToneMappingExposure;

    /// <summary>平行光可见开关标签。</summary>
    public IObservable<string?> SunVisibleLabel => Strings.Instance.Background_KeyLightVisible;

    /// <summary>「重烘 IBL」按钮文案。</summary>
    public IObservable<string?> RebakeIblLabel => Strings.Instance.Background_RebakeIbl;

    /// <summary>IBL 缓存机制说明。</summary>
    public IObservable<string?> IblNoteLabel => Strings.Instance.Background_IblNote;

    /// <summary>读数行标题。</summary>
    public IObservable<string?> IblCacheLabel => Strings.Instance.Background_IblCache;

    /// <summary>
    /// 背景来源下拉的选中项；换来源立即把新的 OneOf 分支写到场景背景上（场景建好之前
    /// 只记在 backing 里，登记时统一补写），HDRI 分支还会按当前边长现转一份立方图。
    /// </summary>
    public object? SelectedSource
    {
        get => selectedSource;
        set
        {
            if (value == null || !SetProperty(ref selectedSource, value))
                return;

            sourceIndex = Math.Max(0, SourceOptions.IndexOf(value));

            if (scene != null)
                ApplyBackground(scene);

            FrameRequested?.Invoke();
        }
    }

    /// <summary>
    /// HDRI 转立方图的边长；按 32 的倍数吸附（旧处理器的步进口径，HDRI 换立方图只认
    /// 32 步进），拖动落在同一格内时 SetProperty 判未变、不再触发重转与重绘。
    /// </summary>
    public double CubeFaceSize
    {
        get => cubeFaceSize;
        set
        {
            if (!SetProperty(ref cubeFaceSize, Math.Round(value / 32) * 32))
                return;

            if (scene != null)
                ApplyBackground(scene);

            FrameRequested?.Invoke();
        }
    }

    /// <summary>画不画背景；只管 BackgroundPass 画不画，不管 clear（口径见下方的说明行）。</summary>
    public bool RenderBackground
    {
        get => renderBackground;
        set
        {
            if (!SetProperty(ref renderBackground, value))
                return;

            var camera = scene?.MainCamera;

            if (camera != null)
                camera.IsRenderBackground = value;

            FrameRequested?.Invoke();
        }
    }

    /// <summary>
    /// 正交投影开关。四个相机参数共享同一条写穿路径——改任何一个都整组写回
    /// （<see cref="ApplyCameraIfNeeded"/>），与旧处理器逐行等价；两个着色器变体对这组
    /// 参数的读取不对称（FOV 只改透视取景、FarPlane 只缩放正交天空，OrthographicSize 只缩
    /// 前景），口径见检视面板里的说明，语义全在引擎侧，这里只照写。
    /// </summary>
    public bool Orthographic
    {
        get => orthographic;
        set
        {
            if (SetProperty(ref orthographic, value))
                ApplyCameraIfNeeded();
        }
    }

    /// <summary>OrthographicSize（度）；正交分支里只缩放前景，天空盒片元的框取量引擎写死。</summary>
    public double OrthoSize
    {
        get => orthoSize;
        set
        {
            if (SetProperty(ref orthoSize, value))
                ApplyCameraIfNeeded();
        }
    }

    /// <summary>FieldOfView（度）；只有透视分支读它。</summary>
    public double FieldOfView
    {
        get => fieldOfView;
        set
        {
            if (SetProperty(ref fieldOfView, value))
                ApplyCameraIfNeeded();
        }
    }

    /// <summary>FarPlane；只有正交分支的天空盒读它（透视 pass 的 near/far 写死 10..100）。</summary>
    public double FarPlane
    {
        get => farPlane;
        set
        {
            if (SetProperty(ref farPlane, value))
                ApplyCameraIfNeeded();
        }
    }

    /// <summary>IBL 环境光强度；新值经事件由视图写进宿主 Settings，随后请求一帧重绘。</summary>
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

    /// <summary>曝光；写穿路径同上。</summary>
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

    /// <summary>
    /// 平行光可见性；写穿目标就是 VM 持有的 <see cref="Sun"/>（旧代码建场景前拨开关会丢写，
    /// 现在开关状态先落在光对象上，建场景时一并生效）。
    /// </summary>
    public bool SunVisible
    {
        get => sunVisible;
        set
        {
            if (!SetProperty(ref sunVisible, value))
                return;

            Sun.Enable = value;
            FrameRequested?.Invoke();
        }
    }

    /// <summary>
    /// 把来源选择落到场景背景上：立方图分支直接喂资产，HDRI 分支按当前边长现转，
    /// 平面图分支铺满窗口，引擎默认分支与 Scene 构造里那张纯色图同款。
    /// 换背景资源时管线自己会作废 IBL 缓存，这里不必再碰。
    /// </summary>
    private void ApplyBackground(Scene target)
    {
        switch (sourceIndex)
        {
            case 1:
                target.Background = fileCube!;
                break;
            case 2:
                target.Background = HDRIToCubeTextureConverter.ConvertFromTexture(hdrPanorama!, (uint)cubeFaceSize);
                break;
            case 3:
                target.Background = flatImage!;
                break;
            default:
                // 引擎在 Scene 构造里就把这张纯色图当默认背景，分支号走 T1。
                target.Background = Texture.CreateFromColor(Color.AliceBlue);
                break;
        }
    }

    /// <summary>场景没建好前只改 backing、不碰引擎对象；建好后整组写回并排一帧重绘。</summary>
    private void ApplyCameraIfNeeded()
    {
        var camera = scene?.MainCamera;

        if (camera == null)
            return;

        ApplyCamera(camera);

        FrameRequested?.Invoke();
    }

    private void ApplyCamera(Camera camera)
    {
        camera.ProjectionType = orthographic ? ProjectionType.Orthographic : ProjectionType.Perspective;
        camera.OrthographicSize = (float)orthoSize;
        camera.FieldOfView = (float)fieldOfView;
        camera.FarPlane = (float)farPlane;
    }

    /// <summary>Readout 的 {1}：当前背景落在 OneOf 的哪一支、尺寸多少；场景未建时先给空串占位。</summary>
    private string BranchText()
    {
        if (scene == null)
            return string.Empty;

        var background = scene.Background;

        return background.IsT0
            ? Strings.Keys.Background_BranchCube.Format(background.AsT0.Width, background.AsT0.Height)
            : background.IsT1
                ? Strings.Keys.Background_BranchImage.Format(background.AsT1.Width, background.AsT1.Height)
                : Strings.Keys.Background_BranchNone.T();
    }
}
