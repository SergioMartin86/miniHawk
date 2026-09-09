using Chimera.Display;
using Chimera.Emulation.Common;

namespace Chimera.Client.Common
{
	public interface IMainFormForTools : IDialogController
	{
		/// <remarks>referenced by 3 or more tools</remarks>
		CheatCollection CheatList { get; }

		/// <remarks>referenced by 3 or more tools</remarks>
		string CurrentlyOpenRom { get; }

		/// <remarks>referenced from HexEditor</remarks>
		LoadRomArgs CurrentlyOpenRomArgs { get; }

		/// <remarks>only referenced from TAStudio</remarks>
		bool EmulatorPaused { get; }

		/// <remarks>only referenced from PlaybackBox</remarks>
		bool HoldFrameAdvance { get; set; }

		/// <remarks>only referenced from LuaConsole</remarks>
		bool IsTurboing { get; }

		/// <remarks>only referenced from TAStudio</remarks>
		bool IsFastForwarding { get; }

		/// <remarks>referenced from PlayMovie and TAStudio</remarks>
		int? PauseOnFrame { get; set; }

		/// <remarks>only referenced from PlaybackBox</remarks>
		bool PressRewind { get; set; }

		/// <remarks>referenced from BookmarksBranchesBox and VideoWriterChooserForm</remarks>
		BitmapBuffer CaptureOSD();

		/// <remarks>only referenced from TAStudio</remarks>
		bool EnsureCoreIsAccurate();

		/// <summary>
		/// True while a shutdown NOBODY IS SITTING IN FRONT OF is under way - one
		/// asked for through the API, which is what a Lua script's client.exit()
		/// is. A prompt raised then is a prompt nothing can answer: the window
		/// waits forever on a question with no asker, and the run loop spins
		/// beside it. So the prompts that would block a close consult this and
		/// take the safe branch instead - safe because it is the one that leaves
		/// the file on disk exactly as it was.
		/// </summary>
		bool ShutdownIsUnattended { get; }

		/// <summary>
		/// Ends the session once the caller's own close has finished: a project and
		/// its TAStudio are one thing, so closing that window closes the project.
		/// </summary>
		/// <remarks>only referenced from TAStudio</remarks>
		void QueueProjectClose();

		/// <remarks>only referenced from TAStudio</remarks>
		void FrameAdvance(bool discardApiSurfaces = true);

		/// <remarks>only referenced from LuaConsole</remarks>
		/// <param name="forceWindowResize">Override <see cref="Common.Config.ResizeWithFramebuffer"/></param>
		void FrameBufferResized(bool forceWindowResize = false);

		/// <remarks>referenced from EmuClientApi</remarks>
		bool LoadRom(string path, LoadRomArgs args);

		/// <remarks>only referenced from BookmarksBranchesBox</remarks>
		BitmapBuffer MakeScreenshotImage();

		/// <remarks>referenced from ToolFormBase</remarks>
		void MaybePauseFromMenuOpened();

		/// <remarks>referenced from ToolFormBase</remarks>
		void MaybeUnpauseFromMenuClosed();

		/// <remarks>referenced by 3 or more tools</remarks>
		void PauseEmulator();

		/// <remarks>only referenced from TAStudio</remarks>
		bool BlockFrameAdvance { get; set; }

		/// <remarks>referenced from PlaybackBox and TAStudio</remarks>
		void SetMainformMovieInfo();

		/// <remarks>referenced by 3 or more tools</remarks>
		bool StartNewMovie(IMovie movie, bool newMovie);

		/// <remarks>only referenced from TAStudio</remarks>
		void TogglePause();

		/// <remarks>referenced by 3 or more tools</remarks>
		void UnpauseEmulator();

		/// <remarks>only referenced from LogWindow</remarks>

		/// <remarks>only referenced from TAStudio</remarks>
		void UpdateWindowTitle();
	}
}
