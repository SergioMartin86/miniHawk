using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Newtonsoft.Json;

namespace Chimera.Emulation.Common.Waterbox
{
	/// <summary>
	/// The per-core declaration in a waterbox package's <c>waterbox.config</c>
	/// (JSON). Describes the STATIC machine surface the generic adapter needs -
	/// system id, controller, video, audio, guest-heap layout - plus a placeholder
	/// for default user-tunable settings. Memory domains are NOT here: their size
	/// and count can depend on runtime settings, so the guest self-describes them
	/// at runtime (guest ABI GetMemoryDomain*).
	///
	/// Deserialized with Newtonsoft (property matching is case-insensitive, so the
	/// JSON uses camelCase and these are PascalCase).
	/// </summary>
	public sealed class WaterboxConfig
	{
		public string CoreName { get; set; }

		/// <summary>
		/// The machine this package is, when it is only one. A package that declares
		/// <see cref="Machines"/> leaves this empty and names the machine there.
		/// </summary>
		public string SystemId { get; set; }

		/// <summary>Who wrote the core. Shown wherever the frontend introduces it.</summary>
		public string Author { get; set; }

		/// <summary>The core's own version string (not the package format's).</summary>
		public string Version { get; set; }

		/// <summary>
		/// The guest ABI this package was built against (see <see cref="GuestAbi"/>).
		/// Absent means <see cref="GuestAbi.Assumed"/>: a package published before the
		/// field existed, which by definition is the first ABI.
		/// </summary>
		public int Abi { get; set; } = GuestAbi.Assumed;

		/// <summary>Where the core lives, for the about box.</summary>
		public string Url { get; set; }

		/// <summary>The mounted file name the guest reads the rom from (default "rom").</summary>
		public string RomFile { get; set; } = "rom";

		public bool Deterministic { get; set; } = true;

		/// <summary>
		/// The core can fill its compile cache without running (it exports
		/// SetPrecompile): the frontend precompiles a rom before its first boot.
		/// </summary>
		public bool Precompile { get; set; }

		/// <summary>Guest heap sizes in MiB, in order: sbrk, sealed, invis, plain, mmap.</summary>
		public uint[] MemoryLayoutMiB { get; set; }

		public VideoConfig Video { get; set; }

		public AudioConfig Audio { get; set; }

		public InputConfig Input { get; set; }

		/// <summary>
		/// The machines one core.wbx can be. Genesis Plus GX is a Mega Drive, a Master
		/// System, a Game Gear and an SG-1000 - the same binary, four machines - and a
		/// package that shipped one machine each would be four copies of the same core
		/// whose only difference is a controller and a name.
		///
		/// What they share stays at the top level: the binary, the guest heap, the
		/// audio rate, the video BUFFER. What makes them different machines is here:
		/// the system they are, the controller they have, the picture they draw, and
		/// the file extensions that belong to them.
		///
		/// Which one a session is comes from a setting - <see cref="MachineSetting"/> -
		/// so the machine is pinned in the project and recorded in the movie like every
		/// other structural choice, rather than being a property of which zip you
		/// happened to install.
		/// </summary>
		public List<MachineConfig> Machines { get; set; }

		/// <summary>
		/// The setting whose value picks the machine. Required when <see cref="Machines"/>
		/// is declared, and it must be one of the package's own settings.
		/// </summary>
		public string MachineSetting { get; set; }

		/// <summary>True when this package describes more than one machine.</summary>
		public bool HasMachines => Machines is { Count: > 0 };

		/// <summary>Every system this package can be, in declaration order.</summary>
		public IReadOnlyList<string> SystemIds
			=> HasMachines
				? Machines.Select(static m => m.Id).Where(static id => !string.IsNullOrEmpty(id)).ToList()
				: string.IsNullOrEmpty(SystemId) ? [ ] : new[] { SystemId };

		/// <summary>
		/// Rom extension -&gt; system, over the whole package (every machine's).
		/// When two machines claim the same extension - dolphin's GameCube and
		/// Wii both boot .iso - the FIRST declaration wins, so a directly-opened
		/// file routes to the machine declared first (the package's default). An
		/// image of the other machine is then refused with a message naming the
		/// wizard, where the choice is explicit.
		/// </summary>
		public Dictionary<string, string> AllExtensions
		{
			get
			{
				Dictionary<string, string> all = new();
				foreach (var (ext, sysID) in Extensions ?? new()) if (!all.ContainsKey(ext)) all[ext] = sysID;
				foreach (var machine in Machines ?? new())
				{
					foreach (var (ext, sysID) in machine.Extensions ?? new()) if (!all.ContainsKey(ext)) all[ext] = sysID;
				}
				return all;
			}
		}

