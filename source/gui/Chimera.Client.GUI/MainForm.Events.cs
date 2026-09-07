using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

using Chimera.Audio;
using Chimera.Client.Common;
using Chimera.Client.GUI.CoreExtensions;
using Chimera.Client.GUI.CustomControls;
using Chimera.Client.GUI.ToolExtensions;
using Chimera.Common;
using Chimera.Common.PathExtensions;
using Chimera.Emulation.Common;
using Chimera.WinForms.Controls;

namespace Chimera.Client.GUI
{
	public partial class MainForm
	{
		private static readonly FilesystemFilterSet ScreenshotsFSFilterSet = new(
			appendAllFilesEntry: false,
			FilesystemFilter.PNGs);

		private void FileSubMenu_DropDownOpened(object sender, EventArgs e)
		{
			CloseRomMenuItem.ShortcutKeyDisplayString = Config.HotkeyBindings["Close ROM"];

			// There is one thing to close, and it is the project - which is open
			// exactly when TAStudio is (see CloseProject). Asked of the WINDOW as
			// well as of the field: closing TAStudio closes the project, so an
			// item still lit after that window has gone offers to close something
			// that is not there, and clicking it did nothing except go grey.
			CloseRomMenuItem.Enabled = _openProject is not null && Tools.IsLoaded<TAStudio>();

			// SAVING IS THE PROJECT'S, and TAStudio is the window it is edited in:
			// a project is open exactly when TAStudio is, so these ask it. Save is
			// offered only when there is something to save; a backup only once the
			// project has a file to make a backup OF.
			var tastudio = Tools.IsLoaded<TAStudio>() ? Tools.TAStudio : null;
			SaveProjectMenuItem.Enabled = tastudio?.HasUnsavedChanges is true;
			SaveProjectAsMenuItem.Enabled = tastudio is not null;
			SaveProjectBackupMenuItem.Enabled = tastudio?.CanSaveBackup is true;

			// nothing is running, so there is nothing to shoot or record
			ScreenshotSubMenu.Enabled = !Emulator.IsNull();

			// a video is an encode of a run, so it needs a run and a picture
			EncodeVideoMenuItem.Enabled = Emulator.HasVideoProvider() && MovieSession.Movie.IsActive();
		}

		private void NewProjectMenuItem_Click(object sender, EventArgs e)
			=> NewProjectDialog();

		private void OpenProjectMenuItem_Click(object sender, EventArgs e)
			=> OpenProjectDialog();

		private void RecentProjectSubMenu_DropDownOpened(object sender, EventArgs e)
			=> RecentProjectSubMenu.ReplaceDropDownItems(Config.RecentProjects.RecentMenu(this, path => LoadProject(path), "Project", noAutoload: true));

		private void ScreenshotSubMenu_DropDownOpening(object sender, EventArgs e)
		{
			ScreenshotCaptureOSDMenuItem1.Checked = Config.ScreenshotCaptureOsd;
			ScreenshotMenuItem.ShortcutKeyDisplayString = Config.HotkeyBindings["Screenshot"];
			ScreenshotClipboardMenuItem.ShortcutKeyDisplayString = Config.HotkeyBindings["Screen Raw to Clipboard"];
			ScreenshotClientClipboardMenuItem.ShortcutKeyDisplayString = Config.HotkeyBindings["Screen Client to Clipboard"];
		}

		private void SaveProjectMenuItem_Click(object sender, EventArgs e)
		{
			if (Tools.IsLoaded<TAStudio>()) Tools.TAStudio.SaveProject();
		}

		private void SaveProjectAsMenuItem_Click(object sender, EventArgs e)
		{
			if (Tools.IsLoaded<TAStudio>()) Tools.TAStudio.SaveProjectAs();
		}

		private void SaveProjectBackupMenuItem_Click(object sender, EventArgs e)
		{
			if (Tools.IsLoaded<TAStudio>()) Tools.TAStudio.SaveProjectBackup();
		}

		/// <summary>
		/// The saving keys, answered here as well as in TAStudio - the same work
		/// is reachable from either window, so the keys have to be too.
		/// </summary>
		/// <remarks>
		/// The File menu's items carry these shortcuts already and it was not
		/// enough: their Enabled state is computed when the menu DROPS DOWN, and
		/// WinForms does not fire a disabled item's shortcut. So Ctrl+S on the main
		/// window worked or did nothing depending on whether that menu had last
		/// been opened with a project loaded - which is no way to save.
		///
		/// Answered from the tool rather than from the menu item, so the condition
		/// is the project's own ("is there anything to save") and not a leftover
		/// from the last time somebody looked at a menu.
		/// </remarks>
		protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
		{
			var tastudio = Tools.IsLoaded<TAStudio>() ? Tools.TAStudio : null;
			if (tastudio is not null)
			{
				switch (keyData)
				{
					case Keys.Control | Keys.S:
						if (tastudio.HasUnsavedChanges) tastudio.SaveProject();
						return true;
					case Keys.Control | Keys.Shift | Keys.S:
						tastudio.SaveProjectAs();
						return true;
				}
			}
			return base.ProcessCmdKey(ref msg, keyData);
		}

