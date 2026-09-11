namespace Chimera.Emulation.Common
{
	/// <summary>
	/// Implemented by a core that CAN have its pictures drawn by a GPU outside
	/// the sandbox.
	///
	/// That is much faster than a software rasteriser and it is not
	/// deterministic: the GPU is outside the savestate, and two machines with
	/// different drivers do not produce the same pixels. So a movie records it,
	/// and a replay that desyncs somewhere else has something to point at.
	/// </summary>
	public interface IGpuRendered
	{
		/// <summary>
		/// What the driver calls itself when one drew this run, and empty when
		/// none did - which is every ordinary run, including one by a core that
		/// could have used a GPU and was not given one.
		/// </summary>
		string GpuRenderer { get; }

		/// <summary>
		/// Whether a savestate this core makes can be loaded into a session
		/// with a DIFFERENT GL context - a later run of the frontend, most of
		/// the time.
		///
		/// It is false by default and for a reason: a renderer holds its GL
		/// objects by the names a driver handed out, those names live in guest
		/// memory, and a state carries them into a session where they name
		/// nothing. The driver refuses every call using one and says so to
		/// nobody, so the machine runs and draws nothing. A core answers true
		/// only when its renderer NOTICES that the context is not the one its
		/// objects came from and builds them again.
		/// </summary>
		bool GpuStatesSurviveTheContext { get; }
	}
}
