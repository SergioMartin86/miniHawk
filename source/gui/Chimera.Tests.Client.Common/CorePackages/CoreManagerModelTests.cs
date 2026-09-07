using System;
using System.Collections.Generic;
using System.Linq;

using Chimera.Client.Common;

namespace Chimera.Tests.Client.Common.CorePackages
{
	/// <summary>
	/// What File &gt; Core Manager tells somebody: which cores exist, which they
	/// have, and which have something newer. Tested without a window, so the window
	/// can stay thin.
	/// </summary>
	[TestClass]
	public class CoreManagerModelTests
	{
		private static RosterCore Roster(string id, string name, params string[] systems)
			=> new() { Id = id, Name = name, Repo = $"ToolAssisted-run/chimera-core-{id}", Systems = systems.ToList() };

		private static DiscoveredCorePackage Package(string name, string version, string? path = null)
			=> new() { Name = name, Version = version, Path = path ?? $"/store/{name}-{version}.chimeraCore", Sha1 = new string('a', 40) };

		private static CoreRelease Release(string version, DateTimeOffset when)
			=> new() { Version = version, Tag = $"nightly-{when:yyyy-MM-dd}", Channel = CoreChannel.Nightly, PublishedAt = when, AssetUrl = "https://example.invalid/x" };

		[TestMethod]
		public void ACoreNobodyHasIsStillListed()
		{
			// the roster is the whole point: you cannot download what you cannot see
			var rows = CoreManagerModel.Build([ Roster("gpgx", "Genesis Plus GX", "GEN") ], [ ]);
			Assert.AreEqual(1, rows.Count);
			Assert.IsFalse(rows[0].IsInstalled);
			Assert.IsFalse(rows[0].IsUnclaimed);
			CollectionAssert.AreEqual(new[] { "GEN" }, rows[0].Systems.ToList());
		}

		[TestMethod]
		public void InstalledVersionsAreGatheredOntoTheirCore()
		{
			var rows = CoreManagerModel.Build(
				[ Roster("gpgx", "Genesis Plus GX", "GEN") ],
				[ Package("Genesis Plus GX", "aaaaaaaa"), Package("Genesis Plus GX", "bbbbbbbb") ]);
			Assert.AreEqual(1, rows.Count, "two versions of one core are one row");
			Assert.AreEqual(2, rows[0].Installed.Count);
		}

		[TestMethod]
		public void APackageIsMatchedByItsFileNameWhenItCallsItselfSomethingElse()
		{
			var rows = CoreManagerModel.Build(
				[ Roster("gpgx", "Genesis Plus GX", "GEN") ],
				[ Package("Genplus", "aaaaaaaa", "/store/gpgx-aaaaaaaa.chimeraCore") ]);
			Assert.IsTrue(rows[0].IsInstalled);
			Assert.IsFalse(rows[0].IsUnclaimed);
		}

		[TestMethod]
		public void SomethingInstalledThatTheRosterDoesNotKnowIsListedLast()
		{
			var rows = CoreManagerModel.Build(
				[ Roster("gpgx", "Genesis Plus GX", "GEN") ],
				[ Package("Aardvark", "aaaaaaaa", "/store/aardvark-aaaaaaaa.chimeraCore") ]);
			Assert.AreEqual(2, rows.Count);
			Assert.AreEqual("Genesis Plus GX", rows[0].Name, "official cores come first however they sort by name");
			Assert.IsTrue(rows[0].IsOfficial);
			Assert.IsTrue(rows[1].IsUnclaimed, "a core from elsewhere is shown, and cannot be fetched or updated");
			Assert.IsFalse(rows[1].IsOfficial);
			Assert.IsTrue(rows[1].RowGoesWhenRemoved, "nothing would be left to list");
			Assert.AreEqual("Aardvark", rows[1].Name);
		}

