using Irihi.Lingua;
using System;

namespace Aura3D.Gallery.Kit;

/// <summary>
/// 检视面板下拉的统一选项模型。显示名是 Lingua observable——模板里 <c>{Binding Name^}</c>
/// 流订阅，切换语言时下拉文字自动换；<c>Value</c> 是语言无关的稳定身份（字符串键或枚举），
/// 选中项比较因此回到对象身份，不再按翻译后的字符串 <c>IndexOf</c> 匹配（那是换语言即脱钩的旧问题）。
/// </summary>
public abstract class ComboOption
{
    /// <summary>下拉里显示的文本；语言切换时由流绑定自动收到新值。</summary>
    public IObservable<string?> Name { get; }

    /// <summary>显示名是 Lingua 键或字面量 observable 的构造。</summary>
    protected ComboOption(IObservable<string?> name) => Name = name;

    /// <summary>从 Lingua 键建选项：显示名跟随语言，value 携带页面的稳定标识。</summary>
    public static ComboOption<T> ForKey<T>(LinguaKey key, T value) =>
        new(key.Manager.GetObservable(key.Key)!, value);

    /// <summary>从字面量建选项：用于语言无关的名字（枚举名、剪辑名、骨骼名等引擎标识）。</summary>
    public static ComboOption<T> Literal<T>(string name, T value) =>
        new(LinguaObservableString.FromLiteral(name), value);

    /// <summary>
    /// 当前语言下的显示文本：直接读 observable 的当前值（不建订阅），供读数模板的重读型
    /// Arg 在每次 Refresh 时取「此刻选中项」的名字——捕获单个选项的 Name 流会把读数钉死在
    /// 构造时的那一项上，跟随不了后续切换。
    /// </summary>
    public string CurrentName => (Name as LinguaObservable<string>)?.CurrentValue ?? string.Empty;
}

/// <inheritdoc cref="ComboOption"/>
public sealed class ComboOption<T>(IObservable<string?> name, T value) : ComboOption(name)
{
    /// <summary>该选项的稳定值，VM 据此驱动引擎侧逻辑。</summary>
    public T Value { get; } = value;
}
