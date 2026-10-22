using System.Net;
using System.Net.Http;
using System.Text;
using AccountManager.Core.Sync;
using Xunit;

namespace AccountManager.Core.Tests;

/// <summary>
/// The sync folder is shared with other tools that write Cockpit account JSON: the newest file wins whatever its
/// prefix, and the tool's own legacy folder is only read when the shared one is empty. Fake WebDAV server, no network.
/// </summary>
public class WebDavSharedFolderTests
{
    private const string Base = "https://dav.example.com/dav/";
    private const string Shared = "/AccountManager/";
    private const string Legacy = "/DshAccountManager/";

    private sealed record Entry(string Name, string? Modified, bool Collection = false);

    /// <summary>A server holding named folders; a folder that is not in the map answers 404.</summary>
    private sealed class FakeDav
    {
        public Dictionary<string, List<Entry>> Folders { get; } = new();
        public List<(string Method, string Path)> Requests { get; } = new();

        public HttpClient Http => new(new Handler(this));

        public WebDavClient Client(string folder) => new(new WebDavOptions { BaseUrl = Base, Username = "u", Password = "p", Folder = folder }, Http);

        private HttpResponseMessage Respond(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add((request.Method.Method, path));
            if (request.Method.Method != "PROPFIND") { return new HttpResponseMessage(HttpStatusCode.OK); }
            var folder = path.Replace("/dav", string.Empty);
            if (!Folders.TryGetValue(folder, out var entries)) { return new HttpResponseMessage(HttpStatusCode.NotFound); }

            var xml = new StringBuilder("<?xml version=\"1.0\"?><d:multistatus xmlns:d=\"DAV:\">");
            xml.Append("<d:response><d:href>/dav").Append(folder).Append("</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop></d:propstat></d:response>");
            foreach (var e in entries)
            {
                xml.Append("<d:response><d:href>/dav").Append(folder).Append(e.Name).Append(e.Collection ? "/" : string.Empty).Append("</d:href><d:propstat><d:prop>");
                xml.Append(e.Collection ? "<d:resourcetype><d:collection/></d:resourcetype>" : "<d:resourcetype/>");
                if (e.Modified is not null) { xml.Append("<d:getlastmodified>").Append(e.Modified).Append("</d:getlastmodified>"); }
                xml.Append("</d:prop></d:propstat></d:response>");
            }

            xml.Append("</d:multistatus>");
            return new HttpResponseMessage((HttpStatusCode)207) { Content = new StringContent(xml.ToString(), Encoding.UTF8, "application/xml") };
        }

        private sealed class Handler(FakeDav owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(owner.Respond(request));
        }
    }

    private static string Utc(int day, int hour) => new DateTime(2026, 10, day, hour, 0, 0, DateTimeKind.Utc).ToString("R");

    private static Task<WebDavSource?> Find(FakeDav dav) => WebDavSharedFolder.FindNewestAsync(dav.Client(Shared), dav.Client(Legacy));