		private void CloseRomMenuItem_Click(object sender, EventArgs e)
			=> CloseProject();

		private void EncodeVideoMenuItem_Click(object sender, EventArgs e)
			=> EncodeVideoDialog();

		private void ScreenshotMenuItem_Click(object sender, EventArgs e)
		{
			TakeScreenshot();
		}

		private void ScreenshotAsMenuItem_Click(object sender, EventArgs e)
		{
			var (dir, file) = $"{ScreenshotPrefix()}.{DateTime.Now:yyyy-MM-dd HH.mm.ss}.png".SplitPathToDirAndFile();
			_ = Directory.CreateDirectory(dir);
			var result = this.ShowFileSaveDialog(
				filter: ScreenshotsFSFilterSet,
				initDir: dir,
				initFileName: file);
			if (result is not null) TakeScreenshot(result);
		}

		private void ScreenshotClipboardMenuItem_Click(object sender, EventArgs e)
		{
			TakeScreenshotToClipboard();
		}

		private void ScreenshotClientClipboardMenuItem_Click(object sender, EventArgs e)
		{
			TakeScreenshotClientToClipboard();
		}

		private void ScreenshotCaptureOSDMenuItem_Click(object sender, EventArgs e)
			=> Config.ScreenshotCaptureOsd = !Config.ScreenshotCaptureOsd;

		private void ExitMenuItem_Click(object sender, EventArgs e)
		{
			if (Tools.AskSave())
			{
				Close();
			}
		}

		private void ScheduleShutdown()
			=> _exitRequestPending = true;

		public void CloseEmulator(int? exitCode = null)
		{
			ScheduleShutdown();
			if (exitCode != null) _exitCode = exitCode.Value;
		}

		private void SystemMenuItem_DropDownOpened(object sender, EventArgs e)
		{
			PauseMenuItem.Checked = _didMenuPause ? _wasPaused : EmulatorPaused;

			PauseMenuItem.ShortcutKeyDisplayString = Config.HotkeyBindings["Pause"];
			RebootCoreMenuItem.ShortcutKeyDisplayString = Config.HotkeyBindings["Reboot Core"];

			RealTimeCounterMenuItem.Text = $"Est. {MovieTimeLengthStr(Emulator.EstimatedRealTimeSincePowerOn())} since power-on";
		}

		private static string MovieTimeLengthStr(TimeSpan movieLength)
			=> movieLength.ToString(
				movieLength.Days == 0 ? @"hh\:mm\:ss\.fff" : @"dd\:hh\:mm\:ss\.fff",
				System.Globalization.DateTimeFormatInfo.InvariantInfo);

		private void PauseMenuItem_Click(object sender, EventArgs e)
		{
			if (Config.PauseWhenMenuActivated && sender == PauseMenuItem)
			{
				const string ERR_MSG = nameof(PauseMenuItem_Click) + " ran before " + nameof(MaybeUnpauseFromMenuClosed) + "?";
				Debug.Assert(EmulatorPaused == _wasPaused, ERR_MSG);
				// fall through
			}
			TogglePause();
		}

		private void PowerMenuItem_Click(object sender, EventArgs e)
		{
			RebootCore();
		}

		private void ViewSubMenu_DropDownOpened(object sender, EventArgs e)
		{
			DisplayFPSMenuItem.Checked = Config.DisplayFps;
			DisplayFrameCounterMenuItem.Checked = Config.DisplayFrameCounter;
			DisplayLagCounterMenuItem.Checked = Config.DisplayLagCounter;
			DisplayInputMenuItem.Checked = Config.DisplayInput;
			DisplayRerecordCountMenuItem.Checked = Config.DisplayRerecordCount;
			DisplaySubtitlesMenuItem.Checked = Config.DisplaySubtitles;

			DisplayFPSMenuItem.ShortcutKeyDisplayString = Config.HotkeyBindings["Display FPS"];
			DisplayFrameCounterMenuItem.ShortcutKeyDisplayString = Config.HotkeyBindings["Frame Counter"];
			DisplayLagCounterMenuItem.ShortcutKeyDisplayString = Config.HotkeyBindings["Lag Counter"];
			DisplayInputMenuItem.ShortcutKeyDisplayString = Config.HotkeyBindings["Input Display"];
			SwitchToFullscreenMenuItem.ShortcutKeyDisplayString = Config.HotkeyBindings["Full Screen"];

			DisplayStatusBarMenuItem.Checked = Config.DispChromeStatusBarWindowed;
			DisplayLogWindowMenuItem.Checked = Tools.IsLoaded<LogWindow>();

			DisplayLagCounterMenuItem.Enabled = Emulator.CanPollInput();

			DisplayMessagesMenuItem.Checked = Config.DisplayMessages;
		}

