using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

using Chimera.Client.Common;
using Chimera.Client.GUI.ToolExtensions;
using Chimera.Client.GUI.Properties;
using Chimera.Common;
using Chimera.Common.StringExtensions;
using Chimera.Emulation.Common;
using Chimera.WinForms.Controls;

namespace Chimera.Client.GUI
{
	public partial class TAStudio : ToolFormBase, IToolFormAutoConfig, IControlMainform
	{
		public static readonly FilesystemFilterSet TAStudioProjectsFSFilterSet = new(FilesystemFilter.TAStudioProjects);

		public static Icon ToolIcon
			=> Resources.TAStudioIcon;

		public override bool BlocksInputWhenFocused => false;

		public new IMainFormForTools MainForm => base.MainForm;

		public new IMovieSession MovieSession => base.MovieSession;

		// TODO: UI flow that conveniently allows to start from savestate
		public ITasMovie CurrentTasMovie => MovieSession.Movie as ITasMovie;

		private readonly List<TasClipboardEntry> _tasClipboard = new List<TasClipboardEntry>();
		private const string CursorColumnName = "CursorColumn";
		private const string FrameColumnName = "FrameColumn";
		private UndoHistoryForm _undoForm;
		private Timer _autosaveTimer;
		private bool _lastAutoSaveSuccess = true;

		private readonly int _defaultMainSplitDistance;
		private readonly int _defaultBranchMarkerSplitDistance;

		private bool _initialized;
		private bool _exiting;
		private bool _engaged;

		private bool CanAutoload => Settings.RecentTas.AutoLoad && !string.IsNullOrEmpty(Settings.RecentTas.MostRecent);

		/// <summary>
		/// Gets a value that separates "restore last position" logic from seeking caused by navigation.
		/// TASEditor never kills LastPositionFrame, and it only pauses on it, if it hasn't been greenzoned beforehand and middle mouse button was pressed.
		/// </summary>
		public int RestorePositionFrame { get; private set; } = -1;
		private bool _shouldMoveGreenArrow;
		private bool _seekingByEdit;

		private int SeekingTo
		{
			get => MainForm.PauseOnFrame ?? -1;
			set
			{
				if (value == -1)
					MainForm.PauseOnFrame = null;
				else
					MainForm.PauseOnFrame = value;
			}
		}

		[ConfigPersist]
		public TAStudioSettings Settings { get; set; } = new TAStudioSettings();

		public TAStudioPalette Palette => Settings.Palette;

		/// <summary>
		/// This is meant to be used by Lua.
		/// </summary>
		public bool StopRecordingOnNextEdit = true;

		public class TAStudioSettings
		{
			public enum PatternPaintModeEnum
			{
				Never, AutoFireOnly, Always,
			}

			public enum PatternSelectionEnum
			{
				Hold, AutoFire, Custom,
			}

			public TAStudioSettings()
			{
				RecentTas = new RecentFiles(8);
				AutoPause = false;
				FollowCursor = true;
				ScrollSpeed = 6;
				FollowCursorAlwaysScroll = false;
				FollowCursorScrollMethod = "near";
				// Thirty minutes. Two was the lineage's, and an autosave writes the
				// whole project and its greenzone - seconds of pause on a PS2 or a
				// DOS machine - so it should not come around before there is a
				// stretch of work worth keeping (issue #34).
				AutosaveInterval = 1800000;
				AutosaveAsBackupFile = false;
				BackupPerFileSave = false;
				EditInvisibleColumns = true;
				OldControlSchemeForBranches = false;
				LoadBranchOnDoubleClick = true;
				CopyIncludesFrameNo = false;
				AutoadjustInput = false;

				// default to taseditor fashion
				DenoteStatesWithIcons = false;
				DenoteStatesWithBGColor = true;
				DenoteMarkersWithIcons = false;
				DenoteMarkersWithBGColor = true;

				Palette = TAStudioPalette.Default;
				MoveWithMainWindow = true;
			}

			public RecentFiles RecentTas { get; set; }
			public bool AutoPause { get; set; }
			public bool AutoRestoreLastPosition { get; set; }
			public bool FollowCursor { get; set; }
			public int ScrollSpeed { get; set; }
			public bool FollowCursorAlwaysScroll { get; set; }
			public string FollowCursorScrollMethod { get; set; }
			public uint AutosaveInterval { get; set; }
			public bool AutosaveAsBackupFile { get; set; }
			public bool BackupPerFileSave { get; set; }
			public bool OldControlSchemeForBranches { get; set; }
			public bool LoadBranchOnDoubleClick { get; set; }
			public bool DenoteStatesWithIcons { get; set; }
			public bool DenoteStatesWithBGColor { get; set; }
			public bool DenoteMarkersWithIcons { get; set; }
			public bool DenoteMarkersWithBGColor { get; set; }
			public bool EditInvisibleColumns { get; set; }
			public int MainVerticalSplitDistance { get; set; }
			public int BranchMarkerSplitDistance { get; set; }

			/// <summary>
			/// The sidebar's width, which is what a person actually chose - the
			/// splitter DISTANCE above is the roll's width, and the roll is the
			/// panel that grows with the window. Saved from a maximised window,
			/// that distance was the width of a maximised roll, and restored into
			/// a normal-sized window it left the sidebar its 25-pixel minimum,
			/// which no amount of maximising afterwards undid because the sidebar
			/// is the fixed panel (issue #15). Zero means never saved; the
			/// distance is then used, as before.
			/// </summary>
			public int SidebarWidth { get; set; }

			/// <summary>
			/// Where the branches end and the markers begin, as a share of the
			/// sidebar's height - the same fix for the same reason, since the
			/// sidebar's height follows the window's. Zero means never saved.
			/// </summary>
			public double BranchMarkerSplitRatio { get; set; }
			public bool BindMarkersToInput { get; set; }
			public bool CopyIncludesFrameNo { get; set; }
			public bool AutoadjustInput { get; set; } // Currently unsupported due to being broken
			public TAStudioPalette Palette { get; set; }
			public int MaxUndoSteps { get; set; } = 1000;

			/// <summary>
			/// Whether this window and the main window move as one. They are two
			/// halves of a session, so dragging either normally takes both; hold
			/// SHIFT while dragging to move one alone, which is also how the
			/// offset between them is changed.
			/// </summary>
			public bool MoveWithMainWindow { get; set; }
			public int RewindStep { get; set; } = 1;
			public int RewindStepFast { get; set; } = 4;
			public bool ScrollSync { get; set; } = true;
			public bool StatesForMarkers { get; set; } = true;
			public PatternPaintModeEnum PatternPaintMode { get; set; } = TAStudioSettings.PatternPaintModeEnum.Never;
			public PatternSelectionEnum PatternSelection { get; set; } = TAStudioSettings.PatternSelectionEnum.Hold;
			public Font TasViewFont { get; set; } = new Font("Arial", 8.25F, FontStyle.Bold, GraphicsUnit.Point, 0);

			public Rectangle SettingsWindow { get; set; }
			public Rectangle UndoHistoryWindow { get; set; }
		}

