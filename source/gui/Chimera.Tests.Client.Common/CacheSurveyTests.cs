using System;
using System.IO;
using System.Linq;

using Chimera.Client.Common;

namespace Chimera.Tests.Client.Common
{
	/// <summary>
	/// What the cache manager lists, and the one rule that makes such a window
	/// safe to offer: everything in it can be deleted, and the cost is
	/// recomputation rather than work. What must NOT appear is as much the point
	/// as what does.
	/// </summary>
	[TestClass]
	public class CacheSurveyTests
	{
		private static string _dir = "";
		private static string _dataHomeWas = "";

		[ClassInitialize]
		public static void MakePlayground(TestContext _)
		{
			_dir = Path.Combine(Path.GetTempPath(), $"chimera-cache-survey-{System.Diagnostics.Process.GetCurrentProcess().Id}");
			Directory.CreateDirectory(_dir);
			_dataHomeWas = Environment.GetEnvironmentVariable("CHIMERA_DATA_HOME") ?? "";
			Environment.SetEnvironmentVariable("CHIMERA_DATA_HOME", Path.Combine(_dir, "data-home"));
		}

		/// <summary>See TasMovieProjectFormatTests: ClassCleanup runs at the end
		/// of the assembly by default, so this is re-established per test.</summary>
		[TestInitialize]
		public void UseThePlaygroundDataHome()
			=> Environment.SetEnvironmentVariable("CHIMERA_DATA_HOME", Path.Combine(_dir, "data-home"));

		[ClassCleanup]
		public static void RemovePlayground()
		{
			Environment.SetEnvironmentVariable("CHIMERA_DATA_HOME", _dataHomeWas.Length is 0 ? null : _dataHomeWas);
			Directory.Delete(_dir, recursive: true);
		}

		private static void Fill(string dir, string name, int bytes)
		{
			Directory.CreateDirectory(dir);
			File.WriteAllBytes(Path.Combine(dir, name), new byte[bytes]);
		}

		[TestMethod]
		public void EachKindOfCacheIsFound()
		{
			ProjectCache.Ensure("00000000000000aa");
			ProjectCache.Remember("00000000000000aa", new ProjectCache.ProjectFacts
			{
				Title = "Prince of Persia",
				ProjectPath = Path.Combine(_dir, "pop.chimeraProject"),
				Core = "xemu",
				System = "XBOX",
				Games = new[] { "Prince of Persia The Sands of Time.iso" },
			});
			Fill(ProjectCache.DirectoryFor("00000000000000aa"), "history.bin", 4096);

			var packages = Path.Combine(_dir, "CoreCache-packages");
			Fill(Path.Combine(packages, "xemu-23df374e61f4b5f212a436b86625546ce1e61324"), "core.wbx", 2048);

			var compiled = Path.Combine(_dir, "CoreCache-compiled");
			Fill(Path.Combine(compiled, "rpcs3", "e6bdd2b2ef65"), "obj.bin", 8192);

			var items = CacheSurvey.Take(packages, compiled);

			var project = items.Single(i => i.Detail is "00000000000000aa");
			Assert.AreEqual("Prince of Persia", project.Label, "a project is named, not shown as a hex id");
			Assert.AreEqual("00000000000000aa", project.Detail);
			Assert.AreEqual("XBOX", project.System, "and says which machine, so a PS3 disc is not mistaken for a cartridge");
			Assert.AreEqual("xemu", project.Core);
			Assert.AreEqual("Prince of Persia The Sands of Time.iso", project.Game, "and which game");

			var package = items.Single(i => i.Kind is CacheKind.CorePackage && i.Label is "xemu");
			Assert.AreEqual("xemu", package.Label);
			Assert.AreEqual("23df374e", package.Detail, "the hash is shown at the length a person reads");

			var code = items.Single(i => i.Kind is CacheKind.CompiledCode && i.Label is "rpcs3");
			Assert.AreEqual("rpcs3", code.Label);
			Assert.AreEqual("e6bdd2b2", code.Detail, "compiled code belongs to one build of one core");

			foreach (var item in items)
			{
				Assert.AreNotEqual("", item.Cost, $"{item.Kind} must say what losing it costs");
			}
		}

