using System.IO;
using System.Linq;

using Chimera.Client.Common;
using Chimera.Emulation.Common.Engine;

namespace Chimera.Tests.Client.Common
{
	/// <summary>
	/// The sidecar that remembers where a machine keeps a project's files. It is
	/// a hint and nothing more: it may spare the resolution dialog, it may never
	/// mount bytes the project did not record, and the project file must stay
	/// free of paths so it can still be handed to someone else.
	/// </summary>
	[TestClass]
	public class ProjectLocalPathsTests
	{
		private static string _dir = "";

		private static string _dataHomeWas = "";

		[ClassInitialize]
		public static void MakePlayground(TestContext _)
		{
			_dir = Path.Combine(Path.GetTempPath(), $"chimera-local-paths-{System.Diagnostics.Process.GetCurrentProcess().Id}");
			Directory.CreateDirectory(_dir);
			// the remembered paths live in the per-user cache now, and a test has no
			// business writing into the machine's real one
			_dataHomeWas = System.Environment.GetEnvironmentVariable("CHIMERA_DATA_HOME") ?? "";
			System.Environment.SetEnvironmentVariable("CHIMERA_DATA_HOME", Path.Combine(_dir, "data-home"));
		}

		[ClassCleanup]
		public static void RemovePlayground()
		{
			System.Environment.SetEnvironmentVariable("CHIMERA_DATA_HOME", _dataHomeWas.Length is 0 ? null : _dataHomeWas);
			Directory.Delete(_dir, recursive: true);
		}

		/// <summary>a project whose rom lives somewhere else entirely</summary>
		private static (string ProjectPath, string RomPath) MakeProject(string name, string romBytes = "cartridge bytes")
		{
			var romDir = Path.Combine(_dir, $"{name}-roms");
			Directory.CreateDirectory(romDir);
			var romPath = Path.Combine(romDir, "game.nes");
			File.WriteAllText(romPath, romBytes);

			var projectDir = Path.Combine(_dir, $"{name}-work");
			Directory.CreateDirectory(projectDir);
			var projectPath = Path.Combine(projectDir, $"{name}.chimeraProject");

			using var p = EngineProject.New();
			p.SetCore("quickernes", "abc123+local", new string('B', 40));
			p.FileAdd("game.nes", "rom", romPath);
			p.Save(projectPath);
			ProjectLocalPaths.Read(p, projectPath).Save(p);
			return (projectPath, romPath);
		}

		[TestMethod]
		public void TheProjectItselfNeverLearnsAPath()
		{
			var (projectPath, romPath) = MakeProject("distributable");
			var json = File.ReadAllText(projectPath);
			StringAssert.Contains(json, "game.nes", "the project names the file");
			Assert.IsFalse(json.Contains(romPath), "the project must carry no paths - it is meant to be handed to someone else");
			Assert.IsFalse(json.Contains(Path.GetDirectoryName(romPath)!), "not even the folder");
		}

		[TestMethod]
		public void TheSidecarFindsAFileThatIsNotBesideTheProject()
		{
			var (projectPath, romPath) = MakeProject("remembered");

			using var reopened = EngineProject.Open(projectPath);
			Assert.IsTrue(File.Exists(ProjectLocalPaths.PathFor(reopened)), "saving the project recorded the paths in its cache");
			reopened.ResolveDir(Path.GetDirectoryName(projectPath)!);
			Assert.IsFalse(reopened.FilesOk, "the rom is not beside the project, so nothing resolved");

			Assert.AreEqual(1, ProjectLocalPaths.Read(reopened, projectPath).ApplyTo(reopened));
			Assert.IsTrue(reopened.FilesOk, "the remembered location resolved it, with no dialog");
			Assert.AreEqual(Path.GetFullPath(romPath), Path.GetFullPath(reopened.FileSourcePath(0)));
		}

		[TestMethod]
		public void ARememberedPathHoldingOtherBytesIsLeftForTheUser()
		{
			var (projectPath, romPath) = MakeProject("changed");
			File.WriteAllText(romPath, "a different cartridge entirely");

			using var reopened = EngineProject.Open(projectPath);
			reopened.ResolveDir(Path.GetDirectoryName(projectPath)!);
			Assert.AreEqual(0, ProjectLocalPaths.Read(reopened, projectPath).ApplyTo(reopened), "nothing was resolved");
			Assert.AreEqual(1, reopened.FileStatus(0), "the file is UNRESOLVED, not quietly mounted with the wrong bytes");
			Assert.IsFalse(reopened.FilesOk, "so the resolution dialog still gets its say");
		}

		[TestMethod]
		public void AMissingSidecarIsSimplyNoHint()
		{
			var (projectPath, _) = MakeProject("nohint");
			using var reopened = EngineProject.Open(projectPath);
			File.Delete(ProjectLocalPaths.PathFor(reopened));

			var local = ProjectLocalPaths.Read(reopened, projectPath);
			Assert.AreEqual(0, local.Files.Count);
			Assert.AreEqual(0, local.ApplyTo(reopened));
		}

		[TestMethod]
		public void AnUnreadableSidecarIsSimplyNoHint()
		{
			var (projectPath, _) = MakeProject("garbled");
			using var reopened = EngineProject.Open(projectPath);
			File.WriteAllText(ProjectLocalPaths.PathFor(reopened), "{ this is not json");

			var local = ProjectLocalPaths.Read(reopened, projectPath);
			Assert.AreEqual(0, local.Files.Count, "a hint that cannot be read is a hint nobody has");
		}

