#nullable enable

using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Chimera.Client.Common;

namespace Chimera.Tests.Client.Common.CorePackages
{
	/// <summary>
	/// Installing a downloaded core, end to end, without a network.
	///
	/// This exists because nothing used to run <see cref="CoreInstaller.InstallAsync"/>
	/// at all: the manager's tests hand it a real installer and then never press the
	/// button that downloads. That gap hid a total failure - the installer wrote the
	/// download to a ".part" file and then read it back with
	/// <see cref="CorePackageDiscovery.Peek"/>, which refuses on the FILENAME before
	/// opening anything, so every core anybody tried to install came back "what was
	/// downloaded is not a core package". A perfectly good package, refused for its
	/// temporary name.
	/// </summary>
	[TestClass]
	public class CoreInstallerTests
	{
		private const string Version = "47130d8113624337bb1a728508fee40ac1947be8";

		/// <summary>The smallest thing the engine will call a waterbox package.</summary>
		private static byte[] Package(string version = Version)
		{
			using MemoryStream buffer = new();
			using (ZipArchive zip = new(buffer, ZipArchiveMode.Create, leaveOpen: true))
			{
				// the engine calls a zip a waterbox package when it holds both of these
				using (var wbx = new StreamWriter(zip.CreateEntry("core.wbx").Open()))
				{
					wbx.Write("not a real guest, and never loaded: this is read as a package, not run");
				}
				using var config = new StreamWriter(zip.CreateEntry("waterbox.config").Open());
				config.Write($@"{{
					""coreName"": ""quickerNES"",
					""version"": ""{version}"",
					""systemId"": ""NES""
				}}");
			}
			return buffer.ToArray();
		}

		/// <summary>Serves one body to whatever asks; no network is touched.</summary>
		private sealed class Canned : HttpMessageHandler
		{
			private readonly byte[] _body;

			public Canned(byte[] body) => _body = body;

			protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
				=> Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_body) });
		}

		private static CoreRelease ReleaseFor(byte[] body, string version = Version)
			=> new()
			{
				Tag = "nightly-2026-09-07",
				Channel = CoreChannel.Nightly,
				Version = version,
				PublishedAt = DateTimeOffset.Parse("2026-09-07T16:46:20Z"),
				AssetName = $"quickernes-{version}.chimeraCore",
				AssetUrl = "https://example.invalid/quickernes.chimeraCore",
				AssetSize = body.Length,
			};

		private static RosterCore Core
			=> new() { Id = "quickernes", Name = "quickerNES", Repo = "ToolAssisted-run/chimera-core-quickernes", Systems = [ "NES" ] };

		[TestMethod]
		public async Task ADownloadedPackageIsInstalled()
		{
			var body = Package();
			CoreInstaller installer = new(new HttpClient(new Canned(body)));
			var result = await installer.InstallAsync(Core, ReleaseFor(body));
			try
			{
				// the message this used to fail with named the package, not the
				// installer, which is what made it look like the core's fault
				Assert.IsNull(result.Error, $"a good package was refused: {result.Error}");
				Assert.IsNotNull(result.Path);
				Assert.IsTrue(File.Exists(result.Path), "the store has no file where the installer said it put one");
				Assert.AreEqual(Version, result.Package?.Version, "installed under the wrong version");
			}
			finally
			{
				if (result.Path is { } path && File.Exists(path)) File.Delete(path);
			}
		}

		/// <summary>
		/// The check that made the bug survive: whatever the installer writes the
		/// download to, discovery has to be able to read it back. Peek refuses on the
		/// filename first, so a temporary name that is not package-shaped fails
		/// everything before a byte is examined.
		/// </summary>
		[TestMethod]
		public void PeekRefusesAPackageNotNamedLikeOne()
		{
			var dir = Path.Combine(Path.GetTempPath(), $"chimera-installer-{Guid.NewGuid():N}");
			Directory.CreateDirectory(dir);
			try
			{
				var named = Path.Combine(dir, "quickernes.chimeraCore");
				var misnamed = Path.Combine(dir, "quickernes.part");
				File.WriteAllBytes(named, Package());
				File.WriteAllBytes(misnamed, Package());

				Assert.IsNotNull(CorePackageDiscovery.Peek(named), "a package named like one must be readable");
				Assert.IsNull(
					CorePackageDiscovery.Peek(misnamed),
					"Peek is filename-gated on purpose (a scan must not hash every file it meets), "
						+ "so anything handing it a path must name that path like a package");
			}
			finally
			{
				try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
			}
		}
	}
}