		public class MovieClientSettings
		{
			public bool HorizontalOrientation { get; set; } = false;
			public bool HideWasLagFrames { get; set; } = false;
			public int LagFramesToHide { get; set; } = 0;
			public RollColumns[] Columns { get; set; } = [ ];

			public AutoPatternBool[] BoolPatterns { get; set; }
			public AutoPatternAxis[] AxisPatterns { get; set; }
		}

		public class AllSettings
		{
			public TAStudioSettings GeneralClientSettings;

			public MovieClientSettings MovieSettings;




		}

		private MovieClientSettings _movieSettings = new();

		public TAStudio()
		{
			InitializeComponent();
			if (OSTailoredCode.IsUnixHost)
			{
				// workaround for https://github.com/mono/mono/issues/12644
				ColumnRightClickMenu.Items.Insert(0, new ToolStripMenuItemEx { Text = "(Dismiss Menu)" }); // don't even need to attach any behaviour, since clicking anything will dismiss the menu first
				ColumnRightClickMenu.Items.Insert(1, new ToolStripSeparatorEx());

				// see https://github.com/TASEmulators/BizHawk/issues/4779#issuecomment-4800440717
				this.Shown += (_, _) => RepositionRolls();
			}
			_tasViewPanel = MainVertialSplit.Panel1;
			// The built-in scroll feature of .NET's scrollable controls is non-functional. So, custom scroll behavior!
			_tasViewHBar.Dock = DockStyle.Bottom;
			_tasViewVBar.Dock = DockStyle.Right;
			_tasViewHBar.Scroll += (_, _) => RepositionRolls();
			_tasViewVBar.Scroll += (_, _) => RepositionRolls();
			_tasViewPanel.Controls.Add(_tasViewHBar);
			_tasViewPanel.Controls.Add(_tasViewVBar);

			recentMacrosToolStripMenuItem.Image = Resources.Recent;
			Icon = ToolIcon;

			_defaultMainSplitDistance = MainVertialSplit.SplitterDistance;
			_defaultBranchMarkerSplitDistance = BranchesMarkersSplit.SplitterDistance;

			// TODO: show this at all times or hide it when saving is done?
			ProgressBar.Visible = false;

			WantsToControlStopMovie = true;
			WantsToControlRestartMovie = true;
			TasPlaybackBox.Tastudio = this;
			MarkerControl.Tastudio = this;
			BookMarkControl.Tastudio = this;
		}

		private void Tastudio_Load(object sender, EventArgs e)
		{
			RestoreMainSplit();
			_activeInputRoll = MakeInputRoll(); // first because stuff in Engage assumes we have at least one

			if (!Engage())
			{
				Close();
				return;
			}
			GenerateIcons();

			_autosaveTimer = new Timer(components);
			_autosaveTimer.Tick += AutosaveTimerEventProcessor;
			ScheduleAutoSave(Settings.AutosaveInterval);

			RestoreBranchMarkerSplit();

			HandleHotkeyUpdate();

			RefreshDialog();
			PlaceBesideMainWindow();
			LinkWindowMovement();
			_initialized = true;
		}

		// ---- the pair moves as one ------------------------------------------------

		/// <summary>guards the two Move handlers from answering each other</summary>
		private bool _movingPair;

		/// <summary>where each window was when we last looked, to take a delta from</summary>
		private Point _lastMain;

		private Form MainWindow => MainForm as Form;

		/// <summary>
		/// Makes this window and the main window move together: drag either and
		/// the other keeps its place beside it.
		///
		/// They are two halves of one session - a project is open exactly when
		/// TAStudio is - and on a desktop they are read as one thing, so dragging
		/// the emulator out from under its input log is almost never what someone
		/// meant. Holding SHIFT moves one alone, which is how the offset between
		/// them is set in the first place.
		/// </summary>
		private void LinkWindowMovement()
		{
			var main = MainWindow;
			if (main is null) return;

			_lastMain = main.Location;
			_lastMainRight = main.Right;
			main.Move += FollowFromMainWindow;
			main.SizeChanged += FollowMainWindowResize;
			FormClosed += (_, _) =>
			{
				main.Move -= FollowFromMainWindow;
				main.SizeChanged -= FollowMainWindowResize;
			};
		}

		/// <summary>the main window's right edge when we last looked, to tell whether we were docked to it</summary>
		private int _lastMainRight;

		/// <summary>
		/// The main window resizes itself to whatever machine booted - a PSP is
		/// not a NES - and a window that grew rightwards would then be sitting on
		/// top of this one. If we were against its right edge, we stay against
		/// it; if the user had moved us somewhere of their own, we stay there.
		/// </summary>
		private void FollowMainWindowResize(object sender, EventArgs e)
		{
			var main = MainWindow;
			if (main is null) return;
			if (!MainWindowHasRealPosition(main)) return;
			var wasDocked = Math.Abs(Left - _lastMainRight) <= UIHelper.ScaleX(12);
			_lastMainRight = main.Right;
			if (!wasDocked || !ShouldMovePair(main)) return;

			_movingPair = true;
			try
			{
				PlaceBesideMainWindow();
				_lastMain = main.Location;
			}
			finally
			{
				_movingPair = false;
			}
		}

		private void FollowFromMainWindow(object sender, EventArgs e)
		{
			var main = MainWindow;
			if (main is null) return;
			// A minimised main window reports the (-32000,-32000) sentinel (and
			// per MainForm can read it even while WindowState still says Normal).
			// Recording that as our reference would make restoring the main
			// window fling TAStudio ~32000px off-screen - it "never comes back".
			if (!MainWindowHasRealPosition(main)) return;
			var delta = new Size(main.Location.X - _lastMain.X, main.Location.Y - _lastMain.Y);
			_lastMain = main.Location;
			if (!ShouldMovePair(main)) return;

			_movingPair = true;
			try
			{
				Location += delta;
			}
			finally
			{
				_movingPair = false;
			}
		}

		/// <summary>
		/// A minimised main window reports the (-32000,-32000) sentinel - and per
		/// MainForm can read it even while WindowState still says Normal. Its
		/// position and size mean nothing there, so we neither follow it nor take
		/// it as our reference point (which is what left TAStudio off-screen after
		/// the main window was minimised and restored).
		/// </summary>
		private static bool MainWindowHasRealPosition(Form main)
			=> main.WindowState is FormWindowState.Normal
				&& main.Location is not { X: -32000, Y: -32000 };

		/// <summary>
		/// The main window's move is followed unless it was one WE made, the user
		/// asked for one window alone with shift, the feature is off, or either
		/// window is minimised or maximised - where a position is not something
		/// the user is choosing.
		///
		/// It follows in ONE direction only. Dragging the main window brings
		/// TAStudio along, because the main window is the thing being put
		/// somewhere and TAStudio is its shoulder. Dragging TAStudio moves
		/// TAStudio: that is somebody arranging the pair, and dragging the main
		/// window out from under them would be the opposite of what they asked.
		/// </summary>
		private bool ShouldMovePair(Form main)
			=> !_movingPair
				&& _initialized
				&& Settings.MoveWithMainWindow
				&& (Control.ModifierKeys & Keys.Shift) is 0
				&& WindowState is FormWindowState.Normal
				&& main.WindowState is FormWindowState.Normal;

