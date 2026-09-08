using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

using Chimera.Common;
using Chimera.Common.IOExtensions;
using Chimera.Emulation.Common;

using Newtonsoft.Json;

namespace Chimera.Client.Common
{
	/// <summary>
	/// This class will manage savestates for TAStudio, with a very similar API as <see cref="ZwinderStateManager"/>.
	/// <br/>This manager intends to address a shortcoming of Zwinder, which is that it could not accept states out of order.
	/// Allowing states to be added out of order has two primary benefits:
	/// <br/>1. Users who load a branch, emulate forward a little, then rewind can still use the full buffer space.
	/// <br/>2. It becomes much easier to "thin out" states (keeping only a subset of them) for saving and to then use them again on load.
	/// <br/><br/>
	/// Out of order states also means we do not need separately allocated buffers for "current", "recent", etc.
	/// This gives us some more flexibility, but still there won't be any one-size-fits-all strategy for state management.
	/// <br/>For the initial implementation I will be using similar settings as Zwinder has, but this may change in the future.
	/// <br/>Additionally, this approach allows us to take the main goals of <see cref="ZwinderBuffer"/> into a state manager:
	/// <br/>1. No copies, ever. States are deposited directly to, and read directly from, one giant buffer (assuming no TempFile storage, which is not currently implemented).
	/// <br/>2. Support for arbitrary and changeable state sizes.
	/// </summary>
	public class PagedStateManager : IStateManager, IDisposable
	{
		public PagedSettings Settings { get; private set; }
		IStateManagerSettings IStateManager.Settings => Settings;

		[JsonConverter(typeof(NoConverter))]
		public class PagedSettings : IStateManagerSettings
		{
			// Instead of the user giving a set of memory limits, the user will give just one.
			// That will be the limit for ALL managed states combined.
			// We'll have three "groups" of states:
			// 1) "new": This is similar to Zwinder's "current". It will hold the most recent (highest frame number) states.
			// 2) "mid": This is similar to Zwinder's "recent". It will hold states older than "new".
			// 3) "old": This is similar to Zwinder's "ancient". It will hold states that should never be thrown out for being "old" (low frame number relative to the ones in "new").
			// Instead of having a group like Zwinder's "gap" we will take from the "mid" group.

			// The defaults given here are largely arbitrary. There is no one-size-fits-all and so we make no attempt to fine-tune the defaults.
			// Users should be encouraged to update the settings if the defaults don't work well for their use case.
			// For best results, FB (FramesBetween) for mid should be an integer multiple of FB for new. Same for old to mid.

			// Ideally the default would be based on actual available RAM.
			// But, auto-save is on by default so having more would make that potentially very annoying.
			[DisplayName("Auto Memory Limit")]
			[Description("Size the state pool to this machine and this system when a movie loads: a quarter of the RAM that is actually free (never less than 1GB, never more than 8GB), and no more than the states can use - a small system stops reserving space it cannot fill, a big one gets what the machine can spare. When off, 'Memory Limit (MB)' is used as-is.")]
			public bool AutoMemoryLimit { get; set; } = true;

			[DisplayName("Old States On Disk")]
			[Description("Keep the old anchor states - the ones spread across the whole movie that never decay - in compressed temp files instead of the memory pool. They are read only when navigating far back, a disk read there is invisible next to re-emulating the gap, and every page they would have held goes to the states you are actually working with. Applies to systems with big states (4MB and up); small states stay in the pool either way.")]
			public bool OldStatesOnDisk { get; set; } = true;

			[DisplayName("Old States Disk Limit (MB)")]
			[Description("How much disk the old anchor states may take, compressed, before the spine thins itself: when the limit is crossed, every other unreserved anchor is evicted and the anchor spacing doubles, so the whole movie stays covered at half the density instead of the disk filling. Frame zero and reserved states (markers, branches) always survive.")]
			[Range(256, 262144)]
			[TypeConverter(typeof(ConstrainedIntConverter))]
			public int OldStatesDiskLimitMB { get; set; } = 8192;

			[DisplayName("Memory Limit (MB)")]
			[Description("The amount of RAM to use for savestates when 'Auto Memory Limit' is off. Bigger values gives more states but makes saving take longer.")]
			[Range(1, 32768)]
			public int TotalMemoryLimitMB { get; set; } = 1024;

			[DisplayName("Frames Between New States")]
			[Description("How many frames from one state to the next, for the newest (highst-frame) states.")]
			[Range(1, int.MaxValue)]
			public int FramesBetweenNewStates { get; set; } = 4;
			[DisplayName("Frames Between Middle States")]
			[Description("How many frames from one state to the next, for the middle states. For best results, this should be a whole number multiple of 'Frames Between New States'.")]
			[Range(1, int.MaxValue)]
			public int FramesBetweenMidStates { get; set; } = 20;
			[DisplayName("Frames Between Old States")]
			[Description("How many frames from one state to the next, for old states. TAStudio will try to keep old states for the entire duration of the movie. For best results, this should be a whole number multiple of 'Frames Between Middle States'.")]
			[Range(1, int.MaxValue)]
			public int FramesBetweenOldStates { get; set; } = 400;