		private void WindowSizeSubMenu_DropDownOpened(object sender, EventArgs e)
		{
			var windowScale = Config.GetWindowScaleFor(Emulator.SystemId);
			foreach (var item in WindowSizeSubMenu.DropDownItems)
			{
				// filter out separators
				if (item is ToolStripMenuItem menuItem && menuItem.Tag is int itemScale)
				{
					menuItem.Checked = itemScale == windowScale && Config.ResizeWithFramebuffer;
				}
			}
			DisableResizeWithFramebufferMenuItem.Checked = !Config.ResizeWithFramebuffer;
		}

		private void DisableResizeWithFramebufferMenuItem_Click(object sender, EventArgs e)
		{
			Config.ResizeWithFramebuffer = !DisableResizeWithFramebufferMenuItem.Checked;
			FrameBufferResized();
		}

		private void WindowSize_Click(object sender, EventArgs e)
		{
			Config.SetWindowScaleFor(Emulator.SystemId, (int) ((ToolStripMenuItem) sender).Tag);
			FrameBufferResized(forceWindowResize: true);
		}

		private void SwitchToFullscreenMenuItem_Click(object sender, EventArgs e)
		{
			ToggleFullscreen();
		}

		private void DisplayFpsMenuItem_Click(object sender, EventArgs e)
		{
			ToggleFps();
		}

		private void DisplayFrameCounterMenuItem_Click(object sender, EventArgs e)
		{
			ToggleFrameCounter();
		}

		private void DisplayLagCounterMenuItem_Click(object sender, EventArgs e)
		{
			ToggleLagCounter();
		}

		private void DisplayInputMenuItem_Click(object sender, EventArgs e)
		{
			ToggleInputDisplay();
		}

		private void DisplayRerecordsMenuItem_Click(object sender, EventArgs e)
			=> Config.DisplayRerecordCount = !Config.DisplayRerecordCount;

		private void DisplaySubtitlesMenuItem_Click(object sender, EventArgs e)
			=> Config.DisplaySubtitles = !Config.DisplaySubtitles;

		private void DisplayStatusBarMenuItem_Click(object sender, EventArgs e)
		{
			Config.DispChromeStatusBarWindowed = !Config.DispChromeStatusBarWindowed;
			SetStatusBar();
		}

		private void DisplayMessagesMenuItem_Click(object sender, EventArgs e)
			=> Config.DisplayMessages = !Config.DisplayMessages;

		private void DisplayLogWindowMenuItem_Click(object sender, EventArgs e)
		{
			Tools.Load<LogWindow>();
		}

		private void ConfigSubMenu_DropDownOpened(object sender, EventArgs e)
		{
			ControllersMenuItem.Enabled = Emulator.ControllerDefinition.Any();
		}

		private void FrameSkipMenuItem_DropDownOpened(object sender, EventArgs e)
		{
			MinimizeSkippingMenuItem.Checked = Config.AutoMinimizeSkipping;
			ClockThrottleMenuItem.Checked = Config.ClockThrottle;
			VsyncThrottleMenuItem.Checked = Config.VSyncThrottle;
			NeverSkipMenuItem.Checked = Config.FrameSkip == 0;
			Frameskip1MenuItem.Checked = Config.FrameSkip == 1;
			Frameskip2MenuItem.Checked = Config.FrameSkip == 2;
			Frameskip3MenuItem.Checked = Config.FrameSkip == 3;
			Frameskip4MenuItem.Checked = Config.FrameSkip == 4;
			Frameskip5MenuItem.Checked = Config.FrameSkip == 5;
			Frameskip6MenuItem.Checked = Config.FrameSkip == 6;
			Frameskip7MenuItem.Checked = Config.FrameSkip == 7;
			Frameskip8MenuItem.Checked = Config.FrameSkip == 8;
			Frameskip9MenuItem.Checked = Config.FrameSkip == 9;
			MinimizeSkippingMenuItem.Enabled = !NeverSkipMenuItem.Checked;
			if (!MinimizeSkippingMenuItem.Enabled)
			{
				MinimizeSkippingMenuItem.Checked = true;
			}

			AudioThrottleMenuItem.Enabled = Config.SoundEnabled;
			AudioThrottleMenuItem.Checked = Config.SoundThrottle;
			VsyncEnabledMenuItem.Checked = Config.VSync;

			Speed100MenuItem.Checked = Config.SpeedPercent == 100;
			Speed100MenuItem.Image = (Config.SpeedPercentAlternate == 100) ? Properties.Resources.FastForward : null;
			Speed150MenuItem.Checked = Config.SpeedPercent == 150;
			Speed150MenuItem.Image = (Config.SpeedPercentAlternate == 150) ? Properties.Resources.FastForward : null;
			Speed400MenuItem.Checked = Config.SpeedPercent == 400;
			Speed400MenuItem.Image = (Config.SpeedPercentAlternate == 400) ? Properties.Resources.FastForward : null;
			Speed200MenuItem.Checked = Config.SpeedPercent == 200;
			Speed200MenuItem.Image = (Config.SpeedPercentAlternate == 200) ? Properties.Resources.FastForward : null;
			Speed75MenuItem.Checked = Config.SpeedPercent == 75;
			Speed75MenuItem.Image = (Config.SpeedPercentAlternate == 75) ? Properties.Resources.FastForward : null;
			Speed50MenuItem.Checked = Config.SpeedPercent == 50;
			Speed50MenuItem.Image = (Config.SpeedPercentAlternate == 50) ? Properties.Resources.FastForward : null;

			Speed50MenuItem.Enabled =
				Speed75MenuItem.Enabled =
				Speed100MenuItem.Enabled =
				Speed150MenuItem.Enabled =
				Speed200MenuItem.Enabled =
				Speed400MenuItem.Enabled =
				Config.ClockThrottle;

			miUnthrottled.Checked = Config.Unthrottled;
		}

