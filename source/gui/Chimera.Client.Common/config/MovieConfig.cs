using System;

namespace Chimera.Client.Common
{
	public interface IMovieConfig
	{
		MovieEndAction MovieEndAction { get; }
		bool EnableBackupMovies { get; }
		int MovieCompressionLevel { get; }
		bool VBAStyleMovieLoadState { get; }

		/// <summary>
		/// What the state history may hold in memory, in megabytes. Beyond it the
		/// oldest stretches go to the project's cache directory rather than being
		/// thrown away, so this is where the line between memory and disk sits and
		/// not how much run you get (docs/state-manager.md).
		/// </summary>
		int GreenzoneBudgetMb { get; }

		/// <summary>
		/// What the spill file that line produces may weigh, in megabytes, or 0
		/// for no limit. This is the OTHER half: the memory budget is met by
		/// moving bytes to disk, so on its own it bounds nothing a disk notices.
		/// Past this the oldest stretches on disk are dropped and the room they
		/// held is given back, which costs replaying to reach a frame that used
		/// to be stored.
		/// </summary>
		int GreenzoneDiskBudgetMb { get; }
	}

	public class MovieConfig : IMovieConfig
	{
		public MovieEndAction MovieEndAction { get; set; } = MovieEndAction.Pause;
		public bool EnableBackupMovies { get; set; } = true;
		public int MovieCompressionLevel { get; set; } = 2;
		public bool VBAStyleMovieLoadState { get; set; }

		// Deltas made density affordable: a frame of xemu costs about two
		// megabytes here where a whole state was two hundred, so this holds far
		// more run than the same number used to. Four gigabytes is a machine with
		// sixteen giving a quarter of them to the run being edited - and it is a
		// CEILING, not a reservation: a history takes what the run needs and the
		// engine halves this on its own if the machine turns out not to have it.
		public int GreenzoneBudgetMb { get; set; } = 4 * 1024;

		// Ten gigabytes: a console greenzone spills tens of them in an afternoon
		// and used to keep every byte, and the whole point of this number is that
		// somebody can leave the machine running. A limit and not a reservation -
		// a short session never comes near it.
		public int GreenzoneDiskBudgetMb { get; set; } = 10 * 1024;

		/// <summary>
		/// The smallest memory budget that means anything.
		///
		/// The engine reads a budget of zero as "no history at all", which is a
		/// perfectly good thing for a headless tool to ask for and never what a
		/// person editing a number meant. A run needs room for one anchor and a
		/// few deltas before it can hold a single frame, so anything under this
		/// is read as this and the history stays a history.
		/// </summary>
		public const int MinimumBudgetMb = 64;

		/// <summary>
		/// The disk budget that goes with a memory budget: never smaller than it.
		///
		/// The two are not independent. Memory fills first and its overflow goes
		/// to disk, so a disk budget below the memory one is a disk that is
		/// already full the moment memory is - and every stretch pushed out of
		/// memory would be dropped on arrival, which is the same as having no
		/// spill at all while still paying to write it. Zero is the exception and
		/// means no limit, which is larger than any number rather than smaller.
		/// </summary>
		public static int DiskBudgetFor(int diskMb, int memoryMb)
			=> diskMb is 0 ? 0 : Math.Max(diskMb, memoryMb);
	}
}
