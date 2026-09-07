using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;

using Chimera.Display;
using Chimera.Common;
using Chimera.Common.CollectionExtensions;
using Chimera.Common.PathExtensions;
using Chimera.Emulation.Common;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Chimera.Client.Common
{
	public class Config
	{
		// A fresh config starts with no bindings at all, deliberately. BizHawk seeded them here from a
		// defctrl.json shipped next to the exe, listing every console it knew; Chimera's cores come
		// from outside the frontend, so the bindings come with them - InputManager.SyncControls takes
		// a controller's defaults from the package that declared it, the first time it sees one.

		public void ResolveDefaults()
		{
			PathEntries.ResolveWithDefaults();
			HotkeyInfo.ResolveWithDefaults(HotkeyBindings);
			PathEntries.RefreshTempPath();
			UnfloatToolsOnce();
		}

		/// <summary>
		/// Tools used to float free of the main window by default, and every
		/// config written before that changed carries the old value per tool - so
		/// changing the default alone would leave anyone who had opened a tool
		/// with the old behaviour forever. This flips those saved values once,
		/// and records that it did, so a later deliberate choice stands.
		/// </summary>
		private void UnfloatToolsOnce()
		{
			if (ToolsUnfloated) return;
			ToolsUnfloated = true;
			foreach (var settings in CommonToolSettings.Values) settings.FloatingWindow = false;
			foreach (var perTool in CustomToolSettings.Values)
			{
				foreach (var settings in perTool.Values.OfType<ToolDialogSettings>()) settings.FloatingWindow = false;
			}
		}

		/// <summary>Whether the one-time unfloat above has run (see <see cref="UnfloatToolsOnce"/>).</summary>
		public bool ToolsUnfloated { get; set; }

		public PathEntryCollection PathEntries { get; set; } = new PathEntryCollection();

		// General Client Settings
		public enum InputPriority
		{
			BOTH,
			INPUT,
			HOTKEY,
		}

		public InputPriority InputHotkeyOverrideOptions { get; set; }
		public bool NoMixedInputHokeyOverride { get; set; }

		public bool StackOSDMessages { get; set; } = true;

		/// <summary>window scale per system, as the user last set it; a system nobody has resized gets 2</summary>
		private Dictionary<string, int> TargetZoomFactors { get; set; } = new();

		public int GetWindowScaleFor(string sysID)
			=> TargetZoomFactors.GetValueOrPut(sysID, static _ => 2);

		public void SetWindowScaleFor(string sysID, int windowScale)
			=> TargetZoomFactors[sysID] = windowScale;

		public int DispFinalFilter { get; set; } = 0; // None
		public RecentFiles RecentRoms { get; set; } = new RecentFiles(10);

		/// <summary>the projects the start screen and File menu offer, most recent first</summary>
		public RecentFiles RecentProjects { get; set; } = new RecentFiles(10);

		/// <summary>last core package loaded via File &gt; Open Core (used to seed the file prompt)</summary>
		public string LastCorePackagePath { get; set; } = "";

		/// <summary>
		/// Directories scanned for core packages at startup, in order. Empty means the
		/// default alone (<c>Cores/</c> beside the executable); the default is always
		/// scanned first, so this only ever adds.
		/// </summary>
		public List<string> CorePackagePaths { get; set; } = new();

		/// <summary>
		/// Cores added by hand in File &gt; Core Manager, by the address of the GitHub
		/// page that publishes them. They are listed below the official ones and
		/// fetched the same way; removing one takes it out of here, and so out of the
		/// list, because nothing else remembers it.
		/// </summary>
		public List<RosterCore> ExternalCores { get; set; } = new();

		public bool PauseWhenMenuActivated { get; set; } = true;
		public bool SaveWindowPosition { get; set; } = true;
		public bool MainFormStayOnTop { get; set; }
		public bool StartPaused { get; set; }
		public bool StartFullscreen { get; set; }
		public Point? MainWindowPosition { get; set; }
		public Size? MainWindowSize { get; set; }
		public bool MainWindowMaximized { get; set; }
		public bool RunInBackground { get; set; } = true;
		public bool AcceptBackgroundInput { get; set; }
		public bool AcceptBackgroundInputControllerOnly { get; set; }
		public bool HandleAlternateKeyboardLayouts { get; set; }
		public bool SingleInstanceMode { get; set; }
		public OpposingDirPolicy OpposingDirPolicy { get; set; }
		public bool ShowContextMenu { get; set; } = true;
		public bool HotkeyConfigAutoTab { get; set; } = true;
		public bool InputConfigAutoTab { get; set; } = true;
		public bool SkipWaterboxIntegrityChecks { get; set; } = false;
		public int AutofireOn { get; set; } = 1;
		public int AutofireOff { get; set; } = 1;
		public bool AutofireLagFrames { get; set; } = true;
		public bool SkipLagFrame { get; set; }
		public bool SuppressAskSave { get; set; }
		public bool AviCaptureOsd { get; set; }
		public bool AviCaptureLua { get; set; }
		public bool ScreenshotCaptureOsd { get; set; }
		public bool SkipOutdatedOsCheck { get; set; }
		public bool CaptureMouse { get; set; } = false;
		public bool MainFormMouseCaptureForcesTopmost { get; set; } = false;

		public bool SkipSuperuserPrivsCheck { get; set; }

		/// <summary>
		/// Whether to make AutoSave files at periodic intervals
		/// </summary>

		/// <summary>
		/// Intervals at which to make AutoSave files
		/// </summary>

		public bool TurboSeek { get; set; }

		// Run-Control settings
		public int FrameProgressDelayMs { get; set; } = 500; // how long until a frame advance hold turns into a frame progress?
		public int FrameSkip { get; set; } = 4;
		public int SpeedPercent { get; set; } = 100;
		public int SpeedPercentAlternate { get; set; } = 400;
		public bool ClockThrottle { get; set; } = true;
		public bool Unthrottled { get; set; } = false;
		public bool AutoMinimizeSkipping { get; set; } = true;
		public bool VSyncThrottle { get; set; } = false;


		public MovieConfig Movies { get; set; } = new MovieConfig();

		/// <summary>
		/// Use vsync when presenting all 3d accelerated windows.
		/// For the main window, if VSyncThrottle = false, this will try to use vsync without throttling to it
		/// </summary>
		public bool VSync { get; set; }

		/// <summary>
		/// Allows non-vsync'd video to tear, this is needed for VFR monitors reportedly
		/// </summary>

		// Display options
		public bool DisplayFps { get; set; }
		public bool DisplayFrameCounter { get; set; }
		public bool DisplayLagCounter { get; set; }
		public bool DisplayInput { get; set; }
		public bool DisplayRerecordCount { get; set; }
		public bool DisplayMessages { get; set; } = true;

		public bool DispFixAspectRatio { get; set; } = true;
		public bool DispFixScaleInteger { get; set; }
		public bool DispFullscreenHacks { get; set; }
		public bool DispAutoPrescale { get; set; }
		public int DispSpeedupFeatures { get; set; } = 2;

		public MessagePosition Fps { get; set; } = DefaultMessagePositions.Fps.Clone();
		public MessagePosition FrameCounter { get; set; } = DefaultMessagePositions.FrameCounter.Clone();
		public MessagePosition LagCounter { get; set; } = DefaultMessagePositions.LagCounter.Clone();
		public MessagePosition InputDisplay { get; set; } = DefaultMessagePositions.InputDisplay.Clone();
		public MessagePosition ReRecordCounter { get; set; } = DefaultMessagePositions.ReRecordCounter.Clone();
		public MessagePosition Messages { get; set; } = DefaultMessagePositions.Messages.Clone();
		public MessagePosition Autohold { get; set; } = DefaultMessagePositions.Autohold.Clone();
		public MessagePosition RamWatches { get; set; } = DefaultMessagePositions.RamWatches.Clone();

		public int MessagesColor { get; set; } = DefaultMessagePositions.MessagesColor;
		public int AlertMessageColor { get; set; } = DefaultMessagePositions.AlertMessageColor;
		public int LastInputColor { get; set; } = DefaultMessagePositions.LastInputColor;
		public int MovieInputColor { get; set; } = DefaultMessagePositions.MovieInputColor;

		public int DispPrescale { get; set; } = 1;


		public int DispChromeFrameWindowed { get; set; } = 2;
		public bool DispChromeStatusBarWindowed { get; set; } = true;
		public bool DispChromeCaptionWindowed { get; set; } = true;
		public bool DispChromeMenuWindowed { get; set; } = true;
		public bool DispChromeStatusBarFullscreen { get; set; }
		public bool DispChromeMenuFullscreen { get; set; }
		public bool DispChromeFullscreenAutohideMouse { get; set; } = true;
		public bool DispChromeAllowDoubleClickFullscreen { get; set; } = true;

		public EDispManagerAR DispManagerAR { get; set; } = EDispManagerAR.System;

		// these are misnomers. they're actually a fixed size (fixme on major release)
		public int DispCustomUserARWidth { get; set; } = -1;
		public int DispCustomUserARHeight { get; set; } = -1;

		// these are more like the actual AR ratio (i.e. 4:3) (fixme on major release)
		public float DispCustomUserArx { get; set; } = -1;
		public float DispCustomUserAry { get; set; } = -1;

		//these default to 0 because by default we crop nothing
		public int DispCropLeft { get; set; } = 0;
		public int DispCropTop { get; set; } = 0;
		public int DispCropRight { get; set; } = 0;
		public int DispCropBottom { get; set; } = 0;

		/// <summary>
		/// Automatically resize main window when framebuffer size changes (default behavior)
		/// </summary>
		public bool ResizeWithFramebuffer { get; set; } = true;

		// Sound options
		public ESoundOutputMethod SoundOutputMethod { get; set; } = ESoundOutputMethod.OpenAL;

		/// <value>iff <see langword="false"/>, cores may skip processing audio</value>
		/// <seealso cref="SoundEnabledNormal"/>
		/// <seealso cref="SoundEnabledRWFF"/>
		/// <seealso cref="MuteFrameAdvance"/>
		public bool SoundEnabled { get; set; } = true;

		/// <value>whether to pass audio through to the host while emulating to normal throttle</value>
		/// <remarks>separate from <see cref="SoundVolume"/> so that the config UI can "remember" the previous value</remarks>
		/// <seealso cref="SoundVolume"/>
		/// <seealso cref="SoundEnabled"/>
		/// <seealso cref="SoundEnabledRWFF"/>
		/// <seealso cref="MuteFrameAdvance"/>
		public bool SoundEnabledNormal { get; set; } = true;

		/// <value>whether to pass audio through to the host while rewinding or fast-forwarding</value>
		/// <remarks>separate from <see cref="SoundVolumeRWFF"/> so that the config UI can "remember" the previous value</remarks>
		/// <seealso cref="SoundVolumeRWFF"/>
		/// <seealso cref="SoundEnabled"/>
		/// <seealso cref="SoundEnabledNormal"/>
		/// <seealso cref="MuteFrameAdvance"/>
		public bool SoundEnabledRWFF { get; set; } = true;

		/// <value>whether to pass audio through to the host when doing a frame advance while paused</value>
		/// <remarks>
		/// sets sample amplitude multiplier to 0x iff <see langword="true"/>,
		/// otherwise the main <see cref="SoundVolume"/> has effect
		/// </remarks>
		/// <seealso cref="SoundEnabled"/>
		/// <seealso cref="SoundEnabledNormal"/>
		/// <seealso cref="SoundEnabledRWFF"/>
		public bool MuteFrameAdvance { get; set; } = true;

		/// <value>
		/// volume level; interpreted as a percentage (i.e. scaled down to 0.0..1.0)
		/// and passed to the platform audio implementation, which should use it as a simple multiplier on each sample;<br/>
		/// so <c>0</c> is scale each sample by 0x (mute),<c>100</c> is scale each sample by 1x (preserve full volume),
		/// <c>50</c> is scale each sample by 0.5x (≈ -3 dB), and <c>25</c> is scale each sample by 0.25x (≈ -6 dB)
		/// </value>
		/// <seealso cref="SoundVolumeRWFF"/>
		/// <seealso cref="SoundEnabledNormal"/>
		public int SoundVolume { get; set; } = 100;

		/// <value>
		/// when rewinding or fast-forwarding, the sample amplitude multiplier is <i>multiplied by this value</i>
		/// (after conversion from percentage), or in other words, <see cref="SoundVolume"/> remains in effect
		/// </value>
		/// <seealso cref="SoundVolume"/>
		/// <seealso cref="SoundEnabledRWFF"/>
		public int SoundVolumeRWFF { get; set; } = 50;

		public bool SoundThrottle { get; set; }
		public string SoundDevice { get; set; } = "";
		public int SoundBufferSizeMs { get; set; } = 100;

		// Lua
		public RecentFiles RecentLua { get; set; } = new RecentFiles(8);
		public RecentFiles RecentLuaSession { get; set; } = new RecentFiles(8);

		// luaconsole-refactor TODO: move this to LuaConsole settings
		public bool RunLuaDuringTurbo { get; set; } = true;

		// Watch Settings
		public RecentFiles RecentWatches { get; set; } = new RecentFiles(8);
		public PreviousType RamWatchDefinePrevious { get; set; } = PreviousType.LastFrame;
		public bool DisplayRamWatch { get; set; }

		// Video dumping settings
		public string VideoWriter { get; set; } = "";
		public int JmdCompression { get; set; } = 3;
		public int JmdThreads { get; set; } = 3;
		public string FFmpegFormat { get; set; } = "";
		public string FFmpegCustomCommand { get; set; } = "-c:v libx264 -c:a aac -pix_fmt yuv420p -f mp4";
		public string AviCodecToken { get; set; } = "";
		public int GifWriterFrameskip { get; set; } = 3;
		public int GifWriterDelay { get; set; } = -1;
		public bool VideoWriterAudioSync { get; set; } = true;

		[JsonIgnore]
		public bool VideoWriterAudioSyncEffective;

		// Emulation core settings
		internal Dictionary<string, JToken> CoreSettings { get; set; } = new Dictionary<string, JToken>();

		public Dictionary<string, ToolDialogSettings> CommonToolSettings { get; set; } = new Dictionary<string, ToolDialogSettings>();
		public Dictionary<string, Dictionary<string, object>> CustomToolSettings { get; set; } = new Dictionary<string, Dictionary<string, object>>();


		// Macro Tool
		public RecentFiles RecentMacros { get; set; } = new RecentFiles(8);

		// Movie Settings
		public RecentFiles RecentMovies { get; set; } = new RecentFiles(8);
		public string DefaultAuthor { get; set; } = "default user";
		public bool UseDefaultAuthor { get; set; } = true;
		public bool DisplaySubtitles { get; set; } = true;

		// Play Movie Dialog
		public bool PlayMovieIncludeSubDir { get; set; }
		public bool PlayMovieMatchHash { get; set; } = true;

		public Dictionary<string, string> HotkeyBindings { get; set; } = new();

		// Analog Hotkey values
		public int AnalogLargeChange { get; set; } = 10;
		public int AnalogSmallChange { get; set; } = 1;

		// [ControllerType][ButtonName] => Physical Bind
		public Dictionary<string, Dictionary<string, string>> AllTrollers { get; set; } = new Dictionary<string, Dictionary<string, string>>();
		public Dictionary<string, Dictionary<string, string>> AllTrollersAutoFire { get; set; } = new Dictionary<string, Dictionary<string, string>>();
		public Dictionary<string, Dictionary<string, AnalogBind>> AllTrollersAnalog { get; set; } = new Dictionary<string, Dictionary<string, AnalogBind>>();
		public Dictionary<string, Dictionary<string, FeedbackBind>> AllTrollersFeedbacks { get; set; } = new Dictionary<string, Dictionary<string, FeedbackBind>>();

		/// <summary>
		/// The core a system's roms open with, keyed by system ID - remembered from the last time the
		/// user chose one (Emulator > Core), and consulted by RomLoader when more than one loaded
		/// package can run the file. Not a matrix of everything the frontend could do: it holds only
		/// what someone actually picked, and a system with one core never appears here.
		/// </summary>
		public Dictionary<string, string> DefaultCores = new();

		/// <summary>
		/// Where the user put each file a core package asked for, keyed
		/// "&lt;core name&gt;/&lt;firmware id&gt;" (see <c>CoreFirmwareStore</c>). The frontend
		/// has no idea what any of them are - the declarations come from the packages.
		/// </summary>
		public Dictionary<string, string> CoreFirmware = new();

		public bool DontTryOtherCores { get; set; }

		// ReSharper disable once UnusedMember.Global
		public string LastWrittenFromDetailed { get; set; } = VersionInfo.GetEmuVersion();

		public bool UseStaticWindowTitles { get; set; }

		public string MainFormStaticWindowTitleOverride { get; set; } = string.Empty;

		[JsonIgnore]
		public string MainFormStaticWindowTitleOverrideEffective
			=> string.IsNullOrWhiteSpace(MainFormStaticWindowTitleOverride)
				? string.IsNullOrWhiteSpace(VersionInfo.CustomBuildString) ? "Chimera" : VersionInfo.CustomBuildString
				: MainFormStaticWindowTitleOverride;

		public List<string> ModifierKeys { get; set; } = new();

		[JsonIgnore]
		public IReadOnlyList<string> ModifierKeysEffective;

		public bool MergeLAndRModifierKeys { get; set; } = true;

		/// <remarks>in seconds</remarks>
		public int OSDMessageDuration { get; set; } = 2;

		public Queue<string> RecentCores { get; set; } = new();


		public bool AVWriterPad { get; set; } = false;

		public int AVWriterResizeHeight { get; set; } = 0;

		public int AVWriterResizeWidth { get; set; } = 0;

		public bool GCAdapterSupportEnabled { get; set; } = false;

		public bool ScaleOSDWithSystemScale { get; set; } = true;

		public int RelativeMouseSensitivity { get; set; } = 100;

		public SnowyNullVideo.TriggerCriterion SnowyNullCore { get; set; } = SnowyNullVideo.TriggerCriterion.WeekOfChristmas;
	}
}