			/// <summary>
			/// What is the ratio for the number of states in the "new" group vs. in the "mid" group?
			/// The "old" group will use whatever it uses and the leftover is given to "new" and "mid" according to this ratio.
			/// </summary>
			[DisplayName("New To Middle Ratio")]
			[Description("How many states to consider as 'new' states compared to 'middle' states. A larger value will result in more states using 'Frames Between New States' and a smaller value will have more states using 'Frames Between Middle States'.")]
			[Range(0.0, double.MaxValue)]
			public double NewToMidRatio { get; set; } = 2.0;

			[DisplayName("Frames Between Saved States")]
			[Description("How many frames from one state to the next, when saving the greenzone (kept in this machine's cache, not beside the project). Higher values will result in faster saves.")]
			[Range(1, int.MaxValue)]
			public int FramesBetweenSavedStates { get; set; } = 100;

			[DisplayName("Save Marker States")]
			[Description("If true, states for markers will be saved regardless of what 'Frames Between Saved States' says.")]
			public bool ForceSaveMarkerStates { get; set; } = false;


			public PagedSettings() { }
			public PagedSettings(PagedSettings other)
			{
				AutoMemoryLimit = other.AutoMemoryLimit;
				OldStatesOnDisk = other.OldStatesOnDisk;
				OldStatesDiskLimitMB = other.OldStatesDiskLimitMB;
				TotalMemoryLimitMB = other.TotalMemoryLimitMB;
				NewToMidRatio = other.NewToMidRatio;

				FramesBetweenNewStates = other.FramesBetweenNewStates;
				FramesBetweenMidStates = other.FramesBetweenMidStates;
				FramesBetweenOldStates = other.FramesBetweenOldStates;
				FramesBetweenSavedStates = other.FramesBetweenSavedStates;

				ForceSaveMarkerStates = other.ForceSaveMarkerStates;
			}

			public IStateManager CreateManager(Func<int, bool> reserveCallback)
			{
				return new PagedStateManager(this, reserveCallback);
			}

			public IStateManagerSettings Clone() => new PagedSettings(this);
		}

		public int Count => _states.Count;

		public int Last => _states.Max.Frame;

		private const int PAGE_SIZE = 4096;
		private const int PAGE_DATA_SIZE = PAGE_SIZE - 4; // PAGE_SIZE minus metadata
		private MemoryBlock _buffer;
		private int _firstFree;

		private enum StateGroup
		{
			New,
			Mid,
			Old,
			None,
		}

		private struct StateInfo: IComparable<StateInfo>
		{
			public int FirstPage;
			public int LastPage;
			public int Size;
			public int Frame = -1;

			public StateInfo() { }

			// Use only to aid searching the list!
			public StateInfo(int f) { Frame = f; }

			public int CompareTo(StateInfo other) => Frame.CompareTo(other.Frame);

			public Stream MakeReadStream(PagedStateManager manager)
			{
				return FirstPage < 0
					? manager.ReadOldFromDisk(Frame)
					: new PagedStream(this, manager, true);
			}
		}
		/* Our collection of states needs to perform well in all of these tasks:
		 * 1) Inserting states at any point. This will most commonly be at the end, but not always.
		 * 2) Removing states at any point. This will most commonly be at the start, but not always.
		 * 3) Finding the state on a given frame.
		 * 4) Finding the closest state after a given frame.
		 * 5) Finding the closest state before a given frame.
		 *
		 * A note on the implementation: SortedSet provides a method GetViewBetween and this is how we do (4) and (5).
		 * One would expect this to be fast, but it is actually O(n) where n is the number of elements in the view.
		 * To avoid this ludicrous performance shortcoming, calls to GetViewBetween should provide as narrow a range as possible.
		 * This is fixed in newer versions of .net. If we later require a recent version of .net we can simplify our code here.
		 */
		private readonly SortedSet<StateInfo> _states = new();
		private readonly SortedSet<StateInfo> _midStates = new();
		private readonly SortedSet<StateInfo> _newStates = new();

		private readonly Func<int, bool> _reserveCallback;