		/// <summary>
		/// Opens at the main window's right shoulder, tops aligned.
		///
		/// TAStudio is the project's window - one is open exactly when the other
		/// is - so the pair should land as a pair rather than TAStudio appearing
		/// centred on top of the screen it is meant to sit beside. A saved
		/// position would put it back wherever it was last dragged, which for a
		/// window that is only ever opened with its main window is the wrong
		/// memory to keep; it is placed on every open.
		///
		/// If there is no room to the right (a narrow screen, or a main window
		/// against the right edge), it goes as far right as the screen allows
		/// rather than off it.
		/// </summary>
		private void PlaceBesideMainWindow()
		{
			if (MainForm is not Form main || main.WindowState is not FormWindowState.Normal) return;

			var screen = Screen.FromControl(main).WorkingArea;
			var x = main.Right;
			if (x + Width > screen.Right) x = Math.Max(screen.Left, screen.Right - Width);
			var y = Math.Max(screen.Top, Math.Min(main.Top, screen.Bottom - Height));

			StartPosition = FormStartPosition.Manual;
			Location = new Point(x, y);
		}

		public override void HandleHotkeyUpdate()
		{
			UndoMenuItem.ShortcutKeyDisplayString = Config.HotkeyBindings["Undo"];
			RedoMenuItem.ShortcutKeyDisplayString = Config.HotkeyBindings["Redo"];
			SelectBetweenMarkersMenuItem.ShortcutKeyDisplayString = Config.HotkeyBindings["Sel. bet. Markers"];
			SelectAllMenuItem.ShortcutKeyDisplayString = Config.HotkeyBindings["Select All"];
			ReselectClipboardMenuItem.ShortcutKeyDisplayString = Config.HotkeyBindings["Reselect Clip."];
			GoToFrameMenuItem.ShortcutKeyDisplayString = Config.HotkeyBindings["Seek To..."];
			ClearFramesMenuItem.ShortcutKeyDisplayString = Config.HotkeyBindings["Clear Frames"];
			DeleteFramesMenuItem.ShortcutKeyDisplayString = Config.HotkeyBindings["Delete Frames"];
			InsertFrameMenuItem.ShortcutKeyDisplayString = Config.HotkeyBindings["Insert Frame"];
			InsertNumFramesMenuItem.ShortcutKeyDisplayString = Config.HotkeyBindings["Insert # Frames"];
			CloneFramesMenuItem.ShortcutKeyDisplayString = Config.HotkeyBindings["Clone Frames"];
			CloneFramesXTimesMenuItem.ShortcutKeyDisplayString = Config.HotkeyBindings["Clone # Times"];

			SelectBetweenMarkersContextMenuItem.ShortcutKeyDisplayString = Config.HotkeyBindings["Sel. bet. Markers"];
			ClearContextMenuItem.ShortcutKeyDisplayString = Config.HotkeyBindings["Clear Frames"];
			DeleteFramesContextMenuItem.ShortcutKeyDisplayString = Config.HotkeyBindings["Delete Frames"];
			InsertFrameContextMenuItem.ShortcutKeyDisplayString = Config.HotkeyBindings["Insert Frame"];
			InsertNumFramesContextMenuItem.ShortcutKeyDisplayString = Config.HotkeyBindings["Insert # Frames"];
			CloneContextMenuItem.ShortcutKeyDisplayString = Config.HotkeyBindings["Clone Frames"];
			CloneXTimesContextMenuItem.ShortcutKeyDisplayString = Config.HotkeyBindings["Clone # Times"];
			PasteInsertMenuItem.ShortcutKeyDisplayString = Config.HotkeyBindings["Paste Insert"];

			TasPlaybackBox.UpdateHotkeyTooltips(Config);
			BookMarkControl.UpdateHotkeyTooltips(Config);
			MarkerControl.UpdateHotkeyTooltips(Config);
		}

		private bool LoadMostRecentOrStartNew()
		{
			return LoadFileWithFallback(Settings.RecentTas.MostRecent);
		}

		private bool Engage()
		{
			_engaged = false;
			MainForm.PauseOnFrame = null;
			MainForm.PauseEmulator();
			bool success = false;

			// Nag if inaccurate core, but not if auto-loading or movie is already loaded
			if (!CanAutoload && MovieSession.Movie.NotActive())
			{
				// Nag but allow the user to continue anyway, so ignore the return value
				MainForm.EnsureCoreIsAccurate();
			}

			// Start Scenario 1: the active project (chimera knows no other kind of movie)
			if (MovieSession.Movie.IsActive() && MovieSession.Movie is ITasMovie)
			{
				success = LoadMovie(CurrentTasMovie, gotoFrame: Emulator.Frame);
				if (!success)
				{
					success = StartNewTasMovie();
				}
			}

			// Start Scenario 3: No movie, but user wants to autoload their last project
			else if (CanAutoload)
			{
				success = LoadMostRecentOrStartNew();
			}

			// Start Scenario 4: No movie, default behavior of engaging tastudio with a new default project
			else
			{
				success = StartNewTasMovie();
			}

			// Attempts to load failed, abort
			if (!success)
			{
				return false;
			}

			MainForm.AddOnScreenMessage("TAStudio engaged");
			MovieSession.ReadOnly = true;
			SetSplicer();

			_engaged = true;
			return true;
		}

		private void ScheduleAutoSave(uint secondsUntil)
		{
			_autosaveTimer.Stop();
			if (secondsUntil != 0)
			{
				_autosaveTimer.Interval = (int)secondsUntil;
				_autosaveTimer.Start();
			}
		}

		private void AutosaveTimerEventProcessor(object sender, EventArgs e)
		{
			if (CurrentTasMovie == null)
			{
				return;
			}

			if (!CurrentTasMovie.Changes || Settings.AutosaveInterval == 0
				|| CurrentTasMovie.Filename == DefaultTasProjName())
			{
				return;
			}

			FileWriteResult saveResult = Settings.AutosaveAsBackupFile
				? SaveTas(saveBackup: true)
				: SaveTas();

			if (saveResult.IsError && _lastAutoSaveSuccess)
			{
				// Should we alert the user?
				// Let's try again once after a bit, then alert if it fails again.
				ScheduleAutoSave(60);
			}
			else if (saveResult.IsError)
			{
				_autosaveTimer.Stop();
				bool tryAgain = DialogController.ShowMessageBox2(
					$"Failed to auto-save. {saveResult.UserFriendlyErrorMessage()}\n{saveResult.Exception.Message}\n\nTry again?",
					"Error");
				if (tryAgain)
				{
					AutosaveTimerEventProcessor(null, EventArgs.Empty);
					return;
				}
				ScheduleAutoSave(Settings.AutosaveInterval);
			}
			else
			{
				ScheduleAutoSave(Settings.AutosaveInterval);
			}

			_lastAutoSaveSuccess = !saveResult.IsError;
		}

