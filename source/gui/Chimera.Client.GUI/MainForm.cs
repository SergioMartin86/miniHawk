using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Security.AccessControl;
using System.Security.Principal;
using System.IO.Pipes;

using Chimera.Display;
using Chimera.Input;

using Chimera.Common;
using Chimera.Common.BufferExtensions;
using Chimera.Common.PathExtensions;
using Chimera.Common.StringExtensions;

using Chimera.Client.Common;

using Chimera.Emulation.Common;
using Chimera.Emulation.Common.Waterbox;

using Chimera.Client.GUI.ToolExtensions;
using Chimera.Client.GUI.CoreExtensions;
using Chimera.Client.GUI.CustomControls;
using Chimera.Common.CollectionExtensions;
using Chimera.WinForms.Controls;

namespace Chimera.Client.GUI
{
	public partial class MainForm : FormBase, IDialogParent,
		IMainFormForApi, IMainFormForTools
	{
		private readonly ToolStripMenuItemEx DOSSubMenu = new() { Text = "&DOS" };

		private readonly ToolStripMenuItemEx RealTimeCounterMenuItem = new() { Enabled = false, Text = "00:00.0" };

		private readonly StatusLabelEx StatusBarMuteIndicator = new();

		private void MainForm_Load(object sender, EventArgs e)
		{
			UpdateWindowTitle();

			SystemSubMenu.DropDownItems.InsertBefore(LoadedCoreNameMenuItem, insert: RealTimeCounterMenuItem);

			{
				for (int i = 1; i <= EmuClientApi.WINDOW_SCALE_MAX; i++)
				{
					// inferred mnemonics assume WINDOW_SCALE_MAX is 10 though...
					long quotient = Math.DivRem(i, 10, out long remainder);
					var temp = new ToolStripMenuItemEx
					{
						Tag = i,
						Text = $"{(quotient is not 0L ? quotient.ToString() : string.Empty)}&{remainder}x",
					};
					temp.Click += this.WindowSize_Click;
					WindowSizeSubMenu.DropDownItems.Insert(i - 1, temp);
				}
			}


			// Hide Status bar icons and general StatusBar prep
			MainStatusBar.Padding = new Padding(MainStatusBar.Padding.Left, MainStatusBar.Padding.Top, MainStatusBar.Padding.Left, MainStatusBar.Padding.Bottom); // Workaround to remove extra padding on right
			PlayRecordStatusButton.Visible = false;


			AVStatusLabel.Visible = false;
			SetPauseStatusBarIcon();
			Tools.UpdateFreezeRelatedTools(null, new(null));
			RebootStatusBarIcon.Visible = false;
			_statusBarDiskLightOnImage = Properties.Resources.LightOn;
			_statusBarDiskLightOffImage = Properties.Resources.LightOff;
			_linkCableOn = Properties.Resources.Connect16X16;
			_linkCableOff = Properties.Resources.NoConnect16X16;
			UpdateCoreStatusBarButton();
			HandleToggleLightAndLink();
			SetStatusBar();
			StatusBarMuteIndicator.Click += (_, _) => ToggleSound();
			MainStatusBar.Items.InsertBefore(KeyPriorityStatusLabel, insert: StatusBarMuteIndicator);
			UpdateStatusBarMuteIndicator();

			if (OSTailoredCode.IsUnixHost)
			{
				// workaround for https://github.com/mono/mono/issues/12644
				MainFormContextMenu.Items.Insert(0, new ToolStripMenuItemEx { Text = "(Dismiss Menu)" }); // don't even need to attach any behaviour, since clicking anything will dismiss the menu first
				MainFormContextMenu.Items.Insert(1, new ToolStripSeparatorEx());
			}

			OfferTheCoreManagerIfNothingIsInstalled();
		}

		/// <summary>
		/// A Chimera with no core cannot open anything, and it ships with none. So
		/// it says so once, and offers to do something about it. Not the manager
		/// itself: opening a window somebody did not ask for, over an application
		/// they have not seen yet, is a worse greeting than a sentence explaining
		/// why the menus will not help them.
		///
		/// Never when there is nobody to answer. HEADLESS is the case that matters:
		/// a witness bootstraps its config by running the frontend with no rom, no
		/// core and no project, which is precisely the shape this would otherwise
		/// interrupt - and a modal in a headless run does not get dismissed, it
		/// gets timed out. (quickernes' gate, first run after this window was
		/// added: "no meta produced".)
		///
		/// Nor when a core package, a rom or a project was named on the command
		/// line: whoever did that has already said what they want.
		/// </summary>
		private void OfferTheCoreManagerIfNothingIsInstalled()
		{
			if (HeadlessMode.Enabled) return;
			if (_discoveredCorePackages.Count is not 0) return;
			if (_argParser.cmdCorePackage is not null || _argParser.cmdRom is not null || _argParser.cmdProject is not null) return;
			if (CoreRoster.Read().Count is 0) return; // nothing to offer; a window saying so would only be rude

			// after the main window is up, so the dialog has something to sit over
			BeginInvoke((Action) (() =>
			{
				using CoreManagerPrompt prompt = new();
				if (this.ShowDialogWithTempMute(prompt).IsOk()) ShowCoreManager();
			}));
		}

		static MainForm()
		{
			// If this isn't here, then our assembly resolving hacks wont work due to the check for MainForm.INTERIM
			// its.. weird. don't ask.
		}

		public CoreComm CreateCoreComm()
		{
			var prefs = CoreComm.CorePreferencesFlags.None;
			if (Config.SkipWaterboxIntegrityChecks)
				prefs = CoreComm.CorePreferencesFlags.WaterboxMemoryConsistencyCheck;

			// can't pass self as IDialogParent :(
			return new CoreComm(
				message => this.ModalMessageBox(message, "Warning", EMsgBoxIcon.Warning),
				AddOnScreenMessage,
				prefs,
				new OpenGLProvider());
		}

		private void SetImages()
		{
			CloseRomMenuItem.Image = Properties.Resources.Close;
			EncodeVideoMenuItem.Image = Properties.Resources.Avi;
			ScreenshotMenuItem.Image = Properties.Resources.Camera;
			PauseMenuItem.Image = Properties.Resources.Pause;
			RebootCoreMenuItem.Image = Properties.Resources.Reboot;
			SwitchToFullscreenMenuItem.Image = Properties.Resources.Fullscreen;
			ControllersMenuItem.Image = Properties.Resources.GameController;
			HotkeysMenuItem.Image = Properties.Resources.HotKeys;
			DisplayConfigMenuItem.Image = Properties.Resources.TvIcon;
			SoundMenuItem.Image = Properties.Resources.Audio;
			PathsMenuItem.Image = Properties.Resources.CopyFolder;
			MessagesMenuItem.Image = Properties.Resources.MessageConfig;
			AutofireMenuItem.Image = Properties.Resources.Lightning;
			SaveConfigMenuItem.Image = Properties.Resources.Save;
			LoadConfigMenuItem.Image = Properties.Resources.LoadConfig;
			(RamWatchMenuItem.Image, /*RamWatchMenuItem.Text*/_) = ToolManager.IconAndNameCache[typeof(RamWatch)]
				= (/*RamWatch.ToolIcon.ToBitmap()*/Properties.Resources.Watch, "RAM Watch");
			(RamSearchMenuItem.Image, /*RamSearchMenuItem.Text*/_) = ToolManager.IconAndNameCache[typeof(RamSearch)]
				= (/*RamSearch.ToolIcon.ToBitmap()*/Properties.Resources.Search, "RAM Search");
			(LuaConsoleMenuItem.Image, /*LuaConsoleMenuItem.Text*/_) = ToolManager.IconAndNameCache[typeof(LuaConsole)]
				= (/*LuaConsole.ToolIcon.ToBitmap()*/Properties.Resources.TextDoc, "Lua Console");
			ToolManager.IconAndNameCache[typeof(TAStudio)]
				= (/*TAStudio.ToolIcon.ToBitmap()*/Properties.Resources.TAStudio, "TAStudio");
			(HexEditorMenuItem.Image, /*HexEditorMenuItem.Text*/_) = ToolManager.IconAndNameCache[typeof(HexEditor)]
				= (/*HexEditor.ToolIcon.ToBitmap()*/Properties.Resources.Poke, "Hex Editor");
			ToolManager.IconAndNameCache[typeof(GenericDebugger)]
				= (/*GenericDebugger.ToolIcon.ToBitmap()*/Properties.Resources.Bug, "Debugger");
			OnlineHelpMenuItem.Image = Properties.Resources.Help;
			AboutMenuItem.Image = Properties.Resources.ChimeraSmall;
			PlayRecordStatusButton.Image = Properties.Resources.Blank;
			PauseStatusButton.Image = Properties.Resources.Blank;
			RebootStatusBarIcon.Image = Properties.Resources.Reboot;
			AVStatusLabel.Image = Properties.Resources.Blank;
			LedLightStatusLabel.Image = Properties.Resources.LightOff;
			KeyPriorityStatusLabel.Image = Properties.Resources.Both;
			CoreNameStatusBarButton.Image = Properties.Resources.ChimeraSmall;
			LinkConnectStatusBarButton.Image = Properties.Resources.Connect16X16;
			RestartMovieContextMenuItem.Image = Properties.Resources.Restart;
			StopMovieContextMenuItem.Image = Properties.Resources.Stop;
			StopNoSaveContextMenuItem.Image = Properties.Resources.Stop;
			SaveMovieContextMenuItem.Image = Properties.Resources.SaveAs;
			SaveMovieAsContextMenuItem.Image = Properties.Resources.SaveAs;
			toolStripMenuItem6.Image = Properties.Resources.GameController;
			toolStripMenuItem7.Image = Properties.Resources.HotKeys;
			toolStripMenuItem8.Image = Properties.Resources.TvIcon;
			toolStripMenuItem9.Image = Properties.Resources.Audio;
			toolStripMenuItem10.Image = Properties.Resources.CopyFolder;
			toolStripMenuItem12.Image = Properties.Resources.MessageConfig;
			toolStripMenuItem13.Image = Properties.Resources.Lightning;
			toolStripMenuItem66.Image = Properties.Resources.Save;
			toolStripMenuItem67.Image = Properties.Resources.LoadConfig;
			ScreenshotContextMenuItem.Image = Properties.Resources.Camera;
			CloseRomContextMenuItem.Image = Properties.Resources.Close;
		}

		public MainForm(
			ParsedCLIFlags cliFlags,
			IGL gl,
			Func<string> getConfigPath,
			Func<Config> getGlobalConfig,
			Action<Sound> updateGlobalSound,
			string[] args,
			out IMovieSession movieSession,
			out bool exitEarly)
		{
			movieSession = null;
			exitEarly = false;

			_getGlobalConfig = getGlobalConfig;

			if (Config.SingleInstanceMode)
			{
				if (SingleInstanceInit(args))
				{
					exitEarly = true;
					return;
				}
			}

			_argParser = cliFlags;
			_getConfigPath = getConfigPath;
			GL = gl;
			_updateGlobalSound = updateGlobalSound;

			InputManager = new InputManager
			{
				GetMainFormMouseInfo = () =>
				{
					var b = Control.MouseButtons;
					return (
						Control.MousePosition,
						MouseWheelTracker,
						(b & MouseButtons.Left) != 0,
						(b & MouseButtons.Middle) != 0,
						(b & MouseButtons.Right) != 0,
						(b & MouseButtons.XButton1) != 0,
						(b & MouseButtons.XButton2) != 0
					);
				},
			};
			movieSession = MovieSession = new MovieSession(
				Config.Movies,
				Config.PathEntries.MovieBackupsAbsolutePath(),
				this,
				PauseEmulator,
				SetMainformMovieInfo,
				(_, _) => UpdateWindowTitle());

			void MainForm_MouseClick(object sender, MouseEventArgs e)
			{
				AutohideCursor(hide: false);
				if (Config.ShowContextMenu && e.Button == MouseButtons.Right)
				{
					// suppress the context menu if right click has a binding
					// (unless shift is being pressed, similar to double click fullscreening)
					var allowSuppress = ModifierKeys != Keys.Shift;
					if (allowSuppress && InputManager.ActiveController.HasBinding("WMouse R"))
					{
						return;
					}

					MainFormContextMenu.Show(PointToScreen(new Point(e.X, e.Y + MainformMenu.Height)));
				}
			}
			void MainForm_MouseMove(object sender, MouseEventArgs e) => AutohideCursor(hide: false, alwaysUpdate: false);
			void MainForm_MouseWheel(object sender, MouseEventArgs e) => MouseWheelTracker += e.Delta;
			MouseClick += MainForm_MouseClick;
			MouseMove += MainForm_MouseMove;

			InitializeComponent();
			Icon = Properties.Resources.Logo;
			SetImages();
#if !DEBUG
#endif
			Game = GameInfo.NullInstance;
			_throttle = new Throttle();
			Emulator = new NullEmulator();

			UpdateKeyPriorityIcon();

			// TODO GL - a lot of disorganized wiring-up here
			_presentationPanel = new(
				Config,
				GL,
				ToggleFullscreen,
				MainForm_MouseClick,
				MainForm_MouseMove,
				MainForm_MouseWheel);

			DisplayManager = new(Config, Emulator, InputManager, MovieSession, GL, _presentationPanel, () => DisableSecondaryThrottling);
			Controls.InsertBefore(MainformMenu, insert: _presentationPanel.Control); // must be first for ??? WinForms reasons

			// set up networking before ApiManager (in ToolManager)
			byte[] NetworkingTakeScreenshot()
				=> (byte[]) new ImageConverter().ConvertTo(MakeScreenshotImage().ToSysdrawingBitmap(), typeof(byte[]));
			NetworkingHelpers = (
				_argParser.HTTPAddresses is var (httpGetURL, httpPostURL)
					? new HttpCommunication(NetworkingTakeScreenshot, httpGetURL, httpPostURL)
					: null,
				new MemoryMappedFiles(NetworkingTakeScreenshot, _argParser.MMFFilename),
				_argParser.SocketAddress is var (socketIP, socketPort)
					? new SocketServer(NetworkingTakeScreenshot, _argParser.SocketProtocol, socketIP, socketPort)
					: null
			);

			Tools = new ToolManager(this, Config, DisplayManager, InputManager, Emulator, MovieSession, Game);

			// TODO GL - move these event handlers somewhere less obnoxious line in the On* overrides
			Load += (o, e) =>
			{
				AllowDrop = true;
				DragEnter += FormDragEnter;
				DragDrop += FormDragDrop;
			};

			Closing += CheckMayCloseAndCleanup;

			ResizeBegin += (o, e) =>
			{
				_inResizeLoop = true;

				if (!OSTailoredCode.IsUnixHost)
				{
					Sound?.StopSound();
				}
			};

			Resize += (_, _) => UpdateWindowTitle();

			ResizeEnd += (o, e) =>
			{
				_inResizeLoop = false;
				UpdateWindowTitle();

				if (!OSTailoredCode.IsUnixHost)
				{
					Sound?.StartSound();
				}
			};

			_presentationPanel.Control.Move += (_, _) =>
			{
				if (Config.CaptureMouse)
				{
					CaptureMouse(false);
					CaptureMouse(true);
				}
			};

			_presentationPanel.Control.Resize += (_, _) =>
			{
				if (Config.CaptureMouse)
				{
					CaptureMouse(false);
					CaptureMouse(true);
				}
			};

			if (!Config.GCAdapterSupportEnabled)
			{
				// annoyingly this isn't an SDL hint in SDL2, only in SDL3, have to use an environment variables to signal this
				Environment.SetEnvironmentVariable("SDL_HIDAPI_DISABLE_LIBUSB", "1");
			}

			Input.Instance = new Input(
				Handle,
				() => Config,
				() => ActiveForm switch
				{
					null => Config.AcceptBackgroundInput // none of our forms are focused, check the background input config
						? Config.AcceptBackgroundInputControllerOnly
							? AllowInput.OnlyController
							: AllowInput.All
						: AllowInput.None,
					FormBase { BlocksInputWhenFocused: false, MenuIsOpen: false } => AllowInput.All,
					ControllerConfig => AllowInput.All,
					HotkeyConfig => AllowInput.All,
					LuaWinform { BlocksInputWhenFocused: false } => AllowInput.All,
					_ => Config.AcceptBackgroundInput ? AllowInput.OnlyController : AllowInput.None,
				}
			);
			InitControls();

			var savedOutputMethod = Config.SoundOutputMethod;
			if (savedOutputMethod is ESoundOutputMethod.Dummy) Config.SoundOutputMethod = ESoundOutputMethod.OpenAL;
			try
			{
				Sound = new Sound(Config, () => Emulator.VsyncRate());
			}
			catch
			{
				if (savedOutputMethod is not ESoundOutputMethod.Dummy)
				{
					ShowMessageBox(
						owner: null,
						text: "Couldn't initialize sound device! Try changing the output method in Sound config.",
						caption: "Initialization Error",
						EMsgBoxIcon.Error);
				}
				Config.SoundOutputMethod = ESoundOutputMethod.Dummy;
				Sound = new Sound(Config, () => Emulator.VsyncRate());
			}

			Sound.StartSound();
			InputManager.SyncControls(Emulator, MovieSession, Config);
			CheatList = new CheatCollection(this);
			CheatList.Changed += Tools.UpdateFreezeRelatedTools;
			RewireSound();

			if (Config.SaveWindowPosition)
			{
				if (Config.MainWindowPosition is Point position)
				{
					Location = position;
				}

				if (Config.MainWindowSize is Size size && !Config.ResizeWithFramebuffer)
				{
					Size = size;
				}

				if (Config.MainWindowMaximized)
				{
					WindowState = FormWindowState.Maximized;
				}
			}

			if (Config.MainFormStayOnTop) TopMost = true;

			// What is in Cores/ is only LISTED here - opening a core is something you do,
			// like opening a rom (File > Open Core). The scan runs in the constructor
			// because the commandline load a few lines below needs the list to exist.
			// The compile cache (docs: a core's compiled objects, kept between
			// sessions under the Compile Cache path). A precompile session is this
			// same frontend as a child process; it installs no hook of its own.
			// Not a configurable path: it is regenerable per-user data, so it lives
			// where the core store and the project caches do rather than wherever
			// somebody's config last said (see CacheStore).
			CacheStore.AdoptLegacy();
			WaterboxCore.CoreCacheRoot = CacheStore.CompiledCode;
			if (_argParser.cmdPrecompile is { } precompileSpec)
			{
				var parts = precompileSpec.Split('/');
				var preIndex = -1;
				var preCount = 0;
				if (parts.Length >= 2) { int.TryParse(parts[0], out preIndex); int.TryParse(parts[1], out preCount); }
				if (preIndex < 0 || preCount < 1 || preIndex >= preCount)
				{
					Console.Error.WriteLine($"bad --precompile {precompileSpec} (want INDEX/COUNT[/game])");
					Environment.Exit(2);
				}
				WaterboxCore.PrecompileRequest = new(preIndex, preCount, FirmwareToo: !parts.Skip(2).Contains("game"));
			}
			ScanForCorePackages();
			// the menus built at construction time predate the packages

			Console.WriteLine($"Chimera {VersionInfo.GetEmuVersion()}");

			if (_argParser.cmdCorePackage != null && !LoadCorePackage(_argParser.cmdCorePackage))
			{
				ShowMessageBox(owner: null, $"Failed to load core package {_argParser.cmdCorePackage} specified on commandline");
			}

			if (_argParser.cmdRom != null)
			{
				// A rom needs a core, and choosing one is never implicit - not on the
				// commandline either. --core says which; nothing else does.
				if (CoreRegistry.Instance.AllFactories.Count is 0)
				{
					ShowMessageBox(
						owner: null,
						$"No core is loaded, so {Path.GetFileName(_argParser.cmdRom)} cannot run."
							+ "\n\nName one with --core=<package>, or open one with File > Open Core...",
						"No core loaded");
				}
				else
				{
					// Commandline should always override auto-load
					var ioa = OpenAdvancedSerializer.ParseWithLegacy(_argParser.cmdRom);
					_ = LoadRom(ioa.SimplePath, new LoadRomArgs(ioa));
					if (Game.IsNullInstance())
					{
						ShowMessageBox(owner: null, $"Failed to load {_argParser.cmdRom} specified on commandline");
					}
					else if (WaterboxCore.PrecompileRequest is { } request)
					{
						// a precompile session: pump the core until it has compiled
						// its share, one progress line per change, then leave
						if (!Emulator.ServiceProvider.HasService<ICorePrecompile>())
						{
							Console.Error.WriteLine($"{Emulator.Attributes().CoreName} has no precompile session");
							Environment.Exit(3);
						}
						var pre = Emulator.AsPrecompile();
						// A redirected stdout is block-buffered, and a progress line
						// held until the process exits is not progress: whoever
						// started this session is watching for these.
						Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
						Console.Error.WriteLine($"precompile session {request.Index} of {request.Count}: cache {WaterboxCore.CoreCacheRoot}");
						uint lastDone = uint.MaxValue;
						while (!pre.PrecompileDone)
						{
							Emulator.FrameAdvance(InputManager.ControllerOutput, false, false);
							var (done, total) = pre.PrecompileProgress;
							if (done != lastDone) { Console.WriteLine($"Precompiled {done}/{total} modules"); lastDone = done; }
						}
						var (fdone, ftotal) = pre.PrecompileProgress;
						Console.WriteLine($"Precompiled {fdone}/{ftotal} modules");
						Console.WriteLine($"precompile session {request.Index} of {request.Count}: {pre.CacheStored} objects stored, {pre.CacheFetched} fetched");
						// leave the way a script does: the run loop closes the window
						// and the process exits with its code, nothing torn down early
						_exitRequestPending = true;
					}
				}
			}
			// no rom autoload: the GUI's entry point is a project (docs/project.md);
			// the start screen offers New/Open/recents once the window is up

			if (_argParser.cmdProject != null && !LoadProject(_argParser.cmdProject))
			{
				ShowMessageBox(owner: null, $"Failed to open project {_argParser.cmdProject} specified on commandline");
			}

			Config.VideoWriterAudioSyncEffective = _argParser.audiosync ?? Config.VideoWriterAudioSync;
			_autoDumpLength = _argParser._autoDumpLength;
			if (_argParser.cmdMovie != null)
			{
				if (Game.IsNullInstance())
				{
					OpenRom();
				}

				// If user picked a game, then do the commandline logic
				if (!Game.IsNullInstance())
				{
					var movie = MovieSession.Get(_argParser.cmdMovie, true);
					if (movie != null)
					{
						MovieSession.ReadOnly = true;

						// if user is dumping and didn't supply dump length, make it as long as the loaded movie
						if (_autoDumpLength == 0)
						{
							_autoDumpLength = movie.InputLogLength;
						}

						StartNewMovie(movie, false);
						Config.RecentMovies.Add(_argParser.cmdMovie);

						}
					else
					{
						ShowMessageBox(owner: null, $"Failed to load movie {_argParser.cmdMovie} specified on commandline");
					}
				}
			}
			else if (Config.RecentMovies.AutoLoad && !Config.RecentMovies.Empty)
			{
				if (Game.IsNullInstance())
				{
					OpenRom();
				}

				// If user picked a game, then do the autoload logic
				if (!Game.IsNullInstance())
				{
					if (File.Exists(Config.RecentMovies.MostRecent))
					{
						StartNewMovie(MovieSession.Get(Config.RecentMovies.MostRecent, true), false);
					}
					else
					{
						Config.RecentMovies.HandleLoadError(this, Config.RecentMovies.MostRecent);
					}
				}
			}

			if (_argParser.startFullscreen || Config.StartFullscreen)
			{
				_needsFullscreenOnLoad = true;
			}

			if (_argParser.UserdataUnparsedPairs is {} pairs) foreach (var (k, v) in pairs)
			{
				MovieSession.UserBag[k] = v switch
				{
					"true" => true,
					"false" => false,
					_ when int.TryParse(v, out var i) => i,
					_ when double.TryParse(v, out var d) => d,
					_ => v,
				};
			}

			Shown += (_, _) =>
			{
				//start Lua Console if requested in the command line arguments
				if (_argParser.luaConsole)
				{
					OpenLuaConsole();
				}
				//load Lua Script if requested in the command line arguments
				if (_argParser.luaScript != null)
				{
					Tools.LuaConsole.LoadFromCommandLine(_argParser.luaScript.MakeAbsolute());
				}
				// nothing is asked at startup: the window comes up empty and the
				// File menu is the way in (New Project, Open Project, and the
				// recents submenu). Being greeted by a dialog before any work
				// exists is friction, not a front door.
			};

			SetStatusBar();

			if (Config.StartPaused)
			{
				PauseEmulator();
			}

			// start dumping, if appropriate
			if (_argParser.cmdDumpType != null && _argParser.cmdDumpName != null)
			{
				RecordAv(_argParser.cmdDumpType, _argParser.cmdDumpName);
			}

			SetMainformMovieInfo();

			SynchChrome();

			_presentationPanel.Control.Paint += (o, e) =>
			{
				// I would like to trigger a repaint here, but this isn't done yet
			};

			if (!Config.SkipOutdatedOsCheck && OSTailoredCode.HostWindowsVersion is not null)
			{
				var (winVersion, win10PlusVersion) = OSTailoredCode.HostWindowsVersion.Value;
				var message = winVersion switch
				{
					OSTailoredCode.WindowsVersion._11 when win10PlusVersion! < new Version(10, 0, 22621) => $"Quick reminder: Your copy of Windows 11 (build {win10PlusVersion.Build}) is no longer supported by Microsoft.\nChimera will probably continue working, but please update to 24H2 for increased security.",
					OSTailoredCode.WindowsVersion._11 => null,
					OSTailoredCode.WindowsVersion._10 when win10PlusVersion! < new Version(10, 0, 19045) => $"Quick reminder: Your copy of Windows 10 (build {win10PlusVersion.Build}) is no longer supported by Microsoft.\nChimera will probably continue working, but please update to 22H2 for increased security.",
					OSTailoredCode.WindowsVersion._10 => null,
					_ => $"Quick reminder: Windows {winVersion.ToString().RemovePrefix('_').Replace('_', '.')} is no longer supported by Microsoft.\nChimera will probably continue working, but please get a new operating system for increased security (either Windows 10+ or a GNU+Linux distro).",
				};
				if (message is not null)
				{
#if DEBUG
				Console.WriteLine(message);
#else
				Load += (_, _) => Config.SkipOutdatedOsCheck = this.ShowMessageBox2($"{message}\n\nSkip this reminder from now on?");
#endif
				}
			}

			Input.Instance.ControlInputFocus(this, HostInputType.Mouse, true);
		}

		private void CheckMayCloseAndCleanup(object/*?*/ closingSender, CancelEventArgs closingArgs)
		{
			if (_currAviWriter is not null)
			{
				if (!this.ModalMessageBox2(
					caption: "Really quit?",
					icon: EMsgBoxIcon.Question,
					text: "You are currently recording A/V.\nChoose \"Yes\" to finalise it and quit Chimera.\nChoose \"No\" to cancel shutdown and continue recording."))
				{
					closingArgs.Cancel = true;
					return;
				}
				// StopAv would be handled in CloseGame, but since we've asked the user about it, best to handle it now.
				StopAv();
			}

			TryAgainResult configSaveResult = this.DoWithTryAgainBox(() => SaveConfig(), "Failed to save config file.");
			if (configSaveResult == TryAgainResult.Canceled)
			{
				closingArgs.Cancel = true;
				return;
			}

			if (!CloseGame())
			{
				closingArgs.Cancel = true;
				return;
			}
			Tools.Close();

			Input.Instance.ControlInputFocus(this, HostInputType.Mouse, false);
		}


		public override bool BlocksInputWhenFocused { get; } = false;

		/// <summary>
		/// Windows does tool stip menu focus things when Alt is released, not pressed.
		/// However, if an alt combination is pressed then those things happen at that time instead.
		/// So we need to know if a key combination was used, so we can skip the alt release logic.
		/// </summary>
		private bool _skipNextAltRelease = true;

		public int ProgramRunLoop()
		{
			// needs to be done late, after the log console snaps on top
			// fullscreen should snap on top even harder!
			if (_needsFullscreenOnLoad)
			{
				_needsFullscreenOnLoad = false;
				ToggleFullscreen();
			}

			CaptureMouse(Config.CaptureMouse);

			// incantation required to get the program reliably on top of the console window
			// we might want it in ToggleFullscreen later, but here, it needs to happen regardless
			BringToFront();
			Activate();
			BringToFront();

			InitializeFpsData();

			// Headless runs have no host input, no window to draw and nobody to serve
			// messages to, but the loop still did all three per emulated frame - and
			// each one is an X11 round trip, which blocks. That capped a headless
			// replay at a few hundred frames a second no matter how fast the core was.
			// Service them on a wall-clock interval instead, so the process still
			// reacts to a close request promptly while emulation runs free.
			var headless = HeadlessMode.Enabled;
			var lastHostServiceTime = DateTime.UtcNow;

			for (; ; )
			{
				// A seek is the same situation with a window attached. Its frames go
				// by faster than anybody can look, and this loop was spending as much
				// on each of them as the core did - the piano roll refreshed, the frame
				// presented, the message pump run, per frame, on a core whose frame is
				// three milliseconds. Measured with CHIMERA_LOOP_TRACE: a Game Boy seek
				// at 5.9 ms a frame of which 3.0 was the machine. So a seek serves the
				// host on the headless cadence too: sixty times a second the window is
				// live, shows where the seek has got to and takes a click to stop it;
				// the frames in between are emulated and nothing else. The destination
				// frame is always drawn and always shown (StepRunLoop_Core).
				bool serviceHost = true;
				if (headless || IsSeeking)
				{
					var now = DateTime.UtcNow;
					serviceHost = (now - lastHostServiceTime).TotalMilliseconds >= 16.0;
					if (serviceHost) lastHostServiceTime = now;
				}
				_seekQuiet = IsSeeking && !serviceHost;
				if (serviceHost) LoopTrace.Served();

				long loopStarted = LoopTrace.Enabled ? Stopwatch.GetTimestamp() : 0;
				if (serviceHost) Input.Instance.Update();

				// handle events and dispatch as a hotkey action, or a hotkey button, or an input button
				// ...but prepare haptics first, those get read in ProcessInput
				var finalHostController = InputManager.ControllerInputCoalescer;
				InputManager.ActiveController.PrepareHapticsForHost(finalHostController);
				Input.Instance.Adapter.SetHaptics(finalHostController.GetHapticsSnapshot());

				InputManager.ProcessInput(Input.Instance, CheckHotkey, Config, (ie, handled) =>
				{
					// Alt key for menu items.
					bool isAltCombination = ie.EventType is InputEventType.Press && (ie.LogicalButton.Modifiers & LogicalButton.MASK_ALT) is not 0U;
					if (isAltCombination || ie.LogicalButton.Button == Input.BUTTON_FORM_CHANGED)
					{
						// Windows will not focus the menu if any other key was pressed while Alt is held. Regardless of whether that key did anything.
						// And the active form will not be us if the user presed alt+tab.
						_skipNextAltRelease = true;
					}

					if (handled || ActiveForm is not FormBase afb) return;

					if (isAltCombination)
					{
						if (ie.LogicalButton.Button.Length == 1)
						{
							var c = ie.LogicalButton.Button.ToLowerInvariant()[0];
							afb.SendAltCombination(c);
						}
						else if (ie.LogicalButton.Button == "Space")
						{
							afb.SendAltCombination(' ');
						}
					}
					else if (ie.EventType is InputEventType.Press && ie.LogicalButton.Button == "Alt")
					{
						// We will only do the alt release if the alt press itself was not already handled.
						_skipNextAltRelease = false;
					}
					else if (ie.EventType is InputEventType.Release
						&& !afb.BlocksInputWhenFocused
						&& ie.LogicalButton.Button == "Alt"
						&& !_skipNextAltRelease)
					{
						afb.FocusToolStipMenu();
					}

					// same as right-click
					if (ie.ToString() == "Press:Apps" && Config.ShowContextMenu && ContainsFocus)
					{
						MainFormContextMenu.Show(PointToScreen(new(0, MainformMenu.Height)));
					}
				});

				// translate mouse coordinates
				// NOTE: these must go together, because in the case of screen rotation, X and Y are transformed together
				{
					var p = DisplayManager.UntransformPoint(new Point(
						finalHostController.AxisValue("WMouse X"),
						finalHostController.AxisValue("WMouse Y")));
					var x = p.X / (float)_currentVideoProvider.BufferWidth;
					var y = p.Y / (float)_currentVideoProvider.BufferHeight;
					finalHostController.AcceptNewAxis("WMouse X", (int)((x * 20000) - 10000));
					finalHostController.AcceptNewAxis("WMouse Y", (int)((y * 20000) - 10000));
				}

				InputManager.RunControllerChain(Config);

				// emu.yield()'ing scripts
				if (Tools.Has<LuaConsole>())
				{
					Tools.LuaConsole.ResumeScripts(false);
				}
				LoopTrace.Add(LoopTrace.Top, loopStarted);
				long phaseStarted = LoopTrace.Enabled ? Stopwatch.GetTimestamp() : 0;
				StepRunLoop_Core();
				LoopTrace.Add(LoopTrace.Core, phaseStarted);
				phaseStarted = LoopTrace.Enabled ? Stopwatch.GetTimestamp() : 0;
				if (serviceHost) Render();
				LoopTrace.Add(LoopTrace.Render, phaseStarted);
				phaseStarted = LoopTrace.Enabled ? Stopwatch.GetTimestamp() : 0;
				StepRunLoop_Throttle();
				LoopTrace.Add(LoopTrace.Throttle, phaseStarted);

				phaseStarted = LoopTrace.Enabled ? Stopwatch.GetTimestamp() : 0;
				if (serviceHost) CheckMessages();
				LoopTrace.Add(LoopTrace.Messages, phaseStarted);

				if (_exitRequestPending)
				{
					_exitRequestPending = false;
					Close();
					/* An unattended close that is REFUSED would otherwise spin
					 * here forever: the request is spent, the window is still
					 * up, and nothing will ask again. Whatever cancelled it
					 * wanted an answer nobody is there to give, so name it and
					 * stop rather than hang - the same bargain HeadlessMode
					 * makes, and the same exit code, because it is the same
					 * situation with a window attached. */
					if (ShutdownIsUnattended && !IsDisposed && !_windowClosedAndSafeToExitProcess)
					{
						Console.Error.WriteLine("[chimera] an unattended shutdown was refused by a window "
							+ "that wanted an answer; leaving anyway");
						_exitCode = HeadlessMode.EXIT_CODE_DIALOG;
						break;
					}
				}

				if (IsDisposed || _windowClosedAndSafeToExitProcess)
				{
					break;
				}
			}

			Shutdown();
			return _exitCode;
		}

		/// <summary>
		/// Clean up any resources being used.
		/// </summary>
		/// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
		protected override void Dispose(bool disposing)
		{
			// NOTE: this gets called twice sometimes. once by using() in Program.cs and once from winforms internals when the form is closed...
			if (disposing)
			{
				// Managed, so only on a real Dispose: from the finalizer (an
				// Environment.Exit, which is how a headless refusal ends) the GL
				// control is already gone, and making its context current again
				// threw ObjectDisposedException out of the finalizer thread - a
				// "FATAL UNHANDLED EXCEPTION" and exit code 255 in place of the
				// refusal's own.
				DisplayManager?.Dispose();
				DisplayManager = null;
				components?.Dispose();
				_presentationPanel?.Dispose();
				SingleInstanceDispose();
			}

			if (OSTailoredCode.IsUnixHost)
			{
				if (_x11Display != IntPtr.Zero)
				{
					for (var i = 0; i < 4; i++)
					{
						if (_pointerBarriers[i] != IntPtr.Zero)
						{
							XfixesImports.XFixesDestroyPointerBarrier(_x11Display, _pointerBarriers[i]);
							_pointerBarriers[i] = IntPtr.Zero;
						}
					}

					_ = XlibImports.XCloseDisplay(_x11Display);
					_x11Display = IntPtr.Zero;
				}
			}

			base.Dispose(disposing);
		}

		private bool _emulatorPaused;
		public bool EmulatorPaused
		{
			get => _emulatorPaused;

			private set
			{
				_didMenuPause = false; // overwritten where relevant
				if (_emulatorPaused == value) return;
				if (_emulatorPaused && !value) // Unpausing
				{
					InitializeFpsData();
				}

				if (value != _emulatorPaused) Tools.OnPauseToggle(value);
				_emulatorPaused = value;
			}
		}

		public bool BlockFrameAdvance { get; set; }

		public string CurrentlyOpenRom { get; private set; } // todo - delete me and use only args instead
		public LoadRomArgs CurrentlyOpenRomArgs { get; private set; }
		public bool PauseAvi { get; set; }
		public bool PressFrameAdvance { get; set; }
		public bool FrameInch { get; set; }
		public bool HoldFrameAdvance { get; set; } // necessary for tastudio > button
		public bool PressRewind { get; set; } // necessary for tastudio < button

		private long MouseWheelTracker;

		private int? _pauseOnFrame;
		public int? PauseOnFrame // If set, upon completion of this frame, the client wil pause
		{
			get => _pauseOnFrame;

			set
			{
				_pauseOnFrame = value;
				SetPauseStatusBarIcon();
			}
		}

		public bool IsSeeking => PauseOnFrame.HasValue;
		private bool IsTurboSeeking => PauseOnFrame.HasValue && Config.TurboSeek;

		/// <summary>
		/// A frame of a seek that nobody is going to see: the run loop is between
		/// two services of the host (ProgramRunLoop), so the frame is not drawn, not
		/// presented and the tools get their fast update only - exactly what a turbo
		/// seek does to every frame on its way. Never true for the seek's last frame.
		/// </summary>
		private bool _seekQuiet;

		// An encode runs as fast as the machine will go, and gets turbo's cheap tool
		// updates for the same reason a turbo seek does: nobody is watching it.
		public bool IsTurboing => InputManager.ClientControls["Turbo"] || IsTurboSeeking || IsEncodingVideo;
		public bool IsFastForwarding => InputManager.ClientControls["Fast Forward"] || IsTurboing;
		public bool IsRewinding { get; private set; }

		/// <summary>
		/// Used to disable secondary throttling (e.g. vsync, audio) for unthrottled modes or when the primary (clock) throttle is taking over (e.g. during fast forward/rewind).
		/// </summary>
		public static bool DisableSecondaryThrottling { get; set; }

		public void AddOnScreenMessage(string message, [LiteralExpected] int? duration = null)
		{
#pragma warning disable CS0618 // this is the sanctioned call-site
			OSD.AddMessage(message, duration);
#pragma warning restore CS0618
			if (!this.SafeScreenReaderAnnounce(message)) Util.DebugWriteLine($"{nameof(AddOnScreenMessage)}: {nameof(AccessibleObject.RaiseAutomationNotification)} failed");
		}

		public void ClearHolds()
		{
			InputManager.StickyHoldController.ClearStickies();
			InputManager.StickyAutofireController.ClearStickies();
		}

		public void FlagNeedsReboot()
		{
			RebootStatusBarIcon.Visible = true;
			AddOnScreenMessage("Core reboot needed for this setting");
		}

		/// <remarks>don't use this, use <see cref="Emulator"/></remarks>
		private IEmulator _emulator;

		public IEmulator Emulator
		{
			get => _emulator;

			private set
			{
				_emulator = value;
				_currentVideoProvider = value.AsVideoProviderOrDefault();
				_currentSoundProvider = value.AsSoundProviderOrDefault();
			}
		}

		public event EventHandler RomLoaded;

		public ShowFutureCallback/*?*/ PreFutureFrameCallback { get; set; }

		public int MaxFutureFrames { get; set; }

		private readonly InputManager InputManager;

		private IVideoProvider _currentVideoProvider = NullVideo.Instance;

		/// <summary>What an idle Chimera looks at you with while no project is open.</summary>
		private readonly IdleEyeVideo _idleEye = new();

		private ISoundProvider _currentSoundProvider = new NullSound(44100 / 60); // Reasonable default until we have a core instance

		/// <remarks>don't use this, use <see cref="Config"/></remarks>
		private readonly Func<Config> _getGlobalConfig;

		private new Config Config => _getGlobalConfig();

		public Action<string> LoadGlobalConfigFromFile { get; set; }

		private readonly Func<string> _getConfigPath;

		private readonly IGL GL;

		private readonly ToolManager Tools;

		private IControlMainform ToolControllingRewind => Tools.FirstOrNull<IControlMainform>(tool => tool.WantsToControlRewind);
		private IControlMainform ToolControllingReboot => Tools.FirstOrNull<IControlMainform>(tool => tool.WantsToControlReboot);
		private IControlMainform ToolControllingStopMovie => Tools.FirstOrNull<IControlMainform>(tool => tool.WantsToControlStopMovie);
		private IControlMainform ToolControllingRestartMovie => Tools.FirstOrNull<IControlMainform>(tool => tool.WantsToControlRestartMovie);
		private IControlMainform ToolControllingReadOnly => Tools.FirstOrNull<IControlMainform>(tool => tool.WantsToControlReadOnly);
		private IControlMainform ToolBypassingMovieEndAction => Tools.FirstOrNull<IControlMainform>(tool => tool.WantsToBypassMovieEndAction);

		private DisplayManager DisplayManager;

		private OSDManager OSD => DisplayManager.OSD;

		public IMovieSession MovieSession { get; }

		public GameInfo Game { get; private set; }

		/// <remarks>don't use this, use <see cref="Sound"/></remarks>
		private Sound _sound;

		private readonly Action<Sound> _updateGlobalSound;

		private Sound Sound
		{
			get => _sound;
			set => _updateGlobalSound(_sound = value);
		}

		public CheatCollection CheatList { get; }

		public (HttpCommunication HTTP, MemoryMappedFiles MMF, SocketServer Sockets) NetworkingHelpers { get; }

		protected override void OnActivated(EventArgs e)
		{
			base.OnActivated(e);

			if (Config.CaptureMouse)
			{
				CaptureMouse(false);
				CaptureMouse(true);
			}
		}

		protected override void OnDeactivate(EventArgs e)
		{
			if (Config.CaptureMouse)
			{
				CaptureMouse(false);
			}

			base.OnDeactivate(e);
		}

		public bool RebootCore()
		{
			if (ToolControllingReboot is { } tool)
			{
				tool.RebootCore();
				return true;
			}
			else
			{
				if (CurrentlyOpenRomArgs == null) return true;
				return LoadRom(
					CurrentlyOpenRomArgs.OpenAdvanced.SimplePath,
					CurrentlyOpenRomArgs with { ForcedSysID = Emulator.SystemId });
			}
		}

		public void PauseEmulator()
		{
			if (Emulator.IsNull()) return; // nothing is running to pause
			EmulatorPaused = true;
			SetPauseStatusBarIcon();
		}

		public void UnpauseEmulator()
		{
			if (Emulator.IsNull()) return;
			EmulatorPaused = false;
			SetPauseStatusBarIcon();
		}

		public void TogglePause()
		{
			if (Emulator.IsNull()) return;
			EmulatorPaused = !EmulatorPaused;
			SetPauseStatusBarIcon();
		}

		public void TakeScreenshotToClipboard()
		{
			using var bb = Config.ScreenshotCaptureOsd ? CaptureOSD() : MakeScreenshotImage();
			bb.ToSysdrawingBitmap().ToClipBoard();
			AddOnScreenMessage("Screenshot (raw) saved to clipboard.");
		}

		private void TakeScreenshotClientToClipboard()
		{
			using var bb = DisplayManager.RenderOffscreen(_currentVideoProvider, Config.ScreenshotCaptureOsd);
			bb.ToSysdrawingBitmap().ToClipBoard();
			AddOnScreenMessage("Screenshot (client) saved to clipboard.");
		}

		private string ScreenshotPrefix()
		{
			var screenPath = Config.PathEntries.ScreenshotAbsolutePathFor(Game.System);
			var name = Game.FilesystemSafeName();
			return Path.Combine(screenPath, name);
		}

		public void TakeScreenshot()
		{
			var basename = $"{ScreenshotPrefix()}.{DateTime.Now:yyyy-MM-dd HH.mm.ss}";

			var fnameBare = $"{basename}.png";
			var fname = $"{basename} (0).png";

			// if the (0) filename exists, do nothing. we'll bump up the number later
			// if the bare filename exists, move it to (0)
			// otherwise, no related filename exists, and we can proceed with the bare filename
			if (!File.Exists(fname))
			{
				if (File.Exists(fnameBare)) File.Move(fnameBare, fname);
				else fname = fnameBare;
			}

			for (var seq = 0; File.Exists(fname); seq++)
				fname = $"{basename} ({seq}).png";

			TakeScreenshot(fname);
		}

		public void TakeScreenshot(string path)
		{
			var fi = new FileInfo(path);
			fi.Directory?.Create();
			using (var bb = Config.ScreenshotCaptureOsd ? CaptureOSD() : MakeScreenshotImage())
			{
				using var img = bb.ToSysdrawingBitmap();
				if (".JPG".EqualsIgnoreCase(Path.GetExtension(path)))
				{
					img.Save(fi.FullName, ImageFormat.Jpeg);
				}
				else
				{
					img.Save(fi.FullName, ImageFormat.Png);
				}
			}

			AddOnScreenMessage($"{fi.Name} saved.");
		}

		public void FrameBufferResized(bool forceWindowResize = false)
		{
			if (WindowState is not FormWindowState.Normal)
			{
				// Wait until no longer maximized/minimized to get correct size/location values
				_framebufferResizedPending = true;
				return;
			}
			if (!Config.ResizeWithFramebuffer && !forceWindowResize)
			{
				return;
			}
			// run this entire thing exactly twice, since the first resize may adjust the menu stacking
			void DoPresentationPanelResize()
			{
				int zoom = Config.GetWindowScaleFor(Emulator.SystemId);
				var area = Screen.FromControl(this).WorkingArea;

				int borderWidth = Size.Width - _presentationPanel.Control.Size.Width;
				int borderHeight = Size.Height - _presentationPanel.Control.Size.Height;

				// start at target zoom and work way down until we find acceptable zoom
				Size lastComputedSize = new Size(1, 1);
				for (; zoom >= 1; zoom--)
				{
					lastComputedSize = DisplayManager.CalculateClientSize(_currentVideoProvider, zoom);
					if (lastComputedSize.Width + borderWidth < area.Width
						&& lastComputedSize.Height + borderHeight < area.Height)
					{
						break;
					}
				}

//				Util.DebugWriteLine($"For emulator framebuffer {new Size(_currentVideoProvider.BufferWidth, _currentVideoProvider.BufferHeight)}:");
//				Util.DebugWriteLine($"  For virtual size {new Size(_currentVideoProvider.VirtualWidth, _currentVideoProvider.VirtualHeight)}:");
//				Util.DebugWriteLine($"  Selecting display size {lastComputedSize}");

				// Change size
				Size = new Size(lastComputedSize.Width + borderWidth, lastComputedSize.Height + borderHeight);
				PerformLayout();

				// Is window off the screen at this size?
				if (!area.Contains(Bounds))
				{
					// At large framebuffer sizes/low screen resolutions, the window may be too large to fit the screen even at 1x scale
					// Prioritize that the top-left of the window is on-screen so the title bar and menu stay accessible

					if (Bounds.Right > area.Right) // Window is off the right edge
					{
						Left = Math.Max(area.Right - Size.Width, area.Left);
					}

					if (Bounds.Bottom > area.Bottom) // Window is off the bottom edge
					{
						Top = Math.Max(area.Bottom - Size.Height, area.Top);
					}
				}
			}

			DoPresentationPanelResize();
			DoPresentationPanelResize();
		}

		private void SynchChrome()
		{
			if (_inFullscreen)
			{
				// TODO - maybe apply a hack tracked during fullscreen here to override it
				FormBorderStyle = FormBorderStyle.None;
				MainMenuStrip.Visible = Config.DispChromeMenuFullscreen && !_argParser._chromeless;
				MainStatusBar.Visible = Config.DispChromeStatusBarFullscreen && !_argParser._chromeless;
			}
			else
			{
				MainStatusBar.Visible = Config.DispChromeStatusBarWindowed && !_argParser._chromeless;
				MainMenuStrip.Visible = Config.DispChromeMenuWindowed && !_argParser._chromeless;
				MaximizeBox = MinimizeBox = Config.DispChromeCaptionWindowed && !_argParser._chromeless;
				if (Config.DispChromeFrameWindowed == 0 || _argParser._chromeless)
				{
					FormBorderStyle = FormBorderStyle.None;
				}
				else if (Config.DispChromeFrameWindowed == 1)
				{
					FormBorderStyle = FormBorderStyle.SizableToolWindow;
				}
				else if (Config.DispChromeFrameWindowed == 2)
				{
					FormBorderStyle = FormBorderStyle.Sizable;
				}
			}
		}

		public void ToggleFullscreen(bool allowSuppress = false)
		{
			AutohideCursor(hide: false);

			// prohibit this operation if the current controls include LMouse
			if (allowSuppress)
			{
				if (InputManager.ActiveController.HasBinding("WMouse L"))
				{
					return;
				}
			}

			if (!_inFullscreen)
			{
				SuspendLayout();

				// Work around an AMD driver bug in >= vista:
				// It seems windows will activate opengl fullscreen mode when a GL control is occupying the exact space of a screen (0,0 and dimensions=screensize)
				// AMD cards manifest a problem under these circumstances, flickering other monitors.
				// It isn't clear whether nvidia cards are failing to employ this optimization, or just not flickering.
				// (this could be determined with more work; other side affects of the fullscreen mode include: corrupted TaskBar, no modal boxes on top of GL control, no screenshots)
				// At any rate, we can solve this by adding a 1px black border around the GL control
				// Please note: It is important to do this before resizing things, otherwise momentarily a GL control without WS_BORDER will be at the magic dimensions and cause the flakeout
				if (!OSTailoredCode.IsUnixHost
					&& Config.DispFullscreenHacks)
				{
					// ATTENTION: this causes the StatusBar to not work well, since the backcolor is now set to black instead of SystemColors.Control.
					// It seems that some StatusBar elements composite with the backcolor.
					// Maybe we could add another control under the StatusBar. with a different backcolor
					Padding = new Padding(1);
					BackColor = Color.Black;

					// FUTURE WORK:
					// re-add this padding back into the display manager (so the image will get cut off a little but, but a few more resolutions will fully fit into the screen)
				}

				_windowedLocation = Location;

				_inFullscreen = true;
				SynchChrome();
				WindowState = FormWindowState.Maximized; // be sure to do this after setting the chrome, otherwise it wont work fully
				ResumeLayout();
			}
			else
			{
				SuspendLayout();

				WindowState = FormWindowState.Normal;

				if (!OSTailoredCode.IsUnixHost)
				{
					// do this even if DispFullscreenHacks aren't enabled, to restore it in case it changed underneath us or something
					Padding = new Padding(0);

					// it's important that we set the form color back to this, because the StatusBar icons blend onto the mainform, not onto the StatusBar--
					// so we need the StatusBar and mainform backdrop color to match
					BackColor = SystemColors.Control;
				}

				_inFullscreen = false;

				SynchChrome();
				Location = _windowedLocation;
				ResumeLayout();

				FrameBufferResized();
			}
		}

		private void OpenLuaConsole()
		{
			if (!LuaLibraries.IsAvailable)
			{
				ShowMessageBox(
					owner: null,
					text: "Native Lua dynamic library was unable to be loaded. "
						+ "This library is provided in the dll/ folder, try redownloading Chimera to fix this error.",
					caption: "Lua Load Error",
					EMsgBoxIcon.Error);
				return;
			}

			Tools.Load<LuaConsole>();
		}

		public void ClickSpeedItem(int num)
		{
			if ((ModifierKeys & Keys.Control) != 0)
			{
				SetSpeedPercentAlternate(num);
			}
			else
			{
				SetSpeedPercent(num);
			}
		}

		private void ThrottleMessage()
		{
			string type = ":(none)";
			if (Config.SoundThrottle)
			{
				type = ":Sound";
			}

			if (Config.VSyncThrottle)
			{
				type = $":Vsync{(Config.VSync ? "[ena]" : "[dis]")}";
			}

			if (Config.ClockThrottle)
			{
				type = ":Clock";
			}

			string throttled = Config.Unthrottled ? "Unthrottled" : "Throttled";
			string msg = $"{throttled}{type} ";

			AddOnScreenMessage(msg);
		}

		public void FrameSkipMessage()
		{
			AddOnScreenMessage($"Frameskipping set to {Config.FrameSkip}");
		}

		public void UpdateCheatStatus()
		{
			if (CheatList.AnyActive)
			{
				CheatStatusButton.ToolTipText = "Addresses are currently frozen";
				CheatStatusButton.Image = Properties.Resources.Cheat;
				CheatStatusButton.Visible = true;
			}
			else
			{
				CheatStatusButton.ToolTipText = "";
				CheatStatusButton.Image = Properties.Resources.Blank;
				CheatStatusButton.Visible = false;
			}
		}

		private Size _lastVideoSize = new Size(-1, -1), _lastVirtualSize = new Size(-1, -1);

		// AVI/WAV state
		private IVideoWriter _currAviWriter;

		// Sound refactor TODO: we can enforce async mode here with a property that gets/sets this but does an async check
		private ISoundProvider _aviSoundInputAsync; // Note: This sound provider must be in async mode!

		private SimpleSyncSoundProvider _dumpProxy; // an audio proxy used for dumping

		/// <summary>zeroes handed to the sound card while Encode Video is running</summary>
		private short[] _encodeSilence = [ ];

		private bool _windowClosedAndSafeToExitProcess;
		private int _exitCode;
		private bool _exitRequestPending;
		private bool _runloopFrameProgress;
		private long _frameAdvanceTimestamp;
		private long _frameRewindTimestamp;
		private bool _frameRewindWasPaused;
		private bool _runloopFrameAdvance;
		private bool _wasRewinding;
		private bool _lastFastForwardingOrRewinding;
		private bool _inResizeLoop;

		private readonly double _fpsUpdatesPerSecond = 4.0;
		private readonly double _fpsSmoothing = 8.0;
		private double _lastFps;
		private int _lastFpsRounded;
		private int _framesSinceLastFpsUpdate;
		private long _timestampLastFpsUpdate;

		public int GetApproxFramerate() => _lastFpsRounded;

		private readonly Throttle _throttle;

		// For handling automatic pausing when entering the menu
		private bool _wasPaused;
		private bool _didMenuPause;

		private bool _cursorHidden;
		private bool _inFullscreen;
		private Point _windowedLocation;
		private bool _needsFullscreenOnLoad;
		private bool _framebufferResizedPending;

		private int _lastOpenRomFilter;

		private readonly ParsedCLIFlags _argParser;

		private int _autoDumpLength;

		// Resources
		/// <summary>
		/// The second and later drive lights. The first is the Designer's own
		/// label, which keeps its place in the status bar; these are inserted
		/// after it the first time a machine turns out to have that many drives,
		/// and hidden rather than destroyed when one does not.
		/// </summary>
		private readonly List<StatusLabelEx> _extraDriveLights = [ ];

		private StatusLabelEx DriveLightLabel(int index)
		{
			if (index is 0) return LedLightStatusLabel;
			while (_extraDriveLights.Count < index)
			{
				var made = new StatusLabelEx { Visible = false };
				_extraDriveLights.Add(made);
				MainStatusBar.Items.Insert(
					MainStatusBar.Items.IndexOf(LedLightStatusLabel) + _extraDriveLights.Count,
					made);
			}
			return _extraDriveLights[index - 1];
		}

		private Bitmap _statusBarDiskLightOnImage;
		private Bitmap _statusBarDiskLightOffImage;
		private Bitmap _linkCableOn;
		private Bitmap _linkCableOff;

		private readonly PresentationPanel _presentationPanel;

		private void SetStatusBar()
		{
			if (!_inFullscreen)
			{
				MainStatusBar.Visible = Config.DispChromeStatusBarWindowed;
				PerformLayout();
				FrameBufferResized();
			}
		}

		protected override string WindowTitle
		{
			get
			{
				var sb = new StringBuilder();

				if (_inResizeLoop)
				{
					var size = _presentationPanel.NativeSize;
					sb.Append($"({size.Width}x{size.Height})={(float) size.Width / size.Height} - ");
				}

				if (Config.DispSpeedupFeatures == 0)
				{
					// we need to display FPS somewhere, in this case
					sb.Append($"({_lastFps:0} fps) - ");
				}

				if (!Emulator.IsNull())
				{
					sb.Append($"{Game.Name} [{Emulator.GetSystemDisplayName()}] - ");
					var movie = MovieSession.Movie;
					if (movie.IsActive())
					{
						// I think the asterisk is conventionally after the filename, but I worry it would often be cut off there --yoshi
						sb.Append($"{(movie.Changes ? "*" : string.Empty)}{Path.GetFileName(movie.Filename)} - ");
					}
				}

				sb.Append(Config.MainFormStaticWindowTitleOverrideEffective);

				return sb.ToString();
			}
		}

		protected override string WindowTitleStatic
			=> Config.MainFormStaticWindowTitleOverrideEffective;

		private void ClearAutohold()
		{
			ClearHolds();
			AddOnScreenMessage("Autohold keys cleared");
		}

		private void UpdateToolsLoadstate()
		{
		}

		private void UpdateToolsAfter()
		{
			Tools.UpdateToolsAfter();
			HandleToggleLightAndLink();
		}

		// Rom details as decided by MainForm, which shouldn't happen, the RomLoader or Core should be doing this
		// Better is to just keep the game and rom hashes as properties and then generate the rom info from this
		private string _defaultRomDetails = "";

		private void RewireSound()
		{
			if (_dumpProxy != null)
			{
				// we're video dumping, so async mode only and use the DumpProxy.
				// note that the avi dumper has already rewired the emulator itself in this case.
				Sound.SetInputPin(_dumpProxy);
			}
			else
			{
				bool useAsyncMode = _currentSoundProvider.CanProvideAsync && !Config.SoundThrottle;
				_currentSoundProvider.SetSyncMode(useAsyncMode ? SyncSoundMode.Async : SyncSoundMode.Sync);
				Sound.SetInputPin(_currentSoundProvider);
			}
		}

		private static readonly IList<Type> SpecializedTools = ReflectionCache.Types
			.Where(static t => !t.IsAbstract && typeof(IToolForm).IsAssignableFrom(t)
				&& t.GetCustomAttribute<SpecializedToolAttribute>() is not null)
			.ToList();

		/// <summary>
		/// The tools that exist only because the running core offers them: each is
		/// backed by an optional guest ABI group, so a core that does not export the
		/// group simply does not register the service and the tool never appears.
		/// This is what fills the per-system menu in a frontend that knows nothing
		/// about any system - where BizHawk listed a hand-written NES PPU viewer, we
		/// list whatever the loaded core actually provides.
		/// </summary>
		private static readonly IList<Type> CoreProvidedTools =
		[
			typeof(GenericDebugger),
			typeof(SurfaceViewer),
			typeof(TraceLogger),
		];

		/// <summary>
		/// Fills the "Emulator" menu, which is EMPTY until a core is loaded: every item
		/// in it comes from a core, so with none there is nothing to show and nothing to
		/// grey out. Opening a package gives it what that package's descriptor declares
		/// (its firmware needs); running a game adds what the machine itself has.
		/// </summary>
		private void DisplayDefaultCoreMenu()
		{
			GenericCoreSubMenu.DropDownItems.Clear();

			if (CoreRegistry.Instance.AllFactories.Count is 0) return; // no core loaded: nothing to say

			// What a loaded package says it needs. Known from the descriptor alone, which
			// is why it is here before any rom is: a rom that needs a BIOS cannot be
			// loaded until the BIOS is provided.
			if (CoreFirmwareStore.AnyExpected(CoreRegistry.Instance))
			{
				ToolStripMenuItem firmwareMenuItem = new() { Text = "&Firmware..." };
				firmwareMenuItem.Click += (_, _) =>
				{
					using CoreFirmwareForm form = new(
						() => CoreFirmwareStore.Enumerate(Config, CoreRegistry.Instance),
						(entry, path) => CoreFirmwareStore.SetPath(Config, entry.CoreName, entry.Decl.Id, path));
					form.ShowDialog(this);
				};
				GenericCoreSubMenu.DropDownItems.Add(firmwareMenuItem);
			}

			// What this core has compiled for games so far (docs/compile-cache.md).
			// Emptying it is the only action: filling it belongs to the wizard,
			// where a project is not created until its game is compiled.
			if (CoreRegistry.Instance.AllFactories.OfType<WaterboxCoreFactory>().FirstOrDefault(f => f.Config.Precompile) is { } precompiling
				&& WaterboxCore.CoreCacheDirectoryFor(precompiling.Config) is { } cacheDir)
			{
				var mb = PrecompileOrchestrator.CacheBytes(cacheDir) / (1024.0 * 1024.0);
				ToolStripMenuItem clearCacheMenuItem = new() { Text = $"&Clear Compiled Code ({mb:F0} MB)" };
				clearCacheMenuItem.Click += (_, _) =>
				{
					if (ShowMessageBox2(owner: this, "Delete the code this core compiled for your games? Each project's game must be compiled again before it opens.", "Clear Compiled Code", EMsgBoxIcon.Question))
						PrecompileOrchestrator.Clear(cacheDir);
				};
				GenericCoreSubMenu.DropDownItems.Add(clearCacheMenuItem);
			}
			if (Emulator.SystemId is VSystemID.Raw.NULL) return; // a core, but no machine running yet

			// The way OUT for what this machine keeps (docs/save-data.md): present
			// exactly when the core exports the savedata group, with no change
			// detection - the user knows when their progress is worth exporting.
			if (Emulator.ServiceProvider.HasService<ICoreSaveData>())
			{
				ToolStripMenuItem exportSaveDataMenuItem = new() { Text = "Export Save &Data..." };
				exportSaveDataMenuItem.Click += (_, _) => ExportSaveData();
				GenericCoreSubMenu.DropDownItems.Insert(0, exportSaveDataMenuItem);
			}

			var coreTools = CoreProvidedTools.Concat(SpecializedTools)
				.Where(Tools.IsAvailable)
				.OrderBy(static t => t.Name)
				.ToList();
			if (coreTools.Count is 0) return;

			GenericCoreSubMenu.DropDownItems.Add(new ToolStripSeparator());
			foreach (var toolType in coreTools)
			{
				var (icon, name) = Tools.GetIconAndNameFor(toolType);
				ToolStripMenuItem item = new() { Image = icon, Text = $"&{name}" };
				item.Click += (_, _) => Tools.Load(toolType);
				GenericCoreSubMenu.DropDownItems.Add(item);
			}
		}

		private void InitControls()
		{
			Controller controls = new(new ControllerDefinition("Emulator Frontend Controls")
			{
				BoolButtons = Config.HotkeyBindings.Keys.ToList(),
			}.MakeImmutable());

			foreach (var (k, v) in Config.HotkeyBindings) controls.BindMulti(k, v);

			InputManager.ClientControls = controls;
			InputManager.ControllerInputCoalescer = new();
		}

		private void LoadMoviesFromRecent(string path)
		{
			bool loaded = false;
			if (File.Exists(path))
			{
				var movie = MovieSession.Get(path, true);
				if (movie != null)
				{
					MovieSession.ReadOnly = true;
					StartNewMovie(movie, false);
					loaded = true;
				}
			}
			if (!loaded)
			{
				Config.RecentMovies.HandleLoadError(this, path);
			}
		}

		private void LoadRomFromRecent(string rom)
		{
			var ioa = OpenAdvancedSerializer.ParseWithLegacy(rom);

			// if(ioa is this or that) - for more complex behaviour
			string romPath = ioa.SimplePath;

			if (!LoadRom(romPath, new LoadRomArgs(ioa), out var failureIsFromAskSave))
			{
				if (failureIsFromAskSave) AddOnScreenMessage("ROM loading cancelled due to unsaved changes");
				else if (File.Exists(romPath)) AddOnScreenMessage("ROM loading failed");
				else Config.RecentRoms.HandleLoadError(this, romPath, rom);
			}
		}

		private void SetPauseStatusBarIcon()
		{
			if (EmulatorPaused)
			{
				PauseStatusButton.Image = Properties.Resources.Pause;
				PauseStatusButton.Visible = true;
				PauseStatusButton.ToolTipText = "Emulator Paused";
			}
			else if (IsTurboSeeking)
			{
				PauseStatusButton.Image = Properties.Resources.Lightning;
				PauseStatusButton.Visible = true;
				// ReSharper disable once PossibleInvalidOperationException
				PauseStatusButton.ToolTipText = $"Emulator is turbo seeking to frame {PauseOnFrame.Value} click to stop seek";
			}
			else if (PauseOnFrame.HasValue)
			{
				PauseStatusButton.Image = Properties.Resources.YellowRight;
				PauseStatusButton.Visible = true;
				PauseStatusButton.ToolTipText = $"Emulator is playing to frame {PauseOnFrame.Value} click to stop seek";
			}
			else
			{
				PauseStatusButton.Image = Properties.Resources.Blank;
				PauseStatusButton.Visible = false;
				PauseStatusButton.ToolTipText = "";
			}
		}

		private void SyncThrottle()
		{
			// "unthrottled" = throttle was turned off with "Toggle Throttle" hotkey
			// "turbo" = throttle is off due to the "Turbo" hotkey being held
			// They are basically the same thing but one is a toggle and the other requires a
			// hotkey to be held. There is however slightly different behavior in that turbo
			// skips outputting the audio. There's also a third way which is when no throttle
			// method is selected, but the clock throttle determines that by itself and
			// everything appears normal here.
			var fastForward = IsFastForwarding;
			var turbo = IsTurboing;

			int speedPercent = fastForward ? Config.SpeedPercentAlternate : Config.SpeedPercent;

			DisableSecondaryThrottling = Config.Unthrottled || turbo || fastForward;

			// realtime throttle is never going to be so exact that using a double here is wrong
			_throttle.SetCoreFps(Emulator.VsyncRate());
			_throttle.signal_paused = EmulatorPaused;
			_throttle.signal_unthrottle = Config.Unthrottled || turbo;

			// zero 26-mar-2016 - vsync and vsync throttle here both is odd, but see comments elsewhere about triple buffering
			_throttle.signal_overrideSecondaryThrottle = fastForward && (Config.SoundThrottle || Config.VSyncThrottle || Config.VSync);
			_throttle.SetSpeedPercent(speedPercent);
		}

		private void SetSpeedPercentAlternate(int value)
		{
			Config.SpeedPercentAlternate = value;
			SyncThrottle();
			AddOnScreenMessage($"Alternate Speed: {value}%");
		}

		private void SetSpeedPercent(int value)
		{
			Config.SpeedPercent = value;
			SyncThrottle();
			AddOnScreenMessage($"Speed: {value}%");
		}

		private void Shutdown()
		{
			_currAviWriter?.CloseFile();
			_currAviWriter = null;
		}

		private DateTime _lastMessageCheck = DateTime.MinValue;

		private void CheckMessages()
		{
			var currentTime = DateTime.UtcNow;
			// only check window messages a maximum of once per millisecond
			// this check is irrelvant for the 99% of cases where fps are <1k
			// but gives a slight fps boost in those scenarios
			if ((uint)(currentTime - _lastMessageCheck).Milliseconds > 0)
			{
				_lastMessageCheck = currentTime;
				Application.DoEvents();
			}

			if (ActiveForm != null)
			{
				ScreenSaver.ResetTimerPeriodically();
			}

			if (PathsFromDragDrop is not null) this.DoWithTempMute(() =>
			{
				try
				{
					FormDragDrop_internal();
				}
				catch (Exception ex)
				{
					ShowMessageBox(owner: null, $"Exception on drag and drop:\n{ex}");
				}
				PathsFromDragDrop = null;
			});

			string[][] todo = Array.Empty<string[]>();
			lock (_singleInstanceForwardedArgs)
			{
				if (_singleInstanceForwardedArgs.Count > 0)
				{
					todo = _singleInstanceForwardedArgs.ToArray();
					_singleInstanceForwardedArgs.Clear();
				}
			}
			foreach (var args in todo) SingleInstanceProcessArgs(args);
		}

		private Point _lastMouseAutoHidePos;

		private void AutohideCursor(bool hide, bool alwaysUpdate = true)
		{
			var mousePos = MousePosition;
			// avoid sensitive mice unhiding the mouse cursor
			var shouldUpdateCursor = alwaysUpdate
				|| Math.Abs(_lastMouseAutoHidePos.X - mousePos.X) > 5
				|| Math.Abs(_lastMouseAutoHidePos.Y - mousePos.Y) > 5;

			if (!shouldUpdateCursor || Config.CaptureMouse)
			{
				return;
			}

			_lastMouseAutoHidePos = mousePos;
			if (hide && !_cursorHidden)
			{
				// this only works assuming the mouse is perfectly still
				// if the mouse is slightly moving, it will use the "moving" cursor rather
				_presentationPanel.Control.Cursor = Properties.Resources.BlankCursor.Value;

				// This will actually fully hide the cursor
				// However, this is a no-op on Mono, so we need to do both ways
				Cursor.Hide();

				_cursorHidden = true;
			}
			else if (!hide && _cursorHidden)
			{
				_presentationPanel.Control.Cursor = Cursors.Default;
				Cursor.Show();
				timerMouseIdle.Stop();
				timerMouseIdle.Start();
				_cursorHidden = false;
			}
		}

		public BitmapBuffer MakeScreenshotImage()
		{
			var ret = new BitmapBuffer(_currentVideoProvider.BufferWidth, _currentVideoProvider.BufferHeight, _currentVideoProvider.GetVideoBufferCopy());
			ret.DiscardAlpha();
			return ret;
		}

		/*internal*/public void Render()
		{
			if (Config.DispSpeedupFeatures == 0)
			{
				DisplayManager.DiscardApiSurfaces();
				return;
			}

			// nothing is loaded, so there is nothing to show but the eye
			var snowing = Emulator is NullEmulator && (Config.SnowyNullCore switch
			{
				SnowyNullVideo.TriggerCriterion.Always => true,
				SnowyNullVideo.TriggerCriterion.WeekOfChristmas => DateTime.Now.DayOfYear is >= 354/*Dec. 20*/ and <= 360/*Dec. 26*/,
				_ => false,
			});
			if (Emulator is NullEmulator && !snowing)
			{
				// the idle eye watches the pointer: its horizontal place over the
				// display, pegged when it wanders off
				var panel = _presentationPanel.Control;
				if (panel.Width > 0)
				{
					var mouseX = panel.PointToClient(Control.MousePosition).X;
					_idleEye.LookAt(((mouseX / (float) panel.Width) * 2f) - 1f);
				}
			}
			var video = Emulator is NullEmulator && !snowing ? _idleEye : _currentVideoProvider;
			Size currVideoSize = new Size(video.BufferWidth, video.BufferHeight);
			Size currVirtualSize = new Size(video.VirtualWidth, video.VirtualHeight);


			bool resizeFramebuffer = currVideoSize != _lastVideoSize || currVirtualSize != _lastVirtualSize;

			bool isZero = currVideoSize.Width == 0 || currVideoSize.Height == 0 || currVirtualSize.Width == 0 || currVirtualSize.Height == 0;

			//don't resize if the new size is 0 somehow; we'll wait until we have a sensible size
			if (isZero)
			{
				resizeFramebuffer = false;
			}

			if (resizeFramebuffer)
			{
				_lastVideoSize = currVideoSize;
				_lastVirtualSize = currVirtualSize;
				FrameBufferResized();
			}

			//rendering flakes out egregiously if we have a zero size
			//can we fix it later not to?
			if (isZero)
			{
				DisplayManager.Blank();
			}
			else
			{
				DisplayManager.UpdateSource(video, useSnow: snowing);
			}
		}

		public static readonly FilesystemFilterSet ConfigFileFSFilterSet = new(
			appendAllFilesEntry: false,
			new FilesystemFilter("Config File", extensions: [ "ini" ]));

		public static readonly FilesystemFilterSet CorePackageFSFilterSet = new(
			new FilesystemFilter("Chimera Core Package", extensions: [ "chimeraCore", "zip" ]));

		/// <summary>packages found by the startup scan, kept so the Core Packages dialog opens without rescanning</summary>
		private IReadOnlyList<DiscoveredCorePackage> _discoveredCorePackages = [ ];

		private IReadOnlyList<(DiscoveredCorePackage Package, string Error)> _corePackageLoadFailures = [ ];

		/// <summary>
		/// Scans the core search directories for packages. It does NOT load them: what
		/// is in a folder is a list of what is available, and a core becomes part of the
		/// session because you opened it, exactly as a rom does. Runs during
		/// construction, so it must not touch the UI.
		/// </summary>
		private void ScanForCorePackages()
		{
			_discoveredCorePackages = CorePackageDiscovery.ScanFor(Config);
		}

		public IReadOnlyList<DiscoveredCorePackage> DiscoveredCorePackages => _discoveredCorePackages;

		public IReadOnlyList<(DiscoveredCorePackage Package, string Error)> CorePackageLoadFailures => _corePackageLoadFailures;

		/// <summary>Loads and registers a core package (dir or zip). Returns false (after telling the user) on failure.</summary>
		public bool LoadCorePackage(string path)
		{
			try
			{
				var (manifest, packageSha1, factories) = CoreRegistry.Instance.LoadCorePackage(path);
				Config.LastCorePackagePath = path;
				// Opening a core is how you choose one: every system this package claims will open
				// its roms with it from now on. Startup discovery deliberately does NOT do this - it
				// only makes packages available, so what is sitting in Cores/ can never silently
				// reassign what you picked.
				foreach (var factory in factories)
				{
					foreach (var sysID in factory.SystemIds) CoreChoices.MakeDefault(Config, sysID, factory.CoreName);
				}
					AddOnScreenMessage(packageSha1 is null
					? $"Loaded core package: {manifest.Name} (directory form, unhashed)"
					: $"Loaded core package: {manifest.Name} [{packageSha1.Substring(0, 8)}]");
				return true;
			}
			catch (Exception ex)
			{
				ShowMessageBox(
					owner: this,
					$"Failed to load core package:\n{path}\n\n{ex.Message}",
					"Open Core",
					EMsgBoxIcon.Error);
				return false;
			}
		}

		private void OpenRom()
		{
			if (CoreRegistry.Instance.AllFactories.Count is 0)
			{
				AddOnScreenMessage("There is no core to run this with; start a project first.");
				return;
			}
			var result = this.ShowFileOpenDialog(
				filter: RomLoader.RomFilter,
				filterIndex: ref _lastOpenRomFilter,
				initDir: Config.PathEntries.RomAbsolutePath(Emulator.SystemId));
			if (result is null) return;
			var filePath = new FileInfo(result).FullName;
			_ = LoadRom(filePath, new LoadRomArgs(new OpenAdvanced_OpenRom(filePath)));
		}

		private void CoreSettings(object sender, RomLoader.SettingsLoadArgs e)
		{
			// a queued movie's settings are the project's flat map wrapped as
			// {"Values":{...}}; without a movie, the user's config store serves
			e.Settings = MovieSession.NewMovieQueued && MovieSettings.Decode(MovieSession.QueuedSettings) is { } queued
				? queued
				: Config.GetCoreSettings(e.Core, e.SettingsType);
		}

		private void HandlePutCoreSettings(PutSettingsDirtyBits dirty)
		{
			if (dirty.HasFlag(PutSettingsDirtyBits.RebootCore)) FlagNeedsReboot();
			if (dirty.HasFlag(PutSettingsDirtyBits.ScreenLayoutChanged)) FrameBufferResized();
		}

		private bool MayPutCoreSettings()
		{
			if (MovieSession.Movie.IsActive())
			{
				AddOnScreenMessage("Attempt to change settings while a movie is active BLOCKED (settings are structural).");
				return false;
			}
			return true;
		}

		public ISettingsAdapter GetSettingsAdapterFor<T>()
			where T : IEmulator
			=> Emulator is T
				? GetSettingsAdapterForLoadedCoreUntyped()
				: new ConfigSettingsAdapter<T>(Config);

		public ISettingsAdapter GetSettingsAdapterFor(ICoreFactory factory)
			=> Emulator?.GetType() == factory.CoreType
				? GetSettingsAdapterForLoadedCoreUntyped()
				: new ConfigSettingsAdapterUntyped(Config, factory.CoreType);

		public ISettingsAdapter GetSettingsAdapterForLoadedCore<T>()
			where T : IEmulator
		{
			if (Emulator is not T) throw new InvalidOperationException();
			return GetSettingsAdapterForLoadedCoreUntyped();
		}

		public SettingsAdapter GetSettingsAdapterForLoadedCoreUntyped()
			=> new(Emulator, MayPutCoreSettings, HandlePutCoreSettings);

		private FileWriteResult SaveConfig(string path = "")
		{
			if (Config.SaveWindowPosition)
			{
				if (WindowState is FormWindowState.Normal
					&& Location is not { X: -32000, Y: -32000 }) // this is the location when minimized on Windows --adelikat // and can occur in some unknown edge case even when `WindowState is Normal` --yoshi
				{
					Config.MainWindowPosition = Location;
					Config.MainWindowSize = Size;
				}
				Config.MainWindowMaximized = WindowState is FormWindowState.Maximized && !_inFullscreen;
			}
			else
			{
				Config.MainWindowPosition = null;
				Config.MainWindowSize = null;
			}

			Config.LastWrittenFromDetailed = VersionInfo.GetEmuVersion();

			if (string.IsNullOrEmpty(path))
			{
				path = _getConfigPath();
			}

			CommitCoreSettingsToConfig();
			return ConfigService.Save(path, Config);
		}

		private void ToggleFps()
			=> Config.DisplayFps = !Config.DisplayFps;

		private void ToggleFrameCounter()
			=> Config.DisplayFrameCounter = !Config.DisplayFrameCounter;

		private void ToggleLagCounter()
			=> Config.DisplayLagCounter = !Config.DisplayLagCounter;

		private void ToggleInputDisplay()
			=> Config.DisplayInput = !Config.DisplayInput;

		public void ToggleSound()
		{
			Config.SoundEnabled = !Config.SoundEnabled;
			Sound.StopSound();
			Sound.StartSound();
			UpdateStatusBarMuteIndicator();
		}

		private void VolumeUp()
		{
			Config.SoundVolume += 10;
			if (Config.SoundVolume > 100)
			{
				Config.SoundVolume = 100;
			}

			AddOnScreenMessage($"Volume {Config.SoundVolume}");
		}

		private void VolumeDown()
		{
			Config.SoundVolume -= 10;
			if (Config.SoundVolume < 0)
			{
				Config.SoundVolume = 0;
			}

			AddOnScreenMessage($"Volume {Config.SoundVolume}");
		}

		public BitmapBuffer CaptureOSD()
		{
			var bb = DisplayManager.RenderOffscreen(_currentVideoProvider, true);
			bb.DiscardAlpha();
			return bb;
		}
		public BitmapBuffer CaptureLua()
		{
			var bb = DisplayManager.RenderOffscreenLua(_currentVideoProvider);
			bb.DiscardAlpha();
			return bb;
		}

		private void IncreaseWindowSize()
		{
			var windowScale = Config.GetWindowScaleFor(Emulator.SystemId);
			if (windowScale < EmuClientApi.WINDOW_SCALE_MAX)
			{
				windowScale++;
				Config.SetWindowScaleFor(Emulator.SystemId, windowScale);
			}
			AddOnScreenMessage($"Screensize set to {windowScale}x");
			FrameBufferResized(forceWindowResize: true);
		}

		private void DecreaseWindowSize()
		{
			var windowScale = Config.GetWindowScaleFor(Emulator.SystemId);
			if (windowScale > 1)
			{
				windowScale--;
				Config.SetWindowScaleFor(Emulator.SystemId, windowScale);
			}
			AddOnScreenMessage($"Screensize set to {windowScale}x");
			FrameBufferResized(forceWindowResize: true);
		}

		private static readonly int[] SpeedPercents = { 1, 3, 6, 12, 25, 50, 75, 100, 150, 200, 300, 400, 800, 1600, 3200, 6400 };

		private bool CheckCanSetSpeed()
		{
			if (Config.ClockThrottle)
				return true;

			AddOnScreenMessage("Unable to change speed, please switch to clock throttle");
			return false;
		}

		private void ResetSpeed()
		{
			if (!CheckCanSetSpeed())
				return;

			SetSpeedPercent(100);
		}

		private void IncreaseSpeed()
		{
			if (!CheckCanSetSpeed())
				return;

			var oldPercent = Config.SpeedPercent;
			int newPercent;

			int i = 0;
			do
			{
				i++;
				newPercent = SpeedPercents[i];
			}
			while (newPercent <= oldPercent && i < SpeedPercents.Length - 1);

			SetSpeedPercent(newPercent);
		}

		private void DecreaseSpeed()
		{
			if (!CheckCanSetSpeed())
				return;

			var oldPercent = Config.SpeedPercent;
			int newPercent;

			int i = SpeedPercents.Length - 1;
			do
			{
				i--;
				newPercent = SpeedPercents[i];
			}
			while (newPercent >= oldPercent && i > 0);

			SetSpeedPercent(newPercent);
		}

		private void SaveMovie()
		{
			if (MovieSession.Movie.IsActive())
			{
				FileWriteResult result = MovieSession.Movie.Save();
				if (result.IsError)
				{
					AddOnScreenMessage($"Failed to save {MovieSession.Movie.Filename}.");
					AddOnScreenMessage(result.UserFriendlyErrorMessage());
				}
				else
				{
					AddOnScreenMessage($"{MovieSession.Movie.Filename} saved.");
				}
			}
		}

		private void HandleToggleLightAndLink()
		{
			if (!MainStatusBar.Visible) return;

			// ONE LIGHT PER DRIVE. A DOS machine can have a disc and a hard disk at
			// the same time, and they are different facts: a single light meaning
			// "something was read" says almost nothing while a game streams from
			// CD. The first light is the Designer's label and the rest are made as
			// they are needed, so a machine with no media adds nothing to the bar.
			if (Emulator.ServiceProvider.GetService<IDriveLights>() is { DriveLightCount: > 0 } lights)
			{
				for (int i = 0; i < lights.DriveLightCount; i++)
				{
					var label = DriveLightLabel(i);
					label.Image = lights.DriveLightOn(i)
						? _statusBarDiskLightOnImage
						: _statusBarDiskLightOffImage;
					label.ToolTipText = lights.DriveLightName(i);
					label.Visible = true;
				}
				for (int i = lights.DriveLightCount; i < _extraDriveLights.Count + 1; i++)
				{
					DriveLightLabel(i).Visible = false;
				}
			}
			else if (Emulator.HasDriveLight() && Emulator.AsDriveLight() is { DriveLightEnabled: true } diskLEDCore)
			{
				// the single-light service, for a core that reports one drive and
				// says nothing about which
				LedLightStatusLabel.Image = diskLEDCore.DriveLightOn ? _statusBarDiskLightOnImage : _statusBarDiskLightOffImage;
				LedLightStatusLabel.ToolTipText = Emulator.AsDriveLight().DriveLightIconDescription;
				LedLightStatusLabel.Visible = true;
			}
			else
			{
				LedLightStatusLabel.Visible = false;
				foreach (var extra in _extraDriveLights) extra.Visible = false;
			}

			if (Emulator.UsesLinkCable())
			{
				var linkableCore = Emulator.AsLinkable();
				LinkConnectStatusBarButton.Image = linkableCore.LinkConnected ? _linkCableOn : _linkCableOff;
				LinkConnectStatusBarButton.ToolTipText = $"Link connection is currently {(linkableCore.LinkConnected ? "enabled" : "disabled")}";
				LinkConnectStatusBarButton.Visible = true;
			}
			else
			{
				LinkConnectStatusBarButton.Visible = false;
			}
		}

		private void UpdateStatusBarMuteIndicator()
			=> (StatusBarMuteIndicator.Image, StatusBarMuteIndicator.ToolTipText) = Config.SoundEnabled
				? (Properties.Resources.Audio, $"Core is producing audio, live playback at {(Config.SoundEnabledNormal ? Config.SoundVolume : 0)}% volume")
				: (Properties.Resources.AudioMuted, "Core is not producing audio");

		private void UpdateKeyPriorityIcon()
		{
			switch (Config.InputHotkeyOverrideOptions)
			{
				default:
				case Config.InputPriority.BOTH:
					KeyPriorityStatusLabel.Image = Properties.Resources.Both;
					KeyPriorityStatusLabel.ToolTipText = "Key priority: Allow both hotkeys and controller buttons";
					break;
				case Config.InputPriority.INPUT:
					KeyPriorityStatusLabel.Image = Properties.Resources.GameController;
					KeyPriorityStatusLabel.ToolTipText = "Key priority: Controller buttons will override hotkeys";
					break;
				case Config.InputPriority.HOTKEY:
					KeyPriorityStatusLabel.Image = Properties.Resources.HotKeys;
					KeyPriorityStatusLabel.ToolTipText = "Key priority: Hotkeys will override controller buttons";
					break;
			}
		}

		private void ToggleBackgroundInput()
		{
			Config.AcceptBackgroundInput = !Config.AcceptBackgroundInput;
			AddOnScreenMessage($"Background Input {(Config.AcceptBackgroundInput ? "enabled" : "disabled")}");
		}

		private void ToggleCaptureMouse()
		{
			if (!InputManager.ActiveController.IsMouseBound)
			{
				AddOnScreenMessage("Nothing bound to mouse, not capturing cursor");
				return;
			}
			Config.CaptureMouse = !Config.CaptureMouse;
			CaptureMouse(Config.CaptureMouse);
			if (Config.CaptureMouse) AddOnScreenMessage($"Mouse cursor captured, press {Config.HotkeyBindings["Capture Mouse"]} to uncapture", duration: 7);
			else AddOnScreenMessage("Mouse cursor uncaptured");
		}

		private void ToggleStayOnTop()
		{
			TopMost = Config.MainFormStayOnTop = !Config.MainFormStayOnTop;
			AddOnScreenMessage($"Stay on Top {(Config.MainFormStayOnTop ? "enabled" : "disabled")}");
		}

		private void VsyncMessage()
		{
			AddOnScreenMessage($"Display Vsync set to {(Config.VSync ? "on" : "off")}");
		}

		private const int WmDeviceChange = 0x0219;

		private void UpdateCoreStatusBarButton()
		{
			var attributes = Emulator.Attributes();
			var coreDispName = attributes.Released ? attributes.CoreName : $"(Experimental) {attributes.CoreName}";
			LoadedCoreNameMenuItem.Text = $"Loaded core: {coreDispName} ({Emulator.SystemId})";
			if (Emulator.IsNull())
			{
				CoreNameStatusBarButton.Visible = false;
				return;
			}

			CoreNameStatusBarButton.Visible = true;

			CoreNameStatusBarButton.Text = coreDispName;
			CoreNameStatusBarButton.Image = Emulator.Icon();
			CoreNameStatusBarButton.ToolTipText = attributes is PortedCoreAttribute ? "(ported) " : "";
		}

		private void ToggleKeyPriority()
		{
			int priority = (int)Config.InputHotkeyOverrideOptions;
			priority++;
			if (priority > 2)
			{
				priority = 0;
			}

			if (Config.NoMixedInputHokeyOverride && priority == 0)
			{
				priority = 1;
			}

			Config.InputHotkeyOverrideOptions = (Config.InputPriority)priority;
			UpdateKeyPriorityIcon();
			switch (Config.InputHotkeyOverrideOptions)
			{
				case Config.InputPriority.BOTH:
					AddOnScreenMessage("Key priority set to Both Hotkey and Input");
					break;
				case Config.InputPriority.INPUT:
					AddOnScreenMessage("Key priority set to Input over Hotkey");
					break;
				case Config.InputPriority.HOTKEY:
					AddOnScreenMessage("Key priority set to Hotkey over Input");
					break;
			}
		}

		private void LoadConfigFile(string iniPath)
		{
			LoadGlobalConfigFromFile(iniPath);
			InitControls(); // rebind hotkeys
			InputManager.SyncControls(Emulator, MovieSession, Config);
			Tools.Restart(Config, Emulator, Game);
			Sound.Config = Config;
			DisplayManager.UpdateGlobals(Config, Emulator);
			AddOnScreenMessage($"Config file loaded: {iniPath}");
		}

		/*internal*/public void StepRunLoop_Throttle()
		{
			SyncThrottle();
			_throttle.signal_frameAdvance = _runloopFrameAdvance;
			_throttle.signal_continuousFrameAdvancing = _runloopFrameProgress;

			_throttle.Step(Config, Sound, allowSleep: true, forceFrameSkip: -1);
		}

		public void FrameAdvance(bool discardApiSurfaces)
		{
			PressFrameAdvance = true;
			StepRunLoop_Core(true);
			if (discardApiSurfaces)
			{
				DisplayManager.DiscardApiSurfaces();
			}
		}

		private void StepRunLoop_Core(bool force = false)
		{
			var runFrame = false;
			var currentTimestamp = Stopwatch.GetTimestamp();

			double frameAdvanceTimestampDeltaMs = (double)(currentTimestamp - _frameAdvanceTimestamp) / Stopwatch.Frequency * 1000.0;
			bool frameProgressTimeElapsed = frameAdvanceTimestampDeltaMs >= Config.FrameProgressDelayMs;

			// TODO technically this should only force run frames if the frame advance key has been used
			if (Config.SkipLagFrame && Emulator.CanPollInput() && Emulator.AsInputPollable().IsLagFrame && Emulator.Frame > 0)
			{
				runFrame = true;
			}

			bool frameAdvance = InputManager.ClientControls["Frame Advance"] || PressFrameAdvance || HoldFrameAdvance;
			if (FrameInch)
			{
				FrameInch = false;
				if (EmulatorPaused)
				{
					frameAdvance = true;
				}
				else
				{
					PauseEmulator();
				}
			}

			if (frameAdvance)
			{
				if (!_runloopFrameAdvance)
				{
					// handle the initial trigger of a frame advance
					runFrame = true;
					_frameAdvanceTimestamp = currentTimestamp;
					PauseEmulator();
				}
				else if (frameProgressTimeElapsed)
				{
					runFrame = true;
					_runloopFrameProgress = true;
					UnpauseEmulator();
				}
			}
			else
			{
				if (_runloopFrameAdvance)
				{
					// handle release of frame advance
					PauseEmulator();
				}
				_runloopFrameProgress = false;
			}

			_runloopFrameAdvance = frameAdvance;

			if (!EmulatorPaused)
			{
				runFrame = true;
			}

			// An encode owns the emulator until it is finished: it runs a frame every
			// iteration whatever else the client would rather do with them.
			if (EncodeWantsFrame)
			{
				runFrame = true;
			}

			bool isRewinding = Rewind(ref runFrame, currentTimestamp, out var returnToRecording);
			IsRewinding = isRewinding;
			_runloopFrameProgress |= isRewinding;

			float atten = 0;

			// BlockFrameAdvance (true when input it being editted in TAStudio) supercedes all other frame advance conditions
			if ((runFrame || force) && (!BlockFrameAdvance || EncodeWantsFrame))
			{
				var isFastForwarding = IsFastForwarding;
				var isFastForwardingOrRewinding = isFastForwarding || isRewinding || Config.Unthrottled;
				// A seek's destination.
				//
				// Drawing only the destination is right for almost every core:
				// a frame is composed from the machine and nothing else, so the
				// frames a seek passed through cost nothing to have skipped.
				// A renderer whose picture lives on the far side of the GPU
				// bridge is the exception, and no amount of drawing NEAR the
				// destination fixes it. That is handled where it belongs, in
				// the engine: a package that says video.drawEveryFrame is never
				// told to stop drawing at all, and only the readback this flag
				// controls is skipped.
				bool atSeekEnd = IsSeeking && Emulator.Frame == PauseOnFrame.Value - 1;
				bool atTurboSeekEnd = IsTurboSeeking && atSeekEnd;
				// the frames a seek passes through get the turbo treatment whether or
				// not it is a turbo seek; what turbo adds is that none of them is
				// shown even when the host is served
				// IsSeeking again, not the flag alone: this runs from outside the loop
				// too (a tool's frame advance), and the flag is the loop's last word
				bool quietFrame = ((IsSeeking && _seekQuiet) || (IsTurboing && !atTurboSeekEnd)) && !atSeekEnd;

				if (isFastForwardingOrRewinding != _lastFastForwardingOrRewinding)
				{
					InitializeFpsData();
				}

				_lastFastForwardingOrRewinding = isFastForwardingOrRewinding;

				// client input-related duties
				OSD.ClearGuiText();

				CheatList.Pulse();

				// zero 03-may-2014 - moved this before call to UpdateToolsBefore(), since it seems to clear the state which a lua event.framestart is going to want to alter
				InputManager.ClickyVirtualPadController.FrameTick();
				InputManager.ButtonOverrideAdapter.FrameTick();

				long toolsStarted = LoopTrace.Enabled ? Stopwatch.GetTimestamp() : 0;
				if (quietFrame)
				{
					Tools.FastUpdateBefore();
				}
				else
				{
					Tools.UpdateToolsBefore();
				}
				LoopTrace.Add(LoopTrace.ToolsBefore, toolsStarted);

				CaptureRewind(isRewinding);

				// Set volume, if enabled
				if (Config.SoundEnabledNormal)
				{
					atten = Config.SoundVolume / 100.0f;

					if (isFastForwardingOrRewinding)
					{
						if (Config.SoundEnabledRWFF)
						{
							atten *= Config.SoundVolumeRWFF / 100.0f;
						}
						else
						{
							atten = 0;
						}
					}

					// Mute if using Frame Advance/Frame Progress
					if (_runloopFrameAdvance && Config.MuteFrameAdvance)
					{
						atten = 0;
					}
				}

				long movieStarted = LoopTrace.Enabled ? Stopwatch.GetTimestamp() : 0;
				MovieSession.HandleFrameBefore();
				LoopTrace.Add(LoopTrace.Movie, movieStarted);

				// why not skip audio if the user doesn't want sound
				bool renderSound = (Config.SoundEnabled && !IsTurboing)
					|| _currAviWriter?.UsesAudio is true;
				if (!renderSound)
				{
					atten = 0;
				}

				// A frame nobody will look at does not have to be drawn, and a core
				// that knows how to stop drawing (SetRenderingEnabled) makes that
				// a real saving rather than a skipped memcpy - most of a frame, on
				// a console with a 3D chip in it.
				//
				// A SEEK has a destination and draws it (atSeekEnd). The frames on
				// the way are drawn only when the host is about to be served, so
				// that the window shows where the seek has got to - and a turbo seek
				// does not draw even those. A held Turbo key is a different thing -
				// the person is watching to see where they are, and the throttle's
				// one-in-four is what they are watching.
				bool render = (!_throttle.skipNextFrame && !(IsTurboSeeking && !atTurboSeekEnd) && !(IsSeeking && _seekQuiet))
					|| _currAviWriter?.UsesVideo is true
					|| atSeekEnd;
				long advanceStarted = LoopTrace.Enabled ? Stopwatch.GetTimestamp() : 0;
				bool newFrame = Emulator.FrameAdvance(InputManager.ControllerOutput, render, renderSound);
				LoopTrace.Add(LoopTrace.Advance, advanceStarted);

				// an encode reaching the end of the movie is the encode finishing, not
				// the movie ending on the person - so it bypasses the end action too
				movieStarted = LoopTrace.Enabled ? Stopwatch.GetTimestamp() : 0;
				MovieSession.HandleFrameAfter(ToolBypassingMovieEndAction is not null || IsEncodingVideo);
				LoopTrace.Add(LoopTrace.Movie, movieStarted);

				if (returnToRecording)
				{
					MovieSession.Movie.SwitchToRecord();
				}

				if (isRewinding && ToolControllingRewind is null && MovieSession.Movie.IsRecording())
				{
					MovieSession.Movie.Truncate(Emulator.Frame);
					if (!_wasRewinding)
						MovieSession.Movie.Rerecords++;
				}

				CheatList.Pulse();

				if (Emulator.CanPollInput() && Emulator.AsInputPollable().IsLagFrame && Config.AutofireLagFrames)
				{
					InputManager.AutoFireController.IncrementStarts();
				}

				InputManager.StickyAutofireController.IncrementLoops(Emulator.CanPollInput() && Emulator.AsInputPollable().IsLagFrame);

				PressFrameAdvance = false;

				toolsStarted = LoopTrace.Enabled ? Stopwatch.GetTimestamp() : 0;
				if (quietFrame)
				{
					Tools.FastUpdateAfter();
				}
				else
				{
					UpdateToolsAfter();
				}
				LoopTrace.Add(LoopTrace.ToolsAfter, toolsStarted);
				if (newFrame) LoopTrace.Frame();

				if (newFrame)
				{
					_framesSinceLastFpsUpdate++;

					CalcFramerateAndUpdateDisplay(currentTimestamp, isRewinding, isFastForwarding);
				}

				if (IsSeeking && PauseOnFrame.Value <= Emulator.Frame)
				{
					if (PauseOnFrame.Value == Emulator.Frame)
					{
						PauseEmulator();
						PauseOnFrame = null;
					}
				}

				_wasRewinding = isRewinding;

				if (newFrame && PreFutureFrameCallback != null)
				{
					IStatable statable = Emulator.AsStatable();
					MemoryStream state = new();
					statable.SaveStateBinary(new(state));

					int frameCount = 0;
					while (!PreFutureFrameCallback(frameCount) && frameCount < MaxFutureFrames)
					{
						frameCount++;
						MovieSession.HandleFrameBefore();
						Emulator.FrameAdvance(InputManager.ControllerOutput, true, false);
						CheatList.Pulse();
						// No tools updates here. No existing tool (except Lua, but that gets the ShowFutureFrameCallback) needs to do anything.
						// Maybe in the future we'll add a special update type, or add a callback for this.
						// Note that other callbacks (e.g. memory hooks) are still being used.
					}

					state.Seek(0, SeekOrigin.Begin);
					statable.LoadStateBinary(new(state));
				}

				if (!PauseAvi && newFrame)
				{
					AvFrameAdvance();
				}

				// after the dump, so a counted frame is one that reached the file
				EncodeAfterFrame();
			}
			else if (isRewinding)
			{
				// Tools will want to be updated after rewind (load state), but we only need to manually do this if we did not frame advance.
				UpdateToolsAfter();
			}

			long soundStarted = LoopTrace.Enabled ? Stopwatch.GetTimestamp() : 0;
			Sound.UpdateSound(atten, DisableSecondaryThrottling);
			LoopTrace.Add(LoopTrace.Sound, soundStarted);
		}

		private void CalcFramerateAndUpdateDisplay(long currentTimestamp, bool isRewinding, bool isFastForwarding)
		{
			double elapsedSeconds = (currentTimestamp - _timestampLastFpsUpdate) / (double)Stopwatch.Frequency;

			if (elapsedSeconds < 1.0 / _fpsUpdatesPerSecond)
			{
				return;
			}

			if (_lastFps == 0) // Initial calculation
			{
				_lastFps = (_framesSinceLastFpsUpdate - 1) / elapsedSeconds;
			}
			else
			{
				_lastFps = (_lastFps + (_framesSinceLastFpsUpdate * _fpsSmoothing)) / (1.0 + (elapsedSeconds * _fpsSmoothing));
			}
			_lastFpsRounded = (int) Math.Round(_lastFps);

			_framesSinceLastFpsUpdate = 0;
			_timestampLastFpsUpdate = currentTimestamp;

			var fpsString = $"{_lastFpsRounded} fps";
			if (isRewinding)
			{
				fpsString += IsTurboing || isFastForwarding ?
					" <<<<" :
					" <<";
			}
			else if (isFastForwarding)
			{
				fpsString += IsTurboing ?
					" >>>>" :
					" >>";
			}

			OSD.Fps = fpsString;

			// need to refresh window caption in this case
			if (Config.DispSpeedupFeatures is 0) UpdateWindowTitle();
		}

		private void InitializeFpsData()
		{
			_lastFps = _lastFpsRounded = 0;
			_timestampLastFpsUpdate = Stopwatch.GetTimestamp();
			_framesSinceLastFpsUpdate = 0;
		}

		/// <summary>
		/// Starts the command line's dump (--dump-type, --dump-name, --dump-length):
		/// a video written by a run that nobody is watching, for a build machine or
		/// a script. A person encoding a video does it in Encode Video instead,
		/// which is why nothing here asks anything.
		/// </summary>
		/// <param name="videoWriterName">match the short name of an <see cref="IVideoWriter"/></param>
		/// <param name="filename">filename to save to</param>
		private void RecordAv(string videoWriterName, string filename)
		{
			if (_currAviWriter != null) return;

			if (Game.IsNullInstance()) throw new InvalidOperationException("how is an A/V recording starting with no game loaded? please report this including as much detail as possible");

			if (string.IsNullOrEmpty(videoWriterName) && !string.IsNullOrEmpty(Config.VideoWriter))
			{
				videoWriterName = Config.VideoWriter;
			}

			IVideoWriter aw = VideoWriterInventory.GetVideoWriter(videoWriterName, this);
			if (aw == null)
			{
				AddOnScreenMessage($"Couldn't start video writer \"{videoWriterName}\"");
				return;
			}

			try
			{
				aw = Config.VideoWriterAudioSyncEffective ? new VideoStretcher(aw) : new AudioStretcher(aw);
				aw.SetMovieParameters(Emulator.VsyncNumerator(), Emulator.VsyncDenominator());
				(IVideoProvider output, Action dispose) = GetCaptureProvider();
				aw.SetVideoParameters(output.BufferWidth, output.BufferHeight);
				if (dispose != null) dispose();

				aw.SetAudioParameters(44100, 2, 16);

				aw.SetDefaultVideoCodecToken(Config);
				aw.OpenFile(filename);

				// commit the avi writing last, in case there were any errors earlier
				_currAviWriter = aw;
				AddOnScreenMessage("A/V capture started");
				AVStatusLabel.Image = Properties.Resources.Avi;
				AVStatusLabel.ToolTipText = "A/V capture in progress";
				AVStatusLabel.Visible = true;
			}
			catch
			{
				AddOnScreenMessage("A/V capture failed!");
				aw.Dispose();
				throw;
			}

			ConfigureAvSound();
		}

		/// <summary>
		/// Points the sound at the writer that has just been opened. Shared with the
		/// Encode Video dialog, which opens its writer without any of the dialogs
		/// above but needs the audio wired up in exactly the same way.
		/// </summary>
		private void ConfigureAvSound()
		{
			if (Config.VideoWriterAudioSyncEffective)
			{
				_currentSoundProvider.SetSyncMode(SyncSoundMode.Sync);
			}
			else
			{
				if (_currentSoundProvider.CanProvideAsync)
				{
					_currentSoundProvider.SetSyncMode(SyncSoundMode.Async);
					_aviSoundInputAsync = _currentSoundProvider;
				}
				else
				{
					_currentSoundProvider.SetSyncMode(SyncSoundMode.Sync);
					_aviSoundInputAsync = new SyncToAsyncProvider(() => Emulator.VsyncRate(), _currentSoundProvider);
				}
			}

			_dumpProxy = new SimpleSyncSoundProvider();
			RewireSound();
		}

		private (IVideoProvider Output, Action/*?*/ Dispose) GetCaptureProvider()
		{
			// TODO ZERO - this code is pretty jacked. we'll want to frugalize buffers better for speedier dumping, and we might want to rely on the GL layer for padding
			if (Config.AVWriterResizeWidth > 0 && Config.AVWriterResizeHeight > 0)
			{
				BitmapBuffer bbIn = null;
				Bitmap bmpIn = null;
				try
				{
					bbIn = Config.AviCaptureOsd
						? CaptureOSD()
						: new BitmapBuffer(_currentVideoProvider.BufferWidth, _currentVideoProvider.BufferHeight, _currentVideoProvider.GetVideoBuffer());

					bbIn.DiscardAlpha();

					Bitmap bmpOut = new(width: Config.AVWriterResizeWidth, height: Config.AVWriterResizeHeight, PixelFormat.Format32bppArgb);
					bmpIn = bbIn.ToSysdrawingBitmap();
					using (var g = Graphics.FromImage(bmpOut))
					{
						if (Config.AVWriterPad)
						{
							g.Clear(Color.FromArgb(_currentVideoProvider.BackgroundColor));
							g.DrawImageUnscaled(bmpIn, (bmpOut.Width - bmpIn.Width) / 2, (bmpOut.Height - bmpIn.Height) / 2);
						}
						else
						{
							g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
							g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
							g.DrawImage(bmpIn, new Rectangle(0, 0, bmpOut.Width, bmpOut.Height));
						}
					}

					IVideoProvider output = new BmpVideoProvider(bmpOut, _currentVideoProvider.VsyncNumerator, _currentVideoProvider.VsyncDenominator);
					return (output, bmpOut.Dispose);
				}
				finally
				{
					bbIn?.Dispose();
					bmpIn?.Dispose();
				}
			}
			else
			{
				BitmapBuffer source = null;
				if (Config.AviCaptureOsd)
				{
					source = CaptureOSD();
				}
				else if (Config.AviCaptureLua)
				{
					source = CaptureLua();
				}

				if (source != null)
				{
					return (new BitmapBufferVideoProvider(source), source.Dispose);
				}
				else
				{
					return (_currentVideoProvider, null);
				}
			}
		}

		private void AbortAv()
		{
			if (_currAviWriter == null)
			{
				_dumpProxy = null;
				RewireSound();
				return;
			}

			_currAviWriter.Dispose();
			_currAviWriter = null;
			AddOnScreenMessage("A/V capture aborted");
			AVStatusLabel.Image = Properties.Resources.Blank;
			AVStatusLabel.ToolTipText = "";
			AVStatusLabel.Visible = false;
			_aviSoundInputAsync = null;
			_dumpProxy = null; // return to normal sound output
			RewireSound();
		}

		private void StopAv()
		{
			if (_currAviWriter == null)
			{
				_dumpProxy = null;
				RewireSound();
				return;
			}

			_currAviWriter.CloseFile();
			_currAviWriter.Dispose();
			_currAviWriter = null;
			AddOnScreenMessage("A/V capture stopped");
			AVStatusLabel.Image = Properties.Resources.Blank;
			AVStatusLabel.ToolTipText = "";
			AVStatusLabel.Visible = false;
			_aviSoundInputAsync = null;
			_dumpProxy = null; // return to normal sound output
			RewireSound();
		}

		private void AvFrameAdvance()
		{
			// is this the best time to handle this? or deeper inside?
			if (_argParser._currAviWriterFrameList?.Contains(Emulator.Frame) != false)
			{
				if (_currAviWriter == null) return;
				Action dispose = null;
				try
				{
					_currAviWriter.SetFrame(Emulator.Frame);

					(IVideoProvider output, dispose) = GetCaptureProvider();

					short[] samp;
					int nsamp;
					if (Config.VideoWriterAudioSyncEffective)
					{
						((VideoStretcher) _currAviWriter).DumpAV(output, _currentSoundProvider, out samp, out nsamp);
					}
					else
					{
						((AudioStretcher) _currAviWriter).DumpAV(output, _aviSoundInputAsync, out samp, out nsamp);
					}

					if (_encode is not null)
					{
						// Encode Video plays the run as fast as the machine will
						// go, and sound at that speed is noise - so the speakers
						// stay silent while a file is written. The samples above
						// have already gone INTO the file; what is muted is only
						// the copy that would have reached the sound card. The
						// dialog's level meters read them instead, which is the
						// only sign left that there is audio in the video.
						_encode.NoteAudio(samp, nsamp);
						if (_encodeSilence.Length < samp.Length) _encodeSilence = new short[samp.Length];
						_dumpProxy.PutSamples(_encodeSilence, nsamp);
					}
					else
					{
						_dumpProxy.PutSamples(samp, nsamp);
					}
				}
				catch (Exception e)
				{
					ShowMessageBox(owner: null, $"Video dumping died:\n\n{e}");
					AbortAv();
				}
				finally
				{
					if (dispose != null) dispose();
				}
			}

			if (_autoDumpLength > 0) //TODO this is probably not necessary because of the call to StopAv --yoshi
			{
				_autoDumpLength--;
				if (_autoDumpLength == 0) // finish
				{
					StopAv();
					if (_argParser._autoCloseOnDump) ScheduleShutdown();
				}
			}
		}

		private int? LoadArchiveChooser(ChimeraFile file)
		{
			using var ac = new ArchiveChooser(file);
			if (this.ShowDialogAsChild(ac).IsOk())
			{
				return ac.SelectedMemberIndex;
			}

			return null;
		}

		private void ShowLoadError(object sender, RomLoader.RomErrorArgs e)
		{
			string title = "load error";
			if (e.AttemptedCoreLoad != null)
			{
				title = $"{e.AttemptedCoreLoad} load error";
			}

			this.ModalMessageBox(e.Message, title, EMsgBoxIcon.Error);
		}

		private string ChoosePlatformForRom(RomGame rom)
		{
			using var platformChooser = new PlatformChooser()
			{
				RomGame = rom,
			};
			this.ShowDialogWithTempMute(platformChooser);
			return platformChooser.PlatformChoice;
		}

		private LoadRomArgs _currentLoadRomArgs;
		private bool _isLoadingRom;

		public bool LoadRom(string path, LoadRomArgs args) => LoadRom(path, args, out _);

		public bool LoadRom(string path, LoadRomArgs args, out bool failureIsFromAskSave)
		{
			if (!LoadRomInternal(path, args, out failureIsFromAskSave))
				return false;

			// what's the meaning of the last rom path when opening an archive? based on the archive file location
			if (args.OpenAdvanced is OpenAdvanced_OpenRom)
			{
				var physicalPath = path;
				if (ChimeraFile.SplitArchiveMemberPath(path) is { } split) physicalPath = split.ArchivePath;
				Config.PathEntries.LastRomPath = Path.GetDirectoryName(Path.GetFullPath(physicalPath)) ?? "";
			}

			return true;
		}

		// Still needs a good bit of refactoring
		private bool LoadRomInternal(string path, LoadRomArgs args, out bool failureIsFromAskSave)
		{
			failureIsFromAskSave = false;
			if (!CloseGame())
			{
				failureIsFromAskSave = true;
				return false;
			}

			if (path == null)
				throw new ArgumentNullException(nameof(path));
			if (args == null)
				throw new ArgumentNullException(nameof(args));

			_isLoadingRom = true;
			path = GuiUtil.ResolveShortcut(path);

			// if this is the first call to LoadRom (they will come in recursively) then stash the args
			bool firstCall = false;
			if (_currentLoadRomArgs == null)
			{
				firstCall = true;
				_currentLoadRomArgs = args;
			}
			else
			{
				args = _currentLoadRomArgs;
			}

			try
			{
				// movies should require deterministic emulation in ALL cases
				// if the core is managing its own DE through settings a 'deterministic' bool can be passed into the core's constructor
				// it is then up to the core itself to override its own local DeterministicEmulation setting
				bool deterministic = args.Deterministic ?? MovieSession.NewMovieQueued;

				var loader = new RomLoader(Config, this)
				{
					ChooseArchive = LoadArchiveChooser,
					ChoosePlatform = romGame => args.ForcedSysID ?? ChoosePlatformForRom(romGame),
					Deterministic = deterministic,
					OpenAdvanced = args.OpenAdvanced,
				};
				if (path.EndsWith(".chimeraProject", StringComparison.OrdinalIgnoreCase))
				{
					// the resolved project drives the mounts; reboots come this way too
					loader.Project = _openProject;
				}

				loader.OnLoadError += ShowLoadError;
				loader.OnLoadSettings += CoreSettings;

				var nextComm = CreateCoreComm();

				IOpenAdvanced ioa = args.OpenAdvanced;
				var oaOpenrom = ioa as OpenAdvanced_OpenRom;

				var forcedCoreName = MovieSession.QueuedCoreName;

				// a movie that does not know its platform cannot have that
				// checked; the core name still forces the choice (projects made
				// before Platform was stamped at creation)
				if (forcedCoreName is not null && !string.IsNullOrEmpty(MovieSession.QueuedSysID))
				{
					var availCores = CoreRegistry.Instance.GetFactories(MovieSession.QueuedSysID);
					if (!availCores.Any(f => f.CoreName == forcedCoreName))
					{
						const string FMT_STR_NO_SUCH_CORE = "This movie is for the \"{0}\" core,"
							+ " but that's not a valid {1} core. (Was the movie made in this version of Chimera?)"
							+ "\nContinue with your preferred core instead?";
						//TODO let the user pick from `availCores`?
						// also use a different message when `availCores` is empty (which might happen if someone makes a movie on a new core and tries to load it in a version without the core)
						if (!this.ModalMessageBox2(
							caption: "No such core",
							icon: EMsgBoxIcon.Error,
							text: string.Format(
								FMT_STR_NO_SUCH_CORE,
								forcedCoreName,
								EmulatorExtensions.SystemIDToDisplayName(MovieSession.QueuedSysID))))
						{
							return false;
						}
						forcedCoreName = null;
					}
				}

				DisplayManager.ActivateOpenGLContext(); // required in case the core wants to create a shared OpenGL context

				var result = loader.LoadRom(
					path: path,
					nextComm,
					forcedCoreName: forcedCoreName);

				// we need to replace the path in the OpenAdvanced with the canonical one the user chose.
				// It can't be done until loader.LoadRom happens (for CanonicalFullPath)
				if (oaOpenrom != null)
				{
					oaOpenrom.Path = loader.CanonicalFullPath;
				}

				var openAdvancedArgs = $"*{OpenAdvancedSerializer.Serialize(ioa)}";
				Config.RecentRoms.Add(openAdvancedArgs);

				if (result)
				{
					Emulator.Dispose();
					Emulator = loader.LoadedEmulator;
					Game = loader.Game;
					var currentCoreName = Emulator.Attributes().CoreName;
					if (!Config.RecentCores.Contains(currentCoreName))
					{
						Config.RecentCores.Enqueue(currentCoreName);
						while (Config.RecentCores.Count > 5) Config.RecentCores.Dequeue();
					}
					InputManager.SyncControls(Emulator, MovieSession, Config);

					var romDetails = Emulator.RomDetails();
					if (string.IsNullOrWhiteSpace(romDetails) && loader.Rom != null)
					{
						_defaultRomDetails = $"{Game.Name}\r\n{SHA1Checksum.ComputePrefixedHex(loader.Rom.RomData)}\r\n{MD5Checksum.ComputePrefixedHex(loader.Rom.RomData)}\r\n";
					}
					else if (string.IsNullOrWhiteSpace(romDetails) && loader.Rom == null)
					{
						// single disc game
						_defaultRomDetails = $"{Game.Name}\r\nSHA1:N/A\r\nMD5:N/A\r\n";
					}

					if (Emulator.HasBoardInfo())
					{
						Console.WriteLine("Core reported BoardID: \"{0}\"", Emulator.AsBoardInfo().BoardName);
					}

					var previousRom = CurrentlyOpenRom;
					CurrentlyOpenRom = oaOpenrom?.Path ?? openAdvancedArgs;
					CurrentlyOpenRomArgs = args;

					Tools.Restart(Config, Emulator, Game);

					// freezes belong to the machine that is running, and a different
					// machine has different addresses
					if (previousRom == CurrentlyOpenRom && Emulator.HasMemoryDomains())
					{
						CheatList.UpdateDomains(Emulator.AsMemoryDomains());
					}
					else
					{
						CheatList.Clear();
					}

					OnRomChanged();
					DisplayManager.UpdateGlobals(Config, Emulator);
					DisplayManager.Blank();

					RewireSound();
					Tools.UpdateFreezeRelatedTools(null, new(null));

					RomLoaded?.Invoke(this, EventArgs.Empty);

					// Some window messages like paints may be dispatched during this function call and before it returns.
					// Therefore this needs to be called very late after tools have been restarted
					// to ensure no stale references like disposed cores are being used, see https://github.com/TASEmulators/BizHawk/issues/4436.
					if (!OSTailoredCode.IsUnixHost) JumpLists.AddRecentItem(openAdvancedArgs, ioa.DisplayName);

					return true;
				}
				else if (Emulator.IsNull())
				{
					// This shows up if there's a problem
					Tools.Restart(Config, Emulator, Game);
					DisplayManager.UpdateGlobals(Config, Emulator);
					DisplayManager.Blank();
					CheatList.Clear();
					OnRomChanged();
					return false;
				}
				else
				{
					// The ROM has been loaded by a recursive invocation of the LoadROM method.
					RomLoaded?.Invoke(this, EventArgs.Empty);
					return true;
				}
			}
			finally
			{
				if (firstCall)
				{
					_currentLoadRomArgs = null;
				}

				_isLoadingRom = false;
			}
		}

		private void OnRomChanged()
		{
			OSD.Fps = "0 fps";
			UpdateWindowTitle();
			HandlePlatformMenus();
			UpdateCoreStatusBarButton();
			SetMainformMovieInfo();
			WarnAboutNonStandardFirmware();
			SayWhenAGpuIsDrawing();
		}

		/// <summary>
		/// Say so, on screen, when the machine's pictures are coming from a GPU.
		/// A project that asked for a hardware renderer may not have got one -
		/// a build without the bridge, a driver that would not give a context,
		/// a core that declined it - and the difference decides whether this
		/// run's movie replays anywhere else. It is also the only place a
		/// person can see WHICH driver drew, which is what the movie records.
		/// </summary>
		private void SayWhenAGpuIsDrawing()
		{
			if (Emulator.IsNull()) return;
			if (Emulator is IGpuRendered { GpuRenderer: { Length: > 0 } driver })
			{
				AddOnScreenMessage($"GPU: {driver}", 5);
			}
		}

		/// <summary>
		/// Say so when a core is running with firmware it does not recognise - a file the
		/// user substituted, or a dump no declaration lists. Both are allowed and both are
		/// recorded in movies, but they make a machine nobody can reproduce without that
		/// exact file, which the user should hear about at load rather than discover when
		/// someone else's replay desyncs.
		/// </summary>
		private void WarnAboutNonStandardFirmware()
		{
			if (Emulator.IsNull()) return;
			foreach (var entry in CoreFirmwareStore.NonStandard(Config, CoreRegistry.Instance, Emulator.Attributes().CoreName))
			{
				AddOnScreenMessage(entry.WarningText, 5);
			}
		}

		/// <summary>
		/// Emulator > Export Save Data... - the one way OUT for the progress a core
		/// keeps inside its machine (docs/save-data.md). The core enumerates
		/// (relative path, bytes); one file saves under its own name, several become
		/// a zip, and not a byte of it is interpreted here. Runs on the UI thread
		/// between frames, so the machine is at a frame boundary by construction.
		/// </summary>
		private void ExportSaveData()
		{
			if (Emulator.ServiceProvider.GetService<ICoreSaveData>() is not ICoreSaveData saveData) return;

			var files = saveData.SaveDataSnapshot();
			if (files is 0)
			{
				ShowMessageBox(owner: null, "This machine holds no save data yet.", "Export Save Data");
				return;
			}

			try
			{
				var chunk = new byte[1 << 20]; // ranged reads: a big file streams
				if (files is 1)
				{
					var name = Path.GetFileName(saveData.SaveDataName(0));
					var ext = Path.GetExtension(name).TrimStart('.');
					var filterSet = ext.Length is 0
						? new FilesystemFilterSet() // just the All Files entry
						: new FilesystemFilterSet(new FilesystemFilter($"{ext} files", new[] { ext }));
					var result = this.ShowFileSaveDialog(
						filter: filterSet,
						initDir: Config.PathEntries.RomAbsolutePath(Emulator.SystemId),
						initFileName: name);
					if (result is null) return;
					using var fs = new FileStream(result, FileMode.Create, FileAccess.Write);
					CopySaveDataFile(saveData, 0, chunk, fs);
				}
				else
				{
					var result = this.ShowFileSaveDialog(
						filter: new(new FilesystemFilter("Zip Archives", new[] { "zip" })),
						initDir: Config.PathEntries.RomAbsolutePath(Emulator.SystemId),
						initFileName: $"{Game.FilesystemSafeName()} (save data).zip");
					if (result is null) return;
					using var fs = new FileStream(result, FileMode.Create, FileAccess.Write);
					using var zip = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create);
					for (var i = 0; i < files; i++)
					{
						using var entry = zip.CreateEntry(saveData.SaveDataName(i)).Open();
						CopySaveDataFile(saveData, i, chunk, entry);
					}
				}
				AddOnScreenMessage($"Save data exported ({files} file{(files is 1 ? "" : "s")})");
			}
			catch (IOException ex)
			{
				ShowMessageBox(owner: null, $"Could not export save data: {ex.Message}", "Export Save Data");
			}
		}

