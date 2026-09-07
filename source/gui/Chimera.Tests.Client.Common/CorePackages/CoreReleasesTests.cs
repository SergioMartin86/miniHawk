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

		/// <summary>
		/// The contract between tools/write-core-index.sh and this parser: the index
		/// is GitHub's own /releases shape, trimmed to these fields. That is the point
		/// of the shape - one parser reads both, so the generator cannot drift from
		/// the reader. This is a real index as the script emits it.
		/// </summary>
		[TestMethod]
		public void TheGeneratedIndexIsReadByTheSameParser()
		{
			const string index = @"[
				{
					""tag_name"": ""nightly-2026-09-07"",
					""published_at"": ""2026-09-07T16:57:11Z"",
					""created_at"": ""2026-09-07T16:36:20Z"",
					""assets"": [ {
						""name"": ""gpgx-6e9e643ae326cc5c9c4ea83d7d18e6840bfce4f9.chimeraCore"",
						""browser_download_url"": ""https://github.com/ToolAssisted-run/chimera-core-gpgx/releases/download/nightly-2026-09-07/gpgx-6e9e643ae326cc5c9c4ea83d7d18e6840bfce4f9.chimeraCore"",
						""size"": 507122,
						""digest"": ""sha256:178010154e61655dbd97a35aa4654c90988b6d6d6ba1d9468e17af46b56ddbeb""
					} ]
				},
				{
					""tag_name"": ""dev"",
					""published_at"": ""2026-09-07T15:30:03Z"",
					""created_at"": ""2026-09-07T15:30:03Z"",
					""assets"": [ {
						""name"": ""gpgx-196b0d7f8552a13f5583678c4e66458bca428549.chimeraCore"",
						""browser_download_url"": ""https://github.com/ToolAssisted-run/chimera-core-gpgx/releases/download/dev/gpgx-196b0d7f8552a13f5583678c4e66458bca428549.chimeraCore"",
						""size"": 507126,
						""digest"": ""sha256:aaaa""
					} ]
				}
			]";

			var releases = CoreReleases.Parse(index, "gpgx");
			Assert.AreEqual(2, releases.Count);

			var newest = releases[0];
			Assert.AreEqual(CoreChannel.Nightly, newest.Channel);
			Assert.AreEqual("6e9e643ae326cc5c9c4ea83d7d18e6840bfce4f9", newest.Version);
			Assert.AreEqual(507122, newest.AssetSize);
			// the digest is what stops a wrong or tampered index installing a wrong
			// core: the installer verifies it against the bytes it downloaded
			Assert.AreEqual("sha256:178010154e61655dbd97a35aa4654c90988b6d6d6ba1d9468e17af46b56ddbeb", newest.Digest);

			Assert.AreEqual(CoreChannel.Dev, releases[1].Channel);
			// nightly, not dev, is what Download latest takes
			Assert.AreEqual(newest.Version, CoreReleases.Newest(releases)!.Version);
		}

		/// <summary>
		/// The address the manager actually asks. This is the whole rate-limit fix in
		/// one assertion: a release asset, never api.github.com, because the API allows
		/// 60 an hour per address and charges for a 304 too.
		/// </summary>
		[TestMethod]
		public void AVersionIndexIsReadFromTheCoreNotTheApi()
		{
			var url = CoreReleases.IndexUrl("ToolAssisted-run/chimera-core-gpgx");
			Assert.AreEqual(
				"https://github.com/ToolAssisted-run/chimera-core-gpgx/releases/download/index/releases.json",
				url);
			Assert.IsFalse(url.Contains("api.github.com"), "the API is what the index exists to avoid");
			// the tag is permanent and the asset is replaced in place, so this address
			// is the same one for the life of the core - a movie's core stays findable
			StringAssert.Contains(url, "/releases/download/", url);
		}
	}
}