		private void SetUpColumns()
		{
			for (int i = 0; i < _inputRolls.Count; i++)
			{
				InputRoll roll = _inputRolls[i];
				MakeDefaultColumns(roll);
				UpdateInputRollDefinition(roll);
			}
		}

		private IEnumerable<RollColumn> GetDefaultHiddenColums(InputRoll roll)
		{
			var columnsToHide = roll.AllColumns
				.Where(c =>
					// todo: make a proper user editable list?
					c.Name == "Power"
					|| c.Name == "Reset"
					|| c.Name == "Light Sensor"
					|| c.Name == "Disc Select"
					|| c.Name == "Disk Index"
					|| c.Name == "Next Drive"
					|| c.Name == "Next Slot"
					|| c.Name == "Insert Disk"
					|| c.Name == "Eject Disk"
					|| c.Name.StartsWithOrdinal("Tilt")
					|| c.Name.StartsWithOrdinal("Key ")
					|| c.Name.StartsWithOrdinal("Open")
					|| c.Name.StartsWithOrdinal("Close")
					|| c.Name.EndsWithOrdinal("Tape")
					|| c.Name.EndsWithOrdinal("Disk")
					|| c.Name.EndsWithOrdinal("Block")
					|| c.Name.EndsWithOrdinal("Status"));

			return columnsToHide;
		}

		private void MakeDefaultColumns(InputRoll roll)
		{
			roll.AllColumns.Clear();

			// All final widths are computed in UpdateColumnWidths, after column creation.
			roll.AllColumns.Add(new(name: CursorColumnName, widthUnscaled: 10, text: string.Empty));
			roll.AllColumns.Add(new(name: FrameColumnName, widthUnscaled: 10, text: "Frame#")
			{
				Rotatable = true,
			});

			List<RollColumn> columns = new(); // add to list first then AddRange to avoid 100 refreshes
			foreach ((string name, string mnemonic, int maxLength) in MnemonicMap())
			{
				columns.Add(new(
					name: name,
					widthUnscaled: 10,
					text: mnemonic)
				{
					Rotatable = ControllerType.Axes.ContainsKey(name),
				});
			}
			roll.AllColumns.AddRange(columns);

			foreach (var column in GetDefaultHiddenColums(roll))
			{
				column.Visible = false;
			}

			foreach (var column in roll.VisibleColumns)
			{
				if (InputManager.StickyHoldController.IsSticky(column.Name) || InputManager.StickyAutofireController.IsSticky(column.Name))
				{
					column.Emphasis = true;
				}
			}

			UpdateColumnWidths(roll);
		}

		private void UpdateColumnWidths(InputRoll roll)
		{
			int minColumnWidth = UIHelper.UnscaleX(roll.GetCellWidthForText("W")); // this gives the same width as the old hard-coded width, with default font
			foreach ((string name, _, int maxLength) in MnemonicMap())
			{
				RollColumn col = roll.AllColumns[name];

				int textLength = col.Text.Length > 1
					? UIHelper.UnscaleX(roll.GetCellWidthForText(col.Text))
					: minColumnWidth; // keep single-character columns a uniform width, for prettyness
				int inputLength = UIHelper.UnscaleX(roll.GetCellWidthForText("".PadRight(maxLength, '8')));

				col.VerticalWidth = Math.Max(textLength, inputLength);
				col.HorizontalHeight = inputLength;
			}

			roll.AllColumns[CursorColumnName].VerticalWidth
				= roll.AllColumns[CursorColumnName].HorizontalHeight
				= UIHelper.UnscaleX(roll.GetCellWidthForText("10") - 4);
			roll.AllColumns[FrameColumnName].VerticalWidth
				= roll.AllColumns[FrameColumnName].HorizontalHeight
				= UIHelper.UnscaleX(roll.GetCellWidthForText("0000000") + 7);

			roll.AllColumns.ColumnsChanged();
		}

		private void SetupCustomPatterns()
		{
			// custom autofire patterns to allow configuring a unique pattern for each button or axis
			_movieSettings.BoolPatterns = new AutoPatternBool[ControllerType.BoolButtons.Count];
			_movieSettings.AxisPatterns = new AutoPatternAxis[ControllerType.Axes.Count];

			for (int i = 0; i < _movieSettings.BoolPatterns.Length; i++)
			{
				// standard 1 on 1 off autofire pattern
				_movieSettings.BoolPatterns[i] = new AutoPatternBool(1, 1);
			}

			for (int i = 0; i < _movieSettings.AxisPatterns.Length; i++)
			{
				// autohold pattern with the maximum axis range as hold value (bit arbitrary)
				var axisSpec = ControllerType.Axes[ControllerType.Axes[i]];
				_movieSettings.AxisPatterns[i] = new AutoPatternAxis([ axisSpec.Range.EndInclusive ]);
			}
		}

		/// <remarks>for Lua</remarks>
		public void AddColumn(string name, string text, int widthUnscaled, int rollIndex)
		{
			for (int i = 0; i < _inputRolls.Count; i++)
			{
				RollColumn/*?*/ col = _inputRolls[i].AllColumns[name];
				if (col == null)
				{
					col = new(name: name, widthUnscaled: widthUnscaled, text: text);
					_inputRolls[i].AllColumns.Add(col);
				}
				else
				{
					col.VerticalWidth = col.HorizontalHeight = widthUnscaled;
					col.Text = text;
				}
				col.Visible = i == rollIndex;
				_inputRolls[i].AllColumns.ColumnsChanged();
				_inputRolls[i].Refresh();
			}

			SetUpToolStripColumns();
		}

		public void LoadBranchByIndex(int index) => BookMarkControl.LoadBranchExternal(index);

		public void ClearFramesExternal()
			=> ClearFramesMenuItem_Click(null, EventArgs.Empty);

		public void InsertFrameExternal()
			=> InsertFrameMenuItem_Click(null, EventArgs.Empty);

		public void InsertNumFramesExternal()
			=> InsertNumFramesMenuItem_Click(null, EventArgs.Empty);

		public void DeleteFramesExternal()
			=> DeleteFramesMenuItem_Click(null, EventArgs.Empty);

		public void CloneFramesExternal()
			=> CloneFramesMenuItem_Click(null, EventArgs.Empty);

		public void CloneFramesXTimesExternal()
			=> CloneFramesXTimesMenuItem_Click(null, EventArgs.Empty);

		public void PasteInsertExternal() => MaybePasteFromClipboard(overwriteSelection: false);

		public void UndoExternal()
			=> UndoMenuItem_Click(null, EventArgs.Empty);

		public void RedoExternal()
			=> RedoMenuItem_Click(null, EventArgs.Empty);

		public void SelectBetweenMarkersExternal()
			=> SelectBetweenMarkersMenuItem_Click(null, EventArgs.Empty);

		public void SelectAllExternal()
			=> SelectAllMenuItem_Click(null, EventArgs.Empty);

		public void ReselectClipboardExternal()
			=> ReselectClipboardMenuItem_Click(null, EventArgs.Empty);

		public void SelectCurrentFrame()
		{
			_activeInputRoll.DeselectAll();
			_activeInputRoll.SelectRow(Emulator.Frame, true);

			RefreshDialog();
		}

