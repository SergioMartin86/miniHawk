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
		private readonly Action? _changed;

		private readonly ListView _cores;
		private readonly ComboBox _versions;
		private readonly Label _versionDetail;
		private readonly Label _header;
		private readonly Label _status;
		private readonly Button _install;
		private readonly Button _remove;
		private readonly Button _checkUpdates;
		private readonly Button _downloadAll;
		private readonly CheckBox _devChannel;

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
			Action? changed = null)
		{
			_roster = roster;
			_scan = scan;
			_feed = feed;
			_installer = installer;
			_changed = changed;

			SuspendLayout();
			ClientSize = new(UIHelper.ScaleX(900), UIHelper.ScaleY(500));
			MinimumSize = new(UIHelper.ScaleX(740), UIHelper.ScaleY(420));
			StartPosition = FormStartPosition.CenterParent;
			ShowIcon = false;

			var margin = UIHelper.ScaleX(8);
			var sideWidth = UIHelper.ScaleX(320);
			var footer = UIHelper.ScaleY(76);
			var listTop = UIHelper.ScaleY(46);

			_header = new Label
			{
				AutoSize = false,
				Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
				Location = new(margin, UIHelper.ScaleY(8)),
				Size = new(ClientSize.Width - (2 * margin), UIHelper.ScaleY(32)),
				Text = "Cores are published by their own projects and downloaded from there. Nothing is fetched until you ask: pick a core, then a version.",
			};

			_cores = new ListView
			{
				Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
				FullRowSelect = true,
				HideSelection = false,
				Location = new(margin, listTop),
				Size = new(ClientSize.Width - sideWidth - (3 * margin), ClientSize.Height - listTop - footer),
				MultiSelect = false,
				View = View.Details,
			};
			_cores.Columns.Add("Core", UIHelper.ScaleX(150));
			_cores.Columns.Add("Systems", UIHelper.ScaleX(180));
			_cores.Columns.Add("Installed", UIHelper.ScaleX(190));
			_cores.SelectedIndexChanged += (_, _) => ShowSelectedCore();

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

			_remove = new Button
			{
				Anchor = AnchorStyles.Top | AnchorStyles.Right,
				Location = new(sideWidth - buttonWidth, UIHelper.ScaleY(54)),
				Size = new(buttonWidth, UIHelper.ScaleY(26)),
				Text = "Remove",
			};
			_remove.Click += (_, _) => RemoveSelected();

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

			side.Controls.AddRange(new Control[] { versionLabel, _versions, _install, _remove, _devChannel, _versionDetail });

			_status = new Label
			{
				Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
				AutoSize = false,
				Location = new(margin, ClientSize.Height - footer + UIHelper.ScaleY(4)),
				Size = new(ClientSize.Width - (2 * margin), UIHelper.ScaleY(32)),
			};

			var buttonRow = ClientSize.Height - UIHelper.ScaleY(34);
			_checkUpdates = new Button
			{
				Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
				Location = new(margin, buttonRow),
				Size = new(UIHelper.ScaleX(160), UIHelper.ScaleY(26)),
				Text = "Check for updates",
			};
			_checkUpdates.Click += async (_, _) => await CheckForUpdates().ConfigureAwait(true);

			_downloadAll = new Button
			{
				Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
				Location = new(margin + UIHelper.ScaleX(168), buttonRow),
				Size = new(UIHelper.ScaleX(160), UIHelper.ScaleY(26)),
				Text = "Download missing cores",
			};
			_downloadAll.Click += async (_, _) => await DownloadAll().ConfigureAwait(true);

			Button close = new()
			{
				Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
				DialogResult = DialogResult.OK,
				Location = new(ClientSize.Width - margin - UIHelper.ScaleX(90), buttonRow),
				Size = new(UIHelper.ScaleX(90), UIHelper.ScaleY(26)),
				Text = "Close",
			};

			Controls.AddRange(new Control[] { _header, _cores, side, _status, _checkUpdates, _downloadAll, close });
			AcceptButton = close;
			ResumeLayout();

			Reload();
		}

		/// <summary>Rebuilds the list from the roster and a fresh scan, keeping the selection.</summary>
		private void Reload()
		{
			var wasSelected = Selected()?.Name;
			_rows = CoreManagerModel.Build(_roster(), _scan(), _feeds, _feedErrors).ToList();

			_cores.BeginUpdate();
			_cores.Items.Clear();
			foreach (var row in _rows)
			{
				ListViewItem item = new(row.Name);
				item.SubItems.Add(SystemNames.Of(row.Systems));
				item.SubItems.Add(InstalledText(row));
				if (!row.IsInstalled) item.ForeColor = SystemColors.GrayText;
				_cores.Items.Add(item);
			}
			_cores.EndUpdate();

			var index = wasSelected is null ? 0 : Math.Max(0, _rows.FindIndex(r => r.Name == wasSelected));
			if (_cores.Items.Count > 0) _cores.Items[Math.Min(index, _cores.Items.Count - 1)].Selected = true;
			ShowSelectedCore();
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
			=> _cores.SelectedIndices.Count is 0 ? null : _rows.ElementAtOrDefault(_cores.SelectedIndices[0]);

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

			_devChannel.Enabled = row?.IsUnofficial is false;
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
			_remove.Enabled = !_busy && choice?.InstalledPath is not null;
			if (row is not null && row.Available.Count is 0 && !row.IsUnofficial && choice is null)
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
			if (row.IsUnofficial) return "Installed from outside the official cores. Chimera has nowhere to check this one for updates.";
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
			return string.Join(Environment.NewLine, lines);
		}

		private void Say(string message) => _status.Text = message;

		private void Busy(bool busy)
		{
			_busy = busy;
			_checkUpdates.Enabled = _downloadAll.Enabled = !busy;
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
				await Install(row.Core, release, _work.Token).ConfigureAwait(true);
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

		/// <summary>Selects the core named <paramref name="name"/>, if it is listed.</summary>
		public bool Select(string name)
		{
			var index = _rows.FindIndex(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
			if (index < 0 || index >= _cores.Items.Count) return false;
			_cores.Items[index].Selected = true;
			return true;
		}

		private async Task Install(RosterCore core, CoreRelease release, CancellationToken cancel)
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
		}

		private async Task CheckForUpdates()
		{
			var installed = _rows.Where(static r => r.IsInstalled && r.Core is not null).ToList();
			if (installed.Count is 0)
			{
				Say("Nothing is installed yet, so there is nothing to check. Pick a core and install one.");
				return;
			}
			_work = new();
			try
			{
				Busy(true);
				var checkedCount = 0;
				foreach (var row in installed)
				{
					Say($"Checking {row.Core!.Name} ({++checkedCount} of {installed.Count})...");
					_ = await Fetch(row.Core, _work.Token).ConfigureAwait(true);
				}
				Reload();
				var updates = _rows.Where(static r => r.Update is not null).Select(static r => r.Name).ToList();
				Say(updates.Count is 0
					? $"All {installed.Count} installed cores are up to date."
					: $"Updates available: {string.Join(", ", updates)}.");
			}
			finally
			{
				Busy(false);
				_work?.Dispose();
				_work = null;
			}
		}

		/// <summary>
		/// Installs the newest published version of every core in the roster that has
		/// none. Deliberately not "every core to its newest": replacing what somebody
		/// is using, in bulk, is not what a button called Download all should do.
		/// </summary>
		private async Task DownloadAll()
		{
			var wanted = _rows.Where(static r => r.Core is not null && !r.IsInstalled).ToList();
			if (wanted.Count is 0)
			{
				Say("Every core in the list is already installed.");
				return;
			}
			if (MessageBox.Show(
				this,
				$"Download the newest published build of {wanted.Count} cores? They are large; this can take a while.",
				"Download all",
				MessageBoxButtons.OKCancel,
				MessageBoxIcon.Question) is not DialogResult.OK)
			{
				return;
			}
			_work = new();
			try
			{
				Busy(true);
				var installed = 0;
				var failed = new List<string>();
				foreach (var row in wanted)
				{
					var core = row.Core!;
					Say($"Asking {core.Name} what it has published...");
					if (!await Fetch(core, _work.Token).ConfigureAwait(true))
					{
						failed.Add($"{core.Name} ({_feedErrors[core.Id]})");
						continue;
					}
					if (CoreReleases.Newest(_feeds[core.Id]) is not { } release)
					{
						failed.Add($"{core.Name} (no published version)");
						continue;
					}
					await Install(core, release, _work.Token).ConfigureAwait(true);
					if (CoreManagerModel.Build(_roster(), _scan(), _feeds, _feedErrors).Any(r => r.Core?.Id == core.Id && r.IsInstalled)) installed++;
					else failed.Add(core.Name);
				}
				Reload();
				Say(failed.Count is 0
					? $"Installed {installed} cores."
					: $"Installed {installed}; could not install {string.Join(", ", failed)}.");
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
		private void RemoveSelected()
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
