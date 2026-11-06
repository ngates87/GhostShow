// Builds everything GhostShow needs, then starts the desktop app in Debug. Works the same on Windows and Linux:
//
//     dotnet run dev.cs                      build what's out of date, then start the app
//     dotnet run dev.cs -- --build-only      just build (VS Code's F5 uses this, then starts the app in its debugger)
//     dotnet run dev.cs -- --skip-browser    leave the full app (/app/) as it is: rebuilding it takes about 5 minutes
//     dotnet run dev.cs -- --browser-only    just the full app, if it's out of date (installer/build.cs uses this)
//
// "Everything" is:
//   - the full app for browsers, served at /app/. It isn't part of the solution, because it needs the WebAssembly build
//     tools, so a plain "dotnet build" leaves it out and Remote Access then quietly doesn't offer /app/.
//   - the solution, in Debug. That also builds the simple remote's page, which needs Node.js.
//
// It's one C# file rather than a shell script because nothing else runs unchanged on both systems. See "Build, test,
// run" in README.md.

using System.Diagnostics;
using System.Runtime.CompilerServices;

string root = FolderOfThisFile();
bool buildOnly = args.Contains("--build-only");
bool skipBrowser = args.Contains("--skip-browser");
bool browserOnly = args.Contains("--browser-only");
var known = new[] { "--build-only", "--skip-browser", "--browser-only" };
if (args.Except(known).FirstOrDefault() is { } unknown)
    return Stop($"I don't know \"{unknown}\". The options are: {string.Join(", ", known)}");

// Without Node.js the build still succeeds, but with a page saying the web remote wasn't built. Better to say so here.
if (!CanRun("node", "--version"))
    return Stop("Node.js wasn't found, and the web remote's page needs it. Install it from https://nodejs.org and try again.");

if (skipBrowser)
    Console.WriteLine("== Leaving the full app for browsers as it is (--skip-browser)");
else if (BrowserAppIsCurrent())
    Console.WriteLine("== The full app for browsers is up to date");
else
{
    int result = BuildBrowserApp();
    if (result != 0) return result;
}

if (browserOnly) return 0;

Console.WriteLine("== The solution, in Debug");
int build = Run("dotnet", root, "build", "GhostShow.slnx", "-c", "Debug");
if (build != 0) return build;

if (buildOnly) return 0;

Console.WriteLine("== Starting GhostShow");
return Run("dotnet", root, "run", "--project", Path.Combine("apps", "GhostShow.App"), "-c", "Debug", "--no-build");


// Out of date if anything the browser build is made from has changed since it was built. It goes by file times, so
// switching git branches can cause a rebuild it didn't strictly need; that only costs time.
bool BrowserAppIsCurrent()
{
    string published = PublishedBrowserApp();
    if (!File.Exists(Path.Combine(published, "index.html"))) return false;

    return NewestFileTime(BrowserAppSources()) <= NewestFileTime(SourceFilesIn(published));
}

// What GhostShow.Browser.csproj compiles: itself, Core, and the desktop app's screens, which it shares as source. This
// is a little more than it uses, since some of the desktop's screens stay out of the browser.
IEnumerable<string> BrowserAppSources()
{
    string app = Path.Combine(root, "apps", "GhostShow.App");
    return SourceFilesIn(Path.Combine(root, "apps", "GhostShow.Browser"))
        .Concat(SourceFilesIn(Path.Combine(root, "libs", "GhostShow.Core")))
        .Concat(SourceFilesIn(Path.Combine(app, "ViewModels")))
        .Concat(SourceFilesIn(Path.Combine(app, "Views")))
        .Concat(SourceFilesIn(Path.Combine(app, "Controls")))
        .Concat(SourceFilesIn(Path.Combine(app, "Assets")))
        .Append(Path.Combine(app, "Icons.axaml"))
        .Append(Path.Combine(app, "Styles.axaml"));
}

string PublishedBrowserApp() =>
    Path.Combine(root, "apps", "GhostShow.Browser", "bin", "Release", "net10.0-browser", "publish", "wwwroot");