		// Old anchor states, moved out of the page pool: zstd-compressed, one
		// temp file each, keyed by frame. A state living here is marked in
		// _states with FirstPage = -1 - the sorted-set logic neither knows nor
		// cares where the bytes are.
		private readonly TempFileStateDictionary _oldOnDisk = new();
		private readonly Dictionary<int, long> _oldDiskSize = new();
		private long _oldDiskBytes;
		// Doubles each time the disk cache thins: ShouldKeepForOld measures gaps
		// against FramesBetweenOldStates * this, so the spine STAYS at its wider
		// spacing instead of refilling the gaps and thinning again forever.
		private int _oldSpacingMultiplier = 1;
		private readonly Chimera.Common.Zstd _zstd = new();

		private bool _bufferIsFull = false;
		private int _newPagesUsed = 0;
		private int _midPagesUsed = 0;

		private int _lastForceCapture = -1;

		private class PagedStream : Stream
		{
			private readonly StateInfo _info;

			private readonly PagedStateManager _manager;
			private readonly MemoryBlock _parentBlock;
			private readonly Stream _blockStream;
			private int _nextBlockId;
			private long _endOfPage;

			private int _bytesSeen;

			public PagedStream(StateInfo stateInfo, PagedStateManager manager, bool forRead)
			{
				_info = stateInfo;
				_manager = manager;
				_parentBlock = manager._buffer;
				_blockStream = _parentBlock.GetStream(_parentBlock.Start, _parentBlock.Size, !forRead);

				_blockStream.Position = (long)_info.FirstPage * PAGE_SIZE;
				_endOfPage = _blockStream.Position + PAGE_SIZE;
				_nextBlockId = Marshal.ReadInt32((IntPtr)(_parentBlock.Start + (ulong)_blockStream.Position));
				_blockStream.Position += 4;

				_bytesSeen = 0;
			}

			public override bool CanRead => true;

			public override bool CanSeek => false;

			public override bool CanWrite => true;

			public override long Length => _info.Size;

			public override int Read(byte[] buffer, int offset, int count)
			{
				if (count + _bytesSeen > _info.Size)
					count = _info.Size - _bytesSeen;
				if (count == 0) return 0;

				int readCount = 0;
				while (_blockStream.Position + count > _endOfPage)
				{
					int bytesToEndOfPage = (int)(_endOfPage - _blockStream.Position);
					if (_blockStream.Read(buffer, offset, bytesToEndOfPage) != bytesToEndOfPage)
						throw new Exception("Unexpected end of buffer in PagedStreamReader.");
					_bytesSeen += bytesToEndOfPage;

					_blockStream.Position = (long)_nextBlockId * PAGE_SIZE;
					_endOfPage = _blockStream.Position + PAGE_SIZE;
					_nextBlockId = Marshal.ReadInt32((IntPtr)(_parentBlock.Start + (ulong)_blockStream.Position));
					_blockStream.Position += 4;

					readCount += bytesToEndOfPage;
					offset += bytesToEndOfPage;
					count -= bytesToEndOfPage;
				}

				int lastReadCount = _blockStream.Read(buffer, offset, count);
				_bytesSeen += lastReadCount;
				return readCount + lastReadCount;
			}

			public override long Position
			{
				get => _bytesSeen;
				set => throw new NotImplementedException();
			}

			public override void Flush() { }
			public override long Seek(long offset, SeekOrigin origin) => throw new NotImplementedException();
			public override void SetLength(long value) => throw new NotImplementedException();
			public override void Write(byte[] buffer, int offset, int count)
			{
				while (_blockStream.Position + count > _endOfPage)
				{
					int bytesToEndOfPage = (int)(_endOfPage - _blockStream.Position);
					_blockStream.Write(buffer, offset, bytesToEndOfPage);
					_bytesSeen += bytesToEndOfPage;

					if (_nextBlockId == -1)
					{
						_nextBlockId = _manager.FreePage(_info.Frame);
						// Update the linked list.
						Marshal.WriteInt32((IntPtr)((long)_parentBlock.Start + _endOfPage - PAGE_SIZE), _nextBlockId);
					}

					_blockStream.Position = (long)_nextBlockId * PAGE_SIZE;
					_endOfPage = _blockStream.Position + PAGE_SIZE;
					_nextBlockId = Marshal.ReadInt32((IntPtr)(_parentBlock.Start + (ulong)_blockStream.Position));
					_blockStream.Position += 4;

					offset += bytesToEndOfPage;
					count -= bytesToEndOfPage;
				}

				_blockStream.Write(buffer, offset, count);
				_bytesSeen += count;
			}

			public struct FinalizedInfo
			{
				public int LastPage;
				public int NextPage;
				public int BytesWritten;
			}
			public FinalizedInfo FinishWrite()
			{
				IntPtr ptr = (IntPtr)((long)_parentBlock.Start + _endOfPage - PAGE_SIZE);
				int nextPage = Marshal.ReadInt32(ptr);
				Marshal.WriteInt32(ptr, -1);

				return new()
				{
					LastPage = (int)(_endOfPage / PAGE_SIZE) - 1,
					BytesWritten = _bytesSeen,
					NextPage = nextPage,
				};
			}
		}

