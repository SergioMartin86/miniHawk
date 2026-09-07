using System;
using System.IO;
using System.Linq;

using Chimera.Client.Common;

namespace Chimera.Tests.Client.Common.CorePackages
{
	/// <summary>
	/// The store is where downloaded cores land, and its naming is what makes two
	/// versions of one core able to sit beside each other - which is the whole of
	/// version picking, since discovery lists them separately and opening one is
	/// choosing it.
	/// </summary>
	[TestClass]
	public class CoreStoreTests
	{
		[TestMethod]
		public void AVersionIsPartOfTheFileName()
		{
			Assert.AreEqual("gpgx-4ed3532117ad.chimeraCore", CoreStore.FileNameFor("gpgx", "4ed3532117ad"));
			Assert.AreNotEqual(
				CoreStore.FileNameFor("gpgx", "4ed3532117ad"),
				CoreStore.FileNameFor("gpgx", "8c50cec0a1b2"),
				"two versions of one core have to be able to sit side by side");
		}

		[TestMethod]
		public void AVersionlessPackageIsStillNamed()
		{
			Assert.AreEqual("gpgx.chimeraCore", CoreStore.FileNameFor("gpgx", ""));
		}

		[TestMethod]
		public void AVersionCannotEscapeTheStore()
		{
			// it arrives as a git tag, so it can hold anything a tag can
			var name = CoreStore.FileNameFor("gpgx", "../../etc/passwd");
			Assert.IsFalse(name.Contains('/'), name);
			Assert.IsFalse(name.Contains('\\'), name);
			Assert.AreEqual(name, Path.GetFileName(name));
		}

		[TestMethod]
		public void AVersionDoesNotMakeAHiddenFile()
		{
			StringAssert.StartsWith(CoreStore.FileNameFor("gpgx", ".hidden"), "gpgx-hidden");
		}

		[TestMethod]
		public void AdoptReplacesTheSameVersionInPlace()
		{
			var store = CoreStore.Ensure();
			var name = $"chimera-test-{Guid.NewGuid():N}.chimeraCore";
			var target = Path.Combine(store, name);
			try
			{
				var first = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
				File.WriteAllText(first, "first");
				Assert.AreEqual(target, CoreStore.Adopt(first, name));
				Assert.IsFalse(File.Exists(first), "the download is moved, not copied");

				// re-downloading the same version is how a truncated file gets fixed
				var second = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
				File.WriteAllText(second, "second");
				_ = CoreStore.Adopt(second, name);
				Assert.AreEqual("second", File.ReadAllText(target));

				Assert.IsTrue(CoreStore.Installed().Contains(target));
				Assert.IsTrue(CoreStore.Owns(target));
			}
			finally
			{
				File.Delete(target);
			}
		}

		[TestMethod]
		public void TheManagerOnlyOwnsItsOwnStore()
		{
			Assert.IsFalse(
				CoreStore.Owns(Path.Combine(CorePackageDiscovery.DefaultSearchPath, "handplaced.chimeraCore")),
				"a package somebody put in the bundle's Cores/ is not the manager's to delete");
		}
	}
}
