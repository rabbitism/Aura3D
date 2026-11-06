using Aura3D.Core.Geometries;
using Aura3D.Core.Nodes;
using Aura3D.Core.Particles;
using Aura3D.Core.Resources;
using Aura3D.Gallery.Kit;
using Aura3D.Gallery.Localization;
using Aura3D.Gallery.Mvvm;
using Irihi.Lingua;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Windows.Input;
using Color = System.Drawing.Color;

namespace Aura3D.Gallery.Demos;

/// <summary>
/// 粒子页的界面状态，MVVM 化试点。分工：
/// <list type="bullet">
///   <item><description>文案：每个标签一个 Lingua observable，XAML 用 <c>{Binding Xxx^}</c> 订阅，
///     换语言时由绑定自动收到新值，不依赖外壳整页重挂。</description></item>
///   <item><description>参数：滑杆/开关/下拉双向绑定到这里的属性，setter 写穿到发射器对象；
///     初值即发射器里真正生效的值，旧页面构造函数里那批「行控件写回」由绑定初始化取代。</description></item>
///   <item><description>Readout：Lingua 1.1 的 format builder 组装，占位符全是计算参数，
///     视图逐帧调 <see cref="Tick"/> 重读，语言切换时模板本身也会自动换。</description></item>
///   <item><description>引擎模型：三个 <see cref="ParticleEmitter"/>（含程序化贴图）住在这里，
///     视图只负责生命周期——取资产喂 <see cref="SetFireSheet"/>、建场景后登记粒子系统、逐帧驱动。</description></item>
/// </list>
/// 视图桥只有两个事件：<see cref="FrameRequested"/>（贴图/混合模式变化要排一次重绘）与
/// <see cref="ShowBoundsToggled"/>（调试开关写在宿主的 PipelineSettings 上，VM 不碰 DemoContext）。
/// </summary>
public sealed class ParticlesViewModel : ViewModelBase
{
    // 程序化贴图是纯 CPU 资源，构造期就能生成。
    private static readonly Texture SoftDot = Procedural.SoftDot(128, 2.4f);
    private static readonly Texture Spark = Procedural.Spark(64, 64);
    private static readonly Texture ChunkSkin = Procedural.Checker(64, 2);

    private static readonly LinguaKey[] TextureKeys =
        [Strings.Keys.Particles_TextureSoftDot, Strings.Keys.Particles_TextureSpark, Strings.Keys.Particles_TextureFlame];

    /// <summary>
    /// 建好 VM：对齐参数初值、接好命令，再把 Readout 的模板与计算参数装配起来。
    /// </summary>
    public ParticlesViewModel()
    {
        TextureOptions = TextureKeys.Select(k => k.T()).ToList();
        BlendModeOptions = new List<BlendMode>
        {
            BlendMode.Opaque,
            BlendMode.Masked,
            BlendMode.Translucent,
        };

        // 参数初值取发射器里真正生效的那一份（等价于旧页面把行控件初值写回）。
        fountainRate = FountainEmitter.EmissionRate;
        fountainConeAngle = FountainEmitter.ConeAngle;
        fountainGravityY = FountainEmitter.Gravity.Y;
        fountainDamping = FountainEmitter.Damping;
        fountainStartSize = FountainEmitter.StartSize.Max;
        flipbookTiles = 8;
        burstRate = BurstEmitter.EmissionRate;
        burstDuration = BurstEmitter.Duration;
        burstLifetime = BurstEmitter.Lifetime.Max;
        burstVelocity = BurstEmitter.Velocity.Max.Y;
        debrisRate = DebrisEmitter.EmissionRate;
        debrisSpin = DebrisEmitter.AngularVelocity.Max;
        debrisMeshScale = DebrisEmitter.MeshScale;
        debrisMaxParticles = DebrisEmitter.MaxParticles;

        selectedTexture = TextureOptions[textureIndex];
        selectedBlendMode = FountainEmitter.BlendMode;

        ReplayBurstCommand = new RelayCommand(() =>
        {
            burst?.Stop();
            burst?.Play();
        });

        ApplyDebrisCommand = new RelayCommand(() =>
        {
            debris?.Stop();
            debris?.Play();
        });

        RestartAllCommand = new RelayCommand(() =>
        {
            foreach (var system in new[] { fountain, burst, debris })
            {
                system?.Stop();
                system?.Play();
            }
        });

        // Readout 模板 {0}..{10}：存活数一类是逐帧读的计算参数，渲染路径与贴图名在切换时才变，
        // 尾部 {10} 是「无贴图」提示——有贴图时是空串，等价于旧代码的字符串拼接。
        ReadoutText = Strings.Keys.Particles_Readout.CreateFormat()
            .Arg(() => (fountain?.ActiveCount ?? 0) + (burst?.ActiveCount ?? 0) + (debris?.ActiveCount ?? 0))
            .Arg(() => fountain?.ActiveCount ?? 0)
            .Arg(() => burst?.ActiveCount ?? 0)
            .Arg(() => BurstEmitter.IsFinished)
            .Arg(() => debris?.ActiveCount ?? 0)
            .Arg(() => DebrisEmitter.UseMeshRenderer ? "InstancedMesh" : Strings.Keys.Particles_RendererBillboard.T())
            .Arg(() => fountain?.IsPlaying ?? false)
            .Arg(() => (string)TextureOptions[textureIndex]!)
            .Arg(() => FountainEmitter.FlipbookTiles.X)
            .Arg(() => FountainEmitter.FlipbookTiles.Y)
            .Arg(() => FountainEmitter.Texture == null ? Strings.Keys.Particles_NoTextureHint.T() : string.Empty)
            .Build();
    }

