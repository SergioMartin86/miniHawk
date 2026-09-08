using System.Collections.Generic;
using Chimera.Emulation.Common;

namespace Chimera.Client.Common
{
	public interface ITasMovie : IMovie, IDisposable
	{
		bool BindMarkersToInput { get; set; }

		IMovieChangeLog ChangeLog { get; }
		IStateHistory States { get; }

		/// <summary>Before the machine moves, every frame.</summary>
		void GreenzoneBeforeFrame();

		/// <summary>Re-tells the history which frames must survive its thinning.</summary>
		void RefreshPins();

		/// <summary>
		/// Why the cache beside the project was set aside on load, or null when it
		/// was used (or there was none). One sentence, for the person opening it.
		/// </summary>
		string DroppedCacheNote { get; }
		Func<string> ClientSettingsForSave { get; set; }
		string LoadedClientSettings { get; }
		ITasMovieRecord this[int index] { get; }
		ITasSession TasSession { get; }
		TasMovieMarkerList Markers { get; }

		/// <summary>
		/// The last frame with anything pressed on it, or zero when nothing is
		/// pressed anywhere. "Empty" is the same emptiness Truncate uses: the log
		/// entry a default controller generates.
		/// </summary>
		int LastNonEmptyInputFrame { get; }
		ITasBranchCollection Branches { get; }
		TasLagLog LagLog { get; }
		IStringLog VerificationLog { get; }

		/// <summary>
		/// Adopts the frontend's RESOLVED engine project as this movie's backing
		/// store, so saves record what actually ran (docs/project.md).
		/// </summary>
		void UseResolvedProject(Chimera.Emulation.Common.Engine.EngineProject project);
		int LastEditedFrame { get; }
		bool LastEditWasRecording { get; }

		/// <summary>
		/// Called whenever the movie is modified in a way that could invalidate savestates in the movie's state history.
		/// Called regardless of whether any states were actually invalidated.
		/// The parameter is the last frame number who's savestate (if it exists) is still valid. That is, the first of the modified frames.
		/// </summary>
		Action<int> GreenzoneInvalidated { get; set; }

		string DisplayValue(int frame, string buttonName, bool defaultAxisAsBlank);
		void FlagChanges();
		void ClearChanges();

		/// <summary>
		/// Replaces the given frame's input with an empty frame
		/// </summary>
		void ClearFrame(int frame);

		void GreenzoneCurrentFrame();
		void ToggleBoolState(int frame, string buttonName);
		void SetAxisState(int frame, string buttonName, int val);
		void SetAxisStates(int frame, int count, string buttonName, int val);
		void SetBoolState(int frame, string buttonName, bool val);
		void SetBoolStates(int frame, int count, string buttonName, bool val);
		void InsertInput(int frame, string inputState);
		void InsertInput(int frame, IEnumerable<string> inputLog);
		void InsertInput(int frame, IEnumerable<IController> inputStates);

		void InsertEmptyFrame(int frame, int count = 1);
		void CopyOverInput(int frame, IEnumerable<IController> inputStates);

		void RemoveFrame(int frame);
		void RemoveFrames(ICollection<int> frames);

		/// <summary>
		/// Remove all frames between removeStart and removeUpTo (excluding removeUpTo).
		/// If <see cref="IMovie.ActiveControllerInputs"/> is not null, this will not actually change the length of the movie.
		/// </summary>
		/// <param name="removeStart">The first frame to remove.</param>
		/// <param name="removeUpTo">The frame after the last frame to remove.</param>
		void RemoveFrames(int removeStart, int removeUpTo);
		void PokeFrame(int frame, string source);

		void LoadBranch(TasBranch branch);

		void CopyVerificationLog(IEnumerable<string> log);

		bool IsReserved(int frame);

		/// <summary>
		/// Sometimes we will be doing a whole bunch of small operations together. (e.g. large painting undo, large Lua edit)
		/// This avoids using the greenzone invalidated callback for each one, making things run smoother.
		/// Note that this is different from undo batching.
		/// For one, we don't undo batch an undo itself.
		/// For two, undo batches might span a significant amount of time during which users want to atually see edits or auto-restore in TAStudio.
		/// </summary>
		void SingleInvalidation(Action action);
	}
}