		public PagedStateManager(PagedSettings settings, Func<int, bool> reserveCallback)
		{
			Settings = settings;
			_reserveCallback = reserveCallback;
			// The pool is allocated at Engage, not here: the frame-zero state's
			// size is what an auto limit scales by, and committing a gigabyte
			// before knowing whether the system needs a tenth of it helps nobody.
		}

		/// <summary>
		/// Allocates the page pool once, sized for this machine and (when the
		/// limit is automatic) this system's states. If the machine refuses the
		/// allocation the pool halves until it fits: a smaller pool is a sparser
		/// history, where the refusal would have been a crash.
		/// </summary>
		private void EnsureAllocated(long stateSizeHint)
		{
			if (_buffer != null) return;

			// Auto applies to BIG states only: a small system's states already fit
			// thousands deep in the configured limit, and that limit keeps meaning
			// exactly what it says for them.
			int limitMB = Settings.AutoMemoryLimit && stateSizeHint >= StateBudget.BigStateBytes
				? StateBudget.PoolMB(stateSizeHint, targetStates: 4096, floorMB: 1024, ceilingMB: 8192)
				: Settings.TotalMemoryLimitMB;

			int pageCount = limitMB * 1024 / 4;
			while (true)
			{
				try
				{
					_buffer = new MemoryBlock((ulong)pageCount * PAGE_SIZE);
					_buffer.Protect(_buffer.Start, _buffer.Size, MemoryBlock.Protection.RW);
					break;
				}
				catch (Exception) when (pageCount > 64 * 1024 / 4)
				{
					// the machine said no; ask for half. The history gets sparser,
					// the session keeps going.
					pageCount /= 2;
					Console.WriteLine($"TAStudio state pool: allocation refused, retrying at {pageCount * 4 / 1024}MB");
				}
			}
			if (Settings.AutoMemoryLimit && stateSizeHint > 0)
			{
				Console.WriteLine($"TAStudio state pool: auto limit {pageCount * 4 / 1024}MB for {stateSizeHint / 1024}KB states");
			}

			// Set up the pages. This is a single link list.
			// The links go like this:
			// 1) Within a state, each page points to the next page for that state.
			// 2) The last page for a state points to an invalid page.
			// 3) The free pages (which they all are, initially) are all linked together, order is arbitrary.
			// 4) The last free page points to an invalid page.

			// When writing a state, we always begin at the first free page from the free list.
			// When we invalidate a state, we put its pages at the front of the free list.

			for (int i = 0; i < pageCount - 1; i++)
			{
				Marshal.WriteInt32((IntPtr)((long)_buffer.Start + (long)i * PAGE_SIZE), i + 1);
			}
			Marshal.WriteInt32((IntPtr)((long)_buffer.Start + (long)(pageCount - 1) * PAGE_SIZE), -1);
			_firstFree = 0;
		}

		/// <summary>the anchor spacing as the disk budget has widened it</summary>
		private int OldSpacing => Settings.FramesBetweenOldStates * _oldSpacingMultiplier;

		/// <summary>a state worth keeping as old, whose bytes should live on disk rather than in the pool</summary>
		private bool ShouldDiskOld(StateInfo state)
			=> Settings.OldStatesOnDisk && state.Size >= StateBudget.BigStateBytes;

		/// <summary>
		/// Moves a state's bytes out of the page pool into a compressed temp
		/// file, leaving a FirstPage = -1 marker in its place. The caller owns
		/// the freed page chain (the same one a plain kick would have returned).
		/// </summary>
		private void MoveOldToDisk(StateInfo state)
		{
			var compressed = new MemoryStream();
			using (var pooled = new PagedStream(state, this, true))
			using (var compressor = _zstd.CreateZstdCompressionStream(compressed, 1))
			{
				pooled.CopyTo(compressor);
			}

			// Over budget? Thin the spine first: half the anchors go, the
			// spacing doubles, and the whole movie stays covered - sparser,
			// the way the RAM side degrades, instead of the disk filling.
			long limitBytes = (long)Settings.OldStatesDiskLimitMB * 1024 * 1024;
			if (_oldDiskBytes + compressed.Length > limitBytes) ThinOldOnDisk();

			// Still over after thinning, and nobody reserved this frame? Then
			// this anchor is the one that goes: the survivors already cover the
			// gap at the widened spacing. Reserved states are written anyway -
			// a marker's state must not vanish - so the overshoot is bounded by
			// what the user explicitly asked to keep.
			if (_oldDiskBytes + compressed.Length > limitBytes && !_reserveCallback(state.Frame))
			{
				_states.Remove(state);
				return;
			}

			try
			{
				compressed.Position = 0;
				_oldOnDisk.SetState(state.Frame, compressed);
			}
			catch (IOException)
			{
				// the DISK said no (full, most likely): degrade exactly like the
				// budget - thin, then retry once, then let the anchor go
				ThinOldOnDisk();
				try
				{
					compressed.Position = 0;
					_oldOnDisk.SetState(state.Frame, compressed);
				}
				catch (IOException)
				{
					Console.WriteLine("TAStudio old-state cache: disk write failed twice; dropping the anchor");
					_states.Remove(state);
					return;
				}
			}
			_oldDiskBytes += compressed.Length;
			_oldDiskSize[state.Frame] = compressed.Length;
			_states.Remove(state);
			_states.Add(new StateInfo { Frame = state.Frame, FirstPage = -1, LastPage = -1, Size = state.Size });
		}