		private static void CopySaveDataFile(ICoreSaveData saveData, int index, byte[] chunk, Stream output)
		{
			var size = saveData.SaveDataSize(index);
			for (var offset = 0L; offset < size;)
			{
				var got = saveData.SaveDataRead(index, offset, chunk);
				if (got <= 0) throw new IOException($"the core stopped supplying {saveData.SaveDataName(index)} at byte {offset}");
				output.Write(chunk, 0, got);
				offset += got;
			}
		}

		private void CommitCoreSettingsToConfig()
		{
			// save settings object
			var t = Emulator.GetType();
			var settable = GetSettingsAdapterForLoadedCoreUntyped();

			// don't trample config with loaded-from-movie settings
			if (settable.HasSettings && MovieSession.Movie.NotActive())
			{
				Config.PutCoreSettings(settable.GetSettings(), t);
			}
		}

		/// <summary>
		/// This closes the game but does not set things up for using the client with the new null emulator.
		/// This method should only be called (outside of <see cref="LoadNullRom(bool)"/>) if the caller is about to load a new game with no user interaction between close and load.
		/// </summary>
		/// <returns>True if the game was closed. False if the user cancelled due to unsaved changes.</returns>
		private bool CloseGame()
		{
			CommitCoreSettingsToConfig(); // Must happen before stopping the movie, since it checks for active movie.

			if (!Tools.AskSave())
			{
				return false;
			}
			// If TAStudio is open, we already asked about saving the movie.
			if (!Tools.IsLoaded<TAStudio>())
			{
				TryAgainResult saveMovieResult = this.DoWithTryAgainBox(() => MovieSession.StopMovie(), "Failed to save movie.");
				if (saveMovieResult == TryAgainResult.Canceled) return false;
			}

			StopAv();


			Emulator.Dispose();

			// This stuff might belong in LoadNullRom.
			// However, Emulator.IsNull is used all over and at least one use (in LoadRomInternal) appears to depend on this code being here.
			// Some refactoring is needed if these things are to be actually moved to LoadNullRom.
			Emulator = new NullEmulator();
			Game = GameInfo.NullInstance;
			InputManager.SyncControls(Emulator, MovieSession, Config);
			RewireSound();
			RebootStatusBarIcon.Visible = false;

			return true;
		}

