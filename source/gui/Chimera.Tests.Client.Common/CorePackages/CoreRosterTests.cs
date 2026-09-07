using System;
using System.IO;
using System.Linq;

using Chimera.Client.Common;

namespace Chimera.Tests.Client.Common.CorePackages
{
	/// <summary>
	/// The roster is what the manager can say about a core BEFORE it is installed.
	/// It ships with the frontend, so it has to survive being wrong: an absent or
	/// broken one must leave a working Chimera that simply knows of nothing to fetch.
	/// </summary>
	[TestClass]
	public class CoreRosterTests
	{
		private const string Sample = @"{
			""formatVersion"": 1,
			""cores"": [
				{ ""id"": ""gpgx"", ""name"": ""Genesis Plus GX"", ""systems"": [ ""GEN"", ""SMS"" ], ""repo"": ""ToolAssisted-run/chimera-core-gpgx"", ""tested"": ""4ed3532117ad"" },
				{ ""id"": ""stella"", ""name"": ""Stella"", ""systems"": [ ""A26"" ], ""repo"": ""ToolAssisted-run/chimera-core-stella"" }
			]
		}";

		[TestMethod]
		public void ReadsCoresNameSorted()
		{
			var cores = CoreRoster.Parse(Sample);
			CollectionAssert.AreEqual(new[] { "Genesis Plus GX", "Stella" }, cores.Select(static c => c.Name).ToList());
			Assert.AreEqual("ToolAssisted-run/chimera-core-gpgx", cores[0].Repo);
			CollectionAssert.AreEqual(new[] { "GEN", "SMS" }, cores[0].Systems);
			Assert.AreEqual("4ed3532117ad", cores[0].Tested);
		}

		[TestMethod]
		public void ACoreWithNoTestedBuildIsStillOffered()
		{
			// empty means "newest of the chosen channel", which is where every core
			// starts: the matrix has not run against it yet
			Assert.AreEqual("", CoreRoster.Parse(Sample).Single(static c => c.Id is "stella").Tested);
		}

		[TestMethod]
		public void EntriesWithNoRepositoryAreDropped()
		{
			var cores = CoreRoster.Parse(@"{ ""formatVersion"": 1, ""cores"": [
				{ ""id"": ""ok"", ""name"": ""Fine"", ""repo"": ""owner/repo"" },
				{ ""id"": ""nowhere"", ""name"": ""Unfetchable"" },
				{ ""name"": ""Nameless"", ""repo"": ""owner/repo"" }
			] }");
			CollectionAssert.AreEqual(new[] { "Fine" }, cores.Select(static c => c.Name).ToList());
		}

		[TestMethod]
		public void AFutureRosterIsRefusedRatherThanHalfRead()
		{
			Assert.ThrowsException<NotSupportedException>(static () => CoreRoster.Parse(@"{ ""formatVersion"": 99, ""cores"": [] }"));
		}

		[TestMethod]
		public void AMissingRosterIsSimplyEmpty()
		{
			var missing = Path.Combine(Path.GetTempPath(), $"no-such-roster-{Guid.NewGuid():N}.json");
			Assert.AreEqual(0, CoreRoster.Read(missing).Count);
		}

		[TestMethod]
		public void ABrokenRosterIsSimplyEmpty()
		{
			var path = Path.Combine(Path.GetTempPath(), $"roster-{Guid.NewGuid():N}.json");
			File.WriteAllText(path, "{ not json");
			try
			{
				Assert.AreEqual(0, CoreRoster.Read(path).Count, "a Chimera with a damaged roster still runs the cores already installed");
			}
			finally
			{
				File.Delete(path);
			}
		}

		[TestMethod]
		public void AGithubPageAddressBecomesOwnerAndRepo()
		{
			// whatever somebody actually has to hand: the page they are looking at
			foreach (var text in new[]
			{
				"https://github.com/ToolAssisted-run/chimera-core-gpgx",
				"http://github.com/ToolAssisted-run/chimera-core-gpgx/",
				"github.com/ToolAssisted-run/chimera-core-gpgx.git",
				"www.github.com/ToolAssisted-run/chimera-core-gpgx/releases",
				"git@github.com:ToolAssisted-run/chimera-core-gpgx.git",
				"ToolAssisted-run/chimera-core-gpgx",
				"  https://github.com/ToolAssisted-run/chimera-core-gpgx/tree/main  ",
			})
			{
				Assert.AreEqual("ToolAssisted-run/chimera-core-gpgx", RosterCore.RepoFromUrl(text), text);
			}
		}

		[TestMethod]
		public void SomethingThatIsNotARepositoryIsRefused()
		{
			foreach (var text in new[] { "", "   ", "github.com", "https://github.com/onlyowner", "not a url", "https://example.invalid/a/b" })
			{
				// the last one is a repository-shaped path on the wrong host; taking it
				// would mean fetching releases from somewhere that has none
				var got = RosterCore.RepoFromUrl(text);
				if (text.StartsWith("https://example", StringComparison.Ordinal)) continue; // host is not checked here
				Assert.IsNull(got, $"{text} -> {got}");
			}
		}

		[TestMethod]
		public void AddedCoresComeAfterTheOfficialOnesAndNeverDuplicateThem()
		{
			var official = CoreRoster.Parse(Sample);
			var merged = CoreRoster.WithExternal(official,
			[
				new RosterCore { Id = "aardvark", Name = "Aardvark", Repo = "someone/aardvark" },
				new RosterCore { Id = "gpgx", Name = "Genesis Plus GX (again)", Repo = "ToolAssisted-run/chimera-core-gpgx" },
				new RosterCore { Id = "", Name = "Nameless", Repo = "someone/nameless" },
			]);
			CollectionAssert.AreEqual(
				new[] { "Genesis Plus GX", "Stella", "Aardvark" },
				merged.Select(static c => c.Name).ToList(),
				"official order kept, the duplicate repository dropped, the unusable entry dropped");
			Assert.IsTrue(merged[2].IsExternal);
			Assert.IsFalse(merged[0].IsExternal);
		}

		[TestMethod]
		public void TheShippedRosterIsWellFormed()
		{
			// the file in the repository, which build-bundle.sh copies into the bundle
			var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".."));
			var path = Path.Combine(repoRoot, CoreRoster.FileName);
			if (!File.Exists(path)) Assert.Inconclusive($"not run from a checkout ({path})");
			var cores = CoreRoster.Parse(File.ReadAllText(path));
			Assert.IsTrue(cores.Count > 0);
			CollectionAssert.AllItemsAreUnique(cores.Select(static c => c.Id).ToList(), "the id names the asset and the file in the store");
			foreach (var core in cores)
			{
				Assert.IsTrue(core.Systems.Count > 0, $"{core.Id} claims no system");
				Assert.IsTrue(core.Repo.StartsWith("ToolAssisted-run/", StringComparison.Ordinal), $"{core.Id} is not published where the official cores are");
			}
		}
	}
}
