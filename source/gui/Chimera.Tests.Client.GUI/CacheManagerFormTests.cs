using System;
using System.Collections.Generic;
using System.Linq;

using Chimera.Client.Common;
using Chimera.Client.GUI;

namespace Chimera.Tests.Client.GUI
{
	/// <summary>
	/// The cache manager's wiring: what it lists, what ticking does, and that a
	/// running session's cache cannot be ticked out from under it. What each row
	/// costs and which rows are orphans is the model's business, tested there.
	/// </summary>
	[TestClass]
	public class CacheManagerFormTests
	{
		private const string Idle = "/cache/idle";
		private const string Open = "/cache/open";
		private const string Gone = "/cache/gone";

		private static List<CacheItem> Three() => new()
		{
			new() { Kind = CacheKind.Project, Label = "a finished run", Detail = "aa", Path = Idle, Bytes = 1024 },
			new() { Kind = CacheKind.Project, Label = "the open one", Detail = "bb", Path = Open, Bytes = 2048, InUse = true },
			new() { Kind = CacheKind.Project, Label = "one whose project went", Detail = "cc", Path = Gone, Bytes = 4096, Orphaned = true },
		};

		[TestMethod]
		public void ItListsWhatTheSurveyFound()
		{
			using CacheManagerForm form = new(() => Three());
			form.Show();
			CollectionAssert.AreEquivalent(
				new[] { "a finished run", "the open one", "one whose project went" },
				form.Rows.ToArray());
		}

		/// <summary>
		/// Removing acts on what is TICKED, so with nothing ticked there is nothing
		/// for it to do and it says so by being unavailable rather than by
		/// complaining afterwards.
		/// </summary>
		[TestMethod]
		public void RemovingActsOnWhatIsTicked()
		{
			using CacheManagerForm form = new(() => Three());
			form.Show();
			Assert.IsFalse(form.RemoveEnabled, "nothing is ticked yet");

			Assert.IsTrue(form.SetChecked(Idle, true));
			CollectionAssert.AreEqual(new[] { Idle }, form.TickedPaths.ToArray());
			Assert.IsTrue(form.RemoveEnabled);

			Assert.IsFalse(form.SetChecked(Idle, false));
			Assert.IsFalse(form.RemoveEnabled, "and unticking takes it away again");
		}

		/// <summary>
		/// Highlighting a row is a different act from ticking it: one is for
		/// looking closely, the other for doing the same thing to several.
		/// </summary>
		[TestMethod]
		public void SelectingIsNotTicking()
		{
			using CacheManagerForm form = new(() => Three());
			form.Show();
			Assert.IsTrue(form.Select(Gone));
			Assert.AreEqual(0, form.TickedPaths.Count, "selecting a row does not tick it");
			Assert.IsTrue(form.OpenFolderEnabled, "but it does give Open Folder something to open");
			Assert.IsFalse(form.RemoveEnabled, "and leaves Remove with nothing to remove");
		}

		[TestMethod]
		public void WhatIsInUseCannotBeTicked()
		{
			using CacheManagerForm form = new(() => Three());
			form.Show();
			Assert.IsFalse(form.SetChecked(Open, true), "the tick is refused outright");
			Assert.AreEqual(0, form.TickedPaths.Count);
			Assert.IsFalse(form.RemoveEnabled);
		}

		/// <summary>
		/// The one selection worth making for somebody: the caches whose projects
		/// are gone. It must take the orphan and leave the open one, even though
		/// leaving it means the button does not simply tick everything it can.
		/// </summary>
		[TestMethod]
		public void SelectingOrphansTicksExactlyThose()
		{
			using CacheManagerForm form = new(() => Three());
			form.Show();
			form.TickOrphans();
			CollectionAssert.AreEqual(new[] { Gone }, form.TickedPaths.ToArray());
			Assert.IsTrue(form.RemoveEnabled);
		}

		/// <summary>
		/// The padlock is a ticked-rows act, like Remove: "keep these four runs and
		/// let the rest go" is one press. One button rather than two, and the mixed
		/// case has an obvious right answer - somebody who ticks a locked row and
		/// an unlocked one meant to keep both.
		/// </summary>
		[TestMethod]
		public void LockingActsOnWhatIsTickedAndTurnsRoundWhenAllOfItIsLocked()
		{
			var items = Three();
			void SetLocked(IReadOnlyList<CacheItem> chosen, bool locked)
			{
				foreach (var one in chosen)
				{
					var at = items.FindIndex(i => i.Path == one.Path);
					items[at] = new()
					{
						Kind = one.Kind, Label = one.Label, Detail = one.Detail, Path = one.Path,
						Bytes = one.Bytes, InUse = one.InUse, Orphaned = one.Orphaned, Locked = locked,
					};
				}
			}

			using CacheManagerForm form = new(() => items, SetLocked);
			form.Show();
			Assert.IsFalse(form.LockEnabled, "with nothing ticked there is nothing to lock");
			Assert.AreEqual(0, form.LockedPaths.Count);

			Assert.IsTrue(form.SetChecked(Idle, true));
			Assert.IsTrue(form.LockEnabled);
			form.ToggleLock();
			CollectionAssert.AreEqual(new[] { Idle }, form.LockedPaths.ToArray());

			form.ToggleLock();
			Assert.AreEqual(0, form.LockedPaths.Count, "the same press on an all-locked selection unlocks it");
		}

