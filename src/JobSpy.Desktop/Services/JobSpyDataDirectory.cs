using System;
using System.IO;

namespace JobSpy.Desktop.Services;

public static class JobSpyDataDirectory
{
    public static string GetPath()
    {
        var configuredPath = Environment.GetEnvironmentVariable("JOBSPY_DATA_DIRECTORY");
        var dataPath = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JobSpy")
            : Path.GetFullPath(configuredPath);
        Directory.CreateDirectory(dataPath);
        return dataPath;
    }
}