using Aura3D.Core.Geometries;
using Aura3D.Core.Nodes;
using Aura3D.Core.Renderers;
using Aura3D.Core.Resources;
using Aura3D.Core.Scenes;
using Aura3D.Gallery.Kit;
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
/// 环境图与 IBL 页的界面状态，MVVM 化（模式照抄 Particles/PostProcessing）。分工：
/// <list type="bullet">
///   <item><description>文案：AXAML 直接写 <c>{Translate}</c> 标记扩展取 Strings.Keys 键值；换语言走外壳既有的整页重挂。</description></item>
///   <item><description>参数：滑杆/开关/下拉双向绑定到这里的属性。全景图与面边长直接写穿到
///     VM 持有的场景（<c>Scene.Background</c> 是引擎对象）；写宿主 <see cref="PipelineSettings"/>
///     的三项（IBL 强度/曝光/亮度上限）照 PostProcessing 页走「backing + 值事件」，
///     由视图逐项转写进 Settings，VM 不碰 DemoContext。</description></item>
///   <item><description>Readout：Lingua 1.1 的 format builder 组装 {0}..{7}，占位语义与旧
///     Report() 里的 string.Format 逐个对齐；fbo 号一类是逐帧读的计算参数，视图逐帧调
///     <see cref="Tick"/> 重读，语言切换时模板本身也会自动换。</description></item>
///   <item><description>引擎模型：平行光与五颗金属度×粗糙度组合球住在这里，视图只负责
///     生命周期——资产阶段取 HDRI/天空盒喂 <see cref="SetEnvironmentAssets"/>、
///     建场景后递场景进来、逐帧驱动自转。</description></item>
/// </list>
/// 引擎侧语义：Scene.Background 是唯一的环境光源入口，两条 PBR 管线拿它烘成辐照度图与
/// 预滤波反射图并缓存在相机节点的两个 GPU 状态槽上。五个来源分两类：前两个是文件资产
/// （1k HDRI 全景、六面天空盒），后三个是程序化生成的等距柱状全景图。文件那一类是这页
/// 的重点——没挂有内容的立方图时，管线退到引擎那张纯白立方图（DefaultIblAmbientCubeTexture），
/// 金属只剩一层没有方向的白光，看上去跟「IBL 没工作」一样。
/// </summary>
public sealed partial class EnvironmentViewModel : ObservableObject
{
    private const string IrradianceKey = "IrradianceMap";
    private const string PrefilterKey = "PrefilteredEnvironmentMap";

    private static readonly LinguaKey[] EnvKeys =
    [
        Strings.Keys.Environment_EnvHdri,
        Strings.Keys.Environment_EnvSkyboxCube,
        Strings.Keys.Environment_EnvStudio,
        Strings.Keys.Environment_EnvSunset,
        Strings.Keys.Environment_EnvTestChart,
    ];

    /// <summary>
    /// 前两个来源直接来自文件，程序化全景图从这一档开始。索引减去它才是 <see cref="BuildPanorama"/> 的花样号。
    /// </summary>
    private const int FirstProceduralIndex = 2;

    /// <summary>
    /// 建好 VM：参数初值对齐宿主 Settings 里真正生效的那一份（等价于旧页面构造函数里那批
    /// 「行控件写回」，方向反过来——绑定初始化替它完成）；再把 Readout 的模板与计算参数装配
    /// 起来；命令经 <c>[RelayCommand]</c> 源生成，无需在此接线。
    /// </summary>
    /// <param name="settings">宿主当前生效的管线设置，仅在构造期读初值，不保存引用。</param>
    public EnvironmentViewModel(PipelineSettings settings)
    {
        PanoramaOptions = EnvKeys.Select(k => k.T()).ToList();

        iblAmbientIntensity = settings.IblAmbientIntensity;
        toneMappingExposure = settings.ToneMappingExposure;
        brightnessClamp = settings.BrightnessClamp;

        selectedPanorama = PanoramaOptions[envIndex];

        // Readout 模板 {0}..{7}：背景分支（立方图带边长/普通图/未设）、全景选项名、请求面大小、
        // 两张缓存 CubeRenderTarget 的 fbo 号（逐帧读，重烘后自动带出新号）、IBL 强度与曝光
        // （读 VM backing——它始终与宿主 Settings 同步，{5:0.00}/{6:0.00} 由模板自带格式说明）、
        // 尾部的重烘提示。场景没递进来之前各槽位回落到「未设/0」，建好场景的当帧就被真值覆盖。
        ReadoutText = Strings.Keys.Environment_Readout.CreateFormat()
            .Arg(() =>
            {
                // OneOf 是结构体：场景缺席时回落到 default（两支都不占），正好显示「未设」。
                var background = scene?.Background ?? default;

                return background.IsT0
                    ? Strings.Keys.Environment_BackgroundCube.Format(background.AsT0.Width, background.AsT0.Height)
                    : background.IsT1
                        ? Strings.Keys.Environment_BackgroundPlain.T()
                        : Strings.Keys.Environment_BackgroundNone.T();
            })
            .Arg(() => (string)PanoramaOptions[envIndex]!)
            .Arg(() => effectiveFaceSize)
            .Arg(() => scene?.MainCamera.GetPipelineGpuState<CubeRenderTarget>(IrradianceKey)?.FrameBufferId ?? 0)
            .Arg(() => scene?.MainCamera.GetPipelineGpuState<CubeRenderTarget>(PrefilterKey)?.FrameBufferId ?? 0)
            .Arg(() => iblAmbientIntensity)
            .Arg(() => toneMappingExposure)
            .Arg(() => rebaked
                ? Strings.Keys.Environment_Rebaked.T()
                : Strings.Keys.Environment_NotRebaked.T())
            .Build();
    }

