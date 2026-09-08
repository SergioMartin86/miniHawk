using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

using Newtonsoft.Json;

using Chimera.Display;
using Chimera.Common;
using Chimera.Common.IOExtensions;
using Chimera.Emulation.Common;
using Chimera.Emulation.Common.Engine;

namespace Chimera.Client.Common
{
	internal partial class TasMovie
	{
		// The JSON .chimeraProject (docs/project.md) is this movie's NATIVE
		// format: the project IS the movie. Everything that is work - input
		// log, markers, branches (with their text, times and markers),
		// subtitles, comments-as-description, the header metadata - lives in
		// the project file; everything regenerable (greenzone, lag log,
		// branch states and screenshots, session position, column layout)
		// lives in a cache sibling that may be lost at the price of
		// recomputation only. There is no other project format: legacy zip
		// tasprojs (and imported movies of any provenance) are not read.

		private EngineProject _project;

		/// <summary>
		/// The engine-side project backing this movie. Holds what the movie
		/// machinery does not model (the file manifest, firmware pins, the
		/// wizard's title and description) so a load-edit-save round trip
		/// preserves them untouched.
		/// </summary>
		public EngineProject Project => _project ??= EngineProject.New();

		/// <summary>
		/// The greenzone sibling: everything regenerable, beside the project.
		/// Present = loaded; absent = a clean slate (docs/project.md).
		/// </summary>
		public string GreenZoneFilename => Path.ChangeExtension(Filename, "chimeraGreenZone");

		public string DroppedCacheNote { get; private set; }

		/// <summary>
		/// What the cached states are states OF: the core build, the settings, the
		/// firmware pins and the game files, hashed into one line. A savestate is
		/// the memory of one exact machine, and the sandbox checks only that the
		/// core is the same binary - not what it was configured with or given to
		/// read - so a cache left beside a project whose machine has since changed
		/// (a hand-edited setting, a project saved over another's name, the
		/// settings a bug once wrote as "{}") loaded states into a machine they
		/// were not made by, and that machine fell over (issue #26). The cache
		/// carries this line, and is used only when it still says the same.
		/// </summary>
		internal static string MachineIdentityOf(EngineProject p)
		{
			StringBuilder sb = new();
			sb.Append("core=").Append(p.CoreSha1.ToUpperInvariant()).Append('\n');
			sb.Append("settings=").Append(CanonicalJson(p.SettingsJson)).Append('\n');
			sb.Append("firmware=").Append(CanonicalJson(p.FirmwareJson)).Append('\n');
			for (var i = 0; i < p.FileCount; i++)
			{
				// the hash is the file's identity; its name on disk is not
				sb.Append("file=").Append(p.FileSlot(i)).Append(':').Append(p.FileSha1(i).ToUpperInvariant()).Append('\n');
			}
			return ChimeraEngine.Sha1Hex(Encoding.UTF8.GetBytes(sb.ToString()));
		}

		/// <summary>Object keys sorted, no whitespace: the same settings in any order are the same machine.</summary>
		private static string CanonicalJson(string json)
		{
			if (string.IsNullOrWhiteSpace(json)) return "";
			try
			{
				return Canonical(Newtonsoft.Json.Linq.JToken.Parse(json)).ToString(Formatting.None);
			}
			catch (JsonException)
			{
				return json.Trim();
			}
		}

		private static Newtonsoft.Json.Linq.JToken Canonical(Newtonsoft.Json.Linq.JToken token)
		{
			switch (token)
			{
				case Newtonsoft.Json.Linq.JObject obj:
					Newtonsoft.Json.Linq.JObject sorted = new();
					foreach (var prop in obj.Properties().OrderBy(static prop => prop.Name, StringComparer.Ordinal))
					{
						sorted[prop.Name] = Canonical(prop.Value);
					}
					return sorted;
				case Newtonsoft.Json.Linq.JArray arr:
					Newtonsoft.Json.Linq.JArray items = new();
					foreach (var item in arr) items.Add(Canonical(item));
					return items;
				default:
					return token;
			}
		}

