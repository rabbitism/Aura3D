using Aura3D.Core.Geometries;
using Aura3D.Core.Math;
using Aura3D.Core.Nodes;
using Aura3D.Core.Resources;
using Aura3D.Gallery.Kit;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Color = System.Drawing.Color;
using ModelNode = Aura3D.Core.Nodes.Model;

namespace Aura3D.Gallery.Demos;

/// <summary>
/// 动画混合页的视图，MVVM 化：文案、参数、命令、两套采样器与剪辑字典全在
/// <see cref="AnimationMixViewModel"/>，XAML 经绑定直连；本类只保留生命周期——取资产喂模型、
/// 在渲染线程回调里组装相机/灯光/地面、按旧顺序登记两份模型并挂骨骼附件。
/// <see cref="Kit.BlendSpacePad"/> 是强视图交互控件，也留在这里：拖动回调把轴值推进 VM，
/// 扫掠/Reset 产生的新轴值在每帧 Update 里经 <c>SetAxis</c> 回显滑块（只挪滑块、不发事件，
/// 不会回流成一次「拖动」）。
/// </summary>
public sealed partial class AnimationMixDemo : Demo
{
    private readonly AnimationMixViewModel viewModel = new();

    /// <summary>
    /// 建页：装配 XAML 并把 DataContext 换成 VM。仍须显式赋值——外壳继承下来的
    /// DataContext 优先于默认值；基类先设的「页面自身」在这里被 VM 顶替。
    /// 编辑板上的采样点摆位与 VM 的 BlendSamples 一一对应，标签就是剪辑名。
    /// </summary>
    /// <param name="context">宿主环境。</param>
    public AnimationMixDemo(DemoContext context) : base(context)
    {
        InitializeComponent();

        DataContext = viewModel;

        viewModel.FrameRequested += () => Context.RequestFrame();

        BlendPad.SetSamples(AnimationMixViewModel.BlendSamples.Select(
            s => new BlendSpacePad.SamplePoint(s.Point.X, s.Point.Y, s.Clip)));
    }

    /// <inheritdoc />
    public override async Task LoadAssetsAsync(AssetBatch assets)
    {
        var (model, animations) = await assets.ModelWithAnimationsAsync("KayKitWarrior");

        viewModel.SetWarrior(model, animations);
    }

    /// <inheritdoc />
    public override void BuildScene()
    {
        var scene = Context.Scene!;

        var warrior = viewModel.WarriorModel;

        if (warrior?.Skeleton == null)
            return;

        scene.ShowGrid = true;
        scene.MainCamera.Position = new Vector3(0, 1.9f, 6.4f);
        scene.MainCamera.LookAt(new Vector3(0, 1.1f, 0));
        // 30×30 地面 + 拉远余量，抬过默认 far 100。
        scene.MainCamera.FarPlane = 120f;

        var light = new DirectionalLight { LightColor = Color.White };

        light.RotationDegrees = new Vector3(-40f, -20f, 0);

        scene.AddNode(light);

        var ground = new Mesh
        {
            Name = "Ground",
            Geometry = new PlaneGeometry(30f, 30f),
            Material = new Material(),
        };

        ground.Material.SetTexture("BaseColor", Procedural.Checker(256, 18));

        scene.AddNode(ground);

        warrior.Position = new Vector3(-1.9f, 0, 0);

        scene.AddNode(warrior);

        viewModel.BuildGraph();

        // 右半边是同一条模型的克隆：SharedResource 连骨骼都共用，两个采样器各写自己的 BoneMatrixBuffer。
        var blendModel = viewModel.CreateBlendClone();

        if (blendModel != null)
        {
            blendModel.Position = new Vector3(1.9f, 0, 0);

            scene.AddNode(blendModel);
        }

        viewModel.BuildBlendSpace();

        // KayKit 的头盔不是蒙皮网格、只是静态挂在 head 骨骼节点下，而引擎的节点树不随动画更新，
        // 骨骼一动头盔就留在原地。显式把它转成 BoneAttachment 跟随骨骼；克隆要在 attach 之前做，
        // 两份模型各自处理一遍。
        AttachRigidMeshesToBones(warrior);

        if (blendModel != null)
            AttachRigidMeshesToBones(blendModel);
    }

