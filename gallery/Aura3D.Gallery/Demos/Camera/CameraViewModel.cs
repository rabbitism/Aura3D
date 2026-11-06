using Aura3D.Core.Nodes;
using Aura3D.Gallery.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;

namespace Aura3D.Gallery.Demos;

/// <summary>
/// 相机页的界面状态。分工：
/// <list type="bullet">
///   <item><description>文案：每个标签一个 Lingua observable，XAML 用 <c>{Binding Xxx^}</c> 订阅，
///     换语言时由绑定自动收到新值，不依赖外壳整页重挂。</description></item>
///   <item><description>参数：滑杆/开关/下拉双向绑定到这里的属性，setter 走 <see cref="ApplyProjection"/>
///     把视锥四参整组写穿到真相机——旧页每个事件处理末尾都调一次 ApplyProjection，
///     「改一项、重放全部」的语义逐行保留。</description></item>
///   <item><description>读数：屏幕投影这句话在「坐标模板」与「相机背后」两种形态间切换，
///     单一 format 模板表达不了，所以走普通 string 属性——视图逐帧喂原始值
///     （<see cref="SetScreenProjection"/>），<see cref="Tick"/> 按当前语言重排整句。</description></item>
///   <item><description>引擎模型：Scene.MainCamera 的可绑参数（投影方式、FOV、正交尺寸、裁剪面、
///     背景开关）加取景目标网格。真相机由 Scene 内部创建、BuildScene 才存在，构造期只存 UI 值；
///     相机控制器、十字标记等视图侧对象留在 View。</description></item>
/// </list>
/// 视图桥只有 <see cref="FrameRequested"/> 一个事件（参数变化要排一次重绘）；
/// VM 不碰 DemoContext，也不依赖渲染回调。
/// </summary>
public sealed partial class CameraViewModel : ObservableObject
{
    private Camera? camera;
    private Mesh? focusTarget;

    private ProjectionType projection = ProjectionType.Perspective;
    private object? selectedProjection;

    // 真相机要等 BuildScene 才出生，ctor 对齐的是「建场景那一刻将要写进它的那一份」初值，
    // 即旧页面滑杆上的 60 / 4 / 0.1 / 100——初值与引擎最终生效值一致，绑定初始化即对表。
    private double fieldOfView = 60;
    private double orthographicSize = 4;
    private double nearPlane = 0.1;
    private double farPlane = 100;

    private bool renderBackground = true;

    // 视图每帧推来的屏幕投影原始值（逻辑像素坐标与输出分辨率），Tick 用它们重排读数。
    private Vector2? screenPoint;
    private uint outputWidth;
    private uint outputHeight;

    private string screenReadoutText = string.Empty;

    /// <summary>
    /// 建好 VM：装配下拉选项并把选中项对齐到真正生效的投影方式（等价于旧页构造函数里
    /// 给 ProjectionRow.SelectedItem 赋初值）。
    /// </summary>
    public CameraViewModel()
    {
        ProjectionOptions = new List<ProjectionType>
        {
            ProjectionType.Perspective,
            ProjectionType.Orthographic,
        };

        selectedProjection = projection;
    }

    /// <summary>投影方式下拉的选项。</summary>
    public IList ProjectionOptions { get; }

    /// <summary>视锥或背景参数变化后请求一帧重绘；视图把它接到宿主的 RequestFrame 上。</summary>
    public event Action? FrameRequested;

    /// <summary>
    /// 建场景后由视图把真相机交进来：构造以来积攒的视锥与背景意图一次性落地
    /// （等价于旧页 BuildScene 末尾的那次 ApplyProjection），之后的 setter 都直写这台相机。
    /// </summary>
    public void AttachCamera(Camera mainCamera)
    {
        camera = mainCamera;

        ApplyProjection();

        // 背景开关若在场景建好前被拨过，旧页会把这次改动丢掉（当时只写 Context.Scene 上的相机，
        // null 时早退）；这里照 Particles 的做法补一次意图，开关与引擎态不再脱节。
        camera.IsRenderBackground = renderBackground;
    }

    /// <summary>建场景后由视图喂第 5 个盒子；「Fit 第 5 盒」按钮从此作用在它身上。</summary>
    public void SetFocusTarget(Mesh mesh) => focusTarget = mesh;

    /// <summary>视图逐帧推屏幕投影原始值；null 表示点在相机背后（WorldToScreen 返回 null）。</summary>
    public void SetScreenProjection(Vector2? screenLogical, uint width, uint height)
    {
        screenPoint = screenLogical;
        outputWidth = width;
        outputHeight = height;
    }

    /// <summary>逐帧调用：按当前语言重排读数，语言切换下一帧即生效（与旧页每帧重拼字符串一致）。</summary>
    public void Tick()
    {
        ScreenReadoutText = screenPoint is { } point
            ? Strings.Keys.Camera_ScreenReadout.Format(point.X, point.Y, outputWidth, outputHeight)
            : Strings.Keys.Camera_BehindCamera.T();
    }

    /// <summary>页首说明。</summary>
    public IObservable<string?> IntroLabel => Strings.Instance.Camera_Intro;

    /// <summary>投影方式下拉标签。</summary>
    public IObservable<string?> ProjectionLabel => Strings.Instance.Camera_Projection;