		/// <summary>
		/// Adopts the frontend's RESOLVED instance (files located and hashed for
		/// this session) in place of the one this movie opened itself, so a save
		/// records the ACTUAL hashes - a knowing override included. The frontend
		/// keeps the same instance for mounts; nothing disposes it until the next
		/// project replaces it.
		/// </summary>
		public void UseResolvedProject(EngineProject project)
		{
			if (ReferenceEquals(_project, project)) return;
			_project?.Dispose();
			_project = project;
		}

		// header keys the project format first-classes; the rest ride the
		// project's ordered headers map verbatim
		private static bool IsFirstClassHeaderKey(string key)
			=> key is HeaderKeys.GameName or HeaderKeys.Rerecords
				or HeaderKeys.Core or HeaderKeys.CoreVersion or HeaderKeys.CorePackageSha1;

		/// <summary>the header's value when it has one, and what the project already says when it does not</summary>
		private static string Keep(string headerValue, string pinned)
			=> string.IsNullOrWhiteSpace(headerValue) ? pinned : headerValue;

		protected override FileWriteResult Write(string fn, bool isBackup = false)
		{
			if (StartsFromSavestate)
			{
				// an anchored movie's savestate is sync data with no project home
				// yet; refusing beats silently dropping it (docs/project.md)
				return new FileWriteResult(FileWriteEnum.FailedDuringWrite, new(fn, ""),
					new InvalidOperationException("a savestate-anchored movie cannot be saved as a project yet"));
			}

			SetCycleValues();
			if (!Header.ContainsKey(HeaderKeys.OriginalEmulatorVersion))
			{
				Header[HeaderKeys.OriginalEmulatorVersion] = Header[HeaderKeys.EmulatorVersion];
			}
			Header[HeaderKeys.EmulatorVersion] = VersionInfo.GetEmuVersion();

			var p = Project;

			// identity: the project's title IS the game name the movie shows
			if (!string.IsNullOrEmpty(Header[HeaderKeys.GameName]))
			{
				p.Title = Header[HeaderKeys.GameName];
			}
			// the pin is the project's, and a movie header that does not carry it is
			// silent rather than empty: writing "" here would unpin the core and leave
			// a project nothing can run
			p.SetCore(
				Keep(Header[HeaderKeys.Core], p.CoreName),
				Keep(Header[HeaderKeys.CoreVersion], p.CoreVersion),
				Keep(Header[HeaderKeys.CorePackageSha1], p.CoreSha1));
			p.Rerecords = Rerecords;
			// The settings are the movie's when it has them, and the project's
			// own when it is silent - the same rule as the core pin above. A
			// movie that starts from a wizard-made project has no settings text
			// of its own (the project boot fills headers, not settings), and
			// flattening that silence wrote "{}" over the wizard's answers: the
			// reopened machine ran on every default, and desynced (issue #29).
			if (!string.IsNullOrWhiteSpace(SettingsJson)) p.SetSettingsJson(FlattenSettings(SettingsJson));
			p.Description = string.Join("\n", Comments);

			p.SubtitlesClear();
			Subtitles.Sort();
			foreach (var subtitle in Subtitles)
			{
				p.SubtitleAdd(subtitle.ToString());
			}

			// Two facts about the run that only the running machine knows, written
			// at save rather than at record so that a project made before either
			// existed gains them the first time it is saved.
			//
			// Where the input stops, so a reader does not have to know what a
			// neutral entry looks like for this core's controller...
			Header[HeaderKeys.LastInputFrame] = LastNonEmptyInputFrame.ToString(CultureInfo.InvariantCulture);
			// ...and the rate the machine runs at. Chimera keeps no per-system rate
			// table - the exact rate is the core's - so without this a movie is a
			// frame count nothing outside that core can turn into a duration.
			// ...and whether a GPU drew. A run made on one carries no promise that
			// it replays anywhere - the GPU is outside the sandbox, outside the
			// savestate and different on every machine - so it is written down,
			// and a replay that desyncs elsewhere can be understood.
			if (Emulator is IGpuRendered gpu && !string.IsNullOrEmpty(gpu.GpuRenderer))
			{
				Header[HeaderKeys.GpuRenderer] = gpu.GpuRenderer;
				// ...and whether the states beside this project can be opened
				// again in another session, which is a question about the core
				// and has to be answered while there is a core to ask
				if (gpu.GpuStatesSurviveTheContext) Header[HeaderKeys.GpuStatesSurvive] = "1";
			}

			if (IsAttached())
			{
				Header[HeaderKeys.VsyncNumerator] =
					Emulator.VsyncNumerator().ToString(CultureInfo.InvariantCulture);
				Header[HeaderKeys.VsyncDenominator] =
					Emulator.VsyncDenominator().ToString(CultureInfo.InvariantCulture);
			}

			p.HeadersClear();
			foreach (var (key, value) in Header)
			{
				if (!IsFirstClassHeaderKey(key)) p.HeaderSet(key, value);
			}

			// the input lump, exactly the [Input] block a movie file carries
			var engineLog = ((EngineStringLog)Log).Engine;
			engineLog.Key = string.IsNullOrEmpty(LogKey)
				? LogEntryGenerator.GenerateLogKey(Session.MovieController.Definition)
				: LogKey;
			p.LogText = engineLog.Serialize(crlf: false);

			p.MarkersClear();
			foreach (var marker in Markers)
			{
				// the run's own three are derived from the movie and worked out
				// again on load: writing them would let them go stale, and reading
				// them back would leave three of somebody else's markers behind
				// every time the project was saved
				if (marker.IsPermanent) continue;
				p.MarkerAdd(marker.Frame, marker.Message ?? "", marker.WantsState);
			}

			p.BranchesClear();
			foreach (var branch in Branches)
			{
				p.BranchAdd(
					branch.UserText ?? "",
					branch.Frame,
					branch.TimeStamp.ToString("o", CultureInfo.InvariantCulture),
					JoinLogLines(branch.InputLog));
				var index = p.BranchCount - 1;
				if (branch.Markers is null) continue;
				foreach (var marker in branch.Markers)
				{
					p.BranchMarkerAdd(index, marker.Frame, marker.Message ?? "", marker.WantsState);
				}
			}

			EngineProgress.Report("writing the project");
			try
			{
				// The folder first: a backup goes to the "Movie backups" path, which
				// a fresh install does not have, and the engine writes files, not
				// folders - so Save Backup failed until somebody made Movies/backup
				// by hand (issue #19). A Save As lands in a folder the dialog showed,
				// but the same line costs nothing there.
				var folder = Path.GetDirectoryName(Path.GetFullPath(fn));
				if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
				p.Save(fn);
			}
			catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
			{
				return new FileWriteResult(FileWriteEnum.FailedDuringWrite, new(fn, ""), ex);
			}

			if (!isBackup)
			{
				EngineProgress.Report("writing the greenzone");
				WriteCacheFile(Path.ChangeExtension(fn, "chimeraGreenZone"));
				// and where this machine keeps the project's files, in a sibling of
				// its own: the project itself stays distributable, carrying names and
				// hashes and no paths at all (docs/project.md). Merged over whatever
				// is already there, so firmware locations recorded at load survive.
				ProjectLocalPaths.Read(p, fn).Save(p);
				// and who this cache belongs to, so the cache manager can name it
				// and say where the project it serves was last seen
				ProjectCache.Remember(p.Id, ProjectCache.FactsOf(p, fn));
				Changes = false;
			}
			return new FileWriteResult();
		}

