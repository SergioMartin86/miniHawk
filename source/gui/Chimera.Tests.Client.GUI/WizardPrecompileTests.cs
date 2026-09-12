using System;
using System.Collections.Generic;
using System.IO;

using Chimera.Client.Common;
using Chimera.Client.GUI;
using Chimera.Emulation.Common.Waterbox;

namespace Chimera.Tests.Client.GUI
{
	/// <summary>
	/// The compile step: a core that translates a game's code before running it
	/// says so here, lists what it compiled, and the project cannot be created
	/// with any of it missing (docs/compile-cache.md).
	/// </summary>
	[TestClass]
	public class WizardPrecompileTests
	{
		private static WaterboxConfig CfgThatCompiles()
			=> WaterboxConfig.FromJson("""
				{
				  "coreName": "compiles",
				  "systemId": "PS3",
				  "version": "1",
				  "precompile": true,
				  "video": { "width": 640, "height": 480 },
				  "audio": { "samplesPerFrame": 1024 },
				  "input": { "buttons": [] }
				}
				""");

		private static WaterboxConfig CfgThatDoesNot()
			=> WaterboxConfig.FromJson("""
				{
				  "coreName": "plain",
				  "systemId": "NES",
				  "video": { "width": 256, "height": 240 },
				  "audio": { "samplesPerFrame": 1024 },
				  "input": { "buttons": [] }
				}
				""");

