using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using BD3DOrchestrator.Tests.Fakes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace BD3DOrchestrator.Tests;

public sealed class MovieDetectorTests
{
    [Test]
    public async Task ParseTitleAndYear_HandlesMvcSuffixAndNumericTitles()
    {
        (string title1, int year1) = MovieDetector.ParseTitleAndYear("Blade Runner 2049 (2017).3d.mvc.mkv");
        (string title2, int year2) = MovieDetector.ParseTitleAndYear("Dune.3d.mvc.mkv");

        await Assert.That(title1).IsEqualTo("Blade Runner 2049");
        await Assert.That(year1).IsEqualTo(2017);
        await Assert.That(title2).IsEqualTo("Dune");
        await Assert.That(year2).IsEqualTo(0);
    }

    [Test]
    public async Task DetectAsync_LocalNfoAnimation_ReturnsNfoAndSkipsTmdb()
    {
        FakeFileSystem fs = new();
        // The file name carries neither this title nor a year, so both can only come from the .nfo.
        fs.AddFile(@"M:\Movies\Up\Pixar Up.3d.mvc.mkv");
        fs.AddFile(@"M:\Movies\Up\movie.nfo", "<movie><title>Up</title><year>2009</year><genre>Adventure / Animation</genre></movie>");
        FakeHttpMessageHandler handler = new();
        using HttpClient httpClient = new(handler);
        MovieDetector detector = new(httpClient, new() { TmdbApiReadAccessToken = "secret-token" }, fs);

        MovieInfo info = await detector.DetectAsync(@"M:\Movies\Up\Pixar Up.3d.mvc.mkv");

        await Assert.That(info.Title).IsEqualTo("Up");
        await Assert.That(info.Year).IsEqualTo(2009);
        await Assert.That(info.IsAnimation).IsTrue();
        await Assert.That(handler.Requests.Count).IsEqualTo(0);
    }

    [Test]
    public async Task DetectAsync_LocalNfoNotAnimation_FallsThroughToTmdb()
    {
        FakeFileSystem fs = new();
        fs.AddFile(@"M:\Movies\Avatar\Avatar.3d.mvc.mkv");
        fs.AddFile(@"M:\Movies\Avatar\movie.nfo", "<movie><title>Avatar</title><year>2009</year><genre>Science Fiction</genre></movie>");
        FakeHttpMessageHandler handler = new(_ => new TmdbSearchResults().ToResponse());
        using HttpClient httpClient = new(handler);
        MovieDetector detector = new(httpClient, new() { TmdbApiReadAccessToken = "secret-token" }, fs);

        MovieInfo info = await detector.DetectAsync(@"M:\Movies\Avatar\Avatar.3d.mvc.mkv");

        await Assert.That(handler.Requests.Count).IsEqualTo(1);
        await Assert.That(info.IsAnimation).IsFalse();
        await Assert.That(info.Reason).IsEqualTo("Live action or unconfirmed");
    }

    [Test]
    public async Task DetectAsync_FileNameYear_IsSentToTmdbAndWinsOverTmdbYear()
    {
        FakeFileSystem fs = new();
        fs.AddFile(@"M:\Movies\Blade Runner 2049 (2017).3d.mvc.mkv");
        FakeHttpMessageHandler handler = new(_ => new TmdbSearchResults(new TmdbMovie("Blade Runner 2049", "2017-10-04", 878)).ToResponse());
        using HttpClient httpClient = new(handler);
        MovieDetector detector = new(httpClient, new() { TmdbApiReadAccessToken = "secret-token" }, fs);

        MovieInfo info = await detector.DetectAsync(@"M:\Movies\Blade Runner 2049 (2017).3d.mvc.mkv");

        await Assert.That(handler.Requests[0].RequestUri!.Query).IsEqualTo("?query=Blade%20Runner%202049&include_adult=false&year=2017");
        await Assert.That(info.Title).IsEqualTo("Blade Runner 2049");
        await Assert.That(info.Year).IsEqualTo(2017);
        await Assert.That(info.Reason).IsEqualTo("Live action or unconfirmed");
    }

    [Test]
    public async Task DetectAsync_TmdbExactMatch_DetectsAnimationAndUsesBearerHeader()
    {
        FakeFileSystem fs = new();
        fs.AddFile(@"M:\Movies\Frozen.3d.mvc.mkv");
        FakeHttpMessageHandler handler = new(_ => new TmdbSearchResults(
            new TmdbMovie("Frozen Ground", "2013-08-23", 53),
            new TmdbMovie("Frozen", "2013-11-27", 16, 10751)).ToResponse());
        using HttpClient httpClient = new(handler);
        MovieDetector detector = new(httpClient, new() { TmdbApiReadAccessToken = "secret-token" }, fs);

        MovieInfo info = await detector.DetectAsync(@"M:\Movies\Frozen.3d.mvc.mkv");

        await Assert.That(info.IsAnimation).IsTrue();
        await Assert.That(info.Year).IsEqualTo(2013);
        await Assert.That(info.Reason).IsEqualTo("TMDb confirmed Animation (Frozen); year 2013 from TMDb");
        await Assert.That(handler.Requests.Count).IsEqualTo(1);
        HttpRequestMessage req = handler.Requests[0];
        await Assert.That(req.RequestUri!.Query).DoesNotContain("year=");
        await Assert.That(req.Headers.Authorization?.Scheme).IsEqualTo("Bearer");
        await Assert.That(req.Headers.Authorization?.Parameter).IsEqualTo("secret-token");
        await Assert.That(req.RequestUri!.AbsoluteUri).DoesNotContain("secret-token");
    }

    [Test]
    public async Task DetectAsync_TmdbHttpError_FallsBackGracefully()
    {
        FakeFileSystem fs = new();
        fs.AddFile(@"M:\Movies\Avatar (2009).3d.mvc.mkv");
        FakeHttpMessageHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using HttpClient httpClient = new(handler);
        MovieDetector detector = new(httpClient, new() { TmdbApiReadAccessToken = "secret-token" }, fs);

        MovieInfo info = await detector.DetectAsync(@"M:\Movies\Avatar (2009).3d.mvc.mkv");

        await Assert.That(info.Title).IsEqualTo("Avatar");
        await Assert.That(info.Year).IsEqualTo(2009);
        await Assert.That(info.IsAnimation).IsFalse();
    }
}
