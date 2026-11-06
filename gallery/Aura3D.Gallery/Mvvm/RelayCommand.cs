using System;
using System.Windows.Input;

namespace Aura3D.Gallery.Mvvm;

/// <summary>
/// 无参命令：构造时给一个执行体，按钮用 <c>Command="{Binding ...}"</c> 接上。
/// 「能不能执行」由执行体自己判断（粒子系统没建好前用 <c>?.</c> 兜底），不提供 CanExecute。
/// </summary>
public sealed class RelayCommand(Action execute) : ICommand
{
    /// <summary>永远可执行；本类没有 CanExecute 概念，无需通知。</summary>
    event EventHandler? ICommand.CanExecuteChanged
    {
        add { }
        remove { }
    }

    /// <inheritdoc />
    public bool CanExecute(object? parameter) => true;

    /// <inheritdoc />
    public void Execute(object? parameter) => execute();
}
