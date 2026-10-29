using System.Diagnostics;

namespace Snowcloak.Core.Profiles;

public static class ProfileWebHostLaunch
{
    public static ProcessStartInfo CreateStartInfo(string hostDirectory, string runtimeDirectory)
    {
        var assembly = Path.GetFullPath(Path.Combine(hostDirectory, "Snowcloak.ProfileWebHost.dll"));
        if (!File.Exists(assembly)) throw new FileNotFoundException("The profile web host is missing.");

        var runtime = new DirectoryInfo(runtimeDirectory);
        if (runtime.Parent?.Name != "Microsoft.NETCore.App" || runtime.Parent.Parent?.Name != "shared")
            throw new DirectoryNotFoundException("The .NET runtime layout is unsupported.");
        var dotnet = Path.Combine(runtime.Parent.Parent.Parent!.FullName, "dotnet.exe");
        if (!File.Exists(dotnet)) throw new FileNotFoundException("The .NET runtime host is missing.");

        var start = new ProcessStartInfo(dotnet)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(assembly);
        return start;
    }
}