		[TestMethod]
		public void AnExternalCoreSitsBelowTheOfficialOnes()
		{
			var external = new RosterCore { Id = "zzz", Name = "Aardvark", Repo = "someone/chimera-core-aardvark", IsExternal = true };
			var rows = CoreManagerModel.Build([ Roster("gpgx", "Genesis Plus GX", "GEN"), external ], [ ]);
			// sorts after every official core however its name sorts
			Assert.AreEqual("Genesis Plus GX", rows[0].Name);
			Assert.AreEqual("Aardvark", rows[1].Name);
			Assert.IsFalse(rows[1].IsOfficial);
			Assert.IsFalse(rows[1].IsUnclaimed, "it is claimed - by an entry somebody added");
			Assert.IsTrue(rows[1].RowGoesWhenRemoved);
		}

		[TestMethod]
		public void AnOfficialCoreKeepsItsRowWhenRemoved()
		{
			var rows = CoreManagerModel.Build([ Roster("gpgx", "Genesis Plus GX", "GEN") ], [ Package("Genesis Plus GX", "aaaaaaaa") ]);
			Assert.IsFalse(rows[0].RowGoesWhenRemoved, "it can always be installed again from the roster");
		}

		[TestMethod]
		public void AnUpdateIsTheNewestPublishedVersionNobodyHas()
		{
			var feeds = new Dictionary<string, IReadOnlyList<CoreRelease>>
			{
				["gpgx"] = [ Release("cccccccc", DateTimeOffset.Parse("2026-09-07T05:00:00Z")), Release("bbbbbbbb", DateTimeOffset.Parse("2026-09-05T05:00:00Z")) ],
			};
			var rows = CoreManagerModel.Build([ Roster("gpgx", "Genesis Plus GX", "GEN") ], [ Package("Genesis Plus GX", "bbbbbbbb") ], feeds);
			Assert.AreEqual("cccccccc", rows[0].Update?.Version);
			Assert.IsTrue(rows[0].Has(feeds["gpgx"][1]));
			Assert.IsFalse(rows[0].Has(feeds["gpgx"][0]));
		}

		[TestMethod]
		public void HavingTheNewestMeansThereIsNoUpdate()
		{
			var feeds = new Dictionary<string, IReadOnlyList<CoreRelease>>
			{
				["gpgx"] = [ Release("cccccccc", DateTimeOffset.Parse("2026-09-07T05:00:00Z")) ],
			};
			var rows = CoreManagerModel.Build([ Roster("gpgx", "Genesis Plus GX", "GEN") ], [ Package("Genesis Plus GX", "cccccccc") ], feeds);
			Assert.IsNull(rows[0].Update);
		}

		[TestMethod]
		public void AnOlderVersionNobodyHasIsNotAnUpdate()
		{
			// holding the newest build and not bothering with last month's is the
			// normal state of an install, and must not read as "update available"
			var feeds = new Dictionary<string, IReadOnlyList<CoreRelease>>
			{
				["gpgx"] = [ Release("cccccccc", DateTimeOffset.Parse("2026-09-07T05:00:00Z")), Release("bbbbbbbb", DateTimeOffset.Parse("2026-09-05T05:00:00Z")) ],
			};
			var rows = CoreManagerModel.Build([ Roster("gpgx", "Genesis Plus GX", "GEN") ], [ Package("Genesis Plus GX", "cccccccc") ], feeds);
			Assert.IsNull(rows[0].Update);
		}

		[TestMethod]
		public void ACoreThatIsNotInstalledHasNoUpdate()
		{
			var feeds = new Dictionary<string, IReadOnlyList<CoreRelease>>
			{
				["gpgx"] = [ Release("cccccccc", DateTimeOffset.Parse("2026-09-07T05:00:00Z")) ],
			};
			var rows = CoreManagerModel.Build([ Roster("gpgx", "Genesis Plus GX", "GEN") ], [ ], feeds);
			Assert.IsNull(rows[0].Update, "it is missing, not out of date");
		}