    /// <summary>场景建好后由视图登记三个粒子系统，命令与开关从此直接作用于它们。</summary>
    public void AttachSystems(ParticleSystem fountainSystem, ParticleSystem burstSystem, ParticleSystem debrisSystem)
    {
        fountain = fountainSystem;
        burst = burstSystem;
        debris = debrisSystem;
    }

    /// <summary>资产阶段取到的火焰翻页图；在此之前构造期贴图回落到程序化圆点。</summary>
    public void SetFireSheet(Texture sheet) => fireSheet = sheet;

    /// <summary>逐帧调用：重读 Readout 的计算参数（存活数、播放状态）。</summary>
    public void Tick() => ReadoutText.Refresh();

    /// <summary>贴图或混合模式变化后请求一帧重绘；视图把它接到宿主的 RequestFrame 上。</summary>
    public event Action? FrameRequested;

    /// <summary>「画粒子体积」开关变化；宿主的调试设置只有视图够得着。</summary>
    public event Action<bool>? ShowBoundsToggled;

    /// <summary>逐帧读数。语言切换时模板换新，绑定自动收到重排后的整句。</summary>
    public LinguaFormatObservable ReadoutText { get; }

    /// <summary>贴图下拉的选项文案；当前语言的一份快照，随页面重建。</summary>
    public IList TextureOptions { get; }

    /// <summary>混合模式下拉的选项。</summary>
    public IList BlendModeOptions { get; }

    /// <summary>重放一次性爆发。</summary>
    public ICommand ReplayBurstCommand { get; }

    /// <summary>按新的 MaxParticles 重开碎块系统。</summary>
    public ICommand ApplyDebrisCommand { get; }

    /// <summary>三个系统全部 Stop→Play。</summary>
    public ICommand RestartAllCommand { get; }

    /// <summary>喷泉发射器：公告板路径的模型对象，双向属性写穿到这里。</summary>
    public ParticleEmitter FountainEmitter { get; } = new()
    {
        Shape = EmissionShape.Cone,
        ConeAngle = 17f,
        ShapeSize = new Vector3(0.3f, 0.3f, 0.3f),
        EmissionRate = 900,
        Lifetime = new RangeFloat(1.1f, 1.9f),
        Velocity = new RangeVector3(new Vector3(-0.4f, 6.4f, -0.4f), new Vector3(0.4f, 8.2f, 0.4f)),
        StartSize = new RangeFloat(0.45f, 0.8f),
        EndSize = new RangeFloat(0.02f, 0.06f),
        StartColor = Color.Goldenrod,
        EndColor = Color.FromArgb(0, 255, 120, 30),
        Gravity = new Vector3(0, -7.5f, 0),
        Damping = 0.35f,
        Texture = SoftDot,
        BlendMode = BlendMode.Translucent,
        MaxParticles = 4000,
    };

    /// <summary>爆发发射器：一次性路径（Looping=false + Duration）。</summary>
    public ParticleEmitter BurstEmitter { get; } = new()
    {
        Shape = EmissionShape.SphereSurface,
        ShapeSize = new Vector3(0.35f, 0.35f, 0.35f),
        EmissionRate = 24000,
        Looping = false,
        Duration = 0.18f,
        Lifetime = new RangeFloat(1.4f, 2.6f),
        Velocity = new RangeVector3(new Vector3(-4.5f, 1.5f, -4.5f), new Vector3(4.5f, 7f, 4.5f)),
        StartSize = new RangeFloat(0.3f, 0.55f),
        EndSize = new RangeFloat(0f, 0.04f),
        StartColor = Color.White,
        EndColor = Color.FromArgb(0, 90, 140, 255),
        AngularVelocity = new RangeFloat(-2.4f, 2.4f),
        Gravity = new Vector3(0, -4f, 0),
        Texture = Spark,
        BlendMode = BlendMode.Translucent,
        MaxParticles = 2000,
    };

