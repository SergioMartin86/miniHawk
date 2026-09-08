#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

using Chimera.Client.Common;
using Chimera.Common;
using Chimera.Emulation.Common.Engine;
using Chimera.Emulation.Common;
using Chimera.Emulation.Common.Waterbox;

namespace Chimera.Client.GUI
{
	/// <summary>
	/// Where a chimera project is born: everything defined at once, up front
	/// (docs/project.md). Step one is the core and nothing else - the first
	/// question is which machine, because every question after it belongs to
	/// that machine. Step two is the core-informed file form - the slots,
	/// cardinalities, formats and tooltips all come from the chosen package's
	/// file_slots.json, this window only renders and enforces; step three is
	/// the settings; step four the firmware those choices call for. On Create,
	/// every file is hashed where it stands and the manifest is validated by
	/// the engine against the core's own declaration - and the project stays
	/// IN MEMORY. Nothing is written until the work is saved, so starting a
	/// project costs no decision about where a file goes.
	/// </summary>
	public sealed class NewProjectWizard : FormBase
	{
		private readonly IReadOnlyList<DiscoveredCorePackage> _cores;
		private readonly Func<ProjectSlotDeclaration.Slot, string[]> _pickFiles;

		private readonly Panel[] _pages = new Panel[5];
		private int _page;

		// page 1
		private readonly ComboBox _core;

		/// <summary>
		/// Which machine, for a package that is several. Genesis Plus GX is one core
		/// that is a Mega Drive, a Master System, a Game Gear or an SG-1000, and the
		/// choice belongs here beside the core: everything after it - the files it
		/// takes, the settings it has, the firmware it wants - depends on it.
		/// </summary>
		private readonly ComboBox _machine;

		private readonly Label _machineLabel;

		// page 5: what the core must compile for this game before it can run it
		/// <summary>the config a precompile session must run with, so it sees the same paths</summary>
		private readonly string? _configPath;

		private ListView _precompileList;
		private Button _precompileButton;
		private Label _precompileStatus;
		private ProgressBar _precompileBar;
		private CoreCacheManifest _precompileManifest;
		private bool _precompiling;

		/// <summary>the game's modules, and how many the sessions have finished</summary>
		private uint _precompileExpected, _precompileDone;

		/// <summary>Cancel was pressed while the sessions were running</summary>
		private bool _precompileCancelled;

		/// <summary>and the window is to close once they have stopped</summary>
		private bool _closeAfterPrecompile;

		/// <summary>
		/// Which renderer draws, for a core that offers a choice. It sits beside the
		/// core rather than among the settings because it is not a property of the
		/// machine: the same machine, drawn twice. A core with one renderer shows it
		/// greyed, so the question is always answered in the same place.
		/// </summary>
		private readonly ComboBox _renderer;

		/// <summary>what a hardware renderer costs, shown only when one is chosen</summary>
		private readonly Label _rendererCaveat;

		// page 2, rebuilt when the chosen core changes
		private readonly Panel _slotsHost;
		private ProjectSlotDeclaration? _declaration;
		private string? _declarationCore;

		/// <summary>the package whose declaration <see cref="_cfg"/> holds</summary>
		private string? _cfgCore;
		private readonly Dictionary<string, ListBox> _slotLists = new();
		private readonly Dictionary<string, GroupBox> _slotGroups = new();
		private readonly ToolTip _tips = new();

		// page 3
		private readonly PropertyGrid _settingsGrid;
		private WaterboxCoreSettings? _settings;
		private WaterboxConfig? _cfg;

		// page 4, computed from every earlier decision (docs/project.md): the
		// decisions nail each requirement to ONE exact file or to nothing -
		// the hash is the identity, the name only a hint, and the Firmware
		// folder answers before the user does
		private readonly Func<string, string?> _pickFirmwareFile;
		private readonly IReadOnlyList<string> _firmwareSearchDirs;
		private readonly Func<string, string, string?> _rememberedFirmwarePath;

		/// <summary>
		/// Every path remembered for a core, whichever dump it was for: a person may
		/// own several releases of one bios, chosen in Config > Firmware or in an
		/// earlier project, and the page finds them by hash wherever they lie.
		/// </summary>
		private readonly Func<string, IReadOnlyList<string>> _rememberedFirmwarePaths;

		/// <summary>
		/// Write the firmware chosen HERE into the config, and save it, before the
		/// precompile sessions start. They are separate processes: they read the
		/// config from DISK, and their only firmware source is what it remembers
		/// (CoreFirmwareStore.ProviderFor). The owner remembers these paths too,
		/// but only after this wizard returns - which is after the sessions have
		/// already run and failed to find any.
		/// </summary>
		private readonly Action<string, IReadOnlyDictionary<string, string>> _rememberFirmwareNow;
		private readonly ListView _firmwareList;
		private readonly Button _firmwareSetButton;
		private readonly Button _firmwareClearButton;
		private readonly Func<string?>? _pickFirmwareFolder;
		private List<FirmwareNeed> _firmwareNeeds = new();
		private IReadOnlyList<FirmwareLocator.IndexedFile> _firmwareIndex = [ ];

		private sealed class FirmwareNeed
		{
			public string Id = "";
			public CoreFirmwareDecl? Decl;
			public string? ChosenPath; // for a pinned entry, set only when the hash matched exactly
			public string? ChosenSha1; // the actual hash - equals the pin, or names an unpinned choice

			/// <summary>
			/// A file was chosen - or the core said it can start without one
			/// (Required false), in which case absent is a fine answer and
			/// the wizard must not stand in the way.
			/// </summary>
			public bool Satisfied => ChosenPath is not null || Decl?.Required == false;
		}

		private readonly Button _backButton;
		private readonly Button _nextButton;
		private readonly Label _status;

		/// <summary>
		/// Set when Create succeeded: the project, in memory and unwritten. The
		/// caller owns it from that moment (it boots the machine from it, and
		/// disposes it when the next project replaces it).
		/// </summary>
		public EngineProject? CreatedProject { get; private set; }

		/// <summary>the chosen core's name, for the owner's firmware bookkeeping</summary>
		public string? ChosenCoreName => ChosenCore?.Name;

		protected override string WindowTitleStatic => "New Chimera Project";