		/// <summary>
		/// The machine a session with these settings is. Null for a single-machine
		/// package. A value naming no machine falls back to the first one declared,
		/// so a package can never end up with no machine at all.
		/// </summary>
		public MachineConfig MachineFor(IReadOnlyDictionary<string, object> effectiveSettings)
		{
			if (!HasMachines) return null;
			if (!string.IsNullOrEmpty(MachineSetting)
				&& effectiveSettings is not null
				&& effectiveSettings.TryGetValue(MachineSetting, out var value))
			{
				var chosen = value?.ToString() ?? "";
				var match = Machines.Find(m => m.Selects(chosen));
				if (match is not null) return match;
			}
			return Machines[0];
		}

		/// <summary>What a machine changes about one of the package's settings.</summary>
		public sealed class SettingOverride
		{
			public List<string> Options { get; set; }

			public object Default { get; set; }
		}

		/// <summary>
		/// The package's settings as this machine has them: same names, same
		/// meanings, with whatever the machine narrows applied.
		/// </summary>
		public IReadOnlyList<SettingDecl> SettingsFor(MachineConfig machine)
		{
			var decls = Settings ?? new List<SettingDecl>();
			var scopes = machine?.When is { Count: > 0 } && decls.Exists(static d => d.When is { Count: > 0 });
			var overrides = machine?.SettingOverrides is { Count: > 0 };
			// The SAME list every time, for the same machine: callers compare
			// declaration lists by reference to tell whether the exposed set
			// changed, and a fresh list every call redraws forever. A package
			// with nothing to scope and nothing to narrow answers with its own.
			if (!scopes && !overrides) return decls;
			var key = (machine.Id ?? "") + "\u0000" + string.Join(",", machine.When ?? new List<string>());
			if (_narrowed.TryGetValue(key, out var cached)) return cached;

			var scoped = scopes
				? decls.FindAll(d => d.When is not { Count: > 0 } || machine.When.Exists(d.AppliesTo))
				: decls;
			if (!overrides) { _narrowed[key] = scoped; return scoped; }

			List<SettingDecl> narrowed = new(scoped.Count);
			foreach (var decl in scoped)
			{
				if (!machine.SettingOverrides.TryGetValue(decl.Name ?? "", out var over) || over is null)
				{
					narrowed.Add(decl);
					continue;
				}
				narrowed.Add(new SettingDecl
				{
					Name = decl.Name,
					Display = decl.Display,
					Description = decl.Description,
					Type = decl.Type,
					Options = over.Options ?? decl.Options,
					Default = over.Default ?? decl.Default,
					Min = decl.Min,
					Max = decl.Max,
					When = decl.When,
				});
			}
			_narrowed[key] = narrowed;
			return narrowed;
		}

		private readonly Dictionary<string, IReadOnlyList<SettingDecl>> _narrowed = new();

		/// <summary>
		/// The same answer as <see cref="SettingsFor"/>, laid out against the
		/// package's OWN settings list rather than against itself: entry i is
		/// the machine's version of <c>Settings[i]</c>, or null where the
		/// machine does not have that setting at all.
		///
		/// The engine answers "which settings are exposed" with indices into
		/// the declaration it was handed, which is the package's list. A
		/// narrowed list is shorter and renumbered, so reading it at those
		/// indices lands on the wrong setting - and every one of a machine's
		/// own settings was quietly dropped that way, which is how a Neo Geo
		/// came to show none of its DIP switches.
		/// </summary>
		public IReadOnlyList<SettingDecl> SettingsByDeclarationIndexFor(MachineConfig machine)
		{
			var decls = Settings ?? new List<SettingDecl>();
			var key = (machine?.Id ?? "") + "\u0000"
				+ string.Join(",", machine?.When ?? new List<string>());
			if (_byDeclIndex.TryGetValue(key, out var cached)) return cached;

			// SettingsFor keeps declaration order and only drops or rewrites
			// entries, so walking the two together pairs each survivor with the
			// slot it came from.
			var narrowed = SettingsFor(machine);
			var mapped = new SettingDecl[decls.Count];
			var at = 0;
			for (var i = 0; i < decls.Count; i++)
			{
				if (at < narrowed.Count && string.Equals(narrowed[at].Name, decls[i].Name,
					StringComparison.Ordinal))
				{
					mapped[i] = narrowed[at];
					at++;
				}
			}
			_byDeclIndex[key] = mapped;
			return mapped;
		}

