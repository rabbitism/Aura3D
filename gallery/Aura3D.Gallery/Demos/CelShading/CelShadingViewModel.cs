using Aura3D.Core.Geometries;
using Aura3D.Core.Nodes;
using Aura3D.Core.Resources;
using Aura3D.Gallery.Kit;
using Aura3D.Gallery.Localization;
using Aura3D.Pipeline.CelShading;
using CommunityToolkit.Mvvm.ComponentModel;
using Irihi.Lingua;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Color = System.Drawing.Color;
using ModelNode = Aura3D.Core.Nodes.Model;

namespace Aura3D.Gallery.Demos;

/// <summary>
/// 卡通着色页的界面状态，按 Particles / MaterialShaders 两页的样板 MVVM 化。本页演示
/// <c>CelShadingPipeline</c> 专属的材质扩展：pass 只吃六个命名通道与一组下划线开头的
/// float/Vector4，且会先把每个 uniform 预载成参考材质（历史示例角色 Nilou 的卡渲参数），
/// 所以缺参数、缺贴图的卡通材质也能正常渲染。分工：
/// <list type="bullet">
///   <item><description>文案：AXAML 直接写 <c>{Translate}</c> 标记扩展取 Strings.Keys 键值；换语言走外壳既有的整页重挂。</description></item>
///   <item><description>参数：开关/滑杆双向绑定到这里的属性，setter 写穿到 <see cref="plugged"/>/
///     <see cref="numbers"/> 两个字典与三档染色 Vector4，再由 <see cref="ApplyAll"/> 落到
///     身体/脸两个卡通材质；初值即旧 XAML 行控件上写死的那一份。</description></item>
///   <item><description>Readout：Lingua 1.1 的 format builder 组装 {0}..{8}——已插通道名、
///     参数个数、三档亮度、脸部分支与描边绑定状态；视图逐帧调 <see cref="Tick"/> 重读，
///     语言切换时模板本身也会自动换。</description></item>
///   <item><description>引擎模型：身体/脸两个卡通材质、程序化贴图（ILM/ramp/SDF 现做）、
///     自转的定向光、棋盘地面与角色模型住在这里，全是纯 CPU 声明，构造期建全没有问题；
///     视图只负责生命周期——取角色资产、建场景、把节点挂进场景、逐帧驱动。</description></item>
/// </list>
/// 视图桥有三条：<see cref="FrameRequested"/>（材质/参数变化要排一次重绘，等价旧处理器
/// 末尾的 InvalidateRender）、<see cref="SettingsAmbientChanged"/>（环境光强度写在宿主的
/// Settings 上，VM 不碰 DemoContext）与 <see cref="FindOutlinePass"/> 定位器（描边 pass
/// 实例只能按类型从宿主管线里翻出来，同样只有视图够得着）。材质参数名与通道名只有
/// CelLightPass 认，换管线就一个都不生效——锁管线的原因见 DemoRegistry 的登记项，
/// 不在这里重复。
/// </summary>
public sealed partial class CelShadingViewModel : ObservableObject
{
    /// <summary>通道表：Name 是材质认的通道名（身份，不参与本地化），Label 只作展示，与检视面板的开关标签同一批键。</summary>
    private static readonly (string Name, LinguaKey Label)[] Channels =
    [
        ("BaseColor", Strings.Keys.CelShading_BaseColor),
        ("Normal", Strings.Keys.CelShading_Normal),
        ("ILM", Strings.Keys.CelShading_Ilm),
        ("ShadowRamp", Strings.Keys.CelShading_ShadowRamp),
        ("SDF", Strings.Keys.CelShading_Sdf),
        ("SpecularRamp", Strings.Keys.CelShading_SpecularRamp),
    ];

