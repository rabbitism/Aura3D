using Aura3D.Core.Nodes;
using Aura3D.Core.Particles;
using Aura3D.Core.Scenes;
using System.Numerics;
using System.Threading.Tasks;
using Color = System.Drawing.Color;

namespace Aura3D.Gallery.Demos;

/// <summary>
/// 粒子系统页的视图，MVVM 试点：文案、参数与命令全在 <see cref="ParticlesViewModel"/>，
/// XAML 经绑定直连，本类只保留生命周期——取资产喂火焰图、在渲染线程回调里组装场景、
/// 逐帧驱动 Readout 与渲染。三条发射路径（公告板含按寿命翻页、一次性爆发、网格模式
/// InstancedMesh）的引擎侧配置见 VM 上的三个发射器属性。
/// </summary>
public sealed partial class ParticlesDemo : Demo
{
    private readonly ParticlesViewModel viewModel = new();

    /// <summary>
    /// 建页：装配 XAML 并把 DataContext 换成 VM。仍须显式赋值——外壳继承下来的
    /// DataContext 优先于默认值；基类先设的「页面自身」在这里被 VM 顶替。
    /// </summary>
    /// <param name="context">宿主环境。</param>
    public ParticlesDemo(DemoContext context) : base(context)
    {
        InitializeComponent();

        DataContext = viewModel;

        viewModel.FrameRequested += () => Context.RequestFrame();
        viewModel.ShowBoundsToggled += value => Context.Settings.Debug.ShowParticleBounds = value;
    }

    /// <inheritdoc />
    public override async Task LoadAssetsAsync(AssetBatch assets)
    {
        viewModel.SetFireSheet(await assets.TextureAsync("ParticleFirePng"));
    }

    /// <inheritdoc />
    public override void BuildScene()
    {
        var scene = Context.Scene!;

        viewModel.ApplyBillboard();
        scene.MainCamera.Position = new Vector3(0, 5.5f, 18f);
        scene.MainCamera.LookAt(new Vector3(0, 3.2f, 0));

        var light = new DirectionalLight { LightColor = Color.White };

        light.RotationDegrees = new Vector3(-50f, -20f, 0);

        scene.AddNode(light);

        viewModel.AttachSystems(
            AddSystem(scene, "Fountain", new Vector3(-6f, 0.6f, 0f), viewModel.FountainEmitter),
            AddSystem(scene, "Burst", new Vector3(0f, 3.2f, 0f), viewModel.BurstEmitter),
            AddSystem(scene, "Debris", new Vector3(6f, 4.2f, 0f), viewModel.DebrisEmitter));

        Context.Settings.Debug.ShowParticleBounds = viewModel.ShowBounds;
    }

    /// <inheritdoc />
    public override void Update(double deltaTime)
    {
        viewModel.Tick();
        Context.RequestFrame();
    }

    private static ParticleSystem AddSystem(Scene scene, string name, Vector3 position, ParticleEmitter emitter)
    {
        var system = new ParticleSystem { Name = name, MaxParticles = emitter.MaxParticles };

        system.Emitters.Add(emitter);
        system.Position = position;

        scene.AddNode(system);

        system.Play();

        return system;
    }
}