		private void KeyPriorityMenuItem_DropDownOpened(object sender, EventArgs e)
		{
			BothHkAndControllerMenuItem.Checked = false;
			InputOverHkMenuItem.Checked = false;
			HkOverInputMenuItem.Checked = false;

			switch (Config.InputHotkeyOverrideOptions)
			{
				default:
				case Config.InputPriority.BOTH:
					BothHkAndControllerMenuItem.Checked = true;
					break;
				case Config.InputPriority.INPUT:
					InputOverHkMenuItem.Checked = true;
					break;
				case Config.InputPriority.HOTKEY:
					HkOverInputMenuItem.Checked = true;
					break;
			}
		}

		private void ControllersMenuItem_Click(object sender, EventArgs e)
		{
			using var controller = new ControllerConfig(this, Emulator, Config);
			if (!this.ShowDialogWithTempMute(controller).IsOk()) return;
			AddOnScreenMessage("Controller settings saved");
			ReinitHostKeybinds(includedHotkeys: false);
		}

		private void HotkeysMenuItem_Click(object sender, EventArgs e)
		{
			using var hotkeyConfig = new HotkeyConfig(Config);
			if (!this.ShowDialogWithTempMute(hotkeyConfig).IsOk()) return;
			AddOnScreenMessage("Hotkey settings saved");
			ReinitHostKeybinds(includedHotkeys: true);
		}

		private void ReinitHostKeybinds(bool includedHotkeys)
		{
			InitControls();
			InputManager.SyncControls(Emulator, MovieSession, Config);
			if (includedHotkeys) Tools.HandleHotkeyUpdate();
		}

		private void MessagesMenuItem_Click(object sender, EventArgs e)
		{
			using var form = new MessageConfig(Config);
			if (this.ShowDialogWithTempMute(form).IsOk()) AddOnScreenMessage("Message settings saved");
		}

		private void PathsMenuItem_Click(object sender, EventArgs e)
		{
			using PathConfig form = new(
				DialogController,
				Config.PathEntries,
				sysID: Game.System,
				newPath => MovieSession.BackupDirectory = newPath);
			if (this.ShowDialogWithTempMute(form).IsOk()) AddOnScreenMessage("Path settings saved");
		}

		/// <summary>
		/// Config > Firmware: every installed core surveyed for what it needs, just
		/// in time - nothing about a core is kept once the window closes, and a
		/// core that is gone takes its rows with it (its chosen paths stay in the
		/// config for when it is put back).
		/// </summary>
		private void FirmwareMenuItem_Click(object sender, EventArgs e)
		{
			var firmwareFolder = Config.PathEntries.FirmwareAbsolutePath();
			IReadOnlyList<FirmwareSurveyGroup> Survey()
			{
				var packages = CorePackageDiscovery.ScanFor(Config);
				var index = FirmwareSurvey.BuildIndex(Config, firmwareFolder, packages.Select(static p => p.Name));
				return FirmwareSurvey.Build(Config, packages, p => FirmwareSurvey.DeclarationsOf(p.Path), firmwareFolder, index);
			}
			using FirmwareSurveyForm form = new(
				Survey,
				(row, path) => CoreFirmwareStore.Remember(Config, row.CoreName, row.Decl, path),
				pickFile: title =>
				{
					using OpenFileDialog picker = new() { Title = title, Filter = "All Files|*.*" };
					return picker.ShowDialog(this) is DialogResult.OK ? picker.FileName : null;
				},
				pickFolder: () =>
				{
					using FolderBrowserDialog picker = new() { Description = "Scan a folder for firmware files" };
					return picker.ShowDialog(this) is DialogResult.OK ? picker.SelectedPath : null;
				},
				scanFolder: folder =>
				{
					// every pinned declaration of every installed core, answered by hash
					// from the folder and its subfolders; what is found is remembered
					// where it lies
					var packages = CorePackageDiscovery.ScanFor(Config);
					var scanned = FirmwareLocator.BuildIndex([ ], ProjectFolderScan.Enumerate(folder).Take(ProjectFolderScan.MaxFiles));
					foreach (var package in packages.Where(static p => p.Error is null))
					{
						foreach (var decl in FirmwareSurvey.DeclarationsOf(package.Path).Decls)
						{
							if (FirmwareLocator.FindEither(decl, scanned) is { } found)
							{
								CoreFirmwareStore.Remember(Config, package.Name, decl, found.Path);
							}
						}
					}
				});
			this.ShowDialogWithTempMute(form);
		}

