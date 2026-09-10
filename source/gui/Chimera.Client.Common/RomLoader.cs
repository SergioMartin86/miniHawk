using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

using Chimera.Common;
using Chimera.Common.IOExtensions;
using Chimera.Common.StringExtensions;
using Chimera.Emulation.Common;

namespace Chimera.Client.Common
{
	public class RomLoader
	{
		private class RomAsset : IRomAsset
		{
			public byte[] RomData { get; set; }
			public byte[] FileData { get; set; }
			public string Extension { get; set; }
			public string RomPath { get; set; }
			public GameInfo Game { get; set; }
		}
		private class LoadParameters
		{
			public CoreComm Comm { get; set; }

			public GameInfo Game { get; set; }

			public List<IRomAsset> Roms { get; set; } = new List<IRomAsset>();
		}
		private readonly Config _config;

		private readonly IDialogParent _dialogParent;

		public RomLoader(Config config, IDialogParent dialogParent)
		{
			_config = config;
			_dialogParent = dialogParent;
		}

		private bool? Question(string text)
			=> _dialogParent.ModalMessageBox3(icon: EMsgBoxIcon.Question, caption: "ROM loader", text: text);

		public enum LoadErrorType
		{
			Unknown,
			Xml,
		}

		// helper methods for the settings events
		private TSetting GetCoreSettings<TCore, TSetting>()
			where TCore : IEmulator
		{
			return (TSetting)GetCoreSettings(typeof(TCore), typeof(TSetting));
		}

		private object GetCoreSettings(Type t, Type settingsType)
		{
			var e = new SettingsLoadArgs(t, settingsType);
			if (OnLoadSettings == null)
				throw new InvalidOperationException("Frontend failed to provide a settings getter");
			OnLoadSettings(this, e);
			if (e.Settings != null && e.Settings.GetType() != settingsType)
				throw new InvalidOperationException($"Frontend did not provide the requested settings type: Expected {settingsType}, got {e.Settings.GetType()}");
			return e.Settings;
		}

		// TODO: reconsider the need for exposing these;
		public IEmulator LoadedEmulator { get; private set; }
		public GameInfo Game { get; private set; }
		public RomGame Rom { get; private set; }
		public string CanonicalFullPath { get; private set; }
		public bool Deterministic { get; set; }

		public class RomErrorArgs : EventArgs
		{
			// TODO: think about naming here, what to pass, a lot of potential good information about what went wrong could go here!
			public RomErrorArgs(string message, string systemId, LoadErrorType type)
			{
				Message = message;
				AttemptedCoreLoad = systemId;
				Type = type;
			}

			public RomErrorArgs(string message, string systemId, string path, bool? det, LoadErrorType type)
				: this(message, systemId, type)
			{
				Deterministic = det;
				RomPath = path;
			}

			public string Message { get; }
			public string AttemptedCoreLoad { get; }
			public string RomPath { get; }
			public bool? Deterministic { get; set; }
			public bool Retry { get; set; }
			public LoadErrorType Type { get; }
		}

		public class SettingsLoadArgs : EventArgs
		{
			public object Settings { get; set; }
			public Type Core { get; }
			public Type SettingsType { get; }
			public SettingsLoadArgs(Type t, Type s)
			{
				Core = t;
				SettingsType = s;
				Settings = null;
			}
		}

		public delegate void SettingsLoadEventHandler(object sender, SettingsLoadArgs e);
		public event SettingsLoadEventHandler OnLoadSettings;

		public delegate void LoadErrorEventHandler(object sender, RomErrorArgs e);
		public event LoadErrorEventHandler OnLoadError;

		public Func<ChimeraFile, int?> ChooseArchive { get; set; }

		public Func<RomGame, string> ChoosePlatform { get; set; }

		// in case we get sent back through the picker more than once, use the same choice the second time
		private int? _previousChoice;
		private int? HandleArchive(ChimeraFile file)
		{
			if (_previousChoice.HasValue)
			{
				return _previousChoice;
			}

			if (ChooseArchive != null)
			{
				_previousChoice = ChooseArchive(file);
				return _previousChoice;
			}

			return null;
		}

		// May want to phase out this method in favor of the overload with more parameters
		private void DoLoadErrorCallback(string message, string systemId, LoadErrorType type = LoadErrorType.Unknown)
		{
			OnLoadError?.Invoke(this, new RomErrorArgs(message, systemId, type));
		}

