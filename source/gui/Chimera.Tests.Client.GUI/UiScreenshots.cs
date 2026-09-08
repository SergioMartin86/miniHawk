using System.Collections.Generic;
using System.Linq;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

using System.Reflection;

using Chimera.Client.Common;
using Chimera.Client.GUI;
using Chimera.Emulation.Common;
using Chimera.Emulation.Common.Waterbox;

namespace Chimera.Tests.Client.GUI
{
	/// <summary>
	/// Renders windows to PNG so a person can look at them.
	///
	/// Everything else in this project asserts behaviour, which is the part a
	/// machine can judge. Whether a window is legible, sensibly laid out and not
	/// embarrassing is not, so these produce pictures instead: run with
	/// CHIMERA_UI_SHOTS=&lt;dir&gt; (tests/ui/run-ui-tests.sh --shots) and look at
	/// what comes out. Without that variable they report inconclusive, so an
	/// ordinary test run neither writes files nor fails.
	/// </summary>
	[TestClass]
	public class UiScreenshots
	{
		private static string ShotDir
			=> Environment.GetEnvironmentVariable("CHIMERA_UI_SHOTS");

		private static void Shoot(Form form, string name)
		{
			var dir = ShotDir;
			form.Show();
			form.Refresh();
			Application.DoEvents();
			using Bitmap bmp = new(form.Width, form.Height);
			using (var g = Graphics.FromImage(bmp))
			{
				// the window is real and on a (headless) screen, so grab it from there:
				// DrawToBitmap skips the non-client area and mis-renders ListViews on Mono
				g.CopyFromScreen(form.Location, Point.Empty, form.Size);
			}
			Directory.CreateDirectory(dir);
			bmp.Save(Path.Combine(dir, $"{name}.png"), ImageFormat.Png);
		}

		/// <summary>
		/// The About window: it says what this build is, and nothing else. Worth a
		/// picture because it is all layout - nothing in it is driven by state.
		/// </summary>
		[TestMethod]
		public void AboutWindow()
		{
			if (ShotDir is null) { Assert.Inconclusive("set CHIMERA_UI_SHOTS to write screenshots"); return; }

			using AboutBox form = new();
			form.StartPosition = FormStartPosition.Manual;
			form.Location = new Point(0, 0);
			Shoot(form, "about");
		}

		/// <summary>
		/// The firmware window, over a package that wants two files and another that
		/// wants one. The states are what a user actually hits - provided, never
		/// provided, and the wrong file - so the picture shows whether they read
		/// clearly enough to act on.
		/// </summary>
		[TestMethod]
		public void FirmwareWindow()
		{
			if (ShotDir is null) { Assert.Inconclusive("set CHIMERA_UI_SHOTS to write screenshots"); return; }

			CoreFirmwareEntry Entry(string core, string id, string display, string description, CoreFirmwareState state, string path, bool required = true)
				=> new()
				{
					CoreName = core,
					Decl = new()
					{
						Id = id,
						Display = display,
						Description = description,
						Size = 8192,
						Required = required,
						Sha1 = "57FE1BDEE955BB48D357E463CCBF129496930B62",
					},
					Path = path,
					State = state,
					Sha1 = state switch
					{
						CoreFirmwareState.Good => "57FE1BDEE955BB48D357E463CCBF129496930B62",
						CoreFirmwareState.Missing => null,
						_ => "9C1D5A0B77E4F3128899AABBCCDDEEFF00112233",
					},
				};

			List<CoreFirmwareEntry> entries =
			[
				Entry("QuickerNesHawk", "bios", "Family Computer Disk System BIOS",
					"The 8 KiB boot rom in the RAM adapter. Any disk image needs it; cartridges do not.",
					CoreFirmwareState.Good, "/home/you/firmware/disksys.rom"),
				Entry("QuickerNesHawk", "expansion", "Expansion audio rom",
					"Optional. Without it the expansion channels are silent.",
					CoreFirmwareState.Missing, null, required: false),
				Entry("synth", "boot", "Boot rom",
					"Runs before the cartridge does.",
					CoreFirmwareState.Unrecognised, "/home/you/firmware/boot-alt.rom"),
				Entry("synth", "char", "Character generator",
					"The font the machine draws text with.",
					CoreFirmwareState.Custom, "/home/you/firmware/custom.bin"),
			];

			using CoreFirmwareForm form = new(() => entries, (_, _) => { });
			form.StartPosition = FormStartPosition.Manual;
			form.Location = new Point(0, 0);
			Shoot(form, "firmware");
		}

