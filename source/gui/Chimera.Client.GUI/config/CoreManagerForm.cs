#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

using Chimera.Client.Common;

namespace Chimera.Client.GUI
{
	/// <summary>
	/// File &gt; Core Manager: the cores that exist, the ones installed, and the
	/// versions of each.
	///
	/// Chimera ships no cores (see docs/core-manager.md). This window is how they
	/// arrive - and it is the ONLY thing that talks to GitHub. Nothing here happens
	/// on a timer or at startup: every request is one somebody pressed a button to
	/// make. The window opens by itself exactly once, when nothing is installed at
	/// all, because that is the one moment a frontend with no cores cannot do
	/// anything useful without help.
	///
	/// Thin over <see cref="CoreManagerModel"/>, like the firmware windows are over
	/// their surveys: what somebody is told is decided by the model, which is tested
	/// without a UI, and this arranges it.
	/// </summary>
	public sealed class CoreManagerForm : FormBase
	{
		private readonly Func<IReadOnlyList<RosterCore>> _roster;
		private readonly Func<IReadOnlyList<DiscoveredCorePackage>> _scan;
		private readonly CoreFeed _feed;
		private readonly CoreInstaller _installer;
		private readonly Action<RosterCore>? _rememberExternal;
		private readonly Action<RosterCore>? _forgetExternal;
		private readonly Func<string?>? _askForUrl;
		private readonly Action? _changed;

		private readonly ListView _cores;
		private readonly CheckBox _selectAll;
		private readonly ComboBox _versions;
		private readonly Label _versionDetail;
		private readonly Label _header;
		private readonly Label _status;
		private readonly Button _install;
		private readonly Button _removeVersion;
		private readonly Button _checkUpdates;
		private readonly Button _downloadLatest;
		private readonly Button _removeCore;
		private readonly Button _addExternal;
		private readonly CheckBox _devChannel;

		/// <summary>Set while the code is ticking boxes, so its own events do not answer back.</summary>
		private bool _suppressCheckEvents;

		/// <summary>
		/// False until the constructor has built every control.
		///
		/// A ListView raises ItemChecked while its handle is being created, which on
		/// .NET Framework happens inside the constructor - before the buttons the
		/// handler wants to enable exist. Mono does not do this, so the Linux tests
		/// never saw it and it arrived as a NullReferenceException on Windows the
		/// first time somebody opened the window.
		/// </summary>
		private bool _ready;

		private readonly Dictionary<string, IReadOnlyList<CoreRelease>> _feeds = new(StringComparer.OrdinalIgnoreCase);
		private readonly Dictionary<string, string> _feedErrors = new(StringComparer.OrdinalIgnoreCase);

		private List<CoreManagerRow> _rows = new();
		private CancellationTokenSource? _work;
		private bool _busy;

		protected override string WindowTitleStatic => "Core Manager";

