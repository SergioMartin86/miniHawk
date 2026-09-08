using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

using Chimera.Common;
using Chimera.Emulation.Common.Engine;

using Newtonsoft.Json;

namespace Chimera.Emulation.Common.Waterbox
{
	/// <summary>
	/// The ONE built-in generic waterbox core adapter, and since the engine
	/// migration a THIN one: the machine itself is the engine's ce_session (see
	/// docs/engine-migration.md) - the same machine chimera-run drives headlessly
	/// and the witness gate's Level E verifies. What remains here is the
	/// frontend-facing half: IEmulator and friends, the settings objects, and the
	/// service surfaces the optional tooling groups back - the groups themselves
	/// are probed and driven by the session.
	/// </summary>
	// The attribute names the ADAPTER, which is all a class-level attribute can do
	// when one class serves every package. It is the fallback; the real identity
	// comes from the package, through ICoreIdentity below.
	[PortedCore(
		name: "Waterbox",
		author: "miniBox",
		portedVersion: "1.0.0",
		portedUrl: "https://github.com/SergioMartin86/miniBox")]
	public sealed partial class WaterboxCore : IEmulator, IVideoProvider, ISoundProvider, IStatable, IStateHistory, IInputPollable, IGpuRendered, ICorePrecompile,
		ICoreIdentity, ISettable<WaterboxCoreSettings>, IDriveLights
	{
		/// <summary>
		/// The identity of the PACKAGE this instance is running - what the status bar,
		/// the movie header and the about box should say. Falls back to the adapter's
		/// name only if a package declares no coreName at all.
		/// </summary>
		public CoreAttribute CoreIdentity => IdentityOf(_cfg);

		/// <summary>Builds a core's identity from its package declaration.</summary>
		internal static CoreAttribute IdentityOf(WaterboxConfig cfg)
			=> new PortedCoreAttribute(
				name: string.IsNullOrWhiteSpace(cfg.CoreName) ? "Waterbox" : cfg.CoreName,
				author: cfg.Author ?? "",
				portedVersion: cfg.Version ?? "",
				portedUrl: cfg.Url ?? "");

		private readonly WaterboxConfig _cfg;

		/// <summary>the machine this session is, for a package that can be several</summary>
		private readonly WaterboxConfig.MachineConfig _machine;

		private readonly EngineSession _session;
		private WaterboxCoreSettings _settings;

		private readonly int _width, _height, _samplesPerFrame;
		private int _nsamp; // what the last frame actually produced
		private readonly int[] _videoBuff;
		private readonly short[] _stereoBuff;

		// A core that mixes at a rate other than the 44100 the sound path assumes is
		// resampled here, once per frame, on its way to whoever asks for samples -
		// the sound output and the encoder alike. Presentation only: the guest's
		// bytes, which the gates hash, are untouched.
		private readonly SDLResampler _resampler;
		private short[] _resampled = [ ];
		private int _resampledCount;
		private readonly string[] _buttons;
		private readonly WaterboxConfig.AxisConfig[] _axes;
		// parallel to the two above: which of the declared controls this machine
		// has, answered by the core once the ports are settled
		private readonly bool[] _buttonActive;
		private readonly bool[] _axisActive;
		private byte[] _stateScratch = [ ];

		/// <summary>
		/// What built the waterbox host this session is running on, as JSON - shown by
		/// the frontend and recorded by movies.
		/// </summary>
		public static string HostBuildInfo => EngineSession.HostBuildInfo;

		/// <param name="rom">the game's bytes, or null when <paramref name="romPath"/> is given</param>
		/// <param name="romPath">
		/// Where the game lies, for the usual case of a file on disk: the engine
		/// mounts it and the machine reads it from there, so nothing is loaded.
		/// A disc image is routinely bigger than a byte[] can be.
		/// </param>
		/// <summary>
		/// The root under which every core keeps the code it compiled for a game
		/// (the frontend sets it from its paths); empty means none. A core's own
		/// directory is named by core and package version below it.
		/// </summary>
		public static string CoreCacheRoot { get; set; } = "";

		/// <summary>The precompile session the next core opens as (null: a normal run).</summary>
		public static PrecompileRequest PrecompileRequest { get; set; }

		/// <summary>
		/// One directory per core and package version: a different build of the
		/// package generates different code, so it must not read the old one's.
		/// </summary>
		public static string CoreCacheDirectoryFor(string cacheRoot, string coreName, string coreVersion)
		{
			if (string.IsNullOrEmpty(cacheRoot)) return null;
			static string Safe(string s) => string.Concat((s ?? "").Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));
			return Path.Combine(cacheRoot, Safe(coreName), Safe(string.IsNullOrEmpty(coreVersion) ? "dev" : coreVersion));
		}

