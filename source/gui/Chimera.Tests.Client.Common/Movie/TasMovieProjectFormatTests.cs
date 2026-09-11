using System;
using System.IO;
using System.Linq;

using Chimera.Client.Common;
using Chimera.Emulation.Common;

namespace Chimera.Tests.Client.Common.Movie
{
	/// <summary>
	/// The JSON .chimeraProject as TasMovie's native format: everything that is
	/// work survives a save-load round trip through the project file alone,
	/// and the cache sibling carries only what may be lost (docs/project.md).
	/// </summary>
	[TestClass]
	public class TasMovieProjectFormatTests
	{
		private const string PalSettings = "{\"region\":\"pal\"}";

		private static string _dir = "";
		private static string _dataHomeWas = "";

		[ClassInitialize]
		public static void MakePlayground(TestContext _)
		{
			_dir = Path.Combine(Path.GetTempPath(), $"chimera-tasmovie-project-{System.Diagnostics.Process.GetCurrentProcess().Id}");
			Directory.CreateDirectory(_dir);
			// the greenzone lives in the per-user cache now, and a test has no
			// business writing into the user's
			_dataHomeWas = Environment.GetEnvironmentVariable("CHIMERA_DATA_HOME") ?? "";
			Environment.SetEnvironmentVariable("CHIMERA_DATA_HOME", Path.Combine(_dir, "data-home"));
		}

		/// <summary>
		/// Per test, not once per class: ClassCleanup defaults to running at the
		/// END OF THE ASSEMBLY, so another class restoring this variable can land
		/// in the middle of this one's tests and send a greenzone to the real
		/// user cache.
		/// </summary>
		[TestInitialize]
		public void UseThePlaygroundDataHome()
			=> Environment.SetEnvironmentVariable("CHIMERA_DATA_HOME", Path.Combine(_dir, "data-home"));

		[ClassCleanup]
		public static void RemovePlayground()
		{
			Environment.SetEnvironmentVariable("CHIMERA_DATA_HOME", _dataHomeWas.Length is 0 ? null : _dataHomeWas);
			Directory.Delete(_dir, recursive: true);
		}

		/// <summary>
		/// Everything in a project's folder that belongs to THAT project, by
		/// name. The playground is shared by every test here, so what matters is
		/// not that the folder is empty but that this project left nothing in it.
		/// </summary>
		private static string[] SiblingsOf(string projectPath)
			=> Directory.GetFiles(Path.GetDirectoryName(projectPath)!,
					Path.GetFileNameWithoutExtension(projectPath) + ".*")
				.Select(Path.GetFileName)
				.OrderBy(static n => n, StringComparer.Ordinal)
				.ToArray();

		private static TasMovie MakeWorkedMovie(string path, string gpuRenderer = "", bool statesSurvive = false)
		{
			FakeEmulator emu = new() { GpuRenderer = gpuRenderer, GpuStatesSurviveTheContext = statesSurvive };
			FakeMovieSession session = new(emu);
			TasMovie movie = new(session, path);
			session.Movie = movie;
			movie.Attach(emu);
			movie.InsertEmptyFrame(0, 6);
			movie.SetBoolState(3, "A", true);

			movie.HeaderEntries[HeaderKeys.Author] = "sergio";
			movie.HeaderEntries[HeaderKeys.Core] = "quickernes";
			movie.HeaderEntries[HeaderKeys.CoreVersion] = "abc123+local";
			movie.HeaderEntries[HeaderKeys.CorePackageSha1] = new string('B', 40);
			movie.HeaderEntries[HeaderKeys.GameName] = "sprilo";
			movie.Rerecords = 77;
			movie.SettingsJson = """{"region":"ntsc"}""";
			movie.Comments.Add("first comment");
			movie.Comments.Add("second comment");
			movie.Subtitles.AddFromString("subtitle 2 10 10 120 FFFFFFFF hi there");

			movie.Markers.Add(new TasMovieMarker(2, "the jump") { WantsState = false }, skipHistory: true);

			var branch = new TasBranch
			{
				Frame = 4,
				UserText = "risky route",
				TimeStamp = new DateTime(2026, 8, 26, 21, 0, 0, DateTimeKind.Utc),
				CoreData = [1, 2, 3, 4],
				InputLog = StringLogUtil.MakeStringLog(),
				Markers = new TasMovieMarkerList(movie),
			};
			branch.InputLog.Add(movie.GetInputLogEntry(0));
			branch.InputLog.Add(movie.GetInputLogEntry(3));
			branch.Markers.Add(new TasMovieMarker(1, "setup"), skipHistory: true);
			movie.Branches.Add(branch);

			return movie;
		}

