// Builds the Windows installer: artifacts/GhostShow-Setup-<version>.exe, one file to copy to the show computer. Works the
// same on Windows and Linux, from the top of the repo:
//
//     dotnet run installer/build.cs                 version 0.1.0
//     dotnet run installer/build.cs -- 1.2.0        any other version: three numbers
//
// It needs:
//   - NSIS, which makes the installer. On Windows: winget install NSIS.NSIS (or https://nsis.sourceforge.io). On Linux:
//     sudo apt install nsis.
//   - what dev.cs needs for the full app for browsers: Node.js, and .NET's WebAssembly build tools.
//   - the internet, the first time, for Microsoft's Visual C++ runtime (ghostshow.nsi says why it's included).
//
// Takes a couple of minutes, and about 5 more when the full app for browsers is out of date. It builds from the working
// folder as it is, including anything not committed. See docs/windows-installer.md.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

string installerFolder = FolderOfThisFile();
string root = Path.GetDirectoryName(installerFolder)!;

string version = args.FirstOrDefault() ?? "0.1.0";
if (args.Length > 1 || !Regex.IsMatch(version, @"^\d+\.\d+\.\d+$"))
    return Stop($"The version must be three numbers, like 1.2.0 (got: {string.Join(" ", args)})");

string? makensis = FindMakensis();
if (makensis is null)
    return Stop(OperatingSystem.IsWindows()
        ? "NSIS wasn't found, and it makes the installer. Install it with\n    winget install NSIS.NSIS\nor from https://nsis.sourceforge.io, then try again."
        : "NSIS wasn't found, and it makes the installer. Install it with\n    sudo apt install nsis\nthen try again.");

string workFolder = Path.Combine(root, "artifacts", "installer");
string appFolder = Path.Combine(workFolder, "app");
string vcRedist = Path.Combine(workFolder, "VC_redist.x64.exe");
string setupFile = Path.Combine(root, "artifacts", $"GhostShow-Setup-{version}.exe");

// dev.cs knows when the full app for browsers is out of date and how to build it on each system, and checks for Node.js,
// which the desktop app's build below needs too.
Console.WriteLine("== The full app for browsers (what the show computer serves at /app/)");
int result = Run("dotnet", root, "run", "dev.cs", "--", "--browser-only");
if (result != 0) return result;

Console.WriteLine("== The desktop app for 64-bit Windows");
// Starts clean, so that files from an older build don't end up in the installer.
if (Directory.Exists(appFolder)) Directory.Delete(appFolder, recursive: true);
// Self-contained, so the show computer doesn't need .NET. ReadyToRun makes it start faster.
result = Run("dotnet", root, "publish", Path.Combine("apps", "GhostShow.App"), "-c", "Release", "-r", "win-x64",
    "--self-contained", "-p:PublishReadyToRun=true", $"-p:Version={version}", "-o", appFolder);
if (result != 0) return result;
// Debug symbols for Skia and HarfBuzz: 100 MB, and only useful for debugging those libraries.
foreach (string symbols in Directory.GetFiles(appFolder, "lib*.pdb")) File.Delete(symbols);
// RemoteViewModel.FindBrowserApp looks for this folder next to the program.
CopyFolder(Path.Combine(root, "apps", "GhostShow.Browser", "bin", "Release", "net10.0-browser", "publish", "wwwroot"),
    Path.Combine(appFolder, "browser-app"));

if (!File.Exists(vcRedist))
{
    Console.WriteLine("== Downloading the Visual C++ runtime from Microsoft");
    try
    {
        await DownloadAsync("https://aka.ms/vs/17/release/vc_redist.x64.exe", vcRedist);
    }
    catch (HttpRequestException problem)
    {
        return Stop($"The download didn't work ({problem.Message}). It's only needed once: try again when online.");
    }
}

Console.WriteLine("== The installer");
result = Run(makensis, installerFolder, "-V2", $"-DVERSION={version}", $"-DAPP_DIR={appFolder}",
    $"-DVC_REDIST={vcRedist}", $"-DSETUP_FILE={setupFile}", "ghostshow.nsi");
if (result != 0) return result;

Console.WriteLine();
Console.WriteLine($"Built: {setupFile}");
return 0;


// On the PATH, or where NSIS's own installer puts it on Windows, which doesn't add it to the PATH.
string? FindMakensis()
{
    string name = OperatingSystem.IsWindows() ? "makensis.exe" : "makensis";
    var folders = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).ToList();
    if (OperatingSystem.IsWindows())
    {
        folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "NSIS"));
        folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NSIS"));
    }
    return folders.Where(folder => folder.Length > 0).Select(folder => Path.Combine(folder, name)).FirstOrDefault(File.Exists);
}

void CopyFolder(string from, string to)
{
    foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
    {
        string copy = Path.Combine(to, Path.GetRelativePath(from, file));
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.Copy(file, copy);
    }
}

async Task DownloadAsync(string address, string file)
{
    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
    using var http = new HttpClient();
    using var response = await http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead);
    response.EnsureSuccessStatusCode();

    // Written under another name first, so that a broken download isn't taken for a finished one next time.
    string partial = file + ".part";
    await using (var output = File.Create(partial))
        await response.Content.CopyToAsync(output);
    File.Move(partial, file, overwrite: true);
}

// The other commands inherit our screen and keyboard, so their output and errors appear as they happen.
int Run(string program, string folder, params string[] arguments)
{
    var command = new ProcessStartInfo(program) { WorkingDirectory = folder, UseShellExecute = false };
    foreach (string argument in arguments) command.ArgumentList.Add(argument);

    // "dotnet run" leaves notes about its own SDK in the environment. dev.cs may use a different SDK (the private one in
    // ~/.dotnet), so they mustn't be passed on.
    foreach (string name in new[] { "MSBUILD_EXE_PATH", "MSBuildExtensionsPath", "MSBuildSDKsPath", "DOTNET_HOST_PATH" })
        command.Environment.Remove(name);

    using var process = Process.Start(command)!;
    process.WaitForExit();
    return process.ExitCode;
}

int Stop(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}

string FolderOfThisFile([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
