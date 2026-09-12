using System;
using System.Linq;
using System.Windows.Forms;

using Chimera.Client.Common;
using Chimera.Client.GUI;

namespace Chimera.Tests.Client.GUI
{
	/// <summary>
	/// The greenzone budgets dialog, whose title is the part that used to throw:
	/// FormBase refuses a direct assignment to Text so that every window's title
	/// goes through WindowTitle, which is what the "static window titles" setting
	/// reaches in to override. A form that ignores that rule does not fail at
	/// build time, only when somebody opens it.
	/// </summary>
	[TestClass]
	public class GreenzoneBudgetsFormTests
	{
		private const string LONG_LABEL = "a run whose name somebody typed at great length and never shortened";

		/// <summary>
		/// Opening it at all: before the fix the constructor threw
		/// InvalidOperationException from the Text setter, so this never reached
		/// the assert.
		/// </summary>
		[TestMethod]
		public void ItOpensWithoutAProject()
		{
			using GreenzoneBudgetsForm form = new(defaultMemoryMb: 1024, defaultDiskMb: 4096);
			form.Show();
			Assert.AreEqual("Greenzone budgets", form.Text);
			Assert.AreEqual(1024, form.DefaultMemoryMb);
			Assert.AreEqual(4096, form.DefaultDiskMb);
		}

		/// <summary>
		/// With a project the title names it, which is the whole reason the title
		/// is a WindowTitle override and not a constant: the window is reached from
		/// one row of the cache manager and should say which row.
		/// </summary>
		[TestMethod]
		public void ItNamesTheProjectItWasOpenedFor()
		{
			using GreenzoneBudgetsForm form = new(
				defaultMemoryMb: 1024,
				defaultDiskMb: 4096,
				projectLabel: "a finished run");
			form.Show();
			Assert.AreEqual("Greenzone budgets: a finished run", form.Text);
		}

		/// <summary>A title bar is not the place to find out how long a label can get.</summary>
		[TestMethod]
		public void ALongProjectNameIsShortened()
		{
			using GreenzoneBudgetsForm form = new(
				defaultMemoryMb: 1024,
				defaultDiskMb: 4096,
				projectLabel: LONG_LABEL);
			form.Show();
			Assert.IsTrue(
				form.Text.EndsWith("...", StringComparison.Ordinal),
				$"expected a shortened title, got \"{form.Text}\"");
			Assert.IsTrue(form.Text.Length < LONG_LABEL.Length, "and one shorter than the label itself");
		}

		/// <summary>
		/// Somebody who has asked for static titles is usually being recorded, and
		/// the name of the run they are working on is exactly what they asked not
		/// to have on screen.
		/// </summary>
		[TestMethod]
		public void StaticTitlesDropTheProjectName()
		{
			using GreenzoneBudgetsForm form = new(
				defaultMemoryMb: 1024,
				defaultDiskMb: 4096,
				projectLabel: "a finished run");
			form.Config = new Config { UseStaticWindowTitles = true };
			form.Show();
			Assert.AreEqual("Greenzone budgets", form.Text);
		}

		/// <summary>
		/// A row's label has to stop where its box starts. Reported from use: the
		/// grey ran on over the number and hid the first digit of it. The rows are
		/// indented under their caption, so the label began 12 past the margin and
		/// kept its full width anyway, which put its right edge 12 into the box -
		/// and a label is not transparent, so it painted the form's own grey there.
		///
		/// Every assert about this window until now was about behaviour, and a
		/// control covering another one is invisible to all of them. This one is
		/// about geometry, which is the only thing that could have caught it.
		///
		/// Every control here is a direct child of the form, so the bounds are all
		/// in the one coordinate space and can be compared as they stand.
		/// </summary>
		[TestMethod]
		public void ARowLabelDoesNotOverlapItsBox()
		{
			// with a project, so the four rows exist: two shared, two for it
			using GreenzoneBudgetsForm form = new(
				defaultMemoryMb: 1024,
				defaultDiskMb: 4096,
				projectLabel: "a finished run");
			form.Show();

			var boxes = form.Controls.OfType<NumericUpDown>().ToList();
			Assert.AreEqual(4, boxes.Count, "two budgets for every project, two for this one");

			foreach (var box in boxes)
			{
				foreach (var label in form.Controls.OfType<Label>())
				{
					Assert.IsFalse(
						label.Bounds.IntersectsWith(box.Bounds),
						$"the label \"{label.Text}\" at {label.Bounds} covers the box at {box.Bounds}");
				}
			}
		}
	}
}