		/// <summary>
		/// File &gt; Core Manager. Chimera ships no cores; this is where they come
		/// from. Opened by hand here, and by itself once when nothing is installed
		/// (see <see cref="OpenCoreManagerIfNothingIsInstalled"/>).
		/// </summary>
		private void CoreManagerMenuItem_Click(object sender, EventArgs e) => ShowCoreManager();

		public void ShowCoreManager()
		{
			using CoreManagerForm form = new(
				roster: () => CoreRoster.Read(),
				scan: () => CorePackageDiscovery.ScanFor(Config),
				feed: new CoreFeed(token: Config.GitHubToken),
				installer: new CoreInstaller(),
				// a core that has just landed must be usable in this session: discovery
				// is separate from loading precisely so a package can appear without a
				// restart, and the menus read from the scan
				changed: ScanForCorePackages);
			this.ShowDialogWithTempMute(form);
			ScanForCorePackages();
		}

		private void SoundMenuItem_Click(object sender, EventArgs e)
		{
			static IEnumerable<string> GetDeviceNamesCallback(ESoundOutputMethod outputMethod) => outputMethod switch
			{
				ESoundOutputMethod.OpenAL => OpenALSoundOutput.GetDeviceNames(),
				_ => [ ],
			};
			var oldOutputMethod = Config.SoundOutputMethod;
			var oldDevice = Config.SoundDevice;
			using var form = new SoundConfig(this, Config, GetDeviceNamesCallback);
			if (!this.ShowDialogWithTempMute(form).IsOk()) return;

			AddOnScreenMessage("Sound settings saved");
			if (Config.SoundOutputMethod == oldOutputMethod && Config.SoundDevice == oldDevice)
			{
				Sound.StopSound();
			}
			else
			{
				Sound.Dispose();
				Sound = new Sound(Config, () => Emulator.VsyncRate());
			}
			Sound.StartSound();
			RewireSound();
			UpdateStatusBarMuteIndicator();
		}

		private void AutofireMenuItem_Click(object sender, EventArgs e)
		{
			using var form = new AutofireConfig(Config, InputManager.AutoFireController, InputManager.StickyAutofireController);
			if (this.ShowDialogWithTempMute(form).IsOk()) AddOnScreenMessage("Autofire settings saved");
		}

		private void CustomizeMenuItem_Click(object sender, EventArgs e)
		{
			using GuiOptions form = new(
				Config,
				() => ReinitHostKeybinds(includedHotkeys: true));
			if (!this.ShowDialogWithTempMute(form).IsOk()) return;
			AddOnScreenMessage("Custom configurations saved.");
		}

		private void ClockThrottleMenuItem_Click(object sender, EventArgs e)
		{
			Config.ClockThrottle = !Config.ClockThrottle;
			if (Config.ClockThrottle)
			{
				var old = Config.SoundThrottle;
				Config.SoundThrottle = false;
				if (old)
				{
					RewireSound();
				}

				Config.VSyncThrottle = false;
			}

			ThrottleMessage();
		}

		private void AudioThrottleMenuItem_Click(object sender, EventArgs e)
		{
			Config.SoundThrottle = !Config.SoundThrottle;
			RewireSound();
			if (Config.SoundThrottle)
			{
				Config.ClockThrottle = false;
				Config.VSyncThrottle = false;
			}

			ThrottleMessage();
		}

		private void VsyncThrottleMenuItem_Click(object sender, EventArgs e)
		{
			Config.VSyncThrottle = !Config.VSyncThrottle;
			if (Config.VSyncThrottle)
			{
				Config.ClockThrottle = false;
				var old = Config.SoundThrottle;
				Config.SoundThrottle = false;
				if (old)
				{
					RewireSound();
				}
			}

			if (!Config.VSync)
			{
				Config.VSync = true;
				VsyncMessage();
			}

			ThrottleMessage();
		}

		private void VsyncEnabledMenuItem_Click(object sender, EventArgs e)
		{
			Config.VSync = !Config.VSync;
			VsyncMessage();
		}

		private void UnthrottledMenuItem_Click(object sender, EventArgs e)
			=> ToggleUnthrottled();