    /// <summary>
    /// 建好 VM：先建出定向光、地面与角色侧的引擎对象，再把滑杆/开关字段对齐到字典初值
    /// （双向绑定初始化时推给行控件），最后装配 Readout 模板。身体/脸两个材质在此刻还是
    /// 空的——首套参数由视图在 BuildScene 里调 <see cref="ApplyAll"/> 落上，与旧页一致。
    /// </summary>
    public CelShadingViewModel()
    {
        // 定向光绕 Y 轴自转：明暗交界线扫过角色与球体，卡渲的分档与色调映射一眼可见。
        // 初始姿态 (-30, -25, 0) 与 sunYaw 的 -25 对齐，之后每帧在 Tick 里转。
        Sun = new DirectionalLight { LightColor = Color.White };

        Sun.RotationDegrees = new Vector3(-30f, -25f, 0);

        Ground = new Mesh
        {
            Name = "Ground",
            Geometry = new PlaneGeometry(30f, 30f),
            Material = new Material(),
        };

        Ground.Material.SetTexture("BaseColor", Procedural.Checker(256, 14));

        // 参数初值即旧 XAML 行控件上写死的那一份。字典（plugged/numbers）是 ApplyAll 与
        // 读数的权威源，行控件字段在这里对齐一次，之后 setter 永远两边同写。
        baseColorPlugged = plugged["BaseColor"];
        normalPlugged = plugged["Normal"];
        ilmPlugged = plugged["ILM"];
        shadowRampPlugged = plugged["ShadowRamp"];
        sdfPlugged = plugged["SDF"];
        specularRampPlugged = plugged["SpecularRamp"];

        greyFac = numbers["_GreyFac"];
        darkFac = numbers["_DarkFac"];
        brightFac = numbers["_BrightFac"];
        brightAreaShadowFac = numbers["_BrightAreaShadowFac"];
        faceShadowOffset = numbers["_FaceShadowOffset"];
        faceShadowSoftness = numbers["_FaceShadowTransitionSoftness"];
        rampIndex0 = numbers["_RampIndex0"];
        rampIndex1 = numbers["_RampIndex1"];
        rampIndex2 = numbers["_RampIndex2"];
        rampIndex3 = numbers["_RampIndex3"];
        rampIndex4 = numbers["_RampIndex4"];

        // 三档染色的滑杆量程是 Vector4 的 X 分量（旧 XAML 的 1.15/0.55/0.45 正是三个初值的 X）。
        lightTint = tint.X;
        darkTint = dark.X;
        coolDarkTint = coolDark.X;

        // Readout 模板 {0}..{8}：三档亮度与描边环境光随滑杆变，通道列表随开关变，
        // 脸部分支文案与描边绑定状态是本地化串（.T() 现取），Tags 数恒为空（见 faceMesh）。
        ReadoutText = Strings.Keys.CelShading_Readout.CreateFormat()
            .Arg(() => string.Join("/", Channels.Where(c => plugged[c.Name]).Select(c => c.Name)))
            .Arg(() => numbers.Count + 4)
            .Arg(() => tint.X)
            .Arg(() => dark.X)
            .Arg(() => coolDark.X)
            .Arg(() => faceRender ? "FACE_RENDER" : Strings.Keys.CelShading_FaceBodyBranch.T())
            .Arg(() => faceMesh?.Tags.Count)
            .Arg(() => outlinePass == null ? Strings.Keys.CelShading_OutlineNotFound.T() : Strings.Keys.CelShading_OutlineBound.T())
            .Arg(() => outlineAmbient)
            .Build();
    }

    /// <summary>材质/参数变化后请求一帧重绘；视图把它接到宿主的 InvalidateRender 上。</summary>
    public event Action? FrameRequested;

    /// <summary>引擎环境光强度变化；写在宿主的 Settings 上，VM 不碰 DemoContext。</summary>
    public event Action<float>? SettingsAmbientChanged;

    /// <summary>绕 Y 自转的定向光；建场景时由视图挂进场景，每帧在 <see cref="Tick"/> 里转。</summary>
    public DirectionalLight Sun { get; }

    /// <summary>30×30 棋盘地面；建场景时由视图挂进场景。</summary>
    public Mesh Ground { get; }

    /// <summary>
    /// 卡渲角色 glb：它的材质没设卡通参数也照常渲染——pass 有参考材质兜底，正好当
    /// 「真实模型」对照。资产阶段由视图喂进来，null 表示还没取到。
    /// </summary>
    public ModelNode? Character => character;

