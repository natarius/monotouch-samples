using System;
using MonoTouch.AudioToolbox;
using System.Threading;
using System.Collections.Generic;
using System.Linq;

namespace StreamingAudio
{
	/// <summary>
	/// A Class to hold the AudioBuffer with all setting together
	/// </summary>
	internal class AudioBuffer
	{
		public IntPtr Buffer { get; set; }
		public List<AudioStreamPacketDescription> PacketDescriptions { get; set; }
		public int CurrentOffset { get; set; }
		public bool IsInUse { get; set; }
	}
	
	/// <summary>
	/// Wrapper around OutputQueue and AudioFileStream to allow streaming of various filetypes
	/// </summary>
	public class StreamingPlayback : IDisposable
	{
		// the AudioToolbox decoder
		AudioFileStream fileStream;
		int bufferSize =  128 * 1024;
		List<AudioBuffer> outputBuffers;
		AudioBuffer currentBuffer;
		OutputAudioQueue OutputQueue;
		// Maximum buffers
		int maxBufferCount = 4;
		// Keep track of all queued up buffers, so that we know that the playback finished
		int queuedBufferCount = 0;
		// Current Filestream Position - if we don't keep track we don't know when to push the last uncompleted buffer
		long currentByteCount = 0;
		
		public event EventHandler Finished;
		
		public bool Started  { get; private set; }
		public float Volume {
			get {
				return OutputQueue.Volume;
			}

			set {
				OutputQueue.Volume = value;
			}
		}
		
		/// <summary>
		/// Defines the size forearch buffer, when using a slow source use more buffers with lower buffersizes
		/// </summary>
		public int BufferSize {
			get {
				return bufferSize;
			}

			set {
				bufferSize = value;
			}
		}
		
		/// <summary>
		/// Defines the maximum Number of Buffers to use, the count can only change after Reset is called or the 
		/// StreamingPlayback is freshly instantiated
		/// </summary>
		public int MaxBufferCount
		{
			get {
				return maxBufferCount;
			}

			set {
				maxBufferCount = value;
			}
		}
		
		public StreamingPlayback() : this (AudioFileType.MP3)
		{
		}
		
		public StreamingPlayback (AudioFileType type) 
		{
			fileStream = new AudioFileStream (type);
			fileStream.PacketDecoded += AudioPacketDecoded;
			fileStream.PropertyFound += AudioPropertyFound;
		}
		
		public void Reset ()
		{
			if (fileStream != null) {
				fileStream.Close ();
				fileStream = new AudioFileStream (AudioFileType.MP3);
				currentByteCount = 0;
				fileStream.PacketDecoded += AudioPacketDecoded;
				fileStream.PropertyFound += AudioPropertyFound;
			}
		}
		
		public void ResetOutputQueue ()
		{
			if (OutputQueue != null) {
				OutputQueue.Stop (true);
				OutputQueue.Reset ();
				foreach (AudioBuffer buf in outputBuffers) {
					buf.PacketDescriptions.Clear ();
					OutputQueue.FreeBuffer (buf.Buffer);
				}
				outputBuffers = null;
				OutputQueue.Dispose ();
			}
		}
		
		/// <summary>
		/// Stops the OutputQueue
		/// </summary>
		public void Pause ()
		{
			OutputQueue.Pause ();
			Started = false;
		}
		
		/// <summary>
		/// Starts the OutputQueue
		/// </summary>
		public void Play ()
		{
			OutputQueue.Start ();
			Started = true;
		}
		
		/// <summary>
		/// Main methode to kick off the streaming, just send the bytes to this method
		/// </summary>
		public void ParseBytes (byte [] buffer, int count, bool discontinuity)
		{
			fileStream.ParseBytes (buffer, 0, count, discontinuity);
		}
		
		public void Dispose ()
		{
			Dispose (true);
			GC.SuppressFinalize (this);
		}
		
		/// <summary>
		/// Cleaning up all the native Resource
		/// </summary>
		protected virtual void Dispose (bool disposing)
		{
			if (disposing) {
				if (OutputQueue != null)
					OutputQueue.Stop (false);
				
				if (outputBuffers != null)
					foreach (var b in outputBuffers)
						OutputQueue.FreeBuffer (b.Buffer);
				
				if (fileStream != null) {
					fileStream.Close ();
					fileStream = null;
				}
				
				if (OutputQueue != null) {
					OutputQueue.Dispose ();
					OutputQueue = null;
				}
			}
		}
		
