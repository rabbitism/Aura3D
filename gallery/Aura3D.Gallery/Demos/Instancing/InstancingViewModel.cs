using Aura3D.Core.Geometries;
using Aura3D.Core.Nodes;
using Aura3D.Core.Resources;
using Aura3D.Gallery.Kit;
using Aura3D.Gallery.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Irihi.Lingua;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Aura3D.Gallery.Demos;

/// <summary>
/// 实例化页的界面状态（MVVM 化，照搬粒子页/HISM 页的分工）：
/// <list type="bullet">
///   <item><description>文案：每个标签一个 Lingua observable，XAML 用 <c>{Binding Xxx^}</c> 订阅，
///     换语言时由绑定自动收到新值，不依赖外壳整页重挂。</description></item>
///   <item><description>参数：滑杆/开关/下拉双向绑定到这里的属性，setter 写穿到两片
///     <see cref="InstancedMesh"/>（或先记字段、由逐帧驱动消费）；初值即原页面 XAML/字段里
///     那一份，旧页面靠 Value/IsChecked 初值 + 事件处理写回，现在由绑定初始化取代。</description></item>
///   <item><description>Readout：Lingua 1.1 的 format builder 组装，占位符是两片实例数、
///     顶点数与模式名，只在重铺（<see cref="Rebuild"/>）与摘除（RemoveFront）两个变异点
///     重算——与旧页点名的两处 <c>Report</c> 同时机，语言切换时模板本身也会自动换。</description></item>
///   <item><description>引擎模型：两片 <see cref="InstancedMesh"/>、锚点/变换缓冲与波动时钟
///     都住在这里，视图只负责生命周期——建场景时取走两片节点挂进场景、逐帧驱动。</description></item>
/// </list>
/// 视图桥只有一个事件：<see cref="FrameRequested"/>——波动推进、重铺/摘除/剔除开关改完场景
/// 要排一次重绘，VM 不碰 DemoContext，由视图把它接到宿主的 RequestFrame 上
/// （原 InvalidateRender 与它同义）。
/// </summary>
public sealed partial class InstancingViewModel : ObservableObject
{
    /// <summary>「逐个 UpdateInstance」模式一帧只驱动这么多实例，让两种方式的代价可比。</summary>
    private const int IncrementalSlice = 300;

    private const float FieldSize = 17f;

    private static readonly Vector3 LeftOffset = new(-9.5f, 0, 0);
    private static readonly Vector3 RightOffset = new(9.5f, 0, 0);

    private static readonly LinguaKey[] UpdateModeKeys =
        [Strings.Keys.Instancing_UpdateBatch, Strings.Keys.Instancing_UpdatePerInstance];

    private readonly List<Vector3> anchors = [];
    private readonly List<Matrix4x4> leftTransforms = [];
    private readonly List<Matrix4x4> rightTransforms = [];

    private InstancedMesh? litField;
    private InstancedMesh? coloredField;

    // updateModeIndex 与下拉选中项互为影子：比较/回写用下标，显示用字符串。
    private int updateModeIndex;
    private object? selectedUpdateMode;

    // 滑杆是 double：实例数这个整数参数在写穿时统一 (int) 换算，与旧事件处理一致。
    private double instanceCount = 1200;
    private double waveSpeed = 1.1;
    private double waveAmount = 1.6;
    private float clock;

    private bool animate = true;
    private bool culling = true;

    /// <summary>
    /// 建 VM：对齐下拉初值，再把 Readout 的模板与代价参数装配起来；命令经
    /// <c>[RelayCommand]</c> 源生成，无需在此接线。
    /// </summary>
    public InstancingViewModel()
    {
        UpdateModeOptions = UpdateModeKeys.Select(k => k.T()).ToList();
        selectedUpdateMode = UpdateModeOptions[updateModeIndex];

        // Readout 模板 {0}..{3}：两片实例数与模式名在读数变化时跟着走，
        // 顶点数读左片（两片同源几何，旧 Report 也是只报左片那份）。
        ReadoutText = Strings.Keys.Instancing_CostReadout.CreateFormat()
            .Arg(() => litField?.InstanceCount ?? 0)
            .Arg(() => coloredField?.InstanceCount ?? 0)
            .Arg(() => litField?.VertexCount ?? 0)
            .Arg(() => (string)UpdateModeOptions[updateModeIndex]!)
            .Build();
    }

    /// <summary>「更新方式」下拉的选项文案；当前语言的一份快照，随页面重建。</summary>
    public IList UpdateModeOptions { get; }

    /// <summary>逐帧读数。语言切换时模板换新，绑定自动收到重排后的整句。</summary>
    public LinguaFormatObservable ReadoutText { get; }

    /// <summary>波动推进、重铺、摘除或剔除开关变化后请求一帧重绘；视图把它接到宿主的 RequestFrame 上。</summary>
    public event Action? FrameRequested;

