using Aura3D.Core.Geometries;
using Aura3D.Core.Nodes;
using Aura3D.Core.Resources;
using Aura3D.Core.Scenes;
using Aura3D.Gallery.Kit;
using Aura3D.Gallery.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Aura3D.Gallery.Demos;

/// <summary>
/// 拾取页的界面状态，按 Particles/Pipelines 页的样板 MVVM 化。分工：
/// <list type="bullet">
///   <item><description>文案：AXAML 直接写 <c>{Translate}</c> 标记扩展取 Strings.Keys 键值；换语言走外壳既有的整页重挂。</description></item>
///   <item><description>参数：滑杆/开关双向绑定到这里的属性。挡板可见与方阵边长写穿到住在
///     VM 里的引擎模型；EnablePicking 是视图属性、中心射线由视图的逐帧路径消费，两者
///     VM 都够不着，setter 走「backing + 值事件」，由视图转写（同 PostProcessing 页的 Settings 桥）。</description></item>
///   <item><description>结果文本：普通 string + SetProperty，视图拾到（或中心射线未中）后调
///     这里的报告方法写入。命中行虽然是 resx 的 {0}..{8} 模板键，但不做成 CreateFormat 的
///     observable——这一行还要在未命中时整句换成另一条无占位符文案，单一模板表达不了；
///     事件驱动的快照串与旧页行为一致（换语言后维持上一条，下次拾取换新）。</description></item>
///   <item><description>引擎模型：五个目标球、半透明挡板与实例方阵住在这里，建场景时由视图
///     逐个挂进场景；相机与灯是纯布景，仍留在视图的 BuildScene 里组装。</description></item>
/// </list>
/// 视图桥是三个事件：<see cref="EnablePickingToggled"/> 与 <see cref="CenterRayToggled"/>
/// （语义开关，视图转写进视图属性/逐帧判定用的影子变量）、<see cref="FrameRequested"/>
/// （结果、挡板或方阵变了要排一次重绘，视图接宿主的 InvalidateRender）。
/// 拾取本身是强视图交互：坐标口径（DIP）、命中点标记与事件订阅都在视图里，VM 只收结果。
/// </summary>
public sealed partial class PickingViewModel : ObservableObject
{
    /// <summary>
    /// 建好 VM：组装五个目标球、挡板与实例方阵（全是 CPU 侧声明，渲染回调里才上传 GPU），
    /// 并按边长初值铺一遍方阵实例。参数初值在字段上对齐引擎/视图对象的默认
    /// （旧 XAML 写死的 IsChecked/Value 由绑定初始化取代）。
    /// </summary>
    public PickingViewModel()
    {
        // 五个目标球：拾取的主要对象，棋盘贴图按序换格数以便肉眼区分。
        var targets = new List<Mesh>(5);

        for (int i = 0; i < 5; i++)
        {
            var mesh = new Mesh
            {
                Name = $"Target{i}",
                Geometry = new SphereGeometry(0.8f, 32, 18),
                Material = new Material(),
            };

            mesh.Material.SetTexture("BaseColor", Procedural.Checker(128, 3 + i));
            mesh.Position = new Vector3((i - 2) * 2.3f, 1.2f, -1f);

            targets.Add(mesh);
        }

        Targets = targets;

        // 一块半透明挡板横在球体前面：同一次点击会同时命中它和后面的球，
        // 用来区分 PickAt（全命中）与 PickClosestAt（最近）。
        var blockerMaterial = new Material { BlendMode = BlendMode.Translucent };

        blockerMaterial.SetTexture("BaseColor", Procedural.SoftDot(128, 0.8f));

        Blocker = new Mesh
        {
            Name = "Blocker",
            Geometry = new PlaneGeometry(13f, 6f),
            Material = blockerMaterial,
            Position = new Vector3(0, 2.4f, 3f),
        };

        // 实例方阵：FromMesh 复制源网格，实例变换随边长滑杆整批重写（见 ApplyField）。
        var cube = new Mesh
        {
            Name = "FieldCube",
            Geometry = new BoxGeometry(0.34f, 0.34f, 0.34f),
            Material = new Material(),
        };

        cube.Material.SetTexture("BaseColor", Procedural.VerticalGradient(width: 8, height: 64));

        Field = InstancedMesh.FromMesh(cube);

        // 等价于旧 BuildField→SetFieldCount(GridSize)：构造期先铺边长初值的一份。
        ApplyField();

        // 挡板可见初值取节点上真正生效的 Enable（引擎默认可见）；EnablePicking/中心射线
        // 的初值分别是视图属性与旧开关的默认（开/关），视图 attach 时会按这里的值对齐。
        occluderVisible = Blocker.Enable;
    }