		/// <summary>
		/// This closes the current ROM, closes tools that require emulator services, and sets things up for the user to interact with the client having no loaded ROM.
		/// </summary>
		public void LoadNullRom()
		{
			if (Tools.AskSave())
			{
				CloseGame();
				Tools.Restart(Config, Emulator, Game);
				DisplayManager.UpdateGlobals(Config, Emulator);
				PauseOnFrame = null;
				CurrentlyOpenRom = null;
				CurrentlyOpenRomArgs = null;
				CheatList.Clear();
				_openProject?.Dispose();
				_openProject = null;
				OnRomChanged();
			}
		}

		public bool EnsureCoreIsAccurate()
			=> true; // single-core build: no more-accurate alternative to offer

		private void CaptureRewind(bool suppressCaptureRewind)
		{
			if (ToolControllingRewind is { } tool)
			{
				tool.CaptureRewind();
			}
		}

		private bool Rewind(ref bool runFrame, long currentTimestamp, out bool returnToRecording)
		{
			var isRewinding = false;

			returnToRecording = false;

			if (ToolControllingRewind is { } rewindTool)
			{
				if (InputManager.ClientControls["Rewind"] || PressRewind)
				{
					if (_frameRewindTimestamp == 0)
					{
						isRewinding = true;
						_frameRewindTimestamp = currentTimestamp;
						_frameRewindWasPaused = EmulatorPaused;
					}
					else
					{
						double timestampDeltaMs = (double)(currentTimestamp - _frameRewindTimestamp) / Stopwatch.Frequency * 1000.0;
						isRewinding = timestampDeltaMs >= Config.FrameProgressDelayMs;

						// clear this flag once we get out of the progress stage
						if (isRewinding)
						{
							_frameRewindWasPaused = false;
						}

						// if we're freely running, there's no need for reverse frame progress semantics (that may be debatable though)
						if (!EmulatorPaused)
						{
							isRewinding = true;
						}

						if (_frameRewindWasPaused)
						{
							if (IsSeeking)
							{
								isRewinding = false;
							}
						}
					}

					if (isRewinding)
					{
						runFrame = rewindTool.Rewind();
					}
				}
				else
				{
					_frameRewindTimestamp = 0;
				}

				return isRewinding;
			}

			_frameRewindTimestamp = 0;

			return isRewinding;
		}