		[TestMethod]
		public void ATickedMixtureIsLockedRatherThanHalfUnlocked()
		{
			var items = Three();
			items[2] = new() { Kind = CacheKind.Project, Label = "one whose project went", Detail = "cc", Path = Gone, Bytes = 4096, Orphaned = true, Locked = true };
			void SetLocked(IReadOnlyList<CacheItem> chosen, bool locked)
			{
				foreach (var one in chosen)
				{
					var at = items.FindIndex(i => i.Path == one.Path);
					items[at] = new()
					{
						Kind = one.Kind, Label = one.Label, Detail = one.Detail, Path = one.Path,
						Bytes = one.Bytes, InUse = one.InUse, Orphaned = one.Orphaned, Locked = locked,
					};
				}
			}

			using CacheManagerForm form = new(() => items, SetLocked);
			form.Show();
			_ = form.SetChecked(Idle, true);
			_ = form.SetChecked(Gone, true);
			form.ToggleLock();
			CollectionAssert.AreEquivalent(new[] { Idle, Gone }, form.LockedPaths.ToArray());
		}

		private const long Gb = 1024L * 1024 * 1024;

		/// <summary>
		/// Clean Now applies the limit by hand, so it has nothing to do until the
		/// cache is over one. The two runs here weigh five gigabytes between them.
		/// </summary>
		[TestMethod]
		public void CleanNowHasNothingToDoUnderTheLimit()
		{
			List<CacheItem> Five() => new()
			{
				new() { Kind = CacheKind.Project, Label = "a long run", Path = "/cache/long", Bytes = 3 * Gb },
				new() { Kind = CacheKind.Project, Label = "a short one", Path = "/cache/short", Bytes = 2 * Gb },
			};

			using CacheManagerForm roomy = new(() => Five(), policy: new CacheCleanPolicy { LimitBytes = 10 * Gb });
			roomy.Show();
			Assert.IsFalse(roomy.CleanNowEnabled);

			using CacheManagerForm cramped = new(() => Five(), policy: new CacheCleanPolicy { LimitBytes = 4 * Gb });
			cramped.Show();
			Assert.IsTrue(cramped.CleanNowEnabled);
		}

		/// <summary>
		/// The limit is a setting, and a setting somebody has to press Close to keep
		/// is one they will lose - so it is saved as it is changed. Nothing is
		/// removed by changing it: a number being typed passes through 1 on its way
		/// to 100, and a window that emptied the cache mid-keystroke is one nobody
		/// would dare open.
		/// </summary>
		[TestMethod]
		public void ChangingTheLimitSavesItAndRemovesNothing()
		{
			var items = Three();
			CacheCleanPolicy? saved = null;
			using CacheManagerForm form = new(
				() => items,
				policy: new CacheCleanPolicy { LimitBytes = 50 * Gb },
				savePolicy: p => saved = new CacheCleanPolicy { Enabled = p.Enabled, LimitBytes = p.LimitBytes });
			form.Show();
			Assert.AreEqual("50", form.LimitText, "the box says the limit it was opened with");

			form.LimitGb = 2m;
			Assert.IsNotNull(saved);
			Assert.AreEqual(2 * Gb, saved!.LimitBytes);
			Assert.IsTrue(saved.Enabled);
			Assert.AreEqual(3, form.Rows.Count, "and the rows are all still there");

			form.AutoCleanTicked = false;
			Assert.IsFalse(saved!.Enabled, "switching it off is saved the same way");
			Assert.AreEqual(2 * Gb, saved.LimitBytes, "and leaves the number it was told");
		}

		[TestMethod]
		public void AnEmptyCacheIsNotAnEmptyWindow()
		{
			using CacheManagerForm form = new(static () => Array.Empty<CacheItem>());
			form.Show();
			Assert.AreEqual(0, form.Rows.Count);
			Assert.IsFalse(form.RemoveEnabled, "with nothing listed there is nothing to press");
			Assert.IsFalse(form.OpenFolderEnabled);
		}
	}
}