    /// <summary>EnablePicking 开关变化；视图属性只有视图够得着，由它转写进 view.EnablePicking。</summary>
    public event Action<bool>? EnablePickingToggled;

    /// <summary>「中心射线」开关变化；逐帧拾取路径在视图里，由它记进自己的影子变量。</summary>
    public event Action<bool>? CenterRayToggled;

    /// <summary>结果、挡板可见或方阵边长变化后请求一帧重绘；视图把它接到宿主的 InvalidateRender 上。</summary>
    public event Action? FrameRequested;

    /// <summary>五个目标球：拾取的主要对象，建场景时由视图挂进场景。</summary>
    public IReadOnlyList<Mesh> Targets { get; }

    /// <summary>半透明挡板：可见开关写穿到它的 Enable。</summary>
    public Mesh Blocker { get; }

    /// <summary>实例方阵：边长滑杆整批重写它的实例变换。</summary>
    public InstancedMesh Field { get; }

    private string resultText = "—";

    private double gridSize = 6;
    private bool enablePicking = true;
    private bool centerRay;
    private bool occluderVisible = true;

    // 命中行的 {7}/{8}：最近一次 PickAt 的命中总数与 ObjectPicked 的累计点击数。
    // 前者由视图按旧顺序登记，后者只有这里的清零命令会改。
    private int lastHitCount;
    private int clickCount;

    /// <summary>
    /// 拾取结果行。事件驱动的快照串：命中行按 resx 模板键格式化，未命中整句换提示文案；
    /// 初值沿用 ReadoutRow 的默认占位「—」，首条报告之前与旧页面显示一致。
    /// </summary>
    public string ResultText
    {
        get => resultText;
        private set => SetProperty(ref resultText, value);
    }

    /// <summary>
    /// 视图的 EnablePicking。VM 不持有视图对象，开关状态先落在这里，经
    /// <see cref="EnablePickingToggled"/> 交给视图写穿；视图 attach 时再按本值对齐一次，
    /// 构造到 attach 之间的拨动不会丢（旧页这一段会静默丢写）。
    /// </summary>
    public bool EnablePicking
    {
        get => enablePicking;
        set
        {
            if (SetProperty(ref enablePicking, value))
                EnablePickingToggled?.Invoke(value);
        }
    }

    /// <summary>
    /// 「中心射线逐帧拾取」。开着时视图每帧改从视口正中拾取并禁用点击拾取，
    /// 判定用的布尔在视图侧，经 <see cref="CenterRayToggled"/> 转写过去。
    /// </summary>
    public bool CenterRay
    {
        get => centerRay;
        set
        {
            if (SetProperty(ref centerRay, value))
                CenterRayToggled?.Invoke(value);
        }
    }

    /// <summary>挡板可见；写穿到 <see cref="Blocker"/> 的 Enable，再排一帧重绘。</summary>
    public bool OccluderVisible
    {
        get => occluderVisible;
        set
        {
            if (!SetProperty(ref occluderVisible, value))
                return;

            Blocker.Enable = value;
            FrameRequested?.Invoke();
        }
    }