		public IDialogController DialogController => this;

		private static string SanitiseForFileDialog(string initDir)
		{
#pragma warning disable RS0030 // passes the dir on to caller
			if (initDir.Length is 0 || Directory.Exists(initDir)) return initDir;
#pragma warning restore RS0030
#if DEBUG
			throw new ArgumentException(
				paramName: nameof(initDir),
				message: File.Exists(initDir)
					? $"file picker called with {nameof(initDir)} set to a non-dir"
					: $"file picker called with {nameof(initDir)} set to a nonexistent path");
#else
			return File.Exists(initDir) ? Path.GetDirectoryName(initDir) : string.Empty;
#endif
		}

		public IReadOnlyList<string>/*?*/ ShowFileMultiOpenDialog(
			IDialogParent dialogParent,
			string/*?*/ filterStr,
			ref int filterIndex,
			string initDir,
			bool discardCWDChange = false,
			string/*?*/ initFileName = null,
			bool maySelectMultiple = false,
			string/*?*/ windowTitle = null)
		{
			using OpenFileDialog ofd = new()
			{
				FileName = initFileName ?? string.Empty,
				Filter = filterStr ?? string.Empty,
				FilterIndex = filterIndex,
				InitialDirectory = SanitiseForFileDialog(initDir),
				Multiselect = maySelectMultiple,
				RestoreDirectory = discardCWDChange,
				Title = windowTitle ?? string.Empty,
				ValidateNames = false, // only raises confusing errors, doesn't affect result
			};
			var result = dialogParent.ShowDialogWithTempMute(ofd);
			filterIndex = ofd.FilterIndex;
			return result.IsOk() && ofd.FileNames.Length is not 0 ? ofd.FileNames : null;
		}