		public NewProjectWizard(
			IReadOnlyList<DiscoveredCorePackage> cores,
			Func<ProjectSlotDeclaration.Slot, string[]> pickFiles,
			Func<string, string?>? pickFirmwareFile = null,
			IReadOnlyList<string>? firmwareSearchDirs = null,
			Func<string?>? pickFirmwareFolder = null,
			Func<string, string, string?>? rememberedFirmwarePath = null,
			Action<string, IReadOnlyDictionary<string, string>>? rememberFirmwareNow = null,
			string? configPath = null,
			Func<string, IReadOnlyList<string>>? rememberedFirmwarePaths = null)
		{
			_rememberedFirmwarePaths = rememberedFirmwarePaths ?? (static _ => [ ]);
			_configPath = configPath;
			_pickFirmwareFile = pickFirmwareFile ?? (static _ => null);
			_firmwareSearchDirs = firmwareSearchDirs ?? [ ];
			_pickFirmwareFolder = pickFirmwareFolder;
			_rememberedFirmwarePath = rememberedFirmwarePath ?? (static (_, _) => null);
			_rememberFirmwareNow = rememberFirmwareNow ?? (static (_, _) => { });
			_cores = cores.Where(static c => c.Error is null).ToList();
			_pickFiles = pickFiles;

			SuspendLayout();
			ClientSize = new(UIHelper.ScaleX(560), UIHelper.ScaleY(420));
			MinimumSize = new(UIHelper.ScaleX(480), UIHelper.ScaleY(340));
			StartPosition = FormStartPosition.CenterParent;
			ShowIcon = false;

			for (var i = 0; i < _pages.Length; i++)
			{
				_pages[i] = new Panel
				{
					Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
					Location = new(0, 0),
					Size = new(UIHelper.ScaleX(560), UIHelper.ScaleY(380)),
					Visible = i is 0,
				};
				Controls.Add(_pages[i]);
			}

			// ---- page 1: the machine ---------------------------------------------
			var p1 = _pages[0];
			p1.Controls.Add(MakeHeading("Please select the emulation core."));
			p1.Controls.Add(MakeLabel("Emulation core:", 8, 52));
			_core = new ComboBox
			{
				Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
				DropDownStyle = ComboBoxStyle.DropDownList,
				Location = Pt(110, 48),
				Width = UIHelper.ScaleX(442),
			};
			foreach (var core in _cores)
			{
				// the version at commit length, not the build script's full bookkeeping:
				// this is a picker, and "12d65377b7d3-dirty+local" says nothing here
				// that "12d65377 local" does not
				_core.Items.Add($"{core.Name}  ({SystemNames.Of(core.Systems)}{(core.ShortVersion.Length is 0 ? "" : $", {core.ShortVersion}")})");
			}
			p1.Controls.Add(_core);

			// The core decides the systems, the system decides the renderers: the
			// combos read top to bottom in the order they inform each other.
			_machineLabel = MakeLabel("System:", 8, 84);
			_machine = new ComboBox
			{
				Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
				DropDownStyle = ComboBoxStyle.DropDownList,
				Location = Pt(110, 80),
				Width = UIHelper.ScaleX(442),
			};
			_machine.SelectedIndexChanged += (_, _) =>
			{
				PinMachine();
				// a package that is several machines may offer different renderers
				RefreshRendererChoices();
			};
			p1.Controls.AddRange([ _machineLabel, _machine ]);

			p1.Controls.Add(MakeLabel("Renderer:", 8, 116));
			_renderer = new ComboBox
			{
				Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
				DropDownStyle = ComboBoxStyle.DropDownList,
				Location = Pt(110, 112),
				Width = UIHelper.ScaleX(442),
			};
			_renderer.SelectedIndexChanged += (_, _) =>
			{
				PinRenderer();
				ShowRendererCaveat();
			};
			p1.Controls.Add(_renderer);

			// What a hardware renderer costs, where the choice is made. The space
			// is reserved whether or not the text is showing, so choosing between
			// renderers does not make the form jump around underneath the hand
			// that is choosing.
			_rendererCaveat = new Label
			{
				Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
				AutoSize = false,
				ForeColor = SystemColors.GrayText,
				Location = Pt(110, 138),
				Size = new(UIHelper.ScaleX(442), UIHelper.ScaleY(46)),
			};
			p1.Controls.Add(_rendererCaveat);
			p1.Controls.Add(MakeIssuesNotice());
			_core.SelectedIndexChanged += (_, _) => LoadChosenPackage();
			if (_core.Items.Count is not 0) _core.SelectedIndex = 0;
			LoadChosenPackage();

			// ---- page 2: the core-informed file form -----------------------------
			var p2 = _pages[1];
			p2.Controls.Add(MakeHeading("Please provide the game ROMs and other persistent data."));
			_slotsHost = new Panel
			{
				Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
				AutoScroll = true,
				Location = Pt(8, 48),
				Size = new(UIHelper.ScaleX(544), UIHelper.ScaleY(324)),
			};
			p2.Controls.Add(_slotsHost);

			// ---- page 3: settings ------------------------------------------------
			var p3 = _pages[2];
			p3.Controls.Add(MakeHeading("Please specify the emulation configuration settings."));
			_settingsGrid = new PropertyGrid
			{
				Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
				Location = Pt(8, 48),
				Size = new(UIHelper.ScaleX(544), UIHelper.ScaleY(324)),
				ToolbarVisible = false,
				PropertySort = PropertySort.Alphabetical,
			};
			_settingsGrid.PropertyValueChanged += (_, _) =>
			{
				RefreshExposedSettings();
				UpdateNavLabels();
			};
			p3.Controls.Add(_settingsGrid);

			// ---- page 4: firmware, decided by everything chosen above ------------
			var p4 = _pages[3];
			p4.Controls.Add(MakeHeading("Please indicate where to find the required firmware files."));
			_firmwareList = new ListView
			{
				Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
				FullRowSelect = true,
				HideSelection = false,
				Location = Pt(8, 56),
				MultiSelect = false,
				ShowItemToolTips = true,
				Size = new(UIHelper.ScaleX(544), UIHelper.ScaleY(282)),
				View = View.Details,
			};
			_firmwareList.Columns.Add("Firmware", UIHelper.ScaleX(150));
			_firmwareList.Columns.Add("Expected file", UIHelper.ScaleX(120));
			_firmwareList.Columns.Add("SHA1", UIHelper.ScaleX(110));
			_firmwareList.Columns.Add("Status", UIHelper.ScaleX(160));
			_firmwareList.SelectedIndexChanged += (_, _) => UpdateFirmwareButtons();
			_firmwareList.DoubleClick += (_, _) => SetFirmwareFile();
			_firmwareSetButton = new Button { AutoSize = true, Location = Pt(8, 344), Text = "Select File..." };
			_firmwareSetButton.Click += (_, _) => SetFirmwareFile();
			_firmwareClearButton = new Button { AutoSize = true, Location = Pt(110, 344), Text = "Clear" };
			_firmwareClearButton.Click += (_, _) => ClearFirmwareFile();
			Button firmwareScanButton = new() { AutoSize = true, Location = Pt(178, 344), Text = "Scan Folder..." };
			firmwareScanButton.Click += (_, _) => ScanFirmwareFolder();
			firmwareScanButton.Visible = pickFirmwareFolder is not null;
			p4.Controls.AddRange([ _firmwareList, _firmwareSetButton, _firmwareClearButton, firmwareScanButton ]);

			// ---- page 5: the code the core compiles for this game -----------------
			var p5 = _pages[4];
			p5.Controls.Add(MakeHeading("This game's code must be recompiled before it can be played."));
			p5.Controls.Add(new Label
			{
				AutoSize = false,
				Location = Pt(8, 52),
				Size = new(UIHelper.ScaleX(544), UIHelper.ScaleY(44)),
				Text = "This core translates the console's code into your machine's before running it. "
					+ "Doing it once now, in parallel, is what makes the game fast enough to work on; "
					+ "otherwise every session pays for it again. It can take several minutes. The result "
					+ "depends only on the game and this core, so it is the same on every machine and is "
					+ "not part of the run.",
			});
			_precompileList = new ListView
			{
				Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
				FullRowSelect = true,
				HideSelection = false,
				Location = Pt(8, 100),
				MultiSelect = false,
				ShowItemToolTips = true,
				Size = new(UIHelper.ScaleX(544), UIHelper.ScaleY(216)),
				View = View.Details,
			};
			_precompileList.Columns.Add("Compiled Module", UIHelper.ScaleX(300));
			_precompileList.Columns.Add("SHA1", UIHelper.ScaleX(180));
			_precompileList.Columns.Add("State", UIHelper.ScaleX(72));
			_precompileBar = new ProgressBar
			{
				Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
				Location = Pt(8, 322),
				Maximum = 1,
				Size = new(UIHelper.ScaleX(544), UIHelper.ScaleY(14)),
				Visible = false,
			};
			_precompileButton = new Button { AutoSize = true, Location = Pt(8, 344), Text = "Compile" };
			_precompileButton.Click += (_, _) => RunPrecompile();
			_precompileStatus = new Label
			{
				Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
				AutoEllipsis = true,
				Location = Pt(96, 349),
				Size = new(UIHelper.ScaleX(456), UIHelper.ScaleY(16)),
			};
			p5.Controls.AddRange([ _precompileList, _precompileBar, _precompileButton, _precompileStatus ]);

			// ---- chrome ----------------------------------------------------------
			// Width is set below, once the buttons have been placed: a fixed one
			// reached under the leftmost of them and painted over it.
			_status = new Label
			{
				Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
				AutoEllipsis = true,
				ForeColor = Color.Firebrick,
				Location = Pt(8, 386),
				Size = new(UIHelper.ScaleX(360), UIHelper.ScaleY(16)),
			};
			_backButton = MakeNavButton("< Back", GoBack);
			_nextButton = MakeNavButton("Next >", Advance);
			Button cancel = MakeNavButton("Cancel", CancelPressed);
			// laid out from the RIGHT EDGE inward, with the same margin the page
			// content has on the left: hand-placed x positions plus an auto-sized
			// button had Cancel hanging over the window's edge
			var margin = UIHelper.ScaleX(8);
			var gap = UIHelper.ScaleX(6);
			var right = ClientSize.Width - margin;
			foreach (var button in new[] { cancel, _nextButton, _backButton })
			{
				right -= button.Width;
				button.Location = new Point(right, UIHelper.ScaleY(382));
				right -= gap;
			}
			// what is left of the row once the buttons have taken their share
			_status.Width = Math.Max(UIHelper.ScaleX(80), right - margin);

			AcceptButton = _nextButton;
			CancelButton = cancel;
			// buttons first, so they sit in front of the status label rather than
			// under it - it is the one thing on this row that can grow
			Controls.AddRange([ _backButton, _nextButton, cancel, _status ]);
			ResumeLayout();
			ShowPage(0);
		}

		private const string IssuesUrl = "https://github.com/ToolAssisted-run/chimera/issues";

		/// <summary>
		/// Where to say something when Chimera gets it wrong. It sits on the first
		/// page because that is the page everyone sees, and it says the URL rather
		/// than hiding it behind a word, so it can be read off a screenshot.
		/// </summary>
		private static LinkLabel MakeIssuesNotice()
		{
			const string text = "Please submit any issues and feature requests here: " + IssuesUrl;
			LinkLabel notice = new()
			{
				Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
				AutoSize = false,
				ForeColor = SystemColors.GrayText,
				LinkBehavior = LinkBehavior.HoverUnderline,
				Location = Pt(8, 352),
				Size = new(UIHelper.ScaleX(544), UIHelper.ScaleY(20)),
				Text = text,
			};
			notice.Links.Clear();
			notice.Links.Add(text.IndexOf(IssuesUrl, StringComparison.Ordinal), IssuesUrl.Length, IssuesUrl);
			notice.LinkClicked += static (_, e) => Util.OpenUrlExternal((string) e.Link.LinkData);
			return notice;
		}

		private static Point Pt(int x, int y) => new(UIHelper.ScaleX(x), UIHelper.ScaleY(y));

		private static Label MakeLabel(string text, int x, int y)
			=> new() { AutoSize = true, Location = Pt(x, y), Text = text };

		/// <summary>
		/// A step's one question, across the full width of the window. Not
		/// auto-sized and never hand-wrapped: the text stops where the window
		/// stops, and follows it when it is resized.
		/// </summary>
		private static Label MakeHeading(string text)
			=> new()
			{
				Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
				AutoSize = false,
				Location = Pt(8, 10),
				Size = new(UIHelper.ScaleX(544), UIHelper.ScaleY(20)),
				Text = text,
			};

		/// <summary>
		/// One of the three navigation buttons. A FIXED size, because they are
		/// laid out from the window's right edge and an auto-sized button's width
		/// is not known until it has been laid out - which is how Cancel ended up
		/// past the edge of the window.
		/// </summary>
		private Button MakeNavButton(string text, Action onClick)
		{
			Button b = new()
			{
				Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
				AutoSize = false,
				Size = new(UIHelper.ScaleX(76), UIHelper.ScaleY(24)),
				Text = text,
			};
			b.Click += (_, _) => onClick();
			return b;
		}

		private DiscoveredCorePackage? ChosenCore
			=> _core.SelectedIndex is >= 0 and var i && i < _cores.Count ? _cores[i] : null;