		/// <summary>
		/// The project stores the flat name-to-value map the engine's settings
		/// channel takes (and chimera-run passes straight through); the movie
		/// machinery stores the settings OBJECT ({"Values":{...}} for a waterbox
		/// core). These two translate at the boundary.
		/// </summary>
		internal static string FlattenSettings(string syncSettingsJson)
		{
			if (string.IsNullOrWhiteSpace(syncSettingsJson)) return "{}";
			try
			{
				var root = Newtonsoft.Json.Linq.JObject.Parse(syncSettingsJson);
				if (root.Count is 1 && root["Values"] is Newtonsoft.Json.Linq.JObject values)
				{
					return values.ToString(Formatting.None);
				}
				return root.ToString(Formatting.None);
			}
			catch (JsonException)
			{
				return "{}";
			}
		}

		internal static string WrapSettings(string flatJson)
		{
			try
			{
				var values = Newtonsoft.Json.Linq.JObject.Parse(
					string.IsNullOrWhiteSpace(flatJson) ? "{}" : flatJson);
				return new Newtonsoft.Json.Linq.JObject { ["Values"] = values }.ToString(Formatting.None);
			}
			catch (JsonException)
			{
				return "{\"Values\":{}}";
			}
		}

		private static string JoinLogLines(IStringLog log)
		{
			if (log is null || log.Count is 0) return "";
			var sb = new System.Text.StringBuilder();
			for (var i = 0; i < log.Count; i++)
			{
				sb.Append(log[i]).Append('\n');
			}
			return sb.ToString();
		}

