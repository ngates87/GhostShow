using System.IO.Compression;
using GhostShow.Core.Storage;

namespace GhostShow.Tests.Storage;

public class OnlineFixtureLibraryTests : IDisposable
{
    const string Fixture = """
        <FixtureDefinition xmlns="http://www.qlcplus.org/FixtureDefinition">
         <Manufacturer>Acme</Manufacturer><Model>{0}</Model><Type>Dimmer</Type>
         <Channel Name="Dim" Preset="IntensityDimmer"/>
         <Mode Name="1ch"><Channel Number="0">Dim</Channel></Mode>
        </FixtureDefinition>
        """;

    readonly TempDirectory _dir = new();
    readonly AppPaths _paths;
    readonly DownloadedLibraryInfo _source = new("Test", "https://example.invalid/lib.zip", DateTimeOffset.Now, 0);

    public OnlineFixtureLibraryTests() => _paths = new AppPaths(_dir.Path);

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Installed_fixtures_load_with_the_rest_of_the_library()
    {
        OnlineFixtureLibrary.Install(Zip(("fixtures/acme-one.qxf", string.Format(Fixture, "One")), ("fixtures/acme-two.qxf", string.Format(Fixture, "Two"))), _paths, _source);

        var library = FixtureProfileLoader.Load(_paths, new MachineSettings());

        Assert.NotNull(library.Find("qlcplus/Acme/One"));
        Assert.NotNull(library.Find("qlcplus/Acme/Two"));
        Assert.DoesNotContain(library.LoadErrors, e => e.Source.StartsWith(_dir.Path));
        Assert.Equal(2, OnlineFixtureLibrary.Installed(_paths)!.FixtureCount);
    }

    [Fact]
    public void Only_fixture_files_are_extracted_and_paths_inside_the_zip_are_ignored()
    {
        OnlineFixtureLibrary.Install(Zip(
            ("fixtures/acme-one.qxf", string.Format(Fixture, "One")),
            ("fixtures/../../escape.qxf", string.Format(Fixture, "Escape")),
            ("gobos/ofl/gobo.svg", "<svg/>"),
            ("README.txt", "hi")), _paths, _source);

        var files = Directory.GetFiles(_dir.Path, "*", SearchOption.AllDirectories).Select(Path.GetFileName).Order().ToArray();

        Assert.Equal(["acme-one.qxf", "escape.qxf", "library.json"], files);
        Assert.True(File.Exists(Path.Combine(OnlineFixtureLibrary.FixturesDirectory(_paths), "escape.qxf")), "flattened into the fixtures folder");
    }

    [Fact]
    public void Updating_replaces_the_previous_library()
    {
        OnlineFixtureLibrary.Install(Zip(("fixtures/old.qxf", string.Format(Fixture, "Old"))), _paths, _source);

        OnlineFixtureLibrary.Install(Zip(("fixtures/new.qxf", string.Format(Fixture, "New"))), _paths, _source);

        Assert.Equal(["new.qxf"], Directory.GetFiles(OnlineFixtureLibrary.FixturesDirectory(_paths)).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Bad_download_keeps_the_previous_library(bool validZipWithoutFixtures)
    {
        OnlineFixtureLibrary.Install(Zip(("fixtures/old.qxf", string.Format(Fixture, "Old"))), _paths, _source);
        var bad = validZipWithoutFixtures ? Zip(("README.txt", "no fixtures here")) : new MemoryStream("<html>error page</html>"u8.ToArray());

        Assert.Throws<InvalidDataException>(() => OnlineFixtureLibrary.Install(bad, _paths, _source));

        Assert.Equal(["old.qxf"], Directory.GetFiles(OnlineFixtureLibrary.FixturesDirectory(_paths)).Select(Path.GetFileName));
        Assert.Single(Directory.GetDirectories(Path.GetDirectoryName(OnlineFixtureLibrary.Directory(_paths))!));   // no staging folders left
    }

    [Fact]
    public void Nothing_installed_reports_no_library()
    {
        Assert.Null(OnlineFixtureLibrary.Installed(_paths));
    }

    static MemoryStream Zip(params (string Path, string Content)[] entries)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open());
                writer.Write(content);
            }
        }
        stream.Position = 0;
        return stream;
    }
}