// The same build as apps/GhostShow.Browser/publish.sh, which can't be used here because Windows has no bash.
int BuildBrowserApp()
{
    string? dotnet = DotnetWithWasmTools();
    if (dotnet is null)
        return Stop(OperatingSystem.IsWindows()
            ? "The full app needs .NET's WebAssembly build tools. In a terminal opened as administrator, run:\n" +
              "    dotnet workload install wasm-tools\n" +
              "Then try again, or use --skip-browser to start without rebuilding it."
            : "The full app needs .NET's WebAssembly build tools, and no SDK here has them. See the top of\n" +
              "apps/GhostShow.Browser/publish.sh, or use --skip-browser to start without rebuilding it.");

    Console.WriteLine("== The full app for browsers (about 5 minutes; --skip-browser skips it)");
    string project = Path.Combine(root, "apps", "GhostShow.Browser");

    // Starts clean: publishing over an old build leaves its files behind, and the folder keeps growing.
    foreach (string folder in new[] { "bin", "obj" })
    {
        string old = Path.Combine(project, folder, "Release");
        if (Directory.Exists(old)) Directory.Delete(old, recursive: true);
    }

    int result = Run(dotnet, project, "publish", "-c", "Release", "-p:RunAOTCompilation=true");

    // A half-finished build mustn't look current next time.
    if (result != 0 && Directory.Exists(PublishedBrowserApp())) Directory.Delete(PublishedBrowserApp(), recursive: true);
    return result;
}

// Ubuntu's own SDK can't have the WebAssembly tools, so on Linux they're in a private SDK in ~/.dotnet (see publish.sh).
// On Windows the normal SDK can have them. Null: none has.
string? DotnetWithWasmTools()
{
    string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    string privateSdk = Path.Combine(home, ".dotnet", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
    string[] candidates = File.Exists(privateSdk) ? [privateSdk, "dotnet"] : ["dotnet"];
    return candidates.FirstOrDefault(dotnet => OutputOf(dotnet, "workload", "list").Contains("wasm-tools"));
}

// Every file in a folder and the folders below it, leaving out build output, which changes without the source changing.
// Judged from the folder itself, since the published app is below a "bin" folder of its own.
IEnumerable<string> SourceFilesIn(string folder)
{
    if (!Directory.Exists(folder)) return [];
    return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Where(file =>
    {
        string[] parts = Path.GetRelativePath(folder, file).Split(Path.DirectorySeparatorChar);
        return !parts.Contains("bin") && !parts.Contains("obj");
    });
}

DateTime NewestFileTime(IEnumerable<string> files) =>
    files.Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max();

// The other commands inherit our screen and keyboard, so their output and errors appear as they happen.
int Run(string program, string folder, params string[] arguments)
{
    using var process = Process.Start(Command(program, folder, arguments))!;
    process.WaitForExit();
    return process.ExitCode;
}

bool CanRun(string program, params string[] arguments)
{
    try
    {
        OutputOf(program, arguments);
        return true;
    }
    catch (System.ComponentModel.Win32Exception)   // what .NET throws when the program doesn't exist
    {
        return false;
    }
}

string OutputOf(string program, params string[] arguments)
{
    var command = Command(program, root, arguments);
    command.RedirectStandardOutput = true;
    command.RedirectStandardError = true;
    using var process = Process.Start(command)!;
    process.ErrorDataReceived += (_, _) => { };
    process.BeginErrorReadLine();   // so a full error pipe can't hold it up
    string output = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    return output;
}

ProcessStartInfo Command(string program, string folder, string[] arguments)
{
    var command = new ProcessStartInfo(program) { WorkingDirectory = folder, UseShellExecute = false };
    foreach (string argument in arguments) command.ArgumentList.Add(argument);

    // "dotnet run dev.cs" leaves notes about its own SDK in the environment. A command using a different SDK (the private
    // one in ~/.dotnet) mustn't follow them.
    foreach (string name in new[] { "MSBUILD_EXE_PATH", "MSBuildExtensionsPath", "MSBuildSDKsPath", "DOTNET_HOST_PATH" })
        command.Environment.Remove(name);

    if (Path.IsPathRooted(program)) command.Environment["DOTNET_ROOT"] = Path.GetDirectoryName(program);
    return command;
}

int Stop(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}

string FolderOfThisFile([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
