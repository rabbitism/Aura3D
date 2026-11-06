using Aura3D.Core.Nodes;
using Aura3D.Core.Geometries;
using Aura3D.Core.Resources;
using Aura3D.Gallery.Kit;
using Avalonia.Controls;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Aura3D.Gallery.Demos;

/// <summary>
/// 相机页的视图：参数与命令全在 <see cref="CameraViewModel"/>，XAML 经绑定直连。
/// 本类只剩生命周期——组装九个棋盘格盒子、把真相机与取景目标交给 VM、逐帧推进
/// 十字标记与屏幕投影读数。投影/视锥/背景等可绑参数的写穿逻辑见 VM。
/// </summary>
public sealed partial class CameraDemo : Demo
{
    private readonly CameraViewModel viewModel = new();

    private readonly List<Mesh> boxes = [];

    private Mesh? focusTarget;
    private Node? markerRoot;

    /// <summary>
    /// 建页：装配 XAML 并把 DataContext 换成 VM。仍须显式赋值——外壳继承下来的
    /// DataContext 优先于默认值；基类先设的「页面自身」在这里被 VM 顶替。
    /// </summary>
    /// <param name="context">宿主环境。</param>
    public CameraDemo(DemoContext context) : base(context)
    {
        InitializeComponent();

        DataContext = viewModel;

        viewModel.FrameRequested += () => Context.RequestFrame();
    }

    /// <inheritdoc />
    public override void BuildScene()
    {
        var scene = Context.Scene!;

        scene.ShowGrid = true;
        scene.ShowAxisGizmo = true;

        var light = new DirectionalLight { LightColor = System.Drawing.Color.White, Irradiance = 90000 };

        light.RotationDegrees = new Vector3(-50f, 30f, 0);

        scene.AddNode(light);

        var random = new Random(20260929);

        for (int i = 0; i < 9; i++)
        {
            var size = 0.6f + (float)random.NextDouble() * 1.4f;

            var box = new Mesh
            {
                Name = $"Box{i}",
                Geometry = new BoxGeometry(size, size, size),
                Material = new Material(),
            };

            box.Material.SetTexture("BaseColor", Procedural.Checker(128, 3));
            box.Position = new Vector3(
                (float)(random.NextDouble() - 0.5) * 9,
                size * 0.5f,
                (float)(random.NextDouble() - 0.5) * 9);
            box.RotationDegrees = new Vector3(0, (float)random.NextDouble() * 90, 0);

            scene.AddNode(box);
            boxes.Add(box);
        }

        focusTarget = boxes[4];
        markerRoot = boxes[0];

        // 相机从这里交给 VM：视锥与背景意图一次性写穿（等价旧页 BuildScene 末尾的 ApplyProjection），
        // 之后行控件经绑定直写这台相机。
        viewModel.AttachCamera(scene.MainCamera);
        viewModel.SetFocusTarget(focusTarget);

        scene.MainCamera.Position = new Vector3(7f, 6f, 9f);
        scene.MainCamera.LookAt(new Vector3(0, 1f, 0));
    }

    /// <inheritdoc />
    public override void Update(double deltaTime)
    {
        var scene = Context.Scene;
        var camera = scene?.MainCamera;

        if (camera == null || markerRoot == null)
            return;

        // 慢速自转保证即使没有输入也持续有帧，读数与十字标记才会跟着相机走。
        markerRoot.RotationDegrees = new Vector3(
            0,
            markerRoot.RotationDegrees.Y + 18f * (float)deltaTime,
            0);

        var screen = camera.WorldToScreen(markerRoot.Position + new Vector3(0, 1.6f, 0));

        if (screen == null)
        {
            Overlay.IsVisible = false;

            // 点在相机背后：没有坐标可摆，读数换成「相机背后」那句。
            viewModel.SetScreenProjection(null, camera.Width, camera.Height);
            viewModel.Tick();

            return;
        }

        var scale = camera.ScreenScale;

        Overlay.IsVisible = true;

        // WorldToScreen 返回的是设备像素，覆盖层用逻辑像素，所以除回 ScreenScale；
        // Y 轴方向由引擎负责，这里直接按左上角摆放标记。
        var point = screen.Value / scale;

        Canvas.SetLeft(Crosshair, point.X - 8);
        Canvas.SetTop(Crosshair, point.Y - 8);

        // 读数与十字标记共用同一份逻辑坐标，VM 负责按当前语言排成一句话。
        viewModel.SetScreenProjection(point, camera.Width, camera.Height);
        viewModel.Tick();
    }
}