		/// <summary>
		/// File &gt; Core Manager, with a roster, a couple of cores installed, and one
		/// core's published versions already fetched. Worth a picture because it is
		/// the window a new install sees first and the only one that shows two lists
		/// that have to agree with each other.
		/// </summary>
		[TestMethod]
		public void CoreManagerWindow()
		{
			if (ShotDir is null) { Assert.Inconclusive("set CHIMERA_UI_SHOTS to write screenshots"); return; }

			List<RosterCore> roster =
			[
				new() { Id = "gpgx", Name = "Genesis Plus GX", Repo = "ToolAssisted-run/chimera-core-gpgx", Systems = [ "GEN", "SMS", "GG", "SG" ] },
				new() { Id = "quickernes", Name = "quickerNES", Repo = "ToolAssisted-run/chimera-core-quickernes", Systems = [ "NES" ] },
				new() { Id = "pcsx2", Name = "PCSX2", Repo = "ToolAssisted-run/chimera-core-pcsx2", Systems = [ "PS2" ] },
				new() { Id = "eka2l1", Name = "EKA2L1", Repo = "ToolAssisted-run/chimera-core-eka2l1", Systems = [ "SYMBIAN" ] },
			];
			roster.Add(new() { Id = "aardvark", Name = "Aardvark", Repo = "someone/chimera-core-aardvark", Systems = [ "ARC" ], IsExternal = true });
			List<DiscoveredCorePackage> installed =
			[
				new() { Name = "Genesis Plus GX", Version = "4ed3532117ad", Path = "/store/gpgx-4ed3532117ad.chimeraCore", Sha1 = new string('a', 40), Systems = [ "GEN" ] },
				new() { Name = "quickerNES", Version = "12d65377b7d3-dirty+local", Path = "/store/quickernes-12d65377b7d3.chimeraCore", Sha1 = new string('b', 40), Systems = [ "NES" ] },
				new() { Name = "Aardvark", Version = "aa11bb22cc33", Path = "/store/aardvark-aa11bb22cc33.chimeraCore", Sha1 = new string('c', 40), Systems = [ "ARC" ] },
			];

			// a canned feed, so the picture shows the window with versions in it
			// rather than the empty state a screenshot of a fresh install would give
			const string feed = @"[
				{ ""tag_name"": ""nightly-2026-09-05"", ""published_at"": ""2026-09-05T05:00:00Z"", ""assets"": [
					{ ""name"": ""gpgx-4ed3532117ad.chimeraCore"", ""browser_download_url"": ""https://example.invalid/b"", ""size"": 6291456 } ] },
				{ ""tag_name"": ""nightly-2026-08-29"", ""published_at"": ""2026-08-29T05:00:00Z"", ""assets"": [
					{ ""name"": ""gpgx-8c50cec0a1b2.chimeraCore"", ""browser_download_url"": ""https://example.invalid/a"", ""size"": 6291456 } ] }
			]";
			var cache = Path.Combine(Path.GetTempPath(), $"chimera-shot-feed-{Guid.NewGuid():N}");
			using CoreManagerForm form = new(
				() => roster,
				() => installed,
				new CoreFeed(new System.Net.Http.HttpClient(new CannedFeed(feed)), cache),
				new CoreInstaller());
			form.StartPosition = FormStartPosition.Manual;
			form.Location = new Point(0, 0);
			form.Show();
			_ = form.Select("Genesis Plus GX");
			form.FetchSelectedVersions().GetAwaiter().GetResult();
			form.SetChecked("Genesis Plus GX", true);   // the header counts what is installed
			Shoot(form, "core-manager");
		}

		/// <summary>
		/// File &gt; Cache Manager: what is on disk that could be worked out again.
		/// Every row is safe to delete, so the picture is mostly about whether the
		/// sizes and the cost line read clearly.
		/// </summary>
		[TestMethod]
		public void CacheManager()
		{
			if (ShotDir is null) { Assert.Inconclusive("set CHIMERA_UI_SHOTS to write screenshots"); return; }
			var items = new List<CacheItem>
			{
				new() { Kind = CacheKind.Project, Label = "Prince of Persia The Sands of Time", Detail = "9f2c14ab7d3e5501", System = "XBOX", Core = "xemu", Games = new[] { "Prince of Persia The Sands of Time.iso" }, ProjectPath = @"D:\TAS\projects\xbox\Prince of Persia.chimeraProject", Path = @"C:\Users\you\AppData\Local\Chimera\Projects\9f2c14ab7d3e5501", Bytes = 2_684_354_560L, LastUsed = new DateTime(2026, 9, 7, 18, 42, 0) },
				new() { Kind = CacheKind.Project, Label = "Street Fighter EX3", Detail = "1a77b0c9de42f318", System = "PS2", Core = "pcsx2", Games = new[] { "Street Fighter EX3 (USA).iso" }, ProjectPath = @"D:\TAS\projects\ps2\Street Fighter EX3.chimeraProject", Path = @"C:\Users\you\AppData\Local\Chimera\Projects\1a77b0c9de42f318", Bytes = 412_876_800L, LastUsed = new DateTime(2026, 9, 2, 9, 15, 0), InUse = true },
				new() { Kind = CacheKind.Project, Label = "an experiment", Detail = "77c0aa31be905412", System = "PS3", Core = "rpcs3", Games = new[] { "GTA San Andreas.iso" }, ProjectPath = @"D:\TAS\scratch\try again.chimeraProject", Path = @"C:\Users\you\AppData\Local\Chimera\Projects\77c0aa31be905412", Bytes = 890_000_000L, LastUsed = new DateTime(2026, 8, 11, 22, 3, 0), Orphaned = true },
				new() { Kind = CacheKind.CorePackage, Label = "xemu", Core = "xemu", Detail = "23df374e", Path = @"C:\Chimera\CoreCache\xemu-23df374e", Bytes = 52_428_800L, LastUsed = new DateTime(2026, 9, 7, 18, 40, 0) },
				new() { Kind = CacheKind.CompiledCode, Label = "rpcs3", Core = "rpcs3", Detail = "e6bdd2b2", Path = @"C:\Chimera\CoreCache\rpcs3\e6bdd2b2", Bytes = 1_073_741_824L, LastUsed = new DateTime(2026, 9, 5, 11, 27, 0) },
				new() { Kind = CacheKind.CoreVersions, Label = "Published core versions", Detail = "", Path = @"C:\Users\you\AppData\Local\Chimera\Cores\.feed-cache", Bytes = 48_128L, LastUsed = new DateTime(2026, 9, 8, 7, 2, 0) },
			};

			using CacheManagerForm form = new(() => items);
			form.StartPosition = FormStartPosition.Manual;
			form.Location = new Point(0, 0);
			form.Show();
			_ = form.Select(@"C:\Users\you\AppData\Local\Chimera\Projects\9f2c14ab7d3e5501");
			Shoot(form, "cache-manager");
		}

		/// <summary>
		/// What a fresh install meets: no cores, so a sentence and a choice.
		/// </summary>
		[TestMethod]
		public void NoCoresPrompt()
		{
			if (ShotDir is null) { Assert.Inconclusive("set CHIMERA_UI_SHOTS to write screenshots"); return; }

			using CoreManagerPrompt form = new();
			form.StartPosition = FormStartPosition.Manual;
			form.Location = new Point(0, 0);
			Shoot(form, "no-cores-prompt");
		}

		private sealed class CannedFeed : System.Net.Http.HttpMessageHandler
		{
			private readonly string _body;

			public CannedFeed(string body) => _body = body;

			protected override System.Threading.Tasks.Task<System.Net.Http.HttpResponseMessage> SendAsync(
				System.Net.Http.HttpRequestMessage request,
				System.Threading.CancellationToken cancellationToken)
				=> System.Threading.Tasks.Task.FromResult(
					new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent(_body) });
		}

		private static ListView ListOfFirst(Form form)
		{
			foreach (Control c in form.Controls)
			{
				if (c is ListView lv) return lv;
			}
			throw new InvalidOperationException("no ListView on the form");
		}
	}
}
