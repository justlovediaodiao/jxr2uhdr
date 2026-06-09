using System.Diagnostics;
using Avalonia.Threading;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace jxr2uhdr.ViewModels;

public enum ConversionState
{
    Pending,
    Converting,
    Done,
    Error
}

public partial class MainWindowViewModel : ObservableObject
{
    private readonly List<FileItemViewModel> _files = [];
    private readonly DispatcherTimer _spinnerTimer;

    public MainWindowViewModel()
    {
        _spinnerTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _spinnerTimer.Tick += (_, _) => SpinnerAngle = (SpinnerAngle + 8) % 360;
    }

    [ObservableProperty]
    private double _quality = 90;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private double _spinnerAngle;

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private ConversionState _state = ConversionState.Pending;

    public bool ShowStatusProgress => Progress > 0;

    public double StatusProgressWidth => Math.Clamp(Progress / 100d, 0, 1) * 278d;

    public string QualityText => Math.Round(Quality).ToString();

    partial void OnProgressChanged(double value)
    {
        OnPropertyChanged(nameof(ShowStatusProgress));
        OnPropertyChanged(nameof(StatusProgressWidth));
    }

    partial void OnQualityChanged(double value)
    {
        OnPropertyChanged(nameof(QualityText));
    }

    partial void OnStateChanged(ConversionState oldValue, ConversionState newValue)
    {
        AddFilesCommand.NotifyCanExecuteChanged();
        ConvertCommand.NotifyCanExecuteChanged();

        if (newValue == ConversionState.Converting)
        {
            _spinnerTimer.Start();
            return;
        }

        if (oldValue == ConversionState.Converting)
        {
            _spinnerTimer.Stop();
            SpinnerAngle = 0;
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditFiles))]
    private async Task AddFiles(IStorageProvider storageProvider)
    {
        var pickedFiles = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = true,
            Title = "Select JXR Files",
            FileTypeFilter =
            [
                new FilePickerFileType("JXR images")
                {
                    Patterns = ["*.jxr", "*.JXR", "*.wdp", "*.WDP"],
                    AppleUniformTypeIdentifiers = ["com.microsoft.jxr"]
                },
                FilePickerFileTypes.All
            ]
        });

        if (pickedFiles.Count == 0)
        {
            return;
        }

        _files.Clear();
        Progress = 0;
        State = ConversionState.Pending;
        Summary = string.Empty;
        foreach (var file in pickedFiles)
        {
            AddFile(file.Path.LocalPath);
        }

        if (_files.Count > 0)
        {
            Summary = BuildSelectionSummary();
        }

        ConvertCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanConvert))]
    private async Task Convert()
    {
        var items = _files.ToList();
        if (items.Count == 0)
        {
            return;
        }

        var cliPath = Path.Combine(AppContext.BaseDirectory, GetCliFileName());
        if (!File.Exists(cliPath))
        {
            Summary = BuildFailureSummary(items.Count, 0);
            State = ConversionState.Error;
            return;
        }

        State = ConversionState.Converting;
        Progress = 0;
        var quality = Math.Clamp((int)Math.Round(Quality), 1, 100);
        var maxParallelism = Math.Min(GetMaxParallelism(items.Count), items.Count);
        var completed = 0;
        var succeeded = 0;

        if (maxParallelism == 1)
        {
            foreach (var item in items)
            {
                if (await ConvertItemAsync(cliPath, item, quality))
                {
                    succeeded++;
                }

                completed++;
                Progress = completed * 100d / items.Count;
            }
        }
        else
        {
            using var semaphore = new SemaphoreSlim(maxParallelism);
            var tasks = items
                .Select(item => ConvertItemWithLimitAsync(semaphore, cliPath, item, quality))
                .ToList();

            while (tasks.Count > 0)
            {
                var finishedTask = await Task.WhenAny(tasks);
                tasks.Remove(finishedTask);

                if (await finishedTask)
                {
                    succeeded++;
                }

                completed++;
                Progress = completed * 100d / items.Count;
            }
        }

        if (succeeded == items.Count)
        {
            State = ConversionState.Done;
        }
        else
        {
            Summary = BuildFailureSummary(items.Count, succeeded);
            State = ConversionState.Error;
        }
    }

    private bool CanConvert()
    {
        return State != ConversionState.Converting && _files.Count > 0;
    }

    private static string GetCliFileName()
    {
        return OperatingSystem.IsWindows() ? "jxr2uhdr-cli.exe" : "jxr2uhdr-cli";
    }

    private static int GetMaxParallelism(int itemCount)
    {
        if (itemCount >= 32)
        {
            return Math.Max(8, Environment.ProcessorCount);
        }

        if (itemCount >= 16)
        {
            return Math.Max(4, Environment.ProcessorCount);
        }

        if (itemCount >= 8)
        {
            return Math.Max(2, Environment.ProcessorCount);
        }

        return 1;
    }

    private bool CanEditFiles()
    {
        return State != ConversionState.Converting;
    }

    private void AddFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || _files.Any(file => file.FullPath == filePath))
        {
            return;
        }

        _files.Add(new FileItemViewModel(filePath, CreateOutputPath(filePath)));
    }

    private string BuildSelectionSummary()
    {
        return _files.Count switch
        {
            0 => string.Empty,
            1 => _files[0].OriginalName,
            _ => $"{_files.Count} images"
        };
    }

    private static string BuildFailureSummary(int total, int succeeded)
    {
        return total == 1
            ? "Failed"
            : $"{succeeded} OK, {total - succeeded} Failed";
    }

    private string CreateOutputPath(string inputPath)
    {
        var directory = Path.GetDirectoryName(inputPath) ?? string.Empty;
        var fileName = Path.GetFileNameWithoutExtension(inputPath);
        return Path.Combine(directory, $"{fileName}_utralhdr.jpg");
    }

    private static async Task<bool> ConvertItemAsync(string cliPath, FileItemViewModel item, int quality)
    {
        try
        {
            var exitCode = await RunCliAsync(cliPath, item.FullPath, item.OutputPath, quality);
            return exitCode == 0 && File.Exists(item.OutputPath);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> ConvertItemWithLimitAsync(
        SemaphoreSlim semaphore,
        string cliPath,
        FileItemViewModel item,
        int quality)
    {
        await semaphore.WaitAsync();

        try
        {
            return await ConvertItemAsync(cliPath, item, quality);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private static async Task<int> RunCliAsync(string cliPath, string inputPath, string outputPath, int quality)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = cliPath,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("--input");
        startInfo.ArgumentList.Add(inputPath);
        startInfo.ArgumentList.Add("--output");
        startInfo.ArgumentList.Add(outputPath);
        startInfo.ArgumentList.Add("--quality");
        startInfo.ArgumentList.Add(quality.ToString());

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        await process.WaitForExitAsync();

        return process.ExitCode;
    }
}