		public CoreManagerForm(
			Func<IReadOnlyList<RosterCore>> roster,
			Func<IReadOnlyList<DiscoveredCorePackage>> scan,
			CoreFeed feed,
			CoreInstaller installer,
			Action<RosterCore>? rememberExternal = null,
			Action<RosterCore>? forgetExternal = null,
			Func<string?>? askForUrl = null,
			Action? changed = null)
		{
			_roster = roster;
			_scan = scan;
			_feed = feed;
			_installer = installer;
			_rememberExternal = rememberExternal;
			_forgetExternal = forgetExternal;
			_askForUrl = askForUrl;
			_changed = changed;

			SuspendLayout();
			ClientSize = new(UIHelper.ScaleX(900), UIHelper.ScaleY(500));
			MinimumSize = new(UIHelper.ScaleX(740), UIHelper.ScaleY(420));
			StartPosition = FormStartPosition.CenterParent;
			ShowIcon = false;

			var margin = UIHelper.ScaleX(8);
			var sideWidth = UIHelper.ScaleX(320);
			var footer = UIHelper.ScaleY(76);
			var listTop = UIHelper.ScaleY(70);

			_header = new Label
			{
				AutoSize = false,
				Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
				Location = new(margin, UIHelper.ScaleY(8)),
				Size = new(ClientSize.Width - (2 * margin), UIHelper.ScaleY(32)),
				Text = "Cores are published by their own projects and downloaded from there. Nothing is fetched until you ask. Tick the cores you want to act on; pick a row to choose a particular version of it. Each core carries its own licence, shown once it is installed - some forbid commercial use.",
			};

			// The select-all sits above the list rather than in the header, because a
			// WinForms ListView header is not a place a control can live - and here it
			// also says how many are ticked, which a header box could not.
			_selectAll = new CheckBox
			{
				Anchor = AnchorStyles.Top | AnchorStyles.Left,
				AutoSize = true,
				Location = new(margin + UIHelper.ScaleX(2), UIHelper.ScaleY(48)),
				Text = "Select all",
			};
			_selectAll.CheckedChanged += (_, _) => SelectAllChanged();

			_cores = new ListView
			{
				Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
				FullRowSelect = true,
				HideSelection = false,
				Location = new(margin, listTop),
				Size = new(ClientSize.Width - sideWidth - (3 * margin), ClientSize.Height - listTop - footer),
				MultiSelect = false,
				View = View.Details,
				CheckBoxes = true,
			};
			_cores.Columns.Add("Core", UIHelper.ScaleX(150));
			_cores.Columns.Add("Systems", UIHelper.ScaleX(180));
			_cores.Columns.Add("Installed", UIHelper.ScaleX(190));
			_cores.SelectedIndexChanged += (_, _) => ShowSelectedCore();
			_cores.ItemChecked += (_, _) => { if (!_suppressCheckEvents) UpdateButtons(); };
			// the separator is a row, and a row in a checkbox ListView has a box; it
			// is not a core, so it never ticks
			_cores.ItemCheck += (_, e) =>
			{
				// same reason as _ready: this can fire before the list has rows
				if (e.Index >= 0 && e.Index < _cores.Items.Count && _cores.Items[e.Index].Tag is null) e.NewValue = CheckState.Unchecked;
			};

			// The right column is a panel of its own so everything in it is placed
			// against ITS left edge. Right-anchoring a dozen loose controls to the form
			// puts them wherever the current DPI and font decide to.
			Panel side = new()
			{
				Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Right,
				Location = new(ClientSize.Width - sideWidth - margin, listTop),
				Size = new(sideWidth, ClientSize.Height - listTop - footer),
			};

			Label versionLabel = new()
			{
				AutoSize = true,
				Location = new(0, UIHelper.ScaleY(2)),
				Text = "Version",
			};

			_versions = new ComboBox
			{
				Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
				DropDownStyle = ComboBoxStyle.DropDownList,
				Location = new(0, UIHelper.ScaleY(22)),
				Width = sideWidth,
			};
			_versions.SelectedIndexChanged += (_, _) => ShowSelectedVersion();

			var buttonWidth = (sideWidth - UIHelper.ScaleX(10)) / 2;
			_install = new Button
			{
				Anchor = AnchorStyles.Top | AnchorStyles.Left,
				Location = new(0, UIHelper.ScaleY(54)),
				Size = new(buttonWidth, UIHelper.ScaleY(26)),
				Text = "Install",
			};
			_install.Click += async (_, _) => await InstallSelected().ConfigureAwait(true);

			_removeVersion = new Button
			{
				Anchor = AnchorStyles.Top | AnchorStyles.Right,
				Location = new(sideWidth - buttonWidth, UIHelper.ScaleY(54)),
				Size = new(buttonWidth, UIHelper.ScaleY(26)),
				Text = "Remove version",
			};
			_removeVersion.Click += (_, _) => RemoveSelectedVersion();

			_devChannel = new CheckBox
			{
				Anchor = AnchorStyles.Top | AnchorStyles.Left,
				AutoSize = true,
				Location = new(0, UIHelper.ScaleY(88)),
				Text = "Show development builds",
			};
			_devChannel.CheckedChanged += (_, _) => ShowSelectedCore();

			_versionDetail = new Label
			{
				Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
				AutoSize = false,
				Location = new(0, UIHelper.ScaleY(118)),
				Size = new(sideWidth, side.Height - UIHelper.ScaleY(118)),
			};

			side.Controls.AddRange(new Control[] { versionLabel, _versions, _install, _removeVersion, _devChannel, _versionDetail });

			_status = new Label
			{
				Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
				AutoSize = false,
				Location = new(margin, ClientSize.Height - footer + UIHelper.ScaleY(4)),
				Size = new(ClientSize.Width - (2 * margin), UIHelper.ScaleY(32)),
			};

			// The three that act on what is TICKED, in the order somebody uses them:
			// find out what is new, take it, get rid of one.
			var buttonRow = ClientSize.Height - UIHelper.ScaleY(34);
			var bw = UIHelper.ScaleX(150);
			var gap = UIHelper.ScaleX(8);
			_checkUpdates = new Button
			{
				Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
				Location = new(margin, buttonRow),
				Size = new(bw, UIHelper.ScaleY(26)),
				Text = "Check for updates",
			};
			_checkUpdates.Click += async (_, _) => await CheckForUpdates().ConfigureAwait(true);

			_downloadLatest = new Button
			{
				Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
				Location = new(margin + bw + gap, buttonRow),
				Size = new(bw, UIHelper.ScaleY(26)),
				Text = "Download latest",
			};
			_downloadLatest.Click += async (_, _) => await DownloadLatest().ConfigureAwait(true);

			_removeCore = new Button
			{
				Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
				Location = new(margin + (2 * (bw + gap)), buttonRow),
				Size = new(bw, UIHelper.ScaleY(26)),
				Text = "Remove",
			};
			_removeCore.Click += (_, _) => RemoveCheckedCores();

			_addExternal = new Button
			{
				Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
				Location = new(margin + (3 * (bw + gap)), buttonRow),
				Size = new(bw, UIHelper.ScaleY(26)),
				Text = "Add external core...",
			};
			_addExternal.Click += async (_, _) => await AddExternalCore().ConfigureAwait(true);

			Button close = new()
			{
				Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
				DialogResult = DialogResult.OK,
				Location = new(ClientSize.Width - margin - UIHelper.ScaleX(90), buttonRow),
				Size = new(UIHelper.ScaleX(90), UIHelper.ScaleY(26)),
				Text = "Close",
			};

			Controls.AddRange(new Control[] { _header, _selectAll, _cores, side, _status, _checkUpdates, _downloadLatest, _removeCore, _addExternal, close });
			AcceptButton = close;
			ResumeLayout();

			// every control exists now, so the list's events have something to talk to
			_ready = true;
			Reload();
		}

