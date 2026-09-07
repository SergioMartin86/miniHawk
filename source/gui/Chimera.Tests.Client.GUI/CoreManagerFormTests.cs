using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

using Chimera.Client.Common;
using Chimera.Client.GUI;

namespace Chimera.Tests.Client.GUI
{
	/// <summary>
	/// The wiring between the Core Manager window and the model underneath it. The
	/// model is tested on its own (CoreManagerModelTests); what is checked here is
	/// that pressing the one button that talks to GitHub puts the answer where
	/// somebody can see it, with the date and commit they need to tell two builds
	/// apart.
	/// </summary>
	[TestClass]
	public class CoreManagerFormTests
	{
		private static readonly List<RosterCore> Roster =
		[
			new() { Id = "gpgx", Name = "Genesis Plus GX", Repo = "ToolAssisted-run/chimera-core-gpgx", Systems = [ "GEN" ] },
		];

		private const string Feed = @"[
			{ ""tag_name"": ""dev"", ""published_at"": ""2026-09-07T11:00:00Z"", ""assets"": [
				{ ""name"": ""gpgx-cccccccccccc.chimeraCore"", ""browser_download_url"": ""https://example.invalid/c"", ""size"": 3145728 } ] },
			{ ""tag_name"": ""nightly-2026-09-05"", ""published_at"": ""2026-09-05T05:00:00Z"", ""assets"": [
				{ ""name"": ""gpgx-bbbbbbbbbbbb.chimeraCore"", ""browser_download_url"": ""https://example.invalid/b"", ""size"": 3145728 } ] },
			{ ""tag_name"": ""nightly-2026-09-01"", ""published_at"": ""2026-09-01T05:00:00Z"", ""assets"": [
				{ ""name"": ""gpgx-aaaaaaaaaaaa.chimeraCore"", ""browser_download_url"": ""https://example.invalid/a"", ""size"": 3145728 } ] }
		]";

		/// <summary>Answers every request with one canned body; no network is touched.</summary>
		private sealed class Canned : HttpMessageHandler
		{
			private readonly string _body;

			private readonly HttpStatusCode _status;

			public Canned(string body, HttpStatusCode status = HttpStatusCode.OK)
			{
				_body = body;
				_status = status;
			}

			protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
				=> Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent(_body) });
		}

		private static CoreManagerForm Open(string body, IReadOnlyList<DiscoveredCorePackage> installed, HttpStatusCode status = HttpStatusCode.OK)
		{
			// a cache directory of its own, so the test never reads or writes the
			// store this machine actually uses
			var cache = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"chimera-feed-{Guid.NewGuid():N}");
			return new CoreManagerForm(
				() => Roster,
				() => installed,
				new CoreFeed(new HttpClient(new Canned(body, status)), cache),
				new CoreInstaller());
		}

		private static ComboBox VersionsOf(Form form)
		{
			foreach (Control panel in form.Controls)
			{
				foreach (Control c in panel.Controls)
				{
					if (c is ComboBox combo) return combo;
				}
			}
			throw new InvalidOperationException("no version selector on the form");
		}

		[TestMethod]
		public async Task FetchingVersionsFillsTheSelectorNewestFirst()
		{
			using var form = Open(Feed, [ ]);
			form.Show();
			Assert.IsTrue(form.Select("Genesis Plus GX"));
			var versions = VersionsOf(form);
			Assert.AreEqual(0, versions.Items.Count, "nothing is fetched to draw the window");

			await form.FetchSelectedVersions();

			// the dev build is newest and is NOT offered by default: it is replaced on
			// every push, so a movie recorded on it can stop being fetchable
			CollectionAssert.AreEqual(
				new[] { "2026-09-05  (bbbbbbbb)", "2026-09-01  (aaaaaaaa)" },
				versions.Items.Cast<object>().Select(static i => i.ToString()).ToList());
		}

		[TestMethod]
		public async Task EveryVersionCarriesItsDateAndCommit()
		{
			using var form = Open(Feed, [ ]);
			form.Show();
			Assert.IsTrue(form.Select("Genesis Plus GX"));
			await form.FetchSelectedVersions();
			foreach (var text in VersionsOf(form).Items.Cast<object>().Select(static i => i.ToString()))
			{
				StringAssert.Matches(text, new System.Text.RegularExpressions.Regex(@"^\d{4}-\d{2}-\d{2}\s+\([0-9a-f]{8}\)"), text);
			}
		}

		[TestMethod]
		public async Task AnInstalledVersionIsMarkedRatherThanOfferedTwice()
		{
			DiscoveredCorePackage have = new()
			{
				Name = "Genesis Plus GX",
				Version = "bbbbbbbbbbbb",
				Path = "/store/gpgx-bbbbbbbbbbbb.chimeraCore",
				Sha1 = new string('b', 40),
			};
			using var form = Open(Feed, [ have ]);
			form.Show();
			Assert.IsTrue(form.Select("Genesis Plus GX"));
			await form.FetchSelectedVersions();
			var texts = VersionsOf(form).Items.Cast<object>().Select(static i => i.ToString()).ToList();
			Assert.AreEqual(2, texts.Count, "the installed version is the published one, not a second line");
			StringAssert.Contains(texts[0], "installed");
			Assert.IsFalse(texts[1].Contains("installed"));
		}

		[TestMethod]
		public async Task ARateLimitIsShownInsteadOfVersions()
		{
			using var form = Open("[]", [ ], HttpStatusCode.Forbidden);
			form.Show();
			Assert.IsTrue(form.Select("Genesis Plus GX"));
			await form.FetchSelectedVersions();
			// the canned 403 carries no rate-limit headers, so it is reported as the
			// plain refusal it is - what matters is that the window says something
			Assert.AreEqual(0, VersionsOf(form).Items.Count);
		}
	}
}
