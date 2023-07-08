﻿using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;

namespace StabilityMatrix.Avalonia.ViewModels;

public class ViewModelBase : ObservableValidator
{
    public virtual void OnLoaded()
    {
        
    }

    public virtual Task OnLoadedAsync()
    {
        return Task.CompletedTask;
    }
}