		/// <summary>
		/// Reads the chosen package's declaration and offers its machines. Done as
		/// soon as a core is picked, because the machine is part of picking it: a
		/// package that is one machine simply has nothing to offer here.
		/// </summary>
		private void LoadChosenPackage()
		{
			var core = ChosenCore;
			if (core is null || _cfgCore == core.Path) return;
			_cfgCore = core.Path;
			_cfg = null;
			_settings = new WaterboxCoreSettings();
			try
			{
				using var pkg = EnginePackage.Open(core.Path);
				_cfg = WaterboxConfig.FromJson(pkg?.EntryText(WaterboxCoreFactory.ConfigFileName) ?? "");
			}
			catch (InvalidOperationException)
			{
				// an unreadable package complains when the user tries to go on
			}
			_machine.Items.Clear();
			var machines = _cfg?.Machines;
			if (machines is { Count: > 0 })
			{
				foreach (var machine in machines) _machine.Items.Add(machine.DisplayName);
			}
			else if (core is not null)
			{
				// a package that is one machine still says which machine it is
				_machine.Items.Add(SystemNames.Of(core.Systems));
			}
			if (_machine.Items.Count is not 0) _machine.SelectedIndex = 0;
			// one system is not a choice, and a disabled box still says what it is
			_machine.Enabled = _machine.Items.Count > 1;
			PinMachine();
			RefreshRendererChoices();
		}

		/// <summary>
		/// Opens the wizard already filled in from a project: its core, its machine,
		/// its settings and the files it is running on.
		///
		/// This IS how a project is reconfigured. Changing a sync setting changes the
		/// machine, so there is no editing a project in place that would not amount
		/// to building another one - and what made that unbearable was re-answering
		/// every question to change one of them. Answer them once; come back and
		/// change the one.
		///
		/// The files are seeded from where a run found them, which is the only place
		/// a path exists: a project carries names and hashes and no paths at all. A
		/// file that is no longer there is skipped rather than guessed at.
		/// </summary>
		/// <summary>
		/// Opens on a file somebody dropped: the core that claims its extension,
		/// and the file already in the slot that takes that kind of file.
		///
		/// The guess is the packages' own, not a table kept here. Every package
		/// declares the rom extensions it handles, so "which core is this .gg for"
		/// is a question the installed cores have already answered - and a core
		/// installed tomorrow answers it without this file changing. When two
		/// claim the same extension the first is chosen and the picker is left
		/// alone: a guess is a starting point, and the core box is right there.
		/// </summary>
		/// <returns>false when no installed core claims the extension</returns>
		public bool StartFrom(string path)
		{
			var index = GuessCoreIndexFor(path);
			if (index < 0) return false;

			_core.SelectedIndex = index;
			LoadChosenPackage();
			if (!BuildSlotForm()) return false;

			AddFileToSlot(SlotForFile(path), path);
			RefreshExposedSettings();
			_settingsGrid.SelectedObject = _settings;
			return true;
		}

		/// <summary>Which installed package says it handles this file, or -1.</summary>
		public int GuessCoreIndexFor(string path)
		{
			var extension = Path.GetExtension(path);
			if (string.IsNullOrEmpty(extension)) return -1;
			return _cores.ToList().FindIndex(c => c.Extensions.ContainsKey(extension.ToLowerInvariant()));
		}

		/// <summary>
		/// Which slot a dropped file belongs in: the first that declares its
		/// extension, and otherwise the first slot there is. A package lists the
		/// formats each slot takes, so a disc lands in the disc slot rather than
		/// in whatever happens to be at the top of the form.
		/// </summary>
		public string SlotForFile(string path)
		{
			var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
			var slots = _declaration!.Slots;
			var match = slots.FirstOrDefault(slot =>
				ForMachine(slot).Formats.Any(f => string.Equals(f, extension, StringComparison.OrdinalIgnoreCase)));
			return (match ?? slots[0]).Id;
		}

		public void SeedFrom(ProjectAnswers answers)
		{
			var index = _cores.ToList().FindIndex(c =>
				string.Equals(c.Name, answers.CoreName, StringComparison.OrdinalIgnoreCase));
			if (index < 0) return;   // that core is not installed; leave the wizard blank
			_core.SelectedIndex = index;
			LoadChosenPackage();

			SeedSettings(answers.SettingsJson);

			// the slot form has to exist before anything can be put in it
			if (!BuildSlotForm()) return;
			foreach (var (slot, path) in answers.Files)
			{
				if (File.Exists(path)) AddFileToSlot(slot, path);
			}

			// and the files can decide settings in their turn
			RefreshExposedSettings();
			_settingsGrid.SelectedObject = _settings;
		}

		/// <summary>
		/// Puts a project's saved values into the settings object, then lets the
		/// machine and renderer pickers read themselves back out of it - they are
		/// settings like any other, and this is where they were recorded.
		/// </summary>
		private void SeedSettings(string flatJson)
		{
			_settings ??= new WaterboxCoreSettings();
			if (!string.IsNullOrWhiteSpace(flatJson))
			{
				try
				{
					var values = Newtonsoft.Json.Linq.JObject.Parse(flatJson);
					foreach (var pair in values)
					{
						if (pair.Value is Newtonsoft.Json.Linq.JValue v && v.Value is not null)
						{
							_settings.Values[pair.Key] = v.Value;
						}
					}
				}
				catch (Newtonsoft.Json.JsonException)
				{
					// a project whose settings will not parse gets the defaults
				}
			}

			if (_cfg?.HasMachines is true)
			{
				var effective = WaterboxCore.EffectiveSettingsFor(_cfg, _settings);
				var machine = _cfg.MachineFor(effective);
				var at = machine is null ? -1 : _cfg.Machines.IndexOf(machine);
				if (at >= 0) _machine.SelectedIndex = at;
			}
			// read before the pickers are refreshed: refreshing them selects the
			// core's default and writes THAT back over what the project said
			var renderer = _settings.Values.TryGetValue(RendererSetting, out var chosen) ? chosen?.ToString() : null;

			PinMachine();
			RefreshRendererChoices();
			if (renderer is not null) SetRenderer(renderer);
		}

		/// <summary>The setting that names the renderers, if this core has one.</summary>
		private const string RendererSetting = "renderer";

		/// <summary>
		/// One entry in the renderer dropdown: the name the CORE knows it by, shown
		/// as what it actually is. The distinction a person is owed here is where
		/// the drawing happens - the core's own rasteriser and its OpenGL path over
		/// a software OpenGL both run on the CPU inside the sandbox and are
		/// deterministic; the hardware one leaves the sandbox for a GPU and is not.
		/// The value is what the project records; renaming what is on screen must
		/// never rename what is in the file.
		/// </summary>
		private sealed class RendererChoice
		{
			internal RendererChoice(string value) => Value = value;

			internal string Value { get; }

			public override string ToString() => Value switch
			{
				"software" or "default" => "Software (Native)",
				"opengl" => "Software (OpenGL)",
				"opengl-hw" => "Hardware (OpenGL)",
				_ => Value,
			};
		}

		/// <summary>
		/// Offers the chosen core's renderers. A core that declares none draws one
		/// way, so there is one entry and nothing to choose; a core that declares
		/// them starts on the one it declared as its default, which is the faster of
		/// the two everywhere this exists so far.
		///
		/// Read through the CHOSEN MACHINE, because a package that is several
		/// machines may narrow a setting per machine.
		/// </summary>
		private void RefreshRendererChoices()
		{
			var decl = RendererDecl();
			var options = decl?.Options;

			_renderer.Items.Clear();
			if (options is { Count: > 0 })
			{
				foreach (var option in options) _renderer.Items.Add(new RendererChoice(option));
			}
			else
			{
				// nothing declared: the core has one renderer and does not name it
				_renderer.Items.Add(new RendererChoice("default"));
			}

			var wanted = decl?.DefaultValue as string;
			var index = wanted is null ? -1 : Array.IndexOf(RendererOptions, wanted);
			_renderer.SelectedIndex = index >= 0 ? index : 0;

			// one renderer is not a choice, and a disabled box still says what it is
			_renderer.Enabled = _renderer.Items.Count > 1;
			PinRenderer();
			ShowRendererCaveat();
		}

		/// <summary>
		/// What a hardware renderer costs, said where the choice is made rather
		/// than in a manual nobody opens. Only for the hardware ones: a person who
		/// picked a software renderer has nothing to be careful about, and a
		/// warning that is always on screen is a warning nobody reads.
		/// </summary>
		internal const string HardwareRendererCaveat =
			"Hardware renderers draw on your PC's GPU, which is fast. The picture is drawn outside the savestate, so "
			+ "it may not look exactly the same on another PC or driver - differences in video output may occur. The "
			+ "machine itself is unaffected: your run continues normally, its greenzone loads and picks up where you "
			+ "left off. Choose a software renderer for a movie whose video must look identical everywhere.";

		private void ShowRendererCaveat()
			=> _rendererCaveat.Text = IsHardware(ChosenRenderer) ? HardwareRendererCaveat : "";

		/// <summary>
		/// Whether a renderer's value names one that draws on the machine's own
		/// GPU. The suffix is the convention every core shares (see WaterboxCore),
		/// so this window understands a core it has never heard of.
		/// </summary>
		internal static bool IsHardware(string? renderer)
			=> renderer is not null && renderer.EndsWith("-hw", StringComparison.Ordinal);

		/// <summary>The caveat as it stands, for tests ("" when none is showing).</summary>
		public string RendererCaveatText => _rendererCaveat.Text;

		private WaterboxConfig.SettingDecl? RendererDecl()
		{
			if (_cfg is null) return null;
			var effective = _settings is null
				? new Dictionary<string, object>()
				: WaterboxCore.EffectiveSettingsFor(_cfg, _settings);
			return _cfg.SettingsFor(_cfg.MachineFor(effective))
				.FirstOrDefault(static decl => decl.Name == RendererSetting);
		}

		/// <summary>
		/// Writes the chosen renderer into the settings, where it is a setting like
		/// any other - so the project records it and the movie cites it. A core that
		/// declares no renderer has nothing to write.
		/// </summary>
		private void PinRenderer()
		{
			if (_settings is null || RendererDecl() is null) return;
			if (_renderer.SelectedItem is RendererChoice chosen) _settings.Values[RendererSetting] = chosen.Value;
		}

		/// <summary>
		/// Writes the chosen machine into the settings, where it is a setting like
		/// any other - so the project records it, the movie cites it, and the file
		/// and firmware gates can ask which machine this is.
		/// </summary>
		private void PinMachine()
		{
			if (_cfg?.HasMachines is not true || _settings is null) return;
			var index = Math.Max(0, Math.Min(_cfg.Machines.Count - 1, _machine.SelectedIndex));
			var machine = _cfg.Machines[index];
			_settings.Values[_cfg.MachineSetting] = machine.When is { Count: > 0 } ? machine.When[0] : machine.Id;
			// the machine decides which files the project takes, so a form built for
			// another machine is stale
			_declarationCore = null;
		}