		public static string CoreCacheDirectoryFor(WaterboxConfig cfg)
			=> cfg.Precompile ? CoreCacheDirectoryFor(CoreCacheRoot, cfg.CoreName, cfg.Version) : null;

		public WaterboxCore(byte[] rom, string romPath, WaterboxConfig cfg, string packageDir, WaterboxCoreSettings settings = null, IReadOnlyDictionary<string, byte[]> firmware = null, IReadOnlyList<CoreFile> extraFiles = null)
		{
			_cfg = cfg;
			_settings = settings?.Clone() ?? new WaterboxCoreSettings();
			// WHICH MACHINE this session is, settled before anything is built: the
			// controller, the picture and the system id all come from it, and a
			// package that is only ever one machine has none and uses the top level.
			_machine = cfg.MachineFor(EffectiveSettingsFor(cfg, _settings));
			var input = _machine?.Input ?? cfg.Input;
			_width = cfg.Video.Width;
			_height = cfg.Video.Height;
			_samplesPerFrame = cfg.Audio.SamplesPerFrame;
			_videoBuff = new int[_width * _height];
			_stereoBuff = new short[_samplesPerFrame * 2];
			if (cfg.Audio.Rate is > 0 and not 44100)
			{
				_resampler = new SDLResampler(cfg.Audio.Rate, 44100, (buf, n) =>
				{
					if (_resampled.Length < n * 2) _resampled = new short[n * 2];
					Buffer.BlockCopy(buf, 0, _resampled, 0, n * 2 * sizeof(short));
					_resampledCount = n;
				});
			}
			_buttons = input.Buttons.ToArray();
			_axes = input.Axes?.ToArray() ?? [ ];

			ServiceProvider = new BasicServiceProvider(this);

			// The engine builds and runs the machine. A refused rom surfaces as the
			// core's own words (GetLoadError), which the engine already collected.
			try
			{
				var effective = EffectiveSettings();
				_session = EngineSession.Open(
					packageDir, rom, romPath, SerializeSettings(effective), firmware, extraFiles,
					wantGpu: WantsGpu(effective),
					cacheDir: CoreCacheDirectoryFor(cfg),
					precompile: PrecompileRequest);
			}
			catch (InvalidOperationException ex)
			{
				throw new CoreLoadException(ex.Message);
			}

			// WHICH OF THE DECLARED CONTROLS THIS MACHINE HAS. A package declares
			// the union of every peripheral its ports can hold, because the
			// declaration is static and cannot know what a project plugged in; the
			// core read the port settings and built the machine, so it is asked.
			// An inactive control is not in the controller, not a TAStudio column
			// and not a character in a movie entry - but the arrays here stay the
			// DECLARATION's, because an index on the wire must never move.
			_buttonActive = new bool[_buttons.Length];
			for (int i = 0; i < _buttons.Length; i++) _buttonActive[i] = _session.ButtonActive(i);
			_axisActive = new bool[_axes.Length];
			for (int i = 0; i < _axes.Length; i++) _axisActive[i] = _session.AxisActive(i);

			// Memory domains are self-described by the guest at runtime (size/count
			// can depend on settings); pointer-backed straight into guest memory.
			var domains = new List<MemoryDomain>(_session.DomainCount);
			for (int i = 0; i < _session.DomainCount; i++)
			{
				domains.Add(new MemoryDomainIntPtr(
					_session.DomainName(i), MemoryDomain.Endian.Little,
					_session.DomainPtr(i), _session.DomainSize(i), _session.DomainWritable(i), 1));
			}

			// The drive lights, if this machine has any media to light one for.
			// Registered rather than always present: the status bar shows a light
			// per drive a core reports, and a core that reports none gets none.
			if (_session.DriveCount > 0)
			{
				((BasicServiceProvider)ServiceProvider).Register<IDriveLights>(this);
			}

			// The optional tooling ABI (see WaterboxCore.Tooling.cs) - may append bus
			// domains to the list, so it runs before the domains are published.
			InitTooling((BasicServiceProvider)ServiceProvider, domains);
			((BasicServiceProvider)ServiceProvider).Register<IMemoryDomains>(new MemoryDomainList(domains));
		}

		// ---- settings ----