		/// <summary>
		/// Evicts every other unreserved disk anchor and doubles the spacing the
		/// spine maintains from here on. Frame zero and reserved frames always
		/// survive; coverage of the whole movie is kept, at half the density.
		/// </summary>
		private void ThinOldOnDisk()
		{
			List<StateInfo> victims = new();
			bool keep = true;
			foreach (StateInfo state in _states)
			{
				if (state.FirstPage >= 0 || state.Frame == 0 || _reserveCallback(state.Frame)) continue;
				keep = !keep;
				if (!keep) victims.Add(state);
			}
			foreach (StateInfo victim in victims) RemoveState(victim);
			if (_oldSpacingMultiplier < 1 << 20) _oldSpacingMultiplier *= 2;
			Console.WriteLine($"TAStudio old-state cache: thinned to {_oldDiskBytes / (1024 * 1024)}MB, anchor spacing now {OldSpacing} frames");
		}

		/// <summary>the way back: the whole state, decompressed, as a stream with a Length</summary>
		private Stream ReadOldFromDisk(int frame)
		{
			var plain = new MemoryStream();
			using (var decompressor = _zstd.CreateZstdDecompressionStream(new MemoryStream(_oldOnDisk[frame])))
			{
				decompressor.CopyTo(plain);
			}
			plain.Position = 0;
			return plain;
		}

		/// <summary>
		/// Will removing the state on this frame leave us with a gap larger than <see cref="PagedSettings.FramesBetweenOldStates"/>?
		/// </summary>
		private bool ShouldKeepForOld(int frame)
		{
			Debug.Assert(_states.Contains(new(frame)), "Do not ask if we should keep a non-existent state.");

			if (frame == 0) return true; // must keep state on frame 0
			if (_states.Max.Frame == frame)
			{
				// There is no future state, so there is no gap between states for us to measure.
				// We're probably unreserving for a marker removal. Allow it to be removed, since that's simpler.
				return false;
			}

			StateInfo nextState = _states.GetViewBetween(new(frame + 1), new(frame + OldSpacing)).Min;
			if (nextState.Frame == 0) return true;
			StateInfo previousState = _states.GetViewBetween(new(frame - OldSpacing), new(frame - 1)).Max;

			return nextState.Frame - previousState.Frame > OldSpacing;
		}