		private void ToggleUnthrottled()
		{
			Config.Unthrottled = !Config.Unthrottled;
			ThrottleMessage();
		}

		private void MinimizeSkippingMenuItem_Click(object sender, EventArgs e)
			=> Config.AutoMinimizeSkipping = !Config.AutoMinimizeSkipping;

		private void NeverSkipMenuItem_Click(object sender, EventArgs e) { Config.FrameSkip = 0; FrameSkipMessage(); }
		private void Frameskip1MenuItem_Click(object sender, EventArgs e) { Config.FrameSkip = 1; FrameSkipMessage(); }
		private void Frameskip2MenuItem_Click(object sender, EventArgs e) { Config.FrameSkip = 2; FrameSkipMessage(); }
		private void Frameskip3MenuItem_Click(object sender, EventArgs e) { Config.FrameSkip = 3; FrameSkipMessage(); }
		private void Frameskip4MenuItem_Click(object sender, EventArgs e) { Config.FrameSkip = 4; FrameSkipMessage(); }
		private void Frameskip5MenuItem_Click(object sender, EventArgs e) { Config.FrameSkip = 5; FrameSkipMessage(); }
		private void Frameskip6MenuItem_Click(object sender, EventArgs e) { Config.FrameSkip = 6; FrameSkipMessage(); }
		private void Frameskip7MenuItem_Click(object sender, EventArgs e) { Config.FrameSkip = 7; FrameSkipMessage(); }
		private void Frameskip8MenuItem_Click(object sender, EventArgs e) { Config.FrameSkip = 8; FrameSkipMessage(); }
		private void Frameskip9MenuItem_Click(object sender, EventArgs e) { Config.FrameSkip = 9; FrameSkipMessage(); }

		private void Speed50MenuItem_Click(object sender, EventArgs e) => ClickSpeedItem(50);
		private void Speed75MenuItem_Click(object sender, EventArgs e) => ClickSpeedItem(75);
		private void Speed100MenuItem_Click(object sender, EventArgs e) => ClickSpeedItem(100);
		private void Speed150MenuItem_Click(object sender, EventArgs e) => ClickSpeedItem(150);
		private void Speed200MenuItem_Click(object sender, EventArgs e) => ClickSpeedItem(200);
		private void Speed400MenuItem_Click(object sender, EventArgs e) => ClickSpeedItem(400);

		private void BothHkAndControllerMenuItem_Click(object sender, EventArgs e)
		{
			Config.InputHotkeyOverrideOptions = Config.InputPriority.BOTH;
			UpdateKeyPriorityIcon();
		}

		private void InputOverHkMenuItem_Click(object sender, EventArgs e)
		{
			Config.InputHotkeyOverrideOptions = Config.InputPriority.INPUT;
			UpdateKeyPriorityIcon();
		}

		private void HkOverInputMenuItem_Click(object sender, EventArgs e)
		{
			Config.InputHotkeyOverrideOptions = Config.InputPriority.HOTKEY;
			UpdateKeyPriorityIcon();
		}

		private void SaveConfigMenuItem_Click(object sender, EventArgs e)
		{
			FileWriteResult result = SaveConfig();
			if (result.IsError)
			{
				this.ErrorMessageBox(result);
			}
			else
			{
				AddOnScreenMessage("Saved settings");
			}
		}

		private void SaveConfigAsMenuItem_Click(object sender, EventArgs e)
		{
			var (dir, file) = _getConfigPath().SplitPathToDirAndFile();
			var result = this.ShowFileSaveDialog(
				filter: ConfigFileFSFilterSet,
				initDir: dir,
				initFileName: file);
			if (result is not null)
			{
				FileWriteResult saveResult = SaveConfig(result);
				if (saveResult.IsError)
				{
					this.ErrorMessageBox(saveResult);
				}
				else
				{
					AddOnScreenMessage("Copied settings");
				}
			}
		}

		private void LoadConfigMenuItem_Click(object sender, EventArgs e)
		{
			LoadConfigFile(_getConfigPath());
		}

		private void LoadConfigFromMenuItem_Click(object sender, EventArgs e)
		{
			var (dir, file) = _getConfigPath().SplitPathToDirAndFile();
			var result = this.ShowFileOpenDialog(filter: ConfigFileFSFilterSet, initDir: dir!, initFileName: file);
			if (result is not null) LoadConfigFile(result);
		}