		[TestMethod]
		public void WhatIsOpenIsNotOffered()
		{
			ProjectCache.Ensure("00000000000000bb");
			Fill(ProjectCache.DirectoryFor("00000000000000bb"), "history.bin", 1024);

			var open = CacheSurvey.Take(null, null, openProjectId: "00000000000000bb")
				.Single(i => i.Detail is "00000000000000bb");
			Assert.IsTrue(open.InUse, "the project standing open cannot be pulled out from under itself");
			StringAssert.Contains(CacheSurvey.Remove(open) ?? "", "in use", "and removing it is refused, not attempted");
			Assert.IsTrue(Directory.Exists(open.Path), "so it is still there");

			var closed = CacheSurvey.Take(null, null).Single(i => i.Detail is "00000000000000bb");
			Assert.IsFalse(closed.InUse, "with nothing open it is an ordinary row");
			Assert.IsNull(CacheSurvey.Remove(closed));
			Assert.IsFalse(Directory.Exists(closed.Path), "and it goes");
		}

		[TestMethod]
		public void ALoadedPackageIsInUse()
		{
			var packages = Path.Combine(_dir, "CoreCache-loaded");
			var sha1 = "4B1E5D554FB41E29B49F1883269A29A85674DBF8";
			Fill(Path.Combine(packages, $"snes9x-{sha1}"), "core.wbx", 512);

			var idle = CacheSurvey.Take(packages, null).Single(i => i.Label is "snes9x");
			Assert.IsFalse(idle.InUse);

			// the registry reports the hash in its own case; the match must not care
			var busy = CacheSurvey.Take(packages, null, null, new[] { sha1.ToLowerInvariant() })
				.Single(i => i.Label is "snes9x");
			Assert.IsTrue(busy.InUse, "a package a loaded core is running out of is in use");
		}

		/// <summary>
		/// The rows worth reclaiming are the ones nothing points at any more. A
		/// cache whose project has been deleted, or moved and not opened since, is
		/// said so - it is still perfectly good, it is simply the one nobody is
		/// asking for.
		/// </summary>
		[TestMethod]
		public void ACacheWhoseProjectHasGoneSaysSo()
		{
			var living = Path.Combine(_dir, "still-here.chimeraProject");
			File.WriteAllText(living, "{}");
			ProjectCache.Ensure("00000000000000cc");
			ProjectCache.Remember("00000000000000cc", new ProjectCache.ProjectFacts { Title = "still here", ProjectPath = living });
			ProjectCache.Ensure("00000000000000dd");
			ProjectCache.Remember("00000000000000dd", new ProjectCache.ProjectFacts { Title = "long gone", ProjectPath = Path.Combine(_dir, "deleted.chimeraProject") });

			var items = CacheSurvey.Take(null, null);
			var here = items.Single(i => i.Detail is "00000000000000cc");
			var gone = items.Single(i => i.Detail is "00000000000000dd");

			Assert.IsFalse(here.Orphaned, "its project is where it was left");
			Assert.AreEqual("", here.Note);
			Assert.IsTrue(gone.Orphaned, "its project is not");
			StringAssert.Contains(gone.Note, "not where it was last seen");
			Assert.AreEqual(Path.Combine(_dir, "deleted.chimeraProject"), gone.ProjectPath,
				"and the window can still say where it looked");
		}

		/// <summary>
		/// A project that is OPEN is not missing, whatever the note says - it was
		/// opened from somewhere, and the note may simply be out of date.
		/// </summary>
		[TestMethod]
		public void TheOpenProjectIsNeverCalledAnOrphan()
		{
			ProjectCache.Ensure("00000000000000ee");
			ProjectCache.Remember("00000000000000ee", new ProjectCache.ProjectFacts { Title = "moved but open", ProjectPath = Path.Combine(_dir, "somewhere-else.chimeraProject") });

			var item = CacheSurvey.Take(null, null, openProjectId: "00000000000000ee")
				.Single(i => i.Detail is "00000000000000ee");
			Assert.IsTrue(item.InUse);
			Assert.IsFalse(item.Orphaned);
		}