		/// <summary>
		/// True when a GPU drew this machine, which decides whether its states
		/// can outlive the session.
		///
		/// A bridged core's renderer keeps its OpenGL objects - textures,
		/// programs, vertex arrays - by the NAMES a driver gave it, and those
		/// names live in guest memory, so a savestate carries them faithfully
		/// into a session where they mean nothing: the context that owned them
		/// is gone, every call naming one is refused by the driver, and the
		/// core is never told (a GL error raised out at the bridge is invisible
		/// to the guest). The machine runs on perfectly - threads alive, memory
		/// changing - and draws NOTHING, which is what a PS3 project reopened
		/// after a few hundred frames did: a black screen, and then a crash
		/// (issue: "saving a project and then reloading it").
		///
		/// So a state a GPU drew is good for the session that made it and no
		/// other. Rewind and branches work as they always did; what does not
		/// cross a restart is written down here and recomputed by replay, which
		/// is what an empty greenzone has always meant.
		/// </summary>
		private bool DrawnByGpu
			=> Emulator is IGpuRendered { GpuRenderer: { Length: > 0 } } gpu
				&& !gpu.GpuStatesSurviveTheContext;

		/// <summary>
		/// The same question asked of the cache rather than of the machine, and
		/// it has to be: a project is READ before its core is booted, so there is
		/// no emulator to ask when the cache is opened. What there is, is what
		/// the project's last save wrote down - the driver that drew it - which
		/// is exactly the session those states belonged to.
		/// </summary>
		private bool StatesMadeByGpu
			=> HeaderEntries.TryGetValue(HeaderKeys.GpuRenderer, out var driver)
				&& !string.IsNullOrWhiteSpace(driver)
				&& !(HeaderEntries.TryGetValue(HeaderKeys.GpuStatesSurvive, out var survives)
					&& survives.Trim() == "1");