		/// <summary>Rebuilds the list from the roster and a fresh scan, keeping the selection.</summary>
		private void Reload()
		{
			var wasSelected = Selected()?.Name;
			_rows = CoreManagerModel.Build(_roster(), _scan(), _feeds, _feedErrors).ToList();

			// what was ticked survives a reload: an install or a removal must not
			// silently change what the next button press would act on
			HashSet<string> ticked = new(Checked().Select(static r => r.Name), StringComparer.OrdinalIgnoreCase);

			_suppressCheckEvents = true;
			_cores.BeginUpdate();
			_cores.Items.Clear();
			var separatorDone = false;
			foreach (var row in _rows)
			{
				// ListViewGroups would be the obvious way to do this and Mono's
				// ListView ignores them in Details view, so the divide is a row.
				if (!row.IsOfficial && !separatorDone)
				{
					separatorDone = true;
					ListViewItem divide = new("External cores") { Tag = null, ForeColor = SystemColors.GrayText };
					divide.SubItems.Add("");
					divide.SubItems.Add("added by hand");
					_cores.Items.Add(divide);
				}
				ListViewItem item = new(row.Name) { Tag = row };
				item.SubItems.Add(SystemNames.Of(row.Systems));
				item.SubItems.Add(InstalledText(row));
				if (!row.IsInstalled) item.ForeColor = SystemColors.GrayText;
				item.Checked = ticked.Contains(row.Name);
				_cores.Items.Add(item);
			}
			_cores.EndUpdate();
			_suppressCheckEvents = false;

			if (wasSelected is not null && ItemFor(wasSelected) is { } keep) keep.Selected = true;
			else if (_cores.Items.Count > 0 && _cores.Items[0].Tag is not null) _cores.Items[0].Selected = true;
			ShowSelectedCore();
			UpdateButtons();
		}

		/// <summary>The rows whose box is ticked, in list order.</summary>
		private List<CoreManagerRow> Checked()
		{
			List<CoreManagerRow> found = new();
			if (_cores is null) return found;
			foreach (ListViewItem item in _cores.Items)
			{
				if (item.Checked && item.Tag is CoreManagerRow row) found.Add(row);
			}
			return found;
		}

		/// <summary>The list item for one core, or null. The separator has no row.</summary>
		private ListViewItem? ItemFor(string name)
		{
			foreach (ListViewItem item in _cores.Items)
			{
				if (item.Tag is CoreManagerRow row && string.Equals(row.Name, name, StringComparison.OrdinalIgnoreCase)) return item;
			}
			return null;
		}