    /// <summary>碎块发射器：网格模式（Mesh 非空时引擎改用 InstancedMesh 绘制）。</summary>
    public ParticleEmitter DebrisEmitter { get; } = new()
    {
        Shape = EmissionShape.Box,
        ShapeSize = new Vector3(1.6f, 0.4f, 1.6f),
        EmissionRate = 90,
        Lifetime = new RangeFloat(2.5f, 4f),
        Velocity = new RangeVector3(new Vector3(-1.2f, -0.4f, -1.2f), new Vector3(1.2f, 1.4f, 1.2f)),
        StartSize = new RangeFloat(0.5f, 1f),
        EndSize = new RangeFloat(0.5f, 1f),
        Rotation = new RangeFloat(0f, 3.14f),
        AngularVelocity = new RangeFloat(-3f, 3f),
        Gravity = new Vector3(0, -12f, 0),
        Mesh = new Mesh
        {
            Name = "DebrisChunk",
            Geometry = new BoxGeometry(0.3f, 0.3f, 0.3f),
            Material = ChunkMaterial(),
        },
        MeshScale = 1f,
        MaxParticles = 600,
    };

    private ParticleSystem? fountain;
    private ParticleSystem? burst;
    private ParticleSystem? debris;

    // 512×512 的火焰翻页图（8×8 共 64 帧），资产阶段由视图喂进来。
    private Texture? fireSheet;

    private int textureIndex = 2;
    private object? selectedTexture;
    private object? selectedBlendMode;

    private double fountainRate;
    private double fountainConeAngle;
    private double fountainGravityY;
    private double fountainDamping;
    private double fountainStartSize;
    private double flipbookTiles;
    private double burstRate;
    private double burstDuration;
    private double burstLifetime;
    private double burstVelocity;
    private double debrisRate;
    private double debrisSpin;
    private double debrisMeshScale;
    private double debrisMaxParticles;

    private bool pauseAll;
    private bool culling;
    private bool showBounds = true;

    /// <summary>页首说明。</summary>
    public IObservable<string?> IntroLabel => Strings.Keys.Particles_Intro.Observe();

    /// <summary>「喷泉」分组标题。</summary>
    public IObservable<string?> FountainSectionLabel => Strings.Keys.Particles_Fountain.Observe();

    /// <summary>「爆发」分组标题。</summary>
    public IObservable<string?> BurstSectionLabel => Strings.Keys.Particles_Burst.Observe();

    /// <summary>「碎块」分组标题。</summary>
    public IObservable<string?> DebrisSectionLabel => Strings.Keys.Particles_Debris.Observe();

    /// <summary>「三者共享」分组标题。</summary>
    public IObservable<string?> SharedSectionLabel => Strings.Keys.Particles_Shared.Observe();

    /// <summary>喷泉发射率标签。</summary>
    public IObservable<string?> FountainRateLabel => Strings.Keys.Particles_FountainRate.Observe();

    /// <summary>锥形角标签。</summary>
    public IObservable<string?> FountainConeAngleLabel => Strings.Keys.Particles_ConeAngle.Observe();

    /// <summary>重力 Y 标签。</summary>
    public IObservable<string?> FountainGravityLabel => Strings.Keys.Particles_GravityY.Observe();

    /// <summary>阻尼标签。</summary>
    public IObservable<string?> FountainDampingLabel => Strings.Keys.Particles_Damping.Observe();

    /// <summary>起始尺寸标签。</summary>
    public IObservable<string?> FountainStartSizeLabel => Strings.Keys.Particles_StartSize.Observe();

    /// <summary>贴图下拉标签。</summary>
    public IObservable<string?> TextureLabel => Strings.Keys.Particles_Map.Observe();

    /// <summary>翻页格数标签。</summary>
    public IObservable<string?> FlipbookTilesLabel => Strings.Keys.Particles_FlipbookTiles.Observe();

    /// <summary>翻页说明。</summary>
    public IObservable<string?> FlipbookNoteLabel => Strings.Keys.Particles_FlipbookNote.Observe();

    /// <summary>爆发发射率标签。</summary>
    public IObservable<string?> BurstRateLabel => Strings.Keys.Particles_BurstRate.Observe();