		/// <summary>The machine chosen on page one, for tests.</summary>
		public string? ChosenMachine
			=> _cfg?.HasMachines is true && _machine.SelectedIndex >= 0
				? _cfg.Machines[_machine.SelectedIndex].Id
				: null;

		/// <summary>
		/// Cancel while a compile is running stops it: the sessions are killed and
		/// the window closes once they have. Closing the window does the same,
		/// because a window that has gone is not waiting for anything either - and
		/// neither can happen the direct way, since the compile is pumping
		/// messages from inside a click handler and nothing may unwind under it.
		/// </summary>
		private void CancelPressed()
		{
			if (_precompiling)
			{
				_precompileCancelled = true;
				_closeAfterPrecompile = true;
				_precompileStatus.Text = "Stopping...";
				return;
			}
			DialogResult = DialogResult.Cancel;
			Close();
		}

		protected override void OnFormClosing(FormClosingEventArgs e)
		{
			if (_precompiling)
			{
				_precompileCancelled = true;
				_closeAfterPrecompile = true;
				_precompileStatus.Text = "Stopping...";
				e.Cancel = true;
				return;
			}
			base.OnFormClosing(e);
		}

		/// <summary>Back walks over the steps that did not apply on the way here.</summary>
		private void GoBack()
		{
			for (var i = _page - 1; i >= 0; i--)
			{
				if (!PageApplies(i)) continue;
				ShowPage(i);
				return;
			}
			ShowPage(0);
		}

		private void ShowPage(int page)
		{
			_page = Math.Max(0, Math.Min(_pages.Length - 1, page));
			for (var i = 0; i < _pages.Length; i++) _pages[i].Visible = i == _page;
			_backButton.Enabled = _page > 0;
			_status.Text = "";
			UpdateNavLabels();
		}

		/// <summary>
		/// Whether a step applies at all. A machine that needs no firmware is not
		/// asked about firmware; a core that compiles nothing has nothing to
		/// compile. Both depend on the choices made so far, so this is re-asked
		/// whenever they change.
		/// </summary>
		private bool PageApplies(int page) => page switch
		{
			3 => AnyFirmwareRequired(),
			4 => PrecompileApplies(),
			_ => true,
		};

		/// <summary>The next step that applies after this one, or -1 when this is the last.</summary>
		private int NextApplicablePage(int page)
		{
			for (var i = page + 1; i < _pages.Length; i++)
			{
				if (PageApplies(i)) return i;
			}
			return -1;
		}

		private bool IsLastPage(int page) => NextApplicablePage(page) < 0;

		/// <summary>
		/// Whether this core compiles the game's code before running it
		/// (docs/compile-cache.md). Needs a core that says so and a game to
		/// compile: a project whose files are not settled yet has neither.
		/// </summary>
		private bool PrecompileApplies()
			=> _cfg?.Precompile is true && PrecompileRomPath() is not null;

		/// <summary>
		/// Which firmware the decisions so far call for - the cheap half of the
		/// firmware step (the engine's decision tree only; no folder is indexed
		/// and nothing is hashed).
		/// </summary>
		private bool AnyFirmwareRequired()
		{
			if (_cfg?.RawFirmwareJson is not { Length: not 0 } declJson) return false;
			var effective = WaterboxCore.EffectiveSettingsFor(_cfg, _settings);
			return Chimera.Emulation.Common.Engine.EngineFirmware.Evaluate(
				declJson,
				CurrentSlotsJson(),
				Newtonsoft.Json.JsonConvert.SerializeObject(effective)).Count is not 0;
		}

		private void UpdateNavLabels()
		{
			_nextButton.Text = IsLastPage(_page) ? "Create" : "Next >";
			UpdateCreateEnabled();
		}

		/// <summary>
		/// Create is not an error message: it is simply unavailable until the last
		/// step is answered - every required firmware provided, and every module
		/// this core must compile for the game compiled.
		/// </summary>
		private void UpdateCreateEnabled()
		{
			if (_precompiling) { _nextButton.Enabled = false; return; }
			if (!IsLastPage(_page)) { _nextButton.Enabled = true; return; }
			_nextButton.Enabled = (_page != 3 || MissingRequiredFirmware() is null)
				&& (_page != 4 || PrecompileSatisfied());
		}

		/// <summary>whether Create is currently available, for tests</summary>
		public bool CreateEnabled => _nextButton.Enabled;

		/// <summary>what the forward button offers right now - "Next &gt;" or "Create", for tests</summary>
		public string NextButtonText => _nextButton.Text;

		private void Advance()
		{
			switch (_page)
			{
				case 0:
					if (ChosenCore is null) { _status.Text = "pick a core (none are installed?)"; return; }
					if (!BuildSlotForm()) return;
					ShowPage(1);
					break;
				case 1:
					var complaint = CardinalityComplaint();
					if (complaint is not null) { _status.Text = complaint; return; }
					if (!BuildSettingsPage()) return;
					ShowPage(2);
					break;
				case 2:
				case 3:
				default:
					if (_page == 3)
					{
						var missing = MissingRequiredFirmware();
						if (missing is not null) { _status.Text = $"{missing} is required and not provided"; return; }
					}
					if (_page == 4)
					{
						if (!PrecompileSatisfied()) { _status.Text = "Compile this game's code first."; return; }
						Create();
						break;
					}
					var next = NextApplicablePage(_page);
					if (next < 0) { Create(); break; }
					if (next == 3) BuildFirmwarePage();
					if (next == 4) BuildPrecompilePage();
					ShowPage(next);
					break;
			}
		}

		// ---- the code this core compiles for this game ---------------------------

		/// <summary>
		/// The game a precompile session would compile: the first file of the
		/// first slot, which is the one a core boots. A slot list that is empty
		/// means there is nothing to compile yet.
		/// </summary>
		/// <summary>a game named directly, for the tests and the screenshots</summary>
		private string? _precompileRomOverride;

		private string? PrecompileRomPath()
		{
			if (_precompileRomOverride is not null) return _precompileRomOverride;
			foreach (var slot in _declaration?.Slots ?? [ ])
			{
				if (!_slotLists.TryGetValue(slot.Id, out var list)) continue;
				if (list.Items.OfType<PickedFile>().FirstOrDefault() is { } file) return file.Path;
			}
			return null;
		}

		private string? _romSha1Path, _romSha1;

		private string? PrecompileRomSha1()
		{
			var path = PrecompileRomPath();
			if (path is null || !File.Exists(path)) return null;
			if (_romSha1Path == path && _romSha1 is not null) return _romSha1;
			using var stream = File.OpenRead(path);
			using var sha1 = System.Security.Cryptography.SHA1.Create();
			_romSha1 = BitConverter.ToString(sha1.ComputeHash(stream)).Replace("-", "");
			_romSha1Path = path;
			return _romSha1;
		}

		private string? PrecompileCacheDir()
			=> _cfg is null ? null : WaterboxCore.CoreCacheDirectoryFor(_cfg);

		/// <summary>Every module this game needs is compiled and unchanged.</summary>
		private bool PrecompileSatisfied()
			=> _precompileManifest is { Files.Count: not 0 } manifest && manifest.Satisfied(PrecompileCacheDir());

		/// <summary>whether Create is available on the compile step, for tests</summary>
		public bool PrecompileReady => PrecompileSatisfied();

		/// <summary>what the compile step lists right now, for tests: name, hash, present</summary>
		public IReadOnlyList<(string Name, string Sha1, bool Present)> PrecompileEntries
			=> _precompileList.Items.Cast<ListViewItem>()
				.Select(i => (i.SubItems[0].Text, i.SubItems[1].Text, i.ForeColor == Color.ForestGreen))
				.ToList();

		private void BuildPrecompilePage()
		{
			_precompileManifest = CoreCacheManifest.Load(PrecompileCacheDir(), PrecompileRomSha1());
			RefreshPrecompileList();
		}

		private void RefreshPrecompileList()
		{
			var dir = PrecompileCacheDir();
			_precompileList.BeginUpdate();
			_precompileList.Items.Clear();
			foreach (var file in _precompileManifest?.Files ?? [ ])
			{
				var present = string.Equals(CoreCacheManifest.HashOf(dir, file.Name), file.Sha1, StringComparison.OrdinalIgnoreCase);
				AddPrecompileRow(file.Name, file.Sha1, present);
			}
			_precompileList.EndUpdate();
			UpdatePrecompileStatus();
		}

		/// <summary>
		/// One module: green when its object is on disk and still what it was.
		/// The name shown is the module's, not the whole path - the path is the
		/// core's business and is in the tooltip for anyone who wants it.
		/// </summary>
		private void AddPrecompileRow(string name, string sha1, bool present)
		{
			ListViewItem item = new(name) { ForeColor = present ? Color.ForestGreen : Color.Firebrick, ToolTipText = name };
			item.SubItems.Add(sha1);
			item.SubItems.Add(present ? "Compiled" : "Missing");
			_precompileList.Items.Add(item);
			// follow the work: a compile puts these there one at a time over
			// minutes, and a list that stays at the top shows the same first
			// screenful throughout, looking stalled while it is anything but.
			// Inside a BeginUpdate this costs nothing and leaves the finished
			// list where the newest row is.
			item.EnsureVisible();
		}

		/// <summary>
		/// The bar fills with the modules compiled, out of what this game needed
		/// last time. A game nobody has compiled yet has no total to fill
		/// against, and the bar says "working" rather than pretending to know.
		/// </summary>
		private void UpdatePrecompileBar()
		{
			if (_precompileExpected is 0)
			{
				_precompileBar.Style = ProgressBarStyle.Marquee;
				return;
			}
			_precompileBar.Style = ProgressBarStyle.Blocks;
			_precompileBar.Maximum = (int)Math.Max(1, _precompileExpected);
			_precompileBar.Value = Math.Min(_precompileList.Items.Count, _precompileBar.Maximum);
		}

