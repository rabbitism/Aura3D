using Aura3D.Core.Geometries;
using Aura3D.Core.Nodes;
using Aura3D.Core.Resources;
using Aura3D.Gallery.Kit;
using System.Numerics;
using System.Threading.Tasks;
using Color = System.Drawing.Color;

namespace Aura3D.Gallery.Demos;

/// <summary>
/// 蒙皮动画页的视图，按 Particles/AnimationMix 的样板 MVVM 化：参数、命令、采样器、
/// 骨骼附件与整支火把（四路火焰发射器、点光源）全在 <see cref="SkinnedAnimationViewModel"/>，
/// XAML 经绑定直连；本类只保留生命周期——取资产喂模型与火焰图、在渲染线程回调里组装
/// 相机/灯光/地面并按旧顺序登记模型与骨骼附件、逐帧驱动火光摇曳与读数。
/// 蒙皮动画的驱动链（AnimationSampler 挂在 Model 上由场景推进、BoneAttachment 从采样器的
/// BonesTransform 里取手骨矩阵把自己搬过去）见 VM 与引擎侧实现。
/// </summary>
public sealed partial class SkinnedAnimationDemo : Demo
{
    private readonly SkinnedAnimationViewModel viewModel = new();

    /// <summary>士兵在 glb 里的默认朝向背对镜头；按相机方位角（约 30°）转过来正对观察者。</summary>
    private const float ModelFacingYaw = 210f;

    /// <summary>
    /// 建页：装配 XAML 并把 DataContext 换成 VM。仍须显式赋值——外壳继承下来的
    /// DataContext 优先于默认值；基类先设的「页面自身」在这里被 VM 顶替。
    /// </summary>
    /// <param name="context">宿主环境。</param>
    public SkinnedAnimationDemo(DemoContext context) : base(context)
    {
        InitializeComponent();

        DataContext = viewModel;

        viewModel.FrameRequested += () => Context.RequestFrame();
        viewModel.ShowBoneToggled += value => Context.Settings.Debug.ShowBone = value;
    }

    /// <inheritdoc />
    public override async Task LoadAssetsAsync(AssetBatch assets)
    {
        var (model, animations) = await assets.ModelWithAnimationsAsync("Soldier");

        viewModel.SetSoldier(model, animations);

        // 火把火焰用的翻页图（8×8 共 64 帧），与粒子页共用同一份资产。
        viewModel.SetFireSheet(await assets.TextureAsync("ParticleFirePng"));
    }

    /// <inheritdoc />
    public override void BuildScene()
    {
        var scene = Context.Scene!;

        var soldier = viewModel.SoldierModel;

        if (soldier == null)
            return;

        scene.ShowGrid = true;
        scene.MainCamera.Position = new Vector3(2.4f, 2.2f, 4.2f);
        scene.MainCamera.LookAt(new Vector3(0, 1.2f, 0));

        var light = new DirectionalLight
        {
            Name = "Key",
            LightColor = Color.White,
            CastShadow = false,
        };

        light.RotationDegrees = new Vector3(-38f, -25f, 0);

        scene.AddNode(light);

        var ground = new Mesh
        {
            Name = "Ground",
            Geometry = new PlaneGeometry(30f, 30f),
            Material = new Material(),
        };

        ground.Material.SetTexture("BaseColor", Procedural.Checker(256, 18));

        scene.AddNode(ground);

        scene.AddNode(soldier);

        // 士兵的建模朝向背对镜头（相机在 +X+Z 一侧看到的是后背），转 ModelFacingYaw 让他正对观察者；
        // BoneAttachment 取的是 Mesh.WorldTransform，模型转过去后火把会跟着一起转到正面。
        soldier.RotationDegrees = new Vector3(0, ModelFacingYaw, 0);

        // 剪辑下拉的选项来自 glb 实际的 animations 数组，只有资产到位后才填得进下拉；
        // 初选会在 VM 里建第一个采样器挂上模型——必须排在下面的 FitToBoundingBox 之前，
        // 采样器构造立即算第一帧姿势，包围盒跟着姿势走。
        viewModel.InitializeClips();

        scene.MainCamera.FitToBoundingBox(soldier.BoundingBox, 0.3f);

        // fit 会按模型盒子把 far 收紧到十几米，30×30 地面和滚轮拉远立刻撞远平面；这里固定抬回本页尺度。
        scene.MainCamera.FarPlane = 120f;

        // 骨骼附件（含整支火把）由 VM 建好；这里只登记进场景，随后开喷火焰——
        // 发射原点取 ParticleSystem 的世界变换，Play 之后每帧都会跟着骨骼矩阵走。
        var attachment = viewModel.BuildAttachment();

        if (attachment != null)
        {
            scene.AddNode(attachment);

            viewModel.StartFlame();
        }

        Context.Settings.Debug.ShowBone = viewModel.ShowBone;
    }

    /// <inheritdoc />
    public override void Update(double deltaTime)
    {
        viewModel.Tick(deltaTime);

        Context.RequestFrame();
    }
}
