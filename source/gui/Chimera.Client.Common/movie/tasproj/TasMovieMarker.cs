using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Chimera.Common.CollectionExtensions;

namespace Chimera.Client.Common
{
	/// <summary>
	/// The three markers a run always has, whether or not anyone placed them.
	/// They are derived from the movie rather than authored, so they cannot be
	/// moved, renamed or removed, and they are never written to the .chimeraProject -
	/// on load they are worked out again, which is the only way they can be
	/// trusted to still be true.
	/// </summary>
	public enum MarkerPermanence
	{
		/// <summary>an ordinary marker, placed by the user</summary>
		None = 0,

		/// <summary>frame zero, where the run begins</summary>
		RunStart,

		/// <summary>the last frame anything is pressed on</summary>
		LastInput,

		/// <summary>the last frame of the movie</summary>
		RunEnd,
	}

	/// <summary>
	/// Represents a TasMovie Marker
	/// A marker is a tagged frame with a message
	/// </summary>
	public class TasMovieMarker
	{
		public TasMovieMarker(int frame, string message = "")
		{
			Frame = frame;
			Message = message;
		}

		public TasMovieMarker(int frame, string message, MarkerPermanence permanence)
		{
			Frame = frame;
			Message = message;
			Permanence = permanence;
		}

		/// <summary>
		/// Which of the run's own markers this is, or <see cref="MarkerPermanence.None"/>
		/// for one the user placed.
		/// </summary>
		public MarkerPermanence Permanence { get; } = MarkerPermanence.None;

		/// <summary>
		/// True for a marker the run owns: it may not be moved, renamed or removed,
		/// and it follows the movie instead.
		/// </summary>
		public bool IsPermanent => Permanence != MarkerPermanence.None;

		/// <summary>
		/// Where a permanent marker has moved to. Not <see cref="ShiftTo"/>: that
		/// offsets a marker riding along with an insert or a delete, and these are
		/// told outright where they now belong.
		/// </summary>
		internal void MoveTo(int frame)
		{
			Frame = frame;
		}

		/// <summary>
		/// Initializes a new instance of the <see cref="TasMovieMarker"/> class from a line of text
		/// </summary>
		public TasMovieMarker(string line, int version)
		{
			var split = line.Split('\t');
			Frame = int.Parse(split[0]);
			if (version == 1)
			{
				Message = split[1];
			}
			else if (version == 2)
			{
				WantsState = bool.Parse(split[1]);
				Message = split[2];
			}
			else
			{
				throw new Exception("Invalid version.");
			}
		}

		public int Frame { get; private set; }

		public string Message { get; set; }

		public bool WantsState { get; set; } = true;

		public override string ToString() => $"{Frame}\t{WantsState}\t{Message}";

		public override int GetHashCode() => Frame.GetHashCode();

		public override bool Equals(object obj)
		{
			return obj switch
			{
				null => false,
				TasMovieMarker marker => Frame == marker.Frame,
				_ => false,
			};
		}

		public static bool operator ==(TasMovieMarker marker, int frame)
		{
			if (marker == null)
			{
				return false;
			}

			return marker.Frame == frame;
		}

		public static bool operator !=(TasMovieMarker marker, int frame)
		{
			if (marker == null)
			{
				return false;
			}

			return marker.Frame != frame;
		}

		/// <summary>
		/// Shifts the marker's position directly.
		/// Should be used sparingly and only while considering the surrounding frames.
		/// Intended for moving binded markers during frame inserts/deletions.
		/// </summary>
		/// <param name="offset">Amount to shift marker by.</param>
		public void ShiftTo(int offset)
		{
			Frame += offset;
		}
	}

	public class TasMovieMarkerList : List<TasMovieMarker>
	{
		private readonly ITasMovie _movie;

		public TasMovieMarkerList(ITasMovie movie)
		{
			_movie = movie;
		}

		public TasMovieMarkerList DeepClone()
		{
			var ret = new TasMovieMarkerList(_movie);
			for (int i = 0; i < Count; i++)
			{
				// used to copy markers between branches
				ret.Add(new TasMovieMarker(this[i].Frame, this[i].Message, this[i].Permanence), skipHistory: true);
			}

			return ret;
		}

		public event NotifyCollectionChangedEventHandler CollectionChanged;

		private void OnListChanged(NotifyCollectionChangedAction action)
		{
			// TODO Allow different types
			CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
		}

		public override string ToString()
		{
			var sb = new StringBuilder();
			sb.AppendLine("2"); // version
			foreach (var marker in this)
			{
				// the run's own markers are derived: writing them down would only
				// let them be wrong when the file is opened again
				if (marker.IsPermanent) continue;
				sb.AppendLine(marker.ToString());
			}

			return sb.ToString();
		}

