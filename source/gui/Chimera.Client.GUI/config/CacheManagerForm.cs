#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

using Chimera.Client.Common;

namespace Chimera.Client.GUI
{
	/// <summary>
	/// Tools &gt; Cache Manager: everything Chimera keeps on disk that it could work
	/// out again, and how much room it is taking.
	///
	/// One rule makes this window safe, and it is worth stating where somebody can
	/// read it: losing a cache costs RECOMPUTATION, never work. Every row here can
	/// be deleted and the worst that happens is waiting - a run replays instead of
	/// resuming, a package is unzipped again, a game's code is translated again.
	/// Nothing that cannot be rebuilt appears: installed cores belong to the Core
	/// Manager, because a movie needs the exact build that recorded it, and
	/// projects, roms and firmware are not caches at all.
	///
	/// Thin over <see cref="CacheSurvey"/>, like the firmware windows are over
	/// their surveys: what is listed, what it costs and what may not be deleted
	/// are the model's, which is tested without a UI, and this arranges it.
	/// </summary>
	public sealed class CacheManagerForm : FormBase
	{
		private readonly Func<IReadOnlyList<CacheItem>> _survey;
		private readonly ListView _list;
		private readonly Label _header;
		private readonly Label _detail;
		private readonly Label _status;
		private readonly Button _remove;

		/// <summary>the column the list is sorted by, and whether it is reversed</summary>
		private int _sortColumn = SizeColumn;
		private bool _sortAscending;

		private const int SizeColumn = 8;
		private const int DateColumn = 9;

		private List<CacheItem> _items = new();

		protected override string WindowTitleStatic => "Cache Manager";

		/// <param name="survey">takes stock; called on open and after every removal</param>
		public CacheManagerForm(Func<IReadOnlyList<CacheItem>> survey)
		{
			_survey = survey;

			SuspendLayout();
			// seven columns of paths and sizes; the two location columns are the
			// wide ones and are the reason to be here at all
			ClientSize = new(UIHelper.ScaleX(1460), UIHelper.ScaleY(470));
			MinimumSize = new(UIHelper.ScaleX(900), UIHelper.ScaleY(360));
			StartPosition = FormStartPosition.CenterParent;
			ShowIcon = false;

			var margin = UIHelper.ScaleX(8);
			var footer = UIHelper.ScaleY(138);

			_header = new Label
			{
				Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
				AutoSize = false,
				Location = new(margin, UIHelper.ScaleY(9)),
				Size = new(ClientSize.Width - (2 * margin), UIHelper.ScaleY(32)),
			};

			_list = new ListView
			{
				Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
				FullRowSelect = true,
				HideSelection = false,
				Location = new(margin, UIHelper.ScaleY(46)),
				Size = new(ClientSize.Width - (2 * margin), ClientSize.Height - UIHelper.ScaleY(46) - footer),
				MultiSelect = false,
				View = View.Details,
			};
			_list.Columns.Add("What", UIHelper.ScaleX(100));
			_list.Columns.Add("Name", UIHelper.ScaleX(180));
			_list.Columns.Add("System", UIHelper.ScaleX(70));
			_list.Columns.Add("Core", UIHelper.ScaleX(90));
			_list.Columns.Add("Game", UIHelper.ScaleX(200));
			_list.Columns.Add("Project id", UIHelper.ScaleX(120));
			_list.Columns.Add("Project file", UIHelper.ScaleX(230));
			_list.Columns.Add("Cache location", UIHelper.ScaleX(230));
			_list.Columns.Add("Size", UIHelper.ScaleX(70), HorizontalAlignment.Right);
			_list.Columns.Add("Last modified", UIHelper.ScaleX(110));
			_list.SelectedIndexChanged += (_, _) => ShowSelected();
			// The reason to open this window is almost always "what is taking the
			// room", and the answer is a sort away. Size and date sort largest and
			// newest first, because that is the question being asked; the text
			// columns sort the way text does.
			_list.ColumnClick += (_, e) => SortBy(e.Column);

			_detail = new Label
			{
				Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
				AutoSize = false,
				Location = new(margin, ClientSize.Height - footer + UIHelper.ScaleY(6)),
				Size = new(ClientSize.Width - (2 * margin), UIHelper.ScaleY(66)),
			};

			_status = new Label
			{
				Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
				AutoSize = false,
				Location = new(margin, ClientSize.Height - footer + UIHelper.ScaleY(74)),
				Size = new(ClientSize.Width - (2 * margin), UIHelper.ScaleY(18)),
			};

			var buttonRow = ClientSize.Height - UIHelper.ScaleY(32);
			var bw = UIHelper.ScaleX(150);
			_remove = new Button
			{
				Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
				Location = new(margin, buttonRow),
				Size = new(bw, UIHelper.ScaleY(26)),
				Text = "Remove Entry",
			};
			_remove.Click += (_, _) => RemoveSelected();

			Button close = new()
			{
				Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
				DialogResult = DialogResult.OK,
				Location = new(ClientSize.Width - margin - UIHelper.ScaleX(90), buttonRow),
				Size = new(UIHelper.ScaleX(90), UIHelper.ScaleY(26)),
				Text = "Close",
			};

			Controls.AddRange(new Control[] { _header, _list, _detail, _status, _remove, close });
			AcceptButton = close;
			ResumeLayout();

			Reload();
		}

