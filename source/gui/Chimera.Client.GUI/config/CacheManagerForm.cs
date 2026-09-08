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
	/// File &gt; Cache Manager: everything Chimera keeps on disk that it could work
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
		private readonly Button _removeKind;

		private List<CacheItem> _items = new();

		protected override string WindowTitleStatic => "Cache Manager";

		/// <param name="survey">takes stock; called on open and after every removal</param>
		public CacheManagerForm(Func<IReadOnlyList<CacheItem>> survey)
		{
			_survey = survey;

			SuspendLayout();
			ClientSize = new(UIHelper.ScaleX(760), UIHelper.ScaleY(440));
			MinimumSize = new(UIHelper.ScaleX(620), UIHelper.ScaleY(360));
			StartPosition = FormStartPosition.CenterParent;
			ShowIcon = false;

			var margin = UIHelper.ScaleX(8);
			var footer = UIHelper.ScaleY(104);

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
			_list.Columns.Add("What", UIHelper.ScaleX(130));
			_list.Columns.Add("Name", UIHelper.ScaleX(250));
			_list.Columns.Add("Detail", UIHelper.ScaleX(160));
			_list.Columns.Add("Size", UIHelper.ScaleX(80), HorizontalAlignment.Right);
			_list.Columns.Add("Last used", UIHelper.ScaleX(100));
			_list.SelectedIndexChanged += (_, _) => ShowSelected();

			_detail = new Label
			{
				Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
				AutoSize = false,
				Location = new(margin, ClientSize.Height - footer + UIHelper.ScaleY(6)),
				Size = new(ClientSize.Width - (2 * margin), UIHelper.ScaleY(32)),
			};

			_status = new Label
			{
				Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
				AutoSize = false,
				Location = new(margin, ClientSize.Height - footer + UIHelper.ScaleY(40)),
				Size = new(ClientSize.Width - (2 * margin), UIHelper.ScaleY(18)),
			};

			var buttonRow = ClientSize.Height - UIHelper.ScaleY(32);
			var bw = UIHelper.ScaleX(150);
			var gap = UIHelper.ScaleX(8);

			_remove = new Button
			{
				Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
				Location = new(margin, buttonRow),
				Size = new(bw, UIHelper.ScaleY(26)),
				Text = "Remove",
			};
			_remove.Click += (_, _) => RemoveSelected();

			_removeKind = new Button
			{
				Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
				Location = new(margin + bw + gap, buttonRow),
				Size = new(bw, UIHelper.ScaleY(26)),
				Text = "Remove all of this kind",
			};
			_removeKind.Click += (_, _) => RemoveKind();

			Button close = new()
			{
				Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
				DialogResult = DialogResult.OK,
				Location = new(ClientSize.Width - margin - UIHelper.ScaleX(90), buttonRow),
				Size = new(UIHelper.ScaleX(90), UIHelper.ScaleY(26)),
				Text = "Close",
			};

			Controls.AddRange(new Control[] { _header, _list, _detail, _status, _remove, _removeKind, close });
			AcceptButton = close;
			ResumeLayout();

			Reload();
		}

		/// <summary>Takes stock again, keeping the selection where it still exists.</summary>
		private void Reload()
		{
			var wasSelected = Selected()?.Path;
			_items = _survey().ToList();

			_list.BeginUpdate();
			_list.Items.Clear();
			foreach (var item in _items)
			{
				ListViewItem row = new(CacheSurvey.Describe(item.Kind)) { Tag = item };
				row.SubItems.Add(item.Label);
				row.SubItems.Add(item.Detail);
				row.SubItems.Add(CacheSurvey.Size(item.Bytes));
				row.SubItems.Add(item.LastUsed == default ? "" : item.LastUsed.ToLocalTime().ToString("yyyy-MM-dd"));
				if (item.InUse) row.ForeColor = SystemColors.GrayText;
				_list.Items.Add(row);
			}
			_list.EndUpdate();

			var total = _items.Sum(static i => i.Bytes);
			_header.Text = _items.Count is 0
				? "Nothing is cached. Everything here is rebuilt as it is needed."
				: $"{_items.Count} cached item(s), {CacheSurvey.Size(total)} in all. Everything here can be"
					+ " removed: what it costs is time, never work.";

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

		private CacheItem? Selected()
			=> _list.SelectedItems.Count is 0 ? null : _list.SelectedItems[0].Tag as CacheItem;

		private void ShowSelected()
		{
			var item = Selected();
			_detail.Text = item is null
				? ""
				: item.InUse
					? $"{item.Cost}{Environment.NewLine}In use right now, so it cannot be removed while it is open."
					: $"{item.Cost}{Environment.NewLine}{item.Path}";
			_remove.Enabled = item is { InUse: false };
			_removeKind.Enabled = item is not null && _items.Any(i => i.Kind == item.Kind && !i.InUse);
		}

		private void RemoveSelected()
		{
			if (Selected() is not { } item) return;
			if (!Confirm(1, item.Bytes, item.Cost)) return;
			var refused = CacheSurvey.Remove(item);
			Reload();
			_status.Text = refused is null ? $"Removed {item.Label}." : $"{item.Label} was not removed: {refused}";
		}

		private void RemoveKind()
		{
			if (Selected() is not { } chosen) return;
			var wanted = _items.Where(i => i.Kind == chosen.Kind && !i.InUse).ToList();
			if (wanted.Count is 0) return;
			if (!Confirm(wanted.Count, wanted.Sum(static i => i.Bytes), chosen.Cost)) return;

			var removed = 0;
			List<string> kept = new();
			foreach (var item in wanted)
			{
				if (CacheSurvey.Remove(item) is null) removed++;
				else kept.Add(item.Label);
			}
			Reload();
			_status.Text = kept.Count is 0
				? $"Removed {removed} item(s)."
				: $"Removed {removed}; left {string.Join(", ", kept)}.";
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
