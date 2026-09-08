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
	public interface IStateHistory
	{
		/// <summary>Bytes to keep in memory; 0 turns it off and drops everything.</summary>
		void HistoryEnable(long budgetBytes);

		/// <summary>Where the far band goes when the budget is full; null for nowhere.</summary>
		void HistorySpillTo(string? directory);

		/// <summary>Frames it can produce - not the number of stored objects.</summary>
		long HistoryCount { get; }

		/// <summary>The greatest frame it can produce at or before this one, or -1.</summary>
		int HistoryNearest(int frame);

		bool HistoryHas(int frame);

		/// <summary>
		/// Before the machine moves, every time. A delta is what changed since a
		/// marked moment, so the moment has to be marked first; skipping it is
		/// not wrong, it just makes the next capture a whole state.
		/// </summary>
		void HistoryBeforeAdvance();

		/// <summary>After it has moved, with the frame now stood on.</summary>
		void HistoryCapture(int frame);

		/// <summary>
		/// Puts the machine on a stored frame, restoring the emulator's own
		/// side-band with it. False when that frame is not one it can produce.
		/// </summary>
		bool HistoryRestore(int frame);

		/// <summary>Drops everything after this frame - what an input edit means.</summary>
		void HistoryInvalidate(int afterFrame);

		/// <summary>
		/// The history across sessions. Both answer "it did not throw", never
		/// "the states are there": a history of another machine is dropped
		/// rather than refused, because losing it costs replaying and never work.
		/// </summary>
		bool HistorySave(string path, string machineId);

		bool HistoryLoad(string path, string machineId);
	}
}