		public void LoadFromFile(TextReader tr)
		{
			string line;
			int version = -1;
			while ((line = tr.ReadLine()) != null)
			{
				if (string.IsNullOrWhiteSpace(line)) continue;
				if (version == -1)
				{
					if (line.Contains('\t'))
					{
						version = 1;
					}
					else
					{
						version = int.Parse(line);
						continue;
					}
				}
				Add(new TasMovieMarker(line, version));
			}
		}

		/// <summary>
		/// Puts the run's own three markers where the movie says they now are, and
		/// makes them if they are not there yet: frame zero, the last frame with
		/// anything pressed on it, and the last frame of the movie.
		///
		/// Called from every edit (TasMovie.InvalidateAfter, which every editing
		/// path funnels through) and after a load, so they cannot go stale. They
		/// are free to sit on the same frame as each other - a one-frame movie has
		/// all three on frame zero - and each is still its own row.
		/// </summary>
		public void RefreshPermanent()
		{
			if (_movie is null) return;

			var want = new[]
			{
				(MarkerPermanence.RunStart, "Run start", 0),
				(MarkerPermanence.LastInput, "Last input", _movie.LastNonEmptyInputFrame),
				(MarkerPermanence.RunEnd, "Run end", Math.Max(0, _movie.InputLogLength - 1)),
			};

			var changed = false;
			foreach (var (kind, message, frame) in want)
			{
				var existing = base.Find(m => m.Permanence == kind);
				if (existing is null)
				{
					base.Add(new TasMovieMarker(frame, message, kind));
					changed = true;
				}
				else if (existing.Frame != frame)
				{
					existing.MoveTo(frame);
					changed = true;
				}
			}

			if (changed)
			{
				SortByFrame();
				OnListChanged(NotifyCollectionChangedAction.Move);
			}
		}

		/// <summary>
		/// Where several markers share a frame, the run's own come first and in the
		/// order the run happens: run start, then last input, then run end. That is
		/// the order they are declared in, so the enum does the deciding.
		/// </summary>
		private void SortByFrame()
			=> Sort(static (m1, m2) => m1.Frame != m2.Frame
				? m1.Frame.CompareTo(m2.Frame)
				: Rank(m1).CompareTo(Rank(m2)));

		/// <summary>Permanents in run order, then everything a person placed.</summary>
		private static int Rank(TasMovieMarker marker)
			=> marker.IsPermanent ? (int)marker.Permanence : int.MaxValue;

		/// <summary>The run's own marker of this kind, if it has been worked out yet.</summary>
		public TasMovieMarker Permanent(MarkerPermanence kind)
			=> kind == MarkerPermanence.None ? null : base.Find(m => m.Permanence == kind);

		// the inherited one
		public new void Add(TasMovieMarker item)
		{
			Add(item, false);
		}

		public void Add(TasMovieMarker item, bool skipHistory)
		{
			// a permanent marker owns none of the frame: a user marker may sit on
			// the same one, and what it must never do is rename the run's own
			var existingItem = Find(m => m.Frame == item.Frame && !m.IsPermanent);
			if (existingItem != null)
			{
				if (existingItem.Message != item.Message)
				{
					if (!skipHistory)
					{
						_movie.ChangeLog.AddMarkerChange(item, item.Frame, existingItem.Message);
					}

					existingItem.Message = item.Message;
					OnListChanged(NotifyCollectionChangedAction.Replace);
				}
			}
			else
			{
				if (!skipHistory)
				{
					_movie.ChangeLog.AddMarkerChange(item);
				}

				base.Add(item);
				Sort((m1, m2) => m1.Frame.CompareTo(m2.Frame));
				OnListChanged(NotifyCollectionChangedAction.Add);
			}
		}

		public void Add(int frame, string message)
		{
			Add(new TasMovieMarker(frame, message));
		}

		public new void AddRange(IEnumerable<TasMovieMarker> collection)
		{
			bool endBatch = _movie.ChangeLog.BeginNewBatch("Add Markers", true);
			foreach (TasMovieMarker m in collection)
			{
				Add(m);
			}

			if (endBatch)
			{
				_movie.ChangeLog.EndBatch();
			}
		}

		public new void Insert(int index, TasMovieMarker item)
		{
			_movie.ChangeLog.AddMarkerChange(item);

			base.Insert(index, item);
			Sort((m1, m2) => m1.Frame.CompareTo(m2.Frame));
			OnListChanged(NotifyCollectionChangedAction.Add);
		}

		public new void InsertRange(int index, IEnumerable<TasMovieMarker> collection)
		{
			collection = collection as IReadOnlyCollection<TasMovieMarker> ?? collection.ToArray();
			bool endBatch = _movie.ChangeLog.BeginNewBatch("Add Markers", true);
			foreach (TasMovieMarker m in collection)
			{
				_movie.ChangeLog.AddMarkerChange(m);
			}

			if (endBatch)
			{
				_movie.ChangeLog.EndBatch();
			}

			base.InsertRange(index, collection);
			Sort((m1, m2) => m1.Frame.CompareTo(m2.Frame));
			OnListChanged(NotifyCollectionChangedAction.Add);
		}

