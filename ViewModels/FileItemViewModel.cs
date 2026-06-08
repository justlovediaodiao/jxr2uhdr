namespace jxr2uhdr.ViewModels;

public sealed class FileItemViewModel(string fullPath, string outputPath)
{
    public string FullPath { get; } = fullPath;

    public string OriginalName { get; } = Path.GetFileName(fullPath);

    public string OutputPath { get; } = outputPath;
}