    /// <summary>
    /// 场景建好后由视图递进来：此后换全景图/面边长直接改 <c>Scene.Background</c>，
    /// 背景开关与重烘也都从它取相机。递进来的当帧先把当前选择落成 Background
    /// （等价旧 BuildScene 里的 ApplyEnvironment(scene)）。
    /// </summary>
    public void AttachScene(Scene attached)
    {
        scene = attached;

        ApplyEnvironment();
    }

    /// <summary>
    /// 资产阶段取到的两张文件环境图（PbrMaterials 页 SetTextures 的先例）：1k HDRI
    /// 等距柱状全景与六面天空盒立方图。BuildScene 必然晚于资产阶段，ApplyEnvironment 放心用。
    /// </summary>
    public void SetEnvironmentAssets(Texture hdrPanoramaTexture, CubeTexture fileCubeTexture)
    {
        hdrPanorama = hdrPanoramaTexture;
        fileCube = fileCubeTexture;
    }

    /// <summary>逐帧调用：重读 Readout 的计算参数（背景分支、fbo 号、重烘提示）。</summary>
    public void Tick() => ReadoutText.Refresh();

    /// <summary>换图/换面边长/重烘/参数写完要排一帧重绘；视图把它接到宿主的 InvalidateRender 上。</summary>
    public event Action? FrameRequested;

    /// <summary>「Reload」按钮触发；视图把它接到宿主的整页重建上，VM 不碰 DemoContext。</summary>
    public event Action? ReloadRequested;

    /// <summary>IBL 环境光强度变化；视图转写进宿主 Settings（取 float 的口径在视图侧）。</summary>
    public event Action<double>? IblAmbientIntensityChanged;

    /// <summary>曝光变化；视图转写。</summary>
    public event Action<double>? ToneMappingExposureChanged;

    /// <summary>亮度上限变化；视图转写。</summary>
    public event Action<double>? BrightnessClampChanged;

    /// <summary>逐帧读数。语言切换时模板换新，绑定自动收到重排后的整句。</summary>
    public LinguaFormatObservable ReadoutText { get; }

    /// <summary>全景图下拉的选项文案；当前语言的一份快照，随页面重建。</summary>
    public IList PanoramaOptions { get; }

    /// <summary>
    /// 请求整页重建；源生成 <c>ReloadCommand</c> 供按钮绑定。重建后页面连同本 VM 一起换新
    /// （换管线也走这条）。
    /// </summary>
    [RelayCommand]
    private void Reload() => ReloadRequested?.Invoke();

    /// <summary>
    /// 强制重烘两张 IBL 缓存；源生成 <c>RebakeCommand</c>。旧处理器里的 Report() 由逐帧
    /// Tick 取代，显示时机不变。
    /// </summary>
    [RelayCommand]
    private void Rebake()
    {
        InvalidateIblCaches();

        rebaked = true;

        FrameRequested?.Invoke();
    }

    /// <summary>平行光：可见开关的写穿目标，建场景时由视图挂进场景（照 PostProcessing 页的 Sun）。</summary>
    public DirectionalLight Sun { get; } = new()
    {
        LightColor = Color.White,
        CastShadow = true,
        Irradiance = 30000,
        RotationDegrees = new Vector3(-38f, -25f, 0),
    };