		private static TasMovie LoadFresh(string path)
		{
			FakeEmulator emu = new();
			FakeMovieSession session = new(emu);
			TasMovie movie = new(session, path);
			session.Movie = movie;
			Assert.IsTrue(movie.Load(), "the project should load");
			movie.Attach(emu);
			return movie;
		}

		[TestMethod]
		public void ItWritesDownWhereTheRunsInputStops()
		{
			// so a reader does not have to know what a neutral entry looks like for
			// this core's controller in order to find the end of the run
			var path = Path.Combine(_dir, "last-input.chimeraProject");
			var movie = MakeWorkedMovie(path);   // presses A on frame 3 of 6
			movie.Save();

			var reloaded = LoadFresh(path);
			Assert.AreEqual("3", reloaded.HeaderEntries[HeaderKeys.LastInputFrame]);
			Assert.AreEqual(3, reloaded.LastNonEmptyInputFrame, "and it agrees with the log it was derived from");

			// it follows the log rather than being stamped once
			reloaded.SetBoolState(5, "A", true);
			reloaded.Save();
			Assert.AreEqual("5", LoadFresh(path).HeaderEntries[HeaderKeys.LastInputFrame]);
		}

		[TestMethod]
		public void ARecordedRateIsWhatTheMovieIsTimedBy()
		{
			// Chimera keeps no per-system rate table - the fallback is a flat
			// 60/50 - so a movie that did not write its rate down cannot be turned
			// into a duration by anything but the core that recorded it.
			var path = Path.Combine(_dir, "vsync.chimeraProject");
			var movie = MakeWorkedMovie(path);
			movie.Save();
			Assert.IsTrue(LoadFresh(path).HeaderEntries.ContainsKey(HeaderKeys.VsyncNumerator),
				"a save writes down what the attached machine runs at");

			// and the recorded rate is what a duration is computed from
			movie.HeaderEntries[HeaderKeys.VsyncNumerator] = "39375000";
			movie.HeaderEntries[HeaderKeys.VsyncDenominator] = "655171";
			Assert.AreEqual(60.0988, movie.FrameRate, 0.0001,
				"an NES runs at 60.0988, which no 60/50 guess would have said");
		}

		[TestMethod]
		public void AMovieSaysWhenAGpuDrewIt()
		{
			// A GPU is outside the sandbox and outside the savestate, so a run made
			// on one carries no promise that it replays. Writing that down is what
			// lets a desync somewhere else be understood rather than mysterious.
			var path = Path.Combine(_dir, "no-gpu.chimeraProject");
			MakeWorkedMovie(path).Save();
			Assert.IsFalse(LoadFresh(path).HeaderEntries.ContainsKey(HeaderKeys.GpuRenderer),
				"an ordinary run says nothing, because nothing happened");
		}