    /// <summary>逐帧读数。语言切换时模板换新，绑定自动收到重排后的整句。</summary>
    public LinguaFormatObservable ReadoutText { get; }

    /// <summary>
    /// 描边 pass 的定位器，由视图注入：实例只能从宿主 RenderPipeline.EveryCameraRenderPasses
    /// 里按类型翻出来（OutlinePass 唯一的公开途径），场景没建好时返回 null，等价旧
    /// ApplyOutline 里 <c>Context.Scene?</c> 的判空。每次应用都重新定位，上下文重建换出的
    /// 新 pass 也能被后续参数变化重新接上。
    /// </summary>
    public Func<OutlinePass?>? FindOutlinePass { get; set; }

    /// <summary>
    /// 资产阶段取到的角色模型：命名与放大（旧 BuildScene 里的 Scale=3）在这里一并落上，
    /// 视图建场景时只管挂进场景。
    /// </summary>
    public void SetCharacter(ModelNode model)
    {
        model.Name = "CelCharacter";
        model.Scale = new Vector3(3f);

        character = model;
    }

    /// <summary>
    /// 引擎环境光强度的初值对齐。旧 ctor 补写的是行控件（读的是
    /// <c>Context.Settings.AmbientIntensity</c>，XAML 里写不出这个常量）；VM 够不着
    /// DemoContext，由视图把引擎当前值喂进来。走属性而非直写字段，让可能已激活的绑定
    /// 也收到；此刻事件还没有订阅者，不会写回引擎。
    /// </summary>
    public void SetEngineAmbient(float value) => SettingsAmbient = value;

    /// <summary>离开页面时由视图调用：描边那个 pass 属于本页创建过的管线，不留悬挂引用。</summary>
    public void ClearOutlinePass() => outlinePass = null;

    /// <summary>
    /// 逐帧调用：推进定向光自转并重读 Readout。旧 Update 里对 sun 的判空不再需要——
    /// 光随 VM 构造期就存在，建场景前后多转几度没有可见差别。
    /// </summary>
    /// <param name="deltaTime">距上一帧的秒数。</param>
    public void Tick(double deltaTime)
    {
        sunYaw += (float)sunYawSpeed * (float)deltaTime;

        Sun.RotationDegrees = new Vector3(-30f, sunYaw, 0f);

        ReadoutText.Refresh();
    }

    /// <summary>BaseColor 通道开关；拨开给身体材质喂程序化棋盘底色，拨回传 null 清掉通道。</summary>
    public bool BaseColorPlugged
    {
        get => baseColorPlugged;
        set
        {
            if (!SetProperty(ref baseColorPlugged, value))
                return;

            plugged["BaseColor"] = value;
            ApplyAll();
        }
    }

    /// <summary>Normal 通道开关；程序化法线贴图只喂给身体材质。</summary>
    public bool NormalPlugged
    {
        get => normalPlugged;
        set
        {
            if (!SetProperty(ref normalPlugged, value))
                return;

            plugged["Normal"] = value;
            ApplyAll();
        }
    }

    /// <summary>ILM 光照遮罩开关；身体与脸共用同一张程序化 ILM。</summary>
    public bool IlmPlugged
    {
        get => ilmPlugged;
        set
        {
            if (!SetProperty(ref ilmPlugged, value))
                return;

            plugged["ILM"] = value;
            ApplyAll();
        }
    }

    /// <summary>ShadowRamp 开关；身体与脸共用同一张五行渐变图。</summary>
    public bool ShadowRampPlugged
    {
        get => shadowRampPlugged;
        set
        {
            if (!SetProperty(ref shadowRampPlugged, value))
                return;

            plugged["ShadowRamp"] = value;
            ApplyAll();
        }
    }

    /// <summary>SDF 脸部距离场开关；只喂给脸材质。</summary>
    public bool SdfPlugged
    {
        get => sdfPlugged;
        set
        {
            if (!SetProperty(ref sdfPlugged, value))
                return;

            plugged["SDF"] = value;
            ApplyAll();
        }
    }