		public void SeekToSelectedFrame()
		{
			if (!AnyRowsSelected) return;
			GoToFrame(FirstSelectedRowIndex);
		}

		public void SeekToUserSpecifiedFrame() => GoToFrameMenuItem_Click(null, EventArgs.Empty);

		public IMovieController GetBranchInput(string branchId, int frame)
		{
			var branch = Guid.TryParseExact(branchId, format: "D", out var parsed)
				? CurrentTasMovie.Branches.FirstOrDefault(b => b.Uuid == parsed)
				: null;
			if (branch == null || frame >= branch.InputLog.Count)
			{
				return null;
			}

			var controller = MovieSession.GenerateMovieController();
			controller.SetFromMnemonic(branch.InputLog[frame]);
			return controller;
		}

		private bool LoadMovie(ITasMovie tasMovie, bool startsFromSavestate = false, int gotoFrame = 0)
		{
			_engaged = false;

			if (!StartNewMovieWrapper(tasMovie, isNew: false))
			{
				return false;
			}

			_engaged = true;
			Settings.RecentTas.Add(CurrentTasMovie.Filename); // only add if it did load
			if (CurrentTasMovie.DroppedCacheNote is { } droppedCache) MainForm.AddOnScreenMessage(droppedCache);

			if (startsFromSavestate)
			{
				GoToFrame(0);
			}
			else if (gotoFrame > 0)
			{
				GoToFrame(gotoFrame);
			}
			else
			{
				GoToFrame(CurrentTasMovie.TasSession.CurrentFrame);
			}

			// clear all selections
			_activeInputRoll.DeselectAll();
			BookMarkControl.Restart();
			MarkerControl.Restart();

			RefreshDialog();
			return true;
		}

		private bool StartNewTasMovie()
		{
			if (!AskSaveChanges())
			{
				return false;
			}

			if (Game.IsNullInstance()) throw new InvalidOperationException("how is TAStudio open with no game loaded? please report this including as much detail as possible");

			var filename = DefaultTasProjName(); // TODO don't do this, take over any mainform actions that can crash without a filename
			var tasMovie = (ITasMovie)MovieSession.Get(filename);
			tasMovie.Author = Config.DefaultAuthor;

			bool success = StartNewMovieWrapper(tasMovie, isNew: true);

			if (success)
			{
				// clear all selections
				_activeInputRoll.DeselectAll();
				BookMarkControl.Restart();
				MarkerControl.Restart();
				RefreshDialog();
			}

			return success;
		}

		private bool StartNewMovieWrapper(ITasMovie movie, bool isNew)
		{
			_initializing = true;

			movie.ClientSettingsForSave = () =>
			{
				_movieSettings.Columns = _inputRolls.Select(static r => r.AllColumns).ToArray();
				return ConfigService.SaveWithType(_movieSettings);
			};
			movie.BindMarkersToInput = Settings.BindMarkersToInput;
			movie.GreenzoneInvalidated = (f) => _ = FrameEdited(f);
			movie.ChangeLog.MaxSteps = Settings.MaxUndoSteps;

			movie.ChangesChanged += TasMovie_OnChangesChanged;
			System.Collections.Specialized.NotifyCollectionChangedEventHandler refreshOnMarker = (_, _) => RefreshDialog();
			movie.Markers.CollectionChanged += refreshOnMarker;
			this.Disposed += (s, e) =>
			{
				movie.ChangesChanged -= TasMovie_OnChangesChanged;
				movie.Markers.CollectionChanged -= refreshOnMarker;
			};

			SuspendLayout();
			WantsToControlStopMovie = false;
			bool result = ReferenceEquals(CurrentTasMovie, movie) || MainForm.StartNewMovie(movie, isNew);
			WantsToControlStopMovie = true;
			ResumeLayout();
			if (result)
			{
				// Initialize stuff.
				RestorePositionFrame = -1;
				_lastRecordAction = -1;
				_doPause = false;
				BookMarkControl.ClearBackupBranch();

				StopSeeking();

				BookMarkControl.UpdateTextColumnWidth();
				MarkerControl.UpdateTextColumnWidth();
				TastudioPlayMode();
				UpdateWindowTitle();
				bool hasClientSettings = CurrentTasMovie.LoadedClientSettings != null;
				if (hasClientSettings)
				{
					object settings = ConfigService.LoadWithType(CurrentTasMovie.LoadedClientSettings);

					if (settings is InputRoll.InputRollSettings inputRollSettings)
					{
						// Old movie, 2.11 or earlier
						RemoveAllRolls();
						InputRoll roll = _activeInputRoll = MakeInputRoll();
						roll.LoadColumns(inputRollSettings.Columns);
						roll.HorizontalOrientation = _movieSettings.HorizontalOrientation = inputRollSettings.HorizontalOrientation;
						roll.LagFramesToHide = _movieSettings.LagFramesToHide = inputRollSettings.LagFramesToHide;
						roll.HideWasLagFrames = _movieSettings.HideWasLagFrames = inputRollSettings.HideWasLagFrames;
						UpdateColumnWidths(roll);
						UpdateInputRollDefinition(roll);
					}
					else if (settings is MovieClientSettings clientSettings)
					{
						_movieSettings = clientSettings;
						if (_movieSettings.Columns.Length == 0)
						{
							// This will happen if the movie was a build between 2.11 and 2.11.1
							_movieSettings.Columns = _inputRolls.Select(static t => t.AllColumns).ToArray();
						}
						MakeInputRollsFromSettings();
					}
					else
					{
						this.DialogController.ShowMessageBox("Failed to load the movie's client settings. Movie is still usable.", "Warning", EMsgBoxIcon.Warning);
						hasClientSettings = false;
					}
				}
				if (!hasClientSettings)
				{
					RemoveAllRolls();
					_activeInputRoll = MakeInputRoll();
					SetUpColumns();
					SetupCustomPatterns();
				}
				SetUpToolStripColumns();
				UpdateAutoFire();
			}

			_initializing = false;

			return result;
		}

		private void DummyLoadProject(string path)
		{
			if (AskSaveChanges())
			{
				LoadFileWithFallback(path);
			}
		}

		private bool LoadFileWithFallback(string path)
		{
			bool movieLoadSucceeded = false;

			if (File.Exists(path))
			{
				var movie = MovieSession.Get(path, loadMovie: true);
				if (movie is ITasMovie tasMovie)
				{
					movieLoadSucceeded = LoadMovie(tasMovie);
				}
			}

			if (!movieLoadSucceeded)
			{
				Settings.RecentTas.HandleLoadError(this, path: path);
				movieLoadSucceeded = StartNewTasMovie();
				_engaged = true;
			}

			return movieLoadSucceeded;
		}

		private void DummyLoadMacro(string path)
		{
			if (!AnyRowsSelected)
			{
				return;
			}

			var loadZone = MovieZone.Load(path, MainForm, CurrentTasMovie);
			loadZone?.PlaceZone(CurrentTasMovie, FirstSelectedRowIndex);
		}