		/// <summary>
		/// Whether these settings ask for a renderer that draws on the machine's
		/// own GPU. The convention is the suffix, not a list: a core names its
		/// hardware renderers "<c>something-hw</c>", and every core that grows one
		/// is understood here without this file changing. A core with no renderer
		/// setting never asks, which is most of them.
		/// </summary>
		internal static bool WantsGpu(IReadOnlyDictionary<string, object> effective)
			=> effective.TryGetValue("renderer", out var r)
				&& r?.ToString() is string name
				&& name.EndsWith("-hw", StringComparison.Ordinal);

		/// <summary>
		/// What the guest is told: every declared setting, at the package's default
		/// unless the user (or the project) overrode it. (The engine overlays this
		/// onto the declared defaults again, harmlessly: the merge is idempotent.)
		/// </summary>
		private Dictionary<string, object> EffectiveSettings()
		{
			var effective = new Dictionary<string, object>();
			foreach (var decl in Decls) effective[decl.Name] = decl.DefaultValue;
			foreach (var kv in _settings.Values ?? new()) effective[kv.Key] = kv.Value;
			return effective;
		}

		/// <summary>
		/// The same merge, callable before a core exists - the firmware decision
		/// tree evaluates against EFFECTIVE settings, and the factory (and the
		/// wizard) need them without booting anything.
		/// </summary>
		public static Dictionary<string, object> EffectiveSettingsFor(
			WaterboxConfig cfg, WaterboxCoreSettings settings)
		{
			// Two passes, because a package of machines has settings whose defaults
			// and legal values depend on WHICH machine - and which machine is itself
			// a setting. So: settle the machine from the package's own defaults and
			// the user's values, then take the rest as that machine has them.
			var effective = Defaults(cfg.Settings, settings);
			if (!cfg.HasMachines) return effective;
			return Defaults(cfg.SettingsFor(cfg.MachineFor(effective)), settings);
		}

		private static Dictionary<string, object> Defaults(
			IReadOnlyList<WaterboxConfig.SettingDecl> decls, WaterboxCoreSettings settings)
		{
			var effective = new Dictionary<string, object>();
			foreach (var decl in decls ?? (IReadOnlyList<WaterboxConfig.SettingDecl>) [ ]) effective[decl.Name] = decl.DefaultValue;
			foreach (var kv in settings?.Values ?? new()) effective[kv.Key] = kv.Value;
			return effective;
		}

		/// <summary>The settings as the machine this session is has them.</summary>
		private IReadOnlyList<WaterboxConfig.SettingDecl> Decls
			=> _cfg.SettingsFor(_machine);


		// Delivered as a flat JSON object, e.g. {"initFillByte":171}. The guest
		// parses it with a small JSON reader (jsmn for C cores, nlohmann for C++).
		private static string SerializeSettings(Dictionary<string, object> settings)
			=> JsonConvert.SerializeObject(settings);

		public IEmulatorServiceProvider ServiceProvider { get; }

		public ControllerDefinition ControllerDefinition => _controllerDefinition ??= MakeControllerDefinition();
		private ControllerDefinition _controllerDefinition;

		private ControllerDefinition MakeControllerDefinition()
		{
			var def = new ControllerDefinition((_machine?.Input ?? _cfg.Input).Name ?? "Waterbox Controller");
			// only the controls this machine HAS: a Four Score's players 3 and 4,
			// or an Arkanoid's paddle, are declared by every NES package and exist
			// only when a project plugged one in
			for (int i = 0; i < _buttons.Length; i++)
			{
				if (_buttonActive[i]) def.BoolButtons.Add(_buttons[i]);
			}
			for (int i = 0; i < _axes.Length; i++)
			{
				if (!_axisActive[i]) continue;
				var axis = _axes[i];
				def.Axes.Add(axis.Name, new AxisSpec(axis.Min.RangeTo(axis.Max), axis.Neutral));
			}
			return def.MakeImmutable();
		}

		public bool FrameAdvance(IController controller, bool render, bool renderSound = true)
		{
			CheckDisposed();
			ulong input = 0;
			if (_buttons.Length > 64)
			{
				// A wide controller (a DOS keyboard): every button rides the
				// engine's set_button channel; the engine delivers only changes
				// to the guest. The packed mask stays zero - one path, exact.
				for (int i = 0; i < _buttons.Length; i++)
				{
					_session.SetButton(i, _buttonActive[i] && controller.IsPressed(_buttons[i]));
				}
			}
			else
			{
				for (int i = 0; i < _buttons.Length; i++)
				{
					// a control the machine does not have is never asked about:
					// it is not in the definition, so the controller has no
					// answer for it
					if (_buttonActive[i] && controller.IsPressed(_buttons[i])) input |= 1ul << i;
				}
			}

			// Analog values don't fit in the button mask, so they go over separately
			// just before the frame they belong to.
			for (int i = 0; i < _axes.Length; i++)
			{
				_session.SetAxis(i, _axisActive[i]
					? controller.AxisValue(_axes[i].Name)
					: _axes[i].Neutral);
			}

			IsLagFrame = _session.FrameAdvance(input, render);
			DrainTrace();
			Frame++;
			if (IsLagFrame) LagCount++;

			if (render) Marshal.Copy(_session.VideoBuffer, _videoBuff, 0, _session.VideoWidth * _session.VideoHeight);
			var audio = _session.AudioBuffer(out _nsamp);
			Marshal.Copy(audio, _stereoBuff, 0, _nsamp * 2);
			if (_resampler is not null)
			{
				_resampledCount = 0;
				if (_nsamp > 0) _resampler.EnqueueSamples(_stereoBuff, _nsamp);
				_resampler.Flush();
			}
			return true;
		}