		/// <summary>
		/// Deletes a state from the buffer and returns the first newly-freed page.
		/// This should be used when saving a state and running out of already free pages.
		/// </summary>
		private int FreePage(int frame)
		{
			Debug.Assert(!_states.Contains(new(frame)), "Invalid use of FreePage. The frame we are capturing a state for should not already have a state.");

			_bufferIsFull = true;

			while (true)
			{
				// A very special case: We have no mid or new states.
				if (_newStates.Count == 0 && _midStates.Count == 0)
				{
					// The second POOLED state: disk states hold no pages, and the
					// earliest pooled state is spared the way the original spared
					// the earliest state.
					StateInfo stateToKick = default;
					int pooledSeen = 0;
					foreach (StateInfo candidate in _states)
					{
						if (candidate.FirstPage < 0) continue;
						pooledSeen++;
						if (pooledSeen == 2) { stateToKick = candidate; break; }
					}
					if (pooledSeen < 2) throw new Exception("Unable to capture a single state. This probably means your Memory Limit setting is too low.");
					// A big old anchor still has somewhere better to go than
					// oblivion: the disk cache. This also closes a hole older
					// than the cache - the last-resort kick used to throw away
					// even RESERVED states when the pool was all-old.
					if (ShouldDiskOld(stateToKick))
					{
						MoveOldToDisk(stateToKick);
						return stateToKick.FirstPage;
					}
					_states.Remove(stateToKick);
					return stateToKick.FirstPage;
				}

				// Kick from new if the ratio limit is met.
				if (_newStates.Count != 0 &&
					(_midPagesUsed == 0 || (double)_newPagesUsed / _midPagesUsed > Settings.NewToMidRatio))
				{
					StateInfo stateToKick = _newStates.Min;
					int pages = (stateToKick.Size + PAGE_DATA_SIZE - 1) / PAGE_DATA_SIZE;
					_newPagesUsed -= pages;
					_newStates.Remove(stateToKick);

					// Kicking a state from new means checking if it belongs in mid.
					StateInfo newestOlderState = _states.GetViewBetween(new(stateToKick.Frame - Settings.FramesBetweenMidStates), new(stateToKick.Frame - 1)).Max;
					bool recategorizeAsMid = stateToKick.Frame - newestOlderState.Frame >= Settings.FramesBetweenMidStates;
					// If it does, we re-categorize it and try kicking the next one from new.
					if (recategorizeAsMid)
					{
						_midPagesUsed += pages;
						_midStates.Add(stateToKick);
					}
					else if (_reserveCallback(stateToKick.Frame))
					{
						// Recategorize as old - and a big state's bytes go to
						// disk, its pages answering the very request that got
						// it demoted.
						if (ShouldDiskOld(stateToKick))
						{
							MoveOldToDisk(stateToKick);
							return stateToKick.FirstPage;
						}
					}
					else
					{
						_states.Remove(stateToKick);
						return stateToKick.FirstPage;
					}
				}
				else
				{
					// At this point, we know _midStates.Count != 0 and we are kicking from mid.
					// Which one to kick? This depends.
					// 1) If there are mid states past the one being captured, we take the oldest of those.
					//		This is so we can capture states while re-playing old sections.
					// 2) Otherwise, we kick the oldest mid state.

					// Slow (see comment on _states)
					//StateInfo oldestNewerState = _midStates.GetViewBetween(new(frame), new(int.MaxValue)).Min;
					//StateInfo stateToKick = oldestNewerState.Frame != 0 ? oldestNewerState : _midStates.Min;
					// Fast
					int maxMidFrame = _midStates.Max.Frame;
					StateInfo stateToKick;
					if (frame > maxMidFrame)
						stateToKick = _midStates.Min;
					else
					{
						stateToKick = new(0);
						int checkFrame = frame;
						while (stateToKick.Frame == 0)
						{
							stateToKick = _midStates.GetViewBetween(new(checkFrame), new(checkFrame + Settings.FramesBetweenOldStates)).Min;
							checkFrame += Settings.FramesBetweenOldStates;
						}
					}

					// Kicking a state means checking if it belongs in old.
					bool recategorizeAsOld = ShouldKeepForOld(stateToKick.Frame) || _reserveCallback(stateToKick.Frame);
					int pages = (stateToKick.Size + PAGE_DATA_SIZE - 1) / PAGE_DATA_SIZE;
					_midPagesUsed -= pages;
					_midStates.Remove(stateToKick);
					if (!recategorizeAsOld)
					{
						_states.Remove(stateToKick);
						return stateToKick.FirstPage;
					}
					if (ShouldDiskOld(stateToKick))
					{
						MoveOldToDisk(stateToKick);
						return stateToKick.FirstPage;
					}
				}
			}
		}

		private void InternalCapture(int frame, IStatable source, StateGroup destinationGroup)
		{
			// engaged managers arrive here allocated; a stray early capture
			// falls back to the manual limit rather than dereferencing nothing
			EnsureAllocated(0);
			if (_firstFree == -1) _firstFree = FreePage(frame);
			StateInfo newState = new StateInfo()
			{
				Frame = frame,
				FirstPage = _firstFree,
			};

			using PagedStream stream = new(newState, this, false);
			using BinaryWriter bw = new(stream);
			source.SaveStateBinary(bw);

			PagedStream.FinalizedInfo finalInfo = stream.FinishWrite();
			newState.LastPage = finalInfo.LastPage;
			newState.Size = finalInfo.BytesWritten;
			_firstFree = finalInfo.NextPage;
			if (_firstFree == -1)
				_bufferIsFull = true;

			_states.Add(newState);
			int pages = (newState.Size + PAGE_DATA_SIZE - 1) / PAGE_DATA_SIZE;
			if (destinationGroup == StateGroup.Mid)
			{
				_midStates.Add(newState);
				_midPagesUsed += pages;
			}
			else if (destinationGroup == StateGroup.New)
			{
				_newStates.Add(newState);
				_newPagesUsed += pages;
			}
		}

