using Aura3D.Core.Geometries;
using Aura3D.Core.Nodes;
using Aura3D.Core.Resources;
using Aura3D.Gallery.Kit;
using Aura3D.Gallery.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using Irihi.Lingua;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Aura3D.Gallery.Demos;

/// <summary>
/// 材质页的界面状态，按 Particles 页的样板 MVVM 化。本页演示材质的三件事：
/// 通道（<c>SetTexture</c> → 同名 <c>*Texture</c> uniform）、自定义着色器覆盖
/// （按 pass 类名挂 GLSL，可只换片元）、材质参数（<c>SetParameterValue</c> → 同名 uniform）。分工：
/// <list type="bullet">
///   <item><description>文案：AXAML 直接写 <c>{Translate}</c> 标记扩展取 Strings.Keys 键值；换语言走外壳既有的整页重挂。</description></item>
///   <item><description>参数：滑杆/开关/下拉双向绑定到这里的属性，setter 写穿到对应材质——
///     通道贴图、自定义着色器的 <c>uColor</c>/<c>uStripe</c> uniform、混合模式与 alpha 裁剪。
///     初值即旧 XAML 行控件上写死的那一份（Hue 滑杆没写 Value，默认 0），材质也按同一份值
///     建出来，行控件与材质状态天然对齐，取代旧构造函数里的下拉对齐。</description></item>
///   <item><description>Readout：Lingua 1.1 的 format builder 组装 {0}..{3}——帧时与 uTime 时钟
///     逐帧变，覆盖键是常量、cutoff 只在拖动时变；视图逐帧调 <see cref="Tick"/> 重读，
///     语言切换时模板本身也会自动换。</description></item>
///   <item><description>引擎模型：程序化贴图（全页零外部资产）、六个材质（引擎一个 + 自定义五个）
///     与整排网格住在这里。全是纯 CPU 声明，不碰 GPU，构造期建全没有问题；
///     视图只负责生命周期——摆相机、加灯、把整排挂进场景、逐帧驱动。</description></item>
/// </list>
/// 视图桥只有两个事件：<see cref="FrameRequested"/>（材质/参数变化要排一次重绘，等价旧处理器
/// 末尾的 InvalidateRender）与 <see cref="ShowGridToggled"/>（地面网格挂在宿主的 Scene 上，
/// VM 不碰 DemoContext）。覆盖键写死为 LightPass，换管线就没有覆盖可言——锁管线的原因
/// 见 DemoRegistry 的登记项，不在这里重复。
/// </summary>
public sealed partial class MaterialShadersViewModel : ObservableObject
{
    /// <summary>被覆盖的 pass 类名；Blinn-Phong 主管线只有这一个着色 pass。</summary>
    private const string Pass = "LightPass";

    private static readonly LinguaKey[] BaseColorKeys =
        [Strings.Keys.MaterialShaders_MapChecker, Strings.Keys.MaterialShaders_MapSoftDot, Strings.Keys.MaterialShaders_MapGradient];