		private void TastudioToggleReadOnly()
		{
			TasPlaybackBox.RecordingMode = !TasPlaybackBox.RecordingMode;
			WasRecording = TasPlaybackBox.RecordingMode; // hard reset at manual click and hotkey
		}

		private void TastudioPlayMode(bool resetWasRecording = false)
		{
			TasPlaybackBox.RecordingMode = false;

			// once user started editing, rec mode is unsafe
			if (resetWasRecording)
			{
				WasRecording = TasPlaybackBox.RecordingMode;
			}
		}

		private void TastudioRecordMode(bool resetWasRecording = false)
		{
			TasPlaybackBox.RecordingMode = true;

			if (resetWasRecording)
			{
				WasRecording = TasPlaybackBox.RecordingMode;
			}
		}

		private void TastudioStopMovie()
		{
			MovieSession.StopMovie(false);
			MainForm.SetMainformMovieInfo();
		}

		private void Disengage()
		{
			_engaged = false;
			MainForm.PauseOnFrame = null;
			MainForm.AddOnScreenMessage("TAStudio disengaged");
			WantsToControlRewind = false;
		}

		// The stand-in name an unwritten project carries (MovieService owns it,
		// because the frontend hands new projects the same one): a movie filed
		// under it saves by ASKING where it goes.
		private string DefaultTasProjName()
			=> MovieService.UnsavedProjectPath(Config.PathEntries);

		// Used for things like SaveFile dialogs to suggest a name to the user
		private string SuggestedTasProjName()
		{
			return Path.Combine(
				Config.PathEntries.MovieAbsolutePath(),
				$"{Game.FilesystemSafeName()}.{MovieService.TasMovieExtension}");
		}

		private void DisplayMessageIfFailed(Func<FileWriteResult> action, string message)
		{
			FileWriteResult result = action();
			if (result.IsError)
			{
				DialogController.ShowMessageBox(
					$"{message}\n{result.UserFriendlyErrorMessage()}\n{result.Exception.Message}",
					"Error",
					EMsgBoxIcon.Error);
			}
		}

		private FileWriteResult SaveTas(bool saveBackup = false)
		{
			if (string.IsNullOrEmpty(CurrentTasMovie.Filename) || CurrentTasMovie.Filename == DefaultTasProjName())
			{
				return SaveAsTas();
			}

			_autosaveTimer.Stop();
			MessageStatusLabel.Text = saveBackup
				? "Saving backup..."
				: "Saving...";
			MessageStatusLabel.Owner.Update();
			Cursor = Cursors.WaitCursor;

			IMovie movieToSave = CurrentTasMovie;

			FileWriteResult result;
			// the greenzone is the wait: hundreds of megabytes of states through
			// zstd, and the window shows them going
			using (ProgressDialog.Begin(this, saveBackup ? "Saving backup" : "Saving project"))
			{
				if (saveBackup)
					result = movieToSave.SaveBackup();
				else
					result = movieToSave.Save();
			}

			if (!result.IsError)
			{
				MessageStatusLabel.Text = saveBackup
					? $"Backup .{MovieService.TasMovieExtension} saved to \"Movie backups\" path."
					: "File saved.";
			}
			else
			{
				MessageStatusLabel.Text = "Failed to save movie.";
			}

			Cursor = Cursors.Default;
			ScheduleAutoSave(Settings.AutosaveInterval);
			return result;
		}

		private FileWriteResult SaveAsTas()
		{
			_autosaveTimer.Stop();

			var filename = CurrentTasMovie.Filename;
			if (string.IsNullOrWhiteSpace(filename) || filename == DefaultTasProjName())
			{
				filename = SuggestedTasProjName();
			}

			var fileInfo = SaveFileDialog(
				currentFile: filename,
				path: Config!.PathEntries.MovieAbsolutePath(),
				TAStudioProjectsFSFilterSet,
				this);

			FileWriteResult saveResult = null;
			if (fileInfo != null)
			{
				MessageStatusLabel.Text = "Saving...";
				MessageStatusLabel.Owner.Update();
				Cursor = Cursors.WaitCursor;
				CurrentTasMovie.Filename = fileInfo.FullName;
				saveResult = CurrentTasMovie.Save();
				Settings.RecentTas.Add(CurrentTasMovie.Filename);
				Cursor = Cursors.Default;

				if (!saveResult.IsError)
				{
					MessageStatusLabel.Text = "File saved.";
					ScheduleAutoSave(Settings.AutosaveInterval);
				}
				else
				{
					MessageStatusLabel.Text = "Failed to save.";
				}
			}

			UpdateWindowTitle(); // changing the movie's filename does not flag changes, so we need to ensure the window title is always updated
			MainForm.UpdateWindowTitle();

			if (fileInfo != null) return saveResult;
			else return new(); // user cancelled, so we were successful in not saving
		}

		protected override string WindowTitle
			=> CurrentTasMovie == null
				? "TAStudio"
				: CurrentTasMovie.Changes
					? $"TAStudio - {CurrentTasMovie.Name}*"
					: $"TAStudio - {CurrentTasMovie.Name}";

		protected override string WindowTitleStatic => "TAStudio";

		public IEnumerable<int> GetSelection() => _activeInputRoll.SelectedRows;

		public void RefreshDialog(bool refreshTasView = true, bool refreshBranches = true)
		{
			if (_exiting)
			{
				return;
			}

			if (refreshTasView)
			{
				SetTasViewRowCount();
			}

			MarkerControl?.UpdateValues();

			if (refreshBranches)
			{
				BookMarkControl?.UpdateValues();
			}

			if (_undoForm != null && !_undoForm.IsDisposed)
			{
				_undoForm.UpdateValues();
			}
		}

		private void SetTasViewRowCount()
		{
			_inputRolls.ForEach(r => r.RowCount = CurrentTasMovie.InputLogLength + 1);
			_lastRefresh = Emulator.Frame;
		}

		/// <summary>
		/// The frame the history would put us on to reach <paramref name="frame"/>:
		/// the one BEFORE it, so whatever follows can advance once and have a
		/// framebuffer. Asking does not move the machine.
		/// </summary>
		private int PriorStateForFramebuffer(int frame)
			=> CurrentTasMovie.States.Nearest(frame > 0 ? frame - 1 : 0);

		/// <summary>Puts the machine on a frame the history holds.</summary>
		public void LoadStateAt(int frame)
		{
			// Backwards a short way - which is what rewinding is - undoes the
			// frames between here and there, and costs those frames. Restoring
			// costs an anchor and every step taken since it, to undo one frame's
			// work. The walk stops where the reverse deltas stop and never goes
			// further than the near band, so a longer jump falls through to the
			// restore rather than paying to discover it should have.
			if (frame < Emulator.Frame && CurrentTasMovie.States.RewindTo(frame, Emulator.Frame) == frame)
			{
				AfterStateLoaded(frame, null);
				return;
			}
			if (!CurrentTasMovie.States.RestoreTo(frame)) return;
			AfterStateLoaded(frame, null);
		}

