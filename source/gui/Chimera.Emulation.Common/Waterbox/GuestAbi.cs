namespace Chimera.Emulation.Common.Waterbox
{
	/// <summary>
	/// The contract between a <c>core.wbx</c> and the frontend that runs it: which
	/// symbols the guest must export, what they are called with, and what they mean.
	///
	/// This exists because cores are published separately from the frontend (see
	/// docs/core-manager.md). While Chimera pinned every core as a submodule, a core
	/// could never meet a Chimera that did not understand it; now it can, and the
	/// failure without a check would be a crash somewhere inside the sandbox rather
	/// than a sentence saying which of the two is too old.
	/// </summary>
	public static class GuestAbi
	{
		/// <summary>
		/// What a package built against today's guest kit declares. Bump this when the
		/// contract changes in a way an existing core.wbx cannot satisfy: a newly
		/// REQUIRED export, a changed signature, a changed meaning. Adding an OPTIONAL
		/// export is not a bump - the tooling groups work that way on purpose, and a
		/// core that lacks one is detected and does without it.
		/// </summary>
		public const int Current = 1;

		/// <summary>
		/// The oldest package this build still runs. Raising it retires every core
		/// published before that ABI, so it moves only when keeping the old path alive
		/// is worse than making people update their cores.
		/// </summary>
		public const int MinimumSupported = 1;

		/// <summary>
		/// What a package that declares no ABI at all is taken to be: everything
		/// published before the field existed, which is ABI 1 by definition.
		/// </summary>
		public const int Assumed = 1;

		/// <summary>
		/// Why <paramref name="abi"/> cannot run here, or null if it can. The message
		/// names the side that is behind, because "incompatible" on its own tells
		/// somebody holding two files nothing about which one to replace.
		/// </summary>
		public static string/*?*/ Refuse(int abi)
		{
			if (abi > Current)
			{
				return $"built for guest ABI {abi}; this Chimera understands up to {Current}. Update Chimera.";
			}
			if (abi < MinimumSupported)
			{
				return $"built for guest ABI {abi}; this Chimera no longer runs anything below {MinimumSupported}. Update the core.";
			}
			return null;
		}

		/// <summary>Whether a package declaring <paramref name="abi"/> can be loaded.</summary>
		public static bool IsSupported(int abi) => Refuse(abi) is null;
	}
}