		private void UpdatePrecompileStatus()
		{
			var total = _precompileList.Items.Count;
			var green = _precompileList.Items.Cast<ListViewItem>().Count(i => i.ForeColor == Color.ForestGreen);
			// While it runs, a row exists only once its module is compiled, so the
			// total cannot come from the list - it comes from the sessions, and
			// only once they have looked at the game. Until then there is nothing
			// honest to count, and saying so beats a number that means nothing.
			_precompileStatus.Text = _precompiling
				? _precompileExpected is not 0
					? $"Compiling {green} of {_precompileExpected} modules..."
					: green is 0
						? "Calculating..."
						: $"Compiling... {green} modules compiled"
				: total is 0
					? "This game has not been compiled yet."
					: green == total
						? $"All {total} modules compiled."
						: $"{total - green} of {total} modules are missing.";
			_precompileStatus.ForeColor = !_precompiling && total is not 0 && green == total ? Color.ForestGreen : SystemColors.ControlText;
			// Compile is offered exactly when there is compiling left to do
			_precompileButton.Enabled = !_precompiling && !PrecompileSatisfied();
			UpdateCreateEnabled();
		}

		/// <summary>
		/// Compiles the game, in as many sessions as the machine can use. The list
		/// fills as the objects land, so the wait shows what it is doing rather
		/// than a bar with no content.
		/// </summary>
		private void RunPrecompile()
		{
			var romPath = PrecompileRomPath();
			if (romPath is null || ChosenCore is null) return;

			_precompiling = true;
			_precompileCancelled = false;
			// what this game needed last time, when it was compiled before
			_precompileExpected = (uint)(_precompileManifest?.Files.Count ?? 0);
			_precompileDone = 0;
			_precompileBar.Visible = true;
			_precompileBar.Value = 0;
			// nothing behind this step may be changed while it runs: the sessions
			// are compiling for the game and the settings as they stand
			_backButton.Enabled = false;
			_precompileList.Items.Clear();
			UpdatePrecompileStatus();
			// the click handler owns the thread from here, so what it has just
			// said must be painted before it does anything slow - and hashing a
			// disc image is slow
			_precompileStatus.Refresh();
			_precompileButton.Refresh();
			Application.DoEvents();

			var romSha1 = PrecompileRomSha1();
			var dir = PrecompileCacheDir();
			if (romSha1 is null || dir is null)
			{
				_precompiling = false;
				_precompileBar.Visible = false;
				_backButton.Enabled = _page > 0;
				UpdatePrecompileStatus();
				return;
			}

			var seen = new HashSet<string>(StringComparer.Ordinal);
			void Entry(PrecompileOrchestrator.Entry entry)
			{
				// arrives on a worker's reader thread; the list is the UI's
				BeginInvoke(new Action(() =>
				{
					if (!seen.Add(entry.Name)) return;
					AddPrecompileRow(entry.Name, entry.Sha1, present: true);
					UpdatePrecompileBar();
					UpdatePrecompileStatus();
				}));
			}
			// The sessions cannot tell how many modules this game has until they
			// have compiled them: most objects come from the executable and what
			// it loads, which every session walks and each compiles a share of.
			// So the only honest total is one a previous compile left behind.
			void Progress(uint modulesDone, uint modulesTotal)
			{
			}

			// The sessions are child processes reading the config off disk, so
			// whatever firmware was chosen on the firmware page has to BE there
			// before they start. Without this a core that needs firmware compiles
			// nothing: the session boots, is refused for want of it, and exits.
			_rememberFirmwareNow(ChosenCore.Name, ProvidedFirmwarePaths);

			var manifest = PrecompileOrchestrator.Run(
				ChosenCore.Path, _configPath, romPath, romSha1, dir, Entry, Progress, cancelled: PumpAndCheckCancel,
				firmware: ProvidedFirmwarePaths);

			_precompiling = false;
			_precompileBar.Visible = false;
			_backButton.Enabled = _page > 0;
			if (manifest is not null) _precompileManifest = manifest;
			RefreshPrecompileList();
			if (_closeAfterPrecompile)
			{
				// the window was asked to go while the sessions were running; they
				// have stopped, so now it can
				DialogResult = DialogResult.Cancel;
				Close();
				return;
			}
			if (manifest is null)
			{
				// Say WHY. The sessions are headless frontends, so a refusal is a
				// modal they could not show - most often firmware this core calls
				// optional but this GAME needs. Without the reason the person is
				// told only that nothing happened, and on Windows the parent has no
				// console for the detail to go to either.
				_status.Text = PrecompileOrchestrator.LastFailure is { Length: not 0 } why
					? $"The compile stopped: {why}"
					: "The compile was stopped; this game is not compiled.";
			}
		}

		/// <summary>
		/// Keeps the window alive while the sessions work - without this the list
		/// never paints and the window is a grey rectangle - and reports whether
		/// the person asked to stop while it was pumping.
		/// </summary>
		private bool PumpAndCheckCancel()
		{
			Application.DoEvents();
			return _precompileCancelled || IsDisposed;
		}

		// ---- the core-informed form ----------------------------------------------

		/// <summary>The chosen package's file_slots.json rendered as slot groups; false when the package has none.</summary>
		private bool BuildSlotForm()
		{
			var core = ChosenCore!;
			if (_declarationCore == core.Path && _declaration is not null) return true;


			string? declarationJson = null;
			try
			{
				using var pkg = EnginePackage.Open(core.Path);
				declarationJson = pkg?.EntryText("file_slots.json");
			}
			catch (InvalidOperationException)
			{
				// unreadable package: the message below covers it
			}
			_declaration = ProjectSlotDeclaration.Parse(declarationJson);
			if (_declaration is null)
			{
				_status.Text = $"{core.Name} declares no file form (no usable file_slots.json in the package)";
				return false;
			}
			_declarationCore = core.Path;
			RenderSlotForm();
			return true;
		}

		/// <summary>Renders a given declaration directly - the test and screenshot door.</summary>
		internal void UseDeclaration(ProjectSlotDeclaration declaration)
		{
			_declaration = declaration;
			_declarationCore = "<injected>";
			RenderSlotForm();
			ShowPage(1);
		}

		/// <summary>Runs the real exposure gate over a given config - the test door.</summary>
		internal void UseSettingsFrom(WaterboxConfig cfg)
		{
			_cfg = cfg;
			_settings = new WaterboxCoreSettings();
			RefreshRendererChoices();   // as choosing the core does
			RefreshExposedSettings();
			_settingsGrid.SelectedObject = _settings;
			ShowPage(2);
		}

		/// <summary>
		/// Shows the compile step for a core and a game, without walking the pages
		/// before it - the test and screenshot door for the step.
		/// </summary>
		/// <summary>
		/// Starts the compile the way the button does - the test door for the
		/// ORDER the sessions depend on: the firmware they will need has to be in
		/// the config before they are spawned, because they are separate processes
		/// that read it off disk.
		/// </summary>
		internal void RunPrecompileForTest() => RunPrecompile();

		internal void UsePrecompileFrom(WaterboxConfig cfg, string cacheRoot, string romPath)
		{
			_cfg = cfg;
			_settings ??= new WaterboxCoreSettings();
			WaterboxCore.CoreCacheRoot = cacheRoot;
			_precompileRomOverride = romPath;
			BuildPrecompilePage();
			ShowPage(4);
		}

		/// <summary>
		/// Offers a given core's renderers without leaving page one - the test and
		/// screenshot door for the dropdown beside the core.
		/// </summary>
		internal void UseRenderersFrom(WaterboxConfig cfg)
		{
			_cfg = cfg;
			_settings = new WaterboxCoreSettings();
			RefreshRendererChoices();
		}

		/// <summary>the renderers offered on page one, in order, for tests</summary>
		public string[] RendererOptions
			=> _renderer.Items.Cast<RendererChoice>().Select(static item => item.Value).ToArray();

		/// <summary>how those renderers are spelled in the dropdown, for tests</summary>
		public string[] RendererLabels
			=> _renderer.Items.Cast<RendererChoice>().Select(static item => item.ToString()).ToArray();

		/// <summary>the renderer chosen on page one, for tests</summary>
		public string? ChosenRenderer => (_renderer.SelectedItem as RendererChoice)?.Value;

		/// <summary>whether the renderer is a choice at all, for tests</summary>
		public bool RendererIsAChoice => _renderer.Enabled;

		/// <summary>what the project would record for a setting - for tests</summary>
		public object? SettingValue(string name)
			=> _settings?.Values is not null && _settings.Values.TryGetValue(name, out var value) ? value : null;

		/// <summary>picks a renderer as the dropdown would - for tests</summary>
		internal void SetRenderer(string name)
		{
			var index = Array.IndexOf(RendererOptions, name);
			if (index >= 0) _renderer.SelectedIndex = index;
		}

		/// <summary>the exposed settings, in order, for tests</summary>
		public string[] ExposedSettingNames
			=> (_settings?.Declarations ?? [ ]).Select(static d => d.Name).ToArray();

		/// <summary>sets a value as the grid would, re-running the gate - for tests</summary>
		internal void SetSettingValue(string name, object value)
		{
			// exactly what the grid does when a value is edited, so the test
			// door cannot drift from the real path
			_settings!.Values[name] = value;
			RefreshExposedSettings();
			UpdateNavLabels();
		}

		/// <summary>Renders given sync-setting declarations directly - the test and screenshot door.</summary>
		internal void UseSettingsDecls(IReadOnlyList<WaterboxConfig.SettingDecl> declarations)
		{
			_settings = new WaterboxCoreSettings { Declarations = declarations };
			_settingsGrid.SelectedObject = _settings;
			ShowPage(2);
		}

