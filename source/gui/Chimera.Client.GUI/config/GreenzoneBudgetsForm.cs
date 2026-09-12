#nullable enable

using System;
using System.Drawing;
using System.Windows.Forms;

using Chimera.Client.Common;

namespace Chimera.Client.GUI
{
	/// <summary>
	/// What a greenzone may weigh: the default for every project, and what one
	/// project asks for instead.
	///
	/// TWO NUMBERS, because a history costs two things and bounding one bounds
	/// nothing. The memory budget decides how much stays in RAM; past it the
	/// oldest stretches go to the project's cache directory, which is how the
	/// memory number is met - by MOVING the bytes, not by giving them up. The
	/// disk number is what that file may then weigh. Without it a run left going
	/// all afternoon fills the disk and no rule anywhere says otherwise
	/// (docs/state-manager.md).
	///
	/// Reached from the Cache Manager because that is the window somebody is
	/// already in when they are looking at what a greenzone weighs, and because
	/// the cache limit next door is the same kind of promise about the same disk.
	///
	/// PER PROJECT, and kept beside the greenzone rather than in the
	/// .chimeraProject: a budget is a fact about the machine the work is being
	/// done on, and the project file is the one thing that gets handed to
	/// somebody else. A run edited on a workstation must not arrive at a laptop
	/// insisting on thirty-two gigabytes.
	/// </summary>
	public sealed class GreenzoneBudgetsForm : FormBase
	{
		private const string TITLE = "Greenzone budgets";

		private readonly string? _projectLabel;

		private readonly NumericUpDown _memory;
		private readonly NumericUpDown _disk;
		private readonly CheckBox? _override;
		private readonly NumericUpDown? _projectMemory;
		private readonly NumericUpDown? _projectDisk;

		/// <summary>The defaults as the window leaves them.</summary>
		public int DefaultMemoryMb => (int) _memory.Value;

		public int DefaultDiskMb => (int) _disk.Value;

		/// <summary>What the named project asks for, or nothing when it asks for the default.</summary>
		public ProjectCache.ProjectBudgets ProjectBudgets
			=> _override is { Checked: true }
				? new ProjectCache.ProjectBudgets
				{
					MemoryMb = (int) _projectMemory!.Value,
					DiskMb = (int) _projectDisk!.Value,
				}
				: new ProjectCache.ProjectBudgets();

		/// <summary>
		/// Names the project when there is one, because this window is reached from
		/// a row in the cache manager and the answer to "whose budgets am I
		/// looking at" should not depend on having noticed the checkbox further
		/// down.
		/// </summary>
		protected override string WindowTitle
			=> _projectLabel is { Length: > 0 } label ? $"{TITLE}: {Shorten(label)}" : TITLE;

		/// <remarks>
		/// A project's name is exactly the sort of thing somebody turning on static
		/// titles does not want read off their window bar, so the static form drops
		/// it rather than shortening it.
		/// </remarks>
		protected override string WindowTitleStatic => TITLE;

		/// <param name="projectLabel">
		/// What to call the project these may be set for, or null for the defaults alone -
		/// which is what the window shows when no project row is selected.
		/// </param>
		public GreenzoneBudgetsForm(
			int defaultMemoryMb,
			int defaultDiskMb,
			string? projectLabel = null,
			ProjectCache.ProjectBudgets? projectBudgets = null)
		{
			// before anything else, and before any chance of the base class asking
			// for a title: WindowTitle reads this field, and a field read too early
			// gives a wrong title silently instead of failing the way a direct
			// assignment to Text does.
			_projectLabel = projectLabel;

			SuspendLayout();
			FormBorderStyle = FormBorderStyle.FixedDialog;
			MaximizeBox = false;
			MinimizeBox = false;
			ShowIcon = false;
			StartPosition = FormStartPosition.CenterParent;

			var margin = UIHelper.ScaleX(12);
			var row = UIHelper.ScaleY(26);
			var labelWidth = UIHelper.ScaleX(230);
			var boxWidth = UIHelper.ScaleX(90);
			var unitWidth = UIHelper.ScaleX(34);
			var width = margin + labelWidth + boxWidth + unitWidth + margin;

			var y = UIHelper.ScaleY(12);
			Controls.Add(Note(
				"A greenzone costs memory and disk, and holding one without the other holds neither.",
				margin, y, width - (2 * margin), UIHelper.ScaleY(32)));
			y += UIHelper.ScaleY(36);

			Controls.Add(Caption("Every project, unless it says otherwise", margin, y, width));
			y += row;

			_memory = Spin(MovieConfig.MinimumBudgetMb, 1024 * 1024, defaultMemoryMb, 256);
			AddRow("Keep in memory", _memory, "MB", margin, y, labelWidth, boxWidth, unitWidth, width);
			y += row;

			// 0 is a real answer here and not a mistake: somebody with room to
			// spare may want the history never thrown away, which is what it did
			// before there was a limit at all.
			_disk = Spin(0, 4 * 1024 * 1024, defaultDiskMb, 1024);
			AddRow("Keep on disk (0: no limit)", _disk, "MB", margin, y, labelWidth, boxWidth, unitWidth, width);
			y += row + UIHelper.ScaleY(10);
			Pair(_memory, _disk);

			if (projectLabel is { Length: > 0 })
			{
				var known = projectBudgets ?? new ProjectCache.ProjectBudgets();
				Controls.Add(Caption("This project", margin, y, width));
				y += row;

				_override = new CheckBox
				{
					AutoSize = false,
					Checked = known.Any,
					Location = new(margin, y),
					Size = new(width - (2 * margin), UIHelper.ScaleY(20)),
					Text = $"Different budgets for {Shorten(projectLabel)}",
				};
				Controls.Add(_override);
				y += row;

				_projectMemory = Spin(MovieConfig.MinimumBudgetMb, 1024 * 1024,
					known.MemoryMb ?? defaultMemoryMb, 256);
				AddRow("Keep in memory", _projectMemory, "MB", margin, y, labelWidth, boxWidth, unitWidth, width);
				y += row;

				_projectDisk = Spin(0, 4 * 1024 * 1024, known.DiskMb ?? defaultDiskMb, 1024);
				AddRow("Keep on disk (0: no limit)", _projectDisk, "MB", margin, y, labelWidth, boxWidth, unitWidth, width);
				y += row;

				Pair(_projectMemory, _projectDisk);
				_override.CheckedChanged += (_, _) => SyncOverride();
				SyncOverride();

				Controls.Add(Note(
					"Takes effect when the project is next opened: a history already holding "
					+ "states is not thrown away to obey a number that has just changed.",
					margin, y, width - (2 * margin), UIHelper.ScaleY(32)));
				y += UIHelper.ScaleY(36);
			}

			y += UIHelper.ScaleY(4);
			Button ok = new()
			{
				DialogResult = DialogResult.OK,
				Location = new(width - margin - (2 * UIHelper.ScaleX(90)) - UIHelper.ScaleX(8), y),
				Size = new(UIHelper.ScaleX(90), UIHelper.ScaleY(26)),
				Text = "OK",
			};
			Button cancel = new()
			{
				DialogResult = DialogResult.Cancel,
				Location = new(width - margin - UIHelper.ScaleX(90), y),
				Size = new(UIHelper.ScaleX(90), UIHelper.ScaleY(26)),
				Text = "Cancel",
			};
			Controls.Add(ok);
			Controls.Add(cancel);
			AcceptButton = ok;
			CancelButton = cancel;

			ClientSize = new(width, y + UIHelper.ScaleY(38));
			ResumeLayout();
		}