		/// <summary>
		/// The three bulk buttons act on what is ticked, so with nothing ticked there
		/// is nothing for them to do and they say so by being unavailable rather than
		/// by complaining afterwards.
		/// </summary>
		private void UpdateButtons()
		{
			if (!_ready) return;
			var any = Checked().Count is not 0;
			_checkUpdates.Enabled = _downloadLatest.Enabled = _removeCore.Enabled = any && !_busy;
			_addExternal.Enabled = !_busy;
			_selectAll.Text = any ? $"Select all ({Checked().Count} ticked)" : "Select all";
		}

		private void SelectAllChanged()
		{
			if (_suppressCheckEvents) return;
			_suppressCheckEvents = true;
			foreach (ListViewItem item in _cores.Items)
			{
				if (item.Tag is not null) item.Checked = _selectAll.Checked;
			}
			_suppressCheckEvents = false;
			UpdateButtons();
		}

		private static string InstalledText(CoreManagerRow row)
		{
			if (!row.IsInstalled) return "not installed";
			var versions = row.Installed.Select(static p => p.ShortVersion).Where(static v => v.Length is not 0).ToList();
			var text = versions.Count switch
			{
				0 => $"{row.Installed.Count} installed",
				1 => versions[0],
				_ => $"{versions[0]}  (+{versions.Count - 1} more)",
			};
			return row.Update is not null ? $"{text}  - update available" : text;
		}

		private CoreManagerRow? Selected()
			=> _cores.SelectedItems.Count is 0 ? null : _cores.SelectedItems[0].Tag as CoreManagerRow;

		/// <summary>
		/// The versions offered for the selected core: what is installed, plus
		/// whatever has been fetched, newest first. Development builds are hidden
		/// unless asked for - a dev release is replaced on every push, so a movie
		/// recorded against one can stop being fetchable.
		/// </summary>
		private void ShowSelectedCore()
		{
			var row = Selected();
			_versions.BeginUpdate();
			_versions.Items.Clear();
			if (row is not null)
			{
				foreach (var release in Offered(row))
				{
					// a version that is both published and installed is ONE line: it can
					// be removed, and there is nothing to install
					var have = row.Installed.FirstOrDefault(p => string.Equals(p.Version, release.Version, StringComparison.OrdinalIgnoreCase));
					_versions.Items.Add(new VersionChoice(release, have?.Path));
				}
				foreach (var package in row.Installed.Where(p => Offered(row).All(r => !string.Equals(r.Version, p.Version, StringComparison.OrdinalIgnoreCase))))
				{
					_versions.Items.Add(new VersionChoice(package));
				}
			}
			_versions.EndUpdate();
			if (_versions.Items.Count > 0) _versions.SelectedIndex = 0;

			_devChannel.Enabled = row?.IsUnclaimed is false;
			ShowSelectedVersion();
			if (row?.FeedError is { } error) Say(error);
		}

		private IReadOnlyList<CoreRelease> Offered(CoreManagerRow row)
			=> _devChannel.Checked
				? row.Available
				: row.Available.Where(static r => r.Channel is not CoreChannel.Dev).ToList();

		private void ShowSelectedVersion()
		{
			var row = Selected();
			var choice = _versions.SelectedItem as VersionChoice;
			_versionDetail.Text = Detail(row, choice);
			_install.Enabled = !_busy && choice?.Release is not null && !choice.Installed;
			_removeVersion.Enabled = !_busy && choice?.InstalledPath is not null;
			if (row is not null && row.Available.Count is 0 && !row.IsUnclaimed && choice is null)
			{
				_install.Enabled = !_busy;
				_install.Text = "Fetch versions";
			}
			else
			{
				_install.Text = "Install";
			}
		}