    /// <summary>
    /// 实例方阵边长；按整数生效，变化即整批重写实例变换并排一帧重绘。
    /// 旧处理器有「场景未建先跳过」的守卫，那是因为方阵当时建在 BuildScene 里；
    /// 现在方阵随 VM 构造就存在，SetInstances 只写 CPU 侧缓冲，建场景前后调用都安全，
    /// 先拖滑杆后进场景也能得到一致的边长（旧页这一段滑杆与画面会不一致）。
    /// </summary>
    public double GridSize
    {
        get => gridSize;
        set
        {
            if (!SetProperty(ref gridSize, Math.Round(value)))
                return;

            ApplyField();
            FrameRequested?.Invoke();
        }
    }

    /// <summary>把点击计数清零；源生成 <c>ClearClicksCommand</c> 供按钮绑定。</summary>
    [RelayCommand]
    private void ClearClicks() => clickCount = 0;

    /// <summary>
    /// ObjectPicked 事件路径：事件只在有最近命中时触发，这里先记一次点击再写命中行。
    /// 该路径拿不到全命中列表，行里的「该点命中 N 个」沿用上一回 PickAt 登记的数，与旧处理器一致。
    /// </summary>
    /// <param name="hit">事件带回的最近命中。</param>
    public void ReportObjectPicked(PickResult hit)
    {
        clickCount++;

        WriteHit(hit, "ObjectPicked");
    }

    /// <summary>
    /// PickAt 手算路径（点击 / 中心射线）的命中报告。来源文案由视图给：
    /// 点击走本地化键、中心射线用 API 名，两者都不是模板参数。
    /// </summary>
    /// <param name="hit">最近命中。</param>
    /// <param name="source">读数行 {0} 的来源名。</param>
    public void ReportPick(PickResult hit, string source) => WriteHit(hit, source);

    /// <summary>中心射线未命中：整句换成无占位符的提示文案（旧代码此分支不排重绘，保持）。</summary>
    public void ReportCenterMiss() => ResultText = Strings.Keys.Picking_NoCenterHit.T();

    /// <summary>
    /// 登记最近一次 PickAt 的命中总数，供命中行的 {7} 显示。调用顺序由视图按旧代码保持：
    /// 点击路径先登记再报告，中心射线先报告再登记（读数落后一帧，旧代码即如此）。
    /// </summary>
    /// <param name="count">该点的全部命中数。</param>
    public void NoteHitCount(int count) => lastHitCount = count;

    /// <summary>按 resx 模板键拼命中行并排一帧重绘；占位顺序与旧 Report 逐个对齐。</summary>
    private void WriteHit(PickResult hit, string source)
    {
        ResultText = Strings.Keys.Picking_HitReadout.Format(
            source,
            hit.Node.Name,
            hit.Distance,
            hit.WorldPosition.X,
            hit.WorldPosition.Y,
            hit.WorldPosition.Z,
            hit.InstanceIndex?.ToString() ?? "—",
            lastHitCount,
            clickCount);

        // 旧 Report 末尾的 InvalidateRender：结果变了排一帧重绘。
        FrameRequested?.Invoke();
    }

    /// <summary>
    /// 按当前边长整批重写方阵实例：XZ 平面铺开、Y 缩放按 (x+z)%4 逐格递增，整体摆在挡板前。
    /// </summary>
    private void ApplyField()
    {
        var size = (int)gridSize;

        var transforms = new List<Matrix4x4>(size * size);

        for (int x = 0; x < size; x++)
        {
            for (int z = 0; z < size; z++)
            {
                var position = new Vector3(
                    (x - (size - 1) / 2f) * 0.55f,
                    0.18f,
                    (z - (size - 1) / 2f) * 0.55f + 4f);

                transforms.Add(
                    Matrix4x4.CreateScale(1f, 1f + ((x + z) % 4) * 0.5f, 1f)
                    * Matrix4x4.CreateTranslation(position));
            }
        }

        Field.SetInstances(transforms);
    }
}
