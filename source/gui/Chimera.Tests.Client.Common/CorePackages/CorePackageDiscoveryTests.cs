using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

using Chimera.Client.Common;
using Chimera.Emulation.Common.Waterbox;

namespace Chimera.Tests.Client.Common.CorePackages
{
	/// <summary>
	/// Discovery is the half of core loading that must work before anything is
	/// loaded: it decides what exists, what it is, and whether the frontend is
	/// allowed to touch it. It is all filesystem reading and bookkeeping, so it is
	/// tested here against real files in a temp directory rather than through the UI.
	/// </summary>
	[TestClass]
	public class CorePackageDiscoveryTests
	{
		private string _root;

		[TestInitialize]
		public void SetUp()
		{
			_root = Path.Combine(Path.GetTempPath(), $"chimera-discovery-{Guid.NewGuid():N}");
			Directory.CreateDirectory(_root);
		}

		[TestCleanup]
		public void TearDown()
		{
			try
			{
				Directory.Delete(_root, recursive: true);
			}
			catch (IOException)
			{
				// a leftover temp dir is not a test failure
			}
		}

		private static string WaterboxConfigJson(string coreName, string systemId, string ext)
			=> $@"{{ ""coreName"": ""{coreName}"", ""systemId"": ""{systemId}"", ""extensions"": {{ ""{ext}"": ""{systemId}"" }} }}";

		private string MakeDir(string name, params (string Name, string Content)[] files)
		{
			var dir = Path.Combine(_root, name);
			Directory.CreateDirectory(dir);
			foreach (var (fileName, content) in files) File.WriteAllText(Path.Combine(dir, fileName), content);
			return dir;
		}

		private string MakeZip(string name, params (string Name, string Content)[] files)
		{
			var path = Path.Combine(_root, name);
			using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
			{
				foreach (var (fileName, content) in files)
				{
					using var writer = new StreamWriter(zip.CreateEntry(fileName).Open());
					writer.Write(content);
				}
			}
			return path;
		}

		private string MakeWaterboxZip(string name, string coreName, string systemId = "NES", string ext = ".nes")
			=> MakeZip(name, ("core.wbx", $"not really an elf, but discovery never reads it ({coreName})"), ("waterbox.config", WaterboxConfigJson(coreName, systemId, ext)));

		[TestMethod]
		public void FindsWaterboxZip()
		{
			_ = MakeWaterboxZip("quickernes.chimeraCore", "quickerNES");
			var found = CorePackageDiscovery.Scan([ _root ]);
			Assert.AreEqual(1, found.Count);
			Assert.AreEqual("quickerNES", found[0].Name);
			CollectionAssert.AreEqual(new[] { "NES" }, found[0].Systems.ToList());
			Assert.AreEqual("NES", found[0].Extensions[".nes"]);
			Assert.IsFalse(found[0].IsDirectoryForm);
			Assert.IsNull(found[0].Error);
			Assert.AreEqual(40, found[0].Sha1.Length, "a zip package's identity is the SHA1 of the file");
		}

		[TestMethod]
		public void FindsWaterboxDirectory()
		{
			_ = MakeDir("devcore", ("core.wbx", "stub"), ("waterbox.config", WaterboxConfigJson("devCore", "SYNTH", ".synth")));
			var found = CorePackageDiscovery.Scan([ _root ]);
			Assert.AreEqual(1, found.Count);
			Assert.AreEqual("devCore", found[0].Name);
			Assert.IsTrue(found[0].IsDirectoryForm);
			Assert.IsNull(found[0].Sha1, "a directory has no file to hash, so it has no identity");
			Assert.AreEqual(found[0].Path, found[0].Key, "without a hash, the path is the only stable key");
		}

		[TestMethod]
		public void FindsManifestPackageAndDerivesItsSystems()
		{
			_ = MakeZip(
				"adapter.chimeraCore",
				("chimera-core.json", @"{ ""formatVersion"": 1, ""name"": ""AdapterCore"", ""assembly"": ""Adapter.dll"", ""extensions"": { "".AAA"": ""SYSA"", "".bbb"": ""SYSB"" } }"));
			var found = CorePackageDiscovery.Scan([ _root ]);
			Assert.AreEqual(1, found.Count);
			Assert.AreEqual("AdapterCore", found[0].Name);
			CollectionAssert.AreEqual(new[] { "SYSA", "SYSB" }, found[0].Systems.ToList());
			Assert.IsTrue(found[0].Extensions.ContainsKey(".aaa"), "extensions are matched lowercase, so they must be stored that way");
		}

		[TestMethod]
		public void IgnoresThingsThatAreNotPackages()
		{
			_ = MakeZip("some-roms.zip", ("mario.nes", "not a core"));
			// a real package under the WRONG extension is not scanned either: the
			// extension is what a scan goes by, so Cores/ never has to be opened zip
			// by zip to find out what is in it
			_ = MakeZip("mislabelled.zip", ("core.wbx", "stub"), ("waterbox.config", WaterboxConfigJson("Hidden", "NES", ".nes")));
			_ = MakeDir("savestates", ("slot1.state", "not a core"));
			File.WriteAllText(Path.Combine(_root, "readme.txt"), "hello");
			Assert.AreEqual(0, CorePackageDiscovery.Scan([ _root ]).Count);
		}