		private readonly Dictionary<string, IReadOnlyList<SettingDecl>> _byDeclIndex = new();

		/// <summary>The machine that IS this system, or null.</summary>
		public MachineConfig MachineForSystem(string systemId)
			=> HasMachines && !string.IsNullOrEmpty(systemId)
				? Machines.Find(m => string.Equals(m.Id, systemId, StringComparison.OrdinalIgnoreCase))
				: null;

		/// <summary>
		/// One machine a package can be. Everything here overrides the top level for a
		/// session that is this machine; everything absent is shared.
		/// </summary>
		public sealed class MachineConfig
		{
			/// <summary>The system this machine is (the movie's platform).</summary>
			public string Id { get; set; }

			/// <summary>What to call it in front of a user. Falls back to the id.</summary>
			public string Label { get; set; }

			/// <summary>Values of the package's machine setting that mean this machine.</summary>
			public List<string> When { get; set; }

			/// <summary>The controller this machine has.</summary>
			public InputConfig Input { get; set; }

			/// <summary>The picture it draws, when it differs from the package default.</summary>
			public int? VirtualWidth { get; set; }

			public int? VirtualHeight { get; set; }

			/// <summary>Rom extensions that belong to this machine.</summary>
			public Dictionary<string, string> Extensions { get; set; }

			/// <summary>
			/// Settings this machine narrows: a Mega Drive port takes a mouse, an
			/// Activator and a Team Player, and a Master System port takes a pad or
			/// nothing. Same setting, same name in the guest, fewer legal values -
			/// so the machine says which, rather than the user being offered a
			/// controller the machine cannot have.
			/// </summary>
			public Dictionary<string, SettingOverride> SettingOverrides { get; set; }

			public string DisplayName => string.IsNullOrWhiteSpace(Label) ? Id : Label;

			/// <summary>True when a machine-setting value names this machine.</summary>
			public bool Selects(string settingValue)
				=> When is { Count: > 0 }
					? When.Exists(v => string.Equals(v, settingValue, StringComparison.OrdinalIgnoreCase))
					: string.Equals(Id, settingValue, StringComparison.OrdinalIgnoreCase);
		}

		public LagConfig Lag { get; set; }

		/// <summary>Rom file extension (with leading dot, lowercase) to system ID map - how files route to this core.</summary>
		public Dictionary<string, string> Extensions { get; set; }

		/// <summary>
		/// Files the core needs that it may not ship - a disk-system BIOS, say. Each is
		/// mounted for the guest under its declared id, alongside the rom and the
		/// settings; the guest opens it by that name during Init.
		/// </summary>
		public List<CoreFirmwareDecl> Firmware { get; set; }

		/// <summary>
		/// The user-tunable settings this core offers, declared by the package. The
		/// frontend renders them from this and nothing else - it has no per-core
		/// settings dialogs to render them with.
		/// </summary>
		public List<SettingDecl> Settings { get; set; }

		/// <summary>
		/// One user-tunable setting. Enough for the frontend to draw a labelled,
		/// typed, documented row without knowing what the setting means.
		/// </summary>
		public sealed class SettingDecl
		{
			/// <summary>Key the guest reads it under, in the mounted settings JSON.</summary>
			public string Name { get; set; }

			/// <summary>Label for the settings grid. Falls back to <see cref="Name"/>.</summary>
			public string Display { get; set; }

			/// <summary>The sentence shown when the row is selected.</summary>
			public string Description { get; set; }

			/// <summary>"bool", "int" or "enum" (the default is inferred from the other fields).</summary>
			public string Type { get; set; }

			public object Default { get; set; }

			/// <summary>Allowed values, for an enum setting.</summary>
			public List<string> Options { get; set; }

			public int? Min { get; set; }

			public int? Max { get; set; }