		/// <summary>
		/// The regenerable bulk, beside the project: greenzone, lag log, session
		/// position, column layout, verification log, branch states and
		/// screenshots (joined to the project's branches by order). A failed
		/// cache write never fails the save - the cache is the one file whose
		/// loss costs recomputation only.
		///
		/// The states themselves are left out for a machine a GPU drew (see
		/// <see cref="DrawnByGpu"/>): they could only be loaded back into a
		/// machine that cannot draw, and for a PlayStation 3 each one is the
		/// better part of a gigabyte.
		/// </summary>
		private void WriteCacheFile(string path)
		{
			var createResult = ZipStateSaver.Create(path, Session.Settings.MovieCompressionLevel);
			if (createResult.IsError) return;
			var bs = createResult.Value;
			try
			{
				IStateManagerSettings settingsToSave;
				try
				{
					settingsToSave = TasStateManager?.Settings ?? Session.Settings.DefaultTasStateManagerSettings;
				}
				catch
				{
					settingsToSave = Session.Settings.DefaultTasStateManagerSettings;
				}
				var settings = JsonConvert.SerializeObject(
					settingsToSave,
					new JsonSerializerSettings() { TypeNameHandling = TypeNameHandling.Objects });
				bs.PutLump(BinaryStateLump.StateHistorySettings, tw => tw.WriteLine(settings));
				// which machine these states belong to - checked before any is loaded
				bs.PutLump(BinaryStateLump.Machine, tw => tw.WriteLine(MachineIdentityOf(Project)));
				bs.PutLump(BinaryStateLump.LagLog, tw => LagLog.Save(tw), zstdCompress: true);
				if (ClientSettingsForSave != null)
				{
					var clientSettingsJson = ClientSettingsForSave();
					bs.PutLump(BinaryStateLump.ClientSettings, (TextWriter tw) => tw.Write(clientSettingsJson));
				}
				if (VerificationLog.Count is not 0)
				{
					bs.PutLump(BinaryStateLump.VerificationLog, tw => tw.WriteLine(VerificationLog.ToInputLog()));
				}
				bs.PutLump(BinaryStateLump.Session, tw => tw.WriteLine(JsonConvert.SerializeObject(TasSession)));
				// ZipStateSaver surfaces a failing lump only at close, which would
				// poison the whole cache - so the state history is serialized into
				// memory FIRST, and a manager that cannot serialize costs a cold
				// greenzone on the next load, nothing more
				byte[] history = null;
				if (TasStateManager is not null && !DrawnByGpu)
				{
					try
					{
						using var ms = new MemoryStream();
						using var bw = new BinaryWriter(ms);
						TasStateManager.SaveStateHistory(bw);
						bw.Flush();
						history = ms.ToArray();
					}
					catch
					{
						history = null;
					}
				}
				if (history is not null)
				{
					bs.PutLump(BinaryStateLump.StateHistory, (Stream s) => s.Write(history, 0, history.Length));
				}

				var ncore = new IndexedStateLump(BinaryStateLump.BranchCoreData);
				var nframebuffer = new IndexedStateLump(BinaryStateLump.BranchFrameBuffer);
				var ncoreframebuffer = new IndexedStateLump(BinaryStateLump.BranchCoreFrameBuffer);
				foreach (var b in Branches)
				{
					// the branch's picture and its metadata are worth keeping; the
					// machine behind it is not, for the same reason the greenzone
					// is not (see DrawnByGpu). Skipped for ALL branches or none:
					// these lumps are joined to the branches by order.
					if (b.CoreData is not null && !DrawnByGpu)
					{
						bs.PutLump(ncore, (Stream s) => s.Write(b.CoreData, 0, b.CoreData.Length));
					}
					if (b.OSDFrameBuffer is not null)
					{
						bs.PutLump(nframebuffer, s =>
						{
							var vp = new BitmapBufferVideoProvider(b.OSDFrameBuffer);
							QuickBmpFile.Save(vp, s, b.OSDFrameBuffer.Width, b.OSDFrameBuffer.Height);
						}, zstdCompress: false);
					}
					if (b.CoreFrameBuffer is not null)
					{
						bs.PutLump(ncoreframebuffer, s =>
						{
							var vp = new BitmapBufferVideoProvider(b.CoreFrameBuffer);
							QuickBmpFile.Save(vp, s, b.CoreFrameBuffer.Width, b.CoreFrameBuffer.Height);
						}, zstdCompress: false);
					}
					ncore.Increment();
					nframebuffer.Increment();
					ncoreframebuffer.Increment();
				}
			}
			catch
			{
				bs.Abort();
				return;
			}
			bs.CloseAndDispose();
		}