		[TestMethod]
		public void ListsBrokenPackagesWithTheirErrorRatherThanHidingThem()
		{
			// looks like a package (it has the marker files) but cannot be understood
			_ = MakeZip("broken.chimeraCore", ("core.wbx", "stub"), ("waterbox.config", "{ this is not json"));
			_ = MakeZip("future.chimeraCore", ("chimera-core.json", @"{ ""formatVersion"": 99, ""name"": ""FromTheFuture"" }"));
			var found = CorePackageDiscovery.Scan([ _root ]).OrderBy(static p => p.Name).ToList();
			Assert.AreEqual(2, found.Count);
			Assert.IsTrue(found.All(static p => p.Error is not null), "both are unreadable and must say so");
			Assert.IsTrue(found.All(static p => !p.IsLoadable));
			Assert.IsTrue(found.Any(static p => p.Error.Contains("99")), "the version mismatch must name the version it saw");
		}

		[TestMethod]
		public void MissingSystemIdIsAnErrorNotACrash()
		{
			_ = MakeZip("nosys.chimeraCore", ("core.wbx", "stub"), ("waterbox.config", @"{ ""coreName"": ""Nameless"" }"));
			var found = CorePackageDiscovery.Scan([ _root ]);
			Assert.AreEqual(1, found.Count);
			StringAssert.Contains(found[0].Error, "systemId");
		}

		[TestMethod]
		public void MissingSearchDirectoriesAreSkippedSilently()
		{
			// a fresh checkout has no Cores/ - that is normal, not an error
			var found = CorePackageDiscovery.Scan([ Path.Combine(_root, "nope"), _root ]);
			Assert.AreEqual(0, found.Count);
		}

		[TestMethod]
		public void SameSearchDirectoryTwiceYieldsOneEntry()
		{
			_ = MakeWaterboxZip("core.chimeraCore", "OnlyOnce");
			Assert.AreEqual(1, CorePackageDiscovery.Scan([ _root, _root, _root + Path.DirectorySeparatorChar ]).Count);
		}

		[TestMethod]
		public void IdenticalPackagesInTwoDirectoriesCollapseToOne()
		{
			var dirA = MakeDir("a");
			var dirB = MakeDir("b");
			var zip = MakeWaterboxZip("core.chimeraCore", "Twin");
			File.Copy(zip, Path.Combine(dirA, "core.chimeraCore"));
			File.Copy(zip, Path.Combine(dirB, "renamed.chimeraCore"));
			File.Delete(zip);
			var found = CorePackageDiscovery.Scan([ dirA, dirB ]);
			Assert.AreEqual(1, found.Count, "same bytes = same package, whatever it is called or wherever it sits");
			StringAssert.Contains(found[0].Path, dirA, "the first search path wins");
		}

		[TestMethod]
		public void ResultsAreNameSorted()
		{
			_ = MakeWaterboxZip("z.chimeraCore", "Alpha");
			_ = MakeWaterboxZip("a.chimeraCore", "Zulu");
			_ = MakeWaterboxZip("m.chimeraCore", "mike");
			CollectionAssert.AreEqual(
				new[] { "Alpha", "mike", "Zulu" },
				CorePackageDiscovery.Scan([ _root ]).Select(static p => p.Name).ToList());
		}

		[TestMethod]
		public void APackageThatDeclaresNoAbiIsTheFirstOne()
		{
			// every core published before the field existed says nothing, and all of
			// them are ABI 1 by definition
			_ = MakeWaterboxZip("old.chimeraCore", "Published Before The Field");
			var found = CorePackageDiscovery.Scan([ _root ]);
			Assert.AreEqual(1, found[0].Abi);
			Assert.IsFalse(found[0].IsAbiIncompatible);
			Assert.IsNull(found[0].Error);
		}

		[TestMethod]
		public void APackageFromANewerAbiIsListedAndRefused()
		{
			// the point of listing it: somebody who just downloaded this must be told
			// which of the two things they are holding is too old, not left looking at
			// a Cores folder the frontend pretends is empty
			_ = MakeZip(
				"future.chimeraCore",
				("core.wbx", "stub"),
				("waterbox.config", $@"{{ ""coreName"": ""FromTheFuture"", ""systemId"": ""NES"", ""abi"": {GuestAbi.Current + 1} }}"));
			var found = CorePackageDiscovery.Scan([ _root ]);
			Assert.AreEqual(1, found.Count, "it is still listed");
			Assert.AreEqual("FromTheFuture", found[0].Name);
			Assert.IsTrue(found[0].IsAbiIncompatible);
			Assert.IsFalse(found[0].IsLoadable);
			StringAssert.Contains(found[0].Error, "Update Chimera");
		}

		[TestMethod]
		public void ARefusedPackageIsNotConfusedWithABrokenOne()
		{
			_ = MakeZip(
				"future.chimeraCore",
				("core.wbx", "stub"),
				("waterbox.config", $@"{{ ""coreName"": ""FromTheFuture"", ""systemId"": ""NES"", ""abi"": {GuestAbi.Current + 1} }}"));
			_ = MakeZip("broken.chimeraCore", ("core.wbx", "stub"), ("waterbox.config", "{ not json"));
			var found = CorePackageDiscovery.Scan([ _root ]);
			Assert.AreEqual(2, found.Count);
			Assert.IsTrue(found.Single(static p => p.Name is "FromTheFuture").IsAbiIncompatible);
			var broken = found.Single(static p => p.Name is not "FromTheFuture");
			Assert.IsNotNull(broken.Error);
			Assert.IsFalse(broken.IsAbiIncompatible, "a corrupt package is not a package for another Chimera");
		}

		[TestMethod]
		public void SearchPathsAlwaysStartWithTheDefaultCoresFolder()
		{
			Config config = new();
			config.CorePackagePaths.Add("/somewhere/else");
			var paths = CorePackageDiscovery.SearchPaths(config);
			Assert.AreEqual(CorePackageDiscovery.DefaultSearchPath, paths[0]);
			Assert.AreEqual("/somewhere/else", paths[1]);
		}
	}
}
