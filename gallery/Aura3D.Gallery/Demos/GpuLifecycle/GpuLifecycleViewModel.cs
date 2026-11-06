using Aura3D.Gallery.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Irihi.Lingua;
using System;
using System.Collections.Generic;

namespace Aura3D.Gallery.Demos;

/// <summary>
/// 生命周期页的界面状态。分工：
/// <list type="bullet">
///   <item><description>文案：AXAML 直接写 <c>{Translate}</c> 标记扩展取 Strings.Keys 键值；换语言走外壳既有的整页重挂。</description></item>
///   <item><description>状态读数：帧号、五个场景事件各自的计数、最慢帧与「最近一次动作」住在这里，
///     由视图逐帧喂进来的事实（<see cref="Tick"/>）与生命周期回调的记账（<see cref="RecordSceneInitialized"/>
///     一族）推进；运行状态行是 11 占位符的 Lingua format，事件日志是普通字符串属性。</description></item>
///   <item><description>动作按钮：全部源生成 <c>[RelayCommand]</c>。动真格 GPU 的三条（释放显存/
///     模拟丢失/销毁场景）只在这里记动作名，视图 API 与排帧经语义事件桥回视图执行——
///     VM 不碰 DemoContext，GL 请求也不能从命令里直接发。</description></item>
/// </list>
/// 本页没有可调参数与引擎模型：场景（转台与四个方块）是纯布景，留在视图的 BuildScene 里；
/// 上下文恢复时引擎自行重建全部 GL 句柄，页面侧没有自缓存句柄要补活，所以 VM 没有恢复入口，
/// 恢复调度（若有）永远属于视图的生命周期。
/// </summary>
public sealed partial class GpuLifecycleViewModel : ObservableObject
{
    private readonly List<string> lines = [];

    private int frames;
    private int initializedCount;
    private int updatedCount;
    private int lostCount;
    private int restoredCount;
    private int destroyedCount;

    private double worstFrame;

    // 三个状态位只在 Tick 里被赋值：模板的 Arg 每次刷新时重读字段，首帧之前是 null，
    // 显示上与旧 Report 里的 view?./pipeline?. 一致。
    private bool? contextLost;
    private bool? pipelineInitialized;
    private bool? pipelineDestroyed;

    // 动作名是「最近一次干了什么」的尾注：三个 API 名保持英文原文（界面上本来就指名道姓），
    // 其余走文案表，换语言时由下一次刷新带出。
    private string lastAction = Strings.Keys.GpuLifecycle_ActionInitial.T();

    private bool continuousRender = true;

    // 日志行初值与 ReadoutRow 的控件默认一致，首条事件之前显示同旧页面。
    private string logText = "—";

    /// <summary>
    /// 建好 VM：把运行状态行的模板与 11 个计算参数装配起来；开关初值 true 与引擎的
    /// <c>AutoRequestNextFrameRendering</c> 默认一致（等价于旧 XAML 写死的 IsChecked="True"），
    /// 命令经 <c>[RelayCommand]</c> 源生成，无需在此接线。
    /// </summary>
    public GpuLifecycleViewModel()
    {
        // 运行状态行模板 {0}..{10}：计数与帧号由生命周期回调推进，{6}..{8} 三个状态位与
        // {9} 最慢帧逐帧重读，{10} 动作名只在按钮/开关落下时变。{9:0} 的毫秒取整由模板自带。
        ReadoutText = Strings.Keys.GpuLifecycle_StateReadout.CreateFormat()
            .Arg(() => frames)
            .Arg(() => initializedCount)
            .Arg(() => updatedCount)
            .Arg(() => lostCount)
            .Arg(() => restoredCount)
            .Arg(() => destroyedCount)
            .Arg(() => contextLost)
            .Arg(() => pipelineInitialized)
            .Arg(() => pipelineDestroyed)
            .Arg(() => worstFrame * 1000)
            .Arg(() => lastAction)
            .Build();
    }

    /// <summary>「释放显存」命令；视图把它接到 <c>ReleaseGpuResources</c> + 排帧上。</summary>
    public event Action? ReleaseGpuResourcesRequested;

    /// <summary>「模拟上下文丢失」命令；视图把它接到 <c>SimulateContextLost</c> + 排帧上。</summary>
    public event Action? SimulateContextLostRequested;

    /// <summary>「销毁场景」命令；视图把它接到 <c>DestroyScene</c> + 排帧上。</summary>
    public event Action? DestroySceneRequested;

    /// <summary>「只画一帧」命令；语义上就是排帧，视图把它接到宿主的 RequestFrame 上。</summary>
    public event Action? FrameRequested;

    /// <summary>连续渲染开关变化；<c>AutoRequestNextFrameRendering</c> 只有视图够得着。</summary>
    public event Action<bool>? ContinuousRenderToggled;

    /// <summary>运行状态行。语言切换时模板换新，绑定自动收到重排后的整句。</summary>
    public LinguaFormatObservable ReadoutText { get; }

    /// <summary>事件日志行（最近 8 条）。非模板的动态字符串，记账时整串换新，SetProperty 自带去重。</summary>
    public string LogText
    {
        get => logText;
        private set => SetProperty(ref logText, value);
    }