    /// <summary>
    /// 建好 VM：先按参数初值建出贴图、材质与整排网格（材质消费的就是这些同一份值），
    /// 再对齐下拉选中项、装配 Readout 模板。上下两排的摆位与旧 BuildScene 逐位一致：
    /// 上排三颗球对比「引擎着色器 / 自定义常量色 / 贴图×染色」，下排是参数驱动的程序化条纹
    /// 加同一张贴图在 Masked 与 Translucent 下的两种透明路径。
    /// </summary>
    public MaterialShadersViewModel()
    {
        // 下拉选项统一走 ComboOption：BaseColor 的 Name 是 Lingua 流绑定（换语言自动换文案），
        // 混合模式显示引擎枚举名（语言无关的引擎标识）；Value 是语言无关的稳定值，
        // 选中项按对象身份比较，不再按显示名 IndexOf 反查。
        baseColorOptionList = BaseColorKeys.Select((k, i) => Kit.ComboOption.ForKey(k, i)).ToList();
        var blendModeList = new List<BlendMode>
        {
            BlendMode.Opaque,
            BlendMode.Masked,
            BlendMode.Translucent,
        }.Select(m => Kit.ComboOption.Literal(m.ToString(), m)).ToList();

        BlendModeOptions = blendModeList;

        // 可换着用的三张 BaseColor 贴图，键即下拉选项的下标。
        swappableTextures[0] = Procedural.Checker(128, 4);
        swappableTextures[1] = Procedural.SoftDot(128);
        swappableTextures[2] = Procedural.VerticalGradient(width: 32, height: 256);

        bumpTexture = Procedural.BumpNormal(128, 5);
        dotTexture = Procedural.SoftDot(128, 1.6f);

        // 左上：引擎材质，只喂 BaseColor 通道，其余全走引擎默认。
        engineMaterial = new Material();
        engineMaterial.SetTexture("BaseColor", swappableTextures[0]);

        // 中上：顶点+片元两段全换的常量色；右上：贴图采样后乘 uColor 染色。
        solidMaterial = Shaders.Solid(Pass, ColorOf(hue));
        texturedMaterial = Shaders.Textured(Pass, swappableTextures[0], TintOf(tintStrength));

        // 左下：只覆盖片元的条纹，参数走 uColor/uStripe/uTime；初值即滑杆当前值。
        stripeMaterial = Shaders.Stripes(Pass, new Vector4(0.15f, 0.45f, 0.85f, 1f), new Vector2((float)stripeFrequencyU, (float)stripeFrequencyV));
        stripeMaterial.AlphaCutoff = (float)stripeCutoff;

        // 中下/右下：同一张点图，分别按 Masked 与 Translucent 两种透明路径画。
        maskedMaterial = Shaders.Textured(Pass, dotTexture, new Vector4(1f, 0.72f, 0.25f, 1f));
        maskedMaterial.BlendMode = BlendMode.Masked;
        maskedMaterial.AlphaCutoff = (float)alphaCutoff;

        translucentMaterial = Shaders.Textured(Pass, dotTexture, new Vector4(0.35f, 0.9f, 0.65f, 1f));
        translucentMaterial.BlendMode = BlendMode.Translucent;
        translucentMaterial.AlphaCutoff = (float)alphaCutoff;

        // 整排挂同一个父节点，自转作用在父节点上一次搞定。
        Row = new Node { Name = "Row" };

        AddSphere(-2.3f, 3.1f, 0f, engineMaterial);
        switchMesh = AddSphere(0f, 3.1f, 0f, solidMaterial);
        AddSphere(2.3f, 3.1f, 0f, texturedMaterial);
        AddSphere(-2.3f, 1.15f, 1.6f, stripeMaterial);
        AddQuad(0f, 1.15f, 1.6f, maskedMaterial);
        AddQuad(2.3f, 1.15f, 1.6f, translucentMaterial);

        // 下拉初值：BaseColor 对齐第一张（棋盘）。条纹材质的混合模式保持 Shaders.Stripes 出厂的
        // Opaque——旧页面构造函数也只拨了下拉显示（Masked），并不写穿材质，照原样不制造新行为。
        selectedBaseColor = baseColorOptionList[0];
        selectedBlendMode = blendModeList.Single(o => o.Value == BlendMode.Masked);

        // Readout 模板 {0}..{3}：帧时由 Tick 逐帧喂进来（构造期为 0，首帧后即真实值），
        // uTime 时钟只在「逐帧写 uTime」开着时前进，覆盖键是常量，cutoff 随滑杆变。
        ReadoutText = Strings.Keys.MaterialShaders_Readout.CreateFormat()
            .Arg(() => frameMs)
            .Arg(() => clock)
            .Arg(() => Pass)
            .Arg(() => alphaCutoff)
            .Build();
    }

    /// <summary>材质/参数变化后请求一帧重绘；视图把它接到宿主的 InvalidateRender 上。</summary>
    public event Action? FrameRequested;

    /// <summary>地面网格开关变化；网格在宿主的 Scene 上，只有视图够得着。</summary>
    public event Action<bool>? ShowGridToggled;

    /// <summary>整排网格的父节点（含全部六个网格）；建场景时由视图挂进场景。</summary>
    public Node Row { get; }

    /// <summary>BaseColor 下拉的选项：Name 跟随语言（Lingua 流绑定），Value 是贴图序号（对应 swappableTextures 的键）。</summary>
    public IList BaseColorOptions => baseColorOptionList;

    /// <summary>条纹材质混合模式下拉的选项：Name 是引擎枚举名（语言无关的引擎标识），Value 是枚举值。</summary>
    public IList BlendModeOptions { get; }

    /// <summary>逐帧读数。语言切换时模板换新，绑定自动收到重排后的整句。</summary>
    public LinguaFormatObservable ReadoutText { get; }

    /// <summary>
    /// 逐帧调用：记录帧时、推进整排自转与条纹时钟，结尾重读运行状态行。
    /// 时钟开着才逐帧写 <c>uTime</c>——写参数会抬高材质 Version、逼带自定义着色器的材质
    /// 重编译，这正是该开关默认关着的原因（见页首说明）。
    /// </summary>
    /// <param name="deltaTime">距上一帧的秒数。</param>
    public void Tick(double deltaTime)
    {
        frameMs = deltaTime * 1000.0;

        // 与旧 Update 一致：每帧直接赋值而非累加，spin 归零整排即停。
        if (spin > 0)
            Row.RotationDegrees = new Vector3(0f, (float)(spin * deltaTime), 0f);

        if (animateTime)
        {
            clock += (float)(deltaTime * stripeSpeed);

            stripeMaterial.SetParameterValue("uTime", clock);
        }

        ReadoutText.Refresh();
    }