		/// <summary>
		/// A disc and its tracks are one game, not four. The cue's bins arrive in
		/// the reserved "support" slot and naming them would bury the disc under
		/// its own contents.
		/// </summary>
		[TestMethod]
		public void TheGameIsTheMediaAndNotItsTracks()
		{
			ProjectCache.Ensure("00000000000000ff");
			ProjectCache.Remember("00000000000000ff", new ProjectCache.ProjectFacts
			{
				Title = "a two disc game",
				Games = new[] { "disc1.cue", "disc2.cue" },
			});

			var item = CacheSurvey.Take(null, null).Single(i => i.Detail is "00000000000000ff");
			Assert.AreEqual(2, item.Games.Count);
			StringAssert.Contains(item.Game, "disc1.cue", "the first is named");
			StringAssert.Contains(item.Game, "+1 more", "and the rest are counted rather than run off the column");
		}

		[TestMethod]
		public void AnAbsentRootIsNoRowsRatherThanAnError()
		{
			var items = CacheSurvey.Take(Path.Combine(_dir, "never-existed"), Path.Combine(_dir, "nor-this"));
			Assert.IsFalse(items.Any(static i => i.Kind is CacheKind.CorePackage or CacheKind.CompiledCode));
		}

		/// <summary>
		/// The defaults follow which way round the mistake would matter: a
		/// greenzone is the room a limit exists to bound, and everything else is
		/// small enough that evicting it frees nothing worth having.
		/// </summary>
		[TestMethod]
		public void GreenzonesStartUnlockedAndTheRestStartsLocked()
		{
			ProjectCache.Ensure("0000000000000101");
			Fill(ProjectCache.DirectoryFor("0000000000000101"), "history.bin", 4096);
			var packages = Path.Combine(_dir, "CoreCache-defaults");
			Fill(Path.Combine(packages, "gpgx-4ed3532117ad"), "core.wbx", 2048);

			var items = CacheSurvey.Take(packages, null);
			Assert.IsFalse(items.Single(i => i.Detail is "0000000000000101").Locked,
				"a greenzone is what the limit is for, so it is the thing the limit can reach");
			Assert.IsTrue(items.Single(i => i.Kind is CacheKind.CorePackage && i.Label is "gpgx").Locked,
				"an unpacked core is furniture: taking it frees nothing and stalls the next boot");
		}

		/// <summary>
		/// A lock is about the AUTO-clean and nothing else. Somebody who ticks a
		/// row and presses Remove has already decided, and a padlock that argued
		/// with them would be a lock on the wrong thing.
		/// </summary>
		/// <remarks>
		/// Serialised: this assembly runs its tests in parallel, and the lock book
		/// is ONE file that every test in the process shares. Two of them
		/// read-modify-writing it at once is a race in the test, not in the
		/// product, where the book has one reader and one writer.
		/// </remarks>
		[TestMethod]
		[DoNotParallelize]
		public void ALockIsRememberedAndOnlyBindsTheAutoClean()
		{
			ProjectCache.Ensure("0000000000000102");
			Fill(ProjectCache.DirectoryFor("0000000000000102"), "history.bin", 8192);

			var before = CacheSurvey.Take(null, null).Single(i => i.Detail is "0000000000000102");
			Assert.IsFalse(before.Locked);
			CacheLocks.Set(new[] { before }, locked: true);

			var locked = CacheSurvey.Take(null, null).Single(i => i.Detail is "0000000000000102");
			Assert.IsTrue(locked.Locked, "and it survives the survey being taken again");
			Assert.AreEqual(0, CacheSurvey.WhatWouldGo(new[] { locked }, limitBytes: 0).Count,
				"nothing the auto-clean does can reach it");

			Assert.IsNull(CacheSurvey.Remove(locked), "but Remove still takes it");
			Assert.IsFalse(Directory.Exists(locked.Path));
			Assert.IsFalse(CacheLocks.Read().ContainsKey(locked.Path),
				"and the lock goes with the thing it was put on");
		}

		/// <summary>
		/// Only deliberate exceptions are written down, so that what a kind
		/// defaults to stays the answer for everything nobody has overruled.
		/// </summary>
		/// <remarks>Serialised for the reason above: one file, many tests.</remarks>
		[TestMethod]
		[DoNotParallelize]
		public void OnlyWhatDiffersFromTheDefaultIsWrittenDown()
		{
			ProjectCache.Ensure("0000000000000103");
			Fill(ProjectCache.DirectoryFor("0000000000000103"), "history.bin", 512);
			var item = CacheSurvey.Take(null, null).Single(i => i.Detail is "0000000000000103");

			CacheLocks.Set(new[] { item }, locked: true);
			Assert.IsTrue(CacheLocks.Read().ContainsKey(item.Path));
			CacheLocks.Set(new[] { item }, locked: false);
			Assert.IsFalse(CacheLocks.Read().ContainsKey(item.Path), "back to its default is back to silence");
			Assert.IsFalse(CacheLocks.IsLocked(CacheKind.Project, item.Path));
		}