    /// <summary>SpecularRamp 开关；绑了不采，开着只验证通道名合法。</summary>
    public bool SpecularRampPlugged
    {
        get => specularRampPlugged;
        set
        {
            if (!SetProperty(ref specularRampPlugged, value))
                return;

            plugged["SpecularRamp"] = value;
            ApplyAll();
        }
    }

    /// <summary>_GreyFac：smoothstep 上限（过渡宽度）；写穿到身体与脸两个材质。</summary>
    public double GreyFac
    {
        get => greyFac;
        set
        {
            if (!SetProperty(ref greyFac, value))
                return;

            numbers["_GreyFac"] = (float)value;
            ApplyAll();
        }
    }

    /// <summary>_DarkFac：NDotL 偏移（把暗部往上推）。</summary>
    public double DarkFac
    {
        get => darkFac;
        set
        {
            if (!SetProperty(ref darkFac, value))
                return;

            numbers["_DarkFac"] = (float)value;
            ApplyAll();
        }
    }

    /// <summary>_BrightFac：亮部阈值（超过就用原始 halfLambert）。</summary>
    public double BrightFac
    {
        get => brightFac;
        set
        {
            if (!SetProperty(ref brightFac, value))
                return;

            numbers["_BrightFac"] = (float)value;
            ApplyAll();
        }
    }

    /// <summary>_BrightAreaShadowFac：亮面权重，暗色 ↔ 亮色。</summary>
    public double BrightAreaShadowFac
    {
        get => brightAreaShadowFac;
        set
        {
            if (!SetProperty(ref brightAreaShadowFac, value))
                return;

            numbers["_BrightAreaShadowFac"] = (float)value;
            ApplyAll();
        }
    }

    /// <summary>_RampIndex0：第 0 材质分区采样到的 ramp 行。</summary>
    public double RampIndex0
    {
        get => rampIndex0;
        set
        {
            if (!SetProperty(ref rampIndex0, value))
                return;

            numbers["_RampIndex0"] = (float)value;
            ApplyAll();
        }
    }

    /// <summary>_RampIndex1：第 1 材质分区采样到的 ramp 行。</summary>
    public double RampIndex1
    {
        get => rampIndex1;
        set
        {
            if (!SetProperty(ref rampIndex1, value))
                return;

            numbers["_RampIndex1"] = (float)value;
            ApplyAll();
        }
    }

    /// <summary>_RampIndex2：第 2 材质分区采样到的 ramp 行。</summary>
    public double RampIndex2
    {
        get => rampIndex2;
        set
        {
            if (!SetProperty(ref rampIndex2, value))
                return;

            numbers["_RampIndex2"] = (float)value;
            ApplyAll();
        }
    }

    /// <summary>_RampIndex3：第 3 材质分区采样到的 ramp 行。</summary>
    public double RampIndex3
    {
        get => rampIndex3;
        set
        {
            if (!SetProperty(ref rampIndex3, value))
                return;

            numbers["_RampIndex3"] = (float)value;
            ApplyAll();
        }
    }

    /// <summary>_RampIndex4：第 4 材质分区采样到的 ramp 行。</summary>
    public double RampIndex4
    {
        get => rampIndex4;
        set
        {
            if (!SetProperty(ref rampIndex4, value))
                return;

            numbers["_RampIndex4"] = (float)value;
            ApplyAll();
        }
    }

    /// <summary>亮部染色亮度；三通道按固定比例展开成 _LightAreaColorTint（Vector4）。</summary>
    public double LightAreaTint
    {
        get => lightTint;
        set
        {
            if (!SetProperty(ref lightTint, value))
                return;

            var k = (float)value;

            tint = new Vector4(k, k * 0.96f, k * 0.9f, 1);
            ApplyAll();
        }
    }

    /// <summary>暖色暗部亮度；展开成 _DarkShadowColor。冷色档恒生效（见 CoolShadowNoteLabel），
    /// 这档实际被 mix(…, 1) 丢掉，拖到 0 也不会变黑。</summary>
    public double DarkShadowTint
    {
        get => darkTint;
        set
        {
            if (!SetProperty(ref darkTint, value))
                return;

            var k = (float)value;

            dark = new Vector4(k * 0.72f, k * 0.55f, k * 0.78f, 1);
            ApplyAll();
        }
    }