		/// <summary>
		/// Puts the machine on a branch's OWN state. A branch does not go through
		/// the history: it keeps a whole state of its own, so that reaching one
		/// never depends on a history that coarsens and evicts around it
		/// (docs/state-manager.md).
		/// </summary>
		public void LoadBranchState(TasBranch branch, int branchIndex)
		{
			StatableEmulator.LoadStateBinary(new BinaryReader(new MemoryStream(branch.CoreData, false)));
			AfterStateLoaded(branch.Frame, branchIndex);
		}

		private void AfterStateLoaded(int frame, int? branchIndex)
		{
			if (frame == 0 && CurrentTasMovie.StartsFromSavestate)
			{
				Emulator.ResetCounters();
			}

			if (branchIndex.HasValue)
			{
				BranchLoadedCallback?.Invoke(branchIndex.Value);
			}
			UpdateTools();
			if (!branchIndex.HasValue)
			{
				// If we are not a branch, we won't have a saved inamge.
				// We will emulate 1 frame to get one, so we can discard now.
				DisplayManager.DiscardApiSurfaces();
			}
		}

		public void AddBranchExternal() => BookMarkControl.AddBranchExternal();

		/// <summary>
		/// Put the run as it stands into branch <paramref name="index"/>, making
		/// that branch if it is not there yet. This is the numbered-slot half of
		/// the branch hotkeys - Shift+F1..F10 - and it deliberately overwrites:
		/// a slot is a place you keep coming back to, not a list you append to.
		/// </summary>
		public void SaveBranchByIndex(int index) => BookMarkControl.UpdateBranchExternal(index);
		public void RemoveBranchExternal() => BookMarkControl.RemoveBranchExternal();

		private void UpdateTools()
		{
			Tools.UpdateToolsAfter();
		}

		public void TogglePause()
		{
			MainForm.TogglePause();
		}

		public override void OnPauseToggle(bool newPauseState)
		{
			if (newPauseState)
			{
				// On pause, interrupt merging of recorded frames.
				_lastRecordAction = -1;

				if (TasPlaybackBox.TurboSeek && SeekingTo != -1)
				{
					SetTasViewRowCount(); // so follow cursor works
					UpdateAfter();
				}
			}
		}

		private void SetSplicer()
		{
			// TODO: columns selected?
			var selectedRowCount = GetSelection().Count();
			var temp = $"Selected: {selectedRowCount} {(selectedRowCount == 1 ? "frame" : "frames")}, States: {CurrentTasMovie.States.Count}";
			var clipboardCount = _tasClipboard.Count;
			if (clipboardCount is not 0) temp += $", Clipboard: {clipboardCount} {(clipboardCount is 1 ? "frame" : "frames")}";
			SplicerStatusLabel.Text = temp;
		}

		private void Tastudio_Closing(object sender, FormClosingEventArgs e)
		{
			if (!_initialized)
			{
				return;
			}

			_exiting = true;

			if (AskSaveChanges())
			{
				WantsToControlStopMovie = false;
				TastudioStopMovie();
				Disengage();
				// this window IS the project's session: it does not outlive it, and
				// the project does not outlive the window
				MainForm.QueueProjectClose();
			}
			else
			{
				e.Cancel = true;
				_exiting = false;
			}

			_undoForm?.Close();
		}

		/// <summary>
		/// This method is called every time the Changes property is toggled on a <see cref="TasMovie"/> instance.
		/// </summary>
		private void TasMovie_OnChangesChanged(object sender, EventArgs e)
		{
			UpdateWindowTitle();
		}

		private void TAStudio_DragDrop(object sender, DragEventArgs e)
		{
			// TODO: Maybe this should call Mainform's DragDrop method,
			// since that can file types that are not movies,
			// and it can process multiple files sequentially
			var filePaths = (string[])e.Data.GetData(DataFormats.FileDrop);
			LoadMovieFile(filePaths[0]);
		}

		private void TAStudio_MouseLeave(object sender, EventArgs e)
		{
			toolTip1.SetToolTip(_inputRolls[0], null);
		}

		private void TAStudio_Deactivate(object sender, EventArgs e)
		{
			if (_leftButtonHeld)
			{
				TasView_MouseUp(_activeInputRoll, new(MouseButtons.Left, 0, 0, 0, 0));
			}
			if (_rightClickFrame != -1)
			{
				_suppressContextMenu = true;
				TasView_MouseUp(_activeInputRoll, new(MouseButtons.Right, 0, 0, 0, 0));
			}
		}

		private void TAStudio_Resize(object sender, EventArgs e)
		{
			if (_inputRolls.Count == 0) return;
			RepositionRolls();
		}

		protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
		{
			if (keyData is Keys.Tab or (Keys.Shift | Keys.Tab) or Keys.Space) return true;

			// Saving lives on the main window's File menu now, and a menu shortcut
			// only fires for the window that owns it - so the keys have to be
			// answered here as well, or Ctrl+S would do nothing in the window where
			// the work is actually done.
			switch (keyData)
			{
				case Keys.Control | Keys.S:
					if (HasUnsavedChanges) SaveProject();
					return true;
				case Keys.Control | Keys.Shift | Keys.S:
					SaveProjectAs();
					return true;
			}
			return base.ProcessCmdKey(ref msg, keyData);
		}

		private bool AutoAdjustInput()
		{
			var lagLog = CurrentTasMovie[Emulator.Frame - 1]; // Minus one because get frame is +1;
			bool isLag = Emulator.AsInputPollable().IsLagFrame;

			if (lagLog.WasLagged.HasValue)
			{
				if (lagLog.WasLagged.Value && !isLag)
				{
					// remove all consecutive was-lag frames in batch like taseditor
					// current frame has no lag so they will all have to go anyway
					var framesToRemove = new List<int>{ Emulator.Frame - 1 };
					for (int frame = Emulator.Frame; CurrentTasMovie[frame].WasLagged.HasValue; frame++)
					{
						if (CurrentTasMovie[frame].WasLagged.Value)
						{
							framesToRemove.Add(frame);
						}
					}

					// Deleting this frame requires rewinding a frame.
					CurrentTasMovie.ChangeLog.AddInputBind(Emulator.Frame - 1, true, $"Bind Input; Delete {Emulator.Frame - 1}");
					bool wasRecording = CurrentTasMovie.ChangeLog.IsRecording;
					CurrentTasMovie.ChangeLog.IsRecording = false;

					CurrentTasMovie.RemoveFrames(framesToRemove);
					foreach (int f in framesToRemove)
					{
						CurrentTasMovie.LagLog.RemoveHistoryAt(f + 1); // Removes from WasLag
					}

					CurrentTasMovie.ChangeLog.IsRecording = wasRecording;
					return true;
				}

				if (!lagLog.WasLagged.Value && isLag)
				{
					// (it shouldn't need to rewind, since the inserted input wasn't polled)
					CurrentTasMovie.ChangeLog.AddInputBind(Emulator.Frame - 1, false, $"Bind Input; Insert {Emulator.Frame - 1}");
					bool wasRecording = CurrentTasMovie.ChangeLog.IsRecording;
					CurrentTasMovie.ChangeLog.IsRecording = false;

					CurrentTasMovie.InsertInput(Emulator.Frame - 1, CurrentTasMovie.GetInputLogEntry(Emulator.Frame - 2));
					CurrentTasMovie.LagLog.InsertHistoryAt(Emulator.Frame, true);

					CurrentTasMovie.ChangeLog.IsRecording = wasRecording;
					return true;
				}
			}

			return false;
		}