		private void DoLoadErrorCallback(string message, string systemId, string path, bool det, LoadErrorType type = LoadErrorType.Unknown)
		{
			OnLoadError?.Invoke(this, new RomErrorArgs(message, systemId, path, det, type));
		}

		public IOpenAdvanced OpenAdvanced { get; set; }

		private bool HandleArchiveBinding(ChimeraFile file, bool showDialog = true)
		{
			// try binding normal rom extensions first
			if (!file.IsBound)
			{
				file.BindSoleItemOf(CoreRegistry.Instance.KnownRomExtensions.ToList());
			}
			// if we have an archive and need to bind something, then pop the dialog
			if (file.IsArchive && !file.IsBound)
			{
				var result = showDialog ? HandleArchive(file) : null;
				if (result.HasValue)
				{
					file.BindArchiveMember(result.Value);
				}
				else
				{
					return false;
				}
			}

			CanonicalFullPath = file.CanonicalFullPath;

			return true;
		}

		/// <summary>
		/// The resolved project when the caller is opening a .chimeraProject: every
		/// file already located and hash-checked (or knowingly overridden) by the
		/// resolution dialog. The loader only builds the mounts from it.
		/// </summary>
		public Emulation.Common.Engine.EngineProject Project { get; set; }

		public bool LoadRom(string path, CoreComm nextComm, string forcedCoreName = null, int recursiveCount = 0)
		{
			if (path == null) return false;

			if (recursiveCount > 1) // hack to stop recursive calls from endlessly rerunning if we can't load it
			{
				DoLoadErrorCallback("Failed multiple attempts to load ROM.", "");
				return false;
			}

			if (Project is not null && path.EndsWith(".chimeraProject", StringComparison.OrdinalIgnoreCase))
			{
				try
				{
					LoadProjectGame(path, nextComm, out var projectEmulator, out var projectGame);
					if (projectEmulator is null) return false;
					CanonicalFullPath = Path.GetFullPath(path);
					Rom = null;
					LoadedEmulator = projectEmulator;
					Game = projectGame;
					return true;
				}
				catch (Exception ex)
				{
					DispatchErrorMessage(ex, system: null, path: path);
					return false;
				}
			}

			using ChimeraFile file = new(path, allowArchives: true);
			// make sure path is absolute
			path = CanonicalFullPath = file.CanonicalFullPath;

			if (!file.Exists) return false; // if the provided file doesn't even exist, give up!

			IEmulator nextEmulator;
			RomGame rom = null;
			GameInfo game = null;

			try
			{
				var cancel = false;

				{
					// do the archive binding we had to skip
					if (!HandleArchiveBinding(file))
					{
						return false;
					}

					// What a file's bytes mean is the core's business, never the frontend's:
					// disc images included, the raw file goes to whichever core package
					// declared the extension, and any format parsing happens in there.
					LoadOther(
						nextComm,
						file,
						ext: file.Extension,
						forcedCoreName: forcedCoreName,
						out nextEmulator,
						out rom,
						out game,
						out cancel);
				}

				if (nextEmulator == null)
				{
					if (!cancel)
					{
						DoLoadErrorCallback("No core could load the rom.", null);
					}

					return false;
				}

				if (game is null) throw new Exception("RomLoader returned core but no GameInfo"); // shouldn't be null if `nextEmulator` isn't? just in case
			}
			catch (Exception ex)
			{
				var system = game?.System;

				DispatchErrorMessage(ex, system: system, path: path);
				return false;
			}

			Rom = rom;
			LoadedEmulator = nextEmulator;
			Game = game;
			return true;
		}