		[TestMethod]
		public void TheRunsOwnMarkersAreNotWrittenAndDoNotAccumulate()
		{
			// They are derived on load - writing them down would let them go stale,
			// and reading them back as ordinary markers would breed three more of
			// them on every save.
			var path = Path.Combine(_dir, "permanent-markers.chimeraProject");
			MakeWorkedMovie(path).Save();

			var reloaded = LoadFresh(path);
			Assert.AreEqual(3, reloaded.Markers.Count(static m => m.IsPermanent),
				"exactly the three the run derives");
			CollectionAssert.AreEqual(
				new[] { "the jump" },
				reloaded.Markers.Where(static m => !m.IsPermanent).Select(static m => m.Message).ToArray(),
				"and nothing of theirs left behind as somebody's own marker");

			// and again, because accumulation only shows on the second pass
			var twice = Path.Combine(_dir, "permanent-markers-twice.chimeraProject");
			reloaded.Filename = twice;
			reloaded.Save();
			var third = LoadFresh(twice);
			Assert.AreEqual(1, third.Markers.Count(static m => !m.IsPermanent),
				"a second round trip adds none either");
		}

		[TestMethod]
		public void TheWorkRoundTripsThroughTheProjectAlone()
		{
			var path = Path.Combine(_dir, "roundtrip.chimeraProject");
			var movie = MakeWorkedMovie(path);
			Assert.IsFalse(movie.Save().IsError);

			// it IS the JSON project format, and the cache is NOT beside it
			using (var fs = File.OpenRead(path))
			{
				Assert.AreEqual('{', (char)fs.ReadByte());
			}
			Assert.IsTrue(File.Exists(movie.GreenZoneFilename),
				$"greenzone missing; cache has: {string.Join(",", Directory.GetFiles(Path.GetDirectoryName(movie.GreenZoneFilename)!).Select(Path.GetFileName))}");
			CollectionAssert.AreEqual(
				new[] { "roundtrip.chimeraProject" },
				SiblingsOf(path),
				"the project's folder holds the project and nothing else - it is the folder people sync");

			var loaded = LoadFresh(path);
			Assert.AreEqual(6, loaded.InputLogLength);
			Assert.AreEqual(movie.GetInputLogEntry(3), loaded.GetInputLogEntry(3));
			Assert.AreEqual("sergio", loaded.HeaderEntries[HeaderKeys.Author]);
			Assert.AreEqual("quickernes", loaded.HeaderEntries[HeaderKeys.Core]);
			Assert.AreEqual("abc123+local", loaded.HeaderEntries[HeaderKeys.CoreVersion]);
			Assert.AreEqual(new string('B', 40), loaded.HeaderEntries[HeaderKeys.CorePackageSha1]);
			Assert.AreEqual("sprilo", loaded.HeaderEntries[HeaderKeys.GameName]);
			Assert.AreEqual(77UL, loaded.Rerecords);
			StringAssert.Contains(loaded.SettingsJson, "\"region\":\"ntsc\"");
			CollectionAssert.AreEqual(new[] { "first comment", "second comment" }, loaded.Comments.ToArray());
			Assert.AreEqual(1, loaded.Subtitles.Count);

			var marker = loaded.Markers.Single(m => m.Frame is 2);
			Assert.AreEqual("the jump", marker.Message);
			Assert.IsFalse(marker.WantsState);

			Assert.AreEqual(1, loaded.Branches.Count);
			var branch = loaded.Branches[0];
			Assert.AreEqual("risky route", branch.UserText);
			Assert.AreEqual(4, branch.Frame);
			Assert.AreEqual(new DateTime(2026, 8, 26, 21, 0, 0, DateTimeKind.Utc), branch.TimeStamp.ToUniversalTime());
			Assert.AreEqual(2, branch.InputLog.Count);
			Assert.AreEqual("setup", branch.Markers.Single().Message);
			CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, branch.CoreData, "the state came from the cache");
		}