		protected override bool LoadProjectFormat()
		{
			_project?.Dispose();
			EngineProgress.Report("reading the project");
			_project = EngineProject.Open(Filename); // structural errors surface with the engine's reason

			ClearBeforeLoad();
			var p = _project;

			// the ctor's defaults, then the stored header metadata over them
			Header[HeaderKeys.MovieVersion] = $"Chimera Project File v{CurrentVersion.ToString(CultureInfo.InvariantCulture)}";
			for (var i = 0; i < p.HeaderCount; i++)
			{
				Header[p.HeaderKeyAt(i)] = p.HeaderValueAt(i);
			}
			if (StartsFromSavestate)
			{
				throw new InvalidOperationException("savestate-anchored projects are not supported (docs/project.md)");
			}
			if (p.CoreName.Length is not 0) Header[HeaderKeys.Core] = p.CoreName;
			if (p.CoreVersion.Length is not 0) Header[HeaderKeys.CoreVersion] = p.CoreVersion;
			if (p.CoreSha1.Length is not 0) Header[HeaderKeys.CorePackageSha1] = p.CoreSha1;
			if (p.Title.Length is not 0) Header[HeaderKeys.GameName] = p.Title;
			Header[HeaderKeys.Rerecords] = p.Rerecords.ToString();

			foreach (var line in p.Description.Split('\n'))
			{
				if (!string.IsNullOrWhiteSpace(line)) Comments.Add(line);
			}
			for (var i = 0; i < p.SubtitleCount; i++)
			{
				Subtitles.AddFromString(p.SubtitleAt(i));
			}
			Subtitles.Sort();

			SettingsJson = WrapSettings(p.SettingsJson);

			var logText = p.LogText;
			if (logText.Length is not 0)
			{
				EngineProgress.Report("reading the input log");
				IsCountingRerecords = false;
				MakeBackup = false;
				ExtractInputLog(new StringReader(logText), out _);
				IsCountingRerecords = true;
			}

			for (var i = 0; i < p.MarkerCount; i++)
			{
				Markers.Add(new TasMovieMarker(checked((int)p.MarkerFrame(i)), p.MarkerText(i))
				{
					WantsState = p.MarkerKeepState(i),
				}, skipHistory: true);
			}
			// the run's own markers are never written to the file, so they are
			// worked out again here - which is the only way they can be trusted
			RefreshLastNonEmptyInput(0);
			Markers.RefreshPermanent();

			Branches.Clear();
			for (var i = 0; i < p.BranchCount; i++)
			{
				var b = new TasBranch
				{
					Frame = checked((int)p.BranchFrame(i)),
					UserText = p.BranchName(i),
					ChangeLog = new TasMovieChangeLog(this) { MaxSteps = ChangeLog.MaxSteps },
					InputLog = StringLogUtil.MakeStringLog(),
					Markers = new TasMovieMarkerList(this),
				};
				b.TimeStamp = DateTime.TryParse(
					p.BranchTime(i), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var stamp)
						? stamp
						: DateTime.Now;
				foreach (var line in p.BranchLogText(i).Split('\n'))
				{
					if (line.StartsWith('|')) b.InputLog.Add(line);
				}
				for (var m = 0; m < p.BranchMarkerCount(i); m++)
				{
					b.Markers.Add(new TasMovieMarker(checked((int)p.BranchMarkerFrame(i, m)), p.BranchMarkerText(i, m))
					{
						WantsState = p.BranchMarkerKeepState(i, m),
					}, skipHistory: true);
				}
				Branches.Add(b);
			}

			EngineProgress.Report("reading the greenzone");
			LoadCacheFile();

			ChangeLog.Clear();
			Changes = false;
			return true;
		}