		private string Detail(CoreManagerRow? row, VersionChoice? choice)
		{
			if (row is null) return "";
			if (row.IsUnclaimed) return "Installed from outside the official cores. Chimera has nowhere to check this one for updates.";
			if (choice is null)
			{
				return row.FeedError ?? "No versions fetched yet. Press Fetch versions to ask this core's repository what it has published.";
			}
			var lines = new List<string> { choice.Detail };
			if (choice.Release is { } release)
			{
				lines.Add(release.Channel is CoreChannel.Dev
					? "Development build: replaced on every change, so it may stop being downloadable."
					: "Published build: kept permanently, so a movie recorded on it stays replayable.");
				if (release.AssetSize > 0) lines.Add($"{release.AssetSize / 1024 / 1024} MB");
			}
			if (choice.Installed || choice.InstalledPath is not null) lines.Add("Installed.");
			// The terms, once they can be read - which is once the package is here.
			// The bundle used to carry every core and compute one LICENSES.md from
			// them; it carries none now, so this is where a core says what it demands.
			if (choice.InstalledPath is { } path)
			{
				lines.Add(CoreLicence.Read(path)?.Summary() is { Length: not 0 } terms
					? terms
					: "This package states no licence.");
			}
			return string.Join(Environment.NewLine, lines);
		}

		private void Say(string message) => _status.Text = message;

		private void Busy(bool busy)
		{
			_busy = busy;
			UpdateButtons();
			ShowSelectedVersion();
			Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
		}

		/// <summary>Asks one core's repository what it has, and remembers the answer for this session.</summary>
		private async Task<bool> Fetch(RosterCore core, CancellationToken cancel)
		{
			var result = await _feed.FetchAsync(core, cancel).ConfigureAwait(true);
			_feeds[core.Id] = result.Releases;
			if (result.Error is null) _feedErrors.Remove(core.Id);
			else _feedErrors[core.Id] = result.Error;
			return result.Ok;
		}

		private async Task InstallSelected()
		{
			var row = Selected();
			if (row?.Core is null) return;
			// nothing chosen means nothing has been asked for yet: the button is
			// Fetch versions, and pressing it is the request
			if (_versions.SelectedItem is not VersionChoice { Release: { } release })
			{
				await FetchSelectedVersions().ConfigureAwait(true);
				return;
			}
			_work = new();
			try
			{
				Busy(true);
				_ = await Install(row.Core, release, _work.Token).ConfigureAwait(true);
			}
			finally
			{
				Busy(false);
				_work?.Dispose();
				_work = null;
			}
		}

		/// <summary>
		/// Asks the selected core's repository what it has published, and fills the
		/// version selector with the answer. Public so a test can drive it: it is the
		/// one request in this window somebody makes on purpose, and what it fills in
		/// is the whole point of the window.
		/// </summary>
		public async Task FetchSelectedVersions()
		{
			if (Selected()?.Core is not { } core) return;
			_work = new();
			try
			{
				Busy(true);
				Say($"Asking {core.Repo} what it has published...");
				var ok = await Fetch(core, _work.Token).ConfigureAwait(true);
				Reload();
				Say(ok
					? _feeds[core.Id].Count is 0
						? $"{core.Name} has published no versions yet."
						: $"{core.Name}: {_feeds[core.Id].Count} versions published."
					: _feedErrors[core.Id]);
			}
			finally
			{
				Busy(false);
				_work?.Dispose();
				_work = null;
			}
		}

		/// <summary>Ticks or unticks the core named <paramref name="name"/>. For tests and screenshots.</summary>
		public bool SetChecked(string name, bool ticked)
		{
			if (ItemFor(name) is not { } item) return false;
			item.Checked = ticked;
			UpdateButtons();
			return true;
		}

		/// <summary>Whether the buttons that act on ticked cores are available.</summary>
		public bool BulkActionsEnabled => _checkUpdates.Enabled && _downloadLatest.Enabled && _removeCore.Enabled;

		/// <summary>Selects the core named <paramref name="name"/>, if it is listed.</summary>
		public bool Select(string name)
		{
			if (ItemFor(name) is not { } item) return false;
			item.Selected = true;
			return true;
		}

		/// <returns>true if the package is now in the store</returns>
		private async Task<bool> Install(RosterCore core, CoreRelease release, CancellationToken cancel)
		{
			Say($"Downloading {core.Name} {release.ShortVersion}...");
			var result = await _installer.InstallAsync(
				core,
				release,
				(done, total) => Say(total > 0
					? $"Downloading {core.Name} {release.ShortVersion}: {done * 100 / total}%"
					: $"Downloading {core.Name} {release.ShortVersion}: {done / 1024} KB"),
				cancel).ConfigureAwait(true);
			Reload();
			_changed?.Invoke();
			Say(result.Ok
				? $"Installed {core.Name} {release.ShortVersion}. It can be used straight away; a version of a core already in use needs a restart."
				: $"{core.Name} {release.ShortVersion} was not installed: {result.Error}");
			return result.Ok;
		}