		/// <summary>Takes stock again, keeping the selection where it still exists.</summary>
		private void Reload()
		{
			var wasSelected = Selected()?.Path;
			_items = Sorted(_survey()).ToList();

			_list.BeginUpdate();
			_list.Items.Clear();
			foreach (var item in _items)
			{
				ListViewItem row = new(CacheSurvey.Describe(item.Kind)) { Tag = item };
				row.SubItems.Add(item.Label);
				row.SubItems.Add(item.System);
				row.SubItems.Add(item.Core);
				row.SubItems.Add(item.Game);
				row.SubItems.Add(item.Kind is CacheKind.Project ? item.Detail : "");
				row.SubItems.Add(item.ProjectPath.Length is not 0
					? item.ProjectPath + (item.Orphaned ? "   (not found)" : "")
					: item.Kind is CacheKind.Project ? "(never recorded)" : item.Detail);
				row.SubItems.Add(item.Path);
				row.SubItems.Add(CacheSurvey.Size(item.Bytes));
				row.SubItems.Add(item.LastUsed == default ? "" : item.LastUsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
				if (item.InUse) row.ForeColor = SystemColors.GrayText;
				_list.Items.Add(row);
			}
			_list.EndUpdate();

			var total = _items.Sum(static i => i.Bytes);
			var orphaned = _items.Where(static i => i.Orphaned).ToList();
			_header.Text = _items.Count is 0
				? "Nothing is cached. Everything here is rebuilt as it is needed."
				: $"{_items.Count} cached item(s), {CacheSurvey.Size(total)} in all. Everything here can be"
					+ " removed: what it costs is time, never work."
					+ (orphaned.Count is 0
						? ""
						: $"{Environment.NewLine}{orphaned.Count} belong to projects that are no longer where they were"
							+ $" ({CacheSurvey.Size(orphaned.Sum(static i => i.Bytes))}).");

			if (wasSelected is not null)
			{
				foreach (ListViewItem row in _list.Items)
				{
					if (row.Tag is CacheItem item && item.Path == wasSelected) { row.Selected = true; break; }
				}
			}
			if (_list.SelectedItems.Count is 0 && _list.Items.Count > 0) _list.Items[0].Selected = true;
			ShowSelected();
		}

		/// <summary>
		/// Sorts by a column, reversing it when it is already the one sorted by.
		/// Size and date start largest and newest, since "what is taking the room"
		/// and "what have I not touched in months" are the two questions this
		/// window exists to answer.
		/// </summary>
		private void SortBy(int column)
		{
			if (column == _sortColumn) _sortAscending = !_sortAscending;
			else
			{
				_sortColumn = column;
				_sortAscending = column is not (SizeColumn or DateColumn);
			}
			Reload();
		}

		/// <summary>
		/// Always built ASCENDING and reversed when it should not be, so that
		/// "which way round is this" has one answer instead of one per column.
		/// </summary>
		private IEnumerable<CacheItem> Sorted(IEnumerable<CacheItem> items)
		{
			IEnumerable<CacheItem> ordered = _sortColumn switch
			{
				0 => items.OrderBy(static i => i.Kind).ThenBy(static i => i.Bytes),
				1 => items.OrderBy(static i => i.Label, StringComparer.CurrentCultureIgnoreCase),
				2 => items.OrderBy(static i => i.System, StringComparer.OrdinalIgnoreCase),
				3 => items.OrderBy(static i => i.Core, StringComparer.OrdinalIgnoreCase),
				4 => items.OrderBy(static i => i.Game, StringComparer.CurrentCultureIgnoreCase),
				5 => items.OrderBy(static i => i.Detail, StringComparer.OrdinalIgnoreCase),
				6 => items.OrderBy(static i => i.ProjectPath, StringComparer.CurrentCultureIgnoreCase),
				7 => items.OrderBy(static i => i.Path, StringComparer.CurrentCultureIgnoreCase),
				DateColumn => items.OrderBy(static i => i.LastUsed),
				_ => items.OrderBy(static i => i.Bytes),
			};
			return _sortAscending ? ordered : ordered.Reverse();
		}

		private CacheItem? Selected()
			=> _list.SelectedItems.Count is 0 ? null : _list.SelectedItems[0].Tag as CacheItem;

		private void ShowSelected()
		{
			var item = Selected();
			if (item is null)
			{
				_detail.Text = "";
			}
			else
			{
				// The columns clip a long path, and a path that cannot be read in
				// full is not much use for deciding whether to delete something.
				List<string> lines = new() { item.Cost };
				if (item.InUse) lines.Add("In use right now, so it cannot be removed while it is open.");
				else if (item.Note.Length is not 0) lines.Add(item.Note);
				if (item.Games.Count > 1) lines.Add($"Game files: {string.Join(", ", item.Games)}");
				if (item.ProjectPath.Length is not 0) lines.Add($"Project file: {item.ProjectPath}");
				lines.Add($"Cache: {item.Path}");
				_detail.Text = string.Join(Environment.NewLine, lines);
			}
			_remove.Enabled = item is { InUse: false };
		}

		private void RemoveSelected()
		{
			if (Selected() is not { } item) return;
			if (!Confirm(1, item.Bytes, item.Cost)) return;
			var refused = CacheSurvey.Remove(item);
			Reload();
			_status.Text = refused is null ? $"Removed {item.Label}." : $"{item.Label} was not removed: {refused}";
		}

		/// <summary>
		/// Says what is about to go and what it costs. A cache is safe to lose, so
		/// this is a confirmation and not a warning - but it is still somebody's
		/// afternoon of recomputation, so it says so in those terms.
		/// </summary>
		private bool Confirm(int count, long bytes, string cost)
			=> MessageBox.Show(
				this,
				$"Remove {count} cached item(s), freeing {CacheSurvey.Size(bytes)}?{Environment.NewLine}{Environment.NewLine}{cost}",
				"Remove cached data",
				MessageBoxButtons.OKCancel,
				MessageBoxIcon.Question) is DialogResult.OK;

		/// <summary>Selects a row by its path. For tests and screenshots.</summary>
		public bool Select(string path)
		{
			foreach (ListViewItem row in _list.Items)
			{
				if (row.Tag is CacheItem item && item.Path == path) { row.Selected = true; return true; }
			}
			return false;
		}

		/// <summary>What the window is showing, for tests.</summary>
		public IReadOnlyList<string> Rows
			=> _list.Items.Cast<ListViewItem>().Select(static r => r.SubItems[1].Text).ToList();

		/// <summary>Whether the selected row may be removed.</summary>
		public bool RemoveEnabled => _remove.Enabled;
	}
}