    /// <summary>
    /// BaseColor 下拉的选中项；选项实例即身份，Value 直接给出贴图序号，换贴图即换
    /// 左上球（引擎材质）的 BaseColor 通道。旧处理器取 <c>litMesh?.Material ?? engineMaterial</c>
    /// ——那颗球从建出来就挂着 engineMaterial（会换材质的是中路的球），这里直接落到 engineMaterial。
    /// </summary>
    public Kit.ComboOption<int>? SelectedBaseColor
    {
        get => selectedBaseColor;
        set
        {
            if (value == null || !SetProperty(ref selectedBaseColor, value))
                return;

            engineMaterial.SetTexture("BaseColor", swappableTextures[value.Value]);

            FrameRequested?.Invoke();
        }
    }

    /// <summary>Normal 通道开关：拨开写入程序化法线贴图，拨回传 null 清掉通道。</summary>
    public bool ShowNormal
    {
        get => showNormal;
        set
        {
            if (!SetProperty(ref showNormal, value))
                return;

            engineMaterial.SetTexture("Normal", value ? bumpTexture : null);

            FrameRequested?.Invoke();
        }
    }

    /// <summary>「用自定义常量色着色器」开关：中路的球在自定义材质与引擎材质之间换挂。</summary>
    public bool UseCustomSolid
    {
        get => useCustomSolid;
        set
        {
            if (!SetProperty(ref useCustomSolid, value))
                return;

            switchMesh.Material = value ? solidMaterial : engineMaterial;

            FrameRequested?.Invoke();
        }
    }

    /// <summary>常量色色相（度）；只改 <c>uColor</c> uniform，着色器源不动。</summary>
    public double Hue
    {
        get => hue;
        set
        {
            if (!SetProperty(ref hue, value))
                return;

            solidMaterial.SetParameterValue("uColor", ColorOf(value));

            FrameRequested?.Invoke();
        }
    }

    /// <summary>贴图染色强度；乘进右上球自定义材质的 <c>uColor</c>。</summary>
    public double TintStrength
    {
        get => tintStrength;
        set
        {
            if (!SetProperty(ref tintStrength, value))
                return;

            texturedMaterial.SetParameterValue("uColor", TintOf(value));

            FrameRequested?.Invoke();
        }
    }

    /// <summary>条纹流动速度；只作为 Tick 里时钟的步长，不单独写 uniform。</summary>
    public double StripeSpeed
    {
        get => stripeSpeed;
        set => SetProperty(ref stripeSpeed, value);
    }

    /// <summary>条纹 U 向频率；与 V 一起合成 <c>uStripe</c>（vec2）。</summary>
    public double StripeFrequencyU
    {
        get => stripeFrequencyU;
        set
        {
            if (SetProperty(ref stripeFrequencyU, value))
                ApplyStripe();
        }
    }

    /// <summary>条纹 V 向频率；与 U 一起合成 <c>uStripe</c>（vec2）。</summary>
    public double StripeFrequencyV
    {
        get => stripeFrequencyV;
        set
        {
            if (SetProperty(ref stripeFrequencyV, value))
                ApplyStripe();
        }
    }

    /// <summary>条纹材质的混合模式；选项的 Value 即枚举，引擎以 #define 传进同一段 GLSL 走不同分支。</summary>
    public Kit.ComboOption<BlendMode>? SelectedBlendMode
    {
        get => selectedBlendMode;
        set
        {
            if (value == null || !SetProperty(ref selectedBlendMode, value))
                return;

            stripeMaterial.BlendMode = value.Value;

            FrameRequested?.Invoke();
        }
    }

    /// <summary>条纹材质的 alpha 裁剪（「材质参数」组里的 alphaCutoff 滑杆）。</summary>
    public double StripeCutoff
    {
        get => stripeCutoff;
        set
        {
            if (!SetProperty(ref stripeCutoff, value))
                return;

            stripeMaterial.AlphaCutoff = (float)value;

            FrameRequested?.Invoke();
        }
    }

    /// <summary>「逐帧写 uTime」开关；开着才会在 Tick 里推时钟（重编译代价见页首说明）。</summary>
    public bool AnimateTime
    {
        get => animateTime;
        set => SetProperty(ref animateTime, value);
    }

