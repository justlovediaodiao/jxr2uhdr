using System.Runtime.InteropServices;
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
    private static readonly string[] SdrImageExtensions = [".png", ".jpg", ".jpeg"];

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
    private double _quality = 95;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private double _spinnerAngle;

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutputDirectory))]
    [NotifyPropertyChangedFor(nameof(OutputDirectoryTip))]
    private string? _outputDirectory;

    [ObservableProperty]
    private ConversionState _state = ConversionState.Pending;

    public bool ShowStatusProgress => Progress > 0;

    public bool HasOutputDirectory => OutputDirectory is not null;

    public string OutputDirectoryTip => OutputDirectory is null
        ? "Select output directory"
        : OutputDirectory;

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
        ChooseOutputDirectoryCommand.NotifyCanExecuteChanged();
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

        OutputDirectory = null;
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
        ChooseOutputDirectoryCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanChooseOutputDirectory))]
    private async Task ChooseOutputDirectory(IStorageProvider storageProvider)
    {
        var pickedFolders = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = false,
            Title = "Select Output Directory"
        });

        if (pickedFolders.Count == 0)
        {
            return;
        }

        var directory = pickedFolders[0].Path.LocalPath;
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        foreach (var file in _files)
        {
            file.OutputPath = Path.Combine(directory, Path.GetFileName(file.OutputPath));
        }

        OutputDirectory = directory;
    }

    [RelayCommand(CanExecute = nameof(CanConvert))]
    private async Task Convert()
    {
        var items = _files.ToList();
        if (items.Count == 0)
        {
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
                if (await ConvertItemAsync(item, quality))
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
                .Select(item => ConvertItemWithLimitAsync(semaphore, item, quality))
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

    private bool CanChooseOutputDirectory()
    {
        return CanEditFiles() && _files.Count > 0;
    }

    private static int GetMaxParallelism(int itemCount)
    {
        // libjxr2uhdr already uses up to 4 worker threads per image, so the
        // outer batch parallelism should stay conservative and scale only for
        // larger batches.
        var cpuCount = Math.Max(1, Environment.ProcessorCount);
        var maxThreads = Math.Min(16, cpuCount);
        var threadsPerImage = Math.Min(4, cpuCount);
        var resourceLimit = Math.Max(1, maxThreads / threadsPerImage);
        var countLimit = itemCount switch
        {
            >= 32 => 4,
            >= 16 => 2,
            _ => 1
        };

        return Math.Min(resourceLimit, countLimit);
    }

    private bool CanEditFiles()
    {
        return State != ConversionState.Converting;
    }

    private void AddFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || _files.Any(file => file.HdrPath == filePath))
        {
            return;
        }

        _files.Add(new FileItemViewModel(filePath, CreateOutputPath(filePath), FindSdrPath(filePath)));
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

    private static string CreateOutputPath(string inputPath)
    {
        var directory = Path.GetDirectoryName(inputPath) ?? string.Empty;
        var fileName = Path.GetFileNameWithoutExtension(inputPath);
        return Path.Combine(directory, $"{fileName}_utralhdr.jpg");
    }

    private static string? FindSdrPath(string hdrPath)
    {
        var directory = Path.GetDirectoryName(hdrPath) ?? string.Empty;
        var fileName = Path.GetFileNameWithoutExtension(hdrPath);

        foreach (var extension in SdrImageExtensions)
        {
            var candidate = Path.Combine(directory, $"{fileName}{extension}");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static async Task<bool> ConvertItemAsync(FileItemViewModel item, int quality)
    {
        try
        {
            var result = await Task.Run(() => NativeMethods.Convert(
                item.HdrPath,
                item.SdrPath,
                quality,
                item.OutputPath));

            return result == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> ConvertItemWithLimitAsync(
        SemaphoreSlim semaphore,
        FileItemViewModel item,
        int quality)
    {
        await semaphore.WaitAsync();

        try
        {
            return await ConvertItemAsync(item, quality);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private static partial class NativeMethods
    {
        [LibraryImport("jxr2uhdr", EntryPoint = "jxr2uhdr_convert", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int Convert(
            string jxrPath,
            string? sdrImagePath,
            int quality,
            string outJpgPath);
    }
}