		// ---- drive lights ----
		// Asked of the session every frame: what is being reported is "was this
		// drive touched during the frame just run", which is not something to
		// cache.
		public int DriveLightCount => _session.DriveCount;

		public string DriveLightName(int index) => _session.DriveName(index);

		public bool DriveLightOn(int index) => _session.DriveLight(index);

		public int Frame { get; private set; }

		public string SystemId => _machine?.Id ?? _cfg.SystemId;

		/// <summary>
		/// A machine a GPU drew is not deterministic whatever its config says, so
		/// the SESSION is asked rather than the config: it knows whether a bridge
		/// was actually offered and taken.
		/// </summary>
		public bool DeterministicEmulation => _cfg.Deterministic && _session.Deterministic;

		/// <summary>
		/// Whether a GPU outside the sandbox drew, and what it calls itself.
		/// Empty when none did, which is every ordinary run.
		/// </summary>
		public string GpuRenderer => _session.GpuDescription;

		/// <summary>
		/// Whether this core's renderer rebuilds after the context it drew on
		/// is gone - declared in waterbox.config, and false unless it says so.
		/// </summary>
		public bool GpuStatesSurviveTheContext => _cfg.Video?.GpuStatesSurviveTheContext is true;

		// ICorePrecompile: only meaningful when the core was opened as a precompile session
		public bool PrecompileDone => _session.PrecompileDone;
		public (uint Done, uint Total) PrecompileProgress => _session.PrecompileProgress;
		public ulong CacheStored => _session.CacheStored;
		public ulong CacheFetched => _session.CacheFetched;

		public void ResetCounters()
		{
			Frame = 0;
			LagCount = 0;
			IsLagFrame = false;
		}

		public void Dispose()
		{
			_resampler?.Dispose();
			_session.Dispose();
		}

		private void CheckDisposed()
		{
			if (_session.Disposed) throw new ObjectDisposedException(nameof(WaterboxCore));
		}

		// ---------------- IVideoProvider ----------------

		// The LIVE size: a mode-changing machine (DOS) reports it per frame,
		// clamped by the engine to the config's buffer; others equal the config.
		public int BufferWidth => _session.Disposed ? _width : _session.VideoWidth;
		public int BufferHeight => _session.Disposed ? _height : _session.VideoHeight;
		// The display aspect. A core that declares none is saying its pixels are
		// square, so the live picture IS its own aspect - which is the only
		// honest answer for a machine whose picture changes size (a Flash movie
		// declares its own stage). Declaring it, as a machine with non-square
		// pixels must, still wins.
		public int VirtualWidth => _machine?.VirtualWidth
			?? (_cfg.Video.VirtualWidth > 0 ? _cfg.Video.VirtualWidth : BufferWidth);

		public int VirtualHeight => _machine?.VirtualHeight
			?? (_cfg.Video.VirtualHeight > 0 ? _cfg.Video.VirtualHeight : BufferHeight);
		public int BackgroundColor => unchecked((int)0xFF000000);
		public int VsyncNumerator => _session.VsyncNumerator;
		public int VsyncDenominator => _session.VsyncDenominator;
		public int[] GetVideoBuffer() => _videoBuff;

		// ---------------- ISoundProvider ----------------

		public bool CanProvideAsync => false;
		public SyncSoundMode SyncMode => SyncSoundMode.Sync;

		public void GetSamplesSync(out short[] samples, out int nsamp)
		{
			if (_resampler is not null)
			{
				samples = _resampled;
				nsamp = _resampledCount;
				return;
			}
			samples = _stereoBuff;
			nsamp = _nsamp;
		}

		public void DiscardSamples() { }

		public void SetSyncMode(SyncSoundMode mode)
		{
			if (mode == SyncSoundMode.Async) throw new NotSupportedException("Async mode is not supported.");
		}