    /// <summary>爆发时长标签。</summary>
    public IObservable<string?> BurstDurationLabel => Strings.Keys.Particles_Duration.Observe();

    /// <summary>寿命上限标签。</summary>
    public IObservable<string?> BurstLifetimeLabel => Strings.Keys.Particles_MaxLife.Observe();

    /// <summary>初速上限标签。</summary>
    public IObservable<string?> BurstVelocityLabel => Strings.Keys.Particles_MaxSpeed.Observe();

    /// <summary>「再放一次」按钮文案。</summary>
    public IObservable<string?> ReplayBurstLabel => Strings.Keys.Particles_Replay.Observe();

    /// <summary>碎块发射率标签。</summary>
    public IObservable<string?> DebrisRateLabel => Strings.Keys.Particles_DebrisRate.Observe();

    /// <summary>自转速度标签。</summary>
    public IObservable<string?> DebrisSpinLabel => Strings.Keys.Particles_SpinSpeed.Observe();

    /// <summary>网格缩放标签。</summary>
    public IObservable<string?> DebrisMeshScaleLabel => Strings.Keys.Particles_MeshScale.Observe();

    /// <summary>单发射器上限标签。</summary>
    public IObservable<string?> DebrisMaxParticlesLabel => Strings.Keys.Particles_MaxParticles.Observe();

    /// <summary>「让上限生效」按钮文案。</summary>
    public IObservable<string?> ApplyDebrisLabel => Strings.Keys.Particles_ApplyMaxParticles.Observe();

    /// <summary>混合模式下拉标签。</summary>
    public IObservable<string?> BlendModeLabel => Strings.Keys.Particles_BlendMode.Observe();

    /// <summary>「全部重开」按钮文案。</summary>
    public IObservable<string?> RestartAllLabel => Strings.Keys.Particles_RestartAll.Observe();

    /// <summary>「Pause 全部」开关标签。</summary>
    public IObservable<string?> PauseAllLabel => Strings.Keys.Particles_PauseAll.Observe();

    /// <summary>按体积剔除开关标签。</summary>
    public IObservable<string?> CullingLabel => Strings.Keys.Particles_VisibilityCulling.Observe();

    /// <summary>画粒子体积开关标签。</summary>
    public IObservable<string?> ShowBoundsLabel => Strings.Keys.Particles_ShowBounds.Observe();

    /// <summary>读数行标题。</summary>
    public IObservable<string?> StateLabel => Strings.Keys.Particles_State.Observe();

    /// <summary>喷泉发射率（个/秒）。</summary>
    public double FountainRate
    {
        get => fountainRate;
        set
        {
            if (Set(ref fountainRate, value))
                FountainEmitter.EmissionRate = (float)value;
        }
    }

    /// <summary>锥形张角（度）。</summary>
    public double FountainConeAngle
    {
        get => fountainConeAngle;
        set
        {
            if (Set(ref fountainConeAngle, value))
                FountainEmitter.ConeAngle = (float)value;
        }
    }

    /// <summary>重力 Y 分量；X/Z 由发射器约定恒为 0。</summary>
    public double FountainGravityY
    {
        get => fountainGravityY;
        set
        {
            if (Set(ref fountainGravityY, value))
                FountainEmitter.Gravity = new Vector3(0, (float)value, 0);
        }
    }

    /// <summary>阻尼。</summary>
    public double FountainDamping
    {
        get => fountainDamping;
        set
        {
            if (Set(ref fountainDamping, value))
                FountainEmitter.Damping = (float)value;
        }
    }

    /// <summary>起始尺寸上限；下限按 0.6 倍跟随，维持原有的区间形状。</summary>
    public double FountainStartSize
    {
        get => fountainStartSize;
        set
        {
            if (!Set(ref fountainStartSize, value))
                return;

            var max = (float)value;

            FountainEmitter.StartSize = new RangeFloat(max * 0.6f, max);
        }
    }

    /// <summary>贴图下拉的选中项；换贴图会连带重设翻页格数（程序化图只有一帧）。</summary>
    public object? SelectedTexture
    {
        get => selectedTexture;
        set
        {
            if (value == null || !Set(ref selectedTexture, value))
                return;

            textureIndex = Math.Max(0, TextureOptions.IndexOf(value));

            ApplyBillboard();
            FrameRequested?.Invoke();
        }
    }

    /// <summary>翻页格数（每边）；按整数生效。</summary>
    public double FlipbookTiles
    {
        get => flipbookTiles;
        set
        {
            if (!Set(ref flipbookTiles, Math.Round(value)))
                return;

            ApplyBillboard();
            FrameRequested?.Invoke();
        }
    }