		/// <summary>
		/// The cache sibling, when it is there: absent or unreadable means a
		/// clean slate (fresh greenzone, no session position), never a failed
		/// load - that is the deal that keeps it out of the project's identity.
		/// </summary>
		private void LoadCacheFile()
		{
			DroppedCacheNote = null;
			ZipStateLoader bl = null;
			try
			{
				if (File.Exists(GreenZoneFilename)) bl = ZipStateLoader.LoadAndDetect(GreenZoneFilename, true);
			}
			catch
			{
				bl = null;
			}

			// The states in it are states of ONE machine; the sandbox will load
			// them into any machine running the same binary and only complain on
			// stderr, so the check has to be here, before a single one is used.
			// A cache from another machine is a clean slate, like a lost one:
			// recomputation, never work (docs/project.md).
			if (bl is not null)
			{
				string recorded = null;
				bl.GetLump(BinaryStateLump.Machine, abort: false, tr => recorded = tr.ReadLine()?.Trim());
				var current = MachineIdentityOf(Project);
				if (recorded is null || !string.Equals(recorded, current, StringComparison.OrdinalIgnoreCase))
				{
					DroppedCacheNote = recorded is null
						? "The cached states beside this project do not say which machine made them, so they were not used: the greenzone starts empty."
						: "The cached states beside this project were made by a machine with other settings, files or core, so they were not used: the greenzone starts empty.";
					bl.Dispose();
					bl = null;
				}
			}

			if (bl is null)
			{
				TasStateManager?.Dispose();
				TasStateManager = Session.Settings.DefaultTasStateManagerSettings.CreateManager(IsReserved);
				return;
			}

			using (bl)
			{
				bl.GetLump(BinaryStateLump.LagLog, abort: false, tr => LagLog.Load(tr));

				bl.GetLump(BinaryStateLump.ClientSettings, abort: false, tr =>
				{
					var clientSettings = tr.ReadToEnd();
					if (!string.IsNullOrEmpty(clientSettings)) LoadedClientSettings = clientSettings;
				});

				bl.GetLump(BinaryStateLump.VerificationLog, abort: false, tr =>
				{
					VerificationLog.Clear();
					while (tr.ReadLine() is string line)
					{
						if (line.StartsWith('|')) VerificationLog.Add(line);
					}
				});

				bl.GetLump(BinaryStateLump.Session, abort: false, tr =>
				{
					try
					{
						TasSession = JsonConvert.DeserializeObject<TasSession>(tr.ReadToEnd());
						Branches.Current = TasSession.CurrentBranch;
					}
					catch
					{
						// a fresh session position instead
					}
				});

				// branch states and screenshots, joined by order
				var ncore = new IndexedStateLump(BinaryStateLump.BranchCoreData);
				var nframebuffer = new IndexedStateLump(BinaryStateLump.BranchFrameBuffer);
				var ncoreframebuffer = new IndexedStateLump(BinaryStateLump.BranchCoreFrameBuffer);
				foreach (var b in Branches)
				{
					// a state a GPU drew belongs to the session that drew it; an
					// older cache may still hold one, and loading it would put a
					// machine that cannot draw on the screen (see DrawnByGpu)
					if (!StatesMadeByGpu)
					{
						bl.GetLump(ncore, abort: false, (Stream s, long _) => b.CoreData = s.ReadAllBytes());
					}
					bl.GetLump(nframebuffer, abort: false, (Stream s, long _) =>
					{
						QuickBmpFile.LoadAuto(s, out var vp);
						b.OSDFrameBuffer = new BitmapBuffer(vp.BufferWidth, vp.BufferHeight, vp.GetVideoBuffer());
					});
					bl.GetLump(ncoreframebuffer, abort: false, (Stream s, long _) =>
					{
						QuickBmpFile.LoadAuto(s, out var vp);
						b.CoreFrameBuffer = new BitmapBuffer(vp.BufferWidth, vp.BufferHeight, vp.GetVideoBuffer());
					});
					ncore.Increment();
					nframebuffer.Increment();
					ncoreframebuffer.Increment();
				}

				var settings = Session.Settings.DefaultTasStateManagerSettings;
				bl.GetLump(BinaryStateLump.StateHistorySettings, abort: false, tr =>
				{
					try
					{
						settings = JsonConvert.DeserializeObject<IStateManagerSettings>(tr.ReadToEnd()) ?? settings;
					}
					catch
					{
						// defaults instead
					}
				});

				TasStateManager?.Dispose();
				TasStateManager = null;
				var hasHistory = !StatesMadeByGpu && bl.GetLump(BinaryStateLump.StateHistory, abort: false, br =>
				{
					try
					{
						TasStateManager = settings.CreateManager(IsReserved);
						TasStateManager.LoadStateHistory(br);
					}
					catch
					{
						TasStateManager?.Dispose();
						TasStateManager = null;
					}
				});
				// Said when there is work to say it about: a project with frames in
				// it opens with an empty greenzone on a machine a GPU draws, and a
				// person who is not told simply sees their cached states gone.
				if (StatesMadeByGpu && DroppedCacheNote is null && InputLogLength > 0)
				{
					DroppedCacheNote =
						"This machine is drawn by a GPU, and what it draws lives outside the machine: a state"
						+ " it made is good only in the session that made it. The greenzone therefore starts"
						+ " empty and fills again as the movie plays.";
				}
				if (!hasHistory || TasStateManager is null)
				{
					try
					{
						TasStateManager = settings.CreateManager(IsReserved);
					}
					catch
					{
						TasStateManager = Session.Settings.DefaultTasStateManagerSettings.CreateManager(IsReserved);
					}
				}
			}
		}
	}
}