		private void RenderSlotForm()
		{
			_slotsHost.Controls.Clear();
			_slotLists.Clear();
			_slotGroups.Clear();
			var y = 0;
			// What the chosen MACHINE exposes, before any file is picked. A slot
			// only a different sub-system takes is not an option that is greyed -
			// it is not an option at all, so it is not shown. Slots gated by other
			// slots' CONTENTS stay: those come back when a file is unloaded, and a
			// row that vanishes underfoot explains nothing.
			var machineExposed = EngineSlotsGate.Evaluate(
				_declaration!.RawJson,
				CurrentSlotsJson(),
				Newtonsoft.Json.JsonConvert.SerializeObject(EffectiveSettings()));
			foreach (var declared in _declaration!.Slots)
			{
				if (declared.SettingGatedOnly && !machineExposed.Contains(declared.Id)) continue;
				var slot = ForMachine(declared);
				GroupBox group = new()
				{
					Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
					Location = Pt(0, y),
					Size = new(UIHelper.ScaleX(520), UIHelper.ScaleY(96)),
					Text = $"{slot.Title}  ({slot.CardinalityText}{(slot.Formats.Count is 0 ? "" : $"; {string.Join(", ", slot.Formats.Select(static f => "." + f))}")})",
				};
				// the core's own explanation of the slot, on hover - the row IS
				// the affordance, so there is no glyph to wonder about
				var help = slot.Help.Length is 0 ? "the core did not explain this slot" : slot.Help;
				_tips.SetToolTip(group, help);
				ListBox list = new()
				{
					Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
					IntegralHeight = false,
					Location = Pt(8, 18),
					Size = new(UIHelper.ScaleX(402), UIHelper.ScaleY(70)),
				};
				_tips.SetToolTip(list, help);

				// A file arrives here the same way it arrives anywhere else on a
				// desktop: dragged onto the thing it belongs to. The list IS the
				// target, so what a person drops on it lands in that slot and no
				// other - which is the whole reason not to make the window one
				// big drop zone that has to guess.
				list.AllowDrop = true;
				list.DragEnter += (_, e) => e.Effect = DroppedPaths(e).Length is 0
					? DragDropEffects.None
					: DragDropEffects.Copy;
				list.DragDrop += (_, e) =>
				{
					foreach (var path in DroppedPaths(e)) AddFileToSlot(slot.Id, path);
				};

				Button add = new() { AutoSize = true, Location = Pt(416, 18), Text = "Add..." };
				add.Click += (_, _) =>
				{
					foreach (var path in _pickFiles(slot)) AddFileToSlot(slot.Id, path);
				};
				Button remove = new() { AutoSize = true, Location = Pt(416, 48), Text = "Remove" };
				remove.Click += (_, _) =>
				{
					if (list.SelectedIndex >= 0) list.Items.RemoveAt(list.SelectedIndex);
					RefreshSlotAvailability();
				};
				group.Controls.AddRange([ list, add, remove ]);
				_slotsHost.Controls.Add(group);
				_slotLists[slot.Id] = list;
				_slotGroups[slot.Id] = group;
				group.Tag = group.Text;
				y += 102;
			}
			RefreshSlotAvailability();
		}

		/// <summary>
		/// The settings as the decision trees should read them: the package's
		/// defaults and per-machine narrowing applied over what has been chosen.
		/// Without a package there is nothing to apply, but the chosen values are
		/// still the truth - answering with an empty map instead would make every
		/// condition that names a setting quietly false.
		/// </summary>
		private System.Collections.Generic.Dictionary<string, object> EffectiveSettings()
			=> _cfg is not null
				? WaterboxCore.EffectiveSettingsFor(_cfg, _settings)
				: new(_settings?.Values ?? new System.Collections.Generic.Dictionary<string, object>());

		/// <summary>
		/// A slot's formats as the chosen machine has them. A package that is
		/// several machines declares which rom extensions each machine claims,
		/// and a slot offering any of them is narrowed to the claimed ones: a
		/// Famicom Disk System project takes .fds and nothing else, a Genesis
		/// project does not offer .sms. A slot the machine claims none of (save
		/// data, patches) is not the machine's to narrow and keeps its own list.
		/// </summary>
		private ProjectSlotDeclaration.Slot ForMachine(ProjectSlotDeclaration.Slot slot)
		{
			var machine = _cfg?.MachineFor(EffectiveSettings());
			if (machine?.Extensions is not { Count: > 0 } || slot.Formats.Count is 0) return slot;
			var claimed = slot.Formats
				.Where(f => machine.Extensions.ContainsKey("." + f.ToLowerInvariant()))
				.ToList();
			if (claimed.Count is 0 || claimed.Count == slot.Formats.Count) return slot;
			return new()
			{
				Id = slot.Id,
				Title = slot.Title,
				Min = slot.Min,
				Max = slot.Max,
				Formats = claimed,
				Help = slot.Help,
				SettingGatedOnly = slot.SettingGatedOnly,
			};
		}

		/// <summary>
		/// The slots themselves join the decision tree: a slot's exposedWhen is
		/// evaluated over the CURRENT picks, so filling one slot can make
		/// another unavailable until it is unloaded (a Famicom disk rules out
		/// a cartridge and vice versa). Greyed, never hidden - the person sees
		/// what the machine could also take and why it cannot right now.
		/// </summary>
		private void RefreshSlotAvailability()
		{
			if (_declaration is null) return;
			// the settings go in because the MACHINE is one: a package that is
			// several machines has slots only some of them have
			var exposed = EngineSlotsGate.Evaluate(
				_declaration.RawJson,
				CurrentSlotsJson(),
				Newtonsoft.Json.JsonConvert.SerializeObject(EffectiveSettings()));
			foreach (var slot in _declaration.Slots)
			{
				if (!_slotGroups.TryGetValue(slot.Id, out var group)) continue;
				var available = exposed.Contains(slot.Id);
				group.Enabled = available;
				group.Text = available
					? (string)group.Tag!
					: $"{(string)group.Tag!} - unavailable with the current files";
			}
		}

		/// <summary>Whether a slot is on the form at all, for tests: a slot only
		/// a different machine takes is not rendered, where one ruled out by the
		/// current FILES stays visible and greyed.</summary>
		public bool SlotRendered(string slotId) => _slotGroups.ContainsKey(slotId);

		/// <summary>The formats a slot offers as the chosen machine narrows them, for tests.</summary>
		internal IReadOnlyList<string> OfferedFormats(string slotId)
		{
			var slot = _declaration?.Slots.FirstOrDefault(s => s.Id == slotId);
			return slot is null ? [ ] : ForMachine(slot).Formats;
		}

		/// <summary>Whether a slot currently accepts files, for tests.</summary>
		public bool SlotAvailable(string slotId)
			=> _slotGroups.TryGetValue(slotId, out var group) && group.Enabled;

		/// <summary>
		/// One picked file, kept as its canonical (bare) name plus where it is
		/// now. A cue sheet counts as ONE pick: its referenced track files ride
		/// along automatically at creation (the engine adds them as support
		/// files, from the cue's own folder), so the row says how many.
		/// </summary>
		private sealed record PickedFile(string Name, string Path, int TrackCount = 0)
		{
			public override string ToString()
				=> TrackCount is 0 ? Name : $"{Name}  (+ {TrackCount} track file{(TrackCount is 1 ? "" : "s")})";
		}

		/// <summary>
		/// The files in a drag, if it is carrying any. Directories are dropped
		/// silently: a slot takes files, and expanding a folder into one would be
		/// a different decision made on the person's behalf.
		/// </summary>
		private static string[] DroppedPaths(DragEventArgs e)
			=> e.Data?.GetDataPresent(DataFormats.FileDrop) is true
				&& e.Data.GetData(DataFormats.FileDrop) is string[] paths
					? paths.Where(File.Exists).ToArray()
					: [ ];