		private static string TempDir()
		{
			var dir = Path.Combine(Path.GetTempPath(), "chimera-precompile-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(dir);
			return dir;
		}

		private static string MakeRom(string dir)
		{
			var path = Path.Combine(dir, "game.iso");
			File.WriteAllText(path, "not a real disc, but it hashes");
			return path;
		}

		/// <summary>Writes an object and the manifest that says the game needs it.</summary>
		private static void Compiled(string cacheRoot, WaterboxConfig cfg, string romPath, string name, string contents)
		{
			var dir = WaterboxCore.CoreCacheDirectoryFor(cacheRoot, cfg.CoreName, cfg.Version);
			var file = Path.Combine(dir, name);
			Directory.CreateDirectory(Path.GetDirectoryName(file)!);
			File.WriteAllText(file, contents);
			var romSha1 = Sha1Of(romPath);
			CoreCacheManifest manifest = new()
			{
				RomName = Path.GetFileName(romPath),
				RomSha1 = romSha1,
				Files = [ new CoreCacheFile { Name = name, Sha1 = CoreCacheManifest.HashOf(dir, name) } ],
			};
			manifest.Save(dir, romSha1);
		}

		private static string Sha1Of(string path)
		{
			using var stream = File.OpenRead(path);
			using var sha1 = System.Security.Cryptography.SHA1.Create();
			return BitConverter.ToString(sha1.ComputeHash(stream)).Replace("-", "");
		}

		[TestMethod]
		public void ACoreThatCompilesNothingIsNeverAsked()
		{
			var dir = TempDir();
			try
			{
				using NewProjectWizard form = new([ ], static _ => [ ]);
				form.Show();
				form.UsePrecompileFrom(CfgThatDoesNot(), dir, MakeRom(dir));
				Assert.AreEqual(0, form.PrecompileEntries.Count, "a core that compiles nothing has nothing to list");
			}
			finally
			{
				Directory.Delete(dir, recursive: true);
			}
		}

		/// <summary>
		/// The sessions are CHILD PROCESSES: they read the config off disk, and
		/// what it remembers is their only source of firmware. So whatever the
		/// firmware page settled has to be written there BEFORE they start - the
		/// owner remembering it after this wizard returns is too late, and a core
		/// that needs firmware then compiles nothing at all, which is what a PS3
		/// project did.
		/// </summary>
		[TestMethod]
		public void TheFirmwareIsRememberedBeforeTheSessionsStart()
		{
			var dir = TempDir();
			try
			{
				// a core has to be CHOSEN for a compile to start at all: the
				// sessions are spawned for a package, and RunPrecompile leaves
				// immediately without one
				var pkg = new DiscoveredCorePackage { Path = Path.Combine(dir, "fake.chimeraCore"), Name = "compiles" };
				var order = new List<string>();
				using NewProjectWizard form = new(
					[ pkg ],
					static _ => [ ],
					rememberFirmwareNow: (_, _) => order.Add("remembered"));
				form.Show();
				form.UsePrecompileFrom(CfgThatCompiles(), dir, MakeRom(dir));
				form.RunPrecompileForTest();
				CollectionAssert.Contains(order, "remembered",
					"the firmware must reach the config before a session is spawned");
			}
			finally
			{
				Directory.Delete(dir, recursive: true);
			}
		}

		[TestMethod]
		public void AGameNotCompiledYetCannotBeCreated()
		{
			var dir = TempDir();
			try
			{
				using NewProjectWizard form = new([ ], static _ => [ ]);
				form.Show();
				form.UsePrecompileFrom(CfgThatCompiles(), dir, MakeRom(dir));
				Assert.AreEqual(0, form.PrecompileEntries.Count);
				Assert.IsFalse(form.PrecompileReady, "nothing is compiled, so there is nothing to create a project against");
				Assert.IsFalse(form.CreateEnabled);
			}
			finally
			{
				Directory.Delete(dir, recursive: true);
			}
		}

		[TestMethod]
		public void ACompiledGameListsItsModulesInGreenAndCanBeCreated()
		{
			var dir = TempDir();
			try
			{
				var cfg = CfgThatCompiles();
				var rom = MakeRom(dir);
				Compiled(dir, cfg, rom, "cache/ppu-abc/module.obj.gz", "compiled bytes");
				using NewProjectWizard form = new([ ], static _ => [ ]);
				form.Show();
				form.UsePrecompileFrom(cfg, dir, rom);
				var entries = form.PrecompileEntries;
				Assert.AreEqual(1, entries.Count);
				Assert.AreEqual("cache/ppu-abc/module.obj.gz", entries[0].Name);
				Assert.AreEqual(40, entries[0].Sha1.Length, "the hash is shown, not just the name");
				Assert.IsTrue(entries[0].Present, "an object that is there and unchanged is green");
				Assert.IsTrue(form.PrecompileReady);
				Assert.IsTrue(form.CreateEnabled);
			}
			finally
			{
				Directory.Delete(dir, recursive: true);
			}
		}

		[TestMethod]
		public void AModuleThatChangedUnderneathIsNotAccepted()
		{
			var dir = TempDir();
			try
			{
				var cfg = CfgThatCompiles();
				var rom = MakeRom(dir);
				Compiled(dir, cfg, rom, "cache/ppu-abc/module.obj.gz", "compiled bytes");
				// something else wrote over it: same name, other content
				var file = Path.Combine(WaterboxCore.CoreCacheDirectoryFor(dir, cfg.CoreName, cfg.Version), "cache/ppu-abc/module.obj.gz");
				File.WriteAllText(file, "not what was compiled");
				using NewProjectWizard form = new([ ], static _ => [ ]);
				form.Show();
				form.UsePrecompileFrom(cfg, dir, rom);
				Assert.AreEqual(1, form.PrecompileEntries.Count);
				Assert.IsFalse(form.PrecompileEntries[0].Present, "a module that is not what it was is not compiled");
				Assert.IsFalse(form.PrecompileReady, "and the project cannot be created against it");
			}
			finally
			{
				Directory.Delete(dir, recursive: true);
			}
		}
	
	[TestMethod]
	public void APathWithSpacesSurvivesTheTripToTheChild()
	{
		// The precompile bug: every session died at once on a game whose folder
		// had a space. Quote() escaped EVERY backslash, so C:\my games\x.iso
		// reached the child as C:\\my games\\x.iso, which does not exist.
		// CommandLineToArgvW treats a backslash as special ONLY before a quote.
		var quote = typeof(SelfProcess).GetMethod("Quote",
			System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
		Assert.IsNotNull(quote, "Quote must exist to be tested");
		string Q(string a) => (string)quote.Invoke(null, [ a ]);

		Assert.AreEqual("\"C:\\my games\\x.iso\"", Q("C:\\my games\\x.iso"),
			"separators inside a quoted path must not be doubled");
		Assert.AreEqual("C:\\plain\\x.iso", Q("C:\\plain\\x.iso"),
			"a path with no space needs no quoting at all");
		Assert.AreEqual("\"--config=C:\\a b\\c.ini\"", Q("--config=C:\\a b\\c.ini"),
			"a --flag=value pair quotes as one argument");
		// a trailing run must double, or the closing quote stops being a delimiter
		Assert.AreEqual("\"C:\\a b\\\\\"", Q("C:\\a b\\"),
			"a trailing backslash run doubles so the quote still closes");
	}

	[TestMethod]
	public void ASessionsRefusalIsCarriedBackToThePerson()
	{
		// The PS3 bug: RPCS3 declares PS3UPDAT.PUP "required": false, so the
		// wizard's firmware page counted the need satisfied with no path, wrote
		// nothing to the config, and every session was then refused at boot. The
		// parent said so on Console.Error - which on Windows, from a GUI-subsystem
		// process, goes nowhere. The person saw "the compile was stopped" and no
		// reason anywhere. A refusal has to survive the trip back.
		var line = typeof(PrecompileOrchestrator).GetNestedType("RefusalLine",
			System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
		var rx = (System.Text.RegularExpressions.Regex)typeof(PrecompileOrchestrator)
			.GetField("RefusalLine", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
			.GetValue(null);
		var refusal = "[headless] text: RPCS3: a PS3 disc needs the system software (PS3UPDAT.PUP), and none was provided";
		var m = rx.Match(refusal);
		Assert.IsTrue(m.Success, "the modal a headless session could not show is what says why");
		Assert.AreEqual(
			"RPCS3: a PS3 disc needs the system software (PS3UPDAT.PUP), and none was provided",
			m.Groups[1].Value);
		// and the lines that are not refusals must not be mistaken for one
		Assert.IsFalse(rx.IsMatch("[headless] caption: PS3 load error"));
		Assert.IsFalse(rx.IsMatch("Precompiled 3/8 modules"));
	}
	/// <summary>
	/// Reported from use: "3 of 8 sessions failed without saying why". A session
	/// the operating system kills prints nothing on its way out, so its exit code
	/// is the only evidence there is - and the orchestrator was holding it while
	/// telling the user nothing. Every code now says something.
	/// </summary>
	[TestMethod]
	public void ADeadSessionAlwaysSaysHowItDied()
	{
		StringAssert.Contains(PrecompileOrchestrator.WhyItDied(137), "out of memory");
		StringAssert.Contains(PrecompileOrchestrator.WhyItDied(139), "crashed");
		StringAssert.Contains(PrecompileOrchestrator.WhyItDied(134), "abort");
		// the codes Windows gives, which is where this was reported
		StringAssert.Contains(PrecompileOrchestrator.WhyItDied(unchecked((int)0xC0000005)), "access violation");
		StringAssert.Contains(PrecompileOrchestrator.WhyItDied(unchecked((int)0xC0000017)), "out of memory");
		// and anything unrecognised still names itself rather than saying nothing
		StringAssert.Contains(PrecompileOrchestrator.WhyItDied(42), "42");
		foreach (var code in new[] { 1, 2, 42, 134, 137, 139, -1, unchecked((int)0xC0000005) })
		{
			Assert.IsFalse(string.IsNullOrWhiteSpace(PrecompileOrchestrator.WhyItDied(code)));
		}
	}

	/// <summary>
	/// Only a death the machine attributes to memory earns a retry with fewer
	/// sessions. Retrying a genuine crash would just take twice as long to fail.
	/// </summary>
	[TestMethod]
	public void OnlyAMemoryDeathIsWorthRetrying()
	{
		Assert.IsTrue(PrecompileOrchestrator.DiedForWantOfMemory(137));
		Assert.IsTrue(PrecompileOrchestrator.DiedForWantOfMemory(unchecked((int)0xC0000017)));
		Assert.IsFalse(PrecompileOrchestrator.DiedForWantOfMemory(139));
		Assert.IsFalse(PrecompileOrchestrator.DiedForWantOfMemory(134));
		Assert.IsFalse(PrecompileOrchestrator.DiedForWantOfMemory(1));
	}

	/// <summary>
	/// One session compiling Ultra Street Fighter IV was measured peaking at
	/// 8.32 GB, so the sessions have to be counted against memory with something
	/// left over for the system - never none, never more than eight, never more
	/// than half the cores.
	/// </summary>
	[TestMethod]
	public void SessionsAreBoundedByTheMachine()
	{
		var workers = PrecompileOrchestrator.Workers;
		Assert.IsTrue(workers >= 1, "there is always at least one session");
		Assert.IsTrue(workers <= 8, $"never more than eight, got {workers}");
		Assert.IsTrue(workers <= Math.Max(1, Environment.ProcessorCount / 2),
			$"never more than half the cores ({Environment.ProcessorCount}), got {workers}");
	}
}
}
