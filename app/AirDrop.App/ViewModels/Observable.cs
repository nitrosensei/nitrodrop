using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using AirDrop.App.Localization;

namespace AirDrop.App.ViewModels;

/// <summary>
/// View-model base. Every instance is told about UI language switches (<see cref="Loc.Subscribe"/>, weak) and by default
/// re-raises all properties, so computed text re-reads the new language; override to rebuild cached strings.
/// </summary>
public abstract class Observable : INotifyPropertyChanged, ILanguageAware
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected Observable() => Loc.Subscribe(this);

    public virtual void OnLanguageChanged() => Raise(string.Empty);

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand(Action<object?> run, Func<object?, bool>? can = null) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? p) => can?.Invoke(p) ?? true;
    public void Execute(object? p) => run(p);
}
