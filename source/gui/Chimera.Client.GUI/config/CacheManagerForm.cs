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
	/// Lock / Unlock is a ticked-rows act too, which is what makes "keep these
	/// four runs and let the rest go" one press instead of four.
	///
	/// The window also owns the limit the cache is held under, because this is
	/// where somebody looking at what it weighs already is. A padlock is only
	/// ever about the AUTO-clean: a locked entry is still removed by Remove, on
	/// the grounds that ticking a row and pressing it is not something anybody
	/// does by accident.
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
		private readonly Button _lock;
		private readonly Button _selectOrphans;
		private readonly Button _openFolder;
		private readonly Button _cleanNow;
		private readonly CheckBox _autoClean;
		private readonly NumericUpDown _limit;

		/// <summary>Locks and unlocks what it is given; the model's, so this window can be tested without one.</summary>
		private readonly Action<IReadOnlyList<CacheItem>, bool> _setLocked;

		private readonly Action<CacheCleanPolicy> _savePolicy;

		/// <summary>The limit and whether it is enforced, as the window has it right now.</summary>
		private readonly CacheCleanPolicy _policy;

		private static readonly ImageList _padlocks = BuildPadlocks();

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

		private const int SizeColumn = 9;
		private const int DateColumn = 10;

		private List<CacheItem> _items = new();

		protected override string WindowTitleStatic => "Cache Manager";

		/// <param name="survey">takes stock; called on open and after every removal</param>
		/// <param name="setLocked">records which entries the auto-clean may not take</param>
		/// <param name="policy">the limit as it stands, read once when the window opens</param>
		/// <param name="savePolicy">where a changed limit goes</param>
		public CacheManagerForm(
			Func<IReadOnlyList<CacheItem>> survey,
			Action<IReadOnlyList<CacheItem>, bool>? setLocked = null,
			CacheCleanPolicy? policy = null,
			Action<CacheCleanPolicy>? savePolicy = null)
		{
			_survey = survey;
			_setLocked = setLocked ?? CacheLocks.Set;
			_policy = policy ?? new CacheCleanPolicy();
			_savePolicy = savePolicy ?? (static _ => { });

			SuspendLayout();
			ClientSize = new(UIHelper.ScaleX(1460), UIHelper.ScaleY(500));
			MinimumSize = new(UIHelper.ScaleX(900), UIHelper.ScaleY(380));
			StartPosition = FormStartPosition.CenterParent;
			ShowIcon = false;

			var margin = UIHelper.ScaleX(8);
			var footer = UIHelper.ScaleY(138);
			// three lines of header: what is cached, what nothing is asking for,
			// and whether that is over the limit
			var listTop = UIHelper.ScaleY(90);

			_header = new Label
			{
				Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
				AutoSize = false,
				Location = new(margin, UIHelper.ScaleY(9)),
				Size = new(ClientSize.Width - (2 * margin), UIHelper.ScaleY(50)),
			};

			// Above the list rather than in the header: a WinForms ListView header
			// is not a place a control can live, and here it also says how many are
			// ticked and what they weigh, which a header box could not.
			_selectAll = new CheckBox
			{
				Anchor = AnchorStyles.Top | AnchorStyles.Left,
				AutoSize = true,
				Location = new(margin + UIHelper.ScaleX(2), UIHelper.ScaleY(66)),
				Text = "Select all",
			};
			_selectAll.CheckedChanged += (_, _) => SelectAllChanged();

			// On the same row as Select all and against the other edge: it is a
			// standing rule rather than an act, so it belongs with what the window
			// is showing rather than among the buttons that do something now.
			var unitWidth = UIHelper.ScaleX(28);
			var limitWidth = UIHelper.ScaleX(80);
			var sayWidth = UIHelper.ScaleX(150);
			var right = ClientSize.Width - margin;
			Label unit = new()
			{
				Anchor = AnchorStyles.Top | AnchorStyles.Right,
				AutoSize = false,
				Location = new(right - unitWidth, UIHelper.ScaleY(68)),
				Size = new(unitWidth, UIHelper.ScaleY(20)),
				Text = "GB",
			};
			_limit = new NumericUpDown
			{
				Anchor = AnchorStyles.Top | AnchorStyles.Right,
				Increment = 5m,
				Location = new(right - unitWidth - limitWidth, UIHelper.ScaleY(64)),
				// Whole gigabytes. The smallest cache worth bounding is one console
				// greenzone and those run to tens of them, so a tenth of a gigabyte
				// is a precision nobody has a use for - and every up-down in the
				// frontend is a plain integer, which is what draws correctly on the
				// toolkits Chimera runs on.
				Maximum = 100_000m,
				Minimum = 1m,
				Value = Gigabytes(_policy.LimitBytes),
				Width = limitWidth,
			};
			_limit.ValueChanged += (_, _) => PolicyChanged();
			_autoClean = new CheckBox
			{
				Anchor = AnchorStyles.Top | AnchorStyles.Right,
				AutoSize = false,
				Checked = _policy.Enabled,
				Location = new(right - unitWidth - limitWidth - sayWidth - UIHelper.ScaleX(6), UIHelper.ScaleY(66)),
				Size = new(sayWidth, UIHelper.ScaleY(20)),
				Text = "Keep the cache under",
			};
			_autoClean.CheckedChanged += (_, _) => PolicyChanged();

			_list = new ListView
			{
				Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
				CheckBoxes = true,
				FullRowSelect = true,
				HideSelection = false,
				Location = new(margin, listTop),
				Size = new(ClientSize.Width - (2 * margin), ClientSize.Height - listTop - footer),
				MultiSelect = false,
				ShowItemToolTips = true,
				SmallImageList = _padlocks,
				View = View.Details,
			};
			_list.Columns.Add("", UIHelper.ScaleX(30));
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

			_lock = new Button
			{
				Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
				Location = new(margin + bw + gap, buttonRow),
				Size = new(bw, UIHelper.ScaleY(26)),
				Text = "Lock / Unlock",
			};
			_lock.Click += (_, _) => ToggleLock();

			_selectOrphans = new Button
			{
				Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
				Location = new(margin + (2 * (bw + gap)), buttonRow),
				Size = new(bw, UIHelper.ScaleY(26)),
				Text = "Select all orphans",
			};
			_selectOrphans.Click += (_, _) => SelectOrphans();

			_openFolder = new Button
			{
				Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
				Location = new(margin + (3 * (bw + gap)), buttonRow),
				Size = new(bw, UIHelper.ScaleY(26)),
				Text = "Open Folder",
			};
			_openFolder.Click += (_, _) => OpenSelectedFolder();

			// Set apart from the four that act on rows: this one acts on the whole
			// cache, and by the limit rather than by anything ticked.
			_cleanNow = new Button
			{
				Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
				Location = new(margin + (4 * (bw + gap)) + gap, buttonRow),
				Size = new(bw, UIHelper.ScaleY(26)),
				Text = "Clean Now",
			};
			_cleanNow.Click += (_, _) => CleanNow();

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
				_header, _selectAll, _autoClean, _limit, unit, _list, _detail, _status,
				_remove, _lock, _selectOrphans, _openFolder, _cleanNow, close,
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
				ListViewItem row = new("")
				{
					ImageIndex = item.Locked ? LockedMark : UnlockedMark,
					Tag = item,
					ToolTipText = item.Locked
						? "Locked: the auto-clean leaves this one alone. Remove still takes it."
						: "Unlocked: the auto-clean may take this one when the cache is over its limit.",
				};
				row.SubItems.Add(CacheSurvey.Describe(item.Kind));
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

			UpdateHeader();

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

		/// <summary>
		/// What the window says about itself: how much is cached, how much of it
		/// nothing is asking for, and whether that is more than the limit allows.
		/// Separate from <see cref="Reload"/> because changing the limit changes
		/// this sentence and nothing else - re-reading the disk to answer a
		/// keystroke would be a directory walk per digit.
		/// </summary>
		private void UpdateHeader()
		{
			var total = _items.Sum(static i => i.Bytes);
			var orphaned = _items.Where(static i => i.Orphaned).ToList();
			_header.Text = _items.Count is 0
				? "Nothing is cached. Everything here is rebuilt as it is needed."
				: $"{_items.Count} cached item(s), {CacheSurvey.Size(total)} in all. Everything here can be"
					+ " removed: what it costs is time, never work."
					+ (orphaned.Count is 0
						? ""
						: $"{Environment.NewLine}{orphaned.Count} belong to projects that are no longer where they were"
							+ $" ({CacheSurvey.Size(orphaned.Sum(static i => i.Bytes))}).")
					+ OverTheLimit(total);
		}

		/// <summary>The rows whose box is ticked, in list order.</summary>
		private List<CacheItem> Ticked() => _items.FindAll(i => _ticked.Contains(i.Path));

		private void UpdateButtons()
		{
			if (!_ready) return;
			var ticked = Ticked();
			_remove.Enabled = ticked.Count is not 0;
			_lock.Enabled = ticked.Count is not 0;
			_cleanNow.Enabled = _items.Sum(static i => i.Bytes) > LimitBytes;
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
				// unlocked first: those are the ones the limit can reach, which is
				// the question somebody sorting by the padlock is asking
				0 => items.OrderBy(static i => i.Locked).ThenBy(static i => i.Kind).ThenBy(static i => i.Bytes),
				1 => items.OrderBy(static i => i.Kind).ThenBy(static i => i.Bytes),
				2 => items.OrderBy(static i => i.Label, StringComparer.CurrentCultureIgnoreCase),
				3 => items.OrderBy(static i => i.System, StringComparer.OrdinalIgnoreCase),
				4 => items.OrderBy(static i => i.Core, StringComparer.OrdinalIgnoreCase),
				5 => items.OrderBy(static i => i.Game, StringComparer.CurrentCultureIgnoreCase),
				6 => items.OrderBy(static i => i.Detail, StringComparer.OrdinalIgnoreCase),
				7 => items.OrderBy(static i => i.ProjectPath, StringComparer.CurrentCultureIgnoreCase),
				8 => items.OrderBy(static i => i.Path, StringComparer.CurrentCultureIgnoreCase),
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
				lines.Add(item.Locked
					? "Locked: the auto-clean will not take this one. Remove still will."
					: "Unlocked: the auto-clean may take this one, oldest first, when the cache is over its limit.");
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

		/// <summary>
		/// Locks the ticked rows, or unlocks them when every one of them is already
		/// locked. One button rather than two because the answer to "what will this
		/// do" is visible in the padlocks it is pointed at, and because the mixed
		/// case has an obvious right answer: somebody who ticks a locked row and an
		/// unlocked one and presses this meant to keep both.
		/// </summary>
		public void ToggleLock()
		{
			var ticked = Ticked();
			if (ticked.Count is 0) return;
			var locking = ticked.Any(static i => !i.Locked);
			_setLocked(ticked, locking);
			Reload();
			_status.Text = locking
				? $"Locked {ticked.Count} item(s); the auto-clean will leave them alone."
				: $"Unlocked {ticked.Count} item(s); the auto-clean may take them when the cache is over its limit.";
		}

		/// <summary>
		/// The limit changed, or was switched off. Saved at once - it is a setting,
		/// and a setting somebody has to press Close to keep is one they will lose.
		/// Nothing is removed here: a number being typed passes through 1 on its way
		/// to 100, and a cache that emptied itself mid-keystroke would be a window
		/// nobody dared open. Clean Now is where that is asked for.
		/// </summary>
		private void PolicyChanged()
		{
			_policy.Enabled = _autoClean.Checked;
			_policy.LimitBytes = (long) (_limit.Value * 1024m * 1024m * 1024m);
			_savePolicy(_policy);
			UpdateHeader();
			UpdateButtons();
		}

		/// <summary>
		/// Does now what the auto-clean would do next time: takes the oldest
		/// unlocked entries until the cache is back under the limit. It asks first,
		/// because it is a press rather than a rule, and because it names things
		/// nobody ticked.
		/// </summary>
		private void CleanNow()
		{
			var going = CacheSurvey.WhatWouldGo(_items, LimitBytes).ToList();
			if (going.Count is 0)
			{
				_status.Text = _items.Sum(static i => i.Bytes) > LimitBytes
					? "Over the limit, but everything that is left is locked or in use."
					: "The cache is already under its limit.";
				return;
			}
			if (!Confirm(going.Count, going.Sum(static i => i.Bytes), going)) return;

			var result = CacheSurvey.AutoClean(_items, new CacheCleanPolicy { Enabled = true, LimitBytes = LimitBytes });
			Reload();
			_status.Text = $"Removed {result.Removed.Count} of the oldest unlocked item(s),"
				+ $" freeing {CacheSurvey.Size(result.Before - result.After)}.";
		}

		/// <summary>
		/// What the cache may weigh, as the box has it right now. The tick beside it
		/// decides whether anything enforces it on its own; Clean Now applies it
		/// either way, since somebody who pressed it is enforcing it by hand.
		/// </summary>
		private long LimitBytes => _policy.LimitBytes;

		/// <summary>
		/// The line that says the cache is over its limit, or "". Only ever shown
		/// when it is: the limit itself is already on screen in the box that sets
		/// it, and a window that repeats a setting back is a window with a line
		/// nobody reads.
		/// </summary>
		private string OverTheLimit(long total)
		{
			if (total <= _policy.LimitBytes) return "";
			var reachable = _items.Where(static i => !i.Locked && !i.InUse).Sum(static i => i.Bytes);
			var over = total - _policy.LimitBytes;
			return Environment.NewLine
				+ $"That is {CacheSurvey.Size(over)} over the limit. "
				+ (reachable < over
					? "Everything else is locked or in use, so the limit cannot be met without unlocking something."
					: _autoClean.Checked
						? "The oldest unlocked items go when this window or a project closes, or now with Clean Now."
						: "Nothing is enforcing it while the tick is off; Clean Now applies it once.");
		}

		private static decimal Gigabytes(long bytes)
		{
			var gb = Math.Round(bytes / 1024m / 1024m / 1024m, MidpointRounding.AwayFromZero);
			return gb < 1m ? 1m : gb > 100_000m ? 100_000m : gb;
		}

		private const int UnlockedMark = 0;
		private const int LockedMark = 1;

		/// <summary>
		/// The two padlocks. Drawn rather than shipped, like the firmware windows'
		/// marks: two shapes and two colours do not need a file, and one drawn here
		/// scales with the rest of the window instead of blurring.
		///
		/// Red and open is the one the auto-clean may take; green and shut is the
		/// one it may not. Colour carries it at a glance and the shackle carries it
		/// for anybody who cannot tell the two colours apart.
		/// </summary>
		private static ImageList BuildPadlocks()
		{
			ImageList list = new() { ImageSize = new(16, 16), ColorDepth = ColorDepth.Depth32Bit };
			Bitmap Padlock(Color colour, bool shut)
			{
				Bitmap bmp = new(16, 16);
				using Graphics g = Graphics.FromImage(bmp);
				g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
				using Pen shackle = new(colour, 1.8f);
				// shut: the shackle comes down into the body on both sides
				// open: it stands up and away, hinged on the left
				if (shut) g.DrawArc(shackle, 4.5f, 3f, 7f, 10f, 180f, 180f);
				else g.DrawArc(shackle, 6.5f, 1.5f, 7f, 10f, 180f, 140f);
				using SolidBrush body = new(colour);
				g.FillRectangle(body, 3, 8, 10, 7);
				return bmp;
			}
			list.Images.Add(Padlock(Color.FromArgb(190, 40, 40), shut: false));
			list.Images.Add(Padlock(Color.FromArgb(0, 140, 0), shut: true));
			return list;
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
			=> _list.Items.Cast<ListViewItem>().Select(static r => r.SubItems[2].Text).ToList();

		/// <summary>The cache locations currently ticked.</summary>
		public IReadOnlyList<string> TickedPaths => Ticked().Select(static i => i.Path).ToList();

		/// <summary>Whether Remove would do anything.</summary>
		public bool RemoveEnabled => _remove.Enabled;

		/// <summary>Whether Open Folder would do anything.</summary>
		public bool OpenFolderEnabled => _openFolder.Enabled;

		/// <summary>Whether Lock / Unlock would do anything.</summary>
		public bool LockEnabled => _lock.Enabled;

		/// <summary>Whether Clean Now has anything to bring the cache back under.</summary>
		public bool CleanNowEnabled => _cleanNow.Enabled;

		/// <summary>The limit as the box has it, in gigabytes. For tests and screenshots.</summary>
		public decimal LimitGb
		{
			get => _limit.Value;
			set => _limit.Value = value;
		}

		/// <summary>What the box is showing. For tests: a limit nobody can read is not a limit.</summary>
		public string LimitText => _limit.Text;

		/// <summary>Whether the cache is being held to the limit on its own. For tests.</summary>
		public bool AutoCleanTicked
		{
			get => _autoClean.Checked;
			set => _autoClean.Checked = value;
		}

		/// <summary>The cache locations shown with a shut padlock.</summary>
		public IReadOnlyList<string> LockedPaths
			=> _list.Items.Cast<ListViewItem>()
				.Where(static r => r.ImageIndex == LockedMark)
				.Select(static r => ((CacheItem) r.Tag).Path)
				.ToList();
	}
}
