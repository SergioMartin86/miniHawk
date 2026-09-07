#nullable enable

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Chimera.Client.Common;

namespace Chimera.Tests.Client.Common.CorePackages
{
	/// <summary>
	/// Reading a core's published version index.
	///
	/// The interesting case is Probe, which is how a core published outside the
	/// official set gets added: nothing is known about such a repository except
	/// what it publishes, so the id and the name have to come out of the newest
	/// asset. That path can be exercised against an OFFICIAL core, because an
	/// official core's index is the same file in the same place - which is worth
	/// knowing, since it means the mechanism can be smoke-tested by hand with an
	/// address anybody has.
	/// </summary>
	[TestClass]
	public class CoreFeedTests
	{
		/// <summary>A real index, as tools/write-core-index.sh emits one.</summary>
		private const string Index = @"[
			{
				""tag_name"": ""nightly-2026-09-07"",
				""published_at"": ""2026-09-07T16:57:11Z"",
				""created_at"": ""2026-09-07T16:36:20Z"",
				""assets"": [ {
					""name"": ""gpgx-6e9e643ae326cc5c9c4ea83d7d18e6840bfce4f9.chimeraCore"",
					""browser_download_url"": ""https://example.invalid/gpgx.chimeraCore"",
					""size"": 507122,
					""digest"": ""sha256:1780""
				} ]
			}
		]";

		private sealed class Canned : HttpMessageHandler
		{
			private readonly string _body;

			private readonly HttpStatusCode _status;

			public Uri? Asked { get; private set; }

			public Canned(string body, HttpStatusCode status = HttpStatusCode.OK)
			{
				_body = body;
				_status = status;
			}

			protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
			{
				Asked = request.RequestUri;
				return Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent(_body) });
			}
		}

		private static CoreFeed FeedOf(Canned canned)
			=> new(new HttpClient(canned), Path.Combine(Path.GetTempPath(), $"chimera-feed-{Guid.NewGuid():N}"));

		[TestMethod]
		public async Task TheIndexIsAskedForNotTheApi()
		{
			Canned canned = new(Index);
			var result = await FeedOf(canned).FetchAsync(
				new RosterCore { Id = "gpgx", Repo = "ToolAssisted-run/chimera-core-gpgx" });

			Assert.IsTrue(result.Ok, result.Error);
			Assert.AreEqual(1, result.Releases.Count);
			Assert.IsNotNull(canned.Asked);
			Assert.AreEqual(
				"https://github.com/ToolAssisted-run/chimera-core-gpgx/releases/download/index/releases.json",
				canned.Asked!.ToString());
			Assert.AreNotEqual("api.github.com", canned.Asked.Host, "the API is what the index exists to avoid");
		}

		/// <summary>
		/// Adding a core by its repository address, against an official core's own
		/// index - the same file an external core publishes. The id and the name are
		/// read out of the asset, because for a repository nobody shipped a roster
		/// entry for there is nothing else to read them from.
		/// </summary>
		[TestMethod]
		public async Task AnExternalCoreIsIdentifiedFromWhatItPublishes()
		{
			var (core, error) = await FeedOf(new Canned(Index)).ProbeAsync("ToolAssisted-run/chimera-core-gpgx");

			Assert.IsNull(error, error);
			Assert.IsNotNull(core);
			Assert.AreEqual("gpgx", core!.Id, "the id comes from the asset name, not the repository name");
			Assert.AreEqual("ToolAssisted-run/chimera-core-gpgx", core.Repo);
			Assert.IsTrue(core.IsExternal);
		}

		/// <summary>
		/// A repository with no index says so, and says it in terms of the thing that
		/// is missing. Since there is no fallback to the API, this message is the only
		/// explanation anybody gets, so it has to name the file and say when it turns
		/// up rather than blaming the network.
		/// </summary>
		[TestMethod]
		public async Task ARepositoryWithNoIndexSaysWhichFileIsMissing()
		{
			var result = await FeedOf(new Canned("Not Found", HttpStatusCode.NotFound)).FetchAsync(
				new RosterCore { Id = "nope", Repo = "someone/not-a-core" });

			Assert.IsFalse(result.Ok);
			StringAssert.Contains(result.Error, "releases.json", result.Error);
			StringAssert.Contains(result.Error, "someone/not-a-core", result.Error);
		}

		[TestMethod]
		public async Task ProbingARepositoryThatPublishesNothingIsRefused()
		{
			var (core, error) = await FeedOf(new Canned("[]")).ProbeAsync("someone/empty");

			Assert.IsNull(core);
			Assert.IsNotNull(error);
		}
	}
}