		[TestMethod]
		public void FirmwareLocationsAreRememberedToo()
		{
			var (projectPath, _) = MakeProject("bios");
			var biosPath = Path.Combine(_dir, "panafz1.bin");
			File.WriteAllText(biosPath, "bios bytes");

			using var opened = EngineProject.Open(projectPath);
			var local = ProjectLocalPaths.Read(opened, projectPath);
			local.RememberFirmware("panafz1", biosPath);
			local.Save(opened);

			var reread = ProjectLocalPaths.Read(opened, projectPath);
			Assert.AreEqual(Path.GetFullPath(biosPath), reread.Firmware["panafz1"]);
			Assert.IsTrue(reread.Files.ContainsKey("game.nes"), "and the files it already knew about survived");
		}

		/// <summary>
		/// The point of the cache: a .chimeraProject is the one file that exists as
		/// far as anyone else is concerned, so the folder it lives in - which for
		/// most people is a synced one - holds nothing else.
		/// </summary>
		[TestMethod]
		public void TheProjectFolderHoldsOnlyTheProject()
		{
			var (projectPath, _) = MakeProject("tidy");
			var folder = Path.GetDirectoryName(projectPath)!;
			CollectionAssert.AreEqual(
				new[] { Path.GetFileName(projectPath) },
				Directory.GetFiles(folder).Select(Path.GetFileName).OrderBy(static n => n, System.StringComparer.Ordinal).ToArray(),
				"nothing but the project belongs beside the project");

			using var opened = EngineProject.Open(projectPath);
			Assert.IsTrue(File.Exists(ProjectLocalPaths.PathFor(opened)), "and the paths went to the cache instead");
		}

		/// <summary>
		/// A project made before the cache existed has its sidecar beside it. It is
		/// taken over rather than ignored - nobody should have to find their files
		/// again - and then it stops cluttering the folder.
		/// </summary>
		[TestMethod]
		public void ASidecarLeftByAnOlderChimeraIsTakenOver()
		{
			var (projectPath, romPath) = MakeProject("legacy");
			using var opened = EngineProject.Open(projectPath);

			// what an older Chimera would have left: the record beside the project,
			// and nothing in the cache
			var legacy = ProjectLocalPaths.LegacyPathFor(projectPath);
			File.Move(ProjectLocalPaths.PathFor(opened), legacy);

			var local = ProjectLocalPaths.Read(opened, projectPath);
			Assert.AreEqual(Path.GetFullPath(romPath), local.Files["game.nes"], "the old sidecar was read");
			Assert.IsTrue(File.Exists(ProjectLocalPaths.PathFor(opened)), "and moved into the cache");
			Assert.IsFalse(File.Exists(legacy), "and taken out of the project's folder");
		}

		/// <summary>
		/// Two attempts at the same game are two projects. Anything derived from
		/// the contents would key them the same and let one clobber the other's
		/// remembered paths and state history; an id cannot.
		/// </summary>
		[TestMethod]
		public void TwoProjectsOfTheSameGameDoNotShareACache()
		{
			var (firstPath, _) = MakeProject("twin-a");
			var (secondPath, _) = MakeProject("twin-b");
			using var first = EngineProject.Open(firstPath);
			using var second = EngineProject.Open(secondPath);

			Assert.AreNotEqual(first.Id, second.Id, "each project is minted its own identity");
			Assert.AreNotEqual(ProjectCache.DirectoryFor(first.Id), ProjectCache.DirectoryFor(second.Id));
		}

		/// <summary>
		/// The identity survives the file being renamed or moved, which is what a
		/// path could never do and why the cache is not keyed by one.
		/// </summary>
		[TestMethod]
		public void MovingTheProjectKeepsItsCache()
		{
			var (projectPath, romPath) = MakeProject("moved");
			string id;
			using (var before = EngineProject.Open(projectPath)) id = before.Id;

			var elsewhere = Path.Combine(_dir, "moved-elsewhere.chimeraProject");
			File.Move(projectPath, elsewhere);

			using var after = EngineProject.Open(elsewhere);
			Assert.AreEqual(id, after.Id, "the same project, wherever it now lives");
			Assert.AreEqual(Path.GetFullPath(romPath), ProjectLocalPaths.Read(after, elsewhere).Files["game.nes"],
				"so what it remembered came with it");
		}

		/// <summary>
		/// A project the wizard has just made has no file yet, so the boot's
		/// firmware lookups have no sidecar to go in - and the config that also
		/// held them is not forever: replace it, or move the dumps, and a saved
		/// project will not open with its own firmware still on the machine that
		/// made it (issue #40). The first save is the first chance to write them
		/// down, and it takes it.
		/// </summary>
		[TestMethod]
		public void WhatTheBootFoundReachesTheFirstSidecarTheProjectGets()
		{
			ProjectLocalPaths.ForgetSessionFirmware();
			var (projectPath, _) = MakeProject("carried");
			var biosPath = Path.Combine(_dir, "mcpx_1.0.bin");
			File.WriteAllText(biosPath, "boot rom bytes");

			// the boot: a project with no file of its own, so this instance is
			// thrown away without ever being written
			new ProjectLocalPaths().RememberFirmware("mcpx", biosPath);

			// the save, later, of a record that has never heard of any of it
			using (var p = EngineProject.Open(projectPath))
			{
				ProjectLocalPaths.Read(p, projectPath).Save(p);
				Assert.AreEqual(Path.GetFullPath(biosPath), ProjectLocalPaths.Read(p, projectPath).Firmware["mcpx"]);
			}

			// and the next project starts from nothing: these are this project's
			// answers, not a running tally of every core the session has touched
			ProjectLocalPaths.ForgetSessionFirmware();
			var (other, _) = MakeProject("another");
			using (var p = EngineProject.Open(other))
			{
				ProjectLocalPaths.Read(p, other).Save(p);
				Assert.AreEqual(0, ProjectLocalPaths.Read(p, other).Firmware.Count);
			}
		}
	}
}