		private void ToolsSubMenu_DropDownOpened(object sender, EventArgs e)
		{
			RamWatchMenuItem.ShortcutKeyDisplayString = Config.HotkeyBindings["RAM Watch"];
			RamSearchMenuItem.ShortcutKeyDisplayString = Config.HotkeyBindings["RAM Search"];
			HexEditorMenuItem.ShortcutKeyDisplayString = Config.HotkeyBindings["Hex Editor"];
			LuaConsoleMenuItem.ShortcutKeyDisplayString = Config.HotkeyBindings["Lua Console"];
			HexEditorMenuItem.Enabled = Tools.IsAvailable<HexEditor>();
			RamSearchMenuItem.Enabled = Tools.IsAvailable<RamSearch>();
			RamWatchMenuItem.Enabled = Tools.IsAvailable<RamWatch>();


			// Core-managed tooling: available exactly when the loaded core backs the
			// service (see ICoreSurfaces / ITraceable), so a core with nothing to show
			// simply greys these out.

			MacroToolMenuItem.Enabled = MovieSession.Movie.IsActive() && Tools.IsAvailable<MacroInputTool>();
		}

		private void RamWatchMenuItem_Click(object sender, EventArgs e)
		{
			Tools.LoadRamWatch(true);
		}

		private void RamSearchMenuItem_Click(object sender, EventArgs e) => Tools.Load<RamSearch>();

		private void LuaConsoleMenuItem_Click(object sender, EventArgs e)
		{
			OpenLuaConsole();
		}

		private void HexEditorMenuItem_Click(object sender, EventArgs e)
		{
			Tools.Load<HexEditor>();
		}

		private void MacroToolMenuItem_Click(object sender, EventArgs e)
		{
			Tools.Load<MacroInputTool>();
		}

		private void BatchRunnerMenuItem_Click(object sender, EventArgs e)
		{
			using var form = new BatchRun(this, Config, CreateCoreComm);
			this.ShowDialogWithTempMute(form);
		}

		private void OnlineHelpMenuItem_Click(object sender, EventArgs e)
		{
			Util.OpenUrlExternal("https://toolassisted.run");
		}

		private void AboutMenuItem_Click(object sender, EventArgs e)
		{
			using AboutBox form = new();
			this.ShowDialogWithTempMute(form);
		}

		private void MainFormContextMenu_Opening(object sender, System.ComponentModel.CancelEventArgs e)
		{
			MaybePauseFromMenuOpened();


			bool showMenuVisible = _inFullscreen || !MainMenuStrip.Visible; // need to always be able to restore this as an emergency measure

			if (_argParser._chromeless)
			{
				showMenuVisible = true; // I decided this was always possible in chrome-less mode, we'll see what they think
			}

			var movieIsActive = MovieSession.Movie.IsActive();

			ShowMenuContextMenuItem.Visible =
				ShowMenuContextMenuSeparator.Visible =
				showMenuVisible;



			ContextSeparator_AfterMovie.Visible =
				ContextSeparator_AfterUndo.Visible =
				ScreenshotContextMenuItem.Visible =
				CloseRomContextMenuItem.Visible =
				!Emulator.IsNull();

			RestartMovieContextMenuItem.Visible =
				StopMovieContextMenuItem.Visible =
				ViewSubtitlesContextMenuItem.Visible =
				ViewCommentsContextMenuItem.Visible =
				SaveMovieContextMenuItem.Visible =
				SaveMovieAsContextMenuItem.Visible =
					movieIsActive;

			BackupMovieContextMenuItem.Visible = movieIsActive;

			StopNoSaveContextMenuItem.Visible = movieIsActive && MovieSession.Movie.Changes;

			AddSubtitleContextMenuItem.Visible = !Emulator.IsNull() && movieIsActive && !MovieSession.ReadOnly;

			ConfigContextMenuItem.Visible = _inFullscreen;

			ContextSeparator_AfterROM.Visible = false;


			if (movieIsActive)
			{
				if (MovieSession.ReadOnly)
				{
					ViewSubtitlesContextMenuItem.Text = "View Subtitles";
					ViewCommentsContextMenuItem.Text = "View Comments";
				}
				else
				{
					ViewSubtitlesContextMenuItem.Text = "Edit Subtitles";
					ViewCommentsContextMenuItem.Text = "Edit Comments";
				}
			}


			ShowMenuContextMenuItem.Text = MainMenuStrip.Visible ? "Hide Menu" : "Show Menu";
		}

		private void MainFormContextMenu_Closing(object sender, ToolStripDropDownClosingEventArgs e)
			=> MaybeUnpauseFromMenuClosed();

		private void DisplayConfigMenuItem_Click(object sender, EventArgs e)
		{
			using DisplayConfig window = new(Config, DialogController, GL);
			if (this.ShowDialogWithTempMute(window).IsOk())
			{
				DisplayManager.UpdateGlobals(Config, Emulator);
				FrameBufferResized();
				SynchChrome();
				UpdateWindowTitle();
			}
		}

		private void BackupMovieContextMenuItem_Click(object sender, EventArgs e)
		{
			MovieSession.Movie.SaveBackup();
			AddOnScreenMessage("Backup movie saved.");
		}

		private void ViewSubtitlesContextMenuItem_Click(object sender, EventArgs e)
		{
			if (MovieSession.Movie.NotActive()) return;
			using EditSubtitlesForm form = new(this, MovieSession.Movie, Config.PathEntries, readOnly: MovieSession.ReadOnly);
			this.ShowDialogWithTempMute(form);
		}