		private async Task CheckForUpdates()
		{
			var wanted = Checked().Where(static r => r.Core is not null).ToList();
			if (wanted.Count is 0)
			{
				Say("Those cores are not published anywhere Chimera can ask.");
				return;
			}
			_work = new();
			try
			{
				Busy(true);
				var done = 0;
				foreach (var row in wanted)
				{
					Say($"Checking {row.Core!.Name} ({++done} of {wanted.Count})...");
					_ = await Fetch(row.Core, _work.Token).ConfigureAwait(true);
				}
				Reload();
				// nothing is downloaded here: this says what is newer and stops
				var updates = _rows.Where(static r => r.Update is not null).Select(static r => r.Name).ToList();
				Say(updates.Count is 0
					? $"Up to date: {string.Join(", ", wanted.Select(static r => r.Name))}."
					: $"Updates available: {string.Join(", ", updates)}. Press Download latest to take them.");
			}
			finally
			{
				Busy(false);
				_work?.Dispose();
				_work = null;
			}
		}

		/// <summary>
		/// Installs the newest published version of every ticked core that has not
		/// got it. A core already holding the newest is left alone rather than
		/// downloaded again.
		/// </summary>
		private async Task DownloadLatest()
		{
			var wanted = Checked().Where(static r => r.Core is not null).ToList();
			if (wanted.Count is 0)
			{
				Say("Those cores are not published anywhere Chimera can fetch from.");
				return;
			}
			_work = new();
			try
			{
				Busy(true);
				var installed = 0;
				var already = 0;
				List<string> failed = new();
				foreach (var row in wanted)
				{
					var core = row.Core!;
					if (row.Available.Count is 0)
					{
						Say($"Asking {core.Name} what it has published...");
						if (!await Fetch(core, _work.Token).ConfigureAwait(true))
						{
							failed.Add($"{core.Name} ({_feedErrors[core.Id]})");
							continue;
						}
						Reload();
					}
					var releases = _feeds.TryGetValue(core.Id, out var r) ? r : [ ];
					if (CoreReleases.Newest(releases) is not { } release)
					{
						failed.Add($"{core.Name} (no published version)");
						continue;
					}
					var current = _rows.FirstOrDefault(x => x.Core?.Id == core.Id);
					if (current?.Has(release) is true) { already++; continue; }
					if (await Install(core, release, _work.Token).ConfigureAwait(true)) installed++;
					else failed.Add(core.Name);
				}
				Reload();
				List<string> said = new();
				if (installed is not 0) said.Add($"installed {installed}");
				if (already is not 0) said.Add($"{already} already newest");
				if (failed.Count is not 0) said.Add($"could not install {string.Join(", ", failed)}");
				Say(said.Count is 0 ? "Nothing to do." : char.ToUpper(said[0][0]) + string.Join("; ", said).Substring(1) + ".");
			}
			finally
			{
				Busy(false);
				_work?.Dispose();
				_work = null;
			}
		}

		/// <summary>
		/// Removes every installed version of every ticked core.
		///
		/// An official core keeps its row and goes back to reading "not installed" -
		/// it can be fetched again from the roster. An external one is forgotten
		/// entirely, because nothing but its own entry was keeping it in the list.
		/// </summary>
		private void RemoveCheckedCores()
		{
			var wanted = Checked().Where(static r => r.IsInstalled || r.RowGoesWhenRemoved).ToList();
			if (wanted.Count is 0)
			{
				Say("Nothing is installed for the cores you ticked.");
				return;
			}
			var versions = wanted.Sum(static r => r.Installed.Count);
			if (MessageBox.Show(
				this,
				$"Remove {versions} installed version(s) of {wanted.Count} core(s)?{Environment.NewLine}{Environment.NewLine}"
					+ "A movie recorded on one of these exact builds needs it to replay.",
				"Remove cores",
				MessageBoxButtons.OKCancel,
				MessageBoxIcon.Warning) is not DialogResult.OK)
			{
				return;
			}

			var removed = 0;
			List<string> kept = new();
			foreach (var row in wanted)
			{
				foreach (var path in row.InstalledPaths)
				{
					if (!CoreStore.Owns(path)) { kept.Add(Path.GetFileName(path)); continue; }
					try
					{
						File.Delete(path);
						removed++;
					}
					catch (Exception ex)
					{
						kept.Add($"{Path.GetFileName(path)} ({ex.Message})");
					}
				}
				// an external core exists only because somebody added it; with its
				// packages gone there is nothing left for a row to be about
				if (row.RowGoesWhenRemoved && row.Core is { IsExternal: true }) _forgetExternal?.Invoke(row.Core);
			}
			Reload();
			_changed?.Invoke();
			Say(kept.Count is 0
				? $"Removed {removed} version(s)."
				: $"Removed {removed}; left alone {string.Join(", ", kept)} (not the manager's to delete).");
		}