		public void Capture(int frame, IStatable source, bool force = false)
		{
			Debug.Assert(_states.Contains(new(0)), "State manager cannot be used until engaged.");

			if (HasState(frame)) return;

			if (force)
			{
				if (HasState(_lastForceCapture))
				{
					StateInfo state = _states.GetViewBetween(new(_lastForceCapture), new(_lastForceCapture)).Min;
					if (!_reserveCallback(_lastForceCapture) && !ShouldKeepForOld(_lastForceCapture))
						RemoveState(state);
				}
				_lastForceCapture = -1; // This will get set if the frame is actually captured because of force.
			}

			StateGroup group = StateGroup.None;
			if (_reserveCallback(frame))
			{
				group = StateGroup.Old;
			}
			else if (source.AvoidRewind)
			{
				// Zwinder did this, so I will too. I'm not sure it's a good idea but maybe it is.
				// (do nothing)
			}
			else if (!_bufferIsFull || _newStates.Count == 0 || frame > _newStates.Min.Frame)
			{
				int max = frame - 1;
				int min = frame - Settings.FramesBetweenNewStates;
				int newestOlderState;

				// Special case: If the buffer is entirely full of old states, we don't attempt to capture into new.
				if (_bufferIsFull && _midStates.Count == 0 && _newStates.Count < 2)
				{
					min = frame - OldSpacing;
					// Now, a quirk: Once we get here ("full of old states") for the first time, there should actually be one new state. Because our last capture went into new, recategorized a new to old, and deleted another new.
					// In this case, we kinda want to ignore that new state.
					if (_newStates.Count == 1)
					{
						int stateToIgnore = _states.GetViewBetween(new(min), new(max)).Max.Frame;
						max = stateToIgnore - 1;
					}
					if (min <= max) newestOlderState = _states.GetViewBetween(new(min), new(max)).Max.Frame;
					else newestOlderState = 0;

					if (frame - newestOlderState >= OldSpacing)
						group = StateGroup.Old;
				}
				else
				{
					// Don't consider non-new states when looking at the gap. (unless we have no new states)
					// This keeps the gaps regular even when some frames are temporarily force captured.
					if (_newStates.Count != 0)
						newestOlderState = _newStates.GetViewBetween(new(min), new(max)).Max.Frame;
					else
						newestOlderState = _states.GetViewBetween(new(min), new(max)).Max.Frame;

					if (frame - newestOlderState >= Settings.FramesBetweenNewStates)
						group = StateGroup.New;
				}
			}
			else
			{
				int max = frame - 1;
				int min = frame - Settings.FramesBetweenMidStates;
				StateInfo newestOlderState = _states.GetViewBetween(new(min), new(max)).Max;
				if (frame - newestOlderState.Frame >= Settings.FramesBetweenMidStates)
					group = StateGroup.Mid;
			}

			if (group != StateGroup.None)
				InternalCapture(frame, source, group);
			else if (force)
			{
				_lastForceCapture = frame;
				InternalCapture(frame, source, StateGroup.Old);
			}
		}

		public void Clear() => InvalidateAfter(0);

		public void Dispose()
		{
			_buffer?.Dispose();
			_oldOnDisk.Dispose();
			_zstd.Dispose();
		}

		public void Engage(byte[] frameZeroState)
		{
			EnsureAllocated(frameZeroState.Length);
			InternalCapture(0, new StatableArray(frameZeroState), StateGroup.Old);
		}

		public KeyValuePair<int, Stream> GetStateClosestToFrame(int frame)
		{
			Debug.Assert(_states.Contains(new(0)), "State manager cannot be used until engaged.");
			if (frame < 0)
				throw new ArgumentOutOfRangeException(nameof(frame));

			StateInfo info = _states.GetViewBetween(new(0), new(frame)).Max;
			return new(info.Frame, info.MakeReadStream(this));
		}

		public bool HasState(int frame) => _states.Contains(new(frame));

		private void RemoveState(StateInfo state)
		{
			Debug.Assert(_states.Contains(state), "Do not attempt to remove a non-existent state.");

			if (state.FirstPage < 0)
			{
				// the bytes live on disk; there are no pages to hand back
				_states.Remove(state);
				_oldOnDisk.Remove(state.Frame);
				if (_oldDiskSize.TryGetValue(state.Frame, out long bytes))
				{
					_oldDiskBytes -= bytes;
					_oldDiskSize.Remove(state.Frame);
				}
				return;
			}

			ulong position = _buffer.Start + ((ulong)state.LastPage * PAGE_SIZE);
			Marshal.WriteInt32((IntPtr)position, _firstFree);
			_firstFree = state.FirstPage;

			int pages = (state.Size + PAGE_DATA_SIZE - 1) / PAGE_DATA_SIZE;
			_states.Remove(state);
			if (_newStates.Remove(state)) _newPagesUsed -= pages;
			else if (_midStates.Remove(state)) _midPagesUsed -= pages;

			_bufferIsFull = false;
		}
		public bool InvalidateAfter(int frame)
		{
			// must keep state on frame 0
			if (frame < 0)
				throw new ArgumentOutOfRangeException(nameof(frame));

			int oldStateCount = Count;

			StateInfo newestState = _states.Max;
			while (newestState.Frame > frame)
			{
				RemoveState(newestState);
				newestState = _states.Max;
			}
			return Count < oldStateCount;
		}

