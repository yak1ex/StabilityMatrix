﻿using System;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AsyncAwaitBestPractices;
using Avalonia;
using Avalonia.Controls.Notifications;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentAvalonia.UI.Controls;
using Microsoft.Extensions.Logging;
using StabilityMatrix.Avalonia.Controls;
using StabilityMatrix.Avalonia.Services;
using StabilityMatrix.Avalonia.ViewModels.Dialogs;
using StabilityMatrix.Avalonia.Views;
using StabilityMatrix.Avalonia.Views.Dialogs;
using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Extensions;
using StabilityMatrix.Core.Helper;
using StabilityMatrix.Core.Helper.Factory;
using StabilityMatrix.Core.Models;
using StabilityMatrix.Core.Models.Packages;
using StabilityMatrix.Core.Processes;
using StabilityMatrix.Core.Python;
using StabilityMatrix.Core.Services;
using Symbol = FluentIcons.Common.Symbol;
using SymbolIconSource = FluentIcons.FluentAvalonia.SymbolIconSource;

namespace StabilityMatrix.Avalonia.ViewModels;

[View(typeof(LaunchPageView))]
public partial class LaunchPageViewModel : PageViewModelBase, IDisposable
{
    private readonly ILogger<LaunchPageViewModel> logger;
    private readonly ISettingsManager settingsManager;
    private readonly IPackageFactory packageFactory;
    private readonly IPyRunner pyRunner;
    private readonly INotificationService notificationService;
    private readonly ISharedFolders sharedFolders;
    private readonly ServiceManager<ViewModelBase> dialogFactory;
    
    // Regex to match if input contains a yes/no prompt,
    // i.e "Y/n", "yes/no". Case insensitive.
    // Separated by / or |.
    [GeneratedRegex(@"y(/|\|)n|yes(/|\|)no", RegexOptions.IgnoreCase)]
    private static partial Regex InputYesNoRegex();
    
    public override string Title => "Launch";
    public override IconSource IconSource => new SymbolIconSource { Symbol = Symbol.Rocket, IsFilled = true};

    public ConsoleViewModel Console { get; } = new();
    
    [ObservableProperty] private bool launchButtonVisibility;
    [ObservableProperty] private bool stopButtonVisibility;
    [ObservableProperty] private bool isLaunchTeachingTipsOpen;
    [ObservableProperty] private bool showWebUiButton;
    
    [ObservableProperty] private InstalledPackage? selectedPackage;
    [ObservableProperty] private ObservableCollection<InstalledPackage> installedPackages = new();

    [ObservableProperty] private BasePackage? runningPackage;
    
    // private bool clearingPackages;
    private string webUiUrl = string.Empty;
    
    // Input info-bars
    [ObservableProperty] private bool showManualInputPrompt;
    [ObservableProperty] private bool showConfirmInputPrompt;
    
    public LaunchPageViewModel(
        ILogger<LaunchPageViewModel> logger, 
        ISettingsManager settingsManager, 
        IPackageFactory packageFactory,
        IPyRunner pyRunner, 
        INotificationService notificationService, 
        ISharedFolders sharedFolders,
        ServiceManager<ViewModelBase> dialogFactory)
    {
        this.logger = logger;
        this.settingsManager = settingsManager;
        this.packageFactory = packageFactory;
        this.pyRunner = pyRunner;
        this.notificationService = notificationService;
        this.sharedFolders = sharedFolders;
        this.dialogFactory = dialogFactory;
        
        EventManager.Instance.PackageLaunchRequested += OnPackageLaunchRequested;
        EventManager.Instance.OneClickInstallFinished += OnOneClickInstallFinished;
        // Handler for console input
        Console.ApcInput += (_, message) =>
        {
            if (InputYesNoRegex().IsMatch(message.Data))
            {
                ShowConfirmInputPrompt = true;
            }
        };
    }
    
    private void OnPackageLaunchRequested(object? sender, Guid e)
    {
        OnLoaded();
        if (SelectedPackage is null) return;
        
        LaunchAsync().SafeFireAndForget();
    }