		public string/*?*/ ShowFileSaveDialog(
			IDialogParent dialogParent,
			bool discardCWDChange,
			string/*?*/ fileExt,
			string/*?*/ filterStr,
			string initDir,
			string/*?*/ initFileName,
			bool muteOverwriteWarning)
		{
			using SaveFileDialog sfd = new()
			{
				DefaultExt = fileExt ?? string.Empty,
				FileName = initFileName ?? string.Empty,
				Filter = filterStr ?? string.Empty,
				InitialDirectory = SanitiseForFileDialog(initDir),
				OverwritePrompt = !muteOverwriteWarning,
				RestoreDirectory = discardCWDChange,
				ValidateNames = false, // only raises confusing errors, doesn't affect result
			};
			var result = dialogParent.ShowDialogWithTempMute(sfd);
			return result.IsOk() ? sfd.FileName : null;
		}

		public string ShowFolderSelectDialog(
			IDialogParent dialogParent,
			string/*?*/ initDir = null,
			string/*?*/ subtitle = null)
		{
			subtitle ??= string.Empty;
			initDir = SanitiseForFileDialog(initDir ?? string.Empty);
			if (OSTailoredCode.IsUnixHost)
			{
				// FolderBrowserEx doesn't work in Mono for obvious reasons
				using FolderBrowserDialog f = new();
				f.Description = subtitle;
				f.SelectedPath = initDir;
				return f.ShowDialog().IsOk() ? f.SelectedPath : null;
			}
			else
			{
				using FolderBrowserEx f = new();
				f.Description = subtitle;
				f.SelectedPath = initDir;
				return f.ShowDialog().IsOk() ? f.SelectedPath : null;
			}
		}