		/// <summary>
		/// Saving the decoded Packets to our active Buffer, if the Buffer is full queue it into the OutputQueue
		/// and wait until another buffer gets freed up
		/// </summary>
		void AudioPacketDecoded (object sender, PacketReceivedEventArgs args)
		{
			foreach (var p in args.PacketDescriptions) {
				currentByteCount += p.DataByteSize;
			
				AudioStreamPacketDescription pd = p;
				
				int left = bufferSize - currentBuffer.CurrentOffset;
				if (left < pd.DataByteSize) {
					EnqueueBuffer ();
					WaitForBuffer ();
				}
					
				AudioQueue.FillAudioData (currentBuffer.Buffer, currentBuffer.CurrentOffset, args.InputData, (int)pd.StartOffset, pd.DataByteSize);
#if true
				// Set new offset for this packet
				pd.StartOffset = currentBuffer.CurrentOffset;
				// Add the packet to our Buffer
				currentBuffer.PacketDescriptions.Add (pd);
				// Add the Size so that we know how much is in the buffer
				currentBuffer.CurrentOffset += pd.DataByteSize;
#else
				// Fill out the packet description
				pdesc [packetsFilled] = pd;
				pdesc [packetsFilled].StartOffset = bytesFilled;
				bytesFilled += packetSize;
				packetsFilled++;
				
				var t = OutputQueue.CurrentTime;
				Console.WriteLine ("Time:  {0}", t);
				
				// If we filled out all of our packet descriptions, enqueue the buffer
				if (pdesc.Length == packetsFilled){
					EnqueueBuffer ();
					WaitForBuffer ();
				}
#endif
			}
			
			if (currentByteCount == fileStream.DataByteCount)
				EnqueueBuffer ();
		}
		
		/// <summary>
		/// Flush the current buffer and close the whole thing up
		/// </summary>
		public void FlushAndClose ()
		{
			EnqueueBuffer ();
			OutputQueue.Flush ();
			
			Dispose ();
		}
		
		/// <summary>
		/// Enqueue the active buffer to the OutputQueue
		/// </summary>
		void EnqueueBuffer ()
		{			
			currentBuffer.IsInUse = true;
			OutputQueue.EnqueueBuffer (currentBuffer.Buffer, currentBuffer.CurrentOffset, currentBuffer.PacketDescriptions.ToArray ());
			queuedBufferCount++;
			StartQueueIfNeeded ();
		}
		
		/// <summary>
		/// Wait until a buffer is freed up
		/// </summary>
		void WaitForBuffer ()
		{
			int curIndex = outputBuffers.IndexOf (currentBuffer);
			currentBuffer = outputBuffers[curIndex < outputBuffers.Count - 1 ? curIndex + 1 : 0];
			
			lock (currentBuffer) {
				while (currentBuffer.IsInUse) 
					Monitor.Wait (currentBuffer);
			}
		}
		
		void StartQueueIfNeeded ()
		{
			if (Started)
				return;
		
			Play ();
		}
			
		/// <summary>
		/// When a AudioProperty in the fed packets is found this callback is called
		/// </summary>
		void AudioPropertyFound (object sender, PropertyFoundEventArgs args)
		{
			switch (args.Property) {
			case AudioFileStreamProperty.ReadyToProducePackets:
				Started = false;
				
				
				if (OutputQueue != null)
					OutputQueue.Dispose ();
				
				OutputQueue = new OutputAudioQueue (fileStream.StreamBasicDescription);
				currentByteCount = 0;
				OutputQueue.OutputCompleted += HandleOutputQueueOutputCompleted;
				outputBuffers = new List<AudioBuffer>();
				
				for (int i = 0; i < MaxBufferCount; i++)
				{
					IntPtr outBuffer;
					OutputQueue.AllocateBuffer (BufferSize, out outBuffer);
					outputBuffers.Add (new AudioBuffer () { Buffer = outBuffer, PacketDescriptions = new List<AudioStreamPacketDescription>() });
				}
				
				currentBuffer = outputBuffers.First ();
				
				OutputQueue.MagicCookie = fileStream.MagicCookie;				
				break;
			}
		}
		
		/// <summary>
		/// Is called when a buffer is completly read and can be freed up
		/// </summary>
		void HandleOutputQueueOutputCompleted (object sender, OutputCompletedEventArgs e)
		{
			queuedBufferCount--;
			IntPtr buf = e.IntPtrBuffer;
			
			foreach (var buffer in outputBuffers) {
				if (buffer.Buffer != buf)
					continue;
				
				// free Buffer
				buffer.PacketDescriptions.Clear ();
				buffer.CurrentOffset = 0;
				lock (buffer) {
					buffer.IsInUse = false;
					Monitor.Pulse (buffer);
				}
			}
			
			if (queuedBufferCount == 0 && Finished != null)
				Finished (this, new EventArgs ());
		}
	}
}