    public override void OnLoaded()
    {
        // Ensure active package either exists or is null
        settingsManager.Transaction(s =>
        {
            s.UpdateActiveInstalledPackage();
        }, ignoreMissingLibraryDir: true);
        
        // Load installed packages
        InstalledPackages =
            new ObservableCollection<InstalledPackage>(settingsManager.Settings.InstalledPackages);
        
        // Load active package
        SelectedPackage = settingsManager.Settings.GetActiveInstalledPackage();
    }

    [RelayCommand]
    private async Task LaunchAsync()
    {
        var activeInstall = SelectedPackage;
        if (activeInstall == null)
        {
            // No selected package: error notification
            notificationService.Show(new Notification(
                message: "You must install and select a package before launching",
                title: "No package selected",
                type: NotificationType.Error));
            return;
        }

        var activeInstallName = activeInstall.PackageName;
        var basePackage = string.IsNullOrWhiteSpace(activeInstallName)
            ? null
            : packageFactory.FindPackageByName(activeInstallName);

        if (basePackage == null)
        {
            logger.LogWarning(
                "During launch, package name '{PackageName}' did not match a definition",
                activeInstallName);
            
            notificationService.Show(new Notification("Package name invalid",
                "Install package name did not match a definition. Please reinstall and let us know about this issue.",
                NotificationType.Error));
            return;
        }

        // If this is the first launch (LaunchArgs is null),
        // load and save a launch options dialog vm
        // so that dynamic initial values are saved.
        if (activeInstall.LaunchArgs == null)
        {
            var definitions = basePackage.LaunchOptions;
            // Open a config page and save it
            var viewModel = dialogFactory.Get<LaunchOptionsViewModel>();
            
            viewModel.Cards = LaunchOptionCard
                .FromDefinitions(definitions, Array.Empty<LaunchOption>())
                .ToImmutableArray();
            
            var args = viewModel.AsLaunchArgs();   
            
            logger.LogDebug("Setting initial launch args: {Args}", 
                string.Join(", ", args.Select(o => o.ToArgString()?.ToRepr())));
     
            settingsManager.SaveLaunchArgs(activeInstall.Id, args);
        }

        await pyRunner.Initialize();

        // Get path from package
        var packagePath = Path.Combine(settingsManager.LibraryDir, activeInstall.LibraryPath!);

        basePackage.ConsoleOutput += OnProcessOutputReceived;
        basePackage.Exited += OnProcessExited;
        basePackage.StartupComplete += RunningPackageOnStartupComplete;
        
        // Clear console and start update processing
        await Console.StopUpdatesAsync();
        await Console.Clear();
        Console.StartUpdates();

        // Update shared folder links (in case library paths changed)
        await sharedFolders.UpdateLinksForPackage(basePackage, packagePath);

        // Load user launch args from settings and convert to string
        var userArgs = settingsManager.GetLaunchArgs(activeInstall.Id);
        var userArgsString = string.Join(" ", userArgs.Select(opt => opt.ToArgString()));

        // Join with extras, if any
        userArgsString = string.Join(" ", userArgsString, basePackage.ExtraLaunchArguments);
        await basePackage.RunPackage(packagePath, userArgsString);
        RunningPackage = basePackage;
    }

    [RelayCommand]
    private async Task Config()
    {
        var activeInstall = SelectedPackage;
        var name = activeInstall?.PackageName;
        if (name == null || activeInstall == null)
        {
            logger.LogWarning($"Selected package is null");
            return;
        }

        var package = packageFactory.FindPackageByName(name);
        if (package == null)
        {
            logger.LogWarning("Package {Name} not found", name);
            return;
        }

        var definitions = package.LaunchOptions;
        // Check if package supports IArgParsable
        // Use dynamic parsed args over static
        /*if (package is IArgParsable parsable)
        {
            var rootPath = activeInstall.FullPath!;
            var moduleName = parsable.RelativeArgsDefinitionScriptPath;
            var parser = new ArgParser(pyRunner, rootPath, moduleName);
            definitions = await parser.GetArgsAsync();
        }*/

        // Open a config page
        var userLaunchArgs = settingsManager.GetLaunchArgs(activeInstall.Id);
        var viewModel = dialogFactory.Get<LaunchOptionsViewModel>();
        viewModel.Cards = LaunchOptionCard.FromDefinitions(definitions, userLaunchArgs)
            .ToImmutableArray();
        
        logger.LogDebug("Launching config dialog with cards: {CardsCount}", 
            viewModel.Cards.Count);
        
        var dialog = new BetterContentDialog
        {
            ContentVerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            IsPrimaryButtonEnabled = true,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Padding = new Thickness(0, 16),
            Content = new LaunchOptionsDialog
            {
                DataContext = viewModel,
            }
        };
        var result = await dialog.ShowAsync();

        if (result == ContentDialogResult.Primary)
        {
            // Save config
            var args = viewModel.AsLaunchArgs();
            settingsManager.SaveLaunchArgs(activeInstall.Id, args);
        }
    }
    