    /// <summary>
    /// 建两片实例场（左片引擎材质、右片逐实例顶点色材质），铺满首批实例后交视图挂进场景——
    /// 等价于旧 BuildScene 中部的组装。节点构造是纯 CPU，安全落在渲染线程回调里；返回前不碰场景，
    /// 所以 VM 不需要 DemoContext。建好之前参数 setter 只改字段，与旧页面 field==null 的行为一致。
    /// </summary>
    public (InstancedMesh LitField, InstancedMesh ColoredField) CreateFields()
    {
        litField = InstancedMesh.FromMesh(new Mesh
        {
            Name = "LitSource",
            Geometry = new BoxGeometry(0.55f, 0.55f, 0.55f),
            Material = LitMaterial(),
        });

        coloredField = InstancedMesh.FromMesh(new Mesh
        {
            Name = "ColoredSource",
            Geometry = new BoxGeometry(0.55f, 0.55f, 0.55f),
            Material = Shaders.InstanceColors("LightPass"),
        });

        Rebuild();

        return (litField, coloredField);
    }

    /// <summary>
    /// 逐帧波动（原 Update 的仿真部分）：时钟按波速推进后把这一帧的实例矩阵写下去；
    /// 关闭动画时早退——不推进也不请求重绘，与旧页 <c>if (!animate) return;</c> 同口径，
    /// 重绘留给按钮/开关经 <see cref="FrameRequested"/> 显式请求的那几帧。
    /// </summary>
    public void Update(double deltaTime)
    {
        if (!animate)
            return;

        clock += (float)(deltaTime * waveSpeed);

        WriteFrame(clock);

        FrameRequested?.Invoke();
    }

    /// <summary>页首说明。</summary>
    public IObservable<string?> IntroLabel => Strings.Instance.Instancing_Intro;

    /// <summary>每片实例数滑杆标签。</summary>
    public IObservable<string?> PerFieldLabel => Strings.Instance.Instancing_PerField;

    /// <summary>更新方式下拉标签。</summary>
    public IObservable<string?> UpdateModeLabel => Strings.Instance.Instancing_UpdateMode;

    /// <summary>波动动画开关标签。</summary>
    public IObservable<string?> WaveLabel => Strings.Instance.Instancing_Wave;

    /// <summary>波动速度滑杆标签。</summary>
    public IObservable<string?> WaveSpeedLabel => Strings.Instance.Instancing_WaveSpeed;

    /// <summary>起伏高度滑杆标签。</summary>
    public IObservable<string?> WaveHeightLabel => Strings.Instance.Instancing_WaveHeight;

    /// <summary>视锥剔除开关标签。</summary>
    public IObservable<string?> CullingLabel => Strings.Instance.Instancing_Culling;

    /// <summary>「摘掉左片前 200 个」按钮文案。</summary>
    public IObservable<string?> RemoveFrontLabel => Strings.Instance.Instancing_RemoveInstances;

    /// <summary>「恢复满量」按钮文案。</summary>
    public IObservable<string?> RestoreLabel => Strings.Instance.Instancing_Restore;

    /// <summary>读数行标题。</summary>
    public IObservable<string?> CostLabel => Strings.Instance.Instancing_Cost;

    /// <summary>每片实例数；改一次整场重铺（锚点网格与逐实例顶点色属性都按新集合重建）。</summary>
    public double InstanceCount
    {
        get => instanceCount;
        set
        {
            if (SetProperty(ref instanceCount, value))
                Rebuild();
        }
    }

    /// <summary>更新方式下拉的选中项；换模式立即按当前时钟重写一帧矩阵，代价差异立刻可见。</summary>
    public object? SelectedUpdateMode
    {
        get => selectedUpdateMode;
        set
        {
            if (value == null || !SetProperty(ref selectedUpdateMode, value))
                return;

            updateModeIndex = Math.Max(0, UpdateModeOptions.IndexOf(value));

            // 旧处理器这里不请求重绘：动画开着自然有下一帧，保持同一口径。
            WriteFrame(clock);
        }
    }

    /// <summary>是否播放波动动画；只改这里，<see cref="Update"/> 每帧来读。</summary>
    public bool Animate
    {
        get => animate;
        set => SetProperty(ref animate, value);
    }

    /// <summary>波动速度；换算成时钟步长在 <see cref="Update"/> 里做。</summary>
    public double WaveSpeed
    {
        get => waveSpeed;
        set => SetProperty(ref waveSpeed, value);
    }

    /// <summary>起伏高度；改完按当前时钟重写一帧矩阵（关闭动画时也能立刻看到新高度）。</summary>
    public double WaveAmount
    {
        get => waveAmount;
        set
        {
            if (!SetProperty(ref waveAmount, value))
                return;

            // 与旧处理器一致：只重写矩阵、不请求重绘。
            WriteFrame(clock);
        }
    }