			/// <summary>
			/// Values of the package's machine setting this belongs to, or empty for
			/// one that belongs to all of them.
			///
			/// A multi-machine core's settings are mostly not shared: a Game Boy's
			/// DMG revision means nothing on a Mega Drive, and a Neo Geo's coin
			/// slots mean nothing anywhere else. Offering every machine's settings
			/// at once would be a list nobody could read and a set of choices most
			/// of which do nothing.
			/// </summary>
			public List<string> When { get; set; }

			/// <summary>Whether this setting belongs to a machine selected by <paramref name="settingValue"/>.</summary>
			public bool AppliesTo(string settingValue)
				=> When is not { Count: > 0 }
					|| (settingValue is not null
						&& When.Exists(v => string.Equals(v, settingValue, StringComparison.OrdinalIgnoreCase)));


			public string DisplayName => string.IsNullOrWhiteSpace(Display) ? Name : Display;

			/// <summary>
			/// The .NET type the settings grid should edit this as. Enums arrive as
			/// strings (the guest reads them by name), so an enum is edited as a string
			/// from a fixed list rather than as a synthesized Enum type.
			/// </summary>
			public Type ClrType => EffectiveType switch
			{
				"bool" => typeof(bool),
				"int" => typeof(int),
				"float" => typeof(double),
				_ => typeof(string),
			};

			/// <summary>The declared type, or one inferred from the options/default when omitted.</summary>
			public string EffectiveType
			{
				get
				{
					if (!string.IsNullOrWhiteSpace(Type)) return Type.ToLowerInvariant();
					if (Options is { Count: > 0 }) return "enum";
					return Default switch
					{
						bool => "bool",
						sbyte or byte or short or ushort or int or uint or long or ulong => "int",
						float or double or decimal => "float",
						_ => "string",
					};
				}
			}

			/// <summary>The default, coerced to <see cref="ClrType"/>.</summary>
			public object DefaultValue => Coerce(Default);

