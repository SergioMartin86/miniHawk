#nullable enable

namespace Chimera.Emulation.Common
{
	/// <summary>
	/// Where the machine has been along a movie's timeline, kept by the engine.
	///
	/// The whole of it lives in C++ (docs/state-manager.md): what a stored frame
	/// costs, when a frame is thinned, which stretches go to disk, and how a
	/// frame is reached from the one before it. This interface is a remote
	/// control, and deliberately holds no state of its own - a second copy of
	/// "which frames exist" up here is a second thing to keep true, and it would
	/// go wrong quietly, on the long runs where nobody could reproduce it.
	///
	/// The frame numbers are the CALLER'S. The engine counts frames only for a
	/// movie it owns itself, and the frontend owns its own.
	/// </summary>
	public interface IStateHistory : IEmulatorService
	{
		/// <summary>Bytes to keep in memory; 0 turns it off and drops everything.</summary>
		void Enable(long budgetBytes);

		/// <summary>Where the far band goes when the budget is full; null for nowhere.</summary>
		void SpillTo(string? directory);

		/// <summary>Frames it can produce - not the number of stored objects.</summary>
		long Count { get; }

		/// <summary>The greatest frame it can produce at or before this one, or -1.</summary>
		int Nearest(int frame);

		bool Has(int frame);

		/// <summary>
		/// Before the machine moves, every time. A delta is what changed since a
		/// marked moment, so the moment has to be marked first; skipping it is
		/// not wrong, it just makes the next capture a whole state.
		/// </summary>
		void BeforeAdvance();

		/// <summary>After it has moved, with the frame now stood on.</summary>
		void Capture(int frame);

		/// <summary>
		/// Puts the machine on a stored frame, restoring the emulator's own
		/// side-band with it. False when that frame is not one it can produce.
		/// </summary>
		bool RestoreTo(int frame);

		/// <summary>
		/// Walks the machine BACKWARDS towards a frame, and answers with the one
		/// it reached; -1 if it could not move at all.
		///
		/// This is what rewinding wants. Reaching the frame before through
		/// <see cref="RestoreTo"/> costs an anchor and every step between it and
		/// the target - to undo one frame's work. Going back undoes exactly that
		/// frame. It is only ever possible near the playhead, which is where
		/// stepping backwards happens, so it stops early rather than failing.
		/// </summary>
		int RewindTo(int frame, int from);

		/// <summary>Drops everything after this frame - what an input edit means.</summary>
		void InvalidateAfter(int afterFrame);

		/// <summary>
		/// The history across sessions. Both answer "it did not throw", never
		/// "the states are there": a history of another machine is dropped
		/// rather than refused, because losing it costs replaying and never work.
		/// </summary>
		bool Save(string path, string machineId);

		bool Load(string path, string machineId);

		/// <summary>
		/// Frames to keep reachable whatever the thinning would otherwise do.
		/// What deserves it is this layer's business - a marker somebody wants
		/// to jump to instantly - and nothing the engine could work out itself.
		/// </summary>
		void Pin(int frame, bool pinned);

		void UnpinAll();
	}
}
