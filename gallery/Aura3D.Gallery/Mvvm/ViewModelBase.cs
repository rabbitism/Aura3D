using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Aura3D.Gallery.Mvvm;

/// <summary>
/// 演示页 ViewModel 的最小基类：手写 INPC，不引 MVVM 框架。页面把 DataContext 从
/// 自身换成 VM 之后，检视面板的行控件经 <c>SliderRow.Value</c> 一类双向绑定直接读写这里。
/// </summary>
public abstract class ViewModelBase : INotifyPropertyChanged
{
    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>写 backing 字段并通知；值没变就不通知，滑杆拖动不会产生回环。</summary>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        return true;
    }
}
