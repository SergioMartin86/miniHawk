using System.Linq;
using System.Windows.Forms;

using Chimera.Client.Common;
using Chimera.Client.GUI;

namespace Chimera.Tests.Client.GUI
{
	/// <summary>
	/// The Reproducible Media Maker window. It decides nothing about the bytes -
	/// the engine packs - so what is worth pinning here is that it opens, that it
	/// offers the three shapes, that it will not start without somewhere to write,
	/// and that nothing sits on top of anything else.
	/// </summary>
	[TestClass]
	public class MediaMakerFormTests
	{
		[TestMethod]
		public void ItOpens()
		{
			using MediaMakerForm form = new();
			form.Show();
			Assert.AreEqual("Reproducible Media Maker", form.Text);
		}

		/// <summary>
		/// Somebody recording with static titles on does not want the window bar
		/// naming their work; this window has nothing project-specific to say, so
		/// its title is the same either way.
		/// </summary>
		[TestMethod]
		public void StaticTitlesChangeNothing()
		{
			using MediaMakerForm form = new();
			form.Config = new Config { UseStaticWindowTitles = true };
			form.Show();
			Assert.AreEqual("Reproducible Media Maker", form.Text);
		}

		/// <summary>Zip, ISO and floppy, and the box opens on one of them.</summary>
		[TestMethod]
		public void ItOffersTheThreeShapes()
		{
			using MediaMakerForm form = new();
			form.Show();
			var combo = form.Controls.OfType<ComboBox>().Single();
			Assert.AreEqual(3, combo.Items.Count);
			Assert.AreEqual(0, combo.SelectedIndex);
			CollectionAssert.AllItemsAreNotNull(combo.Items.Cast<object>().ToList());
		}

		/// <summary>
		/// Nothing is chosen yet, so there is nothing to make: the button is off
		/// rather than offering to pack a folder that was never named.
		/// </summary>
		[TestMethod]
		public void MakeIsOffUntilThereIsSomethingToMake()
		{
			using MediaMakerForm form = new();
			form.Show();
			var make = form.Controls.OfType<Button>().Single(b => b.Text == "Make");
			Assert.IsFalse(make.Enabled);
		}

		/// <summary>
		/// A label that runs on over the field beside it hides what somebody
		/// typed. That happened in the greenzone budgets dialog and was invisible
		/// to every test of behaviour, so this window gets the geometric check
		/// from the start: nothing overlaps a text box or the progress bar.
		/// </summary>
		[TestMethod]
		public void NothingSitsOnTopOfAField()
		{
			using MediaMakerForm form = new();
			form.Show();
			var fields = form.Controls.OfType<Control>()
				.Where(static c => c is TextBox or ComboBox or ProgressBar)
				.ToList();
			Assert.IsTrue(fields.Count >= 4);
			foreach (var field in fields)
			{
				foreach (var label in form.Controls.OfType<Label>())
				{
					Assert.IsFalse(
						label.Bounds.IntersectsWith(field.Bounds),
						$"the label \"{label.Text}\" at {label.Bounds} covers {field.GetType().Name} at {field.Bounds}");
				}
			}
		}
	}
}