    /// <summary>冷色暗部亮度；展开成 _CoolDarkShadowColor，身体和脸真正用到的暗色。</summary>
    public double CoolShadowTint
    {
        get => coolDarkTint;
        set
        {
            if (!SetProperty(ref coolDarkTint, value))
                return;

            var k = (float)value;

            coolDark = new Vector4(k * 0.5f, k * 0.6f, k, 1);
            ApplyAll();
        }
    }

    /// <summary>_FaceShadowOffset：SDF 受光偏移；写穿字典随 ApplyAll 落到两个材质。</summary>
    public double FaceShadowOffset
    {
        get => faceShadowOffset;
        set
        {
            if (!SetProperty(ref faceShadowOffset, value))
                return;

            numbers["_FaceShadowOffset"] = (float)value;
            ApplyAll();
        }
    }

    /// <summary>_FaceShadowTransitionSoftness：SDF 过渡软硬。</summary>
    public double FaceShadowSoftness
    {
        get => faceShadowSoftness;
        set
        {
            if (!SetProperty(ref faceShadowSoftness, value))
                return;

            numbers["_FaceShadowTransitionSoftness"] = (float)value;
            ApplyAll();
        }
    }

    /// <summary>RenderType=1 并打 face 标签；关掉走身体分支。脸网格当前恒为 null
    /// （见 <c>faceMesh</c>），两条网格路径实际都不会执行，等价旧页。</summary>
    public bool FaceRender
    {
        get => faceRender;
        set
        {
            if (SetProperty(ref faceRender, value))
                ApplyAll();
        }
    }

    /// <summary>定向光偏航速度（度/秒）；只在 Tick 里被消费，改它不单独排重绘
    /// （渲染按旧路径逐帧请求）。</summary>
    public double SunYawSpeed
    {
        get => sunYawSpeed;
        set => SetProperty(ref sunYawSpeed, value);
    }

    /// <summary>描边 pass 的环境光强度；写完立刻找 pass 应用并排一次重绘。</summary>
    public double OutlineAmbient
    {
        get => outlineAmbient;
        set
        {
            if (!SetProperty(ref outlineAmbient, value))
                return;

            ApplyOutline();

            FrameRequested?.Invoke();
        }
    }

    /// <summary>引擎全局环境光强度；经事件桥写到宿主的 Settings 上。</summary>
    public double SettingsAmbient
    {
        get => settingsAmbient;
        set
        {
            if (!SetProperty(ref settingsAmbient, value))
                return;

            SettingsAmbientChanged?.Invoke((float)value);

            FrameRequested?.Invoke();
        }
    }

    /// <summary>
    /// 把通道、数字参数与三档染色整套落到身体/脸两个材质上，并顺带应用描边环境光。
    /// 首次调用在视图 BuildScene 里（等价旧 BuildScene 末尾的那次），之后每个开关/滑杆
    /// 变化都会重放。旧版结尾的 Report() 由 Tick 逐帧重读覆盖，InvalidateRender 经
    /// FrameRequested 桥出。
    /// </summary>
    public void ApplyAll()
    {
        body.SetTexture("BaseColor", plugged["BaseColor"] ? Procedural.Checker(64, 2) : null);
        body.SetTexture("Normal", plugged["Normal"] ? Procedural.BumpNormal(256, 6, 1.8f) : null);
        body.SetTexture("ILM", plugged["ILM"] ? BuildIlm() : null);
        body.SetTexture("ShadowRamp", plugged["ShadowRamp"] ? BuildRamp() : null);
        body.SetTexture("SpecularRamp", plugged["SpecularRamp"] ? BuildRamp() : null);

        body.RemoveParameterValue("RenderType");

        foreach (var (key, value) in numbers)
            body.SetParameterValue(key, value);

        body.SetParameterValue("_LightAreaColorTint", tint);
        body.SetParameterValue("_DarkShadowColor", dark);
        body.SetParameterValue("_CoolDarkShadowColor", coolDark);

        // 脸：SDF + RenderType=1 + Tags 里带 face，三个条件同时满足才进 FACE_RENDER 分支。
        face.SetTexture("SDF", plugged["SDF"] ? BuildSdf() : null);
        face.SetTexture("BaseColor", Procedural.SoftDot(128, 1.1f));
        face.SetTexture("ILM", plugged["ILM"] ? BuildIlm() : null);
        face.SetTexture("ShadowRamp", plugged["ShadowRamp"] ? BuildRamp() : null);

        foreach (var (key, value) in numbers)
            face.SetParameterValue(key, value);

        face.SetParameterValue("_LightAreaColorTint", tint);
        face.SetParameterValue("_DarkShadowColor", dark);
        face.SetParameterValue("_CoolDarkShadowColor", coolDark);

        if (faceRender)
        {
            face.SetParameterValue("RenderType", 1);

            if (faceMesh != null)
                faceMesh.Tags.Add("face");
        }
        else if (faceMesh != null)
        {
            face.RemoveParameterValue("RenderType");

            faceMesh.Tags.Remove("face");
        }

        ApplyOutline();

        FrameRequested?.Invoke();
    }