		/// <summary>
		/// Keeps a pair in step: the disk number is never below the memory one.
		///
		/// Memory fills first and its overflow is what goes to disk, so a disk
		/// budget under the memory budget describes a disk that is full the moment
		/// memory is - every stretch dropped as it arrives, paid for in writes and
		/// worth nothing. Raising memory raises disk with it rather than refusing
		/// the number somebody just typed; zero is left alone, being "no limit".
		/// </summary>
		private static void Pair(NumericUpDown memory, NumericUpDown disk)
		{
			memory.ValueChanged += (_, _) =>
			{
				if (disk.Value is not 0 && disk.Value < memory.Value) disk.Value = memory.Value;
			};
			disk.ValueChanged += (_, _) =>
			{
				if (disk.Value is not 0 && disk.Value < memory.Value) disk.Value = memory.Value;
			};
		}

		private void SyncOverride()
		{
			var on = _override is { Checked: true };
			if (_projectMemory is not null) _projectMemory.Enabled = on;
			if (_projectDisk is not null) _projectDisk.Enabled = on;
		}

		private void AddRow(string text, NumericUpDown box, string unit, int margin, int y,
			int labelWidth, int boxWidth, int unitWidth, int width)
		{
			// The row is indented under its caption, so the label starts `indent`
			// past the margin - and it has to LOSE that much width too, or it runs
			// on into where the box begins. A label is not transparent and is added
			// ahead of the box, so the overrun painted the form's own grey over the
			// left edge of the number and hid the first digit of it.
			var indent = UIHelper.ScaleX(12);
			Controls.Add(new Label
			{
				AutoSize = false,
				Location = new(margin + indent, y + UIHelper.ScaleY(3)),
				Size = new(labelWidth - indent, UIHelper.ScaleY(20)),
				Text = text,
			});
			box.Location = new(margin + labelWidth, y);
			box.Width = boxWidth;
			Controls.Add(box);
			Controls.Add(new Label
			{
				AutoSize = false,
				Location = new(margin + labelWidth + boxWidth + UIHelper.ScaleX(4), y + UIHelper.ScaleY(3)),
				Size = new(unitWidth, UIHelper.ScaleY(20)),
				Text = unit,
			});
		}

		private static NumericUpDown Spin(int min, int max, int value, int increment) => new()
		{
			Increment = increment,
			Maximum = max,
			Minimum = min,
			Value = Math.Min(Math.Max(value, min), max),
		};

		private static Label Caption(string text, int x, int y, int width) => new()
		{
			AutoSize = false,
			Font = new Font(DefaultFont, FontStyle.Bold),
			Location = new(x, y + UIHelper.ScaleY(3)),
			Size = new(width - (2 * x), UIHelper.ScaleY(20)),
			Text = text,
		};

		private static Label Note(string text, int x, int y, int width, int height) => new()
		{
			AutoSize = false,
			Location = new(x, y),
			Size = new(width, height),
			Text = text,
		};

		private static string Shorten(string label)
			=> label.Length <= 40 ? label : label.Substring(0, 37) + "...";
	}
}