    [Fact]
    public void TheSharedFolderIsNeutralAndTheClientDefaultsToIt()
    {
        Assert.Equal("/AccountManager/", new WebDavOptions().Folder);
        Assert.Equal("/AccountManager/", new WebDavClient(new WebDavOptions { BaseUrl = Base, Username = "u", Folder = " " }).Folder);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheNewestFileWinsAcrossFilePrefixesWhicheverToolWroteIt(bool otherToolIsNewer)
    {
        var dav = new FakeDav();
        dav.Folders[Shared] = new List<Entry>
        {
            new("dsh_2026-10-20_10-00-00.json", Utc(20, 10)),
            new("othertool_2026-10-21_09-00-00.json", otherToolIsNewer ? Utc(21, 9) : Utc(19, 9)),
        };

        var found = await Find(dav);

        Assert.NotNull(found);
        Assert.Equal(otherToolIsNewer ? "othertool_2026-10-21_09-00-00.json" : "dsh_2026-10-20_10-00-00.json", found!.File.Name);
        Assert.False(found.FromLegacyFolder);
        Assert.Equal(Shared + found.File.Name, found.Path);
        Assert.DoesNotContain(dav.Requests, r => r.Path.Contains("DshAccountManager")); // the legacy folder was not even looked at
    }

    [Fact]
    public async Task EqualTimesFallBackToTheNameAndUnknownTimesLoseToKnownOnes()
    {
        var dav = new FakeDav();
        dav.Folders[Shared] = new List<Entry>
        {
            new("dsh_2026-10-20_10-00-00.json", Utc(20, 10)),
            new("othertool_2026-10-20_10-00-00.json", Utc(20, 10)),
            new("zzz_undated.json", null),
        };

        var found = await Find(dav);

        Assert.Equal("othertool_2026-10-20_10-00-00.json", found!.File.Name);
    }

    [Fact]
    public async Task NonJsonEntriesAndSubFoldersAreIgnored()
    {
        var dav = new FakeDav();
        dav.Folders[Shared] = new List<Entry>
        {
            new("notes.txt", Utc(22, 12)),
            new("newer.json.bak", Utc(22, 12)),
            new("archive.json", Utc(22, 12), Collection: true),
            new("dsh_2026-10-01_00-00-00.json", Utc(1, 0)),
        };

        var found = await Find(dav);

        Assert.Equal("dsh_2026-10-01_00-00-00.json", found!.File.Name);
    }

    [Theory]
    [InlineData(false)] // the shared folder does not exist (404)
    [InlineData(true)]  // it exists but holds no JSON file
    public async Task TheLegacyFolderIsUsedOnlyWhenTheSharedOneIsMissingOrHasNoJson(bool sharedExistsButEmpty)
    {
        var dav = new FakeDav();
        if (sharedExistsButEmpty) { dav.Folders[Shared] = new List<Entry> { new("readme.txt", Utc(21, 1)), new("sub", null, Collection: true) }; }
        dav.Folders[Legacy] = new List<Entry>
        {
            new("dsh_2026-10-05_08-00-00.json", Utc(5, 8)),
            new("dsh_2026-10-06_08-00-00.json", Utc(6, 8)),
        };

        var found = await Find(dav);

        Assert.NotNull(found);
        Assert.True(found!.FromLegacyFolder);
        Assert.Equal("dsh_2026-10-06_08-00-00.json", found.File.Name);
        Assert.Equal(Legacy + "dsh_2026-10-06_08-00-00.json", found.Path);
    }

    [Fact]
    public async Task TheLegacyFolderIsNeverWrittenToOrCreatedWhileLookingForAFile()
    {
        var dav = new FakeDav();
        dav.Folders[Legacy] = new List<Entry> { new("dsh_2026-10-06_08-00-00.json", Utc(6, 8)) };

        _ = await Find(dav);

        Assert.All(dav.Requests, r => Assert.Equal("PROPFIND", r.Method));
        Assert.DoesNotContain(dav.Requests, r => r.Method is "MKCOL" or "PUT" or "DELETE" or "MOVE" or "COPY");
    }

    [Fact]
    public async Task BothFoldersEmptyOrMissingGivesNothingSoTheNoFilesMessageStillApplies()
    {
        var dav = new FakeDav();
        Assert.Null(await Find(dav));

        dav.Folders[Shared] = new List<Entry>();
        dav.Folders[Legacy] = new List<Entry> { new("notes.txt", Utc(1, 1)) };
        Assert.Null(await Find(dav));
    }

    [Fact]
    public async Task WithoutALegacyClientAnEmptySharedFolderGivesNothing()
    {
        var dav = new FakeDav();
        dav.Folders[Legacy] = new List<Entry> { new("dsh_2026-10-06_08-00-00.json", Utc(6, 8)) };

        Assert.Null(await WebDavSharedFolder.FindNewestAsync(dav.Client(Shared), legacy: null));
    }

    [Fact]
    public async Task UploadGoesToTheSharedFolderOnly()
    {
        var dav = new FakeDav();

        await dav.Client(Shared).UploadAsync("dsh_2026-10-22_10-00-00.json", new byte[] { 1 });

        Assert.Contains(dav.Requests, r => r.Method == "MKCOL" && r.Path == "/dav/AccountManager/");
        Assert.Contains(dav.Requests, r => r.Method == "PUT" && r.Path == "/dav/AccountManager/dsh_2026-10-22_10-00-00.json");
        Assert.DoesNotContain(dav.Requests, r => r.Path.Contains("DshAccountManager"));
    }
}