    /// <summary>视锥剔除；写穿到两片实例场。初值 true 与引擎默认一致。</summary>
    public bool Culling
    {
        get => culling;
        set
        {
            if (!SetProperty(ref culling, value))
                return;

            if (litField != null)
                litField.EnableFrustumCulling = value;

            if (coloredField != null)
                coloredField.EnableFrustumCulling = value;

            FrameRequested?.Invoke();
        }
    }

    /// <summary>摘掉左片前 200 个实例；源生成 <c>RemoveFrontCommand</c> 供按钮绑定。</summary>
    [RelayCommand]
    private void RemoveFront()
    {
        if (litField == null)
            return;

        for (int i = 0; i < 200 && litField.InstanceCount > 0; i++)
            litField.RemoveInstance(0);

        ReadoutText.Refresh();
        FrameRequested?.Invoke();
    }

    /// <summary>恢复满量（整场重铺）；源生成 <c>RestoreCommand</c>。</summary>
    [RelayCommand]
    private void Restore() => Rebuild();

    /// <summary>
    /// 按当前实例数重铺两片：锚点排成近似方阵，左片整块 <c>SetInstances</c>，
    /// 右片随后挂逐实例顶点色——属性数组长度必须正好等于实例数，所以总是先立实例再挂属性。
    /// </summary>
    private void Rebuild()
    {
        anchors.Clear();

        var side = Math.Max(2, (int)Math.Ceiling(Math.Sqrt(instanceCount)));
        var step = FieldSize / side;

        for (int i = 0; i < (int)instanceCount; i++)
        {
            var x = (i % side) * step - FieldSize / 2f;
            var z = (i / side) * step - FieldSize / 2f;

            anchors.Add(new Vector3(x, 0f, z));
        }

        var colors = new List<Vector4>(anchors.Count);

        for (int i = 0; i < anchors.Count; i++)
        {
            var t = i / (float)Math.Max(anchors.Count - 1, 1);

            colors.Add(new Vector4(t, 1f - t, 0.35f + 0.5f * MathF.Sin(t * 14f), 1f));
        }

        leftTransforms.Clear();
        rightTransforms.Clear();

        if (litField != null)
            litField.SetInstances(Prepare(leftTransforms, LeftOffset, clock));

        if (coloredField != null)
        {
            coloredField.SetInstances(Prepare(rightTransforms, RightOffset, clock));
            coloredField.SetInstanceAttribute(BuildInVertexAttribute.Color_0, 4, colors);
        }

        ReadoutText.Refresh();
        FrameRequested?.Invoke();
    }

    /// <summary>
    /// 按当前模式把这一帧的实例矩阵写下去：批量整块重传，或只动前 <see cref="IncrementalSlice"/> 个。
    /// </summary>
    private void WriteFrame(float time)
    {
        if (updateModeIndex != 1)
        {
            if (litField != null)
                litField.SetInstances(Prepare(leftTransforms, LeftOffset, time));

            if (coloredField != null)
                coloredField.SetInstances(Prepare(rightTransforms, RightOffset, time));

            return;
        }

        if (litField != null)
            UpdateSome(litField, LeftOffset, time);

        if (coloredField != null)
            UpdateSome(coloredField, RightOffset, time);
    }

    private void UpdateSome(InstancedMesh field, Vector3 offset, float time)
    {
        var limit = Math.Min(IncrementalSlice, Math.Min(field.InstanceCount, anchors.Count));

        for (int i = 0; i < limit; i++)
            field.UpdateInstance(i, TransformAt(anchors[i] + offset, i, time));
    }

    private List<Matrix4x4> Prepare(List<Matrix4x4> buffer, Vector3 offset, float time)
    {
        buffer.Clear();

        for (int i = 0; i < anchors.Count; i++)
            buffer.Add(TransformAt(anchors[i] + offset, i, time));

        return buffer;
    }

    private Matrix4x4 TransformAt(Vector3 anchor, int index, float time)
    {
        var lift = (float)waveAmount * MathF.Sin(anchor.X * 0.42f + anchor.Z * 0.31f + time);

        var stretch = 1f + 0.7f * MathF.Sin(time * 0.8f + index * 0.02f);

        return Matrix4x4.CreateScale(1f, MathF.Max(0.25f, stretch), 1f)
               * Matrix4x4.CreateRotationY(time * 0.25f + index * 0.004f)
               * Matrix4x4.CreateTranslation(new Vector3(anchor.X, 0.45f + MathF.Max(0f, lift), anchor.Z));
    }

    private static Material LitMaterial()
    {
        var material = new Material();

        material.SetTexture("BaseColor", Procedural.Checker(128, 2));

        return material;
    }
}