		/// <summary>
		/// A machine a GPU drew keeps its states for the session that drew them.
		///
		/// The renderer holds its OpenGL objects by the names a driver handed
		/// out, those names live in guest memory, and a savestate carries them
		/// into a session where they mean nothing: the driver refuses every call
		/// naming one, the core is never told, and what comes out is a machine
		/// that runs and draws nothing - a black screen, then a crash. So the
		/// cache carries no states from such a machine, and any older cache's
		/// are not used.
		/// </summary>
		[TestMethod]
		public void StatesAGpuDrewDoNotOutliveTheirSession()
		{
			var path = Path.Combine(_dir, "gpudrawn.chimeraProject");
			var movie = MakeWorkedMovie(path, gpuRenderer: "4.5 (Core Profile) Mesa on llvmpipe");
			Assert.IsFalse(movie.Save().IsError);

			var loaded = LoadFresh(path);
			Assert.AreEqual(6, loaded.InputLogLength, "the work itself is untouched");
			Assert.AreEqual(1, loaded.Branches.Count);
			Assert.AreEqual("risky route", loaded.Branches[0].UserText, "and so is what a branch IS");
			Assert.IsNull(loaded.Branches[0].CoreData, "the branch keeps its input and loses its state");
			// a cold greenzone is not an empty one: the machine as it stands is
			// always the anchor, or no frame could be reached at all
			Assert.AreEqual(1, loaded.States?.Count ?? 0, "nothing came back but the anchor");
			Assert.IsNotNull(loaded.DroppedCacheNote, "and the person is told why it is empty");
			StringAssert.Contains(loaded.DroppedCacheNote, "GPU");

			// the same project on a machine no GPU drew keeps its states, which is
			// what every deterministic core does and must go on doing
			var plain = Path.Combine(_dir, "cpudrawn.chimeraProject");
			Assert.IsFalse(MakeWorkedMovie(plain).Save().IsError);
			var plainLoaded = LoadFresh(plain);
			Assert.IsNull(plainLoaded.DroppedCacheNote);
			CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, plainLoaded.Branches[0].CoreData,
				"the state came from the cache, as it always has");
		}

		/// <summary>
		/// ...unless the core says its renderer builds its objects again when
		/// the context it drew on is gone. Then the states are as good as any
		/// other core's, and the project keeps them.
		/// </summary>
		[TestMethod]
		public void ARendererThatRebuildsKeepsItsStates()
		{
			var path = Path.Combine(_dir, "rebuilds.chimeraProject");
			var movie = MakeWorkedMovie(path, gpuRenderer: "4.5 Mesa on llvmpipe", statesSurvive: true);
			Assert.IsFalse(movie.Save().IsError);

			var loaded = LoadFresh(path);
			Assert.IsNull(loaded.DroppedCacheNote, "nothing was dropped");
			CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, loaded.Branches[0].CoreData,
				"the branch kept the machine behind it");
			Assert.AreEqual("1", loaded.HeaderEntries[HeaderKeys.GpuStatesSurvive],
				"and the project says why, for a session with no core to ask");
		}

		/// <summary>
		/// A savestate is the memory of one exact machine, and the sandbox only
		/// checks the core binary when it loads one. The cache says which machine
		/// made its states, and a project whose machine has since changed - a
		/// setting edited, a file swapped, another core build - gets a clean slate
		/// from it rather than states that will fall over (issue #26).
		/// </summary>
		[TestMethod]
		public void ACacheMadeByAnotherMachineIsSetAside()
		{
			var path = Path.Combine(_dir, "othermachine.chimeraProject");
			var movie = MakeWorkedMovie(path);
			Assert.IsFalse(movie.Save().IsError);

			var same = LoadFresh(path);
			Assert.IsNull(same.DroppedCacheNote, "the same machine uses its cache");
			CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, same.Branches[0].CoreData);

			// the project's settings change underneath the cache
			using (var p = Chimera.Emulation.Common.Engine.EngineProject.Open(path))
			{
				p.SetSettingsJson("""{"region":"pal"}""");
				p.Save(path);
			}
			var other = LoadFresh(path);
			Assert.IsNotNull(other.DroppedCacheNote, "another machine's states are not loaded");
			StringAssert.Contains(other.DroppedCacheNote, "other settings");
			Assert.AreEqual(6, other.InputLogLength, "the work is untouched");
			Assert.AreEqual(1, other.Branches.Count);
			Assert.IsNull(other.Branches[0].CoreData, "the branch keeps its input and loses its state");

			// saving from the new machine writes a cache that is its own
			other.InsertEmptyFrame(0, 1);
			Assert.IsFalse(other.Save().IsError);
			var again = LoadFresh(path);
			Assert.IsNull(again.DroppedCacheNote, "the cache the new machine wrote is used");
		}

		[TestMethod]
		public void TheMachineLineDoesNotCareAboutKeyOrderOrCase()
		{
			using var a = Chimera.Emulation.Common.Engine.EngineProject.New();
			using var b = Chimera.Emulation.Common.Engine.EngineProject.New();
			a.SetCore("core", "v", "abcdef");
			b.SetCore("core", "v", "ABCDEF");
			a.SetSettingsJson("""{"x":1,"y":{"q":true,"p":false}}""");
			b.SetSettingsJson("""{"y":{"p":false,"q":true},"x":1}""");
			Assert.AreEqual(TasMovie.MachineIdentityOf(a), TasMovie.MachineIdentityOf(b));

			b.SetSettingsJson("""{"y":{"p":true,"q":true},"x":1}""");
			Assert.AreNotEqual(TasMovie.MachineIdentityOf(a), TasMovie.MachineIdentityOf(b), "a value that differs is a different machine");
		}

		/// <summary>
		/// A backup goes to the "Movie backups" folder, which a fresh install does
		/// not have; the save makes it rather than fail (issue #19).
		/// </summary>
		[TestMethod]
		public void ABackupMakesTheFolderItGoesTo()
		{
			var path = Path.Combine(_dir, "backedup.chimeraProject");
			var movie = MakeWorkedMovie(path);
			var backups = Path.Combine(_dir, "backups", "nested");
			((FakeMovieSession) movie.Session).BackupDirectory = backups;
			Assert.IsFalse(Directory.Exists(backups));

			var result = movie.SaveBackup();
			Assert.IsFalse(result.IsError, result.Exception?.Message);
			Assert.IsTrue(Directory.Exists(backups), "the folder was made");
			var written = Directory.GetFiles(backups, "*.chimeraProject");
			Assert.AreEqual(1, written.Length, "one backup project in it");
			StringAssert.StartsWith(Path.GetFileName(written[0]), "backedup.");
			Assert.AreEqual(0, Directory.GetFiles(backups, "*.chimeraGreenZone").Length, "a backup carries no cache");
		}

		/// <summary>
		/// A greenzone left beside a project by an older Chimera is taken over
		/// rather than ignored: it is moved into the cache and used. Ignoring it
		/// would be safe - it is only a cache - but it would also silently throw
		/// away hours of somebody's greenzone on the release that moved it, and
		/// leave the gigabyte behind in the folder that was the whole problem.
		/// </summary>
		[TestMethod]
		public void AGreenZoneLeftBesideAProjectIsTakenOver()
		{
			var path = Path.Combine(_dir, "legacy.chimeraProject");
			var movie = MakeWorkedMovie(path);
			Assert.IsFalse(movie.Save().IsError);

			// put it back where an older Chimera would have left it
			var legacy = TasMovie.LegacyGreenZonePathFor(path);
			File.Move(movie.GreenZoneFilename, legacy);
			Assert.IsFalse(File.Exists(movie.GreenZoneFilename));

			var loaded = LoadFresh(path);
			Assert.IsFalse(File.Exists(legacy), "the sibling is gone from the project's folder");
			Assert.IsTrue(File.Exists(loaded.GreenZoneFilename), "and is in the cache instead");
			Assert.IsNull(loaded.DroppedCacheNote, "it was used, not discarded");
			CollectionAssert.AreEqual(new[] { "legacy.chimeraProject" }, SiblingsOf(path));
		}

		/// <summary>
		/// The states survive closing and reopening, which is the entire point of
		/// keeping them. The engine owns the history and proves its own file
		/// round trips; what is checked here is the wiring above it - that the
		/// movie writes one where it says it does, and reads it back when the
		/// emulator arrives rather than when the project is parsed, since until
		/// then there is nowhere to put it.
		/// </summary>
		[TestMethod]
		public void TheStatesComeBackWhenTheProjectIsReopened()
		{
			var path = Path.Combine(_dir, "states.chimeraProject");
			var movie = MakeWorkedMovie(path);
			foreach (var f in new[] { 1, 2, 3, 4 }) movie.States.Capture(f);
			Assert.IsFalse(movie.Save().IsError);
			Assert.IsTrue(File.Exists(movie.StateHistoryFilename), "the history is a file of its own");
			CollectionAssert.AreEqual(new[] { "states.chimeraProject" }, SiblingsOf(path),
				"and it is not beside the project either");

			var loaded = LoadFresh(path);
			foreach (var f in new[] { 1, 2, 3, 4 })
			{
				Assert.IsTrue(loaded.States.Has(f), $"frame {f} came back");
			}

			// and the states go when the machine under them changes - the project's
			// settings edited on disk, so the history file is left as it was
			using (var p = Chimera.Emulation.Common.Engine.EngineProject.Open(path))
			{
				p.SetSettingsJson(PalSettings);
				p.Save(path);
			}
			var other = LoadFresh(path);
			Assert.IsFalse(other.States.Has(3), "another machine's states are not loaded");
			Assert.AreEqual(1, other.States.Count, "and it starts from the anchor alone");
		}

		[TestMethod]
		public void ALostCacheCostsRecomputationNeverWork()
		{
			var path = Path.Combine(_dir, "nocache.chimeraProject");
			var movie = MakeWorkedMovie(path);
			Assert.IsFalse(movie.Save().IsError);
			File.Delete(movie.GreenZoneFilename);

			var loaded = LoadFresh(path);
			Assert.AreEqual(6, loaded.InputLogLength, "the input log is work, not cache");
			Assert.AreEqual(1, loaded.Branches.Count, "the branch itself is work");
			Assert.AreEqual("risky route", loaded.Branches[0].UserText);
			Assert.IsNull(loaded.Branches[0].CoreData, "only its state was cache");
		}

		[TestMethod]
		public void TheWizardSideOfTheProjectSurvivesAMovieRoundTrip()
		{
			// a project born in the creation wizard carries a manifest and a
			// description the movie machinery knows nothing about; saving the
			// movie must preserve them
			var path = Path.Combine(_dir, "wizard.chimeraProject");
			var gamePath = Path.Combine(_dir, "game.nes");
			File.WriteAllText(gamePath, "cartridge bytes");
			using (var p = Chimera.Emulation.Common.Engine.EngineProject.New())
			{
				p.Title = "sprilo";
				p.Description = "the wizard's description";
				p.SetCore("quickernes", "abc123+local", new string('B', 40));
				p.FileAdd("game.nes", "rom", gamePath);
				p.Save(path);
			}

			var movie = LoadFresh(path);
			Assert.AreEqual("sprilo", movie.HeaderEntries[HeaderKeys.GameName]);
			movie.InsertEmptyFrame(0, 3);
			Assert.IsFalse(movie.Save().IsError);

			using var reloaded = Chimera.Emulation.Common.Engine.EngineProject.Open(path);
			Assert.AreEqual(1, reloaded.FileCount, "the manifest survived the movie save");
			Assert.AreEqual("game.nes", reloaded.FileName(0));
			Assert.AreEqual("the wizard's description", reloaded.Description);
			StringAssert.Contains(reloaded.LogText, "|", "and the input log is in the project now");
		}

		/// <summary>
		/// A project made by the wizard pins its core; the movie that starts from it
		/// has no headers at all yet. Saving must not read that silence as "no core"
		/// and unpin the project - a project nothing can run, which is what the
		/// frontend then says when you reopen it.
		/// </summary>
		[TestMethod]
		public void SavingAMovieThatIsSilentAboutTheCoreKeepsThePin()
		{
			var path = Path.Combine(_dir, "pinned.chimeraProject");
			using (var p = Chimera.Emulation.Common.Engine.EngineProject.New())
			{
				p.SetCore("quickernes", "abc123+local", new string('B', 40));
				p.Save(path);
			}

			FakeEmulator emu = new();
			FakeMovieSession session = new(emu);
			TasMovie movie = new(session, path);
			session.Movie = movie;
			Assert.IsTrue(movie.Load(), "the project should load");
			movie.Attach(emu);

			// the movie says nothing about the core, the way a brand new one does
			movie.HeaderEntries.Remove(HeaderKeys.Core);
			movie.HeaderEntries.Remove(HeaderKeys.CoreVersion);
			movie.HeaderEntries.Remove(HeaderKeys.CorePackageSha1);
			movie.InsertEmptyFrame(0, 2);
			Assert.IsFalse(movie.Save().IsError);

			using var reloaded = Chimera.Emulation.Common.Engine.EngineProject.Open(path);
			Assert.AreEqual("quickernes", reloaded.CoreName, "the core pin was lost on save");
			Assert.AreEqual("abc123+local", reloaded.CoreVersion);
			Assert.AreEqual(new string('B', 40), reloaded.CoreSha1);
		}

		/// <summary>
		/// The wizard records every exposed setting at its chosen value, and the
		/// movie that starts from that project has no settings text of its own -
		/// the project boot fills headers, never settings. Saving must keep the
		/// project's answers rather than write the movie's silence over them as
		/// "{}", which put every setting back to its default on reopen (issue #29).
		/// </summary>
		[TestMethod]
		public void SavingAMovieThatIsSilentAboutSettingsKeepsTheWizardsAnswers()
		{
			var path = Path.Combine(_dir, "fresh-settings.chimeraProject");
			var project = Chimera.Emulation.Common.Engine.EngineProject.New();
			project.SetCore("pcsx2-shaped", "abc123+local", new string('C', 40));
			project.SetSettingsJson("""{"fast_boot":false,"memcard1":false,"renderer":"software"}""");

			// the fresh shape: a movie for a project that has never been written -
			// nothing to load, the resolved project handed over, headers filled
			// from the machine, settings never mentioned
			FakeEmulator emu = new();
			FakeMovieSession session = new(emu);
			TasMovie movie = new(session, path);
			session.Movie = movie;
			movie.Attach(emu);
			movie.UseResolvedProject(project);
			movie.HeaderEntries[HeaderKeys.Core] = "pcsx2-shaped";
			Assert.AreEqual("", movie.SettingsJson, "the fresh movie says nothing about settings");
			movie.InsertEmptyFrame(0, 2);
			Assert.IsFalse(movie.Save().IsError);

			using var reloaded = Chimera.Emulation.Common.Engine.EngineProject.Open(path);
			StringAssert.Contains(reloaded.SettingsJson, "\"fast_boot\":false", "the wizard's answer was lost on save");
			StringAssert.Contains(reloaded.SettingsJson, "\"memcard1\":false");
			StringAssert.Contains(reloaded.SettingsJson, "\"renderer\":\"software\"");

			// and a movie that DOES carry settings still has the last word
			var loaded = LoadFresh(path);
			loaded.SettingsJson = """{"Values":{"fast_boot":true}}""";
			Assert.IsFalse(loaded.Save().IsError);
			using var overwritten = Chimera.Emulation.Common.Engine.EngineProject.Open(path);
			StringAssert.Contains(overwritten.SettingsJson, "\"fast_boot\":true");
		}
		/// <summary>
		/// The controller the frontend holds before any core has booted: the null
		/// emulator's, which names no controls at all. It carries a mnemonics
		/// cache because InputManager builds one for every emulator it syncs, the
		/// null one included - so an entry CAN be generated from it rather than
		/// the attempt being refused, which is what lets the fault below through.
		/// </summary>
		private static ControllerDefinition NullEmulatorControls()
		{
			var definition = new ControllerDefinition("Null Controller").MakeImmutable();
			definition.BuildMnemonicsCache("NULL");
			return definition;
		}

		/// <summary>
		/// Opens a saved project in the order the frontend really does it: the
		/// file is read FIRST, because it is the file that says which core to boot
		/// and with what, and only afterwards does the session get the machine's
		/// own controller.
		/// </summary>
		private static TasMovie OpenTheWayAProjectOpens(string path)
		{
			FakeEmulator emu = new();
			FakeMovieSession session = new(emu)
			{
				MovieController = new MovieController(NullEmulatorControls()),
			};
			TasMovie movie = new(session, path);
			session.Movie = movie;
			Assert.IsTrue(movie.Load(), "the project should load");

			// the boot: the machine is up, and the session speaks its controller
			session.MovieController = new MovieController(emu.ControllerDefinition, movie.LogKey);
			movie.Attach(emu);
			return movie;
		}

		/// <summary>
		/// Clearing frames writes a neutral entry over each one, and the piano
		/// roll reads those entries back on its very next paint. Both halves have
		/// to agree on the shape of an entry, and the writer's idea of it used to
		/// be settled the first time anything asked - during the project READ,
		/// before the core had booted, when the only controller to ask was the
		/// null emulator's and it knew none of this movie's axes. The entry that
		/// came out had no axis fields at all, and reading it back threw out of
		/// the paint: "Failed to draw input roll" (issue #54).
		/// </summary>
		[TestMethod]
		public void AClearedFrameIsStillShapedLikeTheMovie()
		{
			var path = Path.Combine(_dir, "clear-after-boot.chimeraProject");
			var worked = MakeWorkedMovie(path); // presses A on frame 3 of 6
			Assert.IsFalse(worked.Save().IsError);

			var movie = OpenTheWayAProjectOpens(path);
			var neutral = movie.GetInputLogEntry(0);

			movie.ClearFrame(3); // the Delete key, one frame of the selection

			// the paint that follows the edit
			Assert.AreEqual("", movie.DisplayValue(3, "A", true));
			Assert.AreEqual("", movie.DisplayValue(3, "Stick", true));
			Assert.AreEqual(neutral, movie.GetInputLogEntry(3),
				"the cleared entry is not shaped like the rest of the log");
		}

		/// <summary>
		/// Where a run's input stops is found by comparing entries against an
		/// empty one, and only the machine can say what empty looks like. Answered
		/// before the boot it matches nothing, so the answer falls through to the
		/// last frame of the movie and the run's "Last input" marker sits at the
		/// end of a run whose input stopped long before.
		/// </summary>
		[TestMethod]
		public void WhereTheInputStopsIsAnsweredByTheMachine()
		{
			var path = Path.Combine(_dir, "last-input-after-boot.chimeraProject");
			var worked = MakeWorkedMovie(path); // presses A on frame 3 of 6
			Assert.IsFalse(worked.Save().IsError);

			var movie = OpenTheWayAProjectOpens(path);

			Assert.AreEqual(3, movie.LastNonEmptyInputFrame);
			Assert.AreEqual(3, movie.Markers.Find(static m => m.Permanence == MarkerPermanence.LastInput)!.Frame,
				"the run's own marker followed the wrong answer");
		}
	}
}