		public void ShowMessageBox(
			IDialogParent/*?*/ owner,
			string text,
			string/*?*/ caption = null,
			EMsgBoxIcon? icon = null)
				=> this.ShowMessageBox(
					owner: owner,
					text: text,
					caption: caption,
					buttons: MessageBoxButtons.OK,
					icon: icon);

		public bool ShowMessageBox2(
			IDialogParent/*?*/ owner,
			string text,
			string/*?*/ caption = null,
			EMsgBoxIcon? icon = null,
			bool useOKCancel = false)
				=> this.ShowMessageBox(
					owner: owner,
					text: text,
					caption: caption,
					buttons: useOKCancel ? MessageBoxButtons.OKCancel : MessageBoxButtons.YesNo,
					icon: icon) switch
				{
					DialogResult.OK => true,
					DialogResult.Yes => true,
					_ => false,
				};

		public bool? ShowMessageBox3(
			IDialogParent/*?*/ owner,
			string text,
			string/*?*/ caption = null,
			EMsgBoxIcon? icon = null)
				=> this.ShowMessageBox(
					owner: owner,
					text: text,
					caption: caption,
					buttons: MessageBoxButtons.YesNoCancel,
					icon: icon) switch
				{
					DialogResult.Yes => true,
					DialogResult.No => false,
					_ => null,
				};

