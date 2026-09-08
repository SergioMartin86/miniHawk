#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

using Chimera.Client.Common;
using Chimera.Common;

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
	/// TICKING is for doing the same thing to several rows; SELECTING is for
	/// looking closely at one - the same division the Core Manager draws. So
	/// Remove acts on what is ticked, and Open Folder on what is highlighted.
	///
	/// Thin over <see cref="CacheSurvey"/>, like the firmware windows are over
	/// their surveys: what is listed, what it costs and what may not be deleted
	/// are the model's, which is tested without a UI, and this arranges it.
	/// </summary>
	public sealed class CacheManagerForm : FormBase
	{
		private readonly Func<IReadOnlyList<CacheItem>> _survey;
		private readonly ListView _list;
		private readonly CheckBox _selectAll;
		private readonly Label _header;
		private readonly Label _detail;
		private readonly Label _status;
		private readonly Button _remove;
		private readonly Button _selectOrphans;
		private readonly Button _openFolder;

		/// <summary>
		/// The rows whose box is ticked, by cache location - which is unique, and
		/// survives the list being rebuilt under a different sort.
		///
		/// Kept as a set rather than read back off the ListView for the reason the
		/// Core Manager learned: the event reporting a tick arrives as a posted
		/// Windows message, and by the time it is delivered the collection may be
		/// mid-rebuild, so walking it from the handler is what crashes the window.
		/// </summary>
		private readonly HashSet<string> _ticked = new(StringComparer.Ordinal);

		private bool _suppressCheckEvents;

		/// <summary>
		/// False until every control exists. A ListView raises ItemChecked while
		/// its handle is being created, which on .NET Framework happens inside the
		/// constructor - before the buttons the handler wants to enable are there.
		/// Mono does not, so a Linux test would never see it.
		/// </summary>
		private bool _ready;

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
			ClientSize = new(UIHelper.ScaleX(1460), UIHelper.ScaleY(500));
			MinimumSize = new(UIHelper.ScaleX(900), UIHelper.ScaleY(380));
			StartPosition = FormStartPosition.CenterParent;
			ShowIcon = false;

			var margin = UIHelper.ScaleX(8);
			var footer = UIHelper.ScaleY(138);
			var listTop = UIHelper.ScaleY(70);

			_header = new Label
			{
				Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
				AutoSize = false,
				Location = new(margin, UIHelper.ScaleY(9)),
				Size = new(ClientSize.Width - (2 * margin), UIHelper.ScaleY(32)),
			};

			// Above the list rather than in the header: a WinForms ListView header
			// is not a place a control can live, and here it also says how many are
			// ticked and what they weigh, which a header box could not.
			_selectAll = new CheckBox
			{
				Anchor = AnchorStyles.Top | AnchorStyles.Left,
				AutoSize = true,
				Location = new(margin + UIHelper.ScaleX(2), UIHelper.ScaleY(46)),
				Text = "Select all",
			};
			_selectAll.CheckedChanged += (_, _) => SelectAllChanged();

			_list = new ListView
			{
				Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
				CheckBoxes = true,
				FullRowSelect = true,
				HideSelection = false,
				Location = new(margin, listTop),
				Size = new(ClientSize.Width - (2 * margin), ClientSize.Height - listTop - footer),
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
			// newest first, because that is the question being asked.
			_list.ColumnClick += (_, e) => SortBy(e.Column);
			// What a session is standing on may not be ticked at all: refusing the
			// tick is plainer than letting it be ticked and then skipped.
			_list.ItemCheck += (_, e) =>
			{
				if (_suppressCheckEvents) return;
				if (e.Index >= 0 && e.Index < _list.Items.Count
					&& _list.Items[e.Index].Tag is CacheItem { InUse: true })
				{
					e.NewValue = CheckState.Unchecked;
				}
			};
			_list.ItemChecked += (_, e) =>
			{
				if (_suppressCheckEvents) return;
				// e.Item is the one this message is about; its collection is not
				// safe to walk from here
				if (e.Item?.Tag is CacheItem item)
				{
					if (e.Item.Checked) _ticked.Add(item.Path);
					else _ticked.Remove(item.Path);
				}
				UpdateButtons();
			};

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
			var bw = UIHelper.ScaleX(160);
			var gap = UIHelper.ScaleX(8);

			_remove = new Button
			{
				Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
				Location = new(margin, buttonRow),
				Size = new(bw, UIHelper.ScaleY(26)),
				Text = "Remove Ticked",
			};
			_remove.Click += (_, _) => RemoveTicked();

			_selectOrphans = new Button
			{
				Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
				Location = new(margin + bw + gap, buttonRow),
				Size = new(bw, UIHelper.ScaleY(26)),
				Text = "Select all orphans",
			};
			_selectOrphans.Click += (_, _) => SelectOrphans();

			_openFolder = new Button
			{
				Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
				Location = new(margin + (2 * (bw + gap)), buttonRow),
				Size = new(bw, UIHelper.ScaleY(26)),
				Text = "Open Folder",
			};
			_openFolder.Click += (_, _) => OpenSelectedFolder();

			Button close = new()
			{
				Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
				DialogResult = DialogResult.OK,
				Location = new(ClientSize.Width - margin - UIHelper.ScaleX(90), buttonRow),
				Size = new(UIHelper.ScaleX(90), UIHelper.ScaleY(26)),
				Text = "Close",
			};

			Controls.AddRange(new Control[]
			{
				_header, _selectAll, _list, _detail, _status, _remove, _selectOrphans, _openFolder, close,
			});
			AcceptButton = close;
			ResumeLayout();

			_ready = true;
			Reload();
		}

		/// <summary>Takes stock again, keeping the selection and the ticks that still have rows.</summary>
		private void Reload()
		{
			var wasSelected = Selected()?.Path;
			_items = Sorted(_survey()).ToList();
			// a tick whose row has gone leaves with it, or Remove would act on
			// something no longer listed
			_ticked.IntersectWith(_items.Select(static i => i.Path));

			_suppressCheckEvents = true;
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
				row.Checked = _ticked.Contains(item.Path);
				_list.Items.Add(row);
			}
			_list.EndUpdate();
			_suppressCheckEvents = false;

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
			UpdateButtons();
		}

		/// <summary>The rows whose box is ticked, in list order.</summary>
		private List<CacheItem> Ticked() => _items.FindAll(i => _ticked.Contains(i.Path));

		private void UpdateButtons()
		{
			if (!_ready) return;
			var ticked = Ticked();
			_remove.Enabled = ticked.Count is not 0;
			_selectOrphans.Enabled = _items.Any(static i => i.Orphaned && !i.InUse);
			_openFolder.Enabled = Selected() is not null;
			_selectAll.Text = ticked.Count is 0
				? "Select all"
				: $"Select all ({ticked.Count} ticked, {CacheSurvey.Size(ticked.Sum(static i => i.Bytes))})";
		}

		private void SelectAllChanged()
		{
			if (_suppressCheckEvents) return;
			SetTicks(_selectAll.Checked ? _items.Where(static i => !i.InUse) : Enumerable.Empty<CacheItem>());
		}

		/// <summary>
		/// Ticks exactly the caches whose project is no longer where it was. Those
		/// are the rows nothing is asking for any more, which makes this the one
		/// selection worth making on somebody's behalf.
		/// </summary>
		private void SelectOrphans()
		{
			SetTicks(_items.Where(static i => i.Orphaned && !i.InUse));
			var ticked = Ticked();
			_status.Text = ticked.Count is 0
				? "No cache belongs to a project that has gone."
				: $"Ticked {ticked.Count} orphaned item(s), {CacheSurvey.Size(ticked.Sum(static i => i.Bytes))}.";
		}

		/// <summary>Makes the ticks exactly these rows, without the list's events answering back.</summary>
		private void SetTicks(IEnumerable<CacheItem> wanted)
		{
			_ticked.Clear();
			foreach (var item in wanted) _ticked.Add(item.Path);
			_suppressCheckEvents = true;
			foreach (ListViewItem row in _list.Items)
			{
				row.Checked = row.Tag is CacheItem item && _ticked.Contains(item.Path);
			}
			_suppressCheckEvents = false;
			UpdateButtons();
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
			UpdateButtons();
		}

		private void RemoveTicked()
		{
			var wanted = Ticked().Where(static i => !i.InUse).ToList();
			if (wanted.Count is 0) return;
			if (!Confirm(wanted.Count, wanted.Sum(static i => i.Bytes), wanted)) return;

			var removed = 0;
			List<string> kept = new();
			foreach (var item in wanted)
			{
				if (CacheSurvey.Remove(item) is null) removed++;
				else kept.Add(item.Label);
			}
			_suppressCheckEvents = true;
			_selectAll.Checked = false;
			_suppressCheckEvents = false;
			Reload();
			_status.Text = kept.Count is 0
				? $"Removed {removed} item(s)."
				: $"Removed {removed}; left {string.Join(", ", kept)}.";
		}

		/// <summary>
		/// Says what is about to go and what it costs. A cache is safe to lose, so
		/// this is a confirmation and not a warning - but it is still somebody's
		/// afternoon of recomputation, so it says so in those terms, and names each
		/// distinct cost rather than only the one the first row happens to carry.
		/// </summary>
		private bool Confirm(int count, long bytes, IEnumerable<CacheItem> items)
		{
			var costs = items.Select(static i => i.Cost).Distinct().ToList();
			return MessageBox.Show(
				this,
				$"Remove {count} cached item(s), freeing {CacheSurvey.Size(bytes)}?{Environment.NewLine}{Environment.NewLine}"
					+ string.Join(Environment.NewLine, costs),
				"Remove cached data",
				MessageBoxButtons.OKCancel,
				MessageBoxIcon.Question) is DialogResult.OK;
		}

		/// <summary>
		/// Shows the highlighted row's directory in whatever the machine uses to
		/// look at directories. A machine with nothing to open it with says so
		/// rather than throwing: this is a convenience, not a capability.
		/// </summary>
		private void OpenSelectedFolder()
		{
			if (Selected() is not { } item) return;
			try
			{
				if (OSTailoredCode.IsUnixHost) Process.Start("xdg-open", item.Path);
				else Process.Start("explorer.exe", $"\"{item.Path}\"");
				_status.Text = $"Opened {item.Path}";
			}
			catch (Exception ex)
			{
				_status.Text = $"Could not open {item.Path}: {ex.Message}";
			}
		}

		/// <summary>Selects a row by its cache location. For tests and screenshots.</summary>
		public bool Select(string path)
		{
			foreach (ListViewItem row in _list.Items)
			{
				if (row.Tag is CacheItem item && item.Path == path) { row.Selected = true; return true; }
			}
			return false;
		}

		/// <summary>
		/// Ticks or unticks one row by its cache location, refusing what a session
		/// is standing on. For tests and screenshots.
		///
		/// The refusal is HERE as well as in the list's ItemCheck because setting
		/// Checked in code does not go through that event on every runtime - Mono
		/// ignores the handler's override - and a rule that holds only when a
		/// mouse is involved is not a rule.
		/// </summary>
		public bool SetChecked(string path, bool ticked)
		{
			foreach (ListViewItem row in _list.Items)
			{
				if (row.Tag is not CacheItem item || item.Path != path) continue;
				if (ticked && item.InUse) return false;
				row.Checked = ticked;
				if (row.Checked) _ticked.Add(item.Path);
				else _ticked.Remove(item.Path);
				UpdateButtons();
				return row.Checked;
			}
			return false;
		}

		/// <summary>Ticks every orphaned row, as the button does. For tests and screenshots.</summary>
		public void TickOrphans() => SelectOrphans();

		/// <summary>What the window is showing, by name.</summary>
		public IReadOnlyList<string> Rows
			=> _list.Items.Cast<ListViewItem>().Select(static r => r.SubItems[1].Text).ToList();

		/// <summary>The cache locations currently ticked.</summary>
		public IReadOnlyList<string> TickedPaths => Ticked().Select(static i => i.Path).ToList();

		/// <summary>Whether Remove would do anything.</summary>
		public bool RemoveEnabled => _remove.Enabled;

		/// <summary>Whether Open Folder would do anything.</summary>
		public bool OpenFolderEnabled => _openFolder.Enabled;
	}
}
