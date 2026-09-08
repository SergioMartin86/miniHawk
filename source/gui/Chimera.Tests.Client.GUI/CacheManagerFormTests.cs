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
