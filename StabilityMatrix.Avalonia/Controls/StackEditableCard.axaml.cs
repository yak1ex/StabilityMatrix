﻿using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using DynamicData.Binding;
using FluentAvalonia.UI.Controls;
using Nito.Disposables.Internals;
using StabilityMatrix.Avalonia.ViewModels.Inference;
using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Extensions;
#pragma warning disable CS0657 // Not a valid attribute location for this declaration

namespace StabilityMatrix.Avalonia.Controls;

[PseudoClasses(":editEnabled")]
[Transient]
public class StackEditableCard : TemplatedControl
{
    private ListBox? listBoxPart;

    // ReSharper disable once MemberCanBePrivate.Global
    public static readonly StyledProperty<bool> IsListBoxEditEnabledProperty =
        AvaloniaProperty.Register<StackEditableCard, bool>("IsListBoxEditEnabled");

    public bool IsListBoxEditEnabled
    {
        get => GetValue(IsListBoxEditEnabledProperty);
        set => SetValue(IsListBoxEditEnabledProperty, value);
    }

    /// <inheritdoc />
    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        listBoxPart = e.NameScope.Find<ListBox>("PART_ListBox");
        if (listBoxPart != null)
        {
            // Register handlers to attach container behavior

            // Forward container index changes to view model
            ((IChildIndexProvider)listBoxPart).ChildIndexChanged += (_, args) =>
            {
                if (args.Child is Control { DataContext: StackExpanderViewModel vm })
                {
                    vm.OnContainerIndexChanged(args.Index);
                }
            };
        }

        var addButton = e.NameScope.Find<Button>("PART_AddButton");
        if (addButton != null)
        {
            addButton.Flyout = GetAddButtonFlyout();
        }
    }

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (listBoxPart is null)
            return;

        if (DataContext is StackEditableCardViewModel vm)
        {
            vm.WhenPropertyChanged(x => x.IsEditEnabled)
                .Subscribe(args =>
                {
                    PseudoClasses.Set(":editEnabled", args.Value);

                    ((IPseudoClasses)listBoxPart!.Classes).Set("draggableVirtualizing", args.Value);

                    foreach (
                        var item in listBoxPart.Items.Cast<StackExpanderViewModel>().WhereNotNull()
                    )
                    {
                        item.IsEditEnabled = args.Value;
                    }
                });
        }
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsListBoxEditEnabledProperty)
        {
            var value = change.GetNewValue<bool>();
            PseudoClasses.Set(":editEnabled", value);

            ((IPseudoClasses)listBoxPart!.Classes).Set("draggableVirtualizing", value);

            foreach (var item in listBoxPart.Items.Cast<StackExpanderViewModel>().WhereNotNull())
            {
                item.IsEditEnabled = value;
            }
        }
    }

    private string GetModuleDisplayName(Type moduleType)
    {
        var name = moduleType.Name;
        name = name.StripEnd("Module");

        // Add a space between lower and upper case letters, unless one part is 1 letter long
        /*name = Regex.Replace(name, @"(\P{Ll})(\P{Lu})", "$1 $2");*/

        return name;
    }

    private FAMenuFlyout GetAddButtonFlyout()
    {
        var vm = (DataContext as StackEditableCardViewModel)!;
        var flyout = new FAMenuFlyout();

        foreach (var moduleType in vm.AvailableModules)
        {
            var menuItem = new MenuFlyoutItem
            {
                Text = GetModuleDisplayName(moduleType),
                Command = vm.AddModuleCommand,
                CommandParameter = moduleType,
            };
            flyout.Items.Add(menuItem);
        }

        return flyout;
    }
}