    // Send user input to running package
    public async Task SendInput(string input)
    {
        if (RunningPackage is BaseGitPackage package)
        {
            var venv = package.VenvRunner;
            var process = venv?.Process;
            if (process is not null)
            {
                await process.StandardInput.WriteLineAsync(input);
            }
            else
            {
                logger.LogWarning("Attempted to write input but Process is null");
            }
        }
    }

    [RelayCommand]
    private async Task SendConfirmInput(bool value)
    {
        // This must be on the UI thread
        Dispatcher.UIThread.CheckAccess();
        // Also send input to our own console
        if (value)
        {
            Console.Post("y\n");
            await SendInput("y\n");
        }
        else
        {
            Console.Post("n\n");
            await SendInput("n\n");
        }

        ShowConfirmInputPrompt = false;
    }
    
    [RelayCommand]
    private async Task SendManualInput(string input)
    {
        // Also send input to our own console
        Console.PostLine(input);
        await SendInput(input);
    }
    
    public async Task Stop()
    {
        if (RunningPackage is null) return;
        await RunningPackage.Shutdown();
        
        RunningPackage = null;
        ShowWebUiButton = false;
        
        Console.PostLine($"{Environment.NewLine}Stopped process at {DateTimeOffset.Now}");
    }

    public void OpenWebUi()
    {
        if (string.IsNullOrEmpty(webUiUrl)) return;
        
        notificationService.TryAsync(Task.Run(() => ProcessRunner.OpenUrl(webUiUrl)),
        "Failed to open URL", $"{webUiUrl}");
    }
    
    private void OnProcessExited(object? sender, int exitCode)
    {
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            logger.LogTrace("Process exited ({Code}) at {Time:g}", 
                exitCode, DateTimeOffset.Now);
            
            // Need to wait for streams to finish before detaching handlers
            if (RunningPackage is BaseGitPackage {VenvRunner: not null} package)
            {
                var process = package.VenvRunner.Process;
                if (process is not null)
                {
                    // Max 5 seconds
                    var ct = new CancellationTokenSource(5000).Token;
                    try
                    {
                        await process.WaitUntilOutputEOF(ct);
                    }
                    catch (OperationCanceledException e)
                    {
                        logger.LogWarning("Waiting for process EOF timed out: {Message}", e.Message);
                    }
                }
            }
        
            // Detach handlers
            if (sender is BasePackage basePackage)
            {
                basePackage.ConsoleOutput -= OnProcessOutputReceived;
                basePackage.Exited -= OnProcessExited;
                basePackage.StartupComplete -= RunningPackageOnStartupComplete;
            }
            RunningPackage = null;
            ShowWebUiButton = false;
            
            await Console.StopUpdatesAsync();
            
            // Need to reset cursor in case its in some weird position
            // from progress bars
            await Console.ResetWriteCursor();
            Console.PostLine($"{Environment.NewLine}Process finished with exit code {exitCode}");
            
        }).SafeFireAndForget();
    }

    // Callback for processes
    private void OnProcessOutputReceived(object? sender, ProcessOutput output)
    {
        Console.Post(output);
        EventManager.Instance.OnScrollToBottomRequested();
    }

    private void OnOneClickInstallFinished(object? sender, bool e)
    {
        OnLoaded();
    }
    
    private void RunningPackageOnStartupComplete(object? sender, string e)
    {
        webUiUrl = e;
        ShowWebUiButton = !string.IsNullOrWhiteSpace(webUiUrl);
    }
    
    public void Dispose()
    {
        Console.Dispose();
        RunningPackage?.Shutdown();
        GC.SuppressFinalize(this);
    }
}
