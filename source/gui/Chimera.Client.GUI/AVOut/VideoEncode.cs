#nullable enable

using System;
using System.IO;

namespace Chimera.Client.GUI
{
	/// <summary>
	/// What a person asked for when they pressed Start Encode: an output file, the
	/// ffmpeg command that fills it, the stretch of the run to reproduce, and the
	/// shape of the picture to hand ffmpeg.
	/// </summary>
	public sealed class VideoEncodeRequest
	{
		public string OutputPath = "";

		/// <summary>the middle of the ffmpeg command line - codecs, format, options</summary>
		public string FFmpegCommand = "";

		/// <summary>first movie frame to appear in the video, inclusive</summary>
		public int StartFrame;

		/// <summary>last movie frame to appear in the video, inclusive</summary>
		public int EndFrame;

		/// <summary>0 for "the size the core draws"</summary>
		public int Width;

		public int Height;

		/// <summary>letterbox rather than scale, when a size is given</summary>
		public bool Pad;

		/// <summary>
		/// Stretch the video to fit the audio rather than the other way round. The
		/// same choice the A/V writer chooser has always offered.
		/// </summary>
		public bool AudioSync;

		public bool CaptureOsd;

		public bool CaptureLua;

		public int FrameCount => EndFrame - StartFrame + 1;
	}

	/// <summary>How far along an encode is, as the dialog wants to say it.</summary>
	public readonly struct VideoEncodeProgress
	{
		public readonly VideoEncodePhase Phase;
		public readonly int FramesDone;
		public readonly int FramesTotal;
		public readonly int CurrentFrame;
		public readonly double FramesPerSecond;
		public readonly TimeSpan? Remaining;
		public readonly string? Message;

		/// <summary>
		/// The loudest sample in each channel of the frame just written, as a
		/// fraction of full scale. The speakers are muted during an encode, so
		/// these are the only sign that sound is going into the file.
		/// </summary>
		public readonly double PeakLeft;

		public readonly double PeakRight;

		public VideoEncodeProgress(
			VideoEncodePhase phase,
			int framesDone,
			int framesTotal,
			int currentFrame,
			double framesPerSecond,
			TimeSpan? remaining,
			string? message,
			double peakLeft = 0,
			double peakRight = 0)
		{
			Phase = phase;
			FramesDone = framesDone;
			FramesTotal = framesTotal;
			CurrentFrame = currentFrame;
			FramesPerSecond = framesPerSecond;
			Remaining = remaining;
			Message = message;
			PeakLeft = peakLeft;
			PeakRight = peakRight;
		}
	}

	public enum VideoEncodePhase
	{
		/// <summary>nothing is running</summary>
		Idle,

		/// <summary>winding the run to the first frame, with nothing written out</summary>
		Seeking,

		/// <summary>reproducing the run into the file</summary>
		Encoding,

		/// <summary>the file is finished and closed</summary>
		Done,

		/// <summary>stopped early; the file holds what got as far as being written</summary>
		Stopped,

		/// <summary>something went wrong, and <see cref="VideoEncodeProgress.Message"/> says what</summary>
		Failed,
	}

	/// <summary>
	/// One encode, from the moment Start Encode is pressed to the moment the file
	/// is closed. It holds no emulator and drives nothing - the main form's run
	/// loop advances it a frame at a time and this only decides what that means:
	/// which phase we are in, how far along, and how much longer.
	/// </summary>
	internal sealed class VideoEncodeJob
	{
		internal VideoEncodeJob(
			VideoEncodeRequest request,
			Stream restoreState,
			bool wasReadOnly,
			bool wasPaused,
			bool wasRecording)
		{
			Request = request;
			RestoreState = restoreState;
			WasReadOnly = wasReadOnly;
			WasPaused = wasPaused;
			WasRecording = wasRecording;
			Phase = VideoEncodePhase.Seeking;
			_startedAt = DateTime.UtcNow;
		}

		internal VideoEncodeRequest Request { get; }

		/// <summary>where the user was before the encode borrowed the emulator</summary>
		internal Stream RestoreState { get; }

		internal bool WasReadOnly { get; }

		internal bool WasPaused { get; }

		/// <summary>
		/// Whether the movie was being RECORDED when the encode borrowed it. An
		/// encode reproduces a run rather than authoring one, so it switches
		/// recording off - and that has to be switched back on afterwards, because
		/// a movie left in Play takes no input from the piano roll at all.
		/// </summary>
		internal bool WasRecording { get; }

		internal VideoEncodePhase Phase { get; private set; }

		internal int FramesDone { get; private set; }

		internal string? Message { get; private set; }

		/// <summary>true once the file is open and frames are going into it</summary>
		internal bool IsEncoding => Phase is VideoEncodePhase.Encoding;

		internal bool IsRunning => Phase is VideoEncodePhase.Seeking or VideoEncodePhase.Encoding;

		private DateTime _startedAt;

		// A short trailing window rather than the whole run: an encode's speed
		// changes with what is on screen, and the useful question is how fast it is
		// going NOW, which is also what makes the estimate follow the truth.
		private const int WindowFrames = 120;
		private DateTime _windowStart;
		private int _windowFrames;
		private double _fps;

		/// <summary>The run reached the first frame; frames from here go in the file.</summary>
		internal void StartEncoding()
		{
			Phase = VideoEncodePhase.Encoding;
			_startedAt = _windowStart = DateTime.UtcNow;
			_windowFrames = 0;
			_fps = 0;
		}

		/// <summary>
		/// The samples that just went into the file, so the dialog can show that
		/// there is sound in it. Interleaved stereo, 16 bit, as the writer takes
		/// them; an odd tail is ignored rather than read past.
		/// </summary>
		internal void NoteAudio(short[] samples, int sampleCount)
		{
			int left = 0, right = 0;
			var pairs = Math.Min(sampleCount, samples.Length / 2);
			for (var i = 0; i < pairs; i++)
			{
				var l = Math.Abs((int)samples[i * 2]);
				var r = Math.Abs((int)samples[(i * 2) + 1]);
				if (l > left) left = l;
				if (r > right) right = r;
			}

			// short.MinValue has no positive counterpart, so the scale is 32768
			_peakLeft = left / 32768.0;
			_peakRight = right / 32768.0;
		}

		private double _peakLeft, _peakRight;

		/// <summary>One more frame is in the file.</summary>
		internal void CountFrame()
		{
			FramesDone++;
			_windowFrames++;
			var elapsed = (DateTime.UtcNow - _windowStart).TotalSeconds;
			if (_windowFrames < WindowFrames && elapsed < 1.0) return;
			if (elapsed > 0) _fps = _windowFrames / elapsed;
			_windowStart = DateTime.UtcNow;
			_windowFrames = 0;
		}

		internal void Finish(VideoEncodePhase phase, string? message = null)
		{
			Phase = phase;
			Message = message;
		}

		internal VideoEncodeProgress Snapshot(int currentFrame)
		{
			var total = Request.FrameCount;
			double fps = _fps;
			if (fps <= 0 && FramesDone > 0)
			{
				// before the first window closes, anything is better than nothing
				var elapsed = (DateTime.UtcNow - _startedAt).TotalSeconds;
				if (elapsed > 0) fps = FramesDone / elapsed;
			}

			TimeSpan? remaining = null;
			if (Phase is VideoEncodePhase.Encoding && fps > 0)
			{
				remaining = TimeSpan.FromSeconds(Math.Max(0, total - FramesDone) / fps);
			}

			return new(Phase, FramesDone, total, currentFrame, fps, remaining, Message, _peakLeft, _peakRight);
		}
	}
}