    /// <inheritdoc />
    public override void Update(double deltaTime)
    {
        viewModel.Tick(deltaTime);

        // 扫掠/Reset 改过的轴值统一在这里回显编辑板：SetAxis 只挪滑块、不发事件；
        // 拖动路径写进 VM 的值与滑块位置一致，这一句对它是幂等的。
        BlendPad.SetAxis(viewModel.AxisX, viewModel.AxisY);

        Context.RequestFrame();
    }

    // 编辑板拖动：用户接管轴值。关扫掠与写穿混合空间都在 VM 的 SetAxisFromPad 里，
    // 这里只做事件转发，让指针交互留在视图层。
    private void OnPadAxisChanged(object? sender, (double X, double Y) e) =>
        viewModel.SetAxisFromPad(e.X, e.Y);

    /// <summary>
    /// 把「没有蒙皮属性、但挂在骨骼节点下」的 mesh（KayKit 的头盔就是这种）转成 <see cref="BoneAttachment"/>：
    /// 从模型树摘下来、<c>Model = null</c> 变成静态网格，挂到场景根的 BoneAttachment 上，
    /// 每帧由 <c>LocalOffset × 骨骼矩阵 × 蒙皮网格世界矩阵</c> 搬到骨骼当前位置。
    /// LocalOffset 取绑定姿势下 mesh 相对骨骼的变换，保证第一帧位置不变。
    /// </summary>
    private void AttachRigidMeshesToBones(ModelNode model)
    {
        var skeleton = model.Skeleton;

        if (skeleton == null)
            return;

        // BoneAttachment 需要一份蒙皮网格来拿 sampler 与模型世界矩阵
        var samplerProvider = model.Meshes.FirstOrDefault(m => m.IsSkinnedMesh);

        if (samplerProvider == null)
            return;

        foreach (var mesh in model.Meshes.ToList())
        {
            // 有蒙皮属性的 mesh 走 GPU 蒙皮，不用管
            if (mesh.Geometry?.GetAttributeData(BuildInVertexAttribute.Joints_0) != null)
                continue;

            // glTF 里关节名就是节点名，沿 mesh 的父链向上找最近的骨骼
            var bone = FindNearestBoneAncestor(mesh.Parent, skeleton);

            if (bone == null)
                continue;

            var localOffset = GetLocalToModel(mesh, model) * bone.WorldMatrix.Inverse();

            mesh.Parent!.RemoveChild(mesh, AttachToParentRule.KeepLocal);

            mesh.Model = null;

            var attachment = new BoneAttachment
            {
                Name = $"{mesh.Name}_Attach",
                Mesh = samplerProvider,
                BoneName = bone.Name,
                LocalOffset = localOffset,
            };

            attachment.AddChild(mesh, AttachToParentRule.KeepLocal);

            Context.Scene!.AddNode(attachment);
        }
    }

    private static Bone? FindNearestBoneAncestor(Node? node, Skeleton skeleton)
    {
        while (node != null)
        {
            var bone = skeleton.Bones.FirstOrDefault(b => b.Name == node.Name);

            if (bone != null)
                return bone;

            node = node.Parent;
        }

        return null;
    }

    // 行主序约定下 world = local × parent，从 mesh 往上累乘到模型根，得到模型空间的绑定变换
    private static Matrix4x4 GetLocalToModel(Node node, Node root)
    {
        var result = Matrix4x4.Identity;

        // 入参保持非空口径；向上走的游标用可空类型——根之上就是 null，
        // 否则 node = node.Parent 会吃一条可空警告（Node.Parent 声明为 Node?）
        Node? cursor = node;

        while (cursor != null && !ReferenceEquals(cursor, root))
        {
            result *= cursor.LocalTransform;

            cursor = cursor.Parent;
        }

        return result;
    }
}