		private void AddSubtitleContextMenuItem_Click(object sender, EventArgs e)
		{
			// TODO: rethink this?
			var subForm = new SubtitleMaker();
			subForm.DisableFrame();

			int index = -1;
			var sub = new Subtitle();
			for (int i = 0; i < MovieSession.Movie.Subtitles.Count; i++)
			{
				sub = MovieSession.Movie.Subtitles[i];
				if (Emulator.Frame == sub.Frame)
				{
					index = i;
					break;
				}
			}

			if (index < 0)
			{
				sub = new Subtitle { Frame = Emulator.Frame };
			}

			subForm.Sub = sub;
			if (!this.ShowDialogWithTempMute(subForm).IsOk()) return;

			if (index >= 0) MovieSession.Movie.Subtitles.RemoveAt(index);
			MovieSession.Movie.Subtitles.Add(subForm.Sub);
		}

		private void ViewCommentsContextMenuItem_Click(object sender, EventArgs e)
		{
			if (MovieSession.Movie.NotActive()) return;
			using EditCommentsForm form = new(MovieSession.Movie, MovieSession.ReadOnly);
			this.ShowDialogWithTempMute(form);
		}

		private void ShowMenuContextMenuItem_Click(object sender, EventArgs e)
		{
			MainMenuStrip.Visible = !MainMenuStrip.Visible;
			FrameBufferResized();
		}

		private readonly ScreenshotForm _screenshotTooltip = new();

		private void KeyPriorityStatusLabel_Click(object sender, EventArgs e)
		{
			Config.InputHotkeyOverrideOptions = Config.InputHotkeyOverrideOptions switch
			{
				Config.InputPriority.INPUT => Config.InputPriority.HOTKEY,
				Config.InputPriority.HOTKEY => Config.NoMixedInputHokeyOverride ? Config.InputPriority.INPUT : Config.InputPriority.BOTH,
				_ => Config.InputPriority.INPUT,
			};
			UpdateKeyPriorityIcon();
		}

		private void LinkConnectStatusBarButton_Click(object sender, EventArgs e)
		{
			// toggle Link status (only outside of a movie session)
			if (!MovieSession.Movie.IsPlaying())
			{
				var core = Emulator.AsLinkable();
				core.LinkConnected = !core.LinkConnected;
				Console.WriteLine($"Cable connect status to {core.LinkConnected}");
			}
		}

		private void MainForm_Activated(object sender, EventArgs e)
		{
			if (!Config.RunInBackground) MaybeUnpauseFromMenuClosed();
		}

		private void MainForm_Deactivate(object sender, EventArgs e)
		{
			if (!Config.RunInBackground) MaybePauseFromMenuOpened();
		}

		private void TimerMouseIdle_Tick(object sender, EventArgs e)
		{
			if (_inFullscreen && Config.DispChromeFullscreenAutohideMouse)
			{
				AutohideCursor(hide: true);
			}
		}

		private void MainForm_Enter(object sender, EventArgs e)
		{
			AutohideCursor(hide: false);
		}

		private void MainForm_Resize(object sender, EventArgs e)
		{
			if (Config.CaptureMouse)
			{
				CaptureMouse(false);
				CaptureMouse(true);
			}

			if (_framebufferResizedPending && WindowState is FormWindowState.Normal)
			{
				_framebufferResizedPending = false;
				FrameBufferResized();
			}
		}

		private void MainForm_Shown(object sender, EventArgs e)
		{
			if (Config.RecentWatches.AutoLoad)
			{
				Tools.LoadRamWatch(!Config.DisplayRamWatch);
			}

			HandlePlatformMenus();
		}

		protected override void OnClosed(EventArgs e)
		{
			_windowClosedAndSafeToExitProcess = true;
			base.OnClosed(e);
		}

		private void MainformMenu_MenuActivate(object sender, EventArgs e)
		{
			HandlePlatformMenus();
			MaybePauseFromMenuOpened();
		}

		public void MaybePauseFromMenuOpened()
		{
			if (!Config.PauseWhenMenuActivated) return;
			_wasPaused = EmulatorPaused;
			PauseEmulator();
			_didMenuPause = true; // overwrites value set during PauseEmulator call
		}

		private void MainformMenu_MenuDeactivate(object sender, EventArgs e) => MaybeUnpauseFromMenuClosed();

		public void MaybeUnpauseFromMenuClosed()
		{
			if (_wasPaused || !Config.PauseWhenMenuActivated) return;
			UnpauseEmulator();
		}

		private static void FormDragEnter(object sender, DragEventArgs e)
		{
			e.Set(DragDropEffects.Copy);
		}

		private void FormDragDrop(object sender, DragEventArgs e)
			=> PathsFromDragDrop = (string[]) e.Data.GetData(DataFormats.FileDrop);
	}
}