		/// <summary>Adds a file to a slot's list; public in behaviour so tests can drive the form without a picker.</summary>
		public void AddFileToSlot(string slotId, string path)
		{
			if (!_slotLists.TryGetValue(slotId, out var list)) return;
			if (_slotGroups.TryGetValue(slotId, out var g) && !g.Enabled) return;
			var name = Path.GetFileName(path);
			if (list.Items.OfType<PickedFile>().Any(f => f.Name == name)) return;

			// a cue sheet is one pick, but its track files must be next to it -
			// the same all-or-nothing rule the engine applies at creation,
			// raised here so the complaint lands at pick time
			var tracks = 0;
			if (name.EndsWith(".cue", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
			{
				var refs = EngineCue.References(File.ReadAllBytes(path));
				var folder = Path.GetDirectoryName(path) ?? "";
				var missing = refs.Where(r => !File.Exists(Path.Combine(folder, r))).ToList();
				if (missing.Count is not 0)
				{
					_status.Text = $"{name} references {missing[0]}, which is not next to it - a cue's files are added together";
					return;
				}
				tracks = refs.Count;
			}

			list.Items.Add(new PickedFile(name, path, tracks));

			// A slot that names its formats has still not been given a rule: the
			// picker offers "All files" too, and a person who renamed something
			// knows better than this form does. So an unexpected extension is
			// SAID rather than refused - the complaint a mis-drop would otherwise
			// become is an engine error at Create, long after the mistake.
			var declared = _declaration?.Slots.FirstOrDefault(s => s.Id == slotId);
			var slot = declared is null ? null : ForMachine(declared);
			var extension = Path.GetExtension(name).TrimStart('.');
			_status.Text = slot is { Formats.Count: > 0 }
				&& !slot.Formats.Any(f => string.Equals(f, extension, StringComparison.OrdinalIgnoreCase))
					? $"{name} is not one of the {string.Join(", ", slot.Formats.Select(static f => "." + f))} this slot expects"
					: "";
			RefreshSlotAvailability();
		}

		/// <summary>The remove button's behaviour, for tests: drop one picked file by canonical name.</summary>
		public void RemoveFileFromSlot(string slotId, string name)
		{
			if (!_slotLists.TryGetValue(slotId, out var list)) return;
			var found = list.Items.OfType<PickedFile>().FirstOrDefault(f => f.Name == name);
			if (found is null) return;
			list.Items.Remove(found);
			RefreshSlotAvailability();
		}

		/// <summary>Whether a slot's list takes dropped files, for tests.</summary>
		public bool SlotAcceptsDrops(string slotId)
			=> _slotLists.TryGetValue(slotId, out var list) && list.AllowDrop;

		/// <summary>What still blocks the file page, for tests (null = nothing).</summary>
		public string? FilesComplaint() => CardinalityComplaint();

		/// <summary>The rows as the list shows them (cue rows carry their track count), for tests.</summary>
		public string[] SlotRowTexts(string slotId)
			=> _slotLists.TryGetValue(slotId, out var list)
				? list.Items.OfType<PickedFile>().Select(static f => f.ToString()).ToArray()
				: [ ];

		/// <summary>The page's live complaint line, for tests.</summary>
		public string StatusText => _status.Text;

		/// <summary>The canonical names in one slot, in order, for tests.</summary>
		public string[] SlotFileNames(string slotId)
			=> _slotLists.TryGetValue(slotId, out var list)
				? list.Items.OfType<PickedFile>().Select(static f => f.Name).ToArray()
				: [ ];

		/// <summary>The declaration's own cardinality rules, phrased for the person filling the form.</summary>
		private string? CardinalityComplaint()
		{
			foreach (var slot in _declaration!.Slots)
			{
				// a slot the machine hides was never rendered, and demands nothing
				if (!_slotLists.TryGetValue(slot.Id, out var list)) continue;
				var count = list.Items.Count;
				if (count < slot.Min && SlotAvailable(slot.Id)) return $"{slot.Title}: needs {slot.CardinalityText}, has {count}";
				if (slot.Max >= 0 && count > slot.Max) return $"{slot.Title}: takes {slot.CardinalityText}, has {count}";
			}
			foreach (var group in _declaration.AtLeastOneOf)
			{
				var total = group.Sum(id => _slotLists.TryGetValue(id, out var l) ? l.Items.Count : 0);
				if (total is 0)
				{
					var titles = group.Select(id => _declaration.Slots.FirstOrDefault(s => s.Id == id)?.Title ?? id);
					return $"at least one file is needed across: {string.Join(", ", titles)}";
				}
			}
			return null;
		}

		// ---- settings -------------------------------------------------------------

		private bool BuildSettingsPage()
		{
			// the package was read when its core was chosen (the machine is picked
			// there, and everything since has depended on it)
			if (_cfg is null)
			{
				_status.Text = $"{ChosenCore?.Name} has no usable {WaterboxCoreFactory.ConfigFileName} in its package";
				return false;
			}
			_settings ??= new WaterboxCoreSettings();
			RefreshExposedSettings();
			_settingsGrid.SelectedObject = _settings;
			return true;
		}

		/// <summary>
		/// The settings the DECISIONS expose (docs/project.md): the chosen files
		/// gate which settings exist at all (a Game Gear cart exposes its
		/// sound chip, a Genesis cart its own), and a setting may gate further
		/// settings - so the set is re-evaluated when a value changes.
		/// </summary>
		private void RefreshExposedSettings()
		{
			if (_cfg is null || _settings is null) return;
			var effective = WaterboxCore.EffectiveSettingsFor(_cfg, _settings);
			var exposed = Chimera.Emulation.Common.Engine.EngineSettingsGate.Evaluate(
				_cfg.RawSettingsJson,
				CurrentSlotsJson(),
				Newtonsoft.Json.JsonConvert.SerializeObject(effective));
			// as the CHOSEN MACHINE has them: a package that is several machines
			// narrows some settings per machine (a Master System port takes a pad
			// or nothing, where a Mega Drive port takes six devices)
			var all = _cfg.SettingsFor(_cfg.MachineFor(effective));
			var declarations = exposed
				.Where(entry => entry.Index >= 0 && entry.Index < all.Count && all[entry.Index].Name == entry.Name)
				.Select(entry => all[entry.Index])
				// ...except the renderer and the machine, which are asked beside
				// the core on page one and would only be asked twice here
				.Where(decl => decl.Name != RendererSetting && decl.Name != _cfg.MachineSetting)
				.ToList();
			var current = _settings.Declarations;
			if (current is not null && current.Count == declarations.Count
				&& current.Zip(declarations, static (a, b) => ReferenceEquals(a, b)).All(static same => same))
			{
				return; // the exposed set did not change
			}
			_settings.Declarations = declarations;
			_settingsGrid.Refresh();
		}

		/// <summary>Renders given firmware needs directly - the test and screenshot door.</summary>
		internal void UseFirmwareNeeds(
			WaterboxConfig cfg,
			IReadOnlyList<(string Id, int Index)> needed,
			IReadOnlyList<FirmwareLocator.IndexedFile>? index = null)
		{
			_cfg = cfg;
			_firmwareIndex = index ?? [ ];
			BuildFirmwareNeeds(needed);
			RenderFirmwareRows();
			ShowPage(3);
		}

		// ---- firmware: the last word, decided by everything before it ------------

		/// <summary>The slot map exactly as the session will mount it, from the form's current picks.</summary>
		private string CurrentSlotsJson()
		{
			Newtonsoft.Json.Linq.JObject slots = new();
			if (_declaration is null) return slots.ToString(Newtonsoft.Json.Formatting.None);
			foreach (var slot in _declaration.Slots)
			{
				if (!_slotLists.TryGetValue(slot.Id, out var list) || list.Items.Count is 0) continue;
				Newtonsoft.Json.Linq.JArray names = new();
				foreach (var file in list.Items.OfType<PickedFile>()) names.Add(file.Name);
				slots[slot.Id] = names;
			}
			return slots.ToString(Newtonsoft.Json.Formatting.None);
		}

		private void BuildFirmwarePage()
		{
			var effective = WaterboxCore.EffectiveSettingsFor(
				_cfg ?? new WaterboxConfig(), _settings);
			var needed = Chimera.Emulation.Common.Engine.EngineFirmware.Evaluate(
				_cfg?.RawFirmwareJson ?? "[]",
				CurrentSlotsJson(),
				Newtonsoft.Json.JsonConvert.SerializeObject(effective));

			// the Firmware folder answers first, plus anything remembered from
			// earlier sessions - every dump ever chosen for this core, not only
			// the one in use per id - and the user is only asked for what neither has
			var remembered = needed
				.Select(need => _rememberedFirmwarePath(ChosenCoreName ?? "", need.Id))
				.Where(static path => path is not null)
				.Select(static path => path!)
				.Concat(_rememberedFirmwarePaths(ChosenCoreName ?? ""));
			_firmwareIndex = FirmwareLocator.BuildIndex(_firmwareSearchDirs, remembered);
			BuildFirmwareNeeds(needed);
			RenderFirmwareRows();
		}

		private void BuildFirmwareNeeds(IReadOnlyList<(string Id, int Index)> needed)
		{
			var decls = _cfg?.Firmware ?? [ ];
			_firmwareNeeds = needed.Select(entry =>
			{
				FirmwareNeed need = new()
				{
					Id = entry.Id,
					Decl = entry.Index >= 0 && entry.Index < decls.Count && decls[entry.Index].Id == entry.Id
						? decls[entry.Index]
						: decls.FirstOrDefault(d => d.Id == entry.Id),
				};
				// A requirement that pins a hash is one exact file: the folder
				// either holds it (under any name) or the user is asked.
				//
				// One that pins none - an Xbox disk image, a console's own
				// identity dump, files no two people have the same copy of - can
				// only be recognised by the name the core declares, or by where
				// this machine last had it. Without that every xemu project asked
				// for all four by hand, with the files sitting in the Firmware
				// folder under exactly the names the page was showing (issue #38).
				if (need.Decl is not null)
				{
					var found = FirmwareLocator.FindEither(need.Decl, _firmwareIndex);
					if (found is null && string.IsNullOrEmpty(need.Decl.Sha1)
						&& _rememberedFirmwarePath(ChosenCoreName ?? "", need.Id) is { } last)
					{
						found = _firmwareIndex.FirstOrDefault(f =>
							f.Path.Equals(System.IO.Path.GetFullPath(last), StringComparison.OrdinalIgnoreCase));
					}
					if (found is not null)
					{
						need.ChosenPath = found.Path;
						need.ChosenSha1 = found.Sha1;
					}
				}
				return need;
			}).ToList();
		}

		private void RenderFirmwareRows()
		{
			var selected = _firmwareList.SelectedIndices.Count is 0 ? -1 : _firmwareList.SelectedIndices[0];
			_firmwareList.BeginUpdate();
			_firmwareList.Items.Clear();
			foreach (var need in _firmwareNeeds)
			{
				var title = need.Decl?.DisplayName ?? need.Id;
				if (!string.IsNullOrEmpty(need.Decl?.Label)) title += $"  ({need.Decl!.Label})";
				// no tick glyph: it renders badly at this size on every platform, and
				// prefixing only the satisfied rows left the column ragged. Green
				// against red says the same thing and says it in line.
				ListViewItem item = new(title)
				{
					ToolTipText = need.Decl?.Description ?? "",
					ForeColor = need.Satisfied ? System.Drawing.Color.DarkGreen : System.Drawing.Color.Firebrick,
				};
				item.SubItems.Add(need.Decl?.Name ?? "");
				item.SubItems.Add(string.IsNullOrEmpty(need.Decl?.Sha1) ? "(your own dump)" : need.Decl!.Sha1);
				item.SubItems.Add(need.ChosenPath is not null
					? $"found: {Path.GetFileName(need.ChosenPath)}"
					: need.Satisfied ? "optional - core default used" : "not found");
				_firmwareList.Items.Add(item);
			}
			if (_firmwareNeeds.Count is 0)
			{
				_firmwareList.Items.Add(new ListViewItem("(these decisions need no firmware)"));
			}
			if (selected >= 0 && selected < _firmwareList.Items.Count) _firmwareList.Items[selected].Selected = true;
			_firmwareList.EndUpdate();
			UpdateFirmwareButtons();
			UpdateCreateEnabled();
		}

		private FirmwareNeed? SelectedFirmwareNeed()
		{
			var index = _firmwareList.SelectedIndices.Count is 0 ? -1 : _firmwareList.SelectedIndices[0];
			return index >= 0 && index < _firmwareNeeds.Count ? _firmwareNeeds[index] : null;
		}

		private void UpdateFirmwareButtons()
		{
			var need = SelectedFirmwareNeed();
			_firmwareSetButton.Enabled = need is not null;
			_firmwareClearButton.Enabled = need?.ChosenPath is not null;
		}

		private void SetFirmwareFile()
		{
			if (SelectedFirmwareNeed() is not { } need) return;
			var path = _pickFirmwareFile($"Locate {need.Decl?.DisplayName ?? need.Id}");
			if (path is null) return;
			ProvideFirmware(need.Id, path);
		}

		/// <summary>
		/// Points one requirement at a file the user chose - allowed even when
		/// the folder already found one. The hash decides: the requirement names
		/// ONE exact file, and only that file satisfies it.
		/// </summary>
		public void ProvideFirmware(string id, string path)
		{
			var need = _firmwareNeeds.FirstOrDefault(n => n.Id == id);
			if (need is null) return;
			byte[] bytes;
			try
			{
				bytes = System.IO.File.ReadAllBytes(path);
			}
			catch (System.IO.IOException ex)
			{
				_status.Text = ex.Message;
				return;
			}
			var sha1 = Chimera.Emulation.Common.Engine.ChimeraEngine.Sha1Hex(bytes);
			// a pinned entry names ONE exact file; an unpinned one (no declared
			// hash - a file only the user can own, like a console's own font
			// region nothing ships) takes what is chosen and records its hash
			if (!string.IsNullOrEmpty(need.Decl?.Sha1)
				&& !sha1.Equals(need.Decl!.Sha1, StringComparison.OrdinalIgnoreCase))
			{
				_status.Text = $"{System.IO.Path.GetFileName(path)} is not this file: its hash is {sha1},"
					+ $" the requirement is {need.Decl!.Sha1}";
				return;
			}
			need.ChosenPath = System.IO.Path.GetFullPath(path);
			need.ChosenSha1 = sha1;
			_status.Text = "";
			RenderFirmwareRows();
		}

		/// <summary>
		/// "My files are somewhere in here": scans a folder (subfolders
		/// included) and fills every requirement it can. A hash-pinned
		/// requirement takes only its exact file, any name; an unpinned one
		/// takes the file bearing its declared name, hashed and recorded like
		/// any hand-picked choice. What the user already chose is left alone.
		/// </summary>
		private void ScanFirmwareFolder()
		{
			var folder = _pickFirmwareFolder?.Invoke();
			if (folder is null) return;
			UseWaitCursor = true;
			try
			{
				var found = Chimera.Client.Common.ProjectFolderScan
					.Enumerate(folder)
					.Take(Chimera.Client.Common.ProjectFolderScan.MaxFiles)
					.ToList();
				// one hashed index answers every pinned requirement
				var index = FirmwareLocator.BuildIndex([ ], found);
				_firmwareIndex = _firmwareIndex.Concat(index).ToList();
				var filled = 0;
				foreach (var need in _firmwareNeeds)
				{
					if (need.ChosenPath is not null || need.Decl is null) continue;
					if (!string.IsNullOrEmpty(need.Decl.Sha1))
					{
						if (FirmwareLocator.FindFor(need.Decl, index) is { } match)
						{
							need.ChosenPath = match.Path;
							need.ChosenSha1 = match.Sha1;
							filled++;
						}
						continue;
					}
					// unpinned: the declared file name is the only lead - and it
					// also reaches files too large for the hash index (a disk
					// image). ProvideFirmware hashes and records it properly.
					var name = need.Decl.Name;
					if (string.IsNullOrEmpty(name)) continue;
					var named = found.FirstOrDefault(f =>
						System.IO.Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase));
					if (named is null) continue;
					ProvideFirmware(need.Id, named);
					if (need.ChosenPath is not null) filled++;
				}
				_status.Text = filled is 0
					? $"scanned {found.Count} files; nothing new matched"
					: $"scanned {found.Count} files; {filled} requirement{(filled is 1 ? "" : "s")} resolved";
			}
			finally
			{
				UseWaitCursor = false;
			}
			RenderFirmwareRows();
		}

		private void ClearFirmwareFile()
		{
			if (SelectedFirmwareNeed() is not { } need) return;
			need.ChosenPath = null;
			RenderFirmwareRows();
		}

		private string? MissingRequiredFirmware()
		{
			foreach (var need in _firmwareNeeds)
			{
				if (!need.Satisfied) return need.Decl?.DisplayName ?? need.Id;
			}
			return null;
		}

		/// <summary>What was found or chosen, for the owner to remember paths per core (Config.CoreFirmware).</summary>
		public IReadOnlyDictionary<string, string> ProvidedFirmwarePaths
			=> _firmwareNeeds.Where(static n => n.ChosenPath is not null)
				.ToDictionary(static n => n.Id, static n => n.ChosenPath!);

		/// <summary>The same, by declaration: what to remember under each dump's own key, so the survey and later projects find it.</summary>
		public IReadOnlyList<(Chimera.Emulation.Common.CoreFirmwareDecl Decl, string Path)> ProvidedFirmwareDumps
			=> _firmwareNeeds.Where(static n => n.ChosenPath is not null && n.Decl is not null)
				.Select(static n => (n.Decl!, n.ChosenPath!))
				.ToList();

		/// <summary>the requirement states, for tests</summary>
		public bool FirmwareSatisfied(string id)
			=> _firmwareNeeds.FirstOrDefault(n => n.Id == id)?.Satisfied is true;

		public string? ChosenFirmwarePath(string id)
			=> _firmwareNeeds.FirstOrDefault(n => n.Id == id)?.ChosenPath;

		// ---- creation -------------------------------------------------------------

		private void Create()
		{
			var core = ChosenCore!;
			// NOT disposed here on success: the caller takes ownership of the
			// finished project and boots the machine from it. On any failure
			// below we dispose it ourselves and stay on the page.
			// Create hashes every file the project takes - a disc image is
			// gigabytes - so the wait says which file it is on
			using var progress = ProgressDialog.Begin(this, "Creating project");
			progress.Step("hashing the project's files");
			var project = EngineProject.New();
			project.SetCore(core.Name, core.Version ?? "", core.Sha1 ?? "");
			// The platform, stamped at creation: reopening forces the pinned core
			// through the movie queue, and that check wants to know the system. A
			// package that is several machines says which one through its settings,
			// which the user has just chosen.
			var system = (_cfg is not null && _settings is not null
					? _cfg.MachineFor(WaterboxCore.EffectiveSettingsFor(_cfg, _settings))?.Id
					: null)
				?? core.Systems.FirstOrDefault();
			if (system is { Length: not 0 })
			{
				project.HeaderSet("Platform", system);
			}

			try
			{
				foreach (var slot in _declaration!.Slots)
				{
					if (!_slotLists.TryGetValue(slot.Id, out var list)) continue;
					foreach (var file in list.Items.OfType<PickedFile>())
					{
						project.FileAdd(file.Name, slot.Id, file.Path);
					}
				}
			}
			catch (InvalidOperationException ex)
			{
				_status.Text = ex.Message;
				project.Dispose();
				ShowPage(1);
				return;
			}

			// the project records every EXPOSED sync setting explicitly, at its
			// effective value: the settings section is exactly the knobs these
			// decisions offered, nothing else
			{
				Dictionary<string, object> recorded = new();
				foreach (var decl in _settings?.Declarations ?? [ ])
				{
					recorded[decl.Name] = _settings!.Values is not null
						&& _settings.Values.TryGetValue(decl.Name, out var value)
							? value
							: decl.DefaultValue;
				}
				// the renderer and the machine are not in that list - both are
				// chosen on page one - but they are settings like any other and
				// the project records them the same way; the machine especially,
				// because the slot gates and the engine's own validation read it
				if (RendererDecl() is { } rendererDecl)
				{
					recorded[rendererDecl.Name] = _settings?.Values is not null
						&& _settings.Values.TryGetValue(rendererDecl.Name, out var chosen)
							? chosen
							: rendererDecl.DefaultValue;
				}
				if (_cfg?.MachineSetting is { Length: not 0 } machineSetting
					&& _settings?.Values is not null
					&& _settings.Values.TryGetValue(machineSetting, out var pinnedMachine))
				{
					recorded[machineSetting] = pinnedMachine;
				}
				if (recorded.Count is not 0)
					project.SetSettingsJson(Newtonsoft.Json.JsonConvert.SerializeObject(recorded));
			}

			{
				Newtonsoft.Json.Linq.JArray pins = new();
				foreach (var need in _firmwareNeeds)
				{
					if (!need.Satisfied || need.ChosenSha1 is null) continue;
					pins.Add(new Newtonsoft.Json.Linq.JObject { ["id"] = need.Id, ["sha1"] = need.ChosenSha1 });
				}
				if (pins.Count is not 0) project.SetFirmwareJson(pins.ToString(Newtonsoft.Json.Formatting.None));
			}

			// What the core compiled for this game, by name and hash. The files
			// themselves are not in the project - they regenerate from the same
			// game and package - but the list is, so that opening this project
			// later can say whether it is running the same compiled code.
			if (_precompileManifest is { Files.Count: not 0 })
			{
				Newtonsoft.Json.Linq.JArray cache = new();
				foreach (var file in _precompileManifest.Files)
				{
					cache.Add(new Newtonsoft.Json.Linq.JObject { ["name"] = file.Name, ["sha1"] = file.Sha1 });
				}
				project.SetCoreCacheJson(cache.ToString(Newtonsoft.Json.Formatting.None));
			}

			string? declarationJson = null;
			try
			{
				using var pkg = EnginePackage.Open(core.Path);
				declarationJson = pkg?.EntryText("file_slots.json");
			}
			catch (InvalidOperationException)
			{
			}
			if (declarationJson is not null && project.Validate(declarationJson) is { } refusal)
			{
				_status.Text = refusal;
				project.Dispose();
				ShowPage(1);
				return;
			}

			// nothing is written: the project lives in memory until the work is
			// saved, which is also when the user is asked where it goes
			CreatedProject = project;
			DialogResult = DialogResult.OK;
			Close();
		}
	}
}