    /// <summary>五颗金属度×粗糙度组合球的排架；视图建场景时挂进场景、逐帧自转。</summary>
    public Node BallRow { get; } = BuildBallRow();

    private Scene? scene;

    // 资产阶段喂进来的两张文件环境图；ApplyEnvironment 只在场景（必晚于资产）就位后才会读它们。
    private Texture? hdrPanorama;
    private CubeTexture? fileCube;

    private int envIndex;
    private object? selectedPanorama;

    // 滑杆 backing 保留拖动的原值（旧页面也不把取整结果写回滑杆）；effectiveFaceSize 才是烘培用的。
    private double faceSize = 128;
    private uint effectiveFaceSize = 128;

    private double iblAmbientIntensity;
    private double toneMappingExposure;
    private double brightnessClamp;

    private bool sunVisible = true;
    private bool renderBackground = true;
    private bool rebaked;

    /// <summary>
    /// 全景图下拉的选中项；换图立即重设 Background（setter 自己会作废两张 IBL 缓存），
    /// 场景没建好前只记账、等 AttachScene 时一并落地。
    /// </summary>
    public object? SelectedPanorama
    {
        get => selectedPanorama;
        set
        {
            if (value == null || !SetProperty(ref selectedPanorama, value))
                return;

            envIndex = Math.Max(0, PanoramaOptions.IndexOf(value));

            if (scene != null)
                ApplyEnvironment();

            FrameRequested?.Invoke();
        }
    }

    /// <summary>立方图面边长（全景图分支的烘培目标边长）；按 32 对齐生效，滑杆显示原值。</summary>
    public double FaceSize
    {
        get => faceSize;
        set
        {
            if (!SetProperty(ref faceSize, value))
                return;

            effectiveFaceSize = (uint)Math.Round(value / 32) * 32;

            if (scene != null)
                ApplyEnvironment();

            FrameRequested?.Invoke();
        }
    }

    /// <summary>IBL 环境光强度（PBR 读）；宿主 Settings 只有视图够得着，新值经事件转写。</summary>
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

    /// <summary>色调映射曝光；写穿路径同上。</summary>
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

    /// <summary>亮度上限；写穿路径同上。</summary>
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

    /// <summary>
    /// 平行光可见。光对象从构造起就住在 VM 里，建场景前拨开关也不会丢写
    /// （旧代码 sun 为空时丢一次写，挂进场景后回到默认开）。
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

    /// <summary>画背景（Camera.IsRenderBackground）；相机归场景所有，场景缺席时按旧语义丢弃本次写。</summary>
    public bool RenderBackground
    {
        get => renderBackground;
        set
        {
            if (!SetProperty(ref renderBackground, value))
                return;

            if (scene != null)
                scene.MainCamera.IsRenderBackground = value;

            FrameRequested?.Invoke();
        }
    }

    /// <summary>
    /// 把当前选择落成 Background。Background 的 setter 会通知管线（OnBackgroundChanged）
    /// 自动作废辐照度/预滤波两张缓存，下一帧用新图重烘——所以这里不需要手动 Invalidate。
    /// </summary>
    private void ApplyEnvironment()
    {
        scene!.Background = envIndex switch
        {
            // 真实 HDRI：文件里是线性 HDR 值，烘之前不能再走一次 gamma 解码（加载器默认就是 false）。
            0 => HDRIToCubeTextureConverter.ConvertFromTexture(hdrPanorama!, effectiveFaceSize),

            // 六面天空盒已经是立方图，直接用；面边长由那六张图自己决定。
            1 => fileCube!,

            // 程序化全景图：花样的编号从 FirstProceduralIndex 起算。
            _ => HDRIToCubeTextureConverter.ConvertFromTexture(
                BuildPanorama(envIndex - FirstProceduralIndex), effectiveFaceSize),
        };
    }

    /// <summary>把相机上两张 IBL 缓存的帧缓冲清零，逼管线下一帧重烘（背景引用没换、内容变了才用得上）。</summary>
    private void InvalidateIblCaches()
    {
        var camera = scene?.MainCamera;

        if (camera == null)
            return;

        foreach (var key in new[] { IrradianceKey, PrefilterKey })
        {
            if (camera.GetPipelineGpuState<CubeRenderTarget>(key) is { } target)
                target.Invalidate();
        }
    }

