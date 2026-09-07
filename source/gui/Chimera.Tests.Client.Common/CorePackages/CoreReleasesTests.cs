using System;
using System.Linq;
using System.Net;
using System.Net.Http;

using Chimera.Client.Common;

namespace Chimera.Tests.Client.Common.CorePackages
{
	/// <summary>
	/// Reading a core's published versions out of a GitHub releases response. The
	/// parsing is separated from the fetching so what the manager BELIEVES about a
	/// feed - which versions exist, which is newest, which channel each is in - can
	/// be tested without a network.
	/// </summary>
	[TestClass]
	public class CoreReleasesTests
	{
		private static string Release(string tag, string published, string assetName, bool draft = false, string extra = "")
			=> $@"{{
				""tag_name"": ""{tag}"",
				""draft"": {(draft ? "true" : "false")},
				""prerelease"": true,
				""published_at"": ""{published}"",
				""assets"": [ {{ ""name"": ""{assetName}"", ""browser_download_url"": ""https://example.invalid/{assetName}"", ""size"": 4096{extra} }} ]
			}}";

		[TestMethod]
		public void NewestFirst()
		{
			var json = $@"[
				{Release("nightly-2026-09-01", "2026-09-01T05:00:00Z", "gpgx-aaaaaaaaaaaa.chimeraCore")},
				{Release("dev", "2026-09-07T11:00:00Z", "gpgx-cccccccccccc.chimeraCore")},
				{Release("nightly-2026-09-05", "2026-09-05T05:00:00Z", "gpgx-bbbbbbbbbbbb.chimeraCore")}
			]";
			var releases = CoreReleases.Parse(json, "gpgx");
			CollectionAssert.AreEqual(
				new[] { "cccccccccccc", "bbbbbbbbbbbb", "aaaaaaaaaaaa" },
				releases.Select(static r => r.Version).ToList());
		}

		[TestMethod]
		public void TheVersionComesFromTheAssetTheBuildNamed()
		{
			// the tag is typed by a workflow; the asset name is written by the build
			// that stamped the version into the package, and that is what a movie cites
			var releases = CoreReleases.Parse($"[{Release("dev", "2026-09-07T11:00:00Z", "gpgx-4ed3532117ad.chimeraCore")}]", "gpgx");
			Assert.AreEqual("4ed3532117ad", releases[0].Version);
			Assert.AreEqual("dev", releases[0].Tag);
		}

		[TestMethod]
		public void ChannelsComeFromTheTag()
		{
			Assert.AreEqual(CoreChannel.Dev, CoreReleases.ChannelOf("dev"));
			Assert.AreEqual(CoreChannel.Nightly, CoreReleases.ChannelOf("nightly-2026-09-07"));
			Assert.AreEqual(CoreChannel.Release, CoreReleases.ChannelOf("v1.0"));
		}

		[TestMethod]
		public void TheDefaultChoiceIsTheNewestNightly()
		{
			var json = $@"[
				{Release("dev", "2026-09-07T11:00:00Z", "gpgx-cccccccccccc.chimeraCore")},
				{Release("nightly-2026-09-05", "2026-09-05T05:00:00Z", "gpgx-bbbbbbbbbbbb.chimeraCore")}
			]";
			var releases = CoreReleases.Parse(json, "gpgx");
			// dev is newer, and is still not the default: it is replaced on every push,
			// so a movie recorded against it can stop being fetchable
			Assert.AreEqual("bbbbbbbbbbbb", CoreReleases.Newest(releases).Version);
			Assert.AreEqual("cccccccccccc", CoreReleases.Newest(releases, CoreChannel.Dev).Version);
		}

		[TestMethod]
		public void DraftsAndAssetlessReleasesAreNotVersions()
		{
			var json = $@"[
				{Release("nightly-2026-09-06", "2026-09-06T05:00:00Z", "gpgx-dddddddddddd.chimeraCore", draft: true)},
				{{ ""tag_name"": ""nightly-2026-09-05"", ""published_at"": ""2026-09-05T05:00:00Z"", ""assets"": [] }},
				{Release("nightly-2026-09-04", "2026-09-04T05:00:00Z", "gpgx-eeeeeeeeeeee.chimeraCore")}
			]";
			var releases = CoreReleases.Parse(json, "gpgx");
			Assert.AreEqual(1, releases.Count);
			Assert.AreEqual("eeeeeeeeeeee", releases[0].Version);
		}

		[TestMethod]
		public void TheRightAssetIsPickedOutOfSeveral()
		{
			var json = @"[ {
				""tag_name"": ""nightly-2026-09-07"",
				""published_at"": ""2026-09-07T05:00:00Z"",
				""assets"": [
					{ ""name"": ""SHA256SUMS"", ""browser_download_url"": ""https://example.invalid/sums"", ""size"": 64 },
					{ ""name"": ""quickerneshawk-111111111111.chimeraCore"", ""browser_download_url"": ""https://example.invalid/other"", ""size"": 10 },
					{ ""name"": ""quickernes-222222222222.chimeraCore"", ""browser_download_url"": ""https://example.invalid/mine"", ""size"": 20 }
				] } ]";
			// quickernes is a PREFIX of quickerneshawk, so a loose match takes the wrong one
			var releases = CoreReleases.Parse(json, "quickernes");
			Assert.AreEqual("222222222222", releases[0].Version);
			Assert.AreEqual("https://example.invalid/mine", releases[0].AssetUrl);
		}

		[TestMethod]
		public void TheDigestIsCarriedWhenGithubReportsOne()
		{
			var withDigest = Release("dev", "2026-09-07T11:00:00Z", "gpgx-aaaaaaaaaaaa.chimeraCore", extra: @", ""digest"": ""sha256:abc""");
			Assert.AreEqual("sha256:abc", CoreReleases.Parse($"[{withDigest}]", "gpgx")[0].Digest);
			Assert.IsNull(CoreReleases.Parse($"[{Release("dev", "2026-09-07T11:00:00Z", "gpgx-aaaaaaaaaaaa.chimeraCore")}]", "gpgx")[0].Digest);
		}

		[TestMethod]
		public void ARateLimitSaysHowLongToWait()
		{
			using HttpResponseMessage response = new(HttpStatusCode.Forbidden);
			response.Headers.Add("x-ratelimit-remaining", "0");
			response.Headers.Add("x-ratelimit-reset", DateTimeOffset.UtcNow.AddMinutes(9).ToUnixTimeSeconds().ToString());
			var message = CoreFeed.RateLimited(response);
			StringAssert.Contains(message, "minutes", message);
		}

		[TestMethod]
		public void AForbiddenThatIsNotARateLimitIsNotReportedAsOne()
		{
			using HttpResponseMessage response = new(HttpStatusCode.Forbidden);
			response.Headers.Add("x-ratelimit-remaining", "57");
			Assert.IsNull(CoreFeed.RateLimited(response));
			using HttpResponseMessage noHeaders = new(HttpStatusCode.Forbidden);
			Assert.IsNull(CoreFeed.RateLimited(noHeaders));
		}
	}
}