		public new void Remove(TasMovieMarker item)
		{
			Debug.Assert(item != null, "Attempted to remove a marker that doens't exist.");
			// the run's own markers are the movie speaking, and are not the user's
			// to delete - frame zero among them, as it always was
			if (item == null || item.IsPermanent || item.Frame == 0)
			{
				return;
			}

			_movie.States?.Pin(item.Frame - 1, false);
			_movie.ChangeLog.AddMarkerChange(null, item.Frame, item.Message);

			base.Remove(item);
			OnListChanged(NotifyCollectionChangedAction.Remove);
		}

		public new int RemoveAll(Predicate<TasMovieMarker> match)
		{
			bool endBatch = _movie.ChangeLog.BeginNewBatch("Remove All Markers", true);
			foreach (TasMovieMarker m in this)
			{
				if (!m.IsPermanent && match.Invoke(m))
				{
					_movie.ChangeLog.AddMarkerChange(null, m.Frame, m.Message);
					_movie.States?.Pin(m.Frame - 1, false);
				}
			}

			if (endBatch)
			{
				_movie.ChangeLog.EndBatch();
			}

			int removeCount = base.RemoveAll(m => !m.IsPermanent && match.Invoke(m));
			if (removeCount > 0)
			{
				OnListChanged(NotifyCollectionChangedAction.Remove);
			}

			return removeCount;
		}

		public void Move(int fromFrame, int toFrame)
		{
			if (fromFrame == 0) // no thanks!
			{
				return;
			}

			TasMovieMarker m = Get(fromFrame);
			Debug.Assert(m != null, "Attempted to move a marker that doens't exist.");
			if (m == null || m.IsPermanent)
			{
				return;
			}

			_movie.ChangeLog.AddMarkerChange(m, m.Frame);
			Insert(0, new TasMovieMarker(toFrame, m.Message));
			Remove(m);
		}

		/// <summary>
		/// Deletes all markers at or below the given start frame.
		/// </summary>
		/// <param name="startFrame">The first frame for markers to be deleted.</param>
		/// <returns>Number of markers deleted.</returns>
		public int TruncateAt(int startFrame)
		{
			int deletedCount = 0;
			bool endBatch = _movie.ChangeLog.BeginNewBatch("Truncate Markers", true);
			for (int i = Count - 1; i > -1; i--)
			{
				if (this[i].Frame >= startFrame)
				{
					// the run's own markers are not truncated away; the refresh that
					// follows the truncation puts them where the movie now ends
					if (i == 0 || this[i].IsPermanent)
					{
						continue;
					}

					_movie.ChangeLog.AddMarkerChange(null, this[i].Frame, this[i].Message);
					RemoveAt(i);
					deletedCount++;
				}
			}

			if (endBatch)
			{
				_movie.ChangeLog.EndBatch();
			}

			if (deletedCount > 0)
			{
				OnListChanged(NotifyCollectionChangedAction.Remove);
			}

			return deletedCount;
		}

		public TasMovieMarker Previous(int currentFrame)
		{
			return PreviousOrCurrent(currentFrame - 1);
		}

		public TasMovieMarker PreviousOrCurrent(int currentFrame)
		{
			int lowerBoundIndex = this.LowerBoundBinarySearch(static m => m.Frame, currentFrame);

			return lowerBoundIndex < 0 ? null : this[lowerBoundIndex];
		}

		public TasMovieMarker Next(int currentFrame)
		{
			return this
				.Where(m => m.Frame > currentFrame)
				.OrderBy(m => m.Frame)
				.FirstOrDefault();
		}

		public int FindIndex(string markerName)
		{
			return FindIndex(m => m.Message == markerName);
		}

		public bool IsMarker(int frame)
		{
			// TODO: could use a BinarySearch here, but CollectionExtensions.BinarySearch currently throws
			// an exception on failure, which is probably so expensive it nullifies any performance benefits
			foreach (var marker in this)
			{
				if (marker.Frame > frame) return false;
				if (marker.Frame == frame) return true;
			}

			return false;
		}

		/// <summary>
		/// The marker on this frame, preferring the user's own where one shares the
		/// frame with a permanent - so editing a frame edits what the user put there.
		/// </summary>
		public TasMovieMarker Get(int frame)
		{
			return Find(m => m == frame && !m.IsPermanent) ?? Find(m => m == frame);
		}

		public void ShiftAt(int frame, int offset)
		{
			// permanent markers do not ride along with an insert or a delete: the
			// refresh that follows puts them where the movie now says they are
			foreach (var marker in this.Where(m => m.Frame >= frame && !m.IsPermanent).ToList())
			{
				marker.ShiftTo(offset);
			}
		}
	}
}