    /// <summary>透明路径的 alpha 裁剪：Masked 与 Translucent 两个材质一起改。</summary>
    public double AlphaCutoff
    {
        get => alphaCutoff;
        set
        {
            if (!SetProperty(ref alphaCutoff, value))
                return;

            maskedMaterial.AlphaCutoff = (float)value;
            translucentMaterial.AlphaCutoff = (float)value;

            FrameRequested?.Invoke();
        }
    }

    /// <summary>Translucent 面片的双面开关。</summary>
    public bool DoubleSided
    {
        get => doubleSided;
        set
        {
            if (!SetProperty(ref doubleSided, value))
                return;

            translucentMaterial.DoubleSided = value;

            FrameRequested?.Invoke();
        }
    }

    /// <summary>整排自转速度（度/秒）；只在 Tick 里被消费。</summary>
    public double Spin
    {
        get => spin;
        set => SetProperty(ref spin, value);
    }

    /// <summary>
    /// 地面网格开关。网格挂在宿主的 Scene 上、VM 不碰 DemoContext：
    /// 先桥接写 Scene 再排重绘，顺序同旧处理器（Scene 尚未建出的窗口期由视图侧兜住）。
    /// </summary>
    public bool ShowGrid
    {
        get => showGrid;
        set
        {
            if (!SetProperty(ref showGrid, value))
                return;

            ShowGridToggled?.Invoke(value);
            FrameRequested?.Invoke();
        }
    }

    /// <summary>把两个方向的目标频率合成 <c>uStripe</c> 写进条纹材质；初值在建材质时已给过一次。</summary>
    private void ApplyStripe()
    {
        stripeMaterial.SetParameterValue(
            "uStripe",
            new Vector2((float)stripeFrequencyU, (float)stripeFrequencyV));

        FrameRequested?.Invoke();
    }

    /// <summary>色相环取色，饱和度与亮度固定为满值。</summary>
    private static Vector4 ColorOf(double hueDegrees)
    {
        var x = (float)(hueDegrees / 360.0) % 1f * 6f;

        static float Channel(float value) => MathF.Min(1f, MathF.Max(0f, value));

        return new Vector4(
            Channel(MathF.Abs(x - 3f) - 1f),
            Channel(2f - MathF.Abs(x - 2f)),
            Channel(2f - MathF.Abs(x - 4f)),
            1f);
    }

    private static Vector4 TintOf(double strength)
    {
        var k = (float)strength;

        return new Vector4(1f - k * 0.75f, 1f - k * 0.1f, 1f - k * 0.55f, 1f);
    }

    private Mesh AddSphere(float x, float y, float z, Material material)
    {
        var mesh = new Mesh
        {
            Name = $"sphere-{x}-{y}",
            Geometry = new SphereGeometry(0.82f, 40, 24),
            Material = material,
        };

        mesh.Position = new Vector3(x, y, z);

        Row.AddChild(mesh, AttachToParentRule.KeepLocal);

        return mesh;
    }

    private Mesh AddQuad(float x, float y, float z, Material material)
    {
        var mesh = new Mesh
        {
            Name = $"quad-{x}-{y}",
            Geometry = new PlaneGeometry(1.7f, 1.7f),
            Material = material,
        };

        mesh.Position = new Vector3(x, y, z);

        Row.AddChild(mesh, AttachToParentRule.KeepLocal);

        return mesh;
    }

    private readonly Dictionary<int, Texture> swappableTextures = new();

    private readonly Texture bumpTexture;
    private readonly Texture dotTexture;

    private readonly Material engineMaterial;
    private readonly Material solidMaterial;
    private readonly Material texturedMaterial;
    private readonly Material stripeMaterial;
    private readonly Material maskedMaterial;
    private readonly Material translucentMaterial;

    // 唯一需要事后换材质的网格（中路的球）；其余网格建完就不再单独引用。
    private readonly Mesh switchMesh;

    private List<Kit.ComboOption<int>> baseColorOptionList = [];
    private Kit.ComboOption<int>? selectedBaseColor;
    private Kit.ComboOption<BlendMode>? selectedBlendMode;

    // 参数初值即旧 .axaml 行控件上写死的那一份（Hue 滑杆没写 Value，默认 0）。
    private double spin = 14;
    private double hue;
    private double tintStrength = 0.55;
    private double stripeSpeed = 0.9;
    private double stripeFrequencyU = 6;
    private double stripeFrequencyV = 2;
    private double stripeCutoff = 0.5;
    private double alphaCutoff = 0.35;
    private bool useCustomSolid = true;
    private bool showNormal;
    private bool animateTime;
    private bool doubleSided;
    private bool showGrid;

    // uTime 时钟（秒）与最近一帧的毫秒帧时，前者只在 AnimateTime 开着时前进，后者由 Tick 喂。
    private float clock;
    private double frameMs;
}