    /// <summary>爆发发射率（个/秒）。</summary>
    public double BurstRate
    {
        get => burstRate;
        set
        {
            if (Set(ref burstRate, value))
                BurstEmitter.EmissionRate = (float)value;
        }
    }

    /// <summary>爆发时长（秒，Looping=false 时读）。</summary>
    public double BurstDuration
    {
        get => burstDuration;
        set
        {
            if (Set(ref burstDuration, value))
                BurstEmitter.Duration = (float)value;
        }
    }

    /// <summary>寿命上限；下限保持不变。</summary>
    public double BurstLifetime
    {
        get => burstLifetime;
        set
        {
            if (Set(ref burstLifetime, value))
                BurstEmitter.Lifetime = new RangeFloat(BurstEmitter.Lifetime.Min, (float)value);
        }
    }

    /// <summary>初速上限；水平与竖直按同一上限对称展开。</summary>
    public double BurstVelocity
    {
        get => burstVelocity;
        set
        {
            if (!Set(ref burstVelocity, value))
                return;

            var max = (float)value;

            BurstEmitter.Velocity = new RangeVector3(
                new Vector3(-max, 1.5f, -max),
                new Vector3(max, max, max));
        }
    }

    /// <summary>碎块发射率（个/秒）。</summary>
    public double DebrisRate
    {
        get => debrisRate;
        set
        {
            if (Set(ref debrisRate, value))
                DebrisEmitter.EmissionRate = (float)value;
        }
    }

    /// <summary>自转速度上限；角速度区间按 ±上限对称。</summary>
    public double DebrisSpin
    {
        get => debrisSpin;
        set
        {
            if (!Set(ref debrisSpin, value))
                return;

            var max = (float)value;

            DebrisEmitter.AngularVelocity = new RangeFloat(-max, max);
        }
    }

    /// <summary>碎块网格缩放。</summary>
    public double DebrisMeshScale
    {
        get => debrisMeshScale;
        set
        {
            if (Set(ref debrisMeshScale, value))
                DebrisEmitter.MeshScale = (float)value;
        }
    }

    /// <summary>单发射器粒子上限；要重开系统才生效（Play 按它开数组）。</summary>
    public double DebrisMaxParticles
    {
        get => debrisMaxParticles;
        set
        {
            if (Set(ref debrisMaxParticles, value))
                DebrisEmitter.MaxParticles = (int)value;
        }
    }

    /// <summary>混合模式；三个系统里公告板路径的两个一起改。</summary>
    public object? SelectedBlendMode
    {
        get => selectedBlendMode;
        set
        {
            if (value is not BlendMode mode || !Set(ref selectedBlendMode, value))
                return;

            FountainEmitter.BlendMode = mode;
            BurstEmitter.BlendMode = mode;

            FrameRequested?.Invoke();
        }
    }

    /// <summary>暂停/恢复全部系统。</summary>
    public bool PauseAll
    {
        get => pauseAll;
        set
        {
            if (!Set(ref pauseAll, value))
                return;

            foreach (var system in new[] { fountain, burst, debris })
            {
                if (value)
                    system?.Pause();
                else
                    system?.Play();
            }
        }
    }

    /// <summary>按包围体积剔除。</summary>
    public bool Culling
    {
        get => culling;
        set
        {
            if (!Set(ref culling, value))
                return;

            foreach (var system in new[] { fountain, burst, debris })
            {
                if (system != null)
                    system.EnableVisibilityCulling = value;
            }
        }
    }

    /// <summary>画粒子包围体积。</summary>
    public bool ShowBounds
    {
        get => showBounds;
        set
        {
            if (Set(ref showBounds, value))
                ShowBoundsToggled?.Invoke(value);
        }
    }

    /// <summary>
    /// 把贴图与翻页格数一起落到喷泉发射器：程序化图只有一帧，选它们时强制 1×1，
    /// 否则会被切成拼贴块。建场景时由视图先调一次，把资产阶段取到的火焰图接上。
    /// </summary>
    public void ApplyBillboard()
    {
        FountainEmitter.Texture = textureIndex switch
        {
            1 => Spark,
            2 => fireSheet,
            _ => SoftDot,
        };

        FountainEmitter.FlipbookTiles = textureIndex == 2
            ? new Vector2((float)flipbookTiles, (float)flipbookTiles)
            : Vector2.One;
    }

    private static Material ChunkMaterial()
    {
        var material = new Material();

        material.SetTexture("BaseColor", ChunkSkin);

        return material;
    }
}