		[TestMethod]
		public void ADevelopmentBuildIsNotAnUpdateToAPublishedOne()
		{
			var feeds = new Dictionary<string, IReadOnlyList<CoreRelease>>
			{
				["gpgx"] =
				[
					new CoreRelease { Version = "dddddddd", Tag = "dev", Channel = CoreChannel.Dev, PublishedAt = DateTimeOffset.Parse("2026-09-08T05:00:00Z"), AssetUrl = "https://example.invalid/d" },
					Release("cccccccc", DateTimeOffset.Parse("2026-09-07T05:00:00Z")),
				],
			};
			var rows = CoreManagerModel.Build([ Roster("gpgx", "Genesis Plus GX", "GEN") ], [ Package("Genesis Plus GX", "cccccccc") ], feeds);
			Assert.IsNull(rows[0].Update, "dev is newer and is not offered by default, so it is not an update");
		}

		[TestMethod]
		public void AFeedThatCouldNotAnswerSaysSoOnItsOwnRow()
		{
			var rows = CoreManagerModel.Build(
				[ Roster("gpgx", "Genesis Plus GX", "GEN"), Roster("stella", "Stella", "A26") ],
				[ ],
				feedErrors: new Dictionary<string, string> { ["stella"] = "could not reach GitHub" });
			Assert.IsNull(rows.Single(static r => r.Name is "Genesis Plus GX").FeedError);
			Assert.AreEqual("could not reach GitHub", rows.Single(static r => r.Name is "Stella").FeedError);
		}

		[TestMethod]
		public void NothingIsFetchedToBuildTheList()
		{
			// every row starts with no versions: the manager asks GitHub per core,
			// when somebody presses something, and never to draw the window
			var rows = CoreManagerModel.Build([ Roster("gpgx", "Genesis Plus GX", "GEN") ], [ ]);
			Assert.AreEqual(0, rows[0].Available.Count);
			Assert.IsNull(rows[0].Update);
		}
		/// <summary>
		/// The date column. It describes the version the Installed column names, not
		/// simply the newest thing published - somebody holding an old build wants to
		/// see when THAT was made.
		/// </summary>
		[TestMethod]
		public void TheDateIsTheInstalledVersionsNotTheNewest()
		{
			var older = Release("aaaaaaaaaaaa", DateTimeOffset.Parse("2026-09-01T05:00:00Z"));
			var newer = Release("bbbbbbbbbbbb", DateTimeOffset.Parse("2026-09-05T05:00:00Z"));
			var row = new CoreManagerRow
			{
				Core = Roster("gpgx", "Genesis Plus GX", "GEN"),
				Installed = [ Package("Genesis Plus GX", "aaaaaaaaaaaa") ],
				Available = [ newer, older ],
			};

			Assert.AreEqual("2026-09-01", row.PublishedAt!.Value.ToString("yyyy-MM-dd"));
		}

		/// <summary>Nothing fetched means no date, rather than a guessed one.</summary>
		[TestMethod]
		public void ADateIsAbsentUntilTheVersionsHaveBeenAskedFor()
		{
			var row = new CoreManagerRow
			{
				Core = Roster("gpgx", "Genesis Plus GX", "GEN"),
				Installed = [ Package("Genesis Plus GX", "aaaaaaaaaaaa") ],
			};

			Assert.IsNull(row.PublishedAt);
		}

		/// <summary>
		/// A size for a core that is not installed: there is no file to measure, so
		/// the release's own figure is what the column shows.
		/// </summary>
		[TestMethod]
		public void ASizeComesFromTheReleaseWhenThereIsNoFileToMeasure()
		{
			var release = Release("cccccccccccc", DateTimeOffset.Parse("2026-09-07T11:00:00Z"));
			var row = new CoreManagerRow
			{
				Core = Roster("gpgx", "Genesis Plus GX", "GEN"),
				Available = [ new CoreRelease
				{
					Version = release.Version,
					Tag = release.Tag,
					Channel = release.Channel,
					PublishedAt = release.PublishedAt,
					AssetUrl = release.AssetUrl,
					AssetSize = 3145728,
				} ],
			};

			Assert.AreEqual(3145728, row.SizeBytes);
		}

	}
}