		public void GetSamplesAsync(short[] samples) => throw new InvalidOperationException("Async mode is not supported.");

		// ---------------- IStateHistory ----------------
		//
		// Every one of these is a line: the history is the engine's, and the only
		// thing this layer adds is the side-band a savestate does not carry.
		// Which frame it is, whether that frame lagged, and how many have - the
		// engine has no opinion on any of it, so it rides along as the note it
		// keeps with the frame and hands back on the way in.

		public void HistoryEnable(long budgetBytes) => _session.GreenzoneEnable((ulong)Math.Max(budgetBytes, 0));

		public void HistorySpillTo(string directory) => _session.GreenzoneSpillTo(directory ?? "");

		public long HistoryCount => _session.GreenzoneCount;

		public int HistoryNearest(int frame) => checked((int)_session.GreenzoneNearest(frame));

		public bool HistoryHas(int frame) => _session.GreenzoneNearest(frame) == frame;

		public void HistoryBeforeAdvance() => _session.GreenzoneBeforeAdvance();

		public void HistoryCapture(int frame)
		{
			CheckDisposed();
			var note = new byte[5];
			note[0] = IsLagFrame ? (byte)1 : (byte)0;
			BitConverter.GetBytes(LagCount).CopyTo(note, 1);
			_session.GreenzoneCapture(frame, note);
		}

		public bool HistoryRestore(int frame)
		{
			CheckDisposed();
			if (!_session.GreenzoneRestore(frame)) return false;
			Frame = frame;
			var note = _session.GreenzoneNote(frame);
			// A frame with no note is one stored before this machine had a lag
			// count worth carrying - frame zero's anchor. Leaving the counters
			// alone is right there: nothing has lagged yet.
			if (note is { Length: >= 5 })
			{
				IsLagFrame = note[0] is not 0;
				LagCount = BitConverter.ToInt32(note, 1);
			}
			return true;
		}

		public void HistoryInvalidate(int afterFrame) => _session.GreenzoneInvalidate(afterFrame);

		public bool HistorySave(string path, string machineId) => _session.HistorySave(path, machineId);

		public bool HistoryLoad(string path, string machineId) => _session.HistoryLoad(path, machineId);

		// ---------------- IStatable ----------------

		public bool AvoidRewind => false;

		public void SaveStateBinary(BinaryWriter writer)
		{
			CheckDisposed();
			var state = _session.SaveState(out var len);
			if (_stateScratch.Length < len) _stateScratch = new byte[len];
			Marshal.Copy(state, _stateScratch, 0, len);
			writer.Write(len);
			writer.Write(_stateScratch, 0, len);
			writer.Write(IsLagFrame);
			writer.Write(LagCount);
			writer.Write(Frame);
		}

		public void LoadStateBinary(BinaryReader reader)
		{
			CheckDisposed();
			int len = reader.ReadInt32();
			if (_stateScratch.Length < len) _stateScratch = new byte[len];
			int got = 0;
			while (got < len)
			{
				int n = reader.Read(_stateScratch, got, len - got);
				if (n <= 0) throw new EndOfStreamException("truncated waterbox savestate");
				got += n;
			}
			// (the engine re-asserts the tracing flag itself: a state load overwrites
			// the guest memory it lives in)
			_session.LoadState(_stateScratch, len);

			IsLagFrame = reader.ReadBoolean();
			LagCount = reader.ReadInt32();
			Frame = reader.ReadInt32();
		}

		// ---------------- ISettable ----------------
		// One kind of setting only: all of them shape the machine, are mounted
		// for Init, are recorded in the project, and changing one is a
		// STRUCTURAL change - it needs a reboot.

		public WaterboxCoreSettings GetSettings()
		{
			var s = _settings.Clone();
			s.Declarations = Decls;
			return s;
		}

		public PutSettingsDirtyBits PutSettings(WaterboxCoreSettings o)
		{
			var incoming = o?.Clone() ?? new WaterboxCoreSettings();
			var changed = !_settings.ValuesEqual(incoming);
			_settings = incoming;
			return changed ? PutSettingsDirtyBits.RebootCore : PutSettingsDirtyBits.None;
		}

		// ---------------- IInputPollable ----------------

		public int LagCount { get; set; }
		public bool IsLagFrame { get; set; }

		public IInputCallbackSystem InputCallbacks
		{
			[FeatureNotImplemented]
#pragma warning disable CA1065 // convention for [FeatureNotImplemented] is to throw NIE
			get => throw new NotImplementedException();
#pragma warning restore CA1065
		}
	}
}
