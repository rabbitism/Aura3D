using Aura3D.Avalonia;
using Aura3D.Core.Geometries;
using Aura3D.Core.Nodes;
using Aura3D.Core.Resources;
using Aura3D.Gallery.Kit;
using System;
using System.Numerics;
using System.Threading.Tasks;

namespace Aura3D.Gallery.Demos;

/// <summary>
/// 视图层的生命周期与 GPU 资源归还，MVVM 化：计数、日志、命令全在
/// <see cref="GpuLifecycleViewModel"/>，本类只剩生命周期——订阅五个场景事件把计数推进 VM、
/// 组装场景（转台加四个方块是纯布景，没有参数写穿，不进 VM）、按 <c>pendingRebuild</c> 调度重建。
/// <c>ContextRestored</c> 是本页主角：GL 句柄由引擎统一重建，本页没有自缓存的句柄，
/// 所以没有恢复动作——哪天真缓存了，恢复逻辑也落在这里（生命周期），不进 VM。
/// 三条回收命令动的是视图 API 且只能排请求，经语义事件桥从 VM 回到本类执行。
/// </summary>
public sealed partial class GpuLifecycleDemo : Demo
{
    private readonly GpuLifecycleViewModel viewModel = new();

    private Aura3DView? view;
    private Node? spinner;

    private bool pendingRebuild;

    /// <summary>
    /// 建页：装配 XAML 并把 DataContext 换成 VM。仍须显式赋值——外壳继承下来的
    /// DataContext 优先于默认值；基类先设的「页面自身」在这里被 VM 顶替。
    /// 同时把 VM 的语义事件接到视图/宿主上：动真格 GPU 的三条在本类里调视图 API 并排帧，
    /// 「只画一帧」与开关变化只排帧。
    /// </summary>
    /// <param name="context">宿主环境。</param>
    public GpuLifecycleDemo(DemoContext context) : base(context)
    {
        InitializeComponent();

        // 顶替基类的 DataContext=this：页面 XAML 的绑定从此落在 VM 上。
        DataContext = viewModel;

        // GL 回收只能排请求（下一帧开头才真正执行），所以这里统一「调 API + 排一帧」，
        // 与旧按钮回调逐行等价；事件在 ViewAttached 之前触发时 view 还是 null，与旧回调的 view? 一致。
        viewModel.ReleaseGpuResourcesRequested += () =>
        {
            view?.ReleaseGpuResources();
            Context.RequestFrame();
        };

        viewModel.SimulateContextLostRequested += () =>
        {
            view?.SimulateContextLost();
            Context.RequestFrame();
        };

        viewModel.DestroySceneRequested += () =>
        {
            view?.DestroyScene();
            Context.RequestFrame();
        };

        viewModel.FrameRequested += () => Context.RequestFrame();

        viewModel.ContinuousRenderToggled += value =>
        {
            if (view != null)
                view.AutoRequestNextFrameRendering = value;

            Context.RequestFrame();
        };
    }

    /// <inheritdoc />
    public override Task LoadAssetsAsync(AssetBatch assets) => Task.CompletedTask;

    /// <inheritdoc />
    public override void ViewAttached(Aura3DView created)
    {
        view = created;

        created.SceneInitialized += OnSceneInitialized;
        created.SceneUpdated += OnSceneUpdated;
        created.ContextLost += OnContextLost;
        created.ContextRestored += OnContextRestored;
        created.SceneDestroyed += OnSceneDestroyed;
    }

    /// <inheritdoc />
    public override void BuildScene()
    {
        // 场景可能是刚被 DestroyScene 换掉的新实例，节点字段全部作废，重建从这里开始。
        spinner = null;
        pendingRebuild = false;

        var scene = Context.Scene!;

        scene.MainCamera.Position = new Vector3(0, 3.2f, 11f);
        scene.MainCamera.LookAt(new Vector3(0, 1.2f, 0));
        // 40×40 地面 + 拉远余量，抬过默认 far 100。
        scene.MainCamera.FarPlane = 150f;

        var sun = new DirectionalLight { LightColor = System.Drawing.Color.White };

        sun.RotationDegrees = new Vector3(-46f, -30f, 0);

        scene.AddNode(sun);

        var ground = new Mesh
        {
            Name = "Ground",
            Geometry = new PlaneGeometry(40f, 40f),
            Material = new Material(),
        };

        ground.Material.SetTexture("BaseColor", Procedural.Checker(128, 16));

        scene.AddNode(ground);

        spinner = new Node { Name = "Spinner" };

        scene.AddNode(spinner);

        for (int i = 0; i < 4; i++)
        {
            var box = new Mesh
            {
                Name = $"Box{i}",
                Geometry = new BoxGeometry(1.1f, 1.1f, 1.1f),
                Material = new Material(),
            };

            box.Material.SetTexture("BaseColor", Procedural.Checker(64, 2 + i * 2));

            var angle = i * MathF.PI / 2f;

            box.Position = new Vector3(MathF.Cos(angle) * 2.6f, 0.85f, MathF.Sin(angle) * 2.6f);

            spinner.AddChild(box, AttachToParentRule.KeepLocal);
        }

        viewModel.RecordBuildScene(scene.Nodes.Count);
    }

    /// <inheritdoc />
    public override void Update(double deltaTime)
    {
        if (pendingRebuild)
            BuildScene();

        if (spinner != null)
            spinner.RotationDegrees = new Vector3(0, spinner.RotationDegrees.Y + (float)(deltaTime * 40), 0);

        // 读数的三个状态位在视图/场景手里，逐帧取来喂给 VM（帧号与最慢帧也在同一次 Tick 里推进）。
        // 不在这里额外排帧：连续渲染由 AutoRequestNextFrameRendering 自驱，旧页面同样只靠它。
        var pipeline = Context.Scene?.RenderPipeline;

        viewModel.Tick(deltaTime, view?.IsContextLost, pipeline?.IsInitialized, pipeline?.IsDestroyed);
    }

    /// <inheritdoc />
    public override void Unload()
    {
        if (view == null)
            return;

        view.SceneInitialized -= OnSceneInitialized;
        view.SceneUpdated -= OnSceneUpdated;
        view.ContextLost -= OnContextLost;
        view.ContextRestored -= OnContextRestored;
        view.SceneDestroyed -= OnSceneDestroyed;
    }

    private void OnSceneInitialized(object? sender, InitializedRoutedEventArgs e)
    {
        // 外壳只在第一个场景的首帧调 BuildScene，之后的新场景由这一位接手。
        pendingRebuild = true;

        viewModel.RecordSceneInitialized(view?.IsContextLost);
    }

    private void OnSceneUpdated(object? sender, UpdateRoutedEventArgs e)
    {
        viewModel.RecordSceneUpdated();
    }

    private void OnContextLost(object? sender, ContextLostRoutedEventArgs e)
    {
        viewModel.RecordContextLost();
    }

    private void OnContextRestored(object? sender, ContextRestoredRoutedEventArgs e)
    {
        viewModel.RecordContextRestored();
    }

    private void OnSceneDestroyed(object? sender, DestroyedRoutedEventArgs e)
    {
        viewModel.RecordSceneDestroyed();
    }
}