		public void ShowMessageIfError(Func<FileWriteResult> action, string message)
		{
			FileWriteResult result = action();
			if (result.IsError)
			{
				this.ErrorMessageBox(result, message);
			}
		}

		public void StartSound() => Sound.StartSound();
		public void StopSound() => Sound.StopSound();

		private Mutex _singleInstanceMutex;
		private NamedPipeServerStream _singleInstanceServer;
		private readonly List<string[]> _singleInstanceForwardedArgs = new();

		private bool SingleInstanceInit(string[] args)
		{
			//note: this isn't 100% reliable, it's just a user convenience
			_singleInstanceMutex = new Mutex(true, "mutex-{84125ACB-F570-4458-9748-321F887FE795}", out bool createdNew);
			if (createdNew)
			{
				StartSingleInstanceServer();
				return false;
			}
			else
			{
				ForwardSingleInstanceStartup(args);
				return true;
			}
		}

		private void SingleInstanceDispose()
		{
			_singleInstanceServer?.Dispose();
		}

		private void ForwardSingleInstanceStartup(string[] args)
		{
			using var namedPipeClientStream = new NamedPipeClientStream(".", "pipe-{84125ACB-F570-4458-9748-321F887FE795}", PipeDirection.Out);
			try
			{
				namedPipeClientStream.Connect(0);
				//do this a bit cryptically to avoid loading up another big assembly (especially ones as frail as http and/or web ones)
				var payloadString = string.Join("|", args.Select(a => Encoding.UTF8.GetBytes(a).BytesToHexString()));
				var payloadBytes = Encoding.ASCII.GetBytes(payloadString);
				namedPipeClientStream.Write(payloadBytes, 0, payloadBytes.Length);
			}
			catch
			{
				Console.WriteLine("Failed forwarding args to already-running single instance");
			}
		}