		public void Unreserve(int frame)
		{
			// We need to get the real state info out of the set.
			StateInfo state = _states.GetViewBetween(new(frame), new(frame)).Min;
			if (state.Frame == 0) return;

			// Remove the state if it's an old state we don't need.
			if (!_newStates.Contains(state) && !_midStates.Contains(state) && !ShouldKeepForOld(frame))
			{
				RemoveState(state);
			}
		}

		public IStateManager UpdateSettings(IStateManagerSettings settings, bool keepOldStates = false)
		{
			if (settings is not PagedSettings pSettings
				|| pSettings.TotalMemoryLimitMB != this.Settings.TotalMemoryLimitMB
				|| pSettings.AutoMemoryLimit != this.Settings.AutoMemoryLimit
				|| pSettings.OldStatesOnDisk != this.Settings.OldStatesOnDisk)
			{
				IStateManager newManager = settings.CreateManager(_reserveCallback);
				newManager.Engage(GetStateClosestToFrame(0).Value.ReadAllBytes());
				if (keepOldStates) foreach (StateInfo state in _states)
				{
					Stream s = state.MakeReadStream(this);
					newManager.Capture(state.Frame, new StatableStream(s, (int)s.Length));
				}

				Dispose();
				return newManager;
			}
			else
			{
				bool recaptureOld = pSettings.FramesBetweenOldStates > this.Settings.FramesBetweenOldStates;
				this.Settings = pSettings;
				if (recaptureOld)
				{
					List<StateInfo> oldStates = _states
						.Where(si => !_newStates.Contains(si) && !_midStates.Contains(si))
						.ToList();
					foreach (StateInfo state in oldStates)
					{
						if (!_reserveCallback(state.Frame) && !ShouldKeepForOld(state.Frame))
							RemoveState(state);
					}
				}
				return this;
			}
		}
		public void SaveStateHistory(BinaryWriter bw)
		{
			bw.Write((byte)2); // version

			List<StateInfo> statesToSave = new();
			foreach (StateInfo state in _states)
			{
				statesToSave.Add(state);
				if (statesToSave.Count > 2)
				{
					int diff = statesToSave[statesToSave.Count - 1].Frame - statesToSave[statesToSave.Count - 3].Frame;
					bool shouldSave = diff > Settings.FramesBetweenSavedStates;
					if (!shouldSave && Settings.ForceSaveMarkerStates)
					{
						shouldSave = _reserveCallback(statesToSave[statesToSave.Count - 2].Frame);
					}

					if (!shouldSave) statesToSave.RemoveAt(statesToSave.Count - 2);
				}
			}
			if (statesToSave.Count > 1)
			{
				int diff = statesToSave[statesToSave.Count - 1].Frame - statesToSave[statesToSave.Count - 2].Frame;
				bool shouldSave = diff >= Settings.FramesBetweenSavedStates ||
					(Settings.ForceSaveMarkerStates && _reserveCallback(statesToSave[statesToSave.Count - 1].Frame));

				if (!shouldSave) statesToSave.RemoveAt(statesToSave.Count - 1);
			}

			statesToSave.RemoveAt(0); // No point keeping this one, the movie defines it elsewhere.

			bw.Write(statesToSave.Count);
			foreach (StateInfo state in statesToSave)
			{
				bw.Write(state.Size);
				bw.Write(state.Frame);
				state.MakeReadStream(this).CopyTo(bw.BaseStream);
			}
		}

		public void LoadStateHistory(BinaryReader br)
		{
			int version = br.ReadByte();
			if (version < 2) return; // Not a PagedStateManager.

			// Fake engage, so we can use the capture logic.
			bool isEngaged = _states.Contains(new(0));
			if (!isEngaged) Engage([ 0 ]);

			int stateCount = br.ReadInt32();
			for (int i = 0; i < stateCount; i++)
			{
				int size = br.ReadInt32();
				int frame = br.ReadInt32();
				Capture(frame, new StatableStream(br.BaseStream, size));
			}

			// Undo fake engage
			if (!isEngaged) RemoveState(_states.Min);
		}

		private class StatableArray : IStatable
		{
			public bool AvoidRewind => throw new NotImplementedException();
			public void LoadStateBinary(BinaryReader reader) => throw new NotImplementedException();

			private byte[] _array;
			public StatableArray(byte[] array) => _array = array;
			public void SaveStateBinary(BinaryWriter writer) => writer.Write(_array);
		}
	}
}