    /// <summary>按定位器重找描边 pass 并写环境光；找不到（场景未建/非卡通管线）保持沉默。</summary>
    private void ApplyOutline()
    {
        var pass = FindOutlinePass?.Invoke();

        if (pass == null)
            return;

        outlinePass = pass;
        pass.AmbientIntensity = (float)outlineAmbient;
    }

    /// <summary>
    /// ILM：R=是否受光（0 的部分保持底色），G=AO（决定阴影强度），B=未用，A=材质分区（选 ramp 行）。
    /// </summary>
    private static Texture BuildIlm()
    {
        const int Size = 64;

        var data = new byte[Size * Size * 4];

        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                float u = x / (float)Size;
                float v = y / (float)Size;

                // 中间一条不受光的带（模拟眼睛/装饰），AO 沿竖向渐变，A 按 u 分五档。
                byte mask = MathF.Abs(v - 0.5f) < 0.12f ? (byte)0 : (byte)255;
                byte ao = (byte)(255 - (int)(MathF.Abs(v - 0.5f) * 2f * 180f));
                byte region = (byte)(MathF.Min(4, u * 5f) / 4f * 255f);

                var i = (y * Size + x) * 4;

                data[i] = mask;
                data[i + 1] = ao;
                data[i + 2] = 255;
                data[i + 3] = region;
            }
        }

        return Rgba(data, Size);
    }

    /// <summary>
    /// 阴影渐变图：五行，每行从暗到亮，行序对应 ILM 的 A 通道选到的 ramp 行。
    /// </summary>
    private static Texture BuildRamp()
    {
        const int Width = 128;
        const int Rows = 5;

        var data = new byte[Width * Rows * 4];

        for (int row = 0; row < Rows; row++)
        {
            // 每行一个色相，方便看出五个分区各自挪到了哪一行。
            float hue = row / (float)Rows;

            for (int x = 0; x < Width; x++)
            {
                float t = x / (float)(Width - 1);

                var rgb = FromHue(hue);
                var lifted = MathF.Min(1f, 0.25f + t);

                var i = (row * Width + x) * 4;

                data[i] = (byte)(rgb.X * lifted * 255f);
                data[i + 1] = (byte)(rgb.Y * lifted * 255f);
                data[i + 2] = (byte)(rgb.Z * lifted * 255f);
                data[i + 3] = 255;
            }
        }

        return Rgba(data, Width, Rows);
    }

    /// <summary>
    /// 一张最简单的圆形距离场，用来让脸走 SDF 分支：中心受光、边缘阴影。
    /// </summary>
    private static Texture BuildSdf()
    {
        const int Size = 64;

        var data = new byte[Size * Size * 4];

        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                float u = x / (float)(Size - 1) * 2f - 1f;
                float v = y / (float)(Size - 1) * 2f - 1f;

                var d = MathF.Sqrt(u * u + v * v);

                // 距离场值：越靠外越小（越暗）。
                var sdf = MathF.Min(1f, MathF.Max(0f, 1f - d * 0.9f));

                var i = (y * Size + x) * 4;

                data[i] = (byte)(sdf * 255f);
                data[i + 1] = (byte)(sdf * 255f);
                data[i + 2] = (byte)(sdf * 255f);
                data[i + 3] = 255;
            }
        }

        return Rgba(data, Size);
    }

    private static Vector3 FromHue(float hue)
    {
        var h = hue * 6f;

        float Channel(float offset)
        {
            var x = MathF.Abs(((h + offset) % 6f)) - 3f;

            return MathF.Min(1f, MathF.Max(0f, 1f - MathF.Abs(x) * 0.5f));
        }

        return new Vector3(Channel(0f), Channel(2f), Channel(4f));
    }

    private static Texture Rgba(byte[] data, int width, int? height = null)
    {
        var texture = new Texture();

        texture.SetLdrData(data, (uint)width, (uint)(height ?? width));
        texture.SetColorFormat(ColorFormat.RGBA);

        // 遮罩与 ramp 都是按值取的，走 gamma 解码会把分档阈值挪掉。
        texture.SetIsGammaSpace(false);

        texture.MinFilter = TextureFilterMode.Linear;
        texture.MagFilter = TextureFilterMode.Linear;
        texture.WrapS = TextureWrapMode.ClampToEdge;
        texture.WrapT = TextureWrapMode.ClampToEdge;

        return texture;
    }

    private readonly Dictionary<string, bool> plugged = new()
    {
        ["BaseColor"] = true,
        ["Normal"] = false,
        ["ILM"] = true,
        ["ShadowRamp"] = true,
        ["SDF"] = true,
        ["SpecularRamp"] = false,
    };

    private readonly Dictionary<string, float> numbers = new()
    {
        ["_GreyFac"] = 0.8f,
        ["_DarkFac"] = 0.15f,
        ["_BrightFac"] = 0.72f,
        ["_BrightAreaShadowFac"] = 0.85f,
        ["_FaceShadowOffset"] = 0.1f,
        ["_FaceShadowTransitionSoftness"] = 0.15f,
        ["_RampIndex0"] = 0f,
        ["_RampIndex1"] = 1f,
        ["_RampIndex2"] = 2f,
        ["_RampIndex3"] = 3f,
        ["_RampIndex4"] = 4f,
    };

    // 三档染色的初值是旧页手抄的字面值，与滑杆公式并不严格相等
    // （如 dark 的 0.42 ≠ 0.55×0.72），首次拖动前显示的就是这批值，照原样保留。
    private Vector4 tint = new(1.15f, 1.1f, 1f, 1);
    private Vector4 dark = new(0.55f, 0.42f, 0.6f, 1);
    private Vector4 coolDark = new(0.45f, 0.55f, 0.85f, 1);

    private readonly Material body = new();
    private readonly Material face = new();

    // 旧页遗留的既有缺陷，按逐行等价迁移原样保留：faceMesh 从未被赋值，脸网格的
    // face 标签因此从未打上、读数里 Tags 恒为空。用「可空类型 + 显式 null 初值」把
    // 「恒为 null」表达成合法状态，消掉旧 code-behind 的 CS0649 警告。
    private readonly Mesh? faceMesh = null;

    private OutlinePass? outlinePass;
    private ModelNode? character;

    // 定向光当前的偏航角（度）；与 Sun 的初始 RotationDegrees.Y 对齐。
    private float sunYaw = -25f;

    private bool baseColorPlugged;
    private bool normalPlugged;
    private bool ilmPlugged;
    private bool shadowRampPlugged;
    private bool sdfPlugged;
    private bool specularRampPlugged;

    private double greyFac;
    private double darkFac;
    private double brightFac;
    private double brightAreaShadowFac;
    private double rampIndex0;
    private double rampIndex1;
    private double rampIndex2;
    private double rampIndex3;
    private double rampIndex4;
    private double lightTint;
    private double darkTint;
    private double coolDarkTint;
    private double faceShadowOffset;
    private double faceShadowSoftness;

    private bool faceRender = true;
    private double sunYawSpeed = 20;
    private double outlineAmbient = 0.1;
    private double settingsAmbient;
}
