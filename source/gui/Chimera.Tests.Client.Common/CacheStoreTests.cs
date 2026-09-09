using System;
using System.IO;

using Chimera.Client.Common;

namespace Chimera.Tests.Client.Common
{
	/// <summary>
	/// Getting the caches out of the install directory, and the one-time move that
	/// takes an older Chimera's <c>&lt;exe&gt;/CoreCache</c> with it. Every root is
	/// an argument here: a routine that moves directories about is not one to test
	/// against somebody's real install.
	/// </summary>
	[TestClass]
	public class CacheStoreTests
	{
		private string _dir = "";
		private string _from = "";
		private string _packages = "";
		private string _compiled = "";

		[TestInitialize]
		public void MakePlayground()
		{
			_dir = Path.Combine(Path.GetTempPath(), $"chimera-cache-store-{Guid.NewGuid():N}");
			_from = Path.Combine(_dir, "bin", "CoreCache");
			_packages = Path.Combine(_dir, "data", "UnpackedCores");
			_compiled = Path.Combine(_dir, "data", "CompiledCode");
			Directory.CreateDirectory(_from);
		}

		[TestCleanup]
		public void RemovePlayground()
		{
			if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
		}

		private void Fill(string dir, string name, string contents)
		{
			Directory.CreateDirectory(dir);
			File.WriteAllText(Path.Combine(dir, name), contents);
		}

		private void Adopt() => CacheStore.AdoptLegacy(_from, _packages, _compiled);

		/// <summary>
		/// The two layouts shared one directory, and are told apart the way they
		/// are named: a package is <c>&lt;name&gt;-&lt;40 hex&gt;</c> and a
		/// compiled-code directory is a core's name.
		/// </summary>
		[TestMethod]
		public void EachLayoutGoesToItsOwnRoot()
		{
			var sha1 = "4b1e5d554fb41e29b49f1883269a29a85674dbf8";
			Fill(Path.Combine(_from, $"xemu-{sha1}"), "core.wbx", "a package");
			Fill(Path.Combine(_from, "rpcs3", "e6bdd2b2ef65"), "module.obj.gz", "an hour of compiling");

			Adopt();

			Assert.AreEqual("a package", File.ReadAllText(Path.Combine(_packages, $"xemu-{sha1}", "core.wbx")));
			Assert.AreEqual(
				"an hour of compiling",
				File.ReadAllText(Path.Combine(_compiled, "rpcs3", "e6bdd2b2ef65", "module.obj.gz")),
				"moved, not deleted: a PS3 game's compiled code is somebody's evening");
			Assert.IsFalse(Directory.Exists(_from), "and the install directory is left as it was downloaded");
		}

		[TestMethod]
		public void WhatIsAlreadyInTheNewPlaceWins()
		{
			Fill(Path.Combine(_from, "rpcs3", "e6bdd2b2ef65"), "module.obj.gz", "the old one");
			Fill(Path.Combine(_compiled, "rpcs3", "e6bdd2b2ef65"), "module.obj.gz", "the new one");

			Adopt();

			Assert.AreEqual(
				"the new one",
				File.ReadAllText(Path.Combine(_compiled, "rpcs3", "e6bdd2b2ef65", "module.obj.gz")),
				"nothing writes to the old place any more, so what is here is the later answer");
			Assert.IsFalse(Directory.Exists(_from));
		}

		/// <summary>
		/// Housekeeping, not a purge: anything that is not one of the two cache
		/// layouts keeps the directory alive rather than being swept up with them.
		/// </summary>
		[TestMethod]
		public void AFileSomebodyLeftKeepsTheDirectory()
		{
			File.WriteAllText(Path.Combine(_from, "notes.txt"), "mine");
			Fill(Path.Combine(_from, "rpcs3", "e6bdd2b2ef65"), "module.obj.gz", "code");

			Adopt();

			Assert.IsTrue(Directory.Exists(_from));
			Assert.AreEqual("mine", File.ReadAllText(Path.Combine(_from, "notes.txt")));
			Assert.IsTrue(Directory.Exists(Path.Combine(_compiled, "rpcs3", "e6bdd2b2ef65")), "the caches still went");
		}

		[TestMethod]
		public void NothingToAdoptIsNotAnError()
		{
			Directory.Delete(_from);
			Adopt();
			Assert.IsFalse(Directory.Exists(_packages), "and nothing is created for the sake of it");
		}
	}
}
