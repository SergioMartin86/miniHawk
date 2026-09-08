using System;
using System.Collections.Generic;
using System.Linq;

using Chimera.Client.Common;
using Chimera.Client.GUI;

namespace Chimera.Tests.Client.GUI
{
	/// <summary>
	/// The cache manager's wiring: that it lists what the survey found, and that
	/// what a running session is standing on cannot be removed from under it.
	/// What each row costs is the model's business and tested there.
	/// </summary>
	[TestClass]
	public class CacheManagerFormTests
	{
		private static List<CacheItem> Two() => new()
		{
			new() { Kind = CacheKind.Project, Label = "a finished run", Detail = "aa", Path = "/cache/aa", Bytes = 1024 },
			new() { Kind = CacheKind.Project, Label = "the open one", Detail = "bb", Path = "/cache/bb", Bytes = 2048, InUse = true },
		};

		[TestMethod]
		public void ItListsWhatTheSurveyFound()
		{
			using CacheManagerForm form = new(() => Two());
			form.Show();
			CollectionAssert.AreEquivalent(
				new[] { "a finished run", "the open one" },
				form.Rows.ToArray());
		}

		[TestMethod]
		public void WhatIsInUseCannotBeRemoved()
		{
			using CacheManagerForm form = new(() => Two());
			form.Show();

			Assert.IsTrue(form.Select("/cache/aa"));
			Assert.IsTrue(form.RemoveEnabled, "an idle cache is the user's to throw away");

			Assert.IsTrue(form.Select("/cache/bb"));
			Assert.IsFalse(form.RemoveEnabled, "the one a session is standing on is not");
		}

		[TestMethod]
		public void AnEmptyCacheIsNotAnEmptyWindow()
		{
			using CacheManagerForm form = new(static () => Array.Empty<CacheItem>());
			form.Show();
			Assert.AreEqual(0, form.Rows.Count);
			Assert.IsFalse(form.RemoveEnabled, "with nothing listed there is nothing to press");
		}
	}
}