    /// <summary>「视锥参数」分组标题。</summary>
    public IObservable<string?> FrustumSectionLabel => Strings.Instance.Camera_Frustum;

    /// <summary>视场角标签。</summary>
    public IObservable<string?> FieldOfViewLabel => Strings.Instance.Camera_FieldOfView;

    /// <summary>正交尺寸标签。</summary>
    public IObservable<string?> OrthographicSizeLabel => Strings.Instance.Camera_OrthographicSize;

    /// <summary>近裁剪面标签。</summary>
    public IObservable<string?> NearPlaneLabel => Strings.Instance.Camera_NearPlane;

    /// <summary>远裁剪面标签。</summary>
    public IObservable<string?> FarPlaneLabel => Strings.Instance.Camera_FarPlane;

    /// <summary>「取景」分组标题。</summary>
    public IObservable<string?> FramingSectionLabel => Strings.Instance.Camera_Framing;

    /// <summary>「Fit 第 5 盒」按钮文案。</summary>
    public IObservable<string?> FitFifthBoxLabel => Strings.Instance.Camera_FitFifthBox;

    /// <summary>「回到默认机位」按钮文案。</summary>
    public IObservable<string?> ResetCameraLabel => Strings.Instance.Camera_ResetCamera;

    /// <summary>渲染背景开关标签。</summary>
    public IObservable<string?> RenderBackgroundLabel => Strings.Instance.Camera_RenderBackground;

    /// <summary>读数行标题。</summary>
    public IObservable<string?> Box0ProjectionLabel => Strings.Instance.Camera_Box0Projection;

    /// <summary>投影方式；写穿真相机并排一次重绘。</summary>
    public object? SelectedProjection
    {
        get => selectedProjection;
        set
        {
            if (value is not ProjectionType type || !SetProperty(ref selectedProjection, value))
                return;

            projection = type;

            ApplyProjection();
        }
    }

    /// <summary>透视视场角（度）；只对透视路径生效，正交下引擎忽略。</summary>
    public double FieldOfView
    {
        get => fieldOfView;
        set
        {
            if (SetProperty(ref fieldOfView, value))
                ApplyProjection();
        }
    }

    /// <summary>正交半高（世界单位）；只对正交路径生效，透视下引擎忽略。</summary>
    public double OrthographicSize
    {
        get => orthographicSize;
        set
        {
            if (SetProperty(ref orthographicSize, value))
                ApplyProjection();
        }
    }

    /// <summary>近裁剪面。</summary>
    public double NearPlane
    {
        get => nearPlane;
        set
        {
            if (SetProperty(ref nearPlane, value))
                ApplyProjection();
        }
    }

    /// <summary>远裁剪面。</summary>
    public double FarPlane
    {
        get => farPlane;
        set
        {
            if (SetProperty(ref farPlane, value))
                ApplyProjection();
        }
    }

    /// <summary>
    /// 渲染背景开关。相机没交进来前先记着（<see cref="AttachCamera"/> 会补写）；
    /// 引擎只认相机上的这个标志，排一帧重绘让清屏逻辑立刻按新值走。
    /// </summary>
    public bool RenderBackground
    {
        get => renderBackground;
        set
        {
            if (!SetProperty(ref renderBackground, value))
                return;

            if (camera != null)
                camera.IsRenderBackground = value;

            FrameRequested?.Invoke();
        }
    }

    /// <summary>屏幕投影读数；由 <see cref="Tick"/> 重排，首个 Update 前为空串。</summary>
    public string ScreenReadoutText
    {
        get => screenReadoutText;
        private set => SetProperty(ref screenReadoutText, value);
    }

    /// <summary>
    /// 把相机框到取景目标上；源生成 <c>FitFifthBoxCommand</c> 供按钮绑定。
    /// Fit 自己会按包围盒推一套裁剪面，随后照旧整组重放 UI 视锥参数——旧页就是这个顺序，
    /// Fit 算出的裁剪面随即被滑杆值盖掉，最终只有位置与朝向生效。
    /// </summary>
    [RelayCommand]
    private void FitFifthBox()
    {
        if (focusTarget?.BoundingBox is not { } box || camera == null)
            return;

        camera.FitToBoundingBox(box, 0.25f);

        ApplyProjection();
    }

    /// <summary>
    /// 回到默认机位；源生成 <c>ResetCameraCommand</c>。本页靠盒子自转持续有帧，
    /// 与旧页一致不额外请求重绘。
    /// </summary>
    [RelayCommand]
    private void ResetCamera()
    {
        if (camera == null)
            return;

        camera.Position = new Vector3(7, 6, 9);
        camera.LookAt(new Vector3(0, 1, 0));
    }

    /// <summary>
    /// 视锥四参整组写穿。旧页每个滑杆事件处理末尾都调它，改一项也重放全部，这里保持同样口径；
    /// 引擎对 near ≥ far 会抛参数异常（旧页同样如此——滑杆范围本就允许拖出这种组合），
    /// 相机未交进来时早退，意图留在字段里等 <see cref="AttachCamera"/> 落地。
    /// </summary>
    private void ApplyProjection()
    {
        if (camera == null)
            return;

        camera.ProjectionType = projection;
        camera.FieldOfView = (float)fieldOfView;
        camera.OrthographicSize = (float)orthographicSize;
        camera.SetClippingPlanes((float)nearPlane, (float)farPlane);

        FrameRequested?.Invoke();
    }
}
