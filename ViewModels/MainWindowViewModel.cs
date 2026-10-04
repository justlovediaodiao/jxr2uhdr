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
    [NotifyPropertyChangedFor(nameof(StatusTip))]
    private string _summary = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutputDirectory))]
    [NotifyPropertyChangedFor(nameof(OutputDirectoryTip))]
    private string? _outputDirectory;

    [ObservableProperty]
    private ConversionState _state = ConversionState.Pending;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusTip))]
    private string _errorDetails = string.Empty;

    public string StatusTip => State == ConversionState.Error && !string.IsNullOrEmpty(ErrorDetails)
        ? ErrorDetails
        : Summary;

    public bool ShowStatusProgress => Progress > 0;

    public bool HasOutputDirectory => OutputDirectory is not null;

    public string OutputDirectoryTip => OutputDirectory is null
        ? "Select output directory"
        : OutputDirectory;

    public string QualityText => Math.Round(Quality).ToString();

    partial void OnProgressChanged(double value)
    {
        OnPropertyChanged(nameof(ShowStatusProgress));
    }

    partial void OnQualityChanged(double value)
    {
        OnPropertyChanged(nameof(QualityText));
    }

    partial void OnStateChanged(ConversionState oldValue, ConversionState newValue)
    {
        OnPropertyChanged(nameof(StatusTip));
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
        ErrorDetails = string.Empty;
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

        ErrorDetails = string.Empty;
        Summary = BuildSelectionSummary();
        State = ConversionState.Converting;
        Progress = 0;
        var quality = Math.Clamp((int)Math.Round(Quality), 1, 100);
        var maxParallelism = Math.Min(GetMaxParallelism(items.Count), items.Count);
        var completed = 0;
        var succeeded = 0;
        var failures = new List<string>();

        if (maxParallelism == 1)
        {
            foreach (var item in items)
            {
                var error = await ConvertItemAsync(item, quality);
                if (error is null) succeeded++;
                else failures.Add(error);

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

                var error = await finishedTask;
                if (error is null) succeeded++;
                else failures.Add(error);

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
            ErrorDetails = string.Join("\n\n", failures);
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

    private static async Task<string?> ConvertItemAsync(FileItemViewModel item, int quality)
    {
        try
        {
            var result = await Task.Run(() => NativeMethods.Convert(
                item.HdrPath,
                item.SdrPath,
                quality,
                item.OutputPath));

            return result == 0 ? null : $"{item.OriginalName}: {DescribeError(result)} (error {result}).";
        }
        catch (Exception ex)
        {
            return $"{item.OriginalName}: {ex.Message}";
        }
    }

    private static string DescribeError(int result) => result switch
    {
        1 => "Invalid conversion arguments",
        2 => "Failed to initialize Windows imaging components (COM)",
        3 => "Failed to read or decode the input image using Windows Imaging Component",
        4 => "Failed to write the output image; check the output directory and permissions",
        5 => "Not enough memory to convert the image",
        6 => "Ultra HDR encoding failed",
        7 => "The input image pixel format is not supported",
        _ => "Native image conversion failed"
    };

    private static async Task<string?> ConvertItemWithLimitAsync(
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