    /// <summary>
    /// 连续渲染开关，写穿到视图的 <c>AutoRequestNextFrameRendering</c>（经事件桥）。
    /// 初值 true 与引擎默认一致，绑定初始化取代旧 XAML 写死的 <c>IsChecked="True"</c>。
    /// </summary>
    public bool ContinuousRender
    {
        get => continuousRender;
        set
        {
            if (!SetProperty(ref continuousRender, value))
                return;

            lastAction = value
                ? Strings.Keys.GpuLifecycle_ActionContinuous.T()
                : Strings.Keys.GpuLifecycle_ActionOnDemand.T();

            ContinuousRenderToggled?.Invoke(value);
        }
    }

    /// <summary>释放显存；源生成 <c>ReleaseGpuResourcesCommand</c>。真正的 GL 回收与排帧在视图的事件桥里。</summary>
    [RelayCommand]
    private void ReleaseGpuResources()
    {
        lastAction = "ReleaseGpuResources";

        ReleaseGpuResourcesRequested?.Invoke();
    }

    /// <summary>模拟上下文丢失；源生成 <c>SimulateContextLostCommand</c>。最慢帧归零，好单独观察重建后那一帧。</summary>
    [RelayCommand]
    private void SimulateContextLost()
    {
        lastAction = "SimulateContextLost";
        worstFrame = 0;

        SimulateContextLostRequested?.Invoke();
    }

    /// <summary>销毁场景与管线；源生成 <c>DestroySceneCommand</c>。之后的新场景由 SceneInitialized 回调接手重建。</summary>
    [RelayCommand]
    private void DestroyScene()
    {
        lastAction = "DestroyScene";

        DestroySceneRequested?.Invoke();
    }

    /// <summary>清空日志；源生成 <c>ClearLogCommand</c>。纯 VM 状态，不需要视图参与。</summary>
    [RelayCommand]
    private void ClearLog()
    {
        lines.Clear();

        LogText = "—";
    }

    /// <summary>手动排一帧；源生成 <c>RequestOneFrameCommand</c>。按需渲染模式下这是让画面动起来的唯一入口。</summary>
    [RelayCommand]
    private void RequestOneFrame()
    {
        lastAction = Strings.Keys.GpuLifecycle_ActionManualFrame.T();

        FrameRequested?.Invoke();
    }

    /// <summary>
    /// 逐帧调用：推进帧号与最慢帧，并把读数要用的三个状态位（上下文是否丢失、管线两个状态位）
    /// 喂进来——它们都得从视图/场景上读，VM 不碰 DemoContext。结尾重读运行状态行。
    /// </summary>
    /// <param name="deltaTime">距上一帧的秒数。</param>
    /// <param name="isContextLost">视图当前 <c>IsContextLost</c>；视图还没建好时为 null。</param>
    /// <param name="isPipelineInitialized">场景管线是否已初始化；场景未建时为 null。</param>
    /// <param name="isPipelineDestroyed">场景管线是否已销毁；场景未建时为 null。</param>
    public void Tick(double deltaTime, bool? isContextLost, bool? isPipelineInitialized, bool? isPipelineDestroyed)
    {
        frames++;

        if (deltaTime > worstFrame)
            worstFrame = deltaTime;

        contextLost = isContextLost;
        pipelineInitialized = isPipelineInitialized;
        pipelineDestroyed = isPipelineDestroyed;

        ReadoutText.Refresh();
    }

    /// <summary>SceneInitialized 记账；新场景的组装由视图按 pendingRebuild 调度，这里只计数与留痕。</summary>
    /// <param name="isContextLost">事件时刻视图的 <c>IsContextLost</c>，进日志行。</param>
    public void RecordSceneInitialized(bool? isContextLost)
    {
        initializedCount++;

        Log(Strings.Keys.GpuLifecycle_LogInitialized.Format(initializedCount, isContextLost));
    }

    /// <summary>SceneUpdated 记账；只计数，频度太高不留痕。</summary>
    public void RecordSceneUpdated() => updatedCount++;

    /// <summary>ContextLost 记账；Scene 实例不变，只有 GL 句柄失效。</summary>
    public void RecordContextLost()
    {
        lostCount++;

        Log(Strings.Keys.GpuLifecycle_LogContextLost.Format(lostCount));
    }

    /// <summary>ContextRestored 记账；同一 Scene 挂新上下文，不会再触发 Initialized。</summary>
    public void RecordContextRestored()
    {
        restoredCount++;

        Log(Strings.Keys.GpuLifecycle_LogContextRestored.Format(restoredCount));
    }

    /// <summary>SceneDestroyed 记账；管线 IsDestroyed、View.Scene 置空。</summary>
    public void RecordSceneDestroyed()
    {
        destroyedCount++;

        Log(Strings.Keys.GpuLifecycle_LogSceneDestroyed.Format(destroyedCount));
    }

    /// <summary>BuildScene 留痕；「第几个场景」用 SceneInitialized 的计数，根节点数由视图数好传入。</summary>
    /// <param name="rootNodes">新建场景的根节点数。</param>
    public void RecordBuildScene(int rootNodes)
    {
        Log(Strings.Keys.GpuLifecycle_LogBuildScene.Format(initializedCount, rootNodes));
    }

    /// <summary>
    /// 追加一条日志并裁到最近 8 条。帧号前缀 <c>f{N,5}</c> 用来对齐事件的相对时刻；
    /// 这里直接换新 <see cref="LogText"/>（旧页面要等下一次 Report 才上屏，连续渲染下同一帧内即被覆盖，等价）。
    /// </summary>
    private void Log(string line)
    {
        lines.Add($"f{frames,5}  {line}");

        while (lines.Count > 8)
            lines.RemoveAt(0);

        LogText = string.Join("\n", lines);
    }
}