			/// <summary>
			/// Brings a value (from JSON, so possibly a boxed long or a string) to the
			/// type this setting is edited as, clamping ints to any declared range.
			/// </summary>
			public object Coerce(object value)
			{
				switch (EffectiveType)
				{
					case "bool":
						return value switch { null => false, bool b => b, _ => bool.TryParse(value.ToString(), out var pb) && pb };
					case "int":
					{
						var n = value switch
						{
							null => 0,
							int i => i,
							_ => int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pi) ? pi : 0,
						};
						if (Min is int min && n < min) n = min;
						if (Max is int max && n > max) n = max;
						return n;
					}
					case "float":
					{
						return value switch
						{
							null => 0.0,
							double d => d,
							float f => (double)f,
							_ => double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var pd) ? pd : 0.0,
						};
					}
					default:
					{
						var s = value?.ToString() ?? "";
						// an unknown option would leave the grid showing a value the core
						// will not accept, so fall back to the first legal one
						if (Options is { Count: > 0 } && !Options.Contains(s)) return Options.Contains(Default?.ToString()) ? Default.ToString() : Options[0];
						return s;
					}
				}
			}
		}

		public sealed class VideoConfig
		{
			public int Width { get; set; }
			public int Height { get; set; }
			public int VirtualWidth { get; set; }
			public int VirtualHeight { get; set; }
			/// <summary>
			/// Frame rate as a fraction. Only a fallback: a core that knows its own rate - because
			/// the region is a user setting, say - exports <c>GetVsyncNumerator</c> and
			/// <c>GetVsyncDenominator</c>, which the adapter asks after Init and prefers.
			/// </summary>
			public int VsyncNumerator { get; set; } = 60;

			public int VsyncDenominator { get; set; } = 1;

			/// <summary>Guest export returning a Width*Height BGRA (0xFFrrggbb) frame.</summary>
			public string GetBgra { get; set; }

			/// <summary>
			/// Whether this core's savestates survive a change of GL context -
			/// which is what opening a project in a later session is.
			///
			/// A bridged renderer holds its GL objects by the names a driver
			/// handed out, and those names live in guest memory, so a state
			/// carries them into a session where they name nothing. A core
			/// declares this only when its renderer NOTICES that (the bridge's
			/// context id, kept beside the objects) and builds them again. The
			/// frontend keeps a project's cached states for a core that says
			/// yes, and drops them for one that does not.
			/// </summary>
			public bool GpuStatesSurviveTheContext { get; set; }

			/// <summary>
			/// How many frames this core's renderer needs to have DRAWN before
			/// its picture is the one a straight playback would show. Zero for
			/// almost every core, which composes each frame from the machine
			/// and nothing else.
			///
			/// A seek replays with drawing off - nobody is looking at the
			/// frames on the way - and a renderer whose display stage carries
			/// state from one frame to the next then composes the destination
			/// from state that never saw them. PCSX2 does: skipping its display
			/// stage freezes a scanmask countdown and the deinterlace phase, and
			/// the frame a seek lands on comes out visibly different from the
			/// same frame played through. Measured on Marvel vs Capcom 2: 7.3%
			/// of the picture wrong with one frame drawn, 3.9% with two, and
			/// EXACT with five.
			///
			/// So the frontend starts drawing this many frames before a seek's
			/// destination. It is bounded and small - it is a warm-up, not a
			/// dependency on history - and a core that needs none pays nothing.
			/// </summary>
			public int RenderWarmupFrames { get; set; }
		}

		public sealed class AudioConfig
		{
			/// <summary>
			/// Samples per frame, per channel. A core that produces a varying number (any blip-style
			/// resampler does) exports <c>GetAudioSampleCount</c> and this becomes the capacity: the
			/// adapter takes what the core reports, clamped to this.
			/// </summary>
			public int SamplesPerFrame { get; set; }

			public int Channels { get; set; } = 1;

			/// <summary>
			/// The rate the core mixes at, in Hz. The frontend's sound path is built
			/// around 44100 and the adapter resamples anything else to it; a package
			/// that says nothing is taken to produce 44100, which is what the
			/// lineage's cores did. A 48 kHz chip (a PS2's SPU2, a GameCube's DSP,
			/// an Xbox's APU) that did not say so was played as if it were 44.1 kHz:
			/// a semitone and a half low, and its audio outrunning its video in
			/// every encode (issue #37).
			/// </summary>
			public int Rate { get; set; } = 44100;

			/// <summary>Guest export returning SamplesPerFrame*Channels interleaved int16.</summary>
			public string Get { get; set; }
		}

		public sealed class InputConfig
		{
			public string Name { get; set; }

			/// <summary>Bool button names, in bit order (button i -&gt; bit i of the 64-bit FrameAdvance mask).</summary>
			public List<string> Buttons { get; set; }

			/// <summary>
			/// Analog controls (paddles, sticks, triggers), in index order. They cannot
			/// travel in the button mask, so the adapter pushes each one to the guest
			/// with the optional <c>SetAxis(index, value)</c> export before every frame.
			/// A core that declares axes must export it.
			/// </summary>
			public List<AxisConfig> Axes { get; set; }
		}

		public sealed class AxisConfig
		{
			public string Name { get; set; }
			public int Min { get; set; }
			public int Max { get; set; }

			/// <summary>Value when the control is untouched (centre for a stick, rest position for a paddle).</summary>
			public int Neutral { get; set; }
		}

		public sealed class LagConfig
		{
			/// <summary>Guest export returning nonzero if input was polled this frame (lag = !this). Optional.</summary>
			public string InputWasRead { get; set; }
		}

		/// <summary>
		/// The "firmware" array exactly as the package wrote it, for the engine's
		/// decision tree (ce_firmware_evaluate) - conditions and all, no round
		/// trip through the typed declarations.
		/// </summary>
		[JsonIgnore]
		public string RawFirmwareJson { get; private set; } = "[]";

		/// <summary>
		/// The "settings" array exactly as the package wrote it, for the engine's
		/// exposure gate (ce_settings_evaluate) - conditions and all.
		/// </summary>
		[JsonIgnore]
		public string RawSettingsJson { get; private set; } = "[]";

		public static WaterboxConfig FromJson(string json)
		{
			var cfg = JsonConvert.DeserializeObject<WaterboxConfig>(json);
			if (cfg is not null)
			{
				try
				{
					var root = Newtonsoft.Json.Linq.JObject.Parse(json);
					if (root["firmware"] is Newtonsoft.Json.Linq.JArray fw) cfg.RawFirmwareJson = fw.ToString(Formatting.None);
					if (root["settings"] is Newtonsoft.Json.Linq.JArray st) cfg.RawSettingsJson = st.ToString(Formatting.None);
				}
				catch (JsonException)
				{
					// the typed parse above already decided the config is usable
				}
			}
			return cfg;
		}
	}
}