		private static CacheItem Aged(string path, long bytes, int daysAgo, bool locked = false, bool inUse = false)
			=> new()
			{
				Kind = CacheKind.Project,
				Label = path,
				Path = path,
				Bytes = bytes,
				LastUsed = new DateTime(2026, 9, 9).AddDays(-daysAgo),
				Locked = locked,
				InUse = inUse,
			};

		/// <summary>
		/// Oldest first, and only as many as it takes. "Least likely to be wanted
		/// next" is the one ordering worth having, and a lock is how the run where
		/// that is wrong says so.
		/// </summary>
		[TestMethod]
		public void TheAutoCleanTakesTheOldestUntilItIsUnderTheLimit()
		{
			CacheItem[] items =
			{
				Aged("/oldest", 30, daysAgo: 40),
				Aged("/middle", 30, daysAgo: 20),
				Aged("/newest", 30, daysAgo: 1),
			};

			CollectionAssert.AreEqual(
				new[] { "/oldest" },
				CacheSurvey.WhatWouldGo(items, limitBytes: 70).Select(static i => i.Path).ToArray(),
				"one is enough, so only one goes");
			CollectionAssert.AreEqual(
				new[] { "/oldest", "/middle" },
				CacheSurvey.WhatWouldGo(items, limitBytes: 40).Select(static i => i.Path).ToArray());
			Assert.AreEqual(0, CacheSurvey.WhatWouldGo(items, limitBytes: 90).Count,
				"a cache that is already under its limit is left entirely alone");
		}

		[TestMethod]
		public void TheAutoCleanTakesNeitherWhatIsLockedNorWhatIsOpen()
		{
			CacheItem[] items =
			{
				Aged("/locked", 50, daysAgo: 40, locked: true),
				Aged("/open", 50, daysAgo: 30, inUse: true),
				Aged("/free", 50, daysAgo: 10),
			};

			CollectionAssert.AreEqual(
				new[] { "/free" },
				CacheSurvey.WhatWouldGo(items, limitBytes: 10).Select(static i => i.Path).ToArray(),
				"the two oldest are both spoken for, so the newest is what is left to take");
		}

		/// <summary>
		/// Being unable to reach the limit is the lock doing its job, not a
		/// failure - but it is worth saying, or the cache quietly stays over.
		/// </summary>
		[TestMethod]
		public void ALimitThatCannotBeMetIsSaidRatherThanForced()
		{
			var result = CacheSurvey.AutoClean(
				new[] { Aged("/locked", 500, daysAgo: 90, locked: true) },
				new CacheCleanPolicy { LimitBytes = 100 });
			Assert.AreEqual(0, result.Removed.Count);
			Assert.IsTrue(result.StillOver);
			Assert.AreEqual(result.Before, result.After);
		}

		[TestMethod]
		public void SwitchingTheAutoCleanOffMeansNothingIsTaken()
		{
			var result = CacheSurvey.AutoClean(
				new[] { Aged("/free", 500, daysAgo: 90) },
				new CacheCleanPolicy { Enabled = false, LimitBytes = 1 });
			Assert.AreEqual(0, result.Removed.Count);
			Assert.IsFalse(result.StillOver, "nothing is being enforced, so nothing is over anything");
		}

		[TestMethod]
		public void SizesReadInTheUnitsRowsAreComparedIn()
		{
			Assert.AreEqual("", CacheSurvey.Size(0), "nothing is shown as nothing, not as 0 KB");
			Assert.AreEqual("512 KB", CacheSurvey.Size(512 * 1024));
			Assert.AreEqual("1.5 MB", CacheSurvey.Size((long)(1.5 * 1024 * 1024)));
			Assert.AreEqual("2.0 GB", CacheSurvey.Size(2L * 1024 * 1024 * 1024));
		}
	}
}