		/// <summary>
		/// A project's machine: the pinned core, the project's own sync settings
		/// (queued by the movie the project IS), and the manifest's mounts - the
		/// slot map as "slots", every file under its canonical name, and the
		/// transitional rom/rom.name/rom2..N view of the first slot, exactly the
		/// mounts chimera-run makes (docs/project.md).
		/// </summary>
		private void LoadProjectGame(string path, CoreComm nextComm, out IEmulator nextEmulator, out GameInfo game)
		{
			var p = Project;
			var primary = -1;
			for (var i = 0; i < p.FileCount; i++)
			{
				if (p.FileSlot(i) is not "support") { primary = i; break; }
			}
			// A project MAY hold no game at all. Two of the ares core's machines
			// are worth starting that way - a PlayStation reaches its own BIOS
			// menu, an MSX its own BASIC - and a core that boots without a
			// medium says so by declaring its rom slot with no minimum. The
			// engine mounts the slot either way; what the core sees is a file of
			// zero length, which is how it tells an empty drive from a full one.
			var haveGame = primary >= 0;
			// A project's files are mounted from WHERE THEY LIE. Nothing here reads
			// one: a PS2 disc is over four gigabytes and a byte[] cannot reach two,
			// so loading them was both wasteful and, past that size, impossible.
			string PathOf(int i) => p.FileSourcePath(i) is { Length: > 0 } where
				? where
				: throw new CoreLoadException($"'{p.FileName(i)}' has not been resolved");
			var primaryPath = haveGame ? PathOf(primary) : "";

			var factory = CoreRegistry.Instance.AllFactories.FirstOrDefault(f => f.CoreName == p.CoreName)
				?? throw new CoreLoadException($"the project's core '{p.CoreName}' is not loaded");

			List<CoreFile> extras = new()
			{
				new("slots", System.Text.Encoding.UTF8.GetBytes(p.SlotsJson)),
			};
			for (var i = 0; i < p.FileCount; i++)
			{
				extras.Add(new(p.FileName(i), PathOf(i)));
			}
			if (haveGame)
			{
				extras.Add(new("rom.name", System.Text.Encoding.UTF8.GetBytes(p.FileName(primary))));
				var primarySlot = p.FileSlot(primary);
				var n = 2;
				for (var i = primary + 1; i < p.FileCount; i++)
				{
					if (p.FileSlot(i) != primarySlot) continue;
					extras.Add(new($"rom{n++}", PathOf(i)));
				}
			}

			game = new GameInfo
			{
				// what to call this: the project's own title if it has one, else
				// the game file's name - which beats the project file's, because
				// an unwritten project's stand-in path says nothing about the game
				Name = p.Title.Length is not 0
					? p.Title
					: (haveGame && Path.GetFileNameWithoutExtension(p.FileName(primary)) is { Length: not 0 } fileName
						? fileName
						: Path.GetFileNameWithoutExtension(path)),
				Hash = haveGame ? p.FileActualSha1(primary) : "",
				System = factory.SystemIds[0],
			};

			var ctx = new CoreCreationContext
			{
				Comm = nextComm,
				Game = game,
				Roms = new List<IRomAsset>
				{
					new RomAsset
					{
						// no bytes: the file is mounted from RomPath, which is
						// where the project resolved it to, not the project's own
						// path (a project is not a game file)
						RomData = null,
						FileData = null,
						Extension = haveGame ? Path.GetExtension(p.FileName(primary)) : "",
						RomPath = primaryPath,
						Game = game,
					},
				},
				DeterministicEmulationRequested = Deterministic,
				// everything sync-relevant lives in the project: its recorded
				// settings drive the boot directly, never a config lookup
				Settings = MovieSettings.Decode(p.SettingsJson)
					?? GetCoreSettings(factory.CoreType, factory.SettingsType),
				FirmwareProvider = CoreFirmwareStore.ProviderFor(_config, factory.CoreName),
				ExtraFiles = extras,
			};
			nextEmulator = factory.Create(ctx);
		}