		// ---- the two splitters, remembered as what was chosen rather than where it landed ----
		//
		// SplitterMoved fires for a drag and for a window that was resized under
		// the splitter alike, so what it records must not depend on the window's
		// size at the time: the sidebar's width (the sidebar being the fixed
		// panel, that is what a drag sets), and the branches/markers boundary as
		// a share of the sidebar's height. Nothing is recorded until the window
		// has finished loading, so restoring a value does not re-record it.

		private static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(max, value));

		private void RestoreMainSplit()
		{
			var width = MainVertialSplit.Width;
			var max = width - MainVertialSplit.Panel2MinSize - MainVertialSplit.SplitterWidth;
			if (Settings.SidebarWidth > 0 && max > MainVertialSplit.Panel1MinSize)
			{
				MainVertialSplit.SplitterDistance = Clamp(width - Settings.SidebarWidth - MainVertialSplit.SplitterWidth, MainVertialSplit.Panel1MinSize, max);
				return;
			}
			MainVertialSplit.SetDistanceOrDefault(Settings.MainVerticalSplitDistance, _defaultMainSplitDistance);
		}

		private void RestoreBranchMarkerSplit()
		{
			var height = BranchesMarkersSplit.Height;
			var max = height - BranchesMarkersSplit.Panel2MinSize - BranchesMarkersSplit.SplitterWidth;
			if (Settings.BranchMarkerSplitRatio > 0 && max > BranchesMarkersSplit.Panel1MinSize)
			{
				BranchesMarkersSplit.SplitterDistance = Clamp((int)Math.Round(Settings.BranchMarkerSplitRatio * height), BranchesMarkersSplit.Panel1MinSize, max);
				return;
			}
			BranchesMarkersSplit.SetDistanceOrDefault(Settings.BranchMarkerSplitDistance, _defaultBranchMarkerSplitDistance);
		}

		private void MainVerticalSplit_SplitterMoved(object sender, SplitterEventArgs e)
		{
			if (_initialized)
			{
				Settings.MainVerticalSplitDistance = MainVertialSplit.SplitterDistance;
				Settings.SidebarWidth = Math.Max(MainVertialSplit.Panel2MinSize, MainVertialSplit.Width - MainVertialSplit.SplitterDistance - MainVertialSplit.SplitterWidth);
			}
			if (_inputRolls.Count != 0) RepositionRolls(); // this event gets called while loading
		}

		private void BranchesMarkersSplit_SplitterMoved(object sender, SplitterEventArgs e)
		{
			if (!_initialized || BranchesMarkersSplit.Height <= 0) return;
			Settings.BranchMarkerSplitDistance = BranchesMarkersSplit.SplitterDistance;
			Settings.BranchMarkerSplitRatio = (double)BranchesMarkersSplit.SplitterDistance / BranchesMarkersSplit.Height;
		}

		private void TasView_CellDropped(object sender, InputRoll.CellEventArgs e)
		{
			if (e.NewCell?.RowIndex != null && !CurrentTasMovie.Markers.IsMarker(e.NewCell.RowIndex.Value))
			{
				CurrentTasMovie.Markers.Move(e.OldCell.RowIndex.Value, e.NewCell.RowIndex.Value);
			}
		}

		// Stupid designer
		protected void DragEnterWrapper(object sender, DragEventArgs e)
		{
			GenericDragEnter(sender, e);
		}

		private IMovieController ControllerFromMnemonicStr(string inputLogEntry)
		{
			try
			{
				var controller = MovieSession.GenerateMovieController();
				controller.SetFromMnemonic(inputLogEntry);

				return controller;
			}
			catch (Exception)
			{
				DialogController.ShowMessageBox($"Invalid mnemonic string: {inputLogEntry}", "Paste Input failed!");
				return null;
			}
		}

		private IEnumerable<(string Name, string Mnemonic, int MaxLength)> MnemonicMap()
		{
			if (MovieSession.MovieController.Definition.MnemonicsCache is null)
				throw new InvalidOperationException("Can't build mnemonic map with empty mnemonics cache");

			foreach (var playerControls in MovieSession.MovieController.Definition.ControlsOrdered)
			{
				foreach ((string name, AxisSpec? axisSpec) in playerControls)
				{
					if (axisSpec.HasValue)
					{
						string mnemonic = MnemonicLookup.LookupAxis(name, MovieSession.Movie.SystemID);
						yield return (name, mnemonic, axisSpec.Value.MaxCharacters);
					}
					else
					{
						yield return (name, MovieSession.MovieController.Definition.MnemonicsCache[name].ToString(), 1);
					}
				}
			}
		}

		private void HandleRotationChanged(object sender, EventArgs e)
		{
			InputRoll roll = (InputRoll)sender;
			CurrentTasMovie.FlagChanges();
			if (roll.HorizontalOrientation)
			{
				BranchesMarkersSplit.Orientation = Orientation.Vertical;
				BranchesMarkersSplit.SplitterDistance = 200;
				foreach (var rollColumn in roll.AllColumns)
				{
					rollColumn.Width = rollColumn.HorizontalHeight;
				}
			}
			else
			{
				BranchesMarkersSplit.Orientation = Orientation.Horizontal;
				BranchesMarkersSplit.SplitterDistance = _defaultBranchMarkerSplitDistance;
				foreach (var rollColumn in roll.AllColumns)
				{
					rollColumn.Width = rollColumn.VerticalWidth;
				}
			}

			foreach (InputRoll roll2 in _inputRolls)
			{
				roll2.HorizontalOrientation = roll.HorizontalOrientation;
			}

			roll.AllColumns.ColumnsChanged();
		}

		private bool _suspendEditLogic;
		public void LoadBranch(TasBranch branch)
		{
			if (Settings.OldControlSchemeForBranches && !TasPlaybackBox.RecordingMode)
			{
				GoToFrame(branch.Frame);
				return;
			}

			// a branch is its input and its state; the state is cache, and a cache
			// that was lost or set aside leaves the branch with nothing to jump to
			if (branch.CoreData is null)
			{
				MainForm.AddOnScreenMessage("This branch has no saved state beside the project; it cannot be loaded.");
				return;
			}

			_suspendEditLogic = true;
			CurrentTasMovie.LoadBranch(branch);
			_suspendEditLogic = false;
			LoadBranchState(branch, CurrentTasMovie.Branches.IndexOf(branch));

			CurrentTasMovie.States.Capture(Emulator.Frame);
			QuickBmpFile.Copy(new BitmapBufferVideoProvider(branch.CoreFrameBuffer), VideoProvider);

			if (Settings.OldControlSchemeForBranches && TasPlaybackBox.RecordingMode)
			{
				CurrentTasMovie.Truncate(branch.Frame);
			}

			StopSeeking();
			RefreshDialog();
		}
	}
}
