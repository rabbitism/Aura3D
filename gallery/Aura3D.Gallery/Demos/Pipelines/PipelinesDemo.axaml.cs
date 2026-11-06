using Aura3D.Core.Geometries;
using Aura3D.Core.Nodes;
using Aura3D.Core.Resources;
using Aura3D.Gallery.Kit;
using Aura3D.Gallery.Localization;
using System;
using System.Numerics;
using System.Threading.Tasks;
using Color = System.Drawing.Color;

namespace Aura3D.Gallery.Demos;

/// <summary>
/// 同一幕场景在五条管线下的差别，MVVM 化：文案、灯光参数与两条读数全在
/// <see cref="PipelinesViewModel"/>，XAML 经绑定直连；本类只保留生命周期——组装场景
/// （相机、地面、五个各只喂一种信息的对象）与逐帧把管线事实喂给 VM 的读数。
/// 页里的每个对象都刻意只喂某一种信息（平涂、法线贴图、金属度粗糙度、遮罩、半透明），
/// 于是在宿主状态栏里换管线就能看出「谁读了哪张贴图、谁压根不读」。
/// 换管线不在页面里做：状态栏的管线框走 <c>DemoContext.Reload</c>，整页连同 VM 重建。
/// </summary>
public sealed partial class PipelinesDemo : Demo
{
    private readonly PipelinesViewModel viewModel = new();

    private Node? row;

    /// <summary>
    /// 建页：装配 XAML 并把 DataContext 换成 VM。仍须显式赋值——外壳继承下来的
    /// DataContext 优先于默认值；基类先设的「页面自身」在这里被 VM 顶替。
    /// </summary>
    /// <param name="context">宿主环境。</param>
    public PipelinesDemo(DemoContext context) : base(context)
    {
        InitializeComponent();

        DataContext = viewModel;

        viewModel.FrameRequested += () => Context.InvalidateRender();
    }

    /// <inheritdoc />
    public override Task LoadAssetsAsync(AssetBatch assets) => Task.CompletedTask;

    /// <inheritdoc />
    public override void BuildScene()
    {
        var scene = Context.Scene!;

        scene.MainCamera.Position = new Vector3(0, 2.6f, 12.5f);
        scene.MainCamera.LookAt(new Vector3(0, 1.1f, 0));
        // 40×40 地面 + 拉远余量，抬过默认 far 100。
        scene.MainCamera.FarPlane = 150f;

        scene.ShowGrid = true;

        // 两个灯是 VM 的引擎模型（参数行写穿到它们），这里只登记进场景。
        scene.AddNode(viewModel.Sun);
        scene.AddNode(viewModel.Point);

        var ground = new Mesh
        {
            Name = "Ground",
            Geometry = new PlaneGeometry(40f, 40f),
            Material = new Material(),
        };

        ground.Material.SetTexture("BaseColor", Procedural.Checker(256, 20));

        scene.AddNode(ground);

        row = new Node { Name = "Row" };

        scene.AddNode(row);

        Add(Strings.Keys.Pipelines_NodeFlat.T(), new SphereGeometry(0.9f, 36, 20), -5f, m => m.SetTexture("BaseColor", Procedural.Checker(128, 6)));
        Add(Strings.Keys.Pipelines_NodeNormal.T(), new SphereGeometry(0.9f, 36, 20), -3f, m =>
        {
            m.SetTexture("BaseColor", Procedural.Checker(128, 5));
            m.SetTexture("Normal", Procedural.BumpNormal(256, 7));
        });
        Add("③ MR", new SphereGeometry(0.9f, 36, 20), -1f, m =>
        {
            m.SetTexture("BaseColor", Procedural.Checker(128, 4, new Procedural.Rgb(220, 120, 60), new Procedural.Rgb(60, 60, 70)));
            m.SetTexture("MetallicRoughness", Procedural.MetalRough(128));
        });
        Add(Strings.Keys.Pipelines_NodeMask.T(), new CylinderGeometry(0.75f, 0.75f, 1.8f, 40), 1f, m =>
        {
            m.SetTexture("BaseColor", Procedural.SoftDot(128));
            m.BlendMode = BlendMode.Masked;
            m.AlphaCutoff = 0.35f;
        });
        Add(Strings.Keys.Pipelines_NodeTranslucent.T(), new SphereGeometry(0.9f, 36, 20), 3f, m =>
        {
            m.SetTexture("BaseColor", Procedural.FromRgba(Solid(255, 90, 90, 90), 1));
            m.BlendMode = BlendMode.Translucent;
        });

        Report();
    }

    /// <inheritdoc />
    public override void Update(double deltaTime)
    {
        Report();

        Context.RequestFrame();
    }

    /// <summary>
    /// 读数的三样原料（要求的管线、实际管线对象、背景是否立方图）都在宿主/场景手里，
    /// VM 不碰 DemoContext，由视图取来逐帧喂给它——这是本页对 Particles 之 Tick 的对应物。
    /// </summary>
    private void Report() => viewModel.Report(
        Context.RequestedPipeline,
        Context.Scene?.RenderPipeline,
        Context.Scene?.Background.IsT0 == true);

    private void Add(string name, Geometry geometry, float x, Action<Material> fill)
    {
        var material = new Material();

        fill(material);

        var mesh = new Mesh
        {
            Name = name,
            Geometry = geometry,
            Material = material,
        };

        mesh.Position = new Vector3(x, 1.15f, 0);

        row!.AddChild(mesh, AttachToParentRule.KeepLocal);
    }

    private static byte[] Solid(byte r, byte g, byte b, byte a)
    {
        var data = new byte[4];

        data[0] = r;
        data[1] = g;
        data[2] = b;
        data[3] = a;

        return data;
    }
}