		private IEmulator MakeCoreFromRegistry(LoadParameters lp, string forcedCoreName = null)
		{
			IReadOnlyList<ICoreFactory> factories;
			if (forcedCoreName != null)
			{
				var singleFactory = CoreRegistry.Instance.GetFactories(lp.Game.System).SingleOrDefault(f => f.CoreName == forcedCoreName);
				factories = singleFactory != null ? [ singleFactory ] : [ ];
			}
			else
			{
				_ = _config.DefaultCores.TryGetValue(lp.Game.System, out var preferredCore);
				var dbForcedCoreName = lp.Game.ForcedCore;
				factories = CoreRegistry.Instance.GetFactories(lp.Game.System)
					.OrderBy(f =>
					{
						if (f.CoreName == preferredCore) return -2;
						if (f.CoreName.EqualsIgnoreCase(dbForcedCoreName)) return -1;
						return 0;
					})
					.ToList();
				if (factories.Count == 0)
				{
					throw new CoreLoadException($"No loaded core can run a {lp.Game.System} game. Open one with File > Open Core...");
				}
			}
			var exceptions = new List<Exception>();
			foreach (var factory in factories)
			{
				try
				{
					var ctx = new CoreCreationContext
					{
						Comm = lp.Comm,
						Game = lp.Game,
						Roms = lp.Roms,
						DeterministicEmulationRequested = Deterministic,
						Settings = GetCoreSettings(factory.CoreType, factory.SettingsType),
						FirmwareProvider = CoreFirmwareStore.ProviderFor(_config, factory.CoreName),
					};
					return factory.Create(ctx);
				}
				catch (Exception e) when (!_config.DontTryOtherCores && e is not InternalCoreException)
				{
					exceptions.Add(e);
				}
			}
			throw new AggregateException("No core could load the game", exceptions);
		}

		private void LoadOther(
			CoreComm nextComm,
			ChimeraFile file,
			string ext,
			string forcedCoreName,
			out IEmulator nextEmulator,
			out RomGame rom,
			out GameInfo game,
			out bool cancel)
		{
			cancel = false;
			rom = new RomGame(file);

			if (string.IsNullOrEmpty(rom.GameInfo.System))
			{
				if (CoreRegistry.Instance.TryGetSystemForExtension(rom.Extension, out var declaredSysID))
				{
					// a loaded core package declared this extension
					rom.GameInfo.System = declaredSysID;
				}
				else if (ChoosePlatform != null)
				{
					var result = ChoosePlatform(rom);
					if (!string.IsNullOrEmpty(result))
					{
						rom.GameInfo.System = result;
					}
					else
					{
						cancel = true;
					}
				}
			}

			game = rom.GameInfo;

			nextEmulator = null;
			if (game.System == null)
				return; // The user picked nothing in the Core picker

			var lp = new LoadParameters
			{
				Comm = nextComm,
				Game = game,
				Roms =
				{
					new RomAsset
					{
						RomData = rom.RomData,
						FileData = rom.FileData,
						Extension = rom.Extension,
						RomPath = file.CanonicalFullPath,
						Game = game,
					},
				},
			};
			nextEmulator = MakeCoreFromRegistry(lp, forcedCoreName);
		}

		private void DispatchErrorMessage(Exception ex, string system, string path)
		{
			if (ex is AggregateException agg)
			{
				// all cores failed to load a game, so tell the user everything that went wrong and maybe they can fix it
				if (agg.InnerExceptions.Count > 1)
				{
					DoLoadErrorCallback("Multiple cores failed to load the rom:", system);
				}
				foreach (Exception e in agg.InnerExceptions)
				{
					DispatchErrorMessage(e, system: system, path: path);
				}

				return;
			}

			// all of the specific exceptions we're trying to catch here aren't expected to have inner exceptions,
			// so drill down in case we got a TargetInvocationException or something like that
			while (ex.InnerException != null)
				ex = ex.InnerException;

			if (ex is CoreLoadException)
			{
				// the user can fix this one, and what to do is in the message
				DoLoadErrorCallback(ex.Message, system);
			}
			else if (ex is NoAvailableCoreException)
			{
				// handle exceptions thrown by the new detected systems that BizHawk does not have cores for
				DoLoadErrorCallback($"{ex.Message}\n\n{ex}", system);
			}
			else
			{
				DoLoadErrorCallback($"A core accepted the rom, but threw an exception while loading it:\n\n{ex}", system);
			}
		}
		/// <remarks>rom extensions (with leading dot, UPPERCASE) declared by loaded core packages; this is what's expected at call-site</remarks>
		public static IReadOnlyCollection<string> KnownRomExtensions
			=> CoreRegistry.Instance.KnownRomExtensions.Select(static e => e.ToUpperInvariant()).ToList();

		/// <remarks>built from the extensions declared by loaded core packages</remarks>
		public static FilesystemFilterSet RomFilter
			=> new(
				combinedEntryDesc: "Everything",
				FilesystemFilter.Archives,
				new FilesystemFilter("ROMs", CoreRegistry.Instance.KnownRomExtensions.Select(static e => e.TrimStart('.')).ToList(), addArchiveExts: true));
	}
}
