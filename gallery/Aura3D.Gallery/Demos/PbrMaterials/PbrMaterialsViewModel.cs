using Aura3D.Core.Geometries;
using Aura3D.Core.Nodes;
using Aura3D.Core.Resources;
using Aura3D.Gallery.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Aura3D.Gallery.Demos;

/// <summary>
/// PBR 材质页的界面状态，MVVM 化（模式照抄 ParticlesViewModel）。分工：
/// <list type="bullet">
///   <item><description>文案：每个标签一个 Lingua observable，XAML 用 <c>{Binding XxxLabel^}</c> 订阅，
///     换语言时由绑定自动收到新值。本页没有可调参数，面板就是一段说明加一行读数。</description></item>
///   <item><description>读数「从左到右」：材质名是资产名而非文案键，不随语言换，
///     所以按普通 string 属性走，三颗球建好后一次性排出来。</description></item>
///   <item><description>引擎模型：三颗球（含每套三张贴图到 Material 的接线与排布）住在这里，
///     视图只负责生命周期——资产阶段按 <see cref="TextureKeys"/> 取图喂 <see cref="SetTextures"/>、
///     建场景后把球挂进场景、逐帧自转。</description></item>
/// </list>
/// 三档选材一眼可分：锈蚀铁皮（锈是电介质、露铁处才有金属度，arm 的 B 通道斑驳）、
/// 混砌砖墙（法线主导的粗糙介质）、镀锌钢板（metal≈0.9、粗糙度≈0.13 的光滑裸金属，IBL 下有锐利的环境反射）。
/// 选材时用 arm 图的通道均值把关：Poly Haven 名字带 metal 的多是涂漆/锈蚀（B 通道≈0），进引擎就是非金属——
/// 之前四颗球看起来一个样就是这么来的。每套三张贴图：diff → BaseColor，nor_gl（OpenGL 约定）→ Normal，
/// arm（R=AO/G=粗糙/B=金属）→ MetallicRoughness——后者不用拆通道，glTF 的打包约定跟引擎着色器读法
/// （G=roughness、B=metallic）一字不差。
/// 本页没有会改渲染结果的参数，VM 不需要 FrameRequested 一类桥事件。
/// </summary>
public sealed partial class PbrMaterialsViewModel : ObservableObject
{
    // 从左到右的排布顺序，也是面板读出行的顺序。
    private static readonly (string Name, string BaseKey, string NormalKey, string ArmKey)[] Materials =
    [
        ("Rusty Metal", "PbrRustyMetalBase", "PbrRustyMetalNormal", "PbrRustyMetalArm"),
        ("Brick Wall", "PbrBrickWallBase", "PbrBrickWallNormal", "PbrBrickWallArm"),
        ("Galvanized Metal", "PbrGalvanizedBase", "PbrGalvanizedNormal", "PbrGalvanizedArm"),
    ];

    private readonly List<Mesh> spheres = [];

    // 初值沿用 ReadoutRow 的占位，资产到位前读数不空缺；球建好后被整体替换。
    private string orderText = "—";

    /// <summary>
    /// 本页要取的贴图资产键，按材质套 × base/normal/arm 的顺序展开——旧页面资产阶段就是这个
    /// 加载序，进度条的先后节奏不变。表本身留在 VM 私有，名字与键不会在两处各自漂移。
    /// </summary>
    public IEnumerable<string> TextureKeys => Materials.SelectMany(m => new[] { m.BaseKey, m.NormalKey, m.ArmKey });

    /// <summary>资产阶段建好的三颗球；视图建场景时挂进场景、逐帧自转。</summary>
    public IReadOnlyList<Mesh> Spheres => spheres;

    /// <summary>页首说明。</summary>
    public IObservable<string?> IntroLabel => Strings.Instance.PbrMaterials_Intro;

    /// <summary>读数行标题（「从左到右」）。</summary>
    public IObservable<string?> OrderLabel => Strings.Instance.PbrMaterials_Order;

    /// <summary>读数行下的补充说明。</summary>
    public IObservable<string?> NoteLabel => Strings.Instance.PbrMaterials_Note;

    /// <summary>三颗球从左到右的材质名；非模板动态字符串，SetProperty 走普通属性即可。</summary>
    public string OrderText
    {
        get => orderText;
        private set => SetProperty(ref orderText, value);
    }

    /// <summary>
    /// 资产阶段收图：按材质表把三张贴图接成 Material、建球并等距排开。
    /// 原先住在视图的 LoadAssetsAsync 里；材质与球是 UI 可变的引擎对象，随 MVVM 挪进这里。
    /// </summary>
    public void SetTextures(IReadOnlyDictionary<string, Texture> textures)
    {
        for (var i = 0; i < Materials.Length; i++)
        {
            var (name, baseKey, normalKey, armKey) = Materials[i];

            // TextureLoader 默认按线性上传；BaseColor 是 sRGB 编码的 JPEG，必须标成 gamma 空间，
            // 驱动才会用 SRGB8 内部格式硬件解码到线性——否则 sRGB 值被当线性参与光照，
            // 末尾 GammaCorrectionPass 再提亮一次，整页就会发白发灰。Normal/ARM 是数据图，保持线性。
            textures[baseKey].SetIsGammaSpace(true);

            var material = new Material();

            material.SetTexture("BaseColor", textures[baseKey]);
            material.SetTexture("Normal", textures[normalKey]);
            material.SetTexture("MetallicRoughness", textures[armKey]);

            var sphere = new Mesh
            {
                Name = name,
                Geometry = new SphereGeometry(1.2f, 64, 40),
                Material = material,
            };

            // 三颗球等距排一排；视图逐帧自转，让法线图和反射随角度变化。
            sphere.Position = new Vector3((i - (Materials.Length - 1) / 2f) * 2.4f, 1.5f, 0);

            spheres.Add(sphere);
        }

        OrderText = string.Join(" → ", Array.ConvertAll(Materials, m => m.Name));
    }
}