    private static Node BuildBallRow()
    {
        var row = new Node { Name = "Balls" };

        // 五档金属度×粗糙度组合。引擎没有 metallicFactor/roughnessFactor 这类数值因子，
        // 只有 MetallicRoughness 这一张图：G=粗糙、B=金属，所以每档都得现做一张纯色图。
        (float Metal, float Rough)[] combos =
        [
            (0f, 0.1f),
            (0f, 0.85f),
            (0.5f, 0.35f),
            (1f, 0.05f),
            (1f, 0.7f),
        ];

        for (int i = 0; i < combos.Length; i++)
        {
            var material = new Material();

            material.SetTexture("BaseColor", Procedural.Checker(64, 3));
            material.SetTexture("MetallicRoughness", Procedural.MetalRoughSolid(combos[i].Metal, combos[i].Rough));

            var ball = new Mesh
            {
                Name = $"M{combos[i].Metal:0.##}R{combos[i].Rough:0.##}",
                Geometry = new SphereGeometry(0.85f, 44, 24),
                Material = material,
            };

            ball.Position = new Vector3((i - 2) * 2.1f, 1.1f, 0);

            row.AddChild(ball, AttachToParentRule.KeepLocal);
        }

        return row;
    }

    private static Texture BuildPanorama(int which)
    {
        const int Width = 256;
        const int Height = 128;

        var data = new float[Width * Height * 4];

        for (int y = 0; y < Height; y++)
        {
            float v = y / (float)(Height - 1);

            for (int x = 0; x < Width; x++)
            {
                float u = x / (float)(Width - 1);

                Vector3 color;

                switch (which)
                {
                    case 1:
                        color = Sunset(u, v);
                        break;
                    case 2:
                        color = TestPattern(u, v);
                        break;
                    default:
                        color = Studio(u, v);
                        break;
                }

                var i = (y * Width + x) * 4;

                data[i] = color.X;
                data[i + 1] = color.Y;
                data[i + 2] = color.Z;
                data[i + 3] = 1f;
            }
        }

        var texture = new Texture();

        texture.SetHdrData(data, Width, Height);
        texture.SetColorFormat(ColorFormat.RGBA);

        // 全景图是线性 HDR 数据，标成 gamma 空间会被再解码一次，反射会明显发白。
        texture.SetIsGammaSpace(false);

        texture.MinFilter = TextureFilterMode.Linear;
        texture.MagFilter = TextureFilterMode.Linear;
        texture.WrapS = TextureWrapMode.Repeat;
        texture.WrapT = TextureWrapMode.ClampToEdge;

        return texture;
    }

    private static Vector3 Studio(float u, float v)
    {
        // 上部天光渐变 + 两条水平柔光箱，给反射一个可读的形状。
        var sky = Vector3.Lerp(new Vector3(0.12f, 0.16f, 0.24f), new Vector3(0.55f, 0.6f, 0.7f), 1f - v);

        var band = 0f;

        if (MathF.Abs(v - 0.3f) < 0.05f)
            band += 3.5f;

        if (MathF.Abs(v - 0.55f) < 0.03f)
            band += 1.8f;

        return sky + new Vector3(band);
    }

    private static Vector3 Sunset(float u, float v)
    {
        var horizon = new Vector3(0.55f, 0.35f, 0.28f);
        var zenith = new Vector3(0.05f, 0.1f, 0.22f);

        var sky = Vector3.Lerp(zenith, horizon, MathF.Pow(1f - v, 2f));

        // 一个小而极亮的太阳盘：HDR 值远大于 1，曝光与亮度上限就是为它准备的。
        var du = MathF.Abs(u - 0.25f);
        var dv = MathF.Abs(v - 0.42f);

        if (du < 0.03f && dv < 0.05f)
            sky += new Vector3(40f, 34f, 24f);

        // 地面一侧压暗，避免整个球被当成环境光。
        if (v > 0.62f)
            sky *= 0.25f;

        return sky;
    }

    private static Vector3 TestPattern(float u, float v)
    {
        // 经线八格、纬线四格，立方图六个面的朝向与接缝一眼可辨。
        var sector = (int)(u * 8f) + (int)(v * 4f) * 8;

        var r = (sector % 2) * 0.8f + 0.15f;
        var g = ((sector >> 1) % 2) * 0.8f + 0.15f;
        var b = ((sector >> 2) % 2) * 0.8f + 0.15f;

        return new Vector3(r, g, b);
    }
}
