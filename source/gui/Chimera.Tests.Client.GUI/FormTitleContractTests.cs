using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Chimera.Client.GUI;

namespace Chimera.Tests.Client.GUI
{
	/// <summary>
	/// The rule every window in the frontend lives under: a title is produced by
	/// overriding WindowTitle or WindowTitleStatic, never by assigning to Text.
	/// FormBase needs that because the "static window titles" setting has to be
	/// able to ask a window for its title again, and a string assigned once
	/// during construction cannot be asked for anything.
	///
	/// The setter throws rather than quietly doing nothing so the mistake is
	/// loud, but it is only loud when the window is opened, which is how a dialog
	/// reached from one button in the cache manager shipped with a title it could
	/// not set (GreenzoneBudgetsForm). This test makes the same mistake loud at
	/// test time instead.
	/// </summary>
	[TestClass]
	public class FormTitleContractTests
	{
		private const BindingFlags DECLARED = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.NonPublic;

		/// <summary>
		/// Every window somebody can actually open. Base classes that exist only
		/// to be derived from (ToolFormBase) are left out: they leave the title to
		/// the tool, and the tools are all in this list themselves.
		/// </summary>
		private static IReadOnlyList<Type> Windows()
		{
			var all = typeof(FormBase).Assembly.GetTypes()
				.Where(static t => typeof(FormBase).IsAssignableFrom(t) && t != typeof(FormBase))
				.ToList();
			var derivedFrom = new HashSet<Type>(all.Select(static t => t.BaseType));
			return all.Where(t => !t.IsAbstract && !derivedFrom.Contains(t)).ToList();
		}

		/// <summary>Walks up to FormBase looking for a title the window declared itself.</summary>
		private static bool DeclaresATitle(Type window)
		{
			for (var t = window; t is not null && t != typeof(FormBase); t = t.BaseType)
			{
				if (t.GetProperty("WindowTitle", DECLARED) is not null
					|| t.GetProperty("WindowTitleStatic", DECLARED) is not null)
				{
					return true;
				}
			}
			return false;
		}

		/// <summary>
		/// A window that declares neither has nowhere to have put its title except
		/// Text, which throws, so this is the same bug every time it appears:
		/// either the window opens with FormBase's NotImplementedException, or its
		/// constructor already threw from the Text setter.
		/// </summary>
		[TestMethod]
		public void EveryWindowGetsItsTitleFromWindowTitle()
		{
			var windows = Windows();
			Assert.IsTrue(windows.Count > 10, $"the sweep found only {windows.Count} windows, so it is not sweeping");
			var missing = windows.Where(static t => !DeclaresATitle(t)).Select(static t => t.Name).OrderBy(static n => n).ToList();
			Assert.AreEqual(
				0,
				missing.Count,
				"these windows override neither WindowTitle nor WindowTitleStatic, so they have no way to set a title "
					+ "that FormBase will accept: " + string.Join(", ", missing));
		}
	}
}