		/// <summary>
		/// Adds a core published somewhere other than the official set, by the address
		/// of its GitHub page. The repository is asked what it publishes before it is
		/// remembered, so a wrong address fails here rather than becoming a row that
		/// can never do anything.
		/// </summary>
		private async Task AddExternalCore()
		{
			if (_askForUrl is null) return;
			var typed = _askForUrl();
			if (string.IsNullOrWhiteSpace(typed)) return;
			if (RosterCore.RepoFromUrl(typed!) is not { } repo)
			{
				Say($"That is not a GitHub repository address: {typed}");
				return;
			}
			_work = new();
			try
			{
				Busy(true);
				Say($"Asking {repo} what it publishes...");
				var (core, error) = await _feed.ProbeAsync(repo, _work.Token).ConfigureAwait(true);
				if (core is null)
				{
					Say(error ?? $"{repo} could not be read.");
					return;
				}
				_rememberExternal?.Invoke(core);
				Reload();
				_ = Select(core.Name);
				Say($"Added {core.Name} from {repo}. Tick it and press Download latest.");
			}
			finally
			{
				Busy(false);
				_work?.Dispose();
				_work = null;
			}
		}

		/// <summary>
		/// Deletes one installed version. Only ever one, and only ever one the
		/// manager put there: an old build is the only way to replay a movie recorded
		/// on it, so nothing removes a version to make room for another.
		/// </summary>
		private void RemoveSelectedVersion()
		{
			var row = Selected();
			if (row is null || _versions.SelectedItem is not VersionChoice { InstalledPath: { } path }) return;
			if (!CoreStore.Owns(path))
			{
				Say($"{Path.GetFileName(path)} was not downloaded by the manager, so it is not the manager's to delete. It is at {path}.");
				return;
			}
			if (MessageBox.Show(
				this,
				$"Remove {row.Name} {Path.GetFileNameWithoutExtension(path)}?{Environment.NewLine}{Environment.NewLine}A movie recorded on this exact build needs it to replay.",
				"Remove core",
				MessageBoxButtons.OKCancel,
				MessageBoxIcon.Warning) is not DialogResult.OK)
			{
				return;
			}
			try
			{
				File.Delete(path);
				Reload();
				_changed?.Invoke();
				Say($"Removed {Path.GetFileName(path)}.");
			}
			catch (Exception ex)
			{
				Say($"Could not remove it: {ex.Message}");
			}
		}

		protected override void OnFormClosing(FormClosingEventArgs e)
		{
			_work?.Cancel();
			base.OnFormClosing(e);
		}

		/// <summary>One line of the version selector: a published version, an installed one, or both.</summary>
		private sealed class VersionChoice
		{
			public VersionChoice(CoreRelease release, string? installedPath)
			{
				Release = release;
				InstalledPath = installedPath;
				Installed = installedPath is not null;
				Detail = $"Published {release.PublishedAt.ToLocalTime():yyyy-MM-dd}{Environment.NewLine}Commit {release.DisplayVersion}";
			}

			public VersionChoice(DiscoveredCorePackage package)
			{
				InstalledPath = package.Path;
				Installed = true;
				_text = package.ShortVersion.Length is 0 ? Path.GetFileNameWithoutExtension(package.Path) : package.ShortVersion;
				Detail = $"Installed at {package.Path}";
			}

			private readonly string? _text;

			public CoreRelease? Release { get; }

			public bool Installed { get; }

			/// <summary>Where this version is in the store, when it is installed at all.</summary>
			public string? InstalledPath { get; }

			/// <summary>The date and the short commit: the two things somebody comparing builds needs.</summary>
			public string Detail { get; } = "";

			public override string ToString()
			{
				if (Release is null) return $"{_text}  (installed)";
				var mark = Installed ? "  (installed)" : "";
				return $"{Release.DateAndCommit}{(Release.Channel is CoreChannel.Dev ? "  dev" : "")}{mark}";
			}
		}
	}
}
