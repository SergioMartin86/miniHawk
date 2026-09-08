namespace Chimera.Client.Common
{
	public interface IMovieConfig
	{
		MovieEndAction MovieEndAction { get; }
		bool EnableBackupMovies { get; }
		int MovieCompressionLevel { get; }
		bool VBAStyleMovieLoadState { get; }
		/// <summary>
		/// What the state history may hold in memory. Beyond it the oldest
		/// stretches go to the project's cache directory rather than being
		/// thrown away, so this is where the line between memory and disk sits
		/// and not how much run you get (docs/state-manager.md).
		/// </summary>
		int GreenzoneBudgetMb { get; }
	}

	public class MovieConfig : IMovieConfig
	{
		public MovieEndAction MovieEndAction { get; set; } = MovieEndAction.Pause;
		public bool EnableBackupMovies { get; set; } = true;
		public int MovieCompressionLevel { get; set; } = 2;
		public bool VBAStyleMovieLoadState { get; set; }

		// Deltas made density affordable: a frame of xemu costs about two
		// megabytes here where a whole state was two hundred, so this holds far
		// more run than the same number used to.
		public int GreenzoneBudgetMb { get; set; } = 2048;
	}
}
