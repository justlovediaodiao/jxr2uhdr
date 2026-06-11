namespace jxr2uhdr.ViewModels;

public sealed class FileItemViewModel(string hdrPath, string outputPath, string? sdrPath)
{
    public string HdrPath { get; } = hdrPath;

    public string OriginalName { get; } = Path.GetFileName(hdrPath);

    public string OutputPath { get; } = outputPath;

    public string? SdrPath { get; } = sdrPath;
}