		private void StartSingleInstanceServer()
		{
			//MIT LICENSE - https://www.autoitconsulting.com/site/development/single-instance-winform-app-csharp-mutex-named-pipes/

			// Create a new pipe accessible by local authenticated users, disallow network
			var sidNetworkService = new SecurityIdentifier(WellKnownSidType.NetworkServiceSid, null);
			var sidWorld = new SecurityIdentifier(WellKnownSidType.WorldSid, null);

			var pipeSecurity = new PipeSecurity();

			// Deny network access to the pipe
			var accessRule = new PipeAccessRule(sidNetworkService, PipeAccessRights.ReadWrite, AccessControlType.Deny);
			pipeSecurity.AddAccessRule(accessRule);

			// Alow Everyone to read/write
			accessRule = new PipeAccessRule(sidWorld, PipeAccessRights.ReadWrite, AccessControlType.Allow);
			pipeSecurity.AddAccessRule(accessRule);

			// Current user is the owner
			SecurityIdentifier sidOwner = WindowsIdentity.GetCurrent().Owner;
			if (sidOwner != null)
			{
				accessRule = new PipeAccessRule(sidOwner, PipeAccessRights.FullControl, AccessControlType.Allow);
				pipeSecurity.AddAccessRule(accessRule);
			}

			// Create pipe and start the async connection wait
#if NET5_0_OR_GREATER
			_singleInstanceServer = NamedPipeServerStreamAcl.Create(
#else
			_singleInstanceServer = new NamedPipeServerStream(
#endif
					"pipe-{84125ACB-F570-4458-9748-321F887FE795}",
					PipeDirection.In,
					1,
					PipeTransmissionMode.Message,
					PipeOptions.Asynchronous,
					0,
					0,
					pipeSecurity);

			// Begin async wait for connections
			_singleInstanceServer.BeginWaitForConnection(SingleInstanceServerPipeCallback, null);
		}

		//Note: This method is called on a non-UI thread.
		//Note: this seems really frail. I don't think it's industrial strength. Pipes are weak compared to sockets.
		//It was probably frail in the first place with the old vbnet impl
		private void SingleInstanceServerPipeCallback(IAsyncResult iAsyncResult)
		{
			try
			{
				_singleInstanceServer.EndWaitForConnection(iAsyncResult);

				//a bit over-engineered in case someone wants to send a script or a rom or something
				//buffer size is set to something tiny so that we are continually testing it
				var payloadBytes = new MemoryStream();
				while (true)
				{
					var bytes = new byte[16];
					int did = _singleInstanceServer.Read(bytes, 0, bytes.Length);
					payloadBytes.Write(bytes, 0, did);
					if (_singleInstanceServer.IsMessageComplete) break;
				}

				var payloadString = Encoding.ASCII.GetString(payloadBytes.GetBuffer(), 0, (int)payloadBytes.Length);
				var args = payloadString.Split('|').Select(a => Encoding.UTF8.GetString(a.HexStringToBytes())).ToArray();

				Console.WriteLine("RECEIVED SINGLE INSTANCE FORWARDED ARGS:");
				lock (_singleInstanceForwardedArgs)
					_singleInstanceForwardedArgs.Add(args);
			}
			catch (ObjectDisposedException)
			{
				// EndWaitForConnection will exception when someone calls closes the pipe before connection made
				// In that case we dont create any more pipes and just return
				// This will happen when app is closing and our pipe is closed/disposed
				return;
			}
			catch (Exception)
			{
				// ignored
			}
			finally
			{
				// Close the original pipe (we will create a new one each time)
				_singleInstanceServer.Dispose();
			}

			// Create a new pipe for next connection
			StartSingleInstanceServer();
		}

		private void SingleInstanceProcessArgs(string[] args)
		{
			//ulp. it's not clear how to handle these.
			//we only have a legacy case where we can tell the form to load a rom, if it's in a sensible condition for that.
			//er.. let's assume it's always in a sensible condition
			//in case this all sounds insanely sketchy to you, remember, the main 99% use case is double clicking roms in explorer

			//BANZAIIIIIIIIIIIIIIIIIIIIIIIIIII
			_ = LoadRom(args[0]);
		}

		private IntPtr _x11Display;
		private bool _hasXFixes;
		private readonly IntPtr[] _pointerBarriers = new IntPtr[4];

#if false
		private delegate void CaptureWithConfineDelegate(Control control, Control confineWindow);

		private static readonly Lazy<CaptureWithConfineDelegate> _captureWithConfine = new(() =>
		{
			var mi = typeof(Control).GetMethod("CaptureWithConfine", BindingFlags.Instance | BindingFlags.NonPublic);
			return (CaptureWithConfineDelegate)Delegate.CreateDelegate(typeof(CaptureWithConfineDelegate), mi!);
		});
#endif

		private void CaptureMouse(bool wantCapture)
		{
			if (wantCapture)
			{
				var fbLocation = Point.Subtract(Bounds.Location, new(PointToClient(Location)));
				fbLocation.Offset(_presentationPanel.Control.Location);
				Cursor.Clip = new(fbLocation, _presentationPanel.Control.Size);
				Cursor.Hide();
				_presentationPanel.Control.Cursor = Properties.Resources.BlankCursor.Value;
				_cursorHidden = true;
				BringToFront();

				if (Config.MainFormMouseCaptureForcesTopmost)
				{
					TopMost = true;
				}
			}
			else
			{
				Cursor.Clip = Rectangle.Empty;
				Cursor.Show();
				_presentationPanel.Control.Cursor = Cursors.Default;
				_cursorHidden = false;

				if (Config.MainFormMouseCaptureForcesTopmost)
				{
					TopMost = Config.MainFormStayOnTop;
				}
			}

			// Cursor.Clip is a no-op on Linux, so we need this too
			if (OSTailoredCode.IsUnixHost)
			{
#if true
				if (_x11Display == IntPtr.Zero)
				{
					_x11Display = XlibImports.XOpenDisplay(null);
					_hasXFixes = XlibImports.XQueryExtension(_x11Display, "XFIXES", out _, out _, out _);
					if (!_hasXFixes)
					{
						Console.Error.WriteLine("XFixes is unsupported, mouse capture will not lock the mouse cursor");
						return;
					}

					try
					{
						if (!XfixesImports.XFixesQueryVersion(_x11Display, out var major, out var minor))
						{
							Console.Error.WriteLine("Failed to query XFixes version, mouse capture will not lock the mouse cursor");
							_hasXFixes = false;
						}
						else if (major * 100 + minor < 500)
						{
							Console.Error.WriteLine($"XFixes version is not at least 5.0 (got {major}.{minor}), mouse capture will not lock the mouse cursor");
							_hasXFixes = false;
						}
					}
					catch
					{
						Console.Error.WriteLine("libXfixes.so.3 is not present, mouse capture will not lock the mouse cursor");
						_hasXFixes = false;
					}
				}

				if (_hasXFixes)
				{
					for (var i = 0; i < 4; i++)
					{
						if (_pointerBarriers[i] != IntPtr.Zero)
						{
							XfixesImports.XFixesDestroyPointerBarrier(_x11Display, _pointerBarriers[i]);
							_pointerBarriers[i] = IntPtr.Zero;
						}
					}

					if (wantCapture)
					{
						var fbLocation = Point.Subtract(Bounds.Location, new(PointToClient(Location)));
						fbLocation.Offset(_presentationPanel.Control.Location);
						var barrierRect = new Rectangle(fbLocation, _presentationPanel.Control.Size);

						// each line of the barrier rect must be a separate barrier object
						// also, the lines should span the entire screen, to avoid the cursor escaping at the corner

						var mfScreen = Screen.FromControl(this);
						var screenRect = mfScreen.Bounds;

						// left barrier
						_pointerBarriers[0] = XfixesImports.XFixesCreatePointerBarrier(
							_x11Display, Handle, barrierRect.X, screenRect.Y, barrierRect.X, screenRect.Bottom,
							XfixesImports.BarrierDirection.BarrierPositiveX, 0, IntPtr.Zero);
						// top barrier
						_pointerBarriers[1] = XfixesImports.XFixesCreatePointerBarrier(
							_x11Display, Handle, screenRect.X, barrierRect.Y, screenRect.Right, barrierRect.Y,
							XfixesImports.BarrierDirection.BarrierPositiveY, 0, IntPtr.Zero);
						// right barrier
						_pointerBarriers[2] = XfixesImports.XFixesCreatePointerBarrier(
							_x11Display, Handle, barrierRect.Right, screenRect.Y, barrierRect.Right, screenRect.Bottom,
							XfixesImports.BarrierDirection.BarrierNegativeX, 0, IntPtr.Zero);
						// bottom barrier
						_pointerBarriers[3] = XfixesImports.XFixesCreatePointerBarrier(
							_x11Display, Handle, screenRect.X, barrierRect.Bottom, screenRect.Right, barrierRect.Bottom,
							XfixesImports.BarrierDirection.BarrierNegativeY, 0, IntPtr.Zero);

						// after creating pointer barriers, warp our cursor over to the presentation panel
						_ = XlibImports.XUngrabPointer(_x11Display, XlibImports.CurrentTime); // just in case someone else has grabbed the pointer
						_ = XlibImports.XGrabPointer(_x11Display, Handle, false, 0,
							XlibImports.GrabMode.Async, XlibImports.GrabMode.Async, _presentationPanel.Control.Handle, IntPtr.Zero, XlibImports.CurrentTime);
						_ = XlibImports.XUngrabPointer(_x11Display, XlibImports.CurrentTime);
					}

					_ = XlibImports.XFlush(_x11Display);
				}
#elif false
				// approach just using XGrabPointer
				// (doesn't work, Mono won't respond to mouse buttons for whatever reason)
				if (_x11Display == IntPtr.Zero)
				{
					_x11Display = XlibImports.XOpenDisplay(null);
				}

				// always returns 1
				_ = XlibImports.XUngrabPointer(_x11Display, XlibImports.CurrentTime);

				if (wantCapture)
				{
					const XlibImports.EventMask eventMask = XlibImports.EventMask.ButtonPressMask | XlibImports.EventMask.ButtonMotionMask
						| XlibImports.EventMask.ButtonReleaseMask | XlibImports.EventMask.PointerMotionMask | XlibImports.EventMask.PointerMotionHintMask
						| XlibImports.EventMask.EnterWindowMask | XlibImports.EventMask.LeaveWindowMask | XlibImports.EventMask.FocusChangeMask;
					_ = XlibImports.XGrabPointer(_x11Display, Handle, false, eventMask, XlibImports.GrabMode.Async,
							XlibImports.GrabMode.Async, _presentationPanel.Control.Handle, IntPtr.Zero, XlibImports.CurrentTime);
				}

				_ = XlibImports.XFlush(_x11Display);
#else
				// approach using internal Mono function that ends up just using XGrabPointer
				// (doesn't work either, while Mono does respond to mouse buttons, it ends up being able to respond to the top menu bar somehow)
				// (also interacting with other windows (e.g. right click menu) cancels the capture)
				if (wantCapture)
				{
					_captureWithConfine.Value(this, _presentationPanel.Control);
				}
				else
				{
					Capture = false;
				}
#endif
			}
		}
	}
}







