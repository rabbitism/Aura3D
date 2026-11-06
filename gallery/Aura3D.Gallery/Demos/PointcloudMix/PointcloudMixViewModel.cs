using Aura3D.Core.Geometries;
using Aura3D.Core.Nodes;
using Aura3D.Core.Resources;
using Aura3D.Gallery.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using Irihi.Lingua;
using System;
using System.Collections.Generic;
using Color = System.Drawing.Color;

namespace Aura3D.Gallery.Demos;

/// <summary>
/// 点云混合页的界面状态，MVVM 化（照 Particles 模式）。分工：
/// <list type="bullet">
///   <item><description>文案：AXAML 直接写 <c>{Translate}</c> 标记扩展取 Strings.Keys 键值；换语言走外壳既有的整页重挂。</description></item>
///   <item><description>参数：滑杆/开关双向绑定到这里的属性，setter 写穿到点云材质的三个普通参数
///     （uPointSize / uUnlit / uUseVertexColor）；初值即材质里真正生效的值，
///     旧页面 XAML 里写死的 <c>Value="4"</c>/<c>IsChecked="True"</c> 由绑定初始化取代。</description></item>
///   <item><description>引擎模型：点云材质与点云网格（斐波那契球壳几何）住在这里，
///     视图只负责生命周期——取桌子资产、建场景后把网格摆进场景。</description></item>
/// </list>
/// 视图桥只有一个事件：<see cref="FrameRequested"/>（材质参数被改后要排一次重绘），
/// VM 不碰 DemoContext。本页是 Issue #21 的对照页：不写任何着色器覆盖，
/// 参数由 RenderPass 按图元类型与设备上限落到着色器，管线保持可切换。
/// </summary>
public sealed partial class PointcloudMixViewModel : ObservableObject
{
    /// <summary>程序化点云的点数；xyz/rgb 的形状就是 .ply 点云的形状。</summary>
    private const int PointCount = 6000;

    /// <summary>
    /// 建 VM：先把点云网格挂到材质上（字段初始化器引用不了实例属性，只能在构造器里建），
    /// 参数初值再从材质里真正生效的那一份读回（等价于旧页面 XAML 写死的行控件初值）。
    /// 材质与网格都是纯 CPU 声明，构造期即可建好。
    /// </summary>
    public PointcloudMixViewModel()
    {
        Pointcloud = new Mesh
        {
            Name = "Pointcloud",
            Geometry = BuildPointSphere(PointCount),
            Material = PointMaterial,
        };

        PointMaterial.TryGetParameterValue(Material.PointSizeParameterName, out float pointSizeValue);
        PointMaterial.TryGetParameterValue(Material.UnlitParameterName, out float unlitValue);
        PointMaterial.TryGetParameterValue(Material.UseVertexColorParameterName, out float vertexColorValue);

        pointSize = pointSizeValue;
        unlit = unlitValue != 0f;
        useVertexColor = vertexColorValue != 0f;
    }

    /// <summary>材质参数被改后请求一帧重绘；视图把它接到宿主的 InvalidateRender 上。</summary>
    public event Action? FrameRequested;

    /// <summary>点云材质：三个参数就是本页的全部秘密，对比 PrimitivesDemo 里 NoLight 管线
    /// 的 Shaders.Points 覆盖——这里是引擎默认路径，所以不锁管线也能出图。</summary>
    public Material PointMaterial { get; } = CreatePointMaterial();

    /// <summary>悬浮在桌子上方的点云网格；几何是纯 CPU 的顶点数组，位置由视图建场景时摆。</summary>
    public Mesh Pointcloud { get; }

    private double pointSize;
    private bool unlit;
    private bool useVertexColor;

    /// <summary>点尺寸（像素）。引擎在点图元上写 gl_PointSize，由设备上限截断。</summary>
    public double PointSize
    {
        get => pointSize;
        set
        {
            if (!SetProperty(ref pointSize, value))
                return;

            PointMaterial.SetParameterValue(Material.PointSizeParameterName, (float)value);
            FrameRequested?.Invoke();
        }
    }

    /// <summary>无光照。关掉后点云会按 fallback 法线（0,1,0）参与光照——正好演示为什么点云默认 unlit。</summary>
    public bool Unlit
    {
        get => unlit;
        set
        {
            if (!SetProperty(ref unlit, value))
                return;

            PointMaterial.SetParameterValue(Material.UnlitParameterName, value ? 1f : 0f);
            FrameRequested?.Invoke();
        }
    }

    /// <summary>顶点色。关掉后彩虹色退回材质 BaseColor（纯白），只剩明暗差异。</summary>
    public bool UseVertexColor
    {
        get => useVertexColor;
        set
        {
            if (!SetProperty(ref useVertexColor, value))
                return;

            PointMaterial.SetParameterValue(Material.UseVertexColorParameterName, value ? 1f : 0f);
            FrameRequested?.Invoke();
        }
    }

    /// <summary>
    /// 建点云材质：纯白 BaseColor 兜底 + 三个参数的默认值，与旧页面 BuildScene 里
    /// 逐行写的那份完全一致。
    /// </summary>
    private static Material CreatePointMaterial()
    {
        var material = new Material();

        material.SetTexture("BaseColor", Texture.CreateFromColor(Color.White));
        material.SetParameterValue(Material.UseVertexColorParameterName, 1f);
        material.SetParameterValue(Material.UnlitParameterName, 1f);
        material.SetParameterValue(Material.PointSizeParameterName, 4f);

        return material;
    }

    /// <summary>
    /// 斐波那契球壳：均匀分布的 xyz + 沿序号铺开的 HSV 彩虹 rgb，就是 .ply 点云的形状。
    /// </summary>
    private static Geometry BuildPointSphere(int count)
    {
        var positions = new List<float>(count * 3);
        var colors = new List<float>(count * 4);

        // 黄金角 ≈ 2.39996323（π(3-√5)），保证球壳上的点分布均匀。
        const float goldenAngle = 2.39996323f;

        for (var i = 0; i < count; i++)
        {
            var y = 1f - 2f * i / (count - 1);
            var radius = MathF.Sqrt(Math.Max(0f, 1f - y * y));
            var theta = goldenAngle * i;

            positions.Add(MathF.Cos(theta) * radius);
            positions.Add(y);
            positions.Add(MathF.Sin(theta) * radius);

            var (r, g, b) = HsvToRgb(i % 360 / 360f, 0.85f, 1f);

            colors.Add(r);
            colors.Add(g);
            colors.Add(b);
            colors.Add(1f);
        }

        var geometry = new Geometry
        {
            PrimitiveType = PrimitiveType.Points,
        };

        geometry.SetVertexAttribute(BuildInVertexAttribute.Position, 3, positions);
        geometry.SetVertexAttribute(BuildInVertexAttribute.Color_0, 4, colors);

        return geometry;
    }

    /// <summary>HSV 转 RGB，彩虹顶点色用。</summary>
    private static (float R, float G, float B) HsvToRgb(float h, float s, float v)
    {
        var sector = (int)(h * 6f);
        var fraction = h * 6f - sector;
        var p = v * (1f - s);
        var q = v * (1f - fraction * s);
        var t = v * (1f - (1f - fraction) * s);

        return sector switch
        {
            0 => (v, t, p),
            1 => (q, v, p),
            2 => (p, v, t),
            3 => (p, q, v),
            4 => (t, p, v),
            _ => (v, p, q),
        };
    }
}
